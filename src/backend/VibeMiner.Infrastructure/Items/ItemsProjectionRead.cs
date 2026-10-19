using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class ItemsProjection
{
    internal async Task<Session> OpenAsync(ItemsProjectionArtifact artifact, string preparedAt, CancellationToken token)
    {
        using (await resources.EnterCpuAsync(context, token)) ValidateArtifact(artifact);
        var input = await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, context, token);
        try
        {
            var header = new byte[HeaderBytes]; await input.ReadExactlyAsync(header, token);
            long count, metadataOffset, namesOffset, nameBytes, directoryOffset; int metadataLength;
            using (await resources.EnterCpuAsync(context, token))
            {
                count = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
                metadataOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
                metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32));
                namesOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(36));
                nameBytes = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(44));
                directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(52));
                if (!header.AsSpan(0, 8).SequenceEqual("VMITEM01"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != 1 ||
                    BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != DirectoryBytes || header.AsSpan(60).ContainsAnyExcept((byte)0) ||
                    count != Number(artifact.Counts.ItemRows) || metadataOffset < HeaderBytes || metadataLength is < 1 or > MaxMetadataBytes || metadataOffset > artifact.ByteLength - metadataLength ||
                    namesOffset != metadataOffset + metadataLength || nameBytes < 0 || nameBytes > limits.MaxNameBytes ||
                    directoryOffset != namesOffset + nameBytes || directoryOffset > artifact.ByteLength || artifact.ByteLength - directoryOffset != count * DirectoryBytes)
                    throw Error("ITEMS_PROJECTION_INVALID", "The immutable Items artifact has invalid complete geometry.");
            }
            input.Position = metadataOffset; var metadata = new byte[metadataLength]; await input.ReadExactlyAsync(metadata, token);
            using (await resources.EnterCpuAsync(context, token))
                if (!metadata.AsSpan().SequenceEqual(MetadataBytes(artifact.Binding, artifact.Counts, artifact.Groups, artifact.FeatureRevision)))
                    throw Error("ITEMS_PROJECTION_INVALID", "The Items artifact differs from its complete source-bound descriptor.");
            var directory = new byte[4096 * DirectoryBytes]; uint? previous = null; long expectedNames = 0, expectedSummary = HeaderBytes;
            for (long at = 0; at < count;)
            {
                var take = (int)Math.Min(4096, count - at); input.Position = directoryOffset + at * DirectoryBytes;
                await input.ReadExactlyAsync(directory.AsMemory(0, take * DirectoryBytes), token);
                using (await resources.EnterCpuAsync(context, token))
                for (var i = 0; i < take; i++)
                {
                    token.ThrowIfCancellationRequested(); var entry = DirectoryEntry.Read(directory.AsSpan(i * DirectoryBytes, DirectoryBytes));
                    if (previous is { } id && entry.Id <= id || !Enum.IsDefined(entry.NameState) || entry.NameOffset != expectedNames ||
                        entry.NameLength is < 0 or > 262144 || (entry.NameState == ItemNameState.Valid) != (entry.NameLength > 0) ||
                        entry.SummaryLength is < 1 or > 8192 || entry.SummaryOffset != expectedSummary ||
                        entry.NameLength > nameBytes - expectedNames || entry.SummaryLength > metadataOffset - expectedSummary)
                        throw Error("ITEMS_PROJECTION_INVALID", "The Items directory contains duplicate, unordered or unbounded entries.");
                    previous = entry.Id; expectedNames += entry.NameLength; expectedSummary += entry.SummaryLength;
                }
                at += take;
                await Task.Yield(); token.ThrowIfCancellationRequested();
            }
            if (expectedNames != nameBytes || expectedSummary != metadataOffset)
                throw Error("ITEMS_PROJECTION_INVALID", "The complete name/summary areas differ from their directory.");
            var session = new Session(input, objects, resources, context, artifact, preparedAt, namesOffset, directoryOffset, count);
            try { await session.ValidateSourcesAsync(token); return session; }
            catch { await session.DisposeAsync(); throw; }
        }
        catch { await input.DisposeAsync(); throw; }
    }

    internal static void ValidateArtifact(ItemsProjectionArtifact a)
    {
        ValidateBinding(a.Binding); ValidateRevision(a.FeatureRevision);
        var c = a.Counts;
        if (a.ContentKey != ContentKey(a.Binding, a.FeatureRevision) || a.Sha256.Length != 64 || a.Sha256.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            a.ByteLength is < HeaderBytes or > VerifiedObjectStore.MaxObjectBytes || Number(c.ItemRows) != Total(a.Binding.ItemRows) ||
            Number(c.SparseRows) != Total(a.Binding.SparseRows) || Number(c.MatchedRows) + Number(c.ItemWithoutSparseRows) != Number(c.ItemRows) ||
            Number(c.MatchedRows) + Number(c.SparseOnlyRows) != Number(c.SparseRows) ||
            Number(c.ValidNames) + Number(c.EmptyNames) + Number(c.InvalidNames) != Number(c.MatchedRows) ||
            JsonSerializer.Serialize(a.Groups, Json) != JsonSerializer.Serialize(Groups(a.Binding, a.FeatureRevision), Json))
            throw Error("ITEMS_PROJECTION_INVALID", "The Items descriptor has conflicting source, count or property-group evidence.");
    }
    private static long Number(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0 || number > TableRowArtifacts.MaxRows || N(number) != value)
            throw Error("ITEMS_PROJECTION_INVALID", "An Items count is not canonical nonnegative integer evidence.");
        return number;
    }

    internal sealed class Session : IAsyncDisposable
    {
        private readonly FileStream input;
        private readonly AcquisitionResources resources;
        private readonly IAcquisitionWorkContext? context;
        private readonly ItemsProjectionArtifact artifact;
        private readonly long namesOffset, directoryOffset, count;
        private readonly TableRowArtifacts itemRows, sparseRows;
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool disposed;
        internal ItemsSnapshot Snapshot { get; }
        internal ItemsProjectionArtifact Artifact => artifact;
        internal long LastNameBlockReads { get; private set; }
        internal long LastPayloadReads { get; private set; }
        internal long LastScannedRows { get; private set; }
        private Task? retirementCompletion;
        private sealed record Cursor(string Binding, long Next);
        private sealed record Match(long Ordinal, DirectoryEntry Entry, ItemName Name);
        internal Session(FileStream input, VerifiedObjectStore objects, AcquisitionResources resources, IAcquisitionWorkContext? context,
            ItemsProjectionArtifact artifact, string preparedAt, long namesOffset, long directoryOffset, long count)
        {
            this.input = input; this.resources = resources; this.context = context; this.artifact = artifact;
            this.namesOffset = namesOffset; this.directoryOffset = directoryOffset; this.count = count;
            itemRows = new(objects, resources, context); sparseRows = new(objects, resources, context);
            Snapshot = new(artifact.Binding.Context, artifact.ContentKey, preparedAt, artifact.FeatureRevision, QueryPolicy,
                artifact.Binding.ItemSource, artifact.Binding.SparseSource, artifact.Counts, artifact.Groups);
        }
        internal async Task ValidateSourcesAsync(CancellationToken token)
        {
            _ = await itemRows.ReadPageAsync(artifact.Binding.ItemRows, 0, 1, null, token);
            _ = await sparseRows.ReadPageAsync(artifact.Binding.SparseRows, 0, 1, null, token);
        }
        private void Check(ItemsContext requestContext, string artifactId)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requestContext != Snapshot.Context || artifactId != Snapshot.ArtifactId)
                throw Error("ITEMS_ARTIFACT_STALE", "This Items request belongs to a different exact source admission.");
        }
        private string QueryBinding(ItemsPageRequest request) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { request.Context, request.ArtifactId, queryPolicy = QueryPolicy, revision = artifact.FeatureRevision, request.Query, request.ExactItemId, request.Limit, order = "uint-id-ascending" }, Json)));
        private static string Encode(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private long Decode(ItemsPageRequest request)
        {
            if (request.After is null) return 0;
            try
            {
                var raw = request.After.Replace('-', '+').Replace('_', '/'); raw = raw.PadRight((raw.Length + 3) / 4 * 4, '=');
                var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(raw), Json);
                if (cursor is null || cursor.Binding != QueryBinding(request) || cursor.Next < 0 || cursor.Next > count || Encode(cursor) != request.After)
                    throw Error("INVALID_CURSOR", "The Items cursor differs from this exact query, page size or source artifact.");
                return cursor.Next;
            }
            catch (Exception failure) when (failure is JsonException or FormatException)
            { throw Error("INVALID_CURSOR", "The Items cursor has malformed bounded framing."); }
        }
        private async Task<DirectoryEntry> Entry(long ordinal, CancellationToken token)
        {
            var bytes = new byte[DirectoryBytes]; input.Position = directoryOffset + ordinal * DirectoryBytes;
            await input.ReadExactlyAsync(bytes, token); return DirectoryEntry.Read(bytes);
        }
        private async Task<long> Bound(uint id, CancellationToken token)
        {
            long lo = 0, hi = count;
            while (lo < hi) { var mid = lo + (hi - lo) / 2; if ((await Entry(mid, token)).Id < id) lo = mid + 1; else hi = mid; }
            return lo;
        }

        internal async Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token)
        {
            ItemsService.Validate(request); await gate.WaitAsync(token);
            try
            {
                Check(request.Context, request.ArtifactId); var after = Decode(request);
                LastNameBlockReads = 0; LastPayloadReads = 0; LastScannedRows = 0;
                var exact = request.ExactItemId is null ? (uint?)null : uint.Parse(request.ExactItemId, CultureInfo.InvariantCulture);
                if (exact is { } id) after = Math.Max(after, await Bound(id, token));
                var matches = new List<Match>(request.Limit + 1); var rawDirectory = new byte[4096 * DirectoryBytes];
                var names = new byte[1 << 20]; var characters = new char[65536]; var entries = new DirectoryEntry[4096]; var stopped = false;
                while (after < count && matches.Count <= request.Limit && !stopped)
                {
                    var take = (int)Math.Min(4096, count - after); input.Position = directoryOffset + after * DirectoryBytes;
                    await input.ReadExactlyAsync(rawDirectory.AsMemory(0, take * DirectoryBytes), token);
                    using (await resources.EnterCpuAsync(context, token))
                        for (var i = 0; i < take; i++) { token.ThrowIfCancellationRequested(); entries[i] = DirectoryEntry.Read(rawDirectory.AsSpan(i * DirectoryBytes, DirectoryBytes)); }
                    for (var first = 0; first < take && matches.Count <= request.Limit && !stopped;)
                    {
                        var end = first + 1; var nameStart = entries[first].NameOffset;
                        while (end < take && entries[end].NameOffset + entries[end].NameLength - nameStart <= names.Length) end++;
                        var bytes = checked((int)(entries[end - 1].NameOffset + entries[end - 1].NameLength - nameStart));
                        if (bytes > 0)
                        {
                            input.Position = namesOffset + nameStart; await input.ReadExactlyAsync(names.AsMemory(0, bytes), token); LastNameBlockReads++;
                        }
                        using (await resources.EnterCpuAsync(context, token))
                        for (var i = first; i < end && matches.Count <= request.Limit; i++)
                        {
                            token.ThrowIfCancellationRequested(); var entry = entries[i]; LastScannedRows++;
                            if (exact is { } wanted && entry.Id != wanted) { stopped = true; break; }
                            var chars = 0;
                            if (entry.NameState == ItemNameState.Valid)
                            {
                                try { chars = Utf8.GetChars(names.AsSpan(checked((int)(entry.NameOffset - nameStart)), entry.NameLength), characters); }
                                catch (ArgumentException) { throw Error("ITEMS_PROJECTION_INVALID", "An indexed name has malformed or oversized strict UTF-8."); }
                            }
                            if (request.Query.Length != 0 && (entry.NameState != ItemNameState.Valid || !characters.AsSpan(0, chars).Contains(request.Query.AsSpan(), StringComparison.OrdinalIgnoreCase))) continue;
                            var name = entry.NameState switch
                            {
                                ItemNameState.Valid => new ItemName(ItemNameState.Valid, new string(characters, 0, chars)),
                                ItemNameState.Empty => new ItemName(ItemNameState.Empty, ""),
                                _ => new ItemName(entry.NameState, null)
                            };
                            matches.Add(new(after + i, entry, name));
                        }
                        first = end;
                    }
                    after += take;
                }
                var returned = matches.Take(request.Limit).ToList(); var summaries = new List<ItemSummary>(returned.Count); long payloadBytes = 0;
                foreach (var match in returned)
                {
                    var summary = await Summary(match.Entry, match.Name, token); summaries.Add(summary);
                    payloadBytes += JsonSerializer.SerializeToUtf8Bytes(summary, Json).Length;
                    // Bound the candidate response to at most one summary over its final budget;
                    // never materialize/serialize fifty maximum-length names at once.
                    if (payloadBytes > MaxResponseBytes) break;
                }
                if (summaries.Count < returned.Count) returned.RemoveRange(summaries.Count, returned.Count - summaries.Count);
                while (true)
                {
                    var more = matches.Count > returned.Count;
                    var next = more && returned.Count > 0 ? Encode(new(QueryBinding(request), returned[^1].Ordinal + 1)) : null;
                    var page = new ItemsPage(request.Context, request.ArtifactId, request.Query, request.ExactItemId, summaries.ToArray(), next);
                    if (JsonSerializer.SerializeToUtf8Bytes(page, Json).Length <= MaxResponseBytes) return page;
                    if (returned.Count <= 1) throw Error("ITEMS_PAGE_LIMIT", "One complete summary and its exact context exceed the Items page budget.");
                    returned.RemoveAt(returned.Count - 1); summaries.RemoveAt(summaries.Count - 1);
                }
            }
            finally { gate.Release(); }
        }
        private async Task<ItemSummary> Summary(DirectoryEntry entry, ItemName name, CancellationToken token)
        {
            var payload = await Payload(entry, token);
            return new(N(entry.Id), name, payload.BaseValues, payload.IconFileDataId);
        }
        private async Task<SummaryPayload> Payload(DirectoryEntry entry, CancellationToken token)
        {
            var bytes = new byte[entry.SummaryLength]; input.Position = entry.SummaryOffset; await input.ReadExactlyAsync(bytes, token); LastPayloadReads++;
            SummaryPayload payload;
            using var cpu = await resources.EnterCpuAsync(context, token);
            try { payload = JsonSerializer.Deserialize<SummaryPayload>(bytes, Json) ?? throw Error("ITEMS_PROJECTION_INVALID", "An Items summary payload is missing."); }
            catch (JsonException) { throw Error("ITEMS_PROJECTION_INVALID", "An Items summary payload has invalid framing."); }
            if (payload.BaseValues is null || payload.BaseValues.Count > 8 || payload.BaseValues.Any(v => v is null || v.Value.Length > 64 ||
                    (v.TableId != ItemTable && v.TableId != SparseTable) || v.ColumnIndex < 0 || v.ColumnIndex >= (v.TableId == ItemTable ? Snapshot.ItemSource.Columns.Count : Snapshot.SparseSource.Columns.Count)))
                throw Error("ITEMS_PROJECTION_INVALID", "An Items summary has invalid source attribution.");
            if (payload.IconFileDataId is { } fdid && (!uint.TryParse(fdid, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || N(id) != fdid))
                throw Error("ITEMS_PROJECTION_INVALID", "An Items summary has a noncanonical optional icon ID.");
            return payload;
        }

        internal async Task<ItemsSummaryAuthorization> AuthorizeSummariesAsync(ItemsContext requestContext, string artifactId,
            IReadOnlyList<string> itemIds, CancellationToken token)
        {
            var ids = ItemsStore.ValidateAuthorizationIds(requestContext, artifactId, itemIds);
            await gate.WaitAsync(token);
            try
            {
                Check(requestContext, artifactId); LastNameBlockReads = 0; LastPayloadReads = 0; LastScannedRows = 0;
                var result = new AuthorizedItemSummary[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var id = uint.Parse(ids[i], CultureInfo.InvariantCulture);
                    var ordinal = await Bound(id, token);
                    if (ordinal == count) { result[i] = new(ids[i], false, []); continue; }
                    var entry = await Entry(ordinal, token);
                    result[i] = entry.Id == id ? new(ids[i], true, (await Payload(entry, token)).BaseValues) : new(ids[i], false, []);
                }
                return new(Snapshot, result);
            }
            finally { gate.Release(); }
        }

        internal async Task<ItemsIconAuthorization> AuthorizeIconsAsync(ItemsContext requestContext, string artifactId,
            IReadOnlyList<string> itemIds, CancellationToken token)
        {
            var ids = ItemsStore.ValidateAuthorizationIds(requestContext, artifactId, itemIds);
            await gate.WaitAsync(token);
            try
            {
                Check(requestContext, artifactId); LastNameBlockReads = 0; LastPayloadReads = 0; LastScannedRows = 0;
                var result = new ItemIconField[ids.Length];
                for (var i = 0; i < ids.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var id = uint.Parse(ids[i], CultureInfo.InvariantCulture);
                    var ordinal = await Bound(id, token);
                    if (ordinal == count) { result[i] = new(ids[i], false, null); continue; }
                    var entry = await Entry(ordinal, token);
                    result[i] = entry.Id == id ? new(ids[i], true, (await Payload(entry, token)).IconFileDataId) : new(ids[i], false, null);
                }
                return new(Snapshot, result);
            }
            finally { gate.Release(); }
        }

        internal async Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token)
        {
            ItemsService.Validate(request); await gate.WaitAsync(token);
            try
            {
                Check(request.Context, request.ArtifactId); var id = uint.Parse(request.ItemId, CultureInfo.InvariantCulture);
                var ordinal = await Bound(id, token);
                if (ordinal == count || (await Entry(ordinal, token)).Id != id) return new(request.Context, request.ArtifactId, request.ItemId, null);
                var entry = await Entry(ordinal, token); ItemName name;
                if (entry.NameState == ItemNameState.Valid)
                { var bytes = new byte[entry.NameLength]; input.Position = namesOffset + entry.NameOffset; await input.ReadExactlyAsync(bytes, token); name = new(ItemNameState.Valid, Utf8.GetString(bytes)); }
                else name = new(entry.NameState, entry.NameState == ItemNameState.Empty ? "" : null);
                var summary = await Summary(entry, name, token);
                TableArtifactPage item, sparse;
                item = await itemRows.ReadPageAsync(artifact.Binding.ItemRows, 0, 50, id, token);
                sparse = await sparseRows.ReadPageAsync(artifact.Binding.SparseRows, 0, 50, id, token);
                if (item.Rows.Count != 1 || sparse.Rows.Count > 1 || (sparse.Rows.Count == 0) != (entry.NameState == ItemNameState.MissingSparse))
                    throw Error("ITEMS_SOURCE_INVALID", "The original source rows differ from their admitted Item-anchored projection.");
                var result = new ItemInspectResult(request.Context, request.ArtifactId, request.ItemId,
                    new(summary, TableRowProjection.Row(item.Rows[0]), sparse.Rows.Count == 0 ? null : TableRowProjection.Row(sparse.Rows[0])));
                if (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > MaxResponseBytes)
                    throw Error("ITEMS_DETAIL_LIMIT", "The complete original Item evidence exceeds the bounded detail response. Inspect the source tables separately.");
                return result;
            }
            finally { gate.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            Task retirement;
            await gate.WaitAsync();
            try
            {
                disposed = true;
                retirement = retirementCompletion ??= DisposeOwnedResourcesAsync(itemRows, sparseRows, input);
            }
            finally { gate.Release(); }
            await retirement;
        }
        // Exactly these three owners, not a general disposal scheduler. Every close
        // is attempted, and the one retained completion replays uncertain failure
        // to concurrent/repeated callers instead of treating disposed as proof.
        internal static async Task DisposeOwnedResourcesAsync(IAsyncDisposable item, IAsyncDisposable sparse, IAsyncDisposable projection)
        {
            List<Exception>? failures = null;
            try { await item.DisposeAsync(); } catch (Exception failure) { (failures ??= []).Add(failure); }
            try { await sparse.DisposeAsync(); } catch (Exception failure) { (failures ??= []).Add(failure); }
            try { await projection.DisposeAsync(); } catch (Exception failure) { (failures ??= []).Add(failure); }
            if (failures is not null) throw new AggregateException("Items resource retirement could not be confirmed.", failures);
        }
    }
}
