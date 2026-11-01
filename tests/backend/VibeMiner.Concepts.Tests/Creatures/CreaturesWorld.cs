using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.Generated.Testing;
using Xunit;

namespace VibeMiner.Concepts.Tests.Creatures;

/// <summary>
/// A synthetic build for the real <see cref="CreaturesService"/>: Creature, CreatureDisplayInfo and CreatureModelData as layer 5
/// fake tables with definitions shaped like the pinned WoWDBDefs blocks (Creature 6E14C900, display 7275F5F6, model 0F5449F8),
/// model and texture files in the layer 4 fake. Synthetic data only.
/// </summary>
public sealed class CreaturesWorld
{
    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32), "us");
    public const string Locale = "enUS";
    public const uint CreatureFdid = 841631, DisplayFdid = 1108759, ModelFdid = 1365368;
    public const string Key = "0123456789ABCDEF";

    public InMemoryTables Tables { get; }
    public InMemoryFileSystem Files { get; } = new();
    public CreaturesService Service { get; }

    public CreaturesWorld(CreaturesOptions? options = null)
    {
        Tables = new InMemoryTables();
        Service = new CreaturesService(Tables, Files, new BlpImages(new InMemoryGeneratedStore()), options);
    }

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static DbdField F(string name, string family, int? bits = null, bool? unsigned = null, int array = 1, bool id = false, bool nonInline = false,
        bool verified = true, bool relation = false) => new(new(name, family, verified, null, null), bits, unsigned, array, id, nonInline, relation, null);

    public static TableSchema Schema(string layout, IEnumerable<DbdField> fields) =>
        TableSchema.FromBlock(new DbdBlock([layout], [], [.. fields], null), SchemaMatch.Build, WowLocale.EnUS);

    /// <summary>Header columns flattened from definition fields, as layer 5 gives them.</summary>
    public static Db2Column[] Columns(IEnumerable<DbdField> fields) => [.. fields.SelectMany(field => Enumerable.Range(0, field.ArrayLength).Select(slot =>
        new Db2Column(field.Column.Name + (field.ArrayLength == 1 ? "" : $"[{slot}]"),
            field.Column.Family == "int" ? "integer" : field.Column.Family == "float" ? "float" : "string", field.IsId)))];

    public static DbdField[] CreatureFields() =>
    [
        F("ID", "int", 32, false, id: true, nonInline: true), F("Name_lang", "locstring"), F("NameAlt_lang", "locstring"), F("Title_lang", "locstring"),
        F("TitleAlt_lang", "locstring"), F("Classification", "int", 8, false), F("CreatureType", "int", 8, true), F("CreatureFamily", "int", 16, true),
        F("StartAnimState", "int", 8, false), F("DisplayID", "int", 32, false, 4), F("DisplayProbability", "float", array: 4),
    ];

    public static DbdField[] DisplayFields() =>
    [
        F("ID", "int", 32, false, id: true), F("ModelID", "int", 16, true), F("SizeClass", "int", 8, false), F("CreatureModelScale", "float"),
        F("ExtendedDisplayInfoID", "int", 32, false), F("TextureVariationFileDataID", "int", 32, false, 4),
    ];

    public static DbdField[] ModelFields() =>
        [F("ID", "int", 32, false, id: true, nonInline: true), F("FileDataID", "int", 32, true), F("ModelScale", "float"), F("CollisionHeight", "float")];

    /// <summary>A Creature row: ID, names, Classification -128, CreatureType 255, CreatureFamily 65535, StartAnimState 127, four displays and probabilities.</summary>
    public static Db2Row Creature(uint id, string name, int[]? displays = null, string[]? probabilities = null, string[]? facts = null, bool copy = false,
        uint? source = null, IReadOnlyList<Db2TextIssue>? issues = null)
    {
        displays ??= [0, 0, 0, 0];
        probabilities ??= ["1", "0", "0", "0"];
        return new(id, copy, source, [N(id), name, "Alt", "Title", "", .. facts ?? ["-128", "255", "65535", "127"], .. displays.Select(d => N(d)), .. probabilities], issues);
    }

    /// <summary>A display row: ID, ModelID, SizeClass 2, scale 1.5, ExtendedDisplayInfoID, four texture variations.</summary>
    public static Db2Row Display(uint id, string model, int extended = 0, int[]? variations = null) =>
        new(id, false, null, [N(id), model, "2", "1.5", N(extended), .. (variations ?? [800, 0, 0, 0]).Select(v => N(v))]);

    public static Db2Row Model(uint id, string file) => new(id, false, null, [N(id), file, "1.25", "2.25"]);

    public static FakeTable CreatureTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, DbdField[]? fields = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null, IReadOnlyList<Db2Column>? columns = null)
    {
        fields ??= CreatureFields();
        return new(CreatureFdid, "Creature", state, columns ?? Columns(fields), TableQueries.IsReadable(state) ? rows : null, Schema("6E14C900", fields),
            ProtectedIds: protectedIds);
    }

    public static FakeTable DisplayTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, string layout = "7275F5F6", DbdField[]? fields = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null)
    {
        fields ??= DisplayFields();
        return new(DisplayFdid, "CreatureDisplayInfo", state, Columns(fields), TableQueries.IsReadable(state) ? rows : null, Schema(layout, fields),
            Counts: TableQueries.IsReadable(state) ? new(rows.Count, rows.Count, 0, 0, "WDC5", layout) : null, ProtectedIds: protectedIds);
    }

    public static FakeTable ModelTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, string layout = "0F5449F8", DbdField[]? fields = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null)
    {
        fields ??= ModelFields();
        return new(ModelFdid, "CreatureModelData", state, Columns(fields), TableQueries.IsReadable(state) ? rows : null, Schema(layout, fields),
            Counts: TableQueries.IsReadable(state) ? new(rows.Count, rows.Count, 0, 0, "WDC5", layout) : null, ProtectedIds: protectedIds);
    }

    public CreaturesWorld With(params FakeTable[] tables)
    {
        Tables.Add(Build, Locale, tables);
        return this;
    }

    public CreaturesWorld WithFiles(params FakeFile[] files)
    {
        Files.Add(Build, [Locale], files);
        return this;
    }

    public async Task<IBuildCreatures> OpenAsync()
    {
        var opened = await Service.ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return opened.Creatures!;
    }

    public static FakeFile Stored(uint id, byte[] content, IReadOnlyList<FileHole>? holes = null) =>
        new(id, Content: content, Storage: FileStorage.Stored, Holes: holes);

    /// <summary>
    /// A chunked 274 model: SFID <paramref name="skin"/>, TXID <paramref name="txid"/>; texture 0 has type
    /// <paramref name="firstType"/> and flags <paramref name="firstFlags"/>, texture 1 type 11 (display variation 0). Texture
    /// combos are [0], or [0, 1] with <paramref name="twoCombos"/>; material i blends with <paramref name="blends"/>[i] (one
    /// opaque material by default). <paramref name="external"/> ("SKID" or "BFID") adds an external
    /// skeleton or bone file 600; <paramref name="version"/> 264 is the unchunked legacy MD20 body alone.
    /// </summary>
    public static byte[] M2(uint skin = 900, uint[]? txid = null, uint firstType = 0, bool twoCombos = false, ushort[]? blends = null,
        uint firstFlags = 3, uint version = 274, string? external = null)
    {
        var body = Body(firstType, twoCombos, blends ?? [0], firstFlags, version);
        if (version == 264) return body;
        (string, byte[])[] chunks = [("SFID", Words32(skin)), ("TXID", Words32(txid ?? [700, 0])), .. external is { } chunk ? new[] { (chunk, Words32(600)) } : [], ("MD21", body)];
        return Chunks(chunks);
    }

    /// <summary>
    /// A 274 SKIN (ModelFixtures' layout): one section, and one batch per entry on section 0 with color -1, its material,
    /// texture count and texture combo index.
    /// </summary>
    public static byte[] Skin(params (ushort Combo, ushort Count, ushort Material)[] batches) => Skin(3, batches.Length == 0 ? [(0, 1, 0)] : batches);

    /// <summary>As <see cref="Skin(ValueTuple{ushort, ushort, ushort}[])"/>, with the section's triangle count.</summary>
    public static byte[] Skin(ushort triangles, params (ushort Combo, ushort Count, ushort Material)[] batches)
    {
        const int header = 56;
        using var output = new MemoryStream(); output.SetLength(header); output.Position = header;
        using var w = new BinaryWriter(output, Encoding.UTF8, true); var ds = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { ds.Add((at, count, (int)output.Position)); write(w); }
        A(4, 4, x => { foreach (var v in new ushort[] { 2, 0, 1, 3 }) x.Write(v); });
        A(12, 3, x => { for (var i = 0; i < 3; i++) x.Write((ushort)(i % 3)); });
        A(20, 4, x => { for (var i = 0; i < 4; i++) x.Write(0x03020100u); });
        A(28, 1, x => { var section = new byte[48]; U16(section, 6, 4); U16(section, 10, triangles); x.Write(section); });
        A(36, batches.Length, x =>
        {
            foreach (var (combo, count, material) in batches)
            {
                var batch = new byte[24]; U16(batch, 8, ushort.MaxValue); U16(batch, 10, material); U16(batch, 14, count); U16(batch, 16, combo);
                x.Write(batch);
            }
        });
        var bytes = output.ToArray(); "SKIN"u8.CopyTo(bytes);
        foreach (var d in ds) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); }
        return bytes;
    }

    /// <summary>A one-stage batch on texture 0 only (texture 1 isn't drawn), and a two-stage batch drawing textures 0 and 1 (needs two combos).</summary>
    public static byte[] FirstOnly => Skin((0, 1, 0));
    public static byte[] BothDrawn => Skin((0, 2, 0));

    /// <summary>One-level 4x4 BLP2: encoding 3 is BGRA8, 4 is no encoding this decoder reads; a <paramref name="length"/> cuts the file.</summary>
    public static byte[] Blp(byte encoding = 3, int? length = null)
    {
        var bytes = new byte[148 + 64];
        "BLP2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        bytes[8] = encoding; bytes[9] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 148);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), 64);
        return length is { } cut ? bytes[..cut] : bytes;
    }

    // The authored MD20 body of tests/backend/VibeMiner.Formats.Tests/Models/ModelFixtures.cs (two textures: firstType, then 11), with a material blend mode.
    private static byte[] Body(uint firstTextureType, bool twoCombos, ushort[] blends, uint firstFlags, uint version)
    {
        const int vertices = 4;
        using var output = new MemoryStream(); output.SetLength(304); output.Position = 304; using var w = new BinaryWriter(output, Encoding.UTF8, true);
        var descriptors = new List<(int At, int Count, int Offset)>();
        void A(int at, int count, Action<BinaryWriter> write) { descriptors.Add((at, count, (int)output.Position)); write(w); }
        A(8, 9, x => x.Write(Encoding.UTF8.GetBytes("authored\0")));
        A(60, vertices, x =>
        {
            for (var i = 0; i < vertices; i++)
            {
                var p = i switch { 0 => Vector3.Zero, 1 => new Vector3(2, 0, 0), 2 => new Vector3(0, 3, 0), _ => new Vector3(50, 60, 70) };
                x.Write(p.X); x.Write(p.Y); x.Write(p.Z); x.Write(0x04030201u); x.Write(0x08070605u); x.Write(0f); x.Write(0f); x.Write(1f);
                x.Write(i == 1 ? 1f : 0f); x.Write(i == 2 ? 1f : 0f); x.Write(0.25f); x.Write(0.75f);
            }
        });
        A(80, 2, x => { x.Write(firstTextureType); x.Write(firstFlags); x.Write(0L); x.Write(11u); x.Write(0u); x.Write(0L); });
        A(112, blends.Length, x => { foreach (var blend in blends) { x.Write((ushort)0); x.Write(blend); } });
        A(128, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)1); });
        A(136, twoCombos ? 2 : 1, x => { x.Write((ushort)0); if (twoCombos) x.Write((ushort)0); });
        A(144, 1, x => x.Write(ushort.MaxValue));
        A(152, twoCombos ? 2 : 1, x => { x.Write(ushort.MaxValue); if (twoCombos) x.Write(ushort.MaxValue); });
        var bytes = output.ToArray(); "MD20"u8.CopyTo(bytes); U32(bytes, 4, version); U32(bytes, 68, 1);
        foreach (var d in descriptors) { U32(bytes, d.At, (uint)d.Count); U32(bytes, d.At + 4, (uint)d.Offset); }
        return bytes;
    }

    private static byte[] Chunks(params (string Id, byte[] Payload)[] chunks)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        foreach (var c in chunks) { w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write((uint)c.Payload.Length); w.Write(c.Payload); }
        return output.ToArray();
    }

    private static byte[] Words32(params uint[] words) { var bytes = new byte[words.Length * 4]; for (var i = 0; i < words.Length; i++) U32(bytes, i * 4, words[i]); return bytes; }
    private static void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void U16(byte[] bytes, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), value);

    public static async Task<List<CreatureSummary>> AllAsync(IBuildCreatures creatures, CreatureQuery query)
    {
        var all = new List<CreatureSummary>();
        for (var guard = 0; guard < 10_000; guard++)
        {
            var page = await creatures.ListAsync(query, TablePriority.Interactive, default);
            Assert.Equal(CreaturesPageState.Ok, page.State);
            all.AddRange(page.Entries);
            if (page.Next is null) return all;
            query = query with { After = page.Next };
        }
        throw new InvalidOperationException("The cursor never ended.");
    }
}
