using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;

namespace VibeMiner.Concepts.Tests.ItemEffects;

/// <summary>
/// A synthetic build for the real <see cref="ItemEffectsService"/>: Item, ItemXItemEffect, ItemEffect and SpellName as layer 5
/// fake tables. Definition blocks are the bundled WoWDBDefs blocks for real builds, so the reviewed field shapes are checked
/// against what layer 5 would resolve. Rows are synthetic.
/// </summary>
public sealed class EffectsWorld
{
    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32), "us");
    public const string Locale = "enUS";
    public const uint ItemFdid = 1001, BridgeFdid = 3177687, EffectFdid = 969941, NameFdid = 1990283;
    public const string RetailVersion = "12.1.5.70448", ClassicVersion = "1.15.9.70003";
    public const string BridgeLayout = "96F083AD", RetailLayout = "4CA77678", ClassicLayout = "1BF9CF3A", NameLayout = "782EE721";
    public const string Key = "0123456789ABCDEF";
    public const TablePriority Now = TablePriority.Interactive;

    public InMemoryTables Tables { get; }
    public ItemEffectsService Service { get; }

    public EffectsWorld(ItemEffectsOptions? options = null, int maxScanRows = TableQueries.DefaultMaxScanRows)
    {
        Tables = new InMemoryTables { MaxScanRows = maxScanRows };
        Service = new ItemEffectsService(Tables, options);
    }

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static readonly Lazy<Task<BundledDefinitions>> Bundled = new(() => BundledDefinitions.OpenAsync(default));

    /// <summary>The bundled block the build and file layout select, as layer 5 resolves it.</summary>
    public static async Task<TableSchema> Schema(string tableId, string version, string layout)
    {
        var selected = (await Bundled.Value).Require(tableId).Document.SelectFile(version, layout);
        var block = Assert.Single(selected.Blocks);
        return TableSchema.FromBlock(block, selected.Match == "layout" ? SchemaMatch.Layout : SchemaMatch.Build, WowLocale.EnUS);
    }

    /// <summary>The block a build selects with no file layout known, as layer 5 resolves a table it hasn't opened; null when not exactly one.</summary>
    public static async Task<TableSchema?> BuildSchema(string tableId, string version)
    {
        var selected = (await Bundled.Value).Require(tableId).Document.SelectBuild(version);
        return selected.Blocks.Count == 1 ? TableSchema.FromBlock(selected.Blocks[0], SchemaMatch.Build, WowLocale.EnUS) : null;
    }

    public static Db2ValueInterpretation Raw(int column) => new("raw-physical-integer-v1", 32, false, 16, column != 3, "Physical raw evidence.");

    public static readonly Db2Column[] ItemColumns = [new("ID", "integer", true), new("ClassID", "integer", false)];
    public static readonly Db2Column[] BridgeColumns = [new("ID", "integer", true), new("ItemEffectID", "integer", false), new("ItemID", "integer", false)];
    public static readonly Db2Column[] NameColumns = [new("ID", "integer", true), new("Name_lang", "string", false)];

    private static readonly string[] InputNames = ["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec",
        "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"];

    /// <summary>Retail ItemEffect header columns; <paramref name="raw"/> are the columns that kept raw evidence (D108).</summary>
    public static Db2Column[] RetailColumns(params int[] raw) =>
        [.. InputNames.Select((name, i) => new Db2Column(name, "integer", i == 0, raw.Contains(i) ? Raw(i) : null))];

    public static Db2Column[] ClassicColumns() => [.. InputNames.Select((name, i) => new Db2Column(name, "integer", i == 0)), new("ParentItemID", "integer", false)];

    public static Db2Row Item(uint id) => new(id, false, null, [N(id), "2"]);
    public static Db2Row Bridge(uint id, long effect, long item, bool copy = false, uint? source = null) => new(id, copy, source, [N(id), N(effect), N(item)]);

    /// <summary>A Retail ItemEffect row: ID then the nine inputs; <paramref name="inputs"/> replaces them, else 1..9 with SpellID as given.</summary>
    public static Db2Row Effect(uint id, long spell = 0, string[]? inputs = null, bool copy = false, uint? source = null, IReadOnlyList<Db2TextIssue>? issues = null) =>
        new(id, copy, source, [N(id), .. inputs ?? ["1", "2", "3", "4", "5", "6", N(spell), "8", "9"]], issues);

    public static Db2Row Classic(uint id, long parent, long spell = 0, string[]? inputs = null, bool copy = false, uint? source = null) =>
        new(id, copy, source, [N(id), .. inputs ?? ["1", "2", "3", "4", "5", "6", N(spell), "8", "9"], N(parent)]);

    public static Db2Row Name(uint id, string text, bool copy = false, uint? source = null, IReadOnlyList<Db2TextIssue>? issues = null) =>
        new(id, copy, source, [N(id), text], issues);

    private static TableCounts Counts(int rows, string layout, long excluded = 0, int sections = 0) => new(rows + excluded, rows, sections, excluded, "WDC5", layout);

    public static FakeTable ItemTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, IReadOnlyDictionary<uint, string>? protectedIds = null) =>
        new(ItemFdid, "Item", state, ItemColumns, TableQueries.IsReadable(state) ? rows : null, ProtectedIds: protectedIds);

    public static FakeTable Table(uint fdid, string name, string layout, IReadOnlyList<Db2Column> columns, IReadOnlyList<Db2Row> rows, TableSchema? schema,
        TableState state = TableState.Ready, IReadOnlyDictionary<uint, string>? protectedIds = null, string? contentKey = null, TableCounts? counts = null,
        TableStateReason? reason = null)
    {
        var readable = TableQueries.IsReadable(state);
        var opened = readable || state == TableState.Encrypted;
        var excluded = state == TableState.Partial ? 1 : 0;
        return new(fdid, name, state, opened ? columns : null, readable ? rows : null, schema, reason,
            opened ? counts ?? Counts(rows.Count, layout, excluded, excluded) : null, ProtectedIds: protectedIds, ContentKey: contentKey);
    }

    public static async Task<FakeTable> BridgeTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, TableCounts? counts = null,
        string? contentKey = null, TableStateReason? reason = null, bool schema = true) =>
        Table(BridgeFdid, "ItemXItemEffect", BridgeLayout, BridgeColumns, rows, schema ? await Schema("00cb674f", RetailVersion, BridgeLayout) : null, state,
            counts: counts, contentKey: contentKey, reason: reason);

    public static async Task<FakeTable> RetailTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, Db2Column[]? columns = null,
        IReadOnlyDictionary<uint, string>? protectedIds = null, TableSchema? schema = null, string layout = RetailLayout, TableStateReason? reason = null,
        bool noSchema = false) =>
        Table(EffectFdid, "ItemEffect", layout, columns ?? RetailColumns(), rows, noSchema ? null : schema ?? await Schema("4002a5b1", RetailVersion, RetailLayout), state,
            protectedIds, reason: reason);

    public static async Task<FakeTable> ClassicTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, string layout = ClassicLayout,
        TableSchema? schema = null, bool noSchema = false, TableStateReason? reason = null) =>
        Table(EffectFdid, "ItemEffect", layout, ClassicColumns(), rows, noSchema ? null : schema ?? await Schema("4002a5b1", ClassicVersion, ClassicLayout), state,
            reason: reason);

    public static async Task<FakeTable> NameTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, IReadOnlyDictionary<uint, string>? protectedIds = null,
        TableStateReason? reason = null, bool schema = true) =>
        Table(NameFdid, "SpellName", NameLayout, NameColumns, rows, schema ? await Schema("46c66698", RetailVersion, NameLayout) : null, state, protectedIds, reason: reason);

    /// <summary>A table the build's ROOT doesn't list (layer 5 NotInBuild), with the block its build selects (or none).</summary>
    public static FakeTable NotInBuild(uint fdid, string name, TableSchema? schema = null) =>
        new(fdid, name, TableState.NotInBuild, Schema: schema, Reason: new("UNKNOWN_FILE", "The build doesn't list the file.", null, null, null, []));

    /// <summary>A table no definition reads (D118): NoDefinition or DefinitionUnusable with the definition state and layer 5 code.</summary>
    public static FakeTable NoDefinition(uint fdid, string name, DefinitionState definition, string? code = null) =>
        new(fdid, name, definition is DefinitionState.NotInProvider or DefinitionState.NoMatch or DefinitionState.FetchingOff ? TableState.NoDefinition : TableState.DefinitionUnusable,
            Reason: new(code, "No definition reads the table.", null, definition, null, []));

    public EffectsWorld With(params FakeTable[] tables)
    {
        Tables.Add(Build, Locale, tables);
        return this;
    }

    public async Task<IBuildItemEffects> OpenAsync()
    {
        var opened = await Service.ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return opened.Effects!;
    }

    /// <summary>A Retail build: item 1 with links 500 (effect 0), 501 (effect 0 again), 502 (effect -1), 503 a copy of 500, and 504 for item 2.</summary>
    public static async Task<FakeTable[]> Retail() =>
    [
        ItemTable([Item(1), Item(2), Item(0)]),
        await BridgeTable([Bridge(500, 0, 1), Bridge(501, 0, 1), Bridge(502, -1, 1), Bridge(504, 7, 2), Bridge(503, 0, 1, true, 500)]),
        await RetailTable([Effect(0, 0), Effect(7, 7)]),
        await NameTable([Name(0, " "), Name(7, "Seven")]),
    ];

    public static async Task<List<ItemEffectEntry>> AllAsync(IBuildItemEffects effects, uint item, int limit)
    {
        var all = new List<ItemEffectEntry>();
        var query = new ItemEffectsQuery(limit);
        for (var guard = 0; guard < 10_000; guard++)
        {
            var page = await effects.GetAsync(item, query, Now, default);
            Assert.Equal(ItemEffectsPageState.Ok, page.State);
            all.AddRange(page.Entries);
            if (page.Next is null) return all;
            query = query with { After = page.Next };
        }
        throw new InvalidOperationException("The cursor never ended.");
    }
}
