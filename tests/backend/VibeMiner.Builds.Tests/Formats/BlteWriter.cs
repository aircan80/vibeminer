using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Tests.Formats;

/// <summary>One synthetic chunk: its encoded block (mode byte first) and the decoded bytes it stands for.</summary>
internal sealed record BlteChunk(byte[] Block, byte[] Content);

/// <summary>A synthetic BLTE object with its keys. <see cref="CKey"/> is the MD5 of every chunk's content in order.</summary>
internal sealed record BlteObject(byte[] Encoded, byte[] Content, CdnKey EKey, CdnKey CKey);

/// <summary>Writes synthetic BLTE objects for tests: headerless or chunked, with N, Z or E chunks.</summary>
internal static class BlteWriter
{
    public static BlteChunk N(byte[] content) => new([(byte)'N', .. content], content);

    public static BlteChunk Z(byte[] content)
    {
        using var block = new MemoryStream();
        block.WriteByte((byte)'Z');
        // RFC 1950 header, a final empty fixed-Huffman block and Adler32(empty) = 1.
        if (content.Length == 0) block.Write([0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        else { using var zlib = new ZLibStream(block, CompressionLevel.SmallestSize, leaveOpen: true); zlib.Write(content); }
        return new(block.ToArray(), content);
    }

    /// <summary>An encrypted chunk standing for <paramref name="content"/>; its payload is opaque filler, not real encryption.</summary>
    public static BlteChunk E(byte[] content) => new([(byte)'E', .. new byte[13], .. content.Select(b => (byte)(b ^ 0x5a))], content);

    public static BlteObject Headerless(BlteChunk chunk)
    {
        byte[] encoded = [.. "BLTE"u8, 0, 0, 0, 0, .. chunk.Block];
        return new(encoded, chunk.Content, Key(encoded), Key(chunk.Content));
    }

    /// <summary>A chunked object; the table's decoded length for each chunk is its content length.</summary>
    public static BlteObject Chunked(params BlteChunk[] chunks)
    {
        using var encoded = new MemoryStream();
        encoded.Write("BLTE"u8);
        var headerLength = 12 + 24 * chunks.Length;
        WriteU32(encoded, (uint)headerLength);
        encoded.Write([15, (byte)(chunks.Length >> 16), (byte)(chunks.Length >> 8), (byte)chunks.Length]);
        foreach (var chunk in chunks)
        {
            WriteU32(encoded, (uint)chunk.Block.Length);
            WriteU32(encoded, (uint)chunk.Content.Length);
            encoded.Write(MD5.HashData(chunk.Block));
        }
        foreach (var chunk in chunks) encoded.Write(chunk.Block);
        var bytes = encoded.ToArray();
        var content = chunks.SelectMany(c => c.Content).ToArray();
        return new(bytes, content, Key(bytes[..headerLength]), Key(content));
    }

    /// <summary>The same object with its header rewritten and the EKey recomputed, so only the edit itself is wrong.</summary>
    public static BlteObject WithHeader(BlteObject blte, Action<byte[]> edit)
    {
        var bytes = blte.Encoded.ToArray();
        edit(bytes);
        var headerLength = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)), bytes.Length);
        return blte with { Encoded = bytes, EKey = Key(headerLength == 0 ? bytes : bytes[..headerLength]) };
    }

    public static CdnKey Key(byte[] bytes) => new(MD5.HashData(bytes));

    private static void WriteU32(Stream target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        target.Write(bytes);
    }
}
