using System.Buffers.Binary;
using System.Numerics;

namespace VibeMiner.Files.Formats;

/// <summary>
/// Salsa20/20 as Bernstein specifies it, which TACT uses for BLTE 'E' chunks. Written from the spec; no third-party code.
/// </summary>
internal static class Salsa20
{
    /// <summary>
    /// XORs the keystream for <paramref name="key"/> (16 or 32 bytes) and <paramref name="nonce"/> (8 bytes) into
    /// <paramref name="data"/> in place, starting at block 0. Running it twice restores the input.
    /// </summary>
    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, Span<byte> data)
    {
        if (key.Length is not (16 or 32)) throw new ArgumentException("A Salsa20 key is 16 or 32 bytes.", nameof(key));
        if (nonce.Length != 8) throw new ArgumentException("A Salsa20 nonce is 8 bytes.", nameof(nonce));

        // "expand 32-byte k" or "expand 16-byte k"; a 16-byte key fills both key slots.
        var wide = key.Length == 32;
        Span<uint> input = stackalloc uint[16];
        input[0] = 0x61707865;
        input[5] = wide ? 0x3320646eu : 0x3120646eu;
        input[10] = wide ? 0x79622d32u : 0x79622d36u;
        input[15] = 0x6b206574;
        var second = wide ? key[16..] : key;
        for (var i = 0; i < 4; i++)
        {
            input[1 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(4 * i)..]);
            input[11 + i] = BinaryPrimitives.ReadUInt32LittleEndian(second[(4 * i)..]);
        }
        input[6] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
        input[7] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[4..]);

        Span<uint> x = stackalloc uint[16];
        Span<byte> stream = stackalloc byte[64];
        ulong counter = 0;
        for (var offset = 0; offset < data.Length; counter++)
        {
            input[8] = (uint)counter;
            input[9] = (uint)(counter >> 32);
            Block(input, x);
            for (var i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(stream[(4 * i)..], x[i]);
            var chunk = data.Slice(offset, Math.Min(64, data.Length - offset));
            for (var i = 0; i < chunk.Length; i++)
                chunk[i] ^= stream[i];
            offset += chunk.Length;
        }
        input.Clear();
        x.Clear();
        stream.Clear();
    }

    /// <summary>Ten double rounds over a copy of <paramref name="input"/>, then the input added back in.</summary>
    private static void Block(ReadOnlySpan<uint> input, Span<uint> x)
    {
        input.CopyTo(x);
        for (var round = 0; round < 10; round++)
        {
            // Columns.
            Quarter(x, 0, 4, 8, 12); Quarter(x, 5, 9, 13, 1); Quarter(x, 10, 14, 2, 6); Quarter(x, 15, 3, 7, 11);
            // Rows.
            Quarter(x, 0, 1, 2, 3); Quarter(x, 5, 6, 7, 4); Quarter(x, 10, 11, 8, 9); Quarter(x, 15, 12, 13, 14);
        }
        for (var i = 0; i < 16; i++)
            x[i] += input[i];
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[b] ^= BitOperations.RotateLeft(x[a] + x[d], 7);
        x[c] ^= BitOperations.RotateLeft(x[b] + x[a], 9);
        x[d] ^= BitOperations.RotateLeft(x[c] + x[b], 13);
        x[a] ^= BitOperations.RotateLeft(x[d] + x[c], 18);
    }
}
