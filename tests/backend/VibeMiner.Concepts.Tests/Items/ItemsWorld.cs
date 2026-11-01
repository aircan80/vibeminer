using System.Buffers.Binary;
using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.Generated.Testing;
using Xunit;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// A synthetic build for the real <see cref="ItemsService"/>: Item and ItemSparse as layer 5 fake tables, icon files in the
/// layer 4 fake, the in-memory generated store behind <see cref="BlpImages"/>. Columns follow the old projection tests' shape.
/// Synthetic data only.
/// </summary>
public sealed class ItemsWorld
{
    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32), "us");
    public const string Locale = "enUS";
    public const uint ItemFdid = 1001, SparseFdid = 1002;
    public const string Key = "0123456789ABCDEF";

    public InMemoryTables Tables { get; }
    public InMemoryFileSystem Files { get; } = new();
    public ItemsService Service { get; }

    public ItemsWorld(ItemsOptions? options = null, int maxScanRows = TableQueries.DefaultMaxScanRows)
    {
        Tables = new InMemoryTables { MaxScanRows = maxScanRows };
        Service = new ItemsService(Tables, Files, new BlpImages(new InMemoryGeneratedStore()), options);
    }

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static readonly Db2Column[] ItemColumns =
        [new("ID", "integer", true), new("ClassID", "integer", false), new("SubclassID", "integer", false), new("InventoryType", "integer", false),
            new("IconFileDataID", "integer", false)];

    /// <summary>ID, Display_lang, Description_lang, OverallQualityID, ItemLevel, InventoryType, then 3 SocketType, 3×10 stat slots, 5 Flags, 2 AllowableRaces, RequiredLevel.</summary>
    public static Db2Column[] SparseColumns(bool rawQuality = false)
    {
        var columns = new List<Db2Column> { new("ID", "integer", true), new("Display_lang", "string", false), new("Description_lang", "string", false),
            new("OverallQualityID", "integer", false, rawQuality ? new("raw-physical-integer-v1", 32, false, 8, true, "Authored raw physical value") : null),
            new("ItemLevel", "integer", false), new("InventoryType", "integer", false) };
        columns.AddRange(Enumerable.Range(0, 3).Select(i => new Db2Column($"SocketType[{i}]", "integer", false)));
        foreach (var name in new[] { "StatModifier_bonusStat", "StatPercentEditor", "StatPercentageOfSocket" })
            columns.AddRange(Enumerable.Range(0, 10).Select(i => new Db2Column($"{name}[{i}]", "integer", false)));
        columns.AddRange(Enumerable.Range(0, 5).Select(i => new Db2Column($"Flags[{i}]", "integer", false)));
        columns.AddRange(Enumerable.Range(0, 2).Select(i => new Db2Column($"AllowableRaces[{i}]", "integer", false)));
        columns.Add(new("RequiredLevel", "integer", false));
        return [.. columns];
    }

    public static Db2Row Item(uint id, bool copy = false, uint? source = null, string icon = "132759", string classId = "2") =>
        new(id, copy, source, [N(id), classId, "7", "13", icon]);

    public static Db2Row Sparse(uint id, string name, bool copy = false, uint? source = null, IReadOnlyList<Db2TextIssue>? issues = null)
    {
        var values = new List<string> { N(id), name, "", "42", "500", "13", "1", "2", "3" };
        values.AddRange(Enumerable.Range(0, 10).Select(i => i == 0 ? "-1" : N(i)));
        values.AddRange(Enumerable.Range(100, 10).Select(i => N(i))); values.AddRange(Enumerable.Range(200, 10).Select(i => N(i)));
        values.AddRange(["4294967295", "0", "2147483648", "1", "2", "4294967295", "2147483648", "-1"]);
        return new(id, copy, source, [.. values], issues);
    }

    public static FakeTable ItemTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, IReadOnlyList<Db2Column>? columns = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null, string? contentKey = null) =>
        new(ItemFdid, "Item", state, columns ?? ItemColumns, TableQueries.IsReadable(state) ? rows : null, ProtectedIds: protectedIds, ContentKey: contentKey);

    public static FakeTable SparseTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, IReadOnlyList<Db2Column>? columns = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null, string? contentKey = null) =>
        new(SparseFdid, "ItemSparse", state, columns ?? SparseColumns(), TableQueries.IsReadable(state) ? rows : null, ProtectedIds: protectedIds, ContentKey: contentKey);

    public ItemsWorld With(params FakeTable[] tables)
    {
        Tables.Add(Build, Locale, tables);
        return this;
    }

    public ItemsWorld WithFiles(params FakeFile[] files)
    {
        Files.Add(Build, [Locale], files);
        return this;
    }

    public async Task<IBuildItems> OpenAsync()
    {
        var opened = await Service.ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return opened.Items!;
    }

    /// <summary>A stored icon file: a one-level BGRA8 BLP2 of the given size.</summary>
    public static FakeFile Icon(uint id, int width, int height) => new(id, Content: Blp(width, height), Storage: FileStorage.Stored);

    /// <summary>One-level BLP2: <paramref name="encoding"/> 3 is BGRA8 (supported), 4 is no encoding this decoder reads.</summary>
    public static byte[] Blp(int width, int height, byte encoding = 3)
    {
        var level = width * height * 4;
        var bytes = new byte[148 + level];
        "BLP2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        bytes[8] = encoding; bytes[9] = 8; bytes[10] = 0; bytes[11] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 148);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(84), (uint)level);
        return bytes;
    }

    public static async Task<List<ItemSummary>> AllAsync(IBuildItems items, ItemQuery query)
    {
        var all = new List<ItemSummary>();
        for (var guard = 0; guard < 10_000; guard++)
        {
            var page = await items.ListAsync(query, TablePriority.Interactive, default);
            Assert.Equal(ItemsPageState.Ok, page.State);
            all.AddRange(page.Entries);
            if (page.Next is null) return all;
            query = query with { After = page.Next };
        }
        throw new InvalidOperationException("The cursor never ended.");
    }
}
