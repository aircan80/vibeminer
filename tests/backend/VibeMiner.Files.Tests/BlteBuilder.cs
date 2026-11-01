using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using VibeMiner.Cdn;
using VibeMiner.Files.Formats;

namespace VibeMiner.Files.Tests;

/// <summary>
/// One synthetic BLTE chunk: its encoded payload (mode byte included, before encryption) and the decoded bytes it stands for.
/// Build them with <see cref="Raw"/>, <see cref="Zlib"/>, <see cref="Encrypted"/> or <see cref="Arbitrary"/>.
/// </summary>
internal sealed record BlteChunkSpec(byte[] Payload, byte[] Decoded, EncryptionSpec? Encryption = null)
{
    /// <summary>Mode 'N': the bytes as they are.</summary>
    public static BlteChunkSpec Raw(byte[] data) => new([(byte)'N', .. data], data);

    /// <summary>Mode 'Z': the bytes as one zlib member.</summary>
    public static BlteChunkSpec Zlib(byte[] data) => new([(byte)'Z', .. BlteBuilder.Compress(data)], data);

    /// <summary>
    /// Mode 'E': <paramref name="inner"/>'s payload Salsa20-encrypted with <paramref name="key"/> and the nonce for the chunk's
    /// ordinal (set when the object is built). <paramref name="cipher"/> other than 'S' (e.g. 'A' for ARC4) still encrypts with Salsa20.
    /// </summary>
    public static BlteChunkSpec Encrypted(ulong keyName, byte[] key, byte[] iv, BlteChunkSpec inner, byte cipher = (byte)'S') =>
        new(inner.Payload, inner.Decoded, new(keyName, key, iv, cipher));

    /// <summary>
    /// Any bytes as the whole chunk payload, mode byte included ('F', a bad zlib member, garbage), declaring
    /// <paramref name="decodedSize"/> decoded bytes (zeros in <see cref="BlteObject.Decoded"/>).
    /// </summary>
    public static BlteChunkSpec Arbitrary(byte[] payload, int decodedSize) => new(payload, new byte[decodedSize]);
}

/// <summary>An 'E' chunk's envelope fields; <see cref="Key"/> is the 16-byte TACT key.</summary>
internal sealed record EncryptionSpec(ulong KeyName, byte[] Key, byte[] Iv, byte Cipher);

/// <summary>A built object: its encoded bytes, its EKey, its decoded bytes and its header length (0 when headerless).</summary>
internal sealed record BlteObject(byte[] Encoded, CdnKey EKey, byte[] Decoded, int HeaderLength)
{
    /// <summary>The header to hand <see cref="BlteChunks.Parse"/>: the whole header, or the 8-byte prefix when headerless.</summary>
    public byte[] Header => Encoded[..(HeaderLength == 0 ? 8 : HeaderLength)];

    public BlteLayout Parse() => BlteChunks.Parse(Header, EKey, Encoded.Length, Decoded.Length);

    /// <summary>The encoded bytes of one parsed chunk.</summary>
    public byte[] ChunkBytes(BlteChunk chunk) => Encoded.AsSpan((int)chunk.EncodedOffset, chunk.EncodedSize).ToArray();
}

/// <summary>
/// Builds synthetic BLTE objects for tests: no game data and no real TACT keys. Chunked objects get a valid chunk table, chunk
/// MD5s and header EKey; <c>patchHeader</c> edits the header before it is hashed, so a test can make a well-signed bad header.
/// </summary>
internal static class BlteBuilder
{
    /// <summary>A chunked object from <paramref name="chunks"/> in order (chunk ordinal = list index).</summary>
    public static BlteObject Chunked(IReadOnlyList<BlteChunkSpec> chunks, Action<byte[]>? patchHeader = null)
    {
        var bodies = chunks.Select((spec, i) => Encode(spec, i)).ToArray();
        var headerLength = 12 + 24 * chunks.Count;
        var header = new byte[headerLength];
        "BLTE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)headerLength);
        header[8] = 0x0F;
        header[9] = (byte)(chunks.Count >> 16);
        header[10] = (byte)(chunks.Count >> 8);
        header[11] = (byte)chunks.Count;
        for (var i = 0; i < chunks.Count; i++)
        {
            var entry = header.AsSpan(12 + 24 * i, 24);
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)bodies[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)chunks[i].Decoded.Length);
            MD5.HashData(bodies[i], entry[8..]);
        }
        patchHeader?.Invoke(header);
        byte[] encoded = [.. header, .. bodies.SelectMany(b => b)];
        return new(encoded, new CdnKey(MD5.HashData(header)), [.. chunks.SelectMany(c => c.Decoded)], headerLength);
    }

    /// <summary>A headerless object of one chunk (ordinal 0); its EKey is the MD5 of the whole object.</summary>
    public static BlteObject Headerless(BlteChunkSpec chunk)
    {
        byte[] encoded = [.. "BLTE\0\0\0\0"u8, .. Encode(chunk, 0)];
        return new(encoded, new CdnKey(MD5.HashData(encoded)), chunk.Decoded, 0);
    }

    /// <summary>One zlib member of <paramref name="data"/>. The framework writes nothing for no input, so empty is spelled out.</summary>
    public static byte[] Compress(byte[] data)
    {
        if (data.Length == 0) return [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return output.ToArray();
    }

    /// <summary>The 8-byte Salsa20 nonce for an 'E' chunk: the 4 IV bytes, 4 zeros, the ordinal's low 4 bytes XORed in.</summary>
    public static byte[] Nonce(byte[] iv, int ordinal)
    {
        var nonce = new byte[8];
        iv.AsSpan(0, 4).CopyTo(nonce);
        for (var i = 0; i < 4; i++) nonce[i] ^= (byte)(ordinal >> (8 * i));
        return nonce;
    }

    /// <summary>Deterministic non-trivial bytes for chunk data.</summary>
    public static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 31 + seed * 17 + (i >> 5));
        return data;
    }

    private static byte[] Encode(BlteChunkSpec spec, int ordinal)
    {
        if (spec.Encryption is not { } e) return spec.Payload;
        var cipherText = spec.Payload.ToArray();
        Salsa20.Transform(e.Key, Nonce(e.Iv, ordinal), cipherText);
        var envelope = new byte[BlteChunks.EnvelopeLength];
        envelope[0] = (byte)'E';
        envelope[1] = 8;
        BinaryPrimitives.WriteUInt64LittleEndian(envelope.AsSpan(2), e.KeyName);
        envelope[10] = 4;
        e.Iv.AsSpan(0, 4).CopyTo(envelope.AsSpan(11));
        envelope[15] = e.Cipher;
        return [.. envelope, .. cipherText];
    }
}
