using VibeMiner.Concepts.Items;
using VibeMiner.Concepts.Tables;
using VibeMiner.Concepts.Tests.Tables;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// Item appearances, icons by source and model refs in the detail (D467-D470): new layer 6 behaviour (the old Items code never
/// joined appearances), over the layer 5 and 4 fakes. Synthetic data only.
/// </summary>
public sealed class ItemAppearancesTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const string Other = "FEDCBA9876543210";

    private static readonly Db2Column[] ModifiedColumns =
        [new("ID", "integer", true), new("ItemID", "integer", false), new("ItemAppearanceModifierID", "integer", false),
            new("ItemAppearanceID", "integer", false), new("OrderIndex", "integer", false)];
    private static readonly Db2Column[] AppearanceColumns =
        [new("ID", "integer", true), new("DisplayType", "integer", false), new("ItemDisplayInfoID", "integer", false), new("DefaultIconFileDataID", "integer", false)];
    private static readonly Db2Column[] DisplayColumns =
        [new("ID", "integer", true), new("ModelResourcesID[0]", "integer", false), new("ModelResourcesID[1]", "integer", false),
            new("ModelMaterialResourcesID[0]", "integer", false), new("ModelMaterialResourcesID[1]", "integer", false)];
    private static readonly Db2Column[] ModelColumns = [new("FileDataID", "integer", true), new("Flags", "integer", false), new("ModelResourcesID", "integer", false)];
    private static readonly Db2Column[] TextureColumns = [new("FileDataID", "integer", true), new("UsageType", "integer", false), new("MaterialResourcesID", "integer", false)];
    private static readonly Db2Column[] ComponentModelColumns =
        [new("ID", "integer", true), new("GenderIndex", "integer", false), new("ClassID", "integer", false), new("RaceID", "integer", false), new("PositionIndex", "integer", false)];
    private static readonly Db2Column[] ComponentTextureColumns =
        [new("ID", "integer", true), new("GenderIndex", "integer", false), new("ClassID", "integer", false), new("RaceID", "integer", false)];

    private static Db2Row R(uint id, params long[] cells) => new(id, false, null, [N(id), .. cells.Select(N)]);
    private static Db2Row Modified(uint id, uint item, long modifier, long appearance, long order = 0) => R(id, item, modifier, appearance, order);

    private static FakeTable Table(uint fdid, string name, Db2Column[] columns, IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready,
        TableSchema? schema = null, IReadOnlyDictionary<uint, string>? protectedIds = null) =>
        new(fdid, name, state, columns, rows, schema, ProtectedIds: protectedIds);

    private static DbdField Field(string name, int bits, bool unsigned = false, int length = 1, bool isId = false) =>
        new(new DbdColumn(name, "int", true, null, null), bits, unsigned, length, isId, false, false, null);

    private static TableSchema Schema(params DbdField[] fields) =>
        TableSchema.FromBlock(new DbdBlock(["00000000"], [], fields, null), SchemaMatch.Build, WowLocale.EnUS);

    /// <summary>
    /// Item 1: rows 100 (modifier 0, appearance 10) and 101 (modifier 1, appearance 11); item 2's row 102 also points at 10.
    /// Appearance 10: display 20, icon 555. Appearance 11: display 21, icon 0. Display 20: models [30, 0], materials [40, 0].
    /// Display 21: models [31, 30], materials [0, 0]. Resource 30: model files 5000 and 5001; 31 has none. Material 40: texture 6000.
    /// 5000 and 6000 have component keys, 5001 doesn't. 5000 is a stored M2 and 555 a stored icon.
    /// </summary>
    private static FakeTable[] Chain(IReadOnlyList<Db2Row>? modified = null, TableState modelState = TableState.Ready,
        IReadOnlyDictionary<uint, string>? modelProtected = null, IReadOnlyList<Db2Row>? models = null) =>
    [
        ItemTable([Item(1), Item(2)]), SparseTable([]),
        Table(2001, "ItemModifiedAppearance", ModifiedColumns, modified ?? [Modified(100, 1, 0, 10), Modified(101, 1, 1, 11, 1), Modified(102, 2, 0, 10)]),
        Table(2002, "ItemAppearance", AppearanceColumns, [R(10, 0, 20, 555), R(11, 0, 21, 0)]),
        Table(2003, "ItemDisplayInfo", DisplayColumns, [R(20, 30, 0, 40, 0), R(21, 31, 30, 0, 0)]),
        Table(2004, "ModelFileData", ModelColumns, models ?? [R(5000, 0, 30), R(5001, 0, 30)], modelState, protectedIds: modelProtected),
        Table(2005, "TextureFileData", TextureColumns, [R(6000, 1, 40)]),
        Table(2006, "ComponentModelFileData", ComponentModelColumns, [R(5000, 0, 0, 1, -1)]),
        Table(2007, "ComponentTextureFileData", ComponentTextureColumns, [R(6000, 1, 0, 2)]),
    ];

    private static FakeFile Model(uint id) => new(id, Content: ModelFixtures.Model(), Storage: FileStorage.Stored);

    private static async Task<ItemDetail> DetailAsync(ItemsWorld world, uint id = 1)
    {
        await using var items = await world.OpenAsync();
        var result = await items.GetAsync(id, Now, default);
        Assert.Equal(ItemResultState.Found, result.State);
        return result.Detail!;
    }

    private static FakeTable[] Replace(FakeTable[] tables, FakeTable table) => [.. tables.Where(t => t.Name != table.Name), table];

    [Fact]
    public async Task A_detail_joins_every_appearance_row_to_its_display_slots_files_and_component_keys()
    {
        var detail = await DetailAsync(new ItemsWorld().With(Chain()).WithFiles(Model(5000), Icon(555, 8, 8)));
        var set = detail.Appearances;
        Assert.Equal(ItemAppearancesState.Complete, set.State);
        Assert.Equal([100u, 101], set.Rows.Select(row => row.Row.Row.Id));
        Assert.Equal([(0L, 0L, 10L), (1, 1, 11)], set.Rows.Select(row => (row.ItemAppearanceModifierId!.Value, row.OrderIndex!.Value, row.ItemAppearanceId!.Value)));
        Assert.Equal((ItemDefaultAppearanceState.Default, 0), (set.Default, set.DefaultIndex));

        var appearance = set.Rows[0].Appearance;
        Assert.Equal((ItemRefState.Found, 555u, 20u), (appearance.State, appearance.DefaultIconFileDataId!.Value, appearance.ItemDisplayInfoId!.Value));
        var slots = appearance.Display!.Slots;
        Assert.Equal([0, 1], slots.Select(slot => slot.Slot));
        var model = slots[0].Model;
        Assert.Equal((ItemResourceState.Found, true, 30L), (model.State, model.Complete, model.ResourceId!.Value));
        Assert.Equal([5000u, 5001], model.Files.Select(file => file.FileDataId));
        Assert.Equal((ItemRefState.Found, 1L, 0L, 0L, -1L), (model.Files[0].Component.State, model.Files[0].Component.RaceId!.Value,
            model.Files[0].Component.GenderIndex!.Value, model.Files[0].Component.ClassId!.Value, model.Files[0].Component.PositionIndex!.Value));
        Assert.Equal(ItemRefState.Absent, model.Files[1].Component.State);
        Assert.Equal(ItemResourceState.NoResource, slots[1].Model.State);
        var material = slots[0].Material;
        Assert.Equal((ItemResourceKind.Material, ItemResourceState.Found), (material.Kind, material.State));
        var texture = Assert.Single(material.Files);
        Assert.Equal((6000u, 2L, 1L, (long?)null), (texture.FileDataId, texture.Component.RaceId!.Value, texture.Component.GenderIndex!.Value, texture.Component.PositionIndex));

        // Slot 0 of display 21 names a resource no row has (unresolved); its slot 1 names resource 30 again, kept as slot 1.
        var second = set.Rows[1].Appearance.Display!.Slots;
        Assert.Equal((ItemResourceState.Absent, 31L), (second[0].Model.State, second[0].Model.ResourceId!.Value));
        Assert.Equal((1, ItemResourceState.Found), (second[1].Model.Slot, second[1].Model.State));
        Assert.Equal(ItemResourceState.NoResource, second[0].Material.State);
    }

    [Fact]
    public async Task Each_distinct_model_file_is_read_once_with_its_textures_and_skin_count()
    {
        var detail = await DetailAsync(new ItemsWorld().With(Chain()).WithFiles(Model(5000)));
        var models = detail.Appearances.Models;
        Assert.Equal(ItemModelsState.Complete, models.State);
        Assert.Equal([5000u, 5001], models.Models.Select(model => model.FileDataId));
        var read = models.Models[0];
        // SFID 900 is the one main skin (view count 1); 901 is a LOD skin.
        Assert.Equal((ItemModelRefState.Read, ModelReadState.Read, 1), (read.State, read.Read, read.SkinFileDataIds));
        Assert.Equal([(0u, 700u), (11u, 0u)], read.Textures!.Select(texture => (texture.Type!.Value, texture.FileDataId!.Value)));
        Assert.Equal(ModelReferenceState.ResolvedElsewhere, read.Textures![1].State);
        Assert.Equal((ItemModelRefState.Read, ModelReadState.UnknownFile), (models.Models[1].State, models.Models[1].Read));
    }

    [Fact]
    public async Task Models_past_the_cap_are_listed_not_read_and_the_cut_off_is_named()
    {
        var world = new ItemsWorld(new ItemsOptions { MaxModels = 1 }).With(Chain()).WithFiles(Model(5000));
        var models = (await DetailAsync(world)).Appearances.Models;
        Assert.Equal((ItemModelsState.Capped, ItemsService.ModelCapCode), (models.State, models.Code));
        Assert.Equal([(5000u, ItemModelRefState.Read), (5001u, ItemModelRefState.NotRead)], models.Models.Select(model => (model.FileDataId, model.State)));
        Assert.Null(models.Models[1].Read);
    }

    [Fact]
    public async Task Both_icons_are_kept_with_their_source_fields_and_neither_wins()
    {
        var detail = await DetailAsync(new ItemsWorld().With(Chain()).WithFiles(Icon(555, 8, 8)));
        Assert.Equal([("Item", "IconFileDataID", 1u), ("ItemAppearance", "DefaultIconFileDataID", 10u)],
            detail.Icons.Select(icon => (icon.Table, icon.Column, icon.RowId)));
        Assert.Same(detail.Icon, detail.Icons[0].Icon);
        Assert.Equal((ItemIconState.FileFailed, 132759u), (detail.Icon.State, detail.Icon.FileDataId!.Value));
        Assert.Equal((ItemIconState.Ready, 555u), (detail.Icons[1].Icon.State, detail.Icons[1].Icon.FileDataId!.Value));
    }

    [Theory]
    [InlineData(new long[] { 0, 0 }, ItemDefaultAppearanceState.Ambiguous)]
    [InlineData(new long[] { 1, 2 }, ItemDefaultAppearanceState.None)]
    [InlineData(new long[] { 3, 0 }, ItemDefaultAppearanceState.Default)]
    public async Task The_default_is_the_one_modifier_0_row_never_a_pick(long[] modifiers, ItemDefaultAppearanceState expected)
    {
        var rows = modifiers.Select((modifier, i) => Modified((uint)(100 + i), 1, modifier, 10, i)).ToArray();
        var detail = await DetailAsync(new ItemsWorld().With(Chain(rows)));
        Assert.Equal(expected, detail.Appearances.Default);
        Assert.Equal(expected == ItemDefaultAppearanceState.Default ? 1 : null, detail.Appearances.DefaultIndex);
        Assert.Equal(expected == ItemDefaultAppearanceState.Default ? 2 : 1, detail.Icons.Count);
    }

    [Fact]
    public async Task A_partial_appearance_table_lists_its_readable_rows_and_proves_no_default()
    {
        var tables = Replace(Chain(), Table(2001, "ItemModifiedAppearance", ModifiedColumns, [Modified(100, 1, 0, 10)], TableState.Partial));
        var set = (await DetailAsync(new ItemsWorld().With(tables))).Appearances;
        Assert.Equal((ItemAppearancesState.Incomplete, ItemDefaultAppearanceState.Unknown, null), (set.State, set.Default, set.DefaultIndex));
        Assert.Equal(ItemRefState.Found, Assert.Single(set.Rows).Appearance.State);
    }

    [Fact]
    public async Task An_unresolved_resource_is_absent_on_ready_and_unknown_on_partial_even_with_hidden_keys()
    {
        var ready = (await DetailAsync(new ItemsWorld().With(Chain())));
        Assert.Equal(ItemResourceState.Absent, ready.Appearances.Rows[1].Appearance.Display!.Slots[0].Model.State);

        var partial = (await DetailAsync(new ItemsWorld().With(Chain(modelState: TableState.Partial)))).Appearances.Rows[1].Appearance.Display!.Slots[0].Model;
        Assert.Equal((ItemResourceState.Unknown, false), (partial.State, partial.Complete));
        var found = (await DetailAsync(new ItemsWorld().With(Chain(modelState: TableState.Partial)))).Appearances.Rows[0].Appearance.Display!.Slots[0].Model;
        Assert.Equal((ItemResourceState.Found, false), (found.State, found.Complete));

        var keyed = (await DetailAsync(new ItemsWorld().With(Chain(modelState: TableState.Partial, modelProtected: new Dictionary<uint, string> { [7000] = Key }))))
            .Appearances.Rows[1].Appearance.Display!.Slots[0].Model;
        // A relation can't say which hidden rows hold the ID: Unknown, with the table's keys as context only.
        Assert.Equal((ItemResourceState.Unknown, false), (keyed.State, keyed.Complete));
        Assert.Equal([Key], keyed.KeyNames!);
    }

    [Fact]
    public async Task A_row_lookup_keeps_absent_unknown_protected_and_not_readable_apart()
    {
        var tables = Replace(Chain([Modified(100, 1, 0, 10), Modified(101, 1, 1, 12), Modified(103, 1, 2, 13)]),
            Table(2002, "ItemAppearance", AppearanceColumns, [R(10, 0, 20, 555)], TableState.Partial, protectedIds: new Dictionary<uint, string> { [12] = Other }));
        tables = Replace(tables, new FakeTable(2003, "ItemDisplayInfo", TableState.NeedsNetwork));
        var rows = (await DetailAsync(new ItemsWorld().With(tables))).Appearances.Rows;
        Assert.Equal(ItemRefState.TableNotReadable, rows[0].Appearance.Display!.State);
        Assert.Equal(TableState.NeedsNetwork, rows[0].Appearance.Display!.Table!.State);
        Assert.Equal((ItemRefState.Protected, Other), (rows[1].Appearance.State, rows[1].Appearance.ProtectedKey));
        Assert.Equal(ItemRefState.Unknown, rows[2].Appearance.State);

        var ready = Chain([Modified(100, 1, 0, 99)]);
        Assert.Equal(ItemRefState.Absent, (await DetailAsync(new ItemsWorld().With(ready))).Appearances.Rows[0].Appearance.State);
    }

    [Fact]
    public async Task No_appearance_table_is_a_named_source_state()
    {
        var tables = Chain().Where(table => table.Name != "ItemModifiedAppearance").ToArray();
        var detail = await DetailAsync(new ItemsWorld().With(tables));
        Assert.Equal((ItemAppearancesState.TableNotReadable, TableState.NoDefinition), (detail.Appearances.State, detail.Appearances.Table!.State));
        Assert.Empty(detail.Appearances.Rows);
        Assert.Single(detail.Icons);
    }

    [Fact]
    public async Task A_full_relation_index_is_a_named_state_not_none_and_lookups_reuse_one_index()
    {
        var full = (await DetailAsync(new ItemsWorld(new ItemsOptions { MaxRelationIndexEntries = 2 }).With(Chain()))).Appearances;
        Assert.Equal((ItemAppearancesState.IndexLimit, RelationIndexes.LimitCode, ItemDefaultAppearanceState.Unknown), (full.State, full.Code, full.Default));
        Assert.Empty(full.Rows);

        var world = new ItemsWorld().With(Chain());
        await using var items = await world.OpenAsync();
        await items.GetAsync(1, Now, default);
        var pages = world.Tables.Calls("PageAsync");
        Assert.Equal(ItemAppearancesState.Complete, (await items.GetAsync(2, Now, default)).Detail!.Appearances.State);
        // The second detail reads its rows back by ordinal (one page each) and builds nothing: 1 appearance row, 2 model files, 1 texture.
        Assert.Equal(pages + 4, world.Tables.Calls("PageAsync"));
    }

    [Fact]
    public async Task A_table_that_keeps_changing_is_stale_never_answered_from_an_old_index()
    {
        var world = new ItemsWorld().With(Chain());
        var version = 0;
        var tables = new HookedTablesService(world.Tables, request =>
        {
            if (request.Table.Name == "ModelFileData")
                world.Tables.Add(Build, Locale, Replace(Chain(), Chain().Single(t => t.Name == "ModelFileData") with { ContentKey = N(++version).PadLeft(32, '0') }));
            return Task.CompletedTask;
        });
        var service = new ItemsService(tables, world.Files, new BlpImages(new VibeMiner.Generated.Testing.InMemoryGeneratedStore()));
        await using var items = (await service.ForAsync(Build, Locale, default)).Items!;
        var model = (await items.GetAsync(1, Now, default)).Detail!.Appearances.Rows[0].Appearance.Display!.Slots[0].Model;
        Assert.Equal((ItemResourceState.Stale, false), (model.State, model.Complete));
        Assert.Empty(model.Files);
    }

    [Fact]
    public async Task Models_when_the_files_dont_open_are_named_per_file()
    {
        var models = (await DetailAsync(new ItemsWorld().With(Chain()))).Appearances.Models;
        Assert.Equal(ItemModelsState.Complete, models.State);
        Assert.All(models.Models, model => Assert.Equal((ItemModelRefState.FilesNotOpen, "OBJECT_ACCESS_NEEDS_NETWORK", (ModelReadState?)null), (model.State, model.Code, model.Read)));
        Assert.Equal([5000u, 5001], models.Models.Select(model => model.FileDataId));
    }

    [Fact]
    public async Task More_rows_than_the_row_bound_keep_the_first_and_say_so()
    {
        var world = new ItemsWorld(new ItemsOptions { MaxAppearanceRows = 1, MaxResourceFiles = 1 }).With(Chain());
        var set = (await DetailAsync(world)).Appearances;
        Assert.Equal((ItemAppearancesState.RowLimit, ItemAppearanceReader.RowLimitCode, ItemDefaultAppearanceState.Unknown), (set.State, set.Code, set.Default));
        var model = Assert.Single(set.Rows).Appearance.Display!.Slots[0].Model;
        Assert.Equal((ItemResourceState.RowLimit, false), (model.State, model.Complete));
        Assert.Equal([5000u], model.Files.Select(file => file.FileDataId));
    }

    [Fact]
    public async Task A_layout_without_a_field_or_with_a_wider_one_is_unsupported()
    {
        var noOrder = Replace(Chain(), Table(2001, "ItemModifiedAppearance", ModifiedColumns[..4], [R(100, 1, 0, 10)]));
        var set = (await DetailAsync(new ItemsWorld().With(noOrder))).Appearances;
        Assert.Equal((ItemAppearancesState.Unsupported, ItemAppearanceReader.ProfileUnsupportedCode), (set.State, set.Code));

        var wide = Replace(Chain(), Table(2003, "ItemDisplayInfo", DisplayColumns, [R(20, 30, 0, 40, 0)], schema: Schema(Field("ID", 32, true, isId: true),
            Field("ModelResourcesID", 64, true, 2), Field("ModelMaterialResourcesID", 32, false, 2))));
        var display = (await DetailAsync(new ItemsWorld().With(wide))).Appearances.Rows[0].Appearance.Display!;
        Assert.Equal((ItemRefState.Unsupported, ItemAppearanceReader.ProfileUnsupportedCode), (display.State, display.Code));

        var fits = Replace(Chain(), Table(2003, "ItemDisplayInfo", DisplayColumns, [R(20, 30, 0, 40, 0)], schema: Schema(Field("ID", 32, true, isId: true),
            Field("ModelResourcesID", 32, true, 2), Field("ModelMaterialResourcesID", 32, false, 2))));
        Assert.Equal(ItemRefState.Found, (await DetailAsync(new ItemsWorld().With(fits))).Appearances.Rows[0].Appearance.Display!.State);
    }

    [Fact]
    public async Task A_key_outside_uint32_is_an_invalid_value_never_wrapped()
    {
        var tables = Replace(Chain([Modified(100, 1, 0, -1), Modified(101, 1, 1, 10)]),
            Table(2003, "ItemDisplayInfo", DisplayColumns, [R(20, -5, 0, 40, 0)]));
        var rows = (await DetailAsync(new ItemsWorld().With(tables))).Appearances.Rows;
        Assert.Equal((ItemRefState.InvalidValue, ItemAppearanceReader.ValueInvalidCode), (rows[0].Appearance.State, rows[0].Appearance.Code));
        Assert.Equal((ItemResourceState.InvalidValue, -5L), (rows[1].Appearance.Display!.Slots[0].Model.State, rows[1].Appearance.Display!.Slots[0].Model.ResourceId!.Value));
    }
}
