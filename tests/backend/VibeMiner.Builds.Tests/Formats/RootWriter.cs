using System.Text;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Tests.Formats;

/// <summary>One synthetic ROOT block. <c>Keys</c> defaults to entry i's key being 16 bytes of <c>(i + 1) * 0x11</c>.</summary>
internal sealed record RootBlock(uint[] Deltas, uint Locale, uint First, uint Second = 0, byte Third = 0,
    ulong[]? Names = null, CdnKey[]? Keys = null);

/// <summary>Writes synthetic TSFM ROOT bytes, header counts derived from the blocks. No game content.</summary>
internal static class RootWriter
{
    public const uint NoNames = 0x10000000;

    /// <summary>Deltas for strictly increasing FileDataIDs.</summary>
    public static uint[] Deltas(params uint[] ids)
    {
        var deltas = new uint[ids.Length];
        for (var i = 0; i < ids.Length; i++) deltas[i] = i == 0 ? ids[0] : ids[i] - ids[i - 1] - 1;
        return deltas;
    }

    public static CdnKey Key(byte fill) => new(Enumerable.Repeat(fill, 16).ToArray());

    public static byte[] Build(int version, int headerBytes, params RootBlock[] blocks)
    {
        using var target = new MemoryStream();
        using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8);
        writer.Write((uint)headerBytes);
        writer.Write((uint)version);
        writer.Write((uint)blocks.Sum(b => b.Deltas.Length));
        writer.Write((uint)blocks.Where(b => b.Names is not null).Sum(b => b.Deltas.Length));
        if (headerBytes == 24) writer.Write(0u);
        foreach (var block in blocks)
        {
            writer.Write((uint)block.Deltas.Length);
            if (version == 1) { writer.Write(block.First); writer.Write(block.Locale); }
            else { writer.Write(block.Locale); writer.Write(block.First); writer.Write(block.Second); writer.Write(block.Third); }
            foreach (var delta in block.Deltas) writer.Write(delta);
            for (var i = 0; i < block.Deltas.Length; i++)
                writer.Write(block.Keys is { } keys ? keys[i].ToBytes() : Key((byte)((i + 1) * 0x11)).ToBytes());
            if (block.Names is not null) foreach (var name in block.Names) writer.Write(name);
        }
        return target.ToArray();
    }
}
