using System.Buffers.Binary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace VibeMiner.Generated.RecordFiles;

// Record file format 1 (#179). All integers little-endian; "varint" is unsigned LEB128 (at most 5 bytes for 32 bits, 10 for 64);
// "zigzag" maps signed to unsigned before the varint. Every zstd frame is one-shot with its content size and content checksum.
//
// Manifest object, at the caller's key, written last (its Put is the commit point):
//   0  4  magic "VMRF"
//   4  2  format version, 1
//   6  2  flags, 0
//   8  8  record count
//  16  4  segment count S
//  20  4  block count B
//  24  4  id chunk count C
//  28  4  metadata length M
//  32  4  body length, exactly S*20 + B*28 + C*24 + M
//  36  4  compressed body length, exactly the rest of the object
//  40  .  body as one zstd frame:
//         S x segment  (16 key, u32 length)
//         B x block    (u32 segment, u32 offset, u32 compressed length, u32 length, u64 first ordinal)
//         C x id chunk (u32 segment, u32 offset, u32 compressed length, u32 length, u32 entries, u32 first id)
//         M metadata bytes
// Segment object: frames (blocks, then id chunks) back to back, at most SegmentBytes unless it holds a single frame, keyed by
//   GeneratedKey.Hash(its own bytes).
// Block (one zstd frame): per record in ordinal order, varint id, varint length, the bytes. Closed once it reaches BlockBytes.
// Id chunk (one zstd frame): up to 1024 (id, ordinal) entries sorted by id; per entry varint (id - previous id, 0 for the first,
//   whose id is in the directory), zigzag varint (ordinal - previous ordinal, previous starts at 0).
internal static class RecordFileFormat
{
    public const uint Magic = 0x46524D56; // "VMRF" read as a little-endian u32
    public const ushort Version = 1;
    public const int HeaderBytes = 40;
    public const int SegmentEntryBytes = GeneratedKey.Bytes + 4;
    public const int BlockEntryBytes = 28;
    public const int ChunkEntryBytes = 24;
    public const int IdChunkEntries = 1024;
    public const int MaxManifestBytes = GeneratedLimits.MaxObjectBytes;
    public const int MaxFrameBytes = GeneratedLimits.MaxObjectBytes;

    public static GeneratedStoreException Corrupt(string problem, Exception? inner = null) =>
        new(GeneratedStoreCodes.Corrupt, $"Record file: {problem}", inner);

    public static GeneratedStoreException Limit(string problem) => new(GeneratedStoreCodes.Limit, $"Record file: {problem}");

    public static Compressor NewCompressor(int level)
    {
        var compressor = new Compressor(level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return compressor;
    }

    /// <summary>Decompresses one frame whose length the directory gives; any mismatch or decoder failure is Corrupt.</summary>
    public static byte[] Decompress(Decompressor decompressor, ReadOnlySpan<byte> frame, int length, string what)
    {
        byte[] output;
        int written;
        try
        {
            if (Decompressor.GetDecompressedSize(frame) != (ulong)length) throw Corrupt($"{what} has a different length than its directory says.");
            output = new byte[length];
            written = decompressor.Unwrap(frame, output);
        }
        catch (GeneratedStoreException) { throw; }
        catch (Exception e) when (e is not OperationCanceledException) { throw Corrupt($"{what} doesn't decompress.", e); }
        if (written != length) throw Corrupt($"{what} decompressed to {written} bytes, not {length}.");
        return output;
    }

    public static ulong ReadVar(ReadOnlySpan<byte> source, ref int position, string what)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= source.Length) throw Corrupt($"{what} ends inside a number.");
            var b = source[position++];
            if (shift == 63 && b > 1) throw Corrupt($"{what} has an oversized number.");
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw Corrupt($"{what} has an oversized number.");
    }

    public static uint ReadVar32(ReadOnlySpan<byte> source, ref int position, string what)
    {
        var value = ReadVar(source, ref position, what);
        if (value > uint.MaxValue) throw Corrupt($"{what} has an oversized number.");
        return (uint)value;
    }

    public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));
    public static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    public static byte[] EncodeManifest(Manifest manifest, int level)
    {
        long bodyLength = BodyLength(manifest.Segments.Count, manifest.Blocks.Count, manifest.Chunks.Count, manifest.Metadata.Length);
        if (bodyLength > MaxManifestBytes) throw Limit("The manifest's directories and metadata are over 64 MiB.");
        var body = new byte[bodyLength];
        var span = body.AsSpan();
        var at = 0;
        foreach (var s in manifest.Segments)
        {
            s.Key.WriteTo(span[at..]); BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], s.Length); at += SegmentEntryBytes;
        }
        foreach (var b in manifest.Blocks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], b.Frame.Segment);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], b.Frame.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], b.Frame.CompressedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 12)..], b.Frame.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(span[(at + 16)..], (ulong)b.FirstOrdinal);
            at += BlockEntryBytes;
        }
        foreach (var c in manifest.Chunks)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], c.Frame.Segment);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 4)..], c.Frame.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 8)..], c.Frame.CompressedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 12)..], c.Frame.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], c.Entries);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 20)..], c.FirstId);
            at += ChunkEntryBytes;
        }
        manifest.Metadata.Span.CopyTo(span[at..]);

        using var compressor = NewCompressor(level);
        var compressed = compressor.Wrap(body);
        if (HeaderBytes + (long)compressed.Length > MaxManifestBytes) throw Limit("The manifest is over 64 MiB.");
        var output = new byte[HeaderBytes + compressed.Length];
        var h = output.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(h[8..], manifest.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[16..], manifest.Segments.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], manifest.Blocks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[24..], manifest.Chunks.Count);
        BinaryPrimitives.WriteInt32LittleEndian(h[28..], manifest.Metadata.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[32..], body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h[36..], compressed.Length);
        compressed.CopyTo(h[HeaderBytes..]);
        return output;
    }

    /// <summary>Parses and checks a whole manifest. Anything out of shape throws Corrupt.</summary>
    public static Manifest DecodeManifest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes) throw Corrupt("The manifest is shorter than its header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) throw Corrupt("The manifest doesn't start with the record file magic.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version) throw Corrupt("The manifest has an unknown format version.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != 0) throw Corrupt("The manifest has unknown flags.");
        var count = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var segmentCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var blockCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]);
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]);
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        if (count < 0 || count > uint.MaxValue || segmentCount < 0 || blockCount < 0 || chunkCount < 0 || metadataLength < 0)
            throw Corrupt("The manifest has a negative or oversized count.");
        if (blockCount > count || chunkCount > count || (count > 0 && (blockCount == 0 || chunkCount == 0)) || segmentCount > (long)blockCount + chunkCount)
            throw Corrupt("The manifest's directory counts don't fit its record count.");
        if (bodyLength != BodyLength(segmentCount, blockCount, chunkCount, metadataLength) || bodyLength > MaxManifestBytes)
            throw Corrupt("The manifest's body length doesn't match its counts.");
        if (compressedLength != bytes.Length - HeaderBytes) throw Corrupt("The manifest's length doesn't match its header.");

        using var decompressor = new Decompressor();
        var body = Decompress(decompressor, bytes[HeaderBytes..], bodyLength, "The manifest body");
        var span = body.AsSpan();
        var at = 0;
        var segments = new SegmentEntry[segmentCount];
        for (var i = 0; i < segmentCount; i++, at += SegmentEntryBytes)
        {
            segments[i] = new(new GeneratedKey(span.Slice(at, GeneratedKey.Bytes)), BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 16)..]));
            if (segments[i].Length is 0 or > GeneratedLimits.MaxObjectBytes) throw Corrupt("A segment has an impossible length.");
        }
        var blocks = new BlockEntry[blockCount];
        for (var i = 0; i < blockCount; i++, at += BlockEntryBytes)
        {
            var frame = ReadFrame(span[at..], segments, "A block");
            var first = BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 16)..]);
            if (i == 0 ? first != 0 : first <= (ulong)blocks[i - 1].FirstOrdinal)
                throw Corrupt("The block directory's first ordinals aren't increasing from 0.");
            if (first >= (ulong)count) throw Corrupt("A block starts past the last record.");
            blocks[i] = new(frame, (long)first);
        }
        var chunks = new ChunkEntry[chunkCount];
        long entries = 0;
        for (var i = 0; i < chunkCount; i++, at += ChunkEntryBytes)
        {
            var frame = ReadFrame(span[at..], segments, "An id chunk");
            var n = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 16)..]);
            var firstId = BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 20)..]);
            if (n is 0 or > IdChunkEntries) throw Corrupt("An id chunk has an impossible entry count.");
            if (i > 0 && firstId <= chunks[i - 1].FirstId) throw Corrupt("The id chunk directory isn't sorted by id.");
            if (frame.Length > n * 15L) throw Corrupt("An id chunk is longer than its entries can be.");
            entries += n;
            chunks[i] = new(frame, n, firstId);
        }
        if (entries != count) throw Corrupt("The id index doesn't hold one entry per record.");
        var metadata = body.AsMemory(at, metadataLength);
        return new Manifest(count, segments, blocks, chunks, metadata);
    }

    private static FrameRef ReadFrame(ReadOnlySpan<byte> entry, SegmentEntry[] segments, string what)
    {
        var frame = new FrameRef(BinaryPrimitives.ReadUInt32LittleEndian(entry), BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]));
        if (frame.Segment >= segments.Length) throw Corrupt($"{what} names a segment that isn't listed.");
        if (frame.CompressedLength == 0 || (long)frame.Offset + frame.CompressedLength > segments[frame.Segment].Length)
            throw Corrupt($"{what} lies outside its segment.");
        if (frame.Length is 0 or > MaxFrameBytes) throw Corrupt($"{what} has an impossible length.");
        return frame;
    }

    private static long BodyLength(long segments, long blocks, long chunks, long metadata) =>
        segments * SegmentEntryBytes + blocks * BlockEntryBytes + chunks * ChunkEntryBytes + metadata;
}

internal readonly record struct SegmentEntry(GeneratedKey Key, uint Length);
internal readonly record struct FrameRef(uint Segment, uint Offset, uint CompressedLength, uint Length);
internal readonly record struct BlockEntry(FrameRef Frame, long FirstOrdinal);
internal readonly record struct ChunkEntry(FrameRef Frame, uint Entries, uint FirstId);

internal sealed record Manifest(long Count, IReadOnlyList<SegmentEntry> Segments, IReadOnlyList<BlockEntry> Blocks, IReadOnlyList<ChunkEntry> Chunks, ReadOnlyMemory<byte> Metadata);

/// <summary>A growable byte buffer for blocks, chunks and segments.</summary>
internal sealed class ByteSink(int capacity)
{
    public byte[] Buffer { get; private set; } = new byte[Math.Max(capacity, 16)];
    public int Length { get; set; }

    public ReadOnlySpan<byte> Written => Buffer.AsSpan(0, Length);

    public void Ensure(int more)
    {
        if (Length + (long)more <= Buffer.Length) return;
        var size = Math.Max(Length + (long)more, Math.Min(Buffer.Length * 2L, Array.MaxLength));
        var next = new byte[size];
        Buffer.AsSpan(0, Length).CopyTo(next);
        Buffer = next;
    }

    public void WriteVar(ulong value)
    {
        Ensure(10);
        while (value >= 0x80) { Buffer[Length++] = (byte)(value | 0x80); value >>= 7; }
        Buffer[Length++] = (byte)value;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(Buffer.AsSpan(Length));
        Length += bytes.Length;
    }
}
