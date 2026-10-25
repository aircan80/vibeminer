using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class ItemEffectsBridgeArtifacts
{
    /// <summary>Consumes an already CAS-verified lease immediately, including invalid/canceled entry.
    /// The caller retains its physical reservation if a close cannot be proved to have retired.</summary>
    internal async Task<Session> OpenAsync(ItemEffectsBridgeArtifact artifact, FileStream verifiedOwnedInput, CancellationToken token)
    {
        var input = verifiedOwnedInput;
        try
        {
            if (input is null) throw ItemEffectsBridgePolicy.Invalid();
            if (artifact is null) throw ItemEffectsBridgePolicy.Invalid();
            // Capture complete original descriptor before any await/normalization.
            var frozen = JsonSerializer.Deserialize<ItemEffectsBridgeArtifact>(ItemEffectsBridgePolicy.Bytes(artifact), ItemEffectsBridgePolicy.Json) ?? throw ItemEffectsBridgePolicy.Invalid();
            token.ThrowIfCancellationRequested();
            if (!input.CanRead || !input.CanSeek || input.Position != 0) throw ItemEffectsBridgePolicy.Invalid();
            await CpuAsync(() => ItemEffectsBridgePolicy.ValidateArtifact(frozen), token);
            await ItemEffectsBridgePolicy.ValidateProfileAsync(frozen.Binding, token, CpuAsync, definitions);
            byte[]? expected = null;
            await CpuAsync(() => expected = ItemEffectsBridgePolicy.MetadataBytes(frozen.Binding, frozen.RowCount), token);
            var header = new byte[64]; await ReadExactlyAsync(input, header, 0, token);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            if (!header.AsSpan(0, 8).SequenceEqual("VMIEFX01"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 1 ||
                count > ItemEffectsBridgePolicy.MaxRows || count != ItemEffectsBridgePolicy.Number(frozen.RowCount) ||
                metadataLength is < 1 or > ItemEffectsBridgePolicy.MaxMetadataBytes || metadataLength != expected!.Length ||
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 32 || BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)) != (ulong)count * 32 ||
                header.AsSpan(32).ContainsAnyExcept((byte)0) || checked(64L + metadataLength + (long)count * 32) != frozen.ByteLength || input.Length != frozen.ByteLength)
                throw ItemEffectsBridgePolicy.Invalid();
            var metadata = new byte[metadataLength]; await ReadExactlyAsync(input, metadata, 64, token);
            await CpuAsync(() => { if (!metadata.AsSpan().SequenceEqual(expected)) throw ItemEffectsBridgePolicy.Invalid(); }, token);
            var offset = 64L + metadataLength; var seen = new byte[(count + 7) / 8]; var buffer = new byte[ItemEffectsBridgePolicy.IoBytes];
            var previous = new byte[32]; var hasPrevious = false; var copyCount = 0;
            for (uint at = 0; at < count;)
            {
                var take = (int)Math.Min((uint)(buffer.Length / 32), count - at); var start = at;
                await ReadExactlyAsync(input, buffer.AsMemory(0, take * 32), offset + (long)at * 32, token);
                await CpuAsync(() =>
                {
                    for (var i = 0; i < take; i++)
                    {
                        token.ThrowIfCancellationRequested(); var span = buffer.AsSpan(i * 32, 32); var tuple = Read(span, start + (uint)i, (int)count);
                        var ordinal = (int)tuple.SourceOrdinal;
                        if (hasPrevious && Compare(previous, span) >= 0 || (seen[ordinal / 8] & (1 << (ordinal % 8))) != 0) throw ItemEffectsBridgePolicy.Invalid();
                        seen[ordinal / 8] |= (byte)(1 << (ordinal % 8)); span.CopyTo(previous); hasPrevious = true;
                        if (tuple.IsCopy) copyCount++;
                    }
                }, token);
                at += (uint)take;
            }
            if (copyCount != frozen.Binding.Rows.Completion.CopyRows) throw ItemEffectsBridgePolicy.Invalid();
            return new(this, frozen, input, offset, (int)count);
        }
        catch
        {
            if (input is not null)
                try { await input.DisposeAsync(); } catch (Exception) { throw ItemEffectsBridgePolicy.Cleanup(); }
            throw;
        }
    }
    private static async Task ReadExactlyAsync(FileStream input, Memory<byte> bytes, long offset, CancellationToken token)
    {
        var used = 0;
        while (used < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var take = Math.Min(ItemEffectsBridgePolicy.IoBytes, bytes.Length - used);
            var read = await RandomAccess.ReadAsync(input.SafeFileHandle, bytes.Slice(used, take), checked(offset + used), token);
            if (read == 0) throw ItemEffectsBridgePolicy.Invalid(); used += read;
            await Task.Yield();
        }
    }
    internal sealed class Session(ItemEffectsBridgeArtifacts owner, ItemEffectsBridgeArtifact artifact,
        FileStream input, long tuplesOffset, int count) : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly object disposalGate = new();
        private volatile bool closing;
        private Task? retirement;
        internal ItemEffectsBridgeArtifact Artifact { get; } = artifact;
        internal int LastLowerProbes { get; private set; }
        internal int LastUpperProbes { get; private set; }
        internal int LastTupleReads { get; private set; }
        internal async Task<ItemEffectsBridgeRange> FindRangeAsync(int parent, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try { ThrowIfClosed(); LastTupleReads = 0; return await FindRangeCoreAsync(parent, token); }
            finally { gate.Release(); }
        }
        private async Task<ItemEffectsBridgeRange> FindRangeCoreAsync(int parent, CancellationToken token)
        {
            LastLowerProbes = 0; LastUpperProbes = 0;
            async Task<uint> Bound(bool upper)
            {
                uint lo = 0, hi = (uint)count;
                while (lo < hi)
                {
                    token.ThrowIfCancellationRequested(); var mid = lo + (hi - lo) / 2;
                    var tuple = await ReadCoreAsync(mid, token); if (upper) LastUpperProbes++; else LastLowerProbes++;
                    if (tuple.ItemId < parent || upper && tuple.ItemId == parent) lo = mid + 1; else hi = mid;
                }
                return lo;
            }
            return new(await Bound(false), await Bound(true));
        }
        internal async Task<ItemEffectsBridgeTuple> ReadTupleAsync(uint indexOrdinal, int expectedParent, CancellationToken token)
        {
            await gate.WaitAsync(token);
            try
            {
                ThrowIfClosed(); LastTupleReads = 0; var tuple = await ReadCoreAsync(indexOrdinal, token);
                if (tuple.ItemId != expectedParent) throw ItemEffectsBridgePolicy.Invalid(); return tuple;
            }
            finally { gate.Release(); }
        }
        private async Task<ItemEffectsBridgeTuple> ReadCoreAsync(uint indexOrdinal, CancellationToken token)
        {
            if (indexOrdinal >= count) throw ItemEffectsBridgePolicy.Invalid();
            var bytes = new byte[32]; await ReadExactlyAsync(input, bytes, checked(tuplesOffset + (long)indexOrdinal * 32), token);
            ItemEffectsBridgeTuple tuple = default;
            await owner.CpuAsync(() => tuple = Read(bytes, indexOrdinal, count), token); LastTupleReads++; return tuple;
        }
        internal async Task<ItemEffectsPage> ReadPageAsync(ItemEffectsPageRequest request, TableDataSnapshot currentCanonicalBridgeSource, CancellationToken token)
        {
            // Public requests are already snapshotted by Application; defensively own scalar records here too.
            if (request is null || request.Selection is null || request.Selection.Context is null) throw ItemEffectsBridgePolicy.CursorInvalid();
            request = request with { Selection = request.Selection with { Context = request.Selection.Context with { } } };
            if (request.Selection.Context != Artifact.Binding.Context || request.BridgeIndexArtifactId != Artifact.ContentKey ||
                !ItemEffectsBridgePolicy.Hash(request.Selection.ItemsArtifactId) || request.Limit is < 1 or > 16 ||
                !int.TryParse(request.Selection.ItemId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parent) ||
                parent < 0 || ItemEffectsBridgePolicy.N(parent) != request.Selection.ItemId) throw ItemEffectsBridgePolicy.CursorInvalid();
            var source = JsonSerializer.Deserialize<TableDataSnapshot>(ItemEffectsBridgePolicy.Bytes(currentCanonicalBridgeSource), ItemEffectsBridgePolicy.Json) ?? throw ItemEffectsBridgePolicy.Invalid();
            if (!ItemEffectsBridgePolicy.SameCurrentSource(Artifact.Binding.Source, source)) throw ItemEffectsBridgePolicy.Invalid();
            await gate.WaitAsync(token);
            try
            {
                ThrowIfClosed(); LastTupleReads = 0; var range = await FindRangeCoreAsync(parent, token);
                var cursorBinding = CursorBinding(request); var begin = request.After is null ? range.Start : DecodeCursor(request.After, cursorBinding, range);
                var edges = new List<ItemEffectsEdge>(request.Limit); uint next = begin;
                while (next < range.End && edges.Count < request.Limit)
                {
                    var tuple = await ReadCoreAsync(next, token);
                    if (tuple.ItemId != parent) throw ItemEffectsBridgePolicy.Invalid();
                    var edge = new ItemEffectsEdge(new(ItemEffectsBridgePolicy.N(tuple.IndexOrdinal)),
                        new(ItemEffectsBridgePolicy.Table, source.ArtifactId, tuple.BridgeId.ToString(System.Globalization.CultureInfo.InvariantCulture), tuple.IsCopy,
                            tuple.CopySourceId?.ToString(System.Globalization.CultureInfo.InvariantCulture), tuple.SourceOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        new(ItemEffectsBridgePolicy.Table, 2, ItemEffectsBridgePolicy.N(tuple.ItemId)), new(ItemEffectsBridgePolicy.Table, 1, ItemEffectsBridgePolicy.N(tuple.ItemEffectId)));
                    edges.Add(edge); var after = next + 1;
                    var candidate = Page(after); var fits = false;
                    await owner.CpuAsync(() => fits = Fits(candidate, owner.maximumPageBytes), token);
                    if (!fits)
                    {
                        edges.RemoveAt(edges.Count - 1);
                        if (edges.Count == 0) throw ResultLimit(); break;
                    }
                    next = after;
                }
                var result = Page(next); var finalFits = false;
                await owner.CpuAsync(() => finalFits = Fits(result, owner.maximumPageBytes), token);
                if (!finalFits) throw ResultLimit(); return result;
                ItemEffectsPage Page(uint after) => new(request.Selection, Artifact.ContentKey, ItemEffectsBridgePolicy.OrderPolicy, source,
                    ItemEffectsBridgePolicy.N(range.Count), Array.AsReadOnly(edges.ToArray()), after < range.End ? EncodeCursor(cursorBinding, after) : null);
            }
            finally { gate.Release(); }
        }
        private void ThrowIfClosed() { ObjectDisposedException.ThrowIf(closing, this); }
        public ValueTask DisposeAsync()
        {
            lock (disposalGate) { closing = true; return new(retirement ??= RetireAsync()); }
        }
        private async Task RetireAsync()
        {
            await Task.Yield(); await gate.WaitAsync();
            try { try { await input.DisposeAsync(); } catch (Exception) { throw ItemEffectsBridgePolicy.Cleanup(); } }
            finally { gate.Release(); }
        }
    }
    private sealed record Cursor(string Binding, string After);
    private static string CursorBinding(ItemEffectsPageRequest request) => Convert.ToHexStringLower(SHA256.HashData(
        ItemEffectsBridgePolicy.Bytes(new { selection = request.Selection, bridgeIndexArtifactId = request.BridgeIndexArtifactId,
            revision = ItemEffectsBridgePolicy.Revision, orderPolicy = ItemEffectsBridgePolicy.OrderPolicy, limit = request.Limit })));
    private static string EncodeCursor(string binding, uint after) => Convert.ToBase64String(ItemEffectsBridgePolicy.Bytes(new Cursor(binding, ItemEffectsBridgePolicy.N(after))))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static uint DecodeCursor(string value, string binding, ItemEffectsBridgeRange range)
    {
        try
        {
            if (value.Length is < 1 or > 256 || value.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) throw ItemEffectsBridgePolicy.CursorInvalid();
            var padded = value.Replace('-', '+').Replace('_', '/'); padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            var cursor = JsonSerializer.Deserialize<Cursor>(bytes, ItemEffectsBridgePolicy.Json) ?? throw ItemEffectsBridgePolicy.CursorInvalid();
            if (cursor.Binding != binding || !uint.TryParse(cursor.After, out var after) || ItemEffectsBridgePolicy.N(after) != cursor.After ||
                after <= range.Start || after >= range.End || EncodeCursor(binding, after) != value) throw ItemEffectsBridgePolicy.CursorInvalid();
            return after;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { throw ItemEffectsBridgePolicy.CursorInvalid(); }
    }
    private static bool Fits(ItemEffectsPage page, int limit)
    {
        try { using var counter = new FrameCounter(limit); JsonSerializer.Serialize(counter, new { protocolVersion = 1, id = new string('\u0800', 128), result = page }, ItemEffectsBridgePolicy.Json); counter.WriteByte((byte)'\n'); return true; }
        catch (DiscoveryException error) when (error.Code == "ITEM_EFFECTS_RESULT_LIMIT") { return false; }
    }
    private static DiscoveryException ResultLimit() => new("ITEM_EFFECTS_RESULT_LIMIT", "The complete effects result exceeds its bounded response frame.");
    private sealed class FrameCounter(int limit) : Stream
    {
        private int count;
        public override void Write(byte[] buffer, int offset, int length) { if (length > limit - count) throw ResultLimit(); count += length; }
        public override void Write(ReadOnlySpan<byte> buffer) { if (buffer.Length > limit - count) throw ResultLimit(); count += buffer.Length; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => count; public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
