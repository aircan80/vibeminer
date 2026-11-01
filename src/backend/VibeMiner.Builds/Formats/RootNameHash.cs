using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace VibeMiner.Builds.Formats;

/// <summary>
/// The 64-bit name hash ROOT blocks carry: Bob Jenkins' lookup3 <c>hashlittle2</c> (public domain, 2006) over the path
/// uppercased (invariant) with <c>/</c> as <c>\</c>, both seeds 0, combined as <c>(pc &lt;&lt; 32) | pb</c>.
/// </summary>
public static class RootNameHash
{
    /// <summary>The ROOT name hash of a game path such as <c>interface/icons/x.blp</c>.</summary>
    public static ulong Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = Encoding.UTF8.GetBytes(path.ToUpperInvariant().Replace('/', '\\'));
        uint pc = 0, pb = 0;
        HashLittle2(bytes, ref pc, ref pb);
        return (ulong)pc << 32 | pb;
    }

    /// <summary>lookup3 <c>hashlittle2</c>: <paramref name="pc"/> and <paramref name="pb"/> are the seeds in and the two hashes out.</summary>
    internal static void HashLittle2(ReadOnlySpan<byte> key, ref uint pc, ref uint pb)
    {
        uint a, b, c;
        a = b = c = 0xdeadbeef + (uint)key.Length + pc;
        c += pb;
        if (key.Length == 0) { pc = c; pb = b; return; }
        while (key.Length > 12)
        {
            a += BinaryPrimitives.ReadUInt32LittleEndian(key);
            b += BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
            c += BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
            Mix(ref a, ref b, ref c);
            key = key[12..];
        }
        Span<byte> tail = stackalloc byte[12];
        tail.Clear();
        key.CopyTo(tail);
        a += BinaryPrimitives.ReadUInt32LittleEndian(tail);
        b += BinaryPrimitives.ReadUInt32LittleEndian(tail[4..]);
        c += BinaryPrimitives.ReadUInt32LittleEndian(tail[8..]);
        Final(ref a, ref b, ref c);
        pc = c; pb = b;
    }

    private static void Mix(ref uint a, ref uint b, ref uint c)
    {
        a -= c; a ^= BitOperations.RotateLeft(c, 4); c += b;
        b -= a; b ^= BitOperations.RotateLeft(a, 6); a += c;
        c -= b; c ^= BitOperations.RotateLeft(b, 8); b += a;
        a -= c; a ^= BitOperations.RotateLeft(c, 16); c += b;
        b -= a; b ^= BitOperations.RotateLeft(a, 19); a += c;
        c -= b; c ^= BitOperations.RotateLeft(b, 4); b += a;
    }

    private static void Final(ref uint a, ref uint b, ref uint c)
    {
        c ^= b; c -= BitOperations.RotateLeft(b, 14);
        a ^= c; a -= BitOperations.RotateLeft(c, 11);
        b ^= a; b -= BitOperations.RotateLeft(a, 25);
        c ^= b; c -= BitOperations.RotateLeft(b, 16);
        a ^= c; a -= BitOperations.RotateLeft(c, 4);
        b ^= a; b -= BitOperations.RotateLeft(a, 14);
        c ^= b; c -= BitOperations.RotateLeft(b, 24);
    }
}
