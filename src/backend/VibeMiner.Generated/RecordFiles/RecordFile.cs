using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ZstdSharp;
using static VibeMiner.Generated.RecordFiles.RecordFileFormat;

namespace VibeMiner.Generated.RecordFiles;

/// <summary>One record: its position in the file, its id and its bytes.</summary>
public readonly record struct Record(long Ordinal, uint Id, ReadOnlyMemory<byte> Bytes);

/// <summary>The outcome of <see cref="RecordFile.OpenAsync"/>. <see cref="File"/> is set only for Present.</summary>
public sealed record RecordFileOpen(GeneratedReadState State, RecordFile? File = null, string? Problem = null);

/// <summary>
/// A committed record file (format in <c>RecordFileFormat.cs</c>). The manifest's directories are held in memory; reads fetch exactly
/// the blocks or id chunk they need by range, into pooled buffers; callers get copies of only the records they asked for. Anything
/// that reads back out of shape throws <see cref="GeneratedStoreException"/> (Corrupt), never wrong bytes. Thread-safe.
/// </summary>
public sealed class RecordFile
{
    /// <summary>The most records one <see cref="ReadAsync"/> returns; use <see cref="ReadAllAsync"/> for more.</summary>
    public const int MaxPageRecords = 1 << 16;

    private readonly IGeneratedStore store;
    private readonly Manifest manifest;
    private readonly BlockEntry[] blocks;
    private readonly ChunkEntry[] chunks;

    private RecordFile(IGeneratedStore store, GeneratedKey manifestKey, Manifest manifest)
    {
        this.store = store;
        this.manifest = manifest;
        blocks = [.. manifest.Blocks];
        chunks = [.. manifest.Chunks];
        Keys = [manifestKey, .. manifest.Segments.Select(s => s.Key).Distinct()];
    }

    public long Count => manifest.Count;
    public ReadOnlyMemory<byte> Metadata => manifest.Metadata;
    /// <summary>
    /// The manifest and every segment. Segments are content-addressed, so another record file with the same bytes can share one:
    /// deleting these keys can break that file. Delete them only when this file is damaged (a shared segment then damages both
    /// anyway) or from a collector that checks which manifests still reference each segment.
    /// </summary>
    public IReadOnlyList<GeneratedKey> Keys { get; }

    internal IReadOnlyList<long> BlockFirstOrdinals => [.. blocks.Select(b => b.FirstOrdinal)];

    /// <summary>Reads the manifest. A damaged or malformed manifest is Corrupt; store failures throw.</summary>
    public static async Task<RecordFileOpen> OpenAsync(IGeneratedStore store, GeneratedKey manifestKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var read = await store.ReadAsync(manifestKey, cancellationToken).ConfigureAwait(false);
        if (read.State == GeneratedReadState.Absent) return new RecordFileOpen(GeneratedReadState.Absent);
        if (read.State != GeneratedReadState.Present || read.Bytes is null) return new RecordFileOpen(GeneratedReadState.Corrupt, Problem: read.Problem ?? "The manifest reads corrupt.");
        try
        {
            return new RecordFileOpen(GeneratedReadState.Present, new RecordFile(store, manifestKey, DecodeManifest(read.Bytes)));
        }
        catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt)
        {
            return new RecordFileOpen(GeneratedReadState.Corrupt, Problem: e.Message);
        }
    }

    /// <summary>Up to <paramref name="count"/> records from <paramref name="firstOrdinal"/>; fewer at the end of the file.</summary>
    public async Task<IReadOnlyList<Record>> ReadAsync(long firstOrdinal, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstOrdinal, Count);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxPageRecords);
        cancellationToken.ThrowIfCancellationRequested();
        var n = (int)Math.Min(count, Count - firstOrdinal);
        var result = new List<Record>(n);
        if (n == 0) return result;
        for (var b = BlockOf(firstOrdinal); result.Count < n; b++)
        {
            var skip = (int)(firstOrdinal + result.Count - blocks[b].FirstOrdinal);
            var take = (int)Math.Min(n - result.Count, RecordsIn(b) - skip);
            result.AddRange(await ReadBlockAsync(b, skip, take, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    /// <summary>The record with <paramref name="id"/>, or null when the file has none.</summary>
    public async Task<Record?> FindAsync(uint id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int lo = 0, hi = chunks.Length - 1, c = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (chunks[mid].FirstId <= id) { c = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (c < 0) return null;
        var (ordinal, found) = await FindOrdinalAsync(c, id, cancellationToken).ConfigureAwait(false);
        if (ordinal < 0 || found != id) return null;
        return await ReadIndexedAsync(ordinal, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The record with the smallest id at or after <paramref name="id"/>, or null when every id is smaller.</summary>
    public async Task<Record?> FindAtOrAfterAsync(uint id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Length == 0) return null;
        int lo = 0, hi = chunks.Length - 1, c = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (chunks[mid].FirstId <= id) { c = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var (ordinal, found) = await FindOrdinalAsync(c, id, cancellationToken).ConfigureAwait(false);
        if (ordinal < 0)
        {
            // Every id in chunk c is smaller: the answer is the next chunk's first entry.
            if (c + 1 == chunks.Length) return null;
            (ordinal, found) = await FindOrdinalAsync(c + 1, chunks[c + 1].FirstId, cancellationToken).ConfigureAwait(false);
            if (ordinal < 0) throw Corrupt("An id chunk doesn't start at its first id.");
        }
        return await ReadIndexedAsync(ordinal, found, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The record at <paramref name="ordinal"/>, which the id index says has <paramref name="id"/>.</summary>
    private async Task<Record> ReadIndexedAsync(long ordinal, uint id, CancellationToken cancellationToken)
    {
        var b = BlockOf(ordinal);
        var record = (await ReadBlockAsync(b, (int)(ordinal - blocks[b].FirstOrdinal), 1, cancellationToken).ConfigureAwait(false))[0];
        if (record.Id != id) throw Corrupt("The id index points at a record with another id.");
        return record;
    }

    /// <summary>Every record in ordinal order, one block at a time.</summary>
    public IAsyncEnumerable<Record> ReadAllAsync(CancellationToken cancellationToken = default) => ReadFromAsync(0, cancellationToken);

    /// <summary>
    /// Every record from <paramref name="firstOrdinal"/> to the end, in ordinal order. Blocks that sit back to back in one segment
    /// are fetched together, up to <see cref="RunBytes"/> of compressed bytes per range read; at most one run's records are held.
    /// </summary>
    public async IAsyncEnumerable<Record> ReadFromAsync(long firstOrdinal, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstOrdinal, Count);
        cancellationToken.ThrowIfCancellationRequested();
        if (firstOrdinal == Count) yield break;
        var b = BlockOf(firstOrdinal);
        var skip = (int)(firstOrdinal - blocks[b].FirstOrdinal);
        while (b < blocks.Length)
        {
            var end = RunEnd(b);
            var first = blocks[b].Frame;
            var last = blocks[end - 1].Frame;
            var length = (int)(last.Offset + last.CompressedLength - first.Offset);
            var compressed = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await ReadSegmentAsync(first.Segment, first.Offset, compressed.AsMemory(0, length), "A block", cancellationToken).ConfigureAwait(false);
                // One block decoded at a time, as the caller gets that far: a short walk doesn't pay for the whole run.
                for (var i = b; i < end; i++, skip = 0)
                    foreach (var record in DecodeBlock(i, compressed.AsSpan((int)(blocks[i].Frame.Offset - first.Offset), (int)blocks[i].Frame.CompressedLength), skip))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return record;
                    }
            }
            finally { ArrayPool<byte>.Shared.Return(compressed); }
            b = end;
        }
    }

    /// <summary>The most compressed bytes one run of <see cref="ReadFromAsync"/> fetches with a single range read, unless one block alone is larger.</summary>
    internal const int RunBytes = 256 << 10;

    /// <summary>The block after the last of the run starting at <paramref name="b"/>: same segment, back to back, within <see cref="RunBytes"/>.</summary>
    private int RunEnd(int b)
    {
        var first = blocks[b].Frame;
        long bytes = first.CompressedLength;
        var e = b + 1;
        for (; e < blocks.Length; e++)
        {
            var frame = blocks[e].Frame;
            var previous = blocks[e - 1].Frame;
            if (frame.Segment != first.Segment || frame.Offset != (long)previous.Offset + previous.CompressedLength || bytes + frame.CompressedLength > RunBytes) break;
            bytes += frame.CompressedLength;
        }
        return e;
    }

    /// <summary>Block <paramref name="b"/>'s records from <paramref name="skip"/>, from its compressed frame.</summary>
    private Record[] DecodeBlock(int b, ReadOnlySpan<byte> frame, int skip)
    {
        var length = (int)blocks[b].Frame.Length;
        var body = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            DecompressInto(frame, body.AsSpan(0, length), "A block");
            var n = RecordsIn(b);
            return ParseBlock(blocks[b], body.AsSpan(0, length), n, skip, (int)(n - skip));
        }
        finally { ArrayPool<byte>.Shared.Return(body); }
    }

    private int BlockOf(long ordinal)
    {
        int lo = 0, hi = blocks.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (blocks[mid].FirstOrdinal <= ordinal) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private long RecordsIn(int b) => (b + 1 < blocks.Length ? blocks[b + 1].FirstOrdinal : Count) - blocks[b].FirstOrdinal;

    /// <summary>
    /// Records [skip, skip + take) of block <paramref name="b"/>. The whole block is checked; only the asked-for records are built,
    /// over one fresh array holding just their stretch of the block.
    /// </summary>
    private async Task<Record[]> ReadBlockAsync(int b, int skip, int take, CancellationToken cancellationToken)
    {
        var entry = blocks[b];
        var length = (int)entry.Frame.Length;
        var body = await ReadAndDecompressAsync(entry.Frame, "A block", cancellationToken).ConfigureAwait(false);
        try { return ParseBlock(entry, body.AsSpan(0, length), RecordsIn(b), skip, take); }
        finally { ArrayPool<byte>.Shared.Return(body); }
    }

    private static Record[] ParseBlock(BlockEntry entry, ReadOnlySpan<byte> body, long n, int skip, int take)
    {
        if (n > body.Length / 2) throw Corrupt("A block holds fewer bytes than its records need.");
        var at = 0;
        int start = 0, end = 0;
        for (var i = 0; i < n; i++)
        {
            if (i == skip) start = at;
            ReadVar32(body, ref at, "A block");
            var recordLength = ReadVar32(body, ref at, "A block");
            if (recordLength > body.Length - at) throw Corrupt("A block's record runs past its end.");
            at += (int)recordLength;
            if (i == skip + take - 1) end = at;
        }
        if (at != body.Length) throw Corrupt("A block has bytes after its last record.");

        // The asked-for records sit back to back: copy that stretch out and point the records into the copy.
        var copy = body[start..end].ToArray();
        var records = new Record[take];
        at = 0;
        for (var i = 0; i < take; i++)
        {
            var id = ReadVar32(copy, ref at, "A block");
            var recordLength = (int)ReadVar32(copy, ref at, "A block");
            records[i] = new Record(entry.FirstOrdinal + skip + i, id, copy.AsMemory(at, recordLength));
            at += recordLength;
        }
        return records;
    }

    /// <summary>The first entry of id chunk <paramref name="c"/> with an id at or after <paramref name="id"/>; ordinal -1 when none.</summary>
    private async Task<(long Ordinal, uint Id)> FindOrdinalAsync(int c, uint id, CancellationToken cancellationToken)
    {
        var body = await ReadAndDecompressAsync(chunks[c].Frame, "An id chunk", cancellationToken).ConfigureAwait(false);
        try { return FindInChunk(body.AsSpan(0, (int)chunks[c].Frame.Length), c, id); }
        finally { ArrayPool<byte>.Shared.Return(body); }
    }

    /// <summary>A frame's decompressed bytes in a rented array (the first Frame.Length bytes); the caller returns it to the shared pool.</summary>
    private async Task<byte[]> ReadAndDecompressAsync(FrameRef frame, string what, CancellationToken cancellationToken)
    {
        var compressed = ArrayPool<byte>.Shared.Rent((int)frame.CompressedLength);
        try
        {
            await ReadFrameAsync(frame, compressed.AsMemory(0, (int)frame.CompressedLength), what, cancellationToken).ConfigureAwait(false);
            var body = ArrayPool<byte>.Shared.Rent((int)frame.Length);
            try
            {
                DecompressInto(compressed.AsSpan(0, (int)frame.CompressedLength), body.AsSpan(0, (int)frame.Length), what);
                return body;
            }
            catch { ArrayPool<byte>.Shared.Return(body); throw; }
        }
        finally { ArrayPool<byte>.Shared.Return(compressed); }
    }

    /// <summary>Like <c>RecordFileFormat.Decompress</c>, into <paramref name="output"/>, whose length the directory gives.</summary>
    private static void DecompressInto(ReadOnlySpan<byte> frame, Span<byte> output, string what)
    {
        var decompressor = Decompressors.Rent();
        int written;
        try
        {
            if (Decompressor.GetDecompressedSize(frame) != (ulong)output.Length) throw Corrupt($"{what} has a different length than its directory says.");
            written = decompressor.Unwrap(frame, output);
        }
        catch (GeneratedStoreException) { Decompressors.Return(decompressor); throw; }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A decoder that failed mid-frame isn't trusted again.
            decompressor.Dispose();
            throw Corrupt($"{what} doesn't decompress.", e);
        }
        Decompressors.Return(decompressor);
        if (written != output.Length) throw Corrupt($"{what} decompressed to {written} bytes, not {output.Length}.");
    }

    private (long Ordinal, uint Id) FindInChunk(ReadOnlySpan<byte> body, int c, uint id)
    {
        var chunk = chunks[c];
        var limit = c + 1 < chunks.Length ? chunks[c + 1].FirstId : (ulong)uint.MaxValue + 1;
        ulong current = chunk.FirstId;
        long ordinal = 0, found = -1;
        uint foundId = 0;
        var at = 0;
        for (var i = 0; i < chunk.Entries; i++)
        {
            var delta = ReadVar32(body, ref at, "An id chunk");
            if ((i == 0) != (delta == 0)) throw Corrupt("An id chunk isn't sorted by id.");
            current += delta;
            ordinal += UnZigZag(ReadVar(body, ref at, "An id chunk"));
            if (current >= limit) throw Corrupt("An id chunk overlaps the next one.");
            if (ordinal < 0 || ordinal >= Count) throw Corrupt("An id chunk points past the last record.");
            if (found < 0 && current >= id) { found = ordinal; foundId = (uint)current; }
        }
        if (at != body.Length) throw Corrupt("An id chunk has bytes after its last entry.");
        return (found, foundId);
    }

    private Task ReadFrameAsync(FrameRef frame, Memory<byte> bytes, string what, CancellationToken cancellationToken) =>
        ReadSegmentAsync(frame.Segment, frame.Offset, bytes, what, cancellationToken);

    private async Task ReadSegmentAsync(uint segmentIndex, uint offset, Memory<byte> bytes, string what, CancellationToken cancellationToken)
    {
        var segment = manifest.Segments[(int)segmentIndex];
        GeneratedRangeRead read;
        try
        {
            read = await store.ReadRangeAsync(segment.Key, offset, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException e) { throw Corrupt($"{what}'s segment is shorter than the manifest says.", e); }
        if (read.State == GeneratedReadState.Absent) throw Corrupt($"{what}'s segment is missing.");
        if (read.State != GeneratedReadState.Present) throw Corrupt($"{what}'s segment reads corrupt: {read.Problem}");
        if (read.Length != segment.Length) throw Corrupt($"{what}'s segment has a different length than the manifest says.");
    }
}

/// <summary>Idle zstd decoders shared by every record file: at most one per CPU kept, any more disposed on return.</summary>
internal static class Decompressors
{
    private static readonly ConcurrentBag<Decompressor> Idle = [];
    private static int idle;

    public static Decompressor Rent()
    {
        if (Idle.TryTake(out var decompressor)) { Interlocked.Decrement(ref idle); return decompressor; }
        return new Decompressor();
    }

    public static void Return(Decompressor decompressor)
    {
        if (Interlocked.Increment(ref idle) <= Environment.ProcessorCount) { Idle.Add(decompressor); return; }
        Interlocked.Decrement(ref idle);
        decompressor.Dispose();
    }
}
