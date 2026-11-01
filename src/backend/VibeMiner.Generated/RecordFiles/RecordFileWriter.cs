using ZstdSharp;
using static VibeMiner.Generated.RecordFiles.RecordFileFormat;

namespace VibeMiner.Generated.RecordFiles;

/// <summary>
/// Writes one record file (format in <c>RecordFileFormat.cs</c>): records in the order added, then <see cref="CompleteAsync"/> writes
/// the id index and, last, the manifest. Memory is one open block, one segment buffer and the id list (4 B per record, 8 B while
/// sorting at the end; ids added out of order also keep a set to refuse duplicates). Not for concurrent use. A writer dropped
/// before <see cref="CompleteAsync"/> leaves only unreferenced segments, never a manifest. After a store failure or cancellation
/// inside a call the writer refuses further calls.
/// </summary>
public sealed class RecordFileWriter : IDisposable
{
    private const int Open = 0, Busy = 1, Done = 2;
    private readonly IGeneratedStore store;
    private readonly RecordFileOptions options;
    private readonly Compressor compressor;
    private readonly ByteSink block, segment;
    private byte[] frame = [];
    private readonly List<SegmentEntry> segments = [];
    private readonly List<BlockEntry> blocks = [];
    private readonly List<ChunkEntry> chunks = [];
    private uint[] ids = new uint[256];
    private long count, blockFirst, uncompressed, compressed, idBytes;
    private HashSet<uint>? seen;
    private int state, compressorDisposed;

    public RecordFileWriter(IGeneratedStore store, RecordFileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.options = options ?? new RecordFileOptions();
        this.options.Validate();
        compressor = NewCompressor(this.options.CompressionLevel);
        block = new ByteSink(Math.Min(this.options.BlockBytes, 1 << 16) + 32);
        segment = new ByteSink(Math.Min(this.options.SegmentBytes, 1 << 16));
    }

    /// <summary>Adds a record; its ordinal is the number added before it. A duplicate id throws <see cref="ArgumentException"/>.</summary>
    public async ValueTask AddAsync(uint id, ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= options.MaxRecords) throw Limit($"More than {options.MaxRecords} records.");
            if (record.Length > options.MaxRecordBytes) throw Limit($"A record is over {options.MaxRecordBytes} bytes.");
            if (IsDuplicate(id)) throw new ArgumentException($"Record id {id} was already added.", nameof(id));
        }
        catch { Leave(); throw; }
        try
        {
            if (block.Length > 0 && record.Length >= options.BlockBytes) await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            if (count == ids.Length) Array.Resize(ref ids, (int)Math.Min(ids.Length * 2L, options.MaxRecords));
            ids[count++] = id;
            seen?.Add(id);
            block.WriteVar(id);
            block.WriteVar((uint)record.Length);
            block.Write(record.Span);
            if (block.Length >= options.BlockBytes) await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            Leave();
        }
        catch { Fail(); throw; }
    }

    /// <summary>
    /// Writes the last block, the id index and the segments, then the manifest at <paramref name="manifestKey"/>: once that Put returns
    /// the file is readable. The writer is finished afterwards, whatever the outcome.
    /// </summary>
    public async Task<RecordFileInfo> CompleteAsync(GeneratedKey manifestKey, ReadOnlyMemory<byte> metadata, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata.Length > options.MaxMetadataBytes) throw Limit($"The metadata is over {options.MaxMetadataBytes} bytes.");
        }
        catch { Leave(); throw; }
        try
        {
            if (block.Length > 0) await CloseBlockAsync(cancellationToken).ConfigureAwait(false);
            await WriteIdIndexAsync(cancellationToken).ConfigureAwait(false);
            await FlushSegmentAsync(cancellationToken).ConfigureAwait(false);
            var manifest = EncodeManifest(new Manifest(count, segments, blocks, chunks, metadata), options.CompressionLevel);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await store.PutAsync([new GeneratedPut(manifestKey, manifest)], cancellationToken).ConfigureAwait(false);
            return new RecordFileInfo(manifestKey, count, uncompressed, compressed, idBytes, manifest.Length, blocks.Count, chunks.Count, segments.Count,
                ManifestWasPresent: outcome[0] == GeneratedPutOutcome.AlreadyPresent);
        }
        finally { Fail(); }
    }

    /// <summary>Finishes the writer. During a call the compressor is released when that call ends.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref state, Done) != Busy) DisposeCompressor();
    }

    /// <summary>Ends a call that leaves the writer usable, unless Dispose ran meanwhile.</summary>
    private void Leave()
    {
        if (Interlocked.CompareExchange(ref state, Open, Busy) != Busy) DisposeCompressor();
    }

    /// <summary>Ends a call that finishes the writer.</summary>
    private void Fail()
    {
        Interlocked.Exchange(ref state, Done);
        DisposeCompressor();
    }

    private void DisposeCompressor()
    {
        if (Interlocked.Exchange(ref compressorDisposed, 1) == 0) compressor.Dispose();
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref state, Busy, Open) != Open)
            throw new InvalidOperationException("The record file writer is in use, finished or failed.");
    }

    private bool IsDuplicate(uint id)
    {
        if (seen is null)
        {
            if (count == 0 || id > ids[count - 1]) return false;
            seen = new HashSet<uint>((int)count);
            for (var i = 0; i < count; i++) seen.Add(ids[i]);
        }
        return seen.Contains(id);
    }

    private async ValueTask CloseBlockAsync(CancellationToken cancellationToken)
    {
        var length = block.Length;
        var at = await AppendFrameAsync(block.Written, cancellationToken).ConfigureAwait(false);
        blocks.Add(new BlockEntry(at with { Length = (uint)length }, blockFirst));
        uncompressed += length;
        compressed += at.CompressedLength;
        blockFirst = count;
        block.Length = 0;
    }

    private async ValueTask WriteIdIndexAsync(CancellationToken cancellationToken)
    {
        if (count == 0) return;
        // (id << 32 | ordinal); ids added in order are already sorted with ordinal = index.
        var pairs = new ulong[count];
        for (var i = 0; i < count; i++) pairs[i] = (ulong)ids[i] << 32 | (uint)i;
        ids = [];
        seen = null;
        Array.Sort(pairs);
        var chunk = new ByteSink(IdChunkEntries * 4);
        for (var start = 0; start < pairs.Length; start += IdChunkEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Math.Min(IdChunkEntries, pairs.Length - start);
            var firstId = (uint)(pairs[start] >> 32);
            uint previousId = firstId;
            long previousOrdinal = 0;
            chunk.Length = 0;
            for (var i = start; i < start + n; i++)
            {
                var id = (uint)(pairs[i] >> 32);
                var ordinal = (long)(uint)pairs[i];
                chunk.WriteVar(id - previousId);
                chunk.WriteVar(ZigZag(ordinal - previousOrdinal));
                previousId = id; previousOrdinal = ordinal;
            }
            var length = chunk.Length;
            var at = await AppendFrameAsync(chunk.Written, cancellationToken).ConfigureAwait(false);
            chunks.Add(new ChunkEntry(at with { Length = (uint)length }, (uint)n, firstId));
            idBytes += at.CompressedLength;
        }
    }

    /// <summary>Compresses a frame into the open segment, flushing that segment first when the frame wouldn't fit.</summary>
    private ValueTask<FrameRef> AppendFrameAsync(ReadOnlySpan<byte> body, CancellationToken cancellationToken)
    {
        var bound = Compressor.GetCompressBound(body.Length);
        if (frame.Length < bound) frame = new byte[bound];
        var length = compressor.Wrap(body, frame);
        return PlaceFrameAsync(length, cancellationToken);
    }

    private async ValueTask<FrameRef> PlaceFrameAsync(int length, CancellationToken cancellationToken)
    {
        if (segment.Length > 0 && segment.Length + (long)length > options.SegmentBytes) await FlushSegmentAsync(cancellationToken).ConfigureAwait(false);
        var at = new FrameRef((uint)segments.Count, (uint)segment.Length, (uint)length, 0);
        segment.Write(frame.AsSpan(0, length));
        return at;
    }

    private async ValueTask FlushSegmentAsync(CancellationToken cancellationToken)
    {
        if (segment.Length == 0) return;
        var key = GeneratedKey.Hash(segment.Written);
        // Content-addressed: AlreadyPresent means the same bytes are already stored under this key.
        await store.PutAsync([new GeneratedPut(key, segment.Buffer.AsMemory(0, segment.Length))], cancellationToken).ConfigureAwait(false);
        segments.Add(new SegmentEntry(key, (uint)segment.Length));
        segment.Length = 0;
    }
}
