using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace VibeMiner.Formats.Testing;

/// <summary>Synthetic M2 model and SKIN bytes for the layer 5 reader and model tests and the layers above. Authored, no game content.</summary>
public static class ModelFixtures
{
    public static byte[] Modern(byte[] body) => Chunks(("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("MD21", body));
    public static byte[] Append(byte[] body, int at, byte[] payload, int count)
    { var result = body.Concat(payload).ToArray(); U32(result, at, (uint)count); U32(result, at + 4, (uint)body.Length); return result; }
    // Authored files: offsets are explicit native-format facts, with deliberately asymmetric geometry.
    public static byte[] Model(uint version = 274, uint firstTextureType = 0, bool activeChannels = false, bool twoCombos = false, int vertices = 4)
    {
        var body = Body(version, firstTextureType, activeChannels, twoCombos, vertices);
        return version == 264 ? body : Chunks(("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("MD21", body));
    }
    public static byte[] Body(uint version = 274, uint firstTextureType = 0, bool activeChannels = false, bool twoCombos = false, int vertices = 4)
    {
        using var output = new MemoryStream(); output.SetLength(304); output.Position = 304; using var w = new BinaryWriter(output, Encoding.UTF8, true);
        var descriptors = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { descriptors.Add((at, count, (int)output.Position)); write(w); }
        A(8, 9, x => x.Write(Encoding.UTF8.GetBytes("authored\0")));
        A(60, vertices, x => { for (var i = 0; i < vertices; i++) { var p = i switch { 0 => Vector3.Zero, 1 => new Vector3(2, 0, 0), 2 => new Vector3(0, 3, 0), _ => new Vector3(50, 60, 70) }; x.Write(p.X); x.Write(p.Y); x.Write(p.Z); x.Write(0x04030201u); x.Write(0x08070605u); x.Write(0f); x.Write(0f); x.Write(1f); x.Write(i == 1 ? 1f : 0f); x.Write(i == 2 ? 1f : 0f); x.Write(0.25f); x.Write(0.75f); } });
        A(80, 2, x => { x.Write(firstTextureType); x.Write(3u); x.Write(0L); x.Write(11u); x.Write(0u); x.Write(0L); });
        A(112, 1, x => x.Write(0u));
        A(128, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)1); });
        A(136, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)0); });
        A(144, 1, x => x.Write(ushort.MaxValue));
        A(152, twoCombos ? 2 : 1, x => { x.Write(ushort.MaxValue); if (twoCombos) x.Write(ushort.MaxValue); });
        if (activeChannels) { A(72, 1, x => x.Write(new byte[40])); A(88, 1, x => x.Write(new byte[20])); A(96, 1, x => x.Write(new byte[60])); }
        var bytes = output.ToArray(); "MD20"u8.CopyTo(bytes); U32(bytes, 4, version); U32(bytes, 68, 1);
        foreach (var d in descriptors) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); } return bytes;
    }
    public static byte[] Skin(uint version = 274, int triangleCount = 3, bool shadows = false, bool twoBatches = false)
    {
        var header = version == 264 ? 48 : 56; using var output = new MemoryStream(); output.SetLength(header); output.Position = header;
        using var w = new BinaryWriter(output, Encoding.UTF8, true); var ds = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { ds.Add((at, count, (int)output.Position)); write(w); }
        A(4, 4, x => { foreach (var v in new ushort[] { 2, 0, 1, 3 }) x.Write(v); });
        A(12, triangleCount, x => { for (var i = 0; i < triangleCount; i++) x.Write((ushort)(i % 3)); });
        A(20, 4, x => { for (var i = 0; i < 4; i++) x.Write(0x03020100u); });
        A(28, 1, x => { var s = new byte[48]; U16(s, 6, 4); U16(s, 10, 3); x.Write(s); });
        A(36, twoBatches ? 2 : 1, x => { var batch = new byte[24]; U16(batch, 8, ushort.MaxValue); U16(batch, 14, 1); x.Write(batch); if (twoBatches) { batch[1] = unchecked((byte)-3); x.Write(batch); } });
        if (shadows && header == 56) A(48, 1, x => x.Write(new byte[12]));
        var bytes = output.ToArray(); "SKIN"u8.CopyTo(bytes); foreach (var d in ds) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); } return bytes;
    }
    public static byte[] Chunks(params (string Id, byte[] Payload)[] chunks)
    { using var output = new MemoryStream(); using var w = new BinaryWriter(output); foreach (var c in chunks) { w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write((uint)c.Payload.Length); w.Write(c.Payload); } return output.ToArray(); }
    public static byte[] Words32(params uint[] words) { var bytes = new byte[words.Length * 4]; for (var i = 0; i < words.Length; i++) U32(bytes, i * 4, words[i]); return bytes; }
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);
}
