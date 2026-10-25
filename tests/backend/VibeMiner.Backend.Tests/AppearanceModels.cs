using System.Buffers.Binary;

namespace VibeMiner.Backend.Tests;

// Authored chunked MD21/MD20 274 models and SKINs for the textured appearance profile. No game bytes.
internal static class AppearanceModels
{
    internal sealed record Texture(uint Type, uint Flags = 0, uint Txid = 0);
    internal sealed record Batch(ushort Section = 0, ushort Shader = 0, byte Flags = 0x10, ushort Material = 0, ushort TextureCount = 1,
        ushort Combo = 0, ushort Weight = 0, ushort Transform = 0, short Color = -1, ushort Coordinate = 0, ushort Layer = 0);
    internal sealed record Model(Texture[] Textures, (ushort Flags, ushort Blend)[] Materials, ushort[] Combos, ushort[] WeightLookup,
        ushort[] TransformLookup, short[][][] WeightKeys, uint[] SequenceFlags, int Particles = 0, int Ribbons = 0, ushort[]? Coordinates = null,
        uint SkinId = 778, (int Tracks, int Slices, bool Shared)? WeightStress = null, int TransformTracks = 0);

    /// <summary>One texture of the given type, opaque material, one static full-opacity weight and no transform.</summary>
    internal static Model Simple(uint type = 11, uint txid = 0, ushort blend = 0, int particles = 0) => new(
        [new(type, 3, type == 0 ? txid : 0)], [(0, blend)], [0], [0], [ushort.MaxValue], [[[32767]]], [0x20], particles);

    internal static byte[] ModelBytes(Model m)
    {
        var body = new List<byte>(new byte[304]);
        int Append(byte[] bytes) { var at = body.Count; body.AddRange(bytes); while (body.Count % 4 != 0) body.Add(0); return at; }
        var header = new byte[304]; "MD20"u8.CopyTo(header); U32(header, 4, 274);
        void Array(int field, int count, int offset) { U32(header, field, (uint)count); U32(header, field + 4, count == 0 ? 0 : (uint)offset); }
        var vertices = new byte[3 * 48];
        float[][] positions = [[0, 0, 0], [1, 0, 0], [0, 0, 1]]; float[][] uvs = [[0, 1], [1, 1], [0, 0]];
        for (var i = 0; i < 3; i++)
        {
            for (var a = 0; a < 3; a++) F(vertices, i * 48 + a * 4, positions[i][a]);
            F(vertices, i * 48 + 28, 1); F(vertices, i * 48 + 32, uvs[i][0]); F(vertices, i * 48 + 36, uvs[i][1]);
        }
        Array(60, 3, Append(vertices)); U32(header, 68, 1);
        var textures = new byte[m.Textures.Length * 16];
        for (var i = 0; i < m.Textures.Length; i++) { U32(textures, i * 16, m.Textures[i].Type); U32(textures, i * 16 + 4, m.Textures[i].Flags); }
        Array(80, m.Textures.Length, Append(textures));
        var materials = new byte[m.Materials.Length * 4];
        for (var i = 0; i < m.Materials.Length; i++) { U16(materials, i * 4, m.Materials[i].Flags); U16(materials, i * 4 + 2, m.Materials[i].Blend); }
        Array(112, m.Materials.Length, Append(materials));
        Array(128, m.Combos.Length, Append(Words(m.Combos)));
        Array(136, m.Coordinates?.Length ?? 0, Append(Words(m.Coordinates ?? [])));
        Array(144, m.WeightLookup.Length, Append(Words(m.WeightLookup)));
        Array(152, m.TransformLookup.Length, Append(Words(m.TransformLookup)));
        var sequences = new byte[m.SequenceFlags.Length * 64];
        for (var i = 0; i < m.SequenceFlags.Length; i++) U32(sequences, i * 64 + 12, m.SequenceFlags[i]);
        Array(28, m.SequenceFlags.Length, Append(sequences));
        if (m.WeightStress is { } stress)
        {
            // Many tracks whose slices are all empty, pointing at one shared slice array or each at its own.
            var shared = Append(new byte[stress.Slices * 8]); var rows = new byte[stress.Tracks * 20];
            for (var t = 0; t < stress.Tracks; t++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(rows.AsSpan(t * 20 + 2), -1); U32(rows, t * 20 + 12, (uint)stress.Slices);
                U32(rows, t * 20 + 16, (uint)(stress.Shared ? shared : Append(new byte[stress.Slices * 8])));
            }
            Array(88, stress.Tracks, Append(rows));
        }
        else
        {
        var tracks = new byte[m.WeightKeys.Length * 20];
        for (var t = 0; t < m.WeightKeys.Length; t++)
        {
            var slices = new byte[m.WeightKeys[t].Length * 8];
            for (var s = 0; s < m.WeightKeys[t].Length; s++)
            {
                var keys = m.WeightKeys[t][s]; var bytes = new byte[keys.Length * 2];
                for (var k = 0; k < keys.Length; k++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(k * 2), keys[k]);
                U32(slices, s * 8, (uint)keys.Length); U32(slices, s * 8 + 4, keys.Length == 0 ? 0 : (uint)Append(bytes));
            }
            U16(tracks, t * 20, 0); BinaryPrimitives.WriteInt16LittleEndian(tracks.AsSpan(t * 20 + 2), -1);
            U32(tracks, t * 20 + 12, (uint)m.WeightKeys[t].Length); U32(tracks, t * 20 + 16, m.WeightKeys[t].Length == 0 ? 0 : (uint)Append(slices));
        }
        Array(88, m.WeightKeys.Length, Append(tracks));
        }
        if (m.TransformTracks > 0) Array(96, m.TransformTracks, Append(new byte[m.TransformTracks * 60]));
        var effects = Append(new byte[16]);
        if (m.Particles > 0) Array(296, m.Particles, effects);
        if (m.Ribbons > 0) Array(288, m.Ribbons, effects);
        var md20 = body.ToArray(); header.CopyTo(md20, 0);
        var chunks = new List<byte>();
        void Chunk(string id, byte[] payload) { chunks.AddRange(System.Text.Encoding.ASCII.GetBytes(id)); var size = new byte[4]; U32(size, 0, (uint)payload.Length); chunks.AddRange(size); chunks.AddRange(payload); }
        Chunk("MD21", md20); Chunk("SFID", Ids(m.SkinId));
        if (m.Textures.Length > 0) Chunk("TXID", Ids(m.Textures.Select(t => t.Txid).ToArray()));
        return chunks.ToArray();
    }

    /// <summary>Three vertices, one triangle, the given sections (each the same triangle) and batches.</summary>
    internal static byte[] SkinBytes(Batch[] batches, int sections = 1)
    {
        var batchAt = 80 + sections * 48; var result = new byte[batchAt + batches.Length * 24]; "SKIN"u8.CopyTo(result);
        U32(result, 4, 3); U32(result, 8, 56); U32(result, 12, 3); U32(result, 16, 62); U32(result, 20, 3); U32(result, 24, 68);
        U32(result, 28, (uint)sections); U32(result, 32, 80); U32(result, 36, (uint)batches.Length); U32(result, 40, batches.Length == 0 ? 0 : (uint)batchAt);
        for (var i = 0; i < 3; i++) { U16(result, 56 + i * 2, (ushort)i); U16(result, 62 + i * 2, (ushort)i); }
        for (var s = 0; s < sections; s++) { U16(result, 80 + s * 48 + 6, 3); U16(result, 80 + s * 48 + 10, 3); }
        for (var i = 0; i < batches.Length; i++)
        {
            var b = batches[i]; var at = batchAt + i * 24; result[at] = b.Flags; U16(result, at + 2, b.Shader); U16(result, at + 4, b.Section);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(at + 8), b.Color); U16(result, at + 10, b.Material); U16(result, at + 12, b.Layer);
            U16(result, at + 14, b.TextureCount); U16(result, at + 16, b.Combo); U16(result, at + 18, b.Coordinate); U16(result, at + 20, b.Weight); U16(result, at + 22, b.Transform);
        }
        return result;
    }

    /// <summary>BGRA BLP2 with every declared level filled with one colour.</summary>
    internal static byte[] Blp(int width, int height, int levels = 1, byte fill = 0x40)
    {
        var sizes = Enumerable.Range(0, levels).Select(l => Math.Max(1, width >> l) * Math.Max(1, height >> l) * 4).ToArray();
        var bytes = new byte[148 + sizes.Sum()]; "BLP2"u8.CopyTo(bytes); U32(bytes, 4, 1); bytes[8] = 3; bytes[9] = 8; bytes[11] = (byte)(levels > 1 ? 1 : 0);
        U32(bytes, 12, (uint)width); U32(bytes, 16, (uint)height); var at = 148;
        for (var l = 0; l < levels; l++) { U32(bytes, 20 + l * 4, (uint)at); U32(bytes, 84 + l * 4, (uint)sizes[l]); bytes.AsSpan(at, sizes[l]).Fill(fill); at += sizes[l]; }
        return bytes;
    }

    private static byte[] Words(ushort[] values) { var b = new byte[values.Length * 2]; for (var i = 0; i < values.Length; i++) U16(b, i * 2, values[i]); return b; }
    private static byte[] Ids(params uint[] values) { var b = new byte[values.Length * 4]; for (var i = 0; i < values.Length; i++) U32(b, i * 4, values[i]); return b; }
    private static void U32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    private static void U16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    private static void F(byte[] b, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);
}
