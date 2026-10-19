using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

public sealed record BlteVerification(string ContentKey, string EncodingKey, long EncodedLength,
    long DecodedLength, int ChunkCount);

/// <summary>
/// Original bounded N/raw and Z/zlib BLTE reader. Streams remain caller-owned. Output is provisional
/// until this method succeeds; callers must not publish it before every integrity check completes.
/// </summary>
public static partial class BlteDecoder
{
    public const long MaxEncodedBytes = 512L * 1024 * 1024;
    public const long MaxDecodedBytes = 1024L * 1024 * 1024;
    public const int MaxChunks = 65_536;
    public const int MaxCompressedChunkBytes = 16 * 1024 * 1024;
    private const int BufferBytes = 65_536;
    private sealed record Chunk(long Encoded, long Decoded, byte[]? Hash);

    public static async Task<BlteVerification> DecodeAsync(Stream encoded, Stream decoded,
        string encodingKey, string contentKey, long encodedLength, long decodedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(decoded);
        cancellationToken.ThrowIfCancellationRequested();
        var expectedEncoding = ParseHash(encodingKey);
        var expectedContent = ParseHash(contentKey);
        if (encodedLength > MaxEncodedBytes || decodedLength > MaxDecodedBytes)
            throw Error("BLTE_LIMIT", "BLTE exceeds the object byte limit.");
        if (encodedLength < 9 || decodedLength < 0)
            throw Error("BLTE_INVALID", "BLTE declared lengths are invalid.");
        using var encodingHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var prefix = new byte[8];
        await ReadExactlyAsync(encoded, prefix, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual("BLTE"u8)) throw Error("BLTE_INVALID", "BLTE magic is missing.");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        encodingHash.AppendData(prefix);
        var chunks = new List<Chunk>();
        if (headerLength == 0) chunks.Add(new(encodedLength - 8, decodedLength, null));
        else
        {
            if (headerLength > 12L + 24L * MaxChunks)
                throw Error("BLTE_LIMIT", "BLTE header exceeds the chunk limit.");
            if (headerLength < 12 || headerLength > encodedLength)
                throw Error("BLTE_INVALID", "BLTE header length is invalid.");
            var table = new byte[(int)headerLength - 8];
            await ReadExactlyAsync(encoded, table, cancellationToken);
            encodingHash.AppendData(table);
            if (table[0] != 15) throw Error("BLTE_UNSUPPORTED", "BLTE chunk table format is unsupported.");
            var count = (table[1] << 16) | (table[2] << 8) | table[3];
            if (count > MaxChunks) throw Error("BLTE_LIMIT", "BLTE exceeds the chunk count limit.");
            if (count == 0 || headerLength != 12L + 24L * count)
                throw Error("BLTE_INVALID", "BLTE chunk table size is invalid.");
            long encodedTotal = headerLength, decodedTotal = 0;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 4 + i * 24;
                var input = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset, 4));
                var output = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset + 4, 4));
                if (input == 0) throw Error("BLTE_INVALID", "BLTE chunk lacks a mode byte.");
                encodedTotal += input;
                decodedTotal += output;
                if (encodedTotal > MaxEncodedBytes || decodedTotal > MaxDecodedBytes)
                    throw Error("BLTE_LIMIT", "BLTE chunk totals exceed the object limits.");
                chunks.Add(new(input, output, table.AsSpan(offset + 8, 16).ToArray()));
            }
            if (encodedTotal != encodedLength || decodedTotal != decodedLength)
                throw Error("BLTE_INVALID", "BLTE chunk totals differ from the declared object lengths.");
            Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
        }

        var buffer = new byte[BufferBytes];
        var modeBuffer = new byte[1];
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await ReadExactlyAsync(encoded, modeBuffer, cancellationToken);
            chunkHash.AppendData(modeBuffer);
            if (headerLength == 0) encodingHash.AppendData(modeBuffer);
            var mode = modeBuffer[0];
            if (mode == 'E') throw Error("BLTE_ENCRYPTED", "BLTE encrypted chunks are not supported.");
            if (mode is not ((byte)'N') and not ((byte)'Z'))
                throw Error("BLTE_UNSUPPORTED", "BLTE chunk compression mode is unsupported.");
            var remaining = chunk.Encoded - 1;
            if (mode == 'N')
            {
                if (remaining != chunk.Decoded) throw Error("BLTE_INVALID", "BLTE raw chunk length is invalid.");
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, remaining);
                    await ReadExactlyAsync(encoded, buffer.AsMemory(0, count), cancellationToken);
                    chunkHash.AppendData(buffer, 0, count);
                    if (headerLength == 0) encodingHash.AppendData(buffer, 0, count);
                    contentHash.AppendData(buffer, 0, count);
                    await decoded.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                }
            }
            else
            {
                if (remaining > MaxCompressedChunkBytes)
                    throw Error("BLTE_LIMIT", "BLTE compressed chunk exceeds the working-memory limit.");
                var compressed = new byte[(int)remaining];
                // Bound each read/hash slice so transport and CPU cancellation stay observable.
                for (var offset = 0; offset < compressed.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, compressed.Length - offset);
                    await ReadExactlyAsync(encoded, compressed.AsMemory(offset, count), cancellationToken);
                    chunkHash.AppendData(compressed, offset, count);
                    if (headerLength == 0) encodingHash.AppendData(compressed, offset, count);
                    offset += count;
                }
                await InflateAsync(compressed, decoded, contentHash, chunk.Decoded, buffer, cancellationToken);
            }
            if (chunk.Hash is not null) Verify(chunkHash.GetHashAndReset(), chunk.Hash, "chunk checksum");
        }
        if (await encoded.ReadAsync(modeBuffer, cancellationToken) != 0)
            throw Error("BLTE_INVALID", "BLTE has trailing encoded bytes.");
        cancellationToken.ThrowIfCancellationRequested();
        if (headerLength == 0) Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
        Verify(contentHash.GetHashAndReset(), expectedContent, "content key");
        return new(contentKey.ToLowerInvariant(), encodingKey.ToLowerInvariant(), encodedLength, decodedLength, chunks.Count);
    }

    private static async Task InflateAsync(byte[] compressed, Stream target, IncrementalHash contentHash,
        long expected, byte[] buffer, CancellationToken cancellationToken)
    {
        using var input = new ExactInflaterInput(compressed, cancellationToken);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        long produced = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacity = (int)Math.Min(buffer.Length, expected - produced + 1);
                // Compression over bounded memory is CPU work. Only caller-stream I/O is asynchronous.
                var count = inflater.Read(buffer, 0, capacity);
                if (count == 0) break;
                if (count > expected - produced) throw Error("BLTE_INVALID", "BLTE zlib output exceeds the declared length.");
                contentHash.AppendData(buffer, 0, count);
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                produced += count;
            }
        }
        catch (InvalidDataException) { throw Error("BLTE_INVALID", "BLTE zlib data is invalid."); }
        if (produced != expected || input.ReadPastEnd || input.Position != compressed.Length)
            throw Error("BLTE_INVALID", "BLTE zlib is truncated or has trailing bytes or an incorrect decoded length.");
    }

    // The framework inflater reads ahead and otherwise accepts a truncated member at physical EOF.
    // Fence off the final byte: a valid member must request it to finish its checksum. A member with
    // trailing data finishes before requesting that byte, even if earlier reads consumed other tail
    // bytes. Physical EOF requests separately identify incomplete members. Reads are memory blocks.
    private sealed class ExactInflaterInput(byte[] bytes, CancellationToken cancellationToken) : Stream
    {
        private int offset;
        public bool ReadPastEnd { get; private set; }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            if (offset == bytes.Length) { ReadPastEnd = true; return 0; }
            var available = offset == bytes.Length - 1 ? 1 : bytes.Length - offset - 1;
            var count = Math.Min(buffer.Length, available);
            bytes.AsSpan(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await source.ReadExactlyAsync(buffer, cancellationToken); }
        catch (EndOfStreamException) { throw Error("BLTE_INVALID", "BLTE is truncated."); }
    }

    private static byte[] ParseHash(string hash)
    {
        if (hash is null || hash.Length != 32 || hash.Any(ch => !char.IsAsciiHexDigit(ch)))
            throw Error("BLTE_INVALID", "BLTE verification requires full MD5 keys.");
        return Convert.FromHexString(hash);
    }
    private static void Verify(byte[] actual, byte[] expected, string role)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw Error("BLTE_INTEGRITY", $"BLTE {role} does not match.");
    }
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
