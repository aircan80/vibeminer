using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed partial class CreaturesProjection
{
    private sealed record Cursor(string Binding, long After);
    private static string CursorBinding(CreaturesPageRequest request) => Convert.ToHexStringLower(SHA256.HashData(CreaturesPolicy.Bytes(new
        { request.Context, request.ArtifactId, CreaturesPolicy.QueryPolicy, request.Query, request.ExactCreatureId, request.Limit, order = "emitted-uint32-ascending-v1" })));
    internal async Task<Session> OpenAsync(CreaturesArtifact artifact, string preparedAt, CancellationToken token)
    {
        CreaturesPolicy.Validate(artifact); FileStream? file = null;
        try
        {
            file = await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, null, token);
            var header = new byte[HeaderBytes]; await ReadAtAsync(file, 0, header, token);
            if (!header.AsSpan(0, 8).SequenceEqual("VMCRTR01"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != DirectoryBytes || header.AsSpan(40).ContainsAnyExcept((byte)0)) throw CreaturesPolicy.Invalid();
            var rows = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)); var metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var summaryBytes = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24)); var nameBytes = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
            if (rows > 1_000_000 || rows != CreaturesPolicy.Total(artifact.Binding) || metadataLength is < 1 or > (128 << 10) || summaryBytes < 0 ||
                summaryBytes > rows * 2048L || nameBytes is < 0 or > (64L << 20)) throw CreaturesPolicy.Invalid();
            var directory = checked(HeaderBytes + (long)metadataLength); var summaries = checked(directory + rows * DirectoryBytes); var names = checked(summaries + summaryBytes);
            if (checked(names + nameBytes) != artifact.ByteLength) throw CreaturesPolicy.Invalid();
            var metadata = new byte[metadataLength]; await ReadAtAsync(file, HeaderBytes, metadata, token);
            using (await resources.EnterCpuAsync(null, token))
            {
                var expected = CreaturesPolicy.Bytes(new Metadata(CreaturesPolicy.Normalize(artifact.Binding), artifact.Counts, CreaturesPolicy.FeatureRevision, CreaturesPolicy.QueryPolicy, CreaturesPolicy.SemanticProfile));
                if (!metadata.AsSpan().SequenceEqual(expected)) throw CreaturesPolicy.Invalid();
            }
            var session = new Session(file, artifact, preparedAt, resources, directory, summaries, names, rows);
            await session.ValidateAsync(summaryBytes, nameBytes, token); file = null; return session;
        }
        catch (Exception error) when (error is JsonException or OverflowException or EndOfStreamException or DecoderFallbackException or ArgumentException)
        { throw CreaturesPolicy.Invalid(); }
        finally { if (file is not null) await file.DisposeAsync(); }
    }
    private static async Task ReadAtAsync(FileStream file, long offset, Memory<byte> bytes, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        {
            var read = await RandomAccess.ReadAsync(file.SafeFileHandle, bytes, offset, token);
            if (read == 0) throw CreaturesPolicy.Invalid(); offset += read; bytes = bytes[read..];
        }
    }
    internal sealed class Session : IAsyncDisposable
    {
        private readonly FileStream file; private readonly AcquisitionResources resources;
        private readonly long directory, summaries, names, rows;
        internal CreaturesArtifact Artifact { get; }
        internal CreaturesSnapshot Snapshot { get; }
        private long directoryReads, nameReads, summaryReads, decodedNames;
        internal (long DirectoryReads, long NameReads, long SummaryReads, long DecodedNames) ReadCounts =>
            (Interlocked.Read(ref directoryReads), Interlocked.Read(ref nameReads), Interlocked.Read(ref summaryReads), Interlocked.Read(ref decodedNames));
        internal Session(FileStream file, CreaturesArtifact artifact, string preparedAt, AcquisitionResources resources, long directory, long summaries, long names, long rows)
        {
            this.file = file; this.resources = resources; this.directory = directory; this.summaries = summaries; this.names = names; this.rows = rows; Artifact = artifact;
            Snapshot = new(artifact.Binding.Context, artifact.ContentKey, preparedAt, CreaturesPolicy.FeatureRevision, CreaturesPolicy.QueryPolicy, CreaturesPolicy.SemanticProfile,
                artifact.Binding.Source, artifact.Counts, CreaturesPolicy.FieldMap(artifact.Binding));
        }
        private async Task<Entry> AtAsync(long ordinal, CancellationToken token)
        {
            if (ordinal < 0 || ordinal >= rows) throw CreaturesPolicy.Invalid();
            Interlocked.Increment(ref directoryReads);
            var bytes = new byte[DirectoryBytes]; await ReadAtAsync(file, checked(directory + ordinal * DirectoryBytes), bytes, token); return Entry.Read(bytes);
        }
        private async Task<byte[]> ReadPayloadAsync(Entry entry, CancellationToken token)
        {
            Interlocked.Increment(ref summaryReads);
            var bytes = new byte[entry.SummaryLength]; await ReadAtAsync(file, checked(summaries + entry.SummaryOffset), bytes, token);
            return bytes;
        }
        private Payload ParsePayload(Entry entry, byte[] bytes)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<Payload>(bytes, CreaturesPolicy.Json);
                if (payload is null || payload.Facts is null || payload.Facts.Count != 4 || payload.DisplayIds is null || payload.DisplayIds.Count != 4 ||
                    payload.Probabilities is null || payload.Probabilities.Count != 4 || payload.Facts.Any(v => v is null) || payload.DisplayIds.Any(v => v is null) || payload.Probabilities.Any(v => v is null) ||
                    payload.NameIssue is not (null or "DB2_TEXT_ENCODING_INVALID") || (entry.State == CreatureNameState.Invalid) != (payload.NameIssue is not null)) throw CreaturesPolicy.Invalid();
                // Reuse the exact numeric/profile validator without opening or replaying source rows.
                var values = new string[Artifact.Binding.Source.Columns.Count]; Array.Fill(values, "");
                for (var i = 0; i < 4; i++) { values[Artifact.Binding.Fields[4 + i].ColumnIndex] = payload.Facts[i]; values[Artifact.Binding.Fields[8 + i].ColumnIndex] = payload.DisplayIds[i]; values[Artifact.Binding.Fields[12 + i].ColumnIndex] = payload.Probabilities[i]; }
                _ = PayloadFrom(Artifact.Binding, new(entry.Id, false, null, values,
                    payload.NameIssue is null ? null : [new(Artifact.Binding.Fields[0].ColumnIndex, payload.NameIssue, "")]));
                return payload;
            }
            catch (JsonException) { throw CreaturesPolicy.Invalid(); }
        }
        private async Task<byte[]> ReadNameAsync(Entry entry, CancellationToken token)
        {
            if (entry.NameLength == 0) return [];
            Interlocked.Increment(ref nameReads); Interlocked.Increment(ref decodedNames);
            var bytes = new byte[entry.NameLength]; await ReadAtAsync(file, checked(names + entry.NameOffset), bytes, token);
            return bytes;
        }
        private static string ParseName(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            var value = Utf8.GetString(bytes); if (value.Length is < 1 or > 65536) throw CreaturesPolicy.Invalid(); return value;
        }
        internal async Task ValidateAsync(long summaryBytes, long nameBytes, CancellationToken token)
        {
            long summaryEnd = 0, nameEnd = 0, valid = 0, empty = 0, invalid = 0, positive = 0, zero = 0, negative = 0;
            uint? previous = null; var sliceBytes = 0; var sliceChars = 0; var sliceEntries = 0;
            for (long i = 0; i < rows; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = await AtAsync(i, token);
                var characters = Math.Min(Math.Max(entry.NameLength, 0), 65536);
                if (sliceEntries >= 4096 || sliceBytes + Math.Max(entry.NameLength, 0) + Math.Max(entry.SummaryLength, 0) > 1 << 20 || sliceChars + characters > 65536)
                { await Task.Yield(); token.ThrowIfCancellationRequested(); sliceEntries = sliceBytes = sliceChars = 0; }
                sliceEntries++; sliceBytes += Math.Max(entry.NameLength, 0) + Math.Max(entry.SummaryLength, 0); sliceChars += characters;
                if (previous is { } id && entry.Id <= id || entry.NameOffset != nameEnd || entry.SummaryOffset != summaryEnd ||
                        entry.NameLength is < 0 or > 262144 || entry.SummaryLength is < 1 or > 2048 ||
                        entry.State == CreatureNameState.Valid && entry.NameLength == 0 || entry.State != CreatureNameState.Valid && entry.NameLength != 0 ||
                        entry.NameLength > nameBytes - nameEnd || entry.SummaryLength > summaryBytes - summaryEnd) throw CreaturesPolicy.Invalid();
                var payloadBytes = await ReadPayloadAsync(entry, token);
                var name = entry.State == CreatureNameState.Valid ? await ReadNameAsync(entry, token) : [];
                using (await resources.EnterCpuAsync(null, token))
                {
                    token.ThrowIfCancellationRequested();
                    previous = entry.Id; nameEnd += entry.NameLength; summaryEnd += entry.SummaryLength;
                    var payload = ParsePayload(entry, payloadBytes); if (entry.State == CreatureNameState.Valid) { _ = ParseName(name); valid++; } else if (entry.State == CreatureNameState.Empty) empty++; else invalid++;
                    foreach (var raw in payload.DisplayIds) { var display = int.Parse(raw, CultureInfo.InvariantCulture); if (display > 0) positive++; else if (display == 0) zero++; else negative++; }
                }
            }
            if (nameEnd != nameBytes || summaryEnd != summaryBytes || new CreatureCounts(CreaturesPolicy.N(rows), CreaturesPolicy.N(valid), CreaturesPolicy.N(empty), CreaturesPolicy.N(invalid),
                CreaturesPolicy.N(positive), CreaturesPolicy.N(zero), CreaturesPolicy.N(negative)) != Artifact.Counts) throw CreaturesPolicy.Invalid();
        }
        private CreatureSummary Summary(Entry entry, Payload payload, string? name)
        {
            var binding = Artifact.Binding; var id = entry.Id.ToString(CultureInfo.InvariantCulture);
            var typed = new CreatureName(entry.State, entry.State == CreatureNameState.Invalid ? null : name ?? "",
                payload.NameIssue is null ? null : new(payload.NameIssue, new(CreaturesPolicy.Table, binding.Source.ArtifactId, id, binding.Fields[0].ColumnIndex)));
            var facts = payload.Facts.Select((v, i) => new CreatureSourceValue(CreaturesPolicy.Table, binding.Fields[4 + i].ColumnIndex, v)).ToArray();
            var slots = payload.DisplayIds.Select((v, i) => new CreatureDisplaySlot(i, v, payload.Probabilities[i], int.Parse(v, CultureInfo.InvariantCulture) switch
                { > 0 => CreatureDisplayState.Declared, 0 => CreatureDisplayState.NoDeclaredDisplay, _ => CreatureDisplayState.InvalidDisplayReference },
                new(CreaturesPolicy.Table, binding.Fields[8 + i].ColumnIndex), new(CreaturesPolicy.Table, binding.Fields[12 + i].ColumnIndex))).ToArray();
            return new(id, typed, facts, slots);
        }
        internal async Task<CreatureSummary?> FindAsync(uint id, CancellationToken token)
        {
            long lo = 0, hi = rows - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2; var entry = await AtAsync(mid, token);
                if (entry.Id < id) lo = mid + 1; else if (entry.Id > id) hi = mid - 1;
                else
                {
                    var payload = await ReadPayloadAsync(entry, token); var name = await ReadNameAsync(entry, token);
                    using (await resources.EnterCpuAsync(null, token))
                    { token.ThrowIfCancellationRequested(); return Summary(entry, ParsePayload(entry, payload), ParseName(name)); }
                }
            }
            return null;
        }
        internal async Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token)
        {
            long after = 0;
            if (request.After is not null)
            {
                try
                {
                    var raw = Convert.FromBase64String(request.After.Replace('-', '+').Replace('_', '/') + new string('=', (4 - request.After.Length % 4) % 4));
                    var cursor = JsonSerializer.Deserialize<Cursor>(raw, CreaturesPolicy.Json);
                    if (cursor is null || cursor.Binding != CursorBinding(request) || cursor.After < 0 || cursor.After >= rows) throw CursorInvalid();
                    after = cursor.After;
                }
                catch (Exception error) when (error is JsonException or FormatException) { throw CursorInvalid(); }
            }
            if (request.ExactCreatureId is not null)
            {
                var id = uint.Parse(request.ExactCreatureId, CultureInfo.InvariantCulture); long lo = 0, hi = rows;
                while (lo < hi) { var mid = lo + (hi - lo) / 2; var candidate = await AtAsync(mid, token); if (candidate.Id < id) lo = mid + 1; else hi = mid; }
                after = Math.Max(after, lo);
            }
            var entries = new List<CreatureSummary>(request.Limit); long lastReturned = after; bool more = false; var bytes = 0;
            var directoryBuffer = new byte[4096 * DirectoryBytes]; long blockStart = -1; var blockCount = 0;
            var nameBuffer = new byte[1 << 20]; var characterBuffer = new char[65536]; long nameBlockStart = -1; var nameBlockCount = 0;
            var sliceEntries = 0; var sliceBytes = 0; var sliceChars = 0;
            for (var ordinal = after; ordinal < rows; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                if (blockStart < 0 || ordinal >= blockStart + blockCount)
                {
                    blockStart = ordinal; blockCount = (int)Math.Min(4096, rows - ordinal);
                    Interlocked.Increment(ref directoryReads);
                    await ReadAtAsync(file, checked(directory + ordinal * DirectoryBytes), directoryBuffer.AsMemory(0, blockCount * DirectoryBytes), token);
                }
                var entry = Entry.Read(directoryBuffer.AsSpan((int)(ordinal - blockStart) * DirectoryBytes, DirectoryBytes));
                var characters = Math.Min(entry.NameLength, 65536);
                if (sliceEntries >= 4096 || sliceBytes + entry.NameLength + DirectoryBytes > 1 << 20 || sliceChars + characters > 65536)
                { await Task.Yield(); token.ThrowIfCancellationRequested(); sliceEntries = sliceBytes = sliceChars = 0; }
                sliceEntries++; sliceBytes += entry.NameLength + DirectoryBytes; sliceChars += characters;
                if (request.ExactCreatureId is not null && entry.Id.ToString(CultureInfo.InvariantCulture) != request.ExactCreatureId) break;
                var decodeName = entry.State == CreatureNameState.Valid && (request.Query.Length != 0 || entries.Count < request.Limit);
                if (decodeName && (nameBlockStart < 0 || entry.NameOffset < nameBlockStart || entry.NameOffset + entry.NameLength > nameBlockStart + nameBlockCount))
                {
                    nameBlockStart = entry.NameOffset; nameBlockCount = (int)Math.Min(nameBuffer.Length, Artifact.ByteLength - names - nameBlockStart);
                    Interlocked.Increment(ref nameReads);
                    await ReadAtAsync(file, checked(names + nameBlockStart), nameBuffer.AsMemory(0, nameBlockCount), token);
                }
                string name;
                using (await resources.EnterCpuAsync(null, token))
                {
                    token.ThrowIfCancellationRequested();
                    var decodedCharacters = 0;
                    if (decodeName)
                    {
                        Interlocked.Increment(ref decodedNames);
                        decodedCharacters = Utf8.GetChars(nameBuffer.AsSpan((int)(entry.NameOffset - nameBlockStart), entry.NameLength), characterBuffer);
                        if (decodedCharacters is < 1 or > 65536) throw CreaturesPolicy.Invalid();
                    }
                    if (request.Query.Length != 0 && (entry.State != CreatureNameState.Valid || !characterBuffer.AsSpan(0, decodedCharacters).Contains(request.Query.AsSpan(), StringComparison.OrdinalIgnoreCase))) continue;
                    // One extra match uses scratch, never a fifty-first retained name string.
                    if (entries.Count == request.Limit) { more = true; break; }
                    name = entry.State == CreatureNameState.Valid ? new string(characterBuffer, 0, decodedCharacters) : "";
                }
                var payload = await ReadPayloadAsync(entry, token);
                using (await resources.EnterCpuAsync(null, token))
                {
                    token.ThrowIfCancellationRequested();
                    var summary = Summary(entry, ParsePayload(entry, payload), name);
                    var cost = ResponseBytes(summary);
                    if (cost > CreaturesPolicy.MaxResponseBytes - bytes)
                    {
                        if (entries.Count == 0) throw new DiscoveryException("CREATURES_RESULT_LIMIT", "One Creature result exceeds the bounded response frame.");
                        more = true; break;
                    }
                    entries.Add(summary); bytes += cost; lastReturned = ordinal + 1;
                }
            }
            string? next = null;
            if (more)
            {
                var cursor = new Cursor(CursorBinding(request), lastReturned);
                next = Convert.ToBase64String(CreaturesPolicy.Bytes(cursor)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            return new(request.Context, request.ArtifactId, request.Query, request.ExactCreatureId, entries, next);
        }
        private static int ResponseBytes(CreatureSummary summary)
        {
            // Bound worst-case default JSON escaping without materializing rejected full response frames.
            var fixedFields = JsonSerializer.SerializeToUtf8Bytes(summary with { Name = summary.Name with { Value = null } }, CreaturesPolicy.Json).Length;
            return checked(fixedFields + 8 + (summary.Name.Value?.Length ?? 0) * 6);
        }
        private static DiscoveryException CursorInvalid() => new("CREATURES_CURSOR_INVALID", "The Creature cursor differs from the exact artifact, query, order or page limit.");
        internal async Task VerifyOriginalAsync(CreatureSummary summary, TableDataRow row, CancellationToken token)
        {
            using (await resources.EnterCpuAsync(null, token))
            {
                if (row.Id != summary.Id || !uint.TryParse(row.Id, out var id) || row.CopySourceId is not null && !uint.TryParse(row.CopySourceId, out _)) throw CreaturesPolicy.Invalid();
                var payload = PayloadFrom(Artifact.Binding, new(id, row.IsCopy, row.CopySourceId is null ? null : uint.Parse(row.CopySourceId, CultureInfo.InvariantCulture), row.Values,
                    row.TextIssues?.Select(i => new Db2TextIssue(i.ColumnIndex, i.Code, i.RawBytesHex)).ToArray()));
                var name = row.Values[Artifact.Binding.Fields[0].ColumnIndex];
                var state = payload.NameIssue is not null ? CreatureNameState.Invalid : name.Length == 0 ? CreatureNameState.Empty : CreatureNameState.Valid;
                var rebuilt = Summary(new(id, state, 0, 0, 0, 0), payload, name);
                if (rebuilt.Id != summary.Id || rebuilt.Name != summary.Name || !rebuilt.Facts.SequenceEqual(summary.Facts) || !rebuilt.DisplaySlots.SequenceEqual(summary.DisplaySlots)) throw CreaturesPolicy.Invalid();
            }
        }
        public ValueTask DisposeAsync() => file.DisposeAsync();
    }
}
