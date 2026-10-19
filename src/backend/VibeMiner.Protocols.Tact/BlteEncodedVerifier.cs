using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

/// <summary>Encoded identity proof only. DeclaredDecodedLength is authenticated header metadata,
/// not a verified decoded length; this proof establishes no content key or readability.</summary>
public sealed record BlteEncodedVerification(string EncodingKey, long EncodedLength, int ChunkCount,
    long? DeclaredDecodedLength, bool Headered);

/// <summary>Original streaming BLTE encoded verifier. Accepts opaque chunk modes without decoding.
/// Reads the caller-owned stream from its current position through exact EOF and leaves it open.
/// Acquisition provenance, scheduling, storage and publication belong to the caller.</summary>
public static class BlteEncodedVerifier
{
    public const string Revision = "blte-encoded-v1";
    public const long MaxEncodedBytes = 512L * 1024 * 1024;
    public const int MaxChunks = 65_536;
    private const int BufferBytes = 65_536;
    private const int DescriptorBytes = 24;
    private const int MaxHeaderBytes = 12 + DescriptorBytes * MaxChunks;

    public static async Task<BlteEncodedVerification> VerifyAsync(Stream encoded, string encodingKey,
        long encodedLength, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        token.ThrowIfCancellationRequested();
        if (!encoded.CanRead) throw new ArgumentException("BLTE input must be readable.", nameof(encoded));
        if (encodingKey is null || encodingKey.Length != 32 || encodingKey.Any(ch => !char.IsAsciiHexDigit(ch)))
            throw Error("BLTE_INVALID", "BLTE encoded verification requires a full MD5 encoding key.");
        var expectedKey = Convert.FromHexString(encodingKey);
        if (encodedLength > MaxEncodedBytes)
            throw Error("BLTE_LIMIT", "BLTE exceeds the encoded object byte limit.");
        if (encodedLength < 9)
            throw Error("BLTE_INVALID", "BLTE encoded length must include its prefix and a chunk mode byte.");

        var prefix = new byte[8];
        await ReadExactlyAsync(encoded, prefix, token);
        if (!prefix.AsSpan(0, 4).SequenceEqual("BLTE"u8))
            throw Error("BLTE_UNSUPPORTED", "Encoded retention supports BLTE objects only.");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        using var keyHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        keyHash.AppendData(prefix);
        var buffer = new byte[BufferBytes];

        if (headerLength == 0)
        {
            await HashBytesAsync(encoded, encodedLength - prefix.Length, keyHash, buffer, token);
            await RequireEofAsync(encoded, buffer, token);
            RequireHash(keyHash.GetHashAndReset(), expectedKey, "encoding key");
            return new(encodingKey.ToLowerInvariant(), encodedLength, 1, null, false);
        }

        if (headerLength > MaxHeaderBytes)
            throw Error("BLTE_LIMIT", "BLTE header exceeds the chunk-header byte limit.");
        if (headerLength < 12 || headerLength > encodedLength)
            throw Error("BLTE_INVALID", "BLTE header length is invalid.");
        var tablePrefix = new byte[4];
        await ReadExactlyAsync(encoded, tablePrefix, token);
        keyHash.AppendData(tablePrefix);
        if (tablePrefix[0] != 15)
            throw Error("BLTE_UNSUPPORTED", "BLTE chunk table format is unsupported.");
        var count = (tablePrefix[1] << 16) | (tablePrefix[2] << 8) | tablePrefix[3];
        if (count > MaxChunks)
            throw Error("BLTE_LIMIT", "BLTE exceeds the chunk count limit.");
        if (count == 0 || headerLength != 12L + DescriptorBytes * (long)count)
            throw Error("BLTE_INVALID", "BLTE chunk table size is invalid.");

        // Retain only the bounded descriptors, never a whole encoded chunk or decoded allocation.
        // The original 24-byte entries also avoid a per-chunk object/hash allocation.
        var descriptors = new byte[checked(count * DescriptorBytes)];
        for (var offset = 0; offset < descriptors.Length;)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(BufferBytes, descriptors.Length - offset);
            await ReadExactlyAsync(encoded, descriptors.AsMemory(offset, length), token);
            keyHash.AppendData(descriptors, offset, length);
            offset += length;
        }

        long encodedTotal = headerLength, declaredDecodedTotal = 0;
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = index * DescriptorBytes;
            var inputLength = BinaryPrimitives.ReadUInt32BigEndian(descriptors.AsSpan(offset, 4));
            var outputLength = BinaryPrimitives.ReadUInt32BigEndian(descriptors.AsSpan(offset + 4, 4));
            if (inputLength == 0)
                throw Error("BLTE_INVALID", "BLTE chunk lacks a mode byte.");
            // With the bounded uint32 descriptor count these sums fit long. Keep arithmetic checked
            // explicitly; decoded declarations impose no decoded working-set or readability claim.
            encodedTotal = checked(encodedTotal + inputLength);
            declaredDecodedTotal = checked(declaredDecodedTotal + outputLength);
            if (encodedTotal > MaxEncodedBytes)
                throw Error("BLTE_LIMIT", "BLTE chunk totals exceed the encoded object byte limit.");
        }
        if (encodedTotal != encodedLength)
            throw Error("BLTE_INVALID", "BLTE chunk totals differ from the declared encoded length.");
        RequireHash(keyHash.GetHashAndReset(), expectedKey, "encoding key");

        using var chunkHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var offset = index * DescriptorBytes;
            var length = BinaryPrimitives.ReadUInt32BigEndian(descriptors.AsSpan(offset, 4));
            await HashBytesAsync(encoded, length, chunkHash, buffer, token);
            RequireHash(chunkHash.GetHashAndReset(), descriptors.AsSpan(offset + 8, 16), "chunk checksum");
        }
        await RequireEofAsync(encoded, buffer, token);
        return new(encodingKey.ToLowerInvariant(), encodedLength, count, declaredDecodedTotal, true);
    }

    private static async Task HashBytesAsync(Stream source, long remaining, IncrementalHash hash,
        byte[] buffer, CancellationToken token)
    {
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var length = (int)Math.Min(buffer.Length, remaining);
            await ReadExactlyAsync(source, buffer.AsMemory(0, length), token);
            hash.AppendData(buffer, 0, length);
            remaining -= length;
        }
        token.ThrowIfCancellationRequested();
    }

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> bytes, CancellationToken token)
    {
        // Keep cancellation observable even when a custom stream ignores the supplied token.
        while (!bytes.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(bytes, token);
            token.ThrowIfCancellationRequested();
            if (read == 0) throw Error("BLTE_INVALID", "BLTE is truncated.");
            bytes = bytes[read..];
        }
    }

    private static async Task RequireEofAsync(Stream source, byte[] buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var read = await source.ReadAsync(buffer.AsMemory(0, 1), token);
        token.ThrowIfCancellationRequested();
        if (read != 0) throw Error("BLTE_INVALID", "BLTE has trailing encoded bytes.");
    }

    private static void RequireHash(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, string role)
    {
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw Error("BLTE_INTEGRITY", $"BLTE {role} does not match.");
    }

    private static DiscoveryException Error(string code, string message) => new(code, message);
}
