using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace VibeMiner.Protocols.Tact;

public sealed record BlteReadableRange(long Offset, long ByteLength, string Sha256);
public sealed record BlteUnavailableRange(long Offset, long ByteLength, int ChunkOrdinal, string KeyName);
public sealed record BlteProjectionVerification(bool ContentKeyVerified,
    IReadOnlyList<BlteReadableRange> ReadableRanges, IReadOnlyList<BlteUnavailableRange> UnavailableRanges);

public static partial class BlteDecoder
{
    public const int MaxProjectionChunks = MaxChunks;
    public const int MaxProjectionRanges = 128;

    /// <summary>Authenticates complete encoded bytes and decodes available chunks. Holes are never plaintext.
    /// Output is caller-owned, seekable, initially empty and provisional until successful completion.</summary>
    public static Task<BlteProjectionVerification> DecodeAvailableAsync(Stream encoded, Stream decoded,
        string encodingKey, string contentKey, long encodedLength, long decodedLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (!decoded.CanSeek || !decoded.CanWrite || decoded.Length != 0 || decoded.Position != 0)
            throw new ArgumentException("Projection output must be writable, seekable and initially empty.", nameof(decoded));
        return DecodeProjectionCoreAsync(encoded, decoded, encodingKey, contentKey, encodedLength, decodedLength, cancellationToken);
    }

    /// <summary>Replays identical authentication/decode/hash checks without retaining decoded plaintext.</summary>
    public static Task<BlteProjectionVerification> VerifyAvailableAsync(Stream encoded,
        string encodingKey, string contentKey, long encodedLength, long decodedLength,
        CancellationToken cancellationToken = default) =>
        DecodeProjectionCoreAsync(encoded, null, encodingKey, contentKey, encodedLength, decodedLength, cancellationToken);

    private static async Task<BlteProjectionVerification> DecodeProjectionCoreAsync(Stream encoded, Stream? decoded,
        string encodingKey, string contentKey, long encodedLength, long decodedLength, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(encoded); token.ThrowIfCancellationRequested();
        var expectedEncoding = ParseHash(encodingKey); var expectedContent = ParseHash(contentKey);
        if (encodedLength > MaxEncodedBytes || decodedLength > MaxDecodedBytes)
            throw Error("BLTE_LIMIT", "BLTE projection exceeds object byte limits.");
        if (encodedLength < 9 || decodedLength < 0)
            throw Error("BLTE_INVALID", "BLTE projection declared lengths are invalid.");
        using var encodingHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var prefix = new byte[8]; await ReadExactlyAsync(encoded, prefix, token);
        if (!prefix.AsSpan(0, 4).SequenceEqual("BLTE"u8)) throw Error("BLTE_INVALID", "BLTE magic is missing.");
        encodingHash.AppendData(prefix);
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        var chunks = new List<Chunk>();
        if (headerLength == 0) chunks.Add(new(encodedLength - 8, decodedLength, null));
        else
        {
            if (headerLength > 12L + MaxProjectionChunks * 24L)
                throw Error("BLTE_LIMIT", "BLTE projection exceeds its chunk-header bound.");
            if (headerLength < 12 || headerLength > encodedLength)
                throw Error("BLTE_INVALID", "BLTE header length is invalid.");
            var table = new byte[(int)headerLength - 8]; await ReadExactlyAsync(encoded, table, token);
            encodingHash.AppendData(table);
            if (table[0] != 15) throw Error("BLTE_UNSUPPORTED", "BLTE chunk table format is unsupported.");
            var count = (table[1] << 16) | (table[2] << 8) | table[3];
            if (count > MaxProjectionChunks) throw Error("BLTE_LIMIT", "BLTE projection exceeds its chunk count bound.");
            if (count == 0 || headerLength != 12L + 24L * count)
                throw Error("BLTE_INVALID", "BLTE chunk table size is invalid.");
            long inputTotal = headerLength, outputTotal = 0;
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested(); var at = 4 + index * 24;
                var input = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at, 4));
                var output = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at + 4, 4));
                CheckProjectionChunk(input, output); inputTotal += input; outputTotal += output;
                if (inputTotal > MaxEncodedBytes || outputTotal > MaxDecodedBytes)
                    throw Error("BLTE_LIMIT", "BLTE chunk totals exceed object bounds.");
                chunks.Add(new(input, output, table.AsSpan(at + 8, 16).ToArray()));
            }
            if (inputTotal != encodedLength || outputTotal != decodedLength)
                throw Error("BLTE_INVALID", "BLTE chunk totals differ from declared object lengths.");
            Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
        }
        var readable = new List<BlteReadableRange>(); var unavailable = new List<BlteUnavailableRange>();
        // Existing small-container receipts retain one range per clear chunk. Larger
        // containers use maximal adjacent clear spans; both algorithms replay identically.
        var coalesce = chunks.Count > MaxProjectionRanges;
        using var spanHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long spanOffset = 0, spanLength = 0; var spanStarted = false;
        void CheckRangeCapacity()
        {
            if (readable.Count + unavailable.Count >= MaxProjectionRanges)
                throw Error("BLTE_LIMIT", "BLTE projection exceeds its readable/unavailable range bound.");
        }
        void FinishReadableSpan()
        {
            if (!spanStarted) return;
            var digest = spanHash.GetHashAndReset();
            if (spanLength > 0)
            { CheckRangeCapacity(); readable.Add(new(spanOffset, spanLength, Convert.ToHexStringLower(digest))); }
            spanStarted = false; spanLength = 0;
        }
        var buffer = new byte[BufferBytes]; long offset = 0;
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            token.ThrowIfCancellationRequested(); var chunk = chunks[ordinal]; CheckProjectionChunk(chunk.Encoded, chunk.Decoded);
            var bytes = new byte[(int)chunk.Encoded];
            using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            for (var at = 0; at < bytes.Length;)
            {
                var count = Math.Min(BufferBytes, bytes.Length - at);
                await ReadExactlyAsync(encoded, bytes.AsMemory(at, count), token); chunkHash.AppendData(bytes, at, count);
                if (headerLength == 0) encodingHash.AppendData(bytes, at, count); at += count;
            }
            if (chunk.Hash is not null) Verify(chunkHash.GetHashAndReset(), chunk.Hash, "chunk checksum");
            token.ThrowIfCancellationRequested(); var mode = bytes[0];
            if (mode == 'E')
            {
                if (headerLength == 0) throw Error("BLTE_UNSUPPORTED", "Encrypted zero-header BLTE cannot establish a partial header proof.");
                if (chunk.Decoded == 0) throw Error("BLTE_INVALID", "An unavailable encrypted chunk must declare a positive plaintext span.");
                var keyName = ReadUnavailableKey(bytes); FinishReadableSpan(); CheckRangeCapacity();
                unavailable.Add(new(offset, chunk.Decoded, ordinal, keyName));
            }
            else
            {
                if (mode is not ((byte)'N') and not ((byte)'Z')) throw Error("BLTE_UNSUPPORTED", "BLTE chunk compression mode is unsupported.");
                if (decoded is not null) decoded.Position = offset;
                if (!spanStarted) { spanOffset = offset; spanStarted = true; }
                using var target = new ProjectionHashOutput(decoded ?? Stream.Null, spanHash);
                if (mode == 'N')
                {
                    if (bytes.Length - 1 != chunk.Decoded) throw Error("BLTE_INVALID", "BLTE raw chunk length is invalid.");
                    for (var at = 1; at < bytes.Length;)
                    {
                        token.ThrowIfCancellationRequested(); var count = Math.Min(BufferBytes, bytes.Length - at);
                        contentHash.AppendData(bytes, at, count); await target.WriteAsync(bytes.AsMemory(at, count), token); at += count;
                    }
                }
                else await InflateAsync(bytes[1..], target, contentHash, chunk.Decoded, buffer, token);
                spanLength += chunk.Decoded;
                if (!coalesce) FinishReadableSpan();
            }
            offset += chunk.Decoded;
        }
        FinishReadableSpan();
        if (await encoded.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
            throw Error("BLTE_INVALID", "BLTE has trailing encoded bytes.");
        token.ThrowIfCancellationRequested();
        if (headerLength == 0) Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
        if (unavailable.Count == 0) Verify(contentHash.GetHashAndReset(), expectedContent, "content key");
        if (decoded is not null) { decoded.SetLength(decodedLength); decoded.Position = decodedLength; }
        return new(unavailable.Count == 0, readable.AsReadOnly(), unavailable.AsReadOnly());
    }

    private static void CheckProjectionChunk(long encoded, long decoded)
    {
        if (encoded < 1) throw Error("BLTE_INVALID", "BLTE chunk lacks a mode byte.");
        if (encoded > MaxCompressedChunkBytes || decoded > MaxCompressedChunkBytes)
            throw Error("BLTE_LIMIT", "BLTE projection chunks exceed their sixteen-MiB bound.");
    }

    private static string ReadUnavailableKey(byte[] bytes)
    {
        if (bytes.Length < 2) throw Error("BLTE_INVALID", "BLTE encrypted envelope is truncated.");
        if (bytes[1] != 8) throw Error("BLTE_UNSUPPORTED", "BLTE encrypted key-name width is unsupported.");
        if (bytes.Length < 11) throw Error("BLTE_INVALID", "BLTE encrypted key name is truncated.");
        if (bytes[10] != 4) throw Error("BLTE_UNSUPPORTED", "BLTE encrypted IV width is unsupported.");
        if (bytes.Length < 17) throw Error("BLTE_INVALID", "BLTE encrypted IV, cipher or ciphertext is truncated.");
        if (bytes[15] != 'S') throw Error("BLTE_UNSUPPORTED", "BLTE encrypted cipher is unsupported.");
        // Key identifiers are numeric little-endian UInt64 values, matching section keys.
        // Complete original envelope/ciphertext bytes remain authenticated by the chunk MD5.
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(2, 8)).ToString("X16", CultureInfo.InvariantCulture);
    }

    private sealed class ProjectionHashOutput(Stream target, IncrementalHash sha) : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); sha.AppendData(buffer.Span); return target.WriteAsync(buffer, cancellationToken); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
