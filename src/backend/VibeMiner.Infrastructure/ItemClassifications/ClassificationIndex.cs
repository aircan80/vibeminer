using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Signed8 semantic-key index. One representative per group; never one label per Item.</summary>
internal sealed class ClassificationIndex(VerifiedObjectStore objects, AcquisitionResources resources,
    IAcquisitionWorkContext? workContext = null, Action<string>? deleteStaging = null)
{
    private const int HeaderBytes = 64, EntryBytes = 32;
    internal static readonly SemaphoreSlim Builder = new(1, 1);
    private DerivedArtifactStaging? cleanupDebt;
    private void RequireBuilderCleanup()
    {
        if (Volatile.Read(ref cleanupDebt) is { } debt) throw debt.Failure();
    }
    internal sealed record Candidate(ItemClassificationState State, string? Text, ItemClassificationTarget Target);
    private sealed class Group(Candidate first)
    {
        internal int Count = 1;
        internal Candidate? First = first;
    }
    internal readonly record struct Entry(int Key, int Count, long Offset, int Length);

    internal async Task<ClassificationIndexArtifact> BuildAsync(ClassificationIndexBinding binding,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token, TableRowArtifacts? heldRows = null)
    {
        RequireBuilderCleanup();
        ClassificationPolicy.ValidateBinding(binding);
        if (binding.Kind != ItemClassificationKind.Class) throw ClassificationPolicy.Invalid();
        var metadata = ClassificationPolicy.NormalizedBytes(binding);
        if (!await Builder.WaitAsync(0, token)) throw new DiscoveryException("ITEM_CLASSIFICATIONS_BUSY", "Another classification index is being prepared.", true);
        string? path = null; DerivedArtifactStaging? staging = null;
        try
        {
            RequireBuilderCleanup();
            // A single <=16MiB file replaces the proposed spool; reserve the conservative full32MiB budget.
            staging = new(await resources.ReserveStagingAsync(32L << 20, workContext, token),
                deleteStaging ?? File.Delete, "ITEM_CLASSIFICATIONS_CLEANUP_FAILED");
            await using var ownedRows = heldRows is null ? new TableRowArtifacts(objects, resources, workContext) : null;
            var rows = heldRows ?? ownedRows!;
            var groups = new Group?[256]; long after = 0; uint? previous = null;
            do
            {
                var page = await rows.ReadPageAsync(binding.Rows, after, 50, null, token);
                using (await resources.EnterCpuAsync(workContext, token))
                {
                    foreach (var row in page.Rows)
                    {
                        token.ThrowIfCancellationRequested();
                        if (previous is { } id && row.Id <= id) throw ClassificationPolicy.Invalid();
                        previous = row.Id;
                        var raw = row.Values[binding.KeyFields[0].ColumnIndex];
                        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ||
                            key.ToString(CultureInfo.InvariantCulture) != raw || key is < -128 or > 127) throw ClassificationPolicy.Invalid();
                        var slot = key + 128;
                        if (groups[slot] is { } group) { group.Count++; group.First = null; }
                        else groups[slot] = new(Text(row, binding));
                    }
                }
                after += page.Rows.Count;
                if (page.NextCursor is not null && (page.Rows.Count == 0 || page.NextCursor != after)) throw ClassificationPolicy.Invalid();
                await Task.Yield(); token.ThrowIfCancellationRequested();
                await progress(new("Indexing class labels", "rows", after, page.TotalCount, ClassificationPolicy.ClassTable));
                if (page.NextCursor is null) break;
            } while (true);
            if (after != binding.Rows.Completion.PhysicalRows + binding.Rows.Completion.CopyRows || after > ClassificationPolicy.MaxRows)
                throw ClassificationPolicy.Invalid();
            var count = groups.Count(g => g is not null);
            var payloadStart = HeaderBytes + metadata.Length + count * EntryBytes;
            var entries = new List<Entry>(count);
            long length;
            {
                var file = await staging.CreateAsync(objects.CreateStagingFileAsync, token);
                path = file.Name;
                await file.WriteAsync(new byte[HeaderBytes], token);
                await file.WriteAsync(metadata, token);
                await file.WriteAsync(new byte[count * EntryBytes], token);
                var encodedSinceYield = 0;
                for (var i = 0; i < groups.Length; i++)
                {
                    if (groups[i] is not { } group) continue;
                    byte[] data;
                    using (await resources.EnterCpuAsync(workContext, token))
                    {
                        token.ThrowIfCancellationRequested();
                        data = group.First is null ? [] : JsonSerializer.SerializeToUtf8Bytes(group.First, ClassificationPolicy.Json);
                    }
                    if (file.Position + data.Length > ClassificationPolicy.MaxArtifactBytes) throw ClassificationPolicy.Limit("The classification index exceeds sixteen MiB.");
                    entries.Add(new(i - 128, group.Count, file.Position, data.Length));
                    await file.WriteAsync(data, token);
                    if (++encodedSinceYield == 16)
                    { encodedSinceYield = 0; await Task.Yield(); token.ThrowIfCancellationRequested(); }
                }
                length = file.Position;
                file.Position = HeaderBytes + metadata.Length;
                foreach (var entry in entries)
                {
                    var data = new byte[EntryBytes];
                    BinaryPrimitives.WriteInt32LittleEndian(data, entry.Key);
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), entry.Count);
                    BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), entry.Offset);
                    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), entry.Length);
                    await file.WriteAsync(data, token);
                }
                var header = new byte[HeaderBytes]; "VMCLASS1"u8.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), metadata.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), count);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), (int)after);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), payloadStart);
                file.Position = 0; await file.WriteAsync(header, token); await file.FlushAsync(token);
            }
            await staging.CloseAsync();
            var sha = await objects.PublishFileAsync(path, length, resources, workContext, token);
            var artifact = new ClassificationIndexArtifact(ClassificationPolicy.ContentKey(binding), sha, length,
                binding, N(after), N(count)); ClassificationPolicy.Validate(artifact); return artifact;
        }
        finally
        {
            try
            {
                if (staging is not null && !await staging.RetireAsync())
                {
                    Volatile.Write(ref cleanupDebt, staging);
                    throw staging.Failure();
                }
            }
            finally { Builder.Release(); }
        }
    }
    internal static Candidate Text(Db2Row row, ClassificationIndexBinding binding)
    {
        var text = row.Values[binding.TextField.ColumnIndex];
        var invalid = row.TextIssues?.Any(issue => issue.ColumnIndex == binding.TextField.ColumnIndex) == true;
        var state = invalid ? ItemClassificationState.InvalidText : text.Length > ClassificationPolicy.MaxTextUnits
            ? ItemClassificationState.Unsupported : text.Length == 0 ? ItemClassificationState.EmptyText : ItemClassificationState.Ready;
        return new(state, state is ItemClassificationState.Ready or ItemClassificationState.EmptyText ? text : null,
            new(binding.Source.TableId, N(row.Id), binding.TextField.ColumnIndex, row.IsCopy,
                row.CopySourceId is { } source ? N(source) : null));
    }
    internal async Task<Session> OpenAsync(ClassificationIndexArtifact artifact, CancellationToken token)
    {
        ClassificationPolicy.Validate(artifact);
        if (artifact.Binding.Kind != ItemClassificationKind.Class) throw ClassificationPolicy.Invalid();
        var stream = await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, workContext, token);
        try
        {
            var header = new byte[HeaderBytes]; await stream.ReadExactlyAsync(header, token);
            int metadataLength, count, rows; long payload;
            using (await resources.EnterCpuAsync(workContext, token))
            {
                metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
                rows = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
                payload = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
                if (!header.AsSpan(0, 8).SequenceEqual("VMCLASS1"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != 1 ||
                    metadataLength is < 1 or > ClassificationPolicy.MaxLiveDescriptorBytes || count is < 0 or > 256 ||
                    N(count) != artifact.KeyCount || N(rows) != artifact.RowCount ||
                    payload != HeaderBytes + metadataLength + count * EntryBytes || payload > artifact.ByteLength ||
                    header.AsSpan(32).ContainsAnyExcept((byte)0)) throw ClassificationPolicy.Invalid();
            }
            var metadata = new byte[metadataLength]; await stream.ReadExactlyAsync(metadata, token);
            using (await resources.EnterCpuAsync(workContext, token))
                if (!metadata.AsSpan().SequenceEqual(ClassificationPolicy.NormalizedBytes(artifact.Binding))) throw ClassificationPolicy.Invalid();
            var directory = new byte[count * EntryBytes]; await stream.ReadExactlyAsync(directory, token);
            var entries = new Entry?[256]; int previous = -129; long total = 0, offset = payload;
            using (await resources.EnterCpuAsync(workContext, token))
            for (var i = 0; i < count; i++)
            {
                var data = directory.AsSpan(i * EntryBytes, EntryBytes);
                var entry = new Entry(BinaryPrimitives.ReadInt32LittleEndian(data), BinaryPrimitives.ReadInt32LittleEndian(data[4..]),
                    BinaryPrimitives.ReadInt64LittleEndian(data[8..]), BinaryPrimitives.ReadInt32LittleEndian(data[16..]));
                if (entry.Key is < -128 or > 127 || entry.Key <= previous || entry.Count is < 1 or > ClassificationPolicy.MaxRows ||
                    entry.Offset != offset || entry.Length is < 0 or > (64 << 10) || entry.Count == 1 && entry.Length == 0 ||
                    entry.Count > 1 && entry.Length != 0 || offset > artifact.ByteLength - entry.Length || data[20..].ContainsAnyExcept((byte)0))
                    throw ClassificationPolicy.Invalid();
                previous = entry.Key; total += entry.Count; offset += entry.Length; entries[entry.Key + 128] = entry;
            }
            if (total != rows || offset != artifact.ByteLength) throw ClassificationPolicy.Invalid();
            return new(artifact, stream, entries, resources, workContext);
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    internal sealed class Session : ClassificationIndexSession
    {
        internal override ClassificationIndexArtifact Artifact { get; }
        private readonly FileStream stream;
        private readonly Entry?[] entries;
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool closed;
        private readonly AcquisitionResources resources;
        private readonly IAcquisitionWorkContext? workContext;
        internal Session(ClassificationIndexArtifact artifact, FileStream stream, Entry?[] entries,
            AcquisitionResources resources, IAcquisitionWorkContext? workContext)
        { Artifact = artifact; this.stream = stream; this.entries = entries; this.resources = resources; this.workContext = workContext; }
        internal override async Task<ItemClassificationValue> ResolveAsync(int key, IReadOnlyList<ItemSourceValue> keys, CancellationToken token)
        {
            if (key is < -128 or > 127) return new(ItemClassificationState.KeyOutOfRange, keys);
            var complete = Artifact.Binding.Source.Coverage.Scope == "complete";
            var entry = entries[key + 128];
            if (entry is null) return new(complete ? ItemClassificationState.NotFound : ItemClassificationState.IncompleteTarget, keys, ObservedCandidates: "0");
            if (entry.Value.Count > 1) return new(ItemClassificationState.Ambiguous, keys, ObservedCandidates: N(entry.Value.Count));
            if (!complete) return new(ItemClassificationState.IncompleteTarget, keys, ObservedCandidates: "1");
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(closed, this);
                var bytes = new byte[entry.Value.Length]; stream.Position = entry.Value.Offset; await stream.ReadExactlyAsync(bytes, token);
                using var cpu = await resources.EnterCpuAsync(workContext, token);
                Candidate candidate;
                try { candidate = JsonSerializer.Deserialize<Candidate>(bytes, ClassificationPolicy.Json) ?? throw ClassificationPolicy.Invalid(); }
                catch (JsonException) { throw ClassificationPolicy.Invalid(); }
                ValidateCandidate(candidate, Artifact.Binding);
                return new(candidate.State, keys, candidate.Text, candidate.Target, "1",
                    candidate.State == ItemClassificationState.Unsupported ? new("ITEM_CLASSIFICATION_TEXT_LIMIT", "The exact unique label exceeds4096 text units.") : null);
            }
            finally { gate.Release(); }
        }
        public override async ValueTask DisposeAsync()
        { await gate.WaitAsync(); try { if (closed) return; closed = true; await stream.DisposeAsync(); } finally { gate.Release(); } }
    }
    internal static void ValidateCandidate(Candidate c, ClassificationIndexBinding binding)
    {
        if (c.State is not (ItemClassificationState.Ready or ItemClassificationState.EmptyText or ItemClassificationState.InvalidText or ItemClassificationState.Unsupported) ||
            c.State == ItemClassificationState.Ready && c.Text is not { Length: >= 1 and <= ClassificationPolicy.MaxTextUnits } ||
            c.State == ItemClassificationState.EmptyText && c.Text != "" ||
            c.State is ItemClassificationState.InvalidText or ItemClassificationState.Unsupported && c.Text is not null ||
            c.Target is null || c.Target.TableId != binding.Source.TableId || c.Target.ColumnIndex != binding.TextField.ColumnIndex ||
            !Canonical(c.Target.RowId) || c.Target.IsCopy != (c.Target.CopySourceId is not null) || c.Target.CopySourceId is { } id && !Canonical(id))
            throw ClassificationPolicy.Invalid();
    }
    private static bool Canonical(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && N(id) == value;
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
