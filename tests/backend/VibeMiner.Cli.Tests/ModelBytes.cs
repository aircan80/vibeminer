using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace VibeMiner.Cli.Tests;

// Synthetic M2 and SKIN bytes for the model section of the file detail, the minimum of
// tests/backend/VibeMiner.Formats.Tests/Models/ModelFixtures.cs (Cli.Tests can't reference Formats.Tests). Authored, no game data.
internal static class ModelBytes
{
    /// <summary>A chunked model: MD21 first (as real files are, so the bytes say M2Chunked), then the given chunks.</summary>
    internal static byte[] Chunked(uint version = 274, params (string Id, byte[] Payload)[] chunks) => Chunks([("MD21", Body(version)), .. chunks]);

    /// <summary>
    /// An MD20 body: 4 vertices, 2 textures (type 0, then type 11), 1 material, 1 skin profile, declared bounds 0 (zeros).
    /// </summary>
    internal static byte[] Body(uint version = 274)
    {
        using var output = new MemoryStream(); output.SetLength(304); output.Position = 304; using var w = new BinaryWriter(output, Encoding.UTF8, true);
        var descriptors = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { descriptors.Add((at, count, (int)output.Position)); write(w); }
        A(8, 9, x => x.Write(Encoding.UTF8.GetBytes("authored\0")));
        A(60, 4, x => { for (var i = 0; i < 4; i++) { var p = i switch { 0 => Vector3.Zero, 1 => new Vector3(2, 0, 0), 2 => new Vector3(0, 3, 0), _ => new Vector3(50, 60, 70) }; x.Write(p.X); x.Write(p.Y); x.Write(p.Z); x.Write(0x04030201u); x.Write(0x08070605u); x.Write(0f); x.Write(0f); x.Write(1f); x.Write(i == 1 ? 1f : 0f); x.Write(i == 2 ? 1f : 0f); x.Write(0.25f); x.Write(0.75f); } });
        A(80, 2, x => { x.Write(0u); x.Write(3u); x.Write(0L); x.Write(11u); x.Write(0u); x.Write(0L); });
        A(112, 1, x => x.Write(0u));
        A(128, 1, x => x.Write((ushort)0));
        A(136, 1, x => x.Write((ushort)0));
        A(144, 1, x => x.Write(ushort.MaxValue));
        A(152, 1, x => x.Write(ushort.MaxValue));
        var bytes = output.ToArray(); "MD20"u8.CopyTo(bytes); U32(bytes, 4, version); U32(bytes, 68, 1);
        foreach (var d in descriptors) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); }
        return bytes;
    }

    /// <summary>A version 274 SKIN for <see cref="Body"/>: 4 lookup entries, 3 triangle indices, 1 section, 1 batch.</summary>
    internal static byte[] Skin()
    {
        const int header = 56; using var output = new MemoryStream(); output.SetLength(header); output.Position = header;
        using var w = new BinaryWriter(output, Encoding.UTF8, true); var ds = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { ds.Add((at, count, (int)output.Position)); write(w); }
        A(4, 4, x => { foreach (var v in new ushort[] { 2, 0, 1, 3 }) x.Write(v); });
        A(12, 3, x => { for (var i = 0; i < 3; i++) x.Write((ushort)i); });
        A(20, 4, x => { for (var i = 0; i < 4; i++) x.Write(0x03020100u); });
        A(28, 1, x => { var s = new byte[48]; U16(s, 6, 4); U16(s, 10, 3); x.Write(s); });
        A(36, 1, x => { var batch = new byte[24]; U16(batch, 8, ushort.MaxValue); U16(batch, 14, 1); x.Write(batch); });
        var bytes = output.ToArray(); "SKIN"u8.CopyTo(bytes); foreach (var d in ds) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); }
        return bytes;
    }

    internal static byte[] Chunks(params (string Id, byte[] Payload)[] chunks)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        foreach (var c in chunks) { w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write((uint)c.Payload.Length); w.Write(c.Payload); }
        return output.ToArray();
    }

    internal static byte[] Words32(params uint[] words) { var bytes = new byte[words.Length * 4]; for (var i = 0; i < words.Length; i++) U32(bytes, i * 4, words[i]); return bytes; }

    /// <summary>AFID records: animation id, variation, FileDataID.</summary>
    internal static byte[] Afid(params (ushort Animation, ushort Variation, uint FileDataId)[] entries)
    {
        var bytes = new byte[entries.Length * 8];
        for (var i = 0; i < entries.Length; i++)
        { U16(bytes, i * 8, entries[i].Animation); U16(bytes, i * 8 + 2, entries[i].Variation); U32(bytes, i * 8 + 4, entries[i].FileDataId); }
        return bytes;
    }

    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
}
