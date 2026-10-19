using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal abstract class ClassificationIndexSession : IAsyncDisposable
{
    internal abstract ClassificationIndexArtifact Artifact { get; }
    internal abstract Task<ItemClassificationValue> ResolveAsync(int key, IReadOnlyList<ItemSourceValue> keys, CancellationToken token);
    public abstract ValueTask DisposeAsync();
}

/// <summary>Finite pair directory; labels spill to bounded disk and never accumulate by key in memory.</summary>
internal sealed class CompositeClassificationIndex(VerifiedObjectStore objects, AcquisitionResources resources,
    Func<CancellationToken, Task<FileStream>>? createStaging = null, IAcquisitionWorkContext? workContext = null, Action<string>? deleteStaging = null)
{
    private DerivedArtifactStaging? cleanupDebt;
    private void RequireBuilderCleanup()
    {
        if (Volatile.Read(ref cleanupDebt) is { } debt) throw debt.Failure();
    }
    private const int Slots = 65_536, HeaderBytes = 64, EntryBytes = 32, BufferBytes = 65_536;
    internal async Task<ClassificationIndexArtifact> BuildAsync(ClassificationIndexBinding binding,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token, TableRowArtifacts? heldRows = null)
    {
        RequireBuilderCleanup();
        ClassificationPolicy.ValidateBinding(binding);
        if (binding.Kind != ItemClassificationKind.Subclass) throw ClassificationPolicy.Invalid();
        var metadata = ClassificationPolicy.NormalizedBytes(binding);
        if (!await ClassificationIndex.Builder.WaitAsync(0, token)) throw new DiscoveryException("ITEM_CLASSIFICATIONS_BUSY", "Another classification index is being prepared.", true);
        DerivedArtifactStaging? staging = null;
        try
        {
            RequireBuilderCleanup();
            staging = new(await resources.ReserveStagingAsync(32L << 20, workContext, token),
                deleteStaging ?? File.Delete, "ITEM_CLASSIFICATIONS_CLEANUP_FAILED");
            var counts = new uint[Slots]; var offsets = new long[Slots]; var lengths = new int[Slots];
            await using var ownRows = heldRows is null ? new TableRowArtifacts(objects, resources, workContext) : null;
            var rows = heldRows ?? ownRows!;
            long after = 0; uint? previous = null; int keyCount = 0; long fileLength;
            string finalPath;
            async Task<FileStream> Stage()
            {
                return await staging.CreateAsync(createStaging ?? objects.CreateStagingFileAsync, token);
            }
            {
                var spool = await Stage();
                var final = await Stage();
                finalPath = final.Name;
                do
                {
                    var page = await rows.ReadPageAsync(binding.Rows, after, 50, null, token);
                    foreach (var row in page.Rows)
                    {
                        byte[] payload;
                        using (await resources.EnterCpuAsync(workContext, token))
                        {
                            token.ThrowIfCancellationRequested();
                            if (previous is { } id && row.Id <= id) throw ClassificationPolicy.Invalid();
                            previous = row.Id;
                            var components = new int[2];
                            for (var i = 0; i < components.Length; i++)
                            {
                                var raw = row.Values[binding.KeyFields[i].ColumnIndex];
                                if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out components[i]) ||
                                    N(components[i]) != raw || components[i] is < -128 or > 127) throw ClassificationPolicy.Invalid();
                            }
                            var key = ((components[0] + 128) << 8) | (components[1] + 128);
                            if (counts[key] != 0) { counts[key] = checked(counts[key] + 1); lengths[key] = 0; continue; }
                            payload = JsonSerializer.SerializeToUtf8Bytes(ClassificationIndex.Text(row, binding), ClassificationPolicy.Json);
                            if (payload.Length > BufferBytes || spool.Length + payload.Length > ClassificationPolicy.MaxArtifactBytes)
                                throw ClassificationPolicy.Limit("Subclass label spool exceeds its sixteen MiB allowance.");
                            counts[key] = 1; offsets[key] = spool.Position; lengths[key] = payload.Length; keyCount++;
                        }
                        await spool.WriteAsync(payload, token);
                    }
                    after += page.Rows.Count;
                    if (page.NextCursor is not null && (page.Rows.Count == 0 || page.NextCursor != after)) throw ClassificationPolicy.Invalid();
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                    await progress(new("Indexing subclass labels", "rows", after, page.TotalCount, ClassificationPolicy.SubclassTable));
                    if (page.NextCursor is null) break;
                } while (true);
                if (after != binding.Rows.Completion.PhysicalRows + binding.Rows.Completion.CopyRows) throw ClassificationPolicy.Invalid();
                long payloadOffset = HeaderBytes + metadata.Length + (long)keyCount * EntryBytes;
                var payloadEnd = payloadOffset;
                for (var begin = 0; begin < Slots; begin += 4096)
                {
                    using (await resources.EnterCpuAsync(workContext, token))
                        for (var i = begin; i < begin + 4096; i++)
                        { token.ThrowIfCancellationRequested(); if (counts[i] == 1) payloadEnd = checked(payloadEnd + lengths[i]); }
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                }
                if (payloadEnd > ClassificationPolicy.MaxArtifactBytes || spool.Length + payloadEnd > (32L << 20))
                    throw ClassificationPolicy.Limit("Subclass index exceeds its sixteen MiB final or combined staging allowance.");
                var header = new byte[HeaderBytes]; "VMCLASS2"u8.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 2);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), metadata.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), keyCount);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), checked((int)after));
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), payloadOffset);
                await final.WriteAsync(header, token); await final.WriteAsync(metadata, token);
                var directoryBuffer = new byte[4096 * EntryBytes]; long cursor = payloadOffset;
                for (var begin = 0; begin < Slots; begin += 4096)
                {
                    var written = 0;
                    using (await resources.EnterCpuAsync(workContext, token))
                        for (var key = begin; key < begin + 4096; key++)
                        {
                            token.ThrowIfCancellationRequested(); if (counts[key] == 0) continue;
                            var entry = directoryBuffer.AsSpan(written * EntryBytes, EntryBytes); entry.Clear();
                            BinaryPrimitives.WriteInt32LittleEndian(entry, key);
                            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], counts[key]);
                            BinaryPrimitives.WriteInt64LittleEndian(entry[8..], cursor);
                            var length = counts[key] == 1 ? lengths[key] : 0;
                            BinaryPrimitives.WriteInt32LittleEndian(entry[16..], length);
                            written++; cursor += length;
                        }
                    await final.WriteAsync(directoryBuffer.AsMemory(0, written * EntryBytes), token);
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                }
                await spool.FlushAsync(token); var copy = new byte[BufferBytes];
                var live = new int[4096]; var copiedBytes = 0;
                for (var begin = 0; begin < Slots; begin += live.Length)
                {
                    var liveCount = 0;
                    using (await resources.EnterCpuAsync(workContext, token))
                        for (var key = begin; key < begin + live.Length; key++)
                        { token.ThrowIfCancellationRequested(); if (counts[key] == 1) live[liveCount++] = key; }
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                    for (var i = 0; i < liveCount; i++)
                    {
                        var key = live[i]; spool.Position = offsets[key]; var remaining = lengths[key];
                        while (remaining > 0)
                        {
                            var take = Math.Min(remaining, Math.Min(copy.Length, (1 << 20) - copiedBytes));
                            await spool.ReadExactlyAsync(copy.AsMemory(0, take), token);
                            await final.WriteAsync(copy.AsMemory(0, take), token); remaining -= take; copiedBytes += take;
                            if (copiedBytes >= (1 << 20))
                            { copiedBytes = 0; await Task.Yield(); token.ThrowIfCancellationRequested(); }
                        }
                    }
                }
                fileLength = final.Position;
                if (fileLength != payloadEnd) throw ClassificationPolicy.Invalid();
                await final.FlushAsync(token);
            }
            await staging.CloseAsync();
            var sha = await objects.PublishFileAsync(finalPath, fileLength, resources, workContext, token);
            var artifact = new ClassificationIndexArtifact(ClassificationPolicy.ContentKey(binding), sha, fileLength, binding,
                N(after), N(keyCount), ClassificationPolicy.CompositeRevision);
            ClassificationPolicy.Validate(artifact); return artifact;
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
            finally { ClassificationIndex.Builder.Release(); }
        }
    }
    internal async Task<ClassificationIndexSession> OpenAsync(ClassificationIndexArtifact artifact, CancellationToken token)
    {
        ClassificationPolicy.Validate(artifact);
        if (artifact.Binding.Kind != ItemClassificationKind.Subclass) throw ClassificationPolicy.Invalid();
        var stream = await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, workContext, token);
        try
        {
            var header = new byte[HeaderBytes]; await stream.ReadExactlyAsync(header, token);
            int metadataLength, count, rows; long payload;
            using (await resources.EnterCpuAsync(workContext, token))
            {
                metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
                count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)); rows = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
                payload = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
                if (!header.AsSpan(0, 8).SequenceEqual("VMCLASS2"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != 2 ||
                    metadataLength is < 1 or > ClassificationPolicy.MaxLiveDescriptorBytes || count is < 0 or > Slots ||
                    N(count) != artifact.KeyCount || N(rows) != artifact.RowCount || payload != HeaderBytes + metadataLength + (long)count * EntryBytes ||
                    payload > artifact.ByteLength || header.AsSpan(32).ContainsAnyExcept((byte)0)) throw ClassificationPolicy.Invalid();
            }
            var metadata = new byte[metadataLength]; await stream.ReadExactlyAsync(metadata, token);
            using (await resources.EnterCpuAsync(workContext, token))
                if (!metadata.AsSpan().SequenceEqual(ClassificationPolicy.NormalizedBytes(artifact.Binding))) throw ClassificationPolicy.Invalid();
            var directory = HeaderBytes + metadataLength; var buffer = new byte[BufferBytes];
            int seen = 0, previous = -1; long total = 0, offset = payload;
            while (seen < count)
            {
                token.ThrowIfCancellationRequested(); var entries = Math.Min(count - seen, buffer.Length / EntryBytes);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, entries * EntryBytes), token);
                using (await resources.EnterCpuAsync(workContext, token))
                for (var i = 0; i < entries; i++)
                {
                    var entry = Read(buffer.AsSpan(i * EntryBytes, EntryBytes));
                    if (entry.Key <= previous || entry.Key >= Slots || entry.Count is < 1 or > ClassificationPolicy.MaxRows || entry.Offset != offset ||
                        entry.Length is < 0 or > BufferBytes || entry.Count == 1 && entry.Length == 0 || entry.Count > 1 && entry.Length != 0 ||
                        offset > artifact.ByteLength - entry.Length) throw ClassificationPolicy.Invalid();
                    previous = entry.Key; total += entry.Count; offset += entry.Length;
                }
                seen += entries;
                await Task.Yield(); token.ThrowIfCancellationRequested();
            }
            if (total != rows || offset != artifact.ByteLength) throw ClassificationPolicy.Invalid();
            return new Session(artifact, stream, directory, count, resources, workContext);
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private readonly record struct Entry(int Key, uint Count, long Offset, int Length);
    private static Entry Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes[20..].ContainsAnyExcept((byte)0)) throw ClassificationPolicy.Invalid();
        return new(BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]));
    }
    private sealed class Session(ClassificationIndexArtifact artifact, FileStream stream, long directory, int count,
        AcquisitionResources resources, IAcquisitionWorkContext? workContext) : ClassificationIndexSession
    {
        internal override ClassificationIndexArtifact Artifact => artifact;
        private readonly SemaphoreSlim gate = new(1, 1);
        private bool closed;
        internal override async Task<ItemClassificationValue> ResolveAsync(int key, IReadOnlyList<ItemSourceValue> keys, CancellationToken token)
        {
            if (key is < 0 or >= Slots) return new(ItemClassificationState.KeyOutOfRange, keys);
            await gate.WaitAsync(token);
            try
            {
                ObjectDisposedException.ThrowIf(closed, this); int lo = 0, hi = count - 1; Entry? found = null;
                var bytes = new byte[EntryBytes];
                while (lo <= hi)
                {
                    var mid = lo + (hi - lo) / 2; stream.Position = directory + (long)mid * EntryBytes;
                    await stream.ReadExactlyAsync(bytes, token);
                    using (await resources.EnterCpuAsync(workContext, token))
                    {
                        var entry = Read(bytes);
                        if (entry.Key == key) { found = entry; break; }
                        if (entry.Key < key) lo = mid + 1; else hi = mid - 1;
                    }
                }
                var complete = artifact.Binding.Source.Coverage.Scope == "complete";
                if (found is null) return new(complete ? ItemClassificationState.NotFound : ItemClassificationState.IncompleteTarget, keys, ObservedCandidates: "0");
                var match = found.Value;
                if (match.Count > 1) return new(ItemClassificationState.Ambiguous, keys, ObservedCandidates: N(match.Count));
                if (!complete) return new(ItemClassificationState.IncompleteTarget, keys, ObservedCandidates: "1");
                var payload = new byte[match.Length]; stream.Position = match.Offset; await stream.ReadExactlyAsync(payload, token);
                using var cpu = await resources.EnterCpuAsync(workContext, token);
                ClassificationIndex.Candidate candidate;
                try { candidate = JsonSerializer.Deserialize<ClassificationIndex.Candidate>(payload, ClassificationPolicy.Json) ?? throw ClassificationPolicy.Invalid(); }
                catch (JsonException) { throw ClassificationPolicy.Invalid(); }
                ClassificationIndex.ValidateCandidate(candidate, artifact.Binding);
                return new(candidate.State, keys, candidate.Text, candidate.Target, "1", candidate.State == ItemClassificationState.Unsupported
                    ? new("ITEM_CLASSIFICATION_TEXT_LIMIT", "The exact unique label exceeds4096 text units.") : null);
            }
            finally { gate.Release(); }
        }
        public override async ValueTask DisposeAsync()
        { await gate.WaitAsync(); try { if (closed) return; closed = true; await stream.DisposeAsync(); } finally { gate.Release(); } }
    }
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
