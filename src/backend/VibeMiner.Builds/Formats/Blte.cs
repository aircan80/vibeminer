using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>What a successful whole-object decode proved: the encoded and decoded lengths and the chunk count.</summary>
internal readonly record struct BlteDecoded(long EncodedLength, long DecodedLength, int ChunkCount);

/// <summary>
/// Whole-object BLTE (headerless or chunked; modes <c>N</c> and <c>Z</c>), a port of the engine's <c>BlteDecoder</c>.
/// The EKey is always checked (whole object when headerless, the header when chunked, plus every chunk MD5); the CKey and
/// decoded length only when given. An <c>E</c> chunk anywhere is Encrypted <c>BLTE_ENCRYPTED</c>, and <c>F</c> or an
/// unknown mode is Unsupported <c>BLTE_UNSUPPORTED</c>, each claimed only after that chunk's bytes pass their hash. Bad bytes
/// are Corrupt: <c>BLTE_INVALID</c>, <c>BLTE_INTEGRITY</c>, <c>BLTE_LIMIT</c>. Output is provisional until this returns.
/// </summary>
internal static class Blte
{
    public const long MaxEncodedBytes = 512L * 1024 * 1024;
    public const long MaxDecodedBytes = 1024L * 1024 * 1024;
    public const int MaxChunks = 65_536;
    public const int MaxCompressedChunkBytes = 16 * 1024 * 1024;
    private const int BufferBytes = 65_536;
    private sealed record Chunk(long Encoded, long? Decoded, byte[]? Hash);

    /// <summary>Decodes <paramref name="encodedLength"/> bytes from <paramref name="encoded"/> into <paramref name="decoded"/>. Streams stay the caller's.</summary>
    public static async Task<BlteDecoded> DecodeAsync(Stream encoded, long encodedLength, Stream decoded, CdnKey eKey, CdnKey? cKey,
        long? decodedLength, long maxDecodedBytes = MaxDecodedBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDecodedBytes, MaxDecodedBytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (encodedLength > MaxEncodedBytes || decodedLength > maxDecodedBytes) throw Limit("BLTE is over the object byte limit.");
        if (encodedLength < 9 || decodedLength < 0) throw Invalid("BLTE declared lengths are invalid.");
        var expectedEncoding = eKey.ToBytes();
        using var encodingHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var prefix = new byte[8];
        await ReadExactlyAsync(encoded, prefix, cancellationToken);
        if (!prefix.AsSpan(0, 4).SequenceEqual("BLTE"u8)) throw Invalid("BLTE magic is missing.");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        var headerless = headerLength == 0;
        encodingHash.AppendData(prefix);
        var chunks = new List<Chunk>();
        if (headerless) chunks.Add(new(encodedLength - 8, decodedLength, null));
        else
        {
            if (headerLength > 12L + 24L * MaxChunks) throw Limit("BLTE header is over the chunk limit.");
            if (headerLength < 12 || headerLength > encodedLength) throw Invalid("BLTE header length is invalid.");
            var table = new byte[(int)headerLength - 8];
            await ReadExactlyAsync(encoded, table, cancellationToken);
            encodingHash.AppendData(table);
            // The header is authenticated before any of its claims is believed.
            Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
            if (table[0] != 15) throw Unsupported("BLTE chunk table format is unsupported.");
            var count = (table[1] << 16) | (table[2] << 8) | table[3];
            if (count > MaxChunks) throw Limit("BLTE is over the chunk count limit.");
            if (count == 0 || headerLength != 12L + 24L * count) throw Invalid("BLTE chunk table size is invalid.");
            long encodedTotal = headerLength, decodedTotal = 0;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var offset = 4 + i * 24;
                var input = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset, 4));
                var output = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(offset + 4, 4));
                if (input == 0) throw Invalid("A BLTE chunk has no mode byte.");
                encodedTotal += input;
                decodedTotal += output;
                if (encodedTotal > MaxEncodedBytes || decodedTotal > maxDecodedBytes) throw Limit("BLTE chunk totals are over the object limits.");
                chunks.Add(new(input, output, table.AsSpan(offset + 8, 16).ToArray()));
            }
            if (encodedTotal != encodedLength || decodedLength is { } declared && decodedTotal != declared)
                throw Invalid("BLTE chunk totals differ from the declared lengths.");
        }

        var buffer = new byte[BufferBytes];
        var modeBuffer = new byte[1];
        long produced = 0;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            var hash = headerless ? encodingHash : chunkHash;
            await ReadExactlyAsync(encoded, modeBuffer, cancellationToken);
            hash.AppendData(modeBuffer);
            var mode = modeBuffer[0];
            var remaining = chunk.Encoded - 1;
            if (mode == 'N')
            {
                if (chunk.Decoded is { } expected && remaining != expected) throw Invalid("A BLTE raw chunk length is invalid.");
                if (produced + remaining > maxDecodedBytes) throw Limit("BLTE is over the decoded byte limit.");
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, remaining);
                    await ReadExactlyAsync(encoded, buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    contentHash.AppendData(buffer, 0, count);
                    await decoded.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    remaining -= count;
                    produced += count;
                }
            }
            else if (mode == 'Z')
            {
                if (remaining > MaxCompressedChunkBytes) throw Limit("A BLTE compressed chunk is over the working-memory limit.");
                var compressed = new byte[(int)remaining];
                for (var offset = 0; offset < compressed.Length;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(buffer.Length, compressed.Length - offset);
                    await ReadExactlyAsync(encoded, compressed.AsMemory(offset, count), cancellationToken);
                    hash.AppendData(compressed, offset, count);
                    offset += count;
                }
                produced += await InflateAsync(compressed, decoded, contentHash, chunk.Decoded, maxDecodedBytes - produced, buffer, cancellationToken);
            }
            else
            {
                // Hash the rest first so a corrupt mode byte is never reported as encrypted or unsupported.
                await SkipAsync(encoded, remaining, hash, buffer, cancellationToken);
                if (headerless) await RequireEndAsync(encoded, modeBuffer, cancellationToken);
                Verify(hash.GetHashAndReset(), headerless ? expectedEncoding : chunk.Hash!, headerless ? "encoding key" : "chunk checksum");
                if (mode == 'E') throw new BuildFormatException(BuildFailure.Encrypted, "BLTE_ENCRYPTED", "BLTE has an encrypted chunk.");
                throw Unsupported("A BLTE chunk mode is unsupported.");
            }
            if (!headerless) Verify(chunkHash.GetHashAndReset(), chunk.Hash!, "chunk checksum");
        }
        await RequireEndAsync(encoded, modeBuffer, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (headerless) Verify(encodingHash.GetHashAndReset(), expectedEncoding, "encoding key");
        if (cKey is { } content) Verify(contentHash.GetHashAndReset(), content.ToBytes(), "content key");
        return new(encodedLength, produced, chunks.Count);
    }

    /// <summary>
    /// Decodes a small whole object (ENCODING, ROOT and the like) into memory, refusing with <c>BLTE_LIMIT</c> past
    /// <paramref name="maxDecodedBytes"/>.
    /// </summary>
    public static async Task<byte[]> DecodeAsync(ReadOnlyMemory<byte> encoded, CdnKey eKey, CdnKey? cKey, long? decodedLength,
        long maxDecodedBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDecodedBytes, MaxDecodedBytes);
        if (decodedLength > maxDecodedBytes) throw Limit("BLTE is over the decoded byte limit.");
        using var input = MemoryMarshal.TryGetArray(encoded, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(encoded.ToArray(), writable: false);
        // A declared length is unauthenticated: start no larger than the encoded bytes already held and grow from there.
        using var output = new MemoryStream((int)Math.Min(decodedLength ?? 0, encoded.Length));
        await DecodeAsync(input, encoded.Length, output, eKey, cKey, decodedLength, maxDecodedBytes, cancellationToken);
        return output.ToArray();
    }

    private static async Task<long> InflateAsync(byte[] compressed, Stream target, IncrementalHash contentHash,
        long? expected, long allowed, byte[] buffer, CancellationToken cancellationToken)
    {
        using var input = new ExactInflaterInput(compressed, cancellationToken);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        var cap = expected ?? allowed;
        long produced = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var capacity = (int)Math.Min(buffer.Length, cap - produced + 1);
                // Inflating bounded memory is CPU work; only the caller's streams are asynchronous.
                var count = inflater.Read(buffer, 0, capacity);
                if (count == 0) break;
                if (count > cap - produced)
                    throw expected is null ? Limit("BLTE is over the decoded byte limit.") : Invalid("BLTE zlib output is over the declared length.");
                contentHash.AppendData(buffer, 0, count);
                await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                produced += count;
            }
        }
        catch (InvalidDataException) { throw Invalid("BLTE zlib data is invalid."); }
        if (expected is { } length && produced != length || input.ReadPastEnd || input.Position != compressed.Length)
            throw Invalid("BLTE zlib is truncated, has trailing bytes or has the wrong decoded length.");
        return produced;
    }

    private static async Task SkipAsync(Stream source, long remaining, IncrementalHash hash, byte[] buffer, CancellationToken cancellationToken)
    {
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            await ReadExactlyAsync(source, buffer.AsMemory(0, count), cancellationToken);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
    }

    private static async Task RequireEndAsync(Stream source, byte[] probe, CancellationToken cancellationToken)
    {
        if (await source.ReadAsync(probe, cancellationToken) != 0) throw Invalid("BLTE has trailing encoded bytes.");
    }

    // The framework inflater reads ahead and otherwise accepts a truncated member at physical EOF. The final byte is
    // fenced off: a valid member must ask for it to finish its checksum, and a member with trailing data finishes first.
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
        catch (EndOfStreamException) { throw Invalid("BLTE is truncated."); }
    }

    private static void Verify(byte[] actual, byte[] expected, string role)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new BuildFormatException(BuildFailure.Corrupt, "BLTE_INTEGRITY", $"BLTE {role} doesn't match.");
    }
    private static BuildFormatException Invalid(string message) => new(BuildFailure.Corrupt, "BLTE_INVALID", message);
    private static BuildFormatException Limit(string message) => new(BuildFailure.Corrupt, "BLTE_LIMIT", message);
    private static BuildFormatException Unsupported(string message) => new(BuildFailure.Unsupported, "BLTE_UNSUPPORTED", message);
}
