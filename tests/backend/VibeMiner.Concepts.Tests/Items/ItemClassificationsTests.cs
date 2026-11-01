using VibeMiner.Builds;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.Generated.Testing;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// classification-label-index-v2 (D049, D455-D456), ported from the old ItemClassificationIndexTests (rule tests) and the first
/// ItemClassificationsStoreTests test to <see cref="ItemsService.GetAsync"/> over the layer 5 fakes. Synthetic data only.
/// </summary>
public sealed class ItemClassificationsTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const uint ClassFdid = 1140189, SubclassFdid = 1261604;

    private static readonly Db2Column[] ClassColumns =
        [new("ID", "integer", true), new("ClassID", "integer", false), new("ClassName_lang", "string", false), new("Flags", "integer", false)];
    private static readonly Db2Column[] SubclassColumns =
        [new("ID", "integer", true), new("ClassID", "integer", false), new("SubClassID", "integer", false), new("DisplayName_lang", "string", false),
            new("Flags", "integer", false)];

    private static Db2Row Row(uint id, int key, string text, uint? copy = null, params Db2TextIssue[] issues) =>
        new(id, copy is not null, copy, [N(id), N(key), text, ""], issues);

    private static Db2Row Sub(uint id, int cls, int sub, string text, uint? copy = null, params Db2TextIssue[] issues) =>
        new(id, copy is not null, copy, [N(id), N(cls), N(sub), text, ""], issues);

    private static Db2Row ItemOf(uint id, string cls, string sub = "1") => new(id, false, null, [N(id), cls, sub, "13", "0"]);

    private static FakeTable ClassTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, TableSchema? schema = null) =>
        new(ClassFdid, "ItemClass", state, ClassColumns, TableQueries.IsReadable(state) ? rows : null, schema);

    private static FakeTable SubclassTable(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready) =>
        new(SubclassFdid, "ItemSubClass", state, SubclassColumns, TableQueries.IsReadable(state) ? rows : null);

    private static async Task<ItemClassLabels> LabelsAsync(ItemsWorld world, uint id)
    {
        await using var items = await world.OpenAsync();
        var result = await items.GetAsync(id, Now, default);
        Assert.Equal(ItemResultState.Found, result.State);
        return result.Detail!.Labels;
    }

    private static async Task<List<ItemClassLabels>> LabelsAsync(IReadOnlyList<Db2Row> items, params FakeTable[] targets)
    {
        var world = new ItemsWorld().With([ItemTable(items), SparseTable([]), .. targets]);
        await using var handle = await world.OpenAsync();
        var labels = new List<ItemClassLabels>();
        foreach (var item in items) labels.Add((await handle.GetAsync(item.Id, Now, default)).Detail!.Labels);
        return labels;
    }

    private static DbdField Field(string name, string family, int? bits, bool? unsigned, bool isId = false, bool verified = true) =>
        new(new DbdColumn(name, family, verified, null, null), bits, unsigned, 1, isId, false, false, null);

    private static TableSchema Schema(params DbdField[] fields) =>
        TableSchema.FromBlock(new DbdBlock(["00000000"], [], fields, null), SchemaMatch.Build, WowLocale.EnUS);

    [Fact]
    public async Task Semantic_class_id_matches_independently_of_record_id_and_preserves_signed_keys_and_copies()
    {
        var labels = await LabelsAsync([ItemOf(1, "2"), ItemOf(2, "-1"), ItemOf(3, "0"), ItemOf(4, "99"), ItemOf(5, "255")],
            ClassTable([Row(99, 2, "Weapon"), Row(100, -1, "Negative"), Row(uint.MaxValue, 0, "Zero", 99)]));
        var weapon = labels[0].Class;
        Assert.Equal((ItemClassLabelState.Ready, "Weapon", 99u, 2), (weapon.State, weapon.Text, weapon.Target!.Row.Id, weapon.TextColumn));
        Assert.Equal(new ItemSourceValue("Item", "ClassID", 1, "2"), Assert.Single(weapon.SourceKeys));
        Assert.Equal("Negative", labels[1].Class.Text);
        var copy = labels[2].Class.Target!.Row;
        Assert.Equal((uint.MaxValue, true, 99u), (copy.Id, copy.IsCopy, copy.CopySourceId));
        Assert.Equal((ItemClassLabelState.NotFound, 0L), (labels[3].Class.State, labels[3].Class.Candidates));
        Assert.Equal(ItemClassLabelState.KeyOutOfRange, labels[4].Class.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Every_emitted_duplicate_including_copies_is_ambiguous_even_with_equal_text(bool partial, bool sameText)
    {
        var labels = await LabelsAsync([ItemOf(1, "2")],
            ClassTable([Row(1, 2, "First"), Row(2, 2, sameText ? "First" : "Other", 1)], partial ? TableState.Partial : TableState.Ready));
        var value = labels[0].Class;
        Assert.Equal((ItemClassLabelState.Ambiguous, 2L), (value.State, value.Candidates));
        Assert.Null(value.Text); Assert.Null(value.Target);
    }

    [Fact]
    public async Task Partial_unique_or_missing_never_claims_a_label_or_absence()
    {
        var labels = await LabelsAsync([ItemOf(1, "2"), ItemOf(2, "3")], ClassTable([Row(99, 2, "Observed")], TableState.Partial));
        for (var i = 0; i < 2; i++)
        {
            var value = labels[i].Class;
            Assert.Equal((ItemClassLabelState.IncompleteTarget, i == 0 ? 1L : 0L), (value.State, value.Candidates));
            Assert.Null(value.Text); Assert.Null(value.Target);
            Assert.Equal(TableState.Partial, value.Table!.State);
        }
    }

    [Fact]
    public async Task Unique_empty_whitespace_invalid_and_oversized_text_keeps_the_exact_target()
    {
        var labels = await LabelsAsync([.. Enumerable.Range(1, 5).Select(i => ItemOf((uint)i, N(i)))],
            ClassTable([Row(1, 1, ""), Row(2, 2, "  "), Row(3, 3, "", null, new Db2TextIssue(2, "DB2_TEXT_ENCODING_INVALID", "ff")),
                Row(4, 4, new('x', 4097)), Row(5, 5, "Selected", null, new Db2TextIssue(3, "DB2_TEXT_ENCODING_INVALID", "ff"))]));
        var values = labels.Select(label => label.Class).ToList();
        Assert.Equal([ItemClassLabelState.EmptyText, ItemClassLabelState.Ready, ItemClassLabelState.InvalidText, ItemClassLabelState.Unsupported,
            ItemClassLabelState.Ready], values.Select(v => v.State));
        Assert.Equal(["", "  ", null, null, "Selected"], values.Select(v => v.Text));
        Assert.All(values, v => { Assert.NotNull(v.Target); Assert.Equal(2, v.TextColumn); Assert.Equal(1L, v.Candidates); });
        Assert.Equal(ItemClassifications.TextLimitCode, values[3].Code);
        var limit = (await LabelsAsync([ItemOf(1, "1")], ClassTable([Row(1, 1, new('x', 4096))])))[0].Class;
        Assert.Equal(ItemClassLabelState.Ready, limit.State);
    }

    // Was SourceContextSchemaAndIncompleteCoverageClaimsCannotBecomeReady (the key and text profile part; context, coverage and
    // binding checks are layer 5's): the target's keys must be verified signed 8-bit and its text a verified string.
    [Theory]
    [InlineData(32, false, true)]
    [InlineData(8, true, true)]
    [InlineData(8, false, false)]
    public async Task A_target_outside_the_signed8_key_and_text_profile_is_unsupported(int bits, bool unsigned, bool textVerified)
    {
        var schema = Schema(Field("ID", "int", 32, true, isId: true), Field("ClassID", "int", bits, unsigned),
            Field("ClassName_lang", "locstring", null, null, verified: textVerified), Field("Flags", "int", 32, false));
        var value = (await LabelsAsync([ItemOf(1, "2")], ClassTable([Row(99, 2, "Weapon")], schema: schema)))[0].Class;
        Assert.Equal((ItemClassLabelState.Unsupported, ItemClassifications.ProfileUnsupportedCode), (value.State, value.Code));
        Assert.Null(value.Text);
        var good = Schema(Field("ID", "int", 32, true, isId: true), Field("ClassID", "int", 8, false),
            Field("ClassName_lang", "locstring", null, null), Field("Flags", "int", 32, false));
        Assert.Equal("Weapon", (await LabelsAsync([ItemOf(1, "2")], ClassTable([Row(99, 2, "Weapon")], schema: good)))[0].Class.Text);
    }

    [Fact]
    public async Task A_target_without_its_key_or_text_column_is_unsupported()
    {
        var world = new ItemsWorld().With(ItemTable([ItemOf(1, "2")]), SparseTable([]),
            new FakeTable(ClassFdid, "ItemClass", TableState.Ready, [new("ID", "integer", true), new("ClassID", "integer", false)], [new(99, false, null, ["99", "2"])]),
            new FakeTable(SubclassFdid, "ItemSubClass", TableState.Ready,
                [new("ID", "integer", true), new("ClassID", "integer", false), new("SubClassID", "integer", false, new("raw-physical-integer-v1", 32, false, 8, true, "raw")),
                    new("DisplayName_lang", "string", false)], [new(1, false, null, ["1", "2", "1", "Sub"])]));
        var labels = await LabelsAsync(world, 1);
        Assert.Equal(ItemClassLabelState.Unsupported, labels.Class.State);
        Assert.Equal(ItemClassLabelState.Unsupported, labels.Subclass.State);
    }

    [Fact]
    public async Task Item_key_fields_outside_their_reviewed_width_are_no_field()
    {
        var schema = Schema(Field("ID", "int", 32, true, isId: true), Field("ClassID", "int", 8, false), Field("SubclassID", "int", 8, true),
            Field("InventoryType", "int", 8, false), Field("IconFileDataID", "int", 32, false));
        var world = new ItemsWorld().With(new FakeTable(ItemFdid, "Item", TableState.Ready, ItemColumns, [ItemOf(1, "2")], schema),
            SparseTable([]), ClassTable([Row(99, 2, "Weapon")]), SubclassTable([Sub(1, 2, 1, "Sub")]));
        var labels = await LabelsAsync(world, 1);
        Assert.Equal(ItemClassLabelState.FieldUnavailable, labels.Class.State);
        Assert.Empty(labels.Class.SourceKeys);
        Assert.Equal(ItemClassLabelState.FieldUnavailable, labels.Subclass.State);
    }

    private static int Pair(int cls, int sub) => ((cls + 128) << 8) | (sub + 128);

    // Was CompositeKeysDistinguishClassesAndPreserveLegacyClassBytes (index bytes aren't kept in layer 6): the subclass key is the
    // (ClassID, SubClassID) pair, never the row ID and never one of the two alone.
    [Fact]
    public async Task Composite_keys_distinguish_classes()
    {
        var labels = await LabelsAsync([ItemOf(1, "2", "1"), ItemOf(2, "4", "1"), ItemOf(3, "0", "0"), ItemOf(4, "10", "1"), ItemOf(5, "-1", "1")],
            ClassTable([Row(99, 2, "Class")]),
            SubclassTable([Sub(10, 2, 1, "A"), Sub(11, 4, 1, "B"), Sub(12, -1, -1, "Negative"), Sub(13, 0, 0, "Zero", 10), Sub(14, 1, 10, "Swapped")]));
        Assert.Equal(["A", "B", "Zero", null, null], labels.Select(label => label.Subclass.Text));
        Assert.Equal((13u, 10u), (labels[2].Subclass.Target!.Row.Id, labels[2].Subclass.Target!.Row.CopySourceId));
        Assert.Equal(ItemClassLabelState.NotFound, labels[3].Subclass.State);
        Assert.Equal(ItemClassLabelState.NotFound, labels[4].Subclass.State);
        Assert.Equal(2, labels[0].Subclass.SourceKeys.Count);
        Assert.Equal("Class", labels[0].Class.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Composite_duplicate_copy_partial_and_selected_text_rules(bool partial)
    {
        var labels = await LabelsAsync([ItemOf(1, "2", "1"), .. Enumerable.Range(1, 4).Select(i => ItemOf((uint)(1 + i), "4", N(i))), ItemOf(6, "0", "0")],
            SubclassTable([Sub(1, 2, 1, "Same"), Sub(2, 2, 1, "Same", 1), Sub(3, 4, 1, ""),
                Sub(4, 4, 2, "", null, new Db2TextIssue(3, "DB2_TEXT_ENCODING_INVALID", "ff")), Sub(5, 4, 3, new('x', 4097)),
                Sub(6, 4, 4, "  ", null, new Db2TextIssue(4, "DB2_TEXT_ENCODING_INVALID", "ff"))], partial ? TableState.Partial : TableState.Ready));
        Assert.Equal((ItemClassLabelState.Ambiguous, 2L), (labels[0].Subclass.State, labels[0].Subclass.Candidates));
        for (var i = 1; i <= 4; i++)
        {
            var value = labels[i].Subclass;
            Assert.Equal(partial ? ItemClassLabelState.IncompleteTarget : new[] { ItemClassLabelState.EmptyText, ItemClassLabelState.InvalidText,
                ItemClassLabelState.Unsupported, ItemClassLabelState.Ready }[i - 1], value.State);
            if (partial) Assert.Null(value.Target); else Assert.NotNull(value.Target);
        }
        Assert.Equal(partial ? ItemClassLabelState.IncompleteTarget : ItemClassLabelState.NotFound, labels[5].Subclass.State);
    }

    [Theory]
    [InlineData("-1", ItemClassLabelState.FieldUnavailable)]
    [InlineData("256", ItemClassLabelState.FieldUnavailable)]
    [InlineData("01", ItemClassLabelState.FieldUnavailable)]
    [InlineData("128", ItemClassLabelState.KeyOutOfRange)]
    [InlineData("255", ItemClassLabelState.KeyOutOfRange)]
    [InlineData("127", null)]
    [InlineData("0", null)]
    public void Unsigned_item_source_is_validated_before_the_signed_target_range(string sub, ItemClassLabelState? expected)
    {
        var result = ItemClassifications.SourceKey([new("Item", "ClassID", 1, "2"), new("Item", "SubclassID", 2, sub)], true);
        Assert.Equal(expected, result.State); Assert.Equal(2, result.Keys.Count);
        if (expected is null) Assert.Equal(Pair(2, int.Parse(sub, System.Globalization.CultureInfo.InvariantCulture)), Pair(result.ClassId, result.SubclassId));
    }

    [Theory]
    [InlineData("-128", null)]
    [InlineData("-1", null)]
    [InlineData("127", null)]
    [InlineData("-129", ItemClassLabelState.KeyOutOfRange)]
    [InlineData("128", ItemClassLabelState.KeyOutOfRange)]
    [InlineData("2147483647", ItemClassLabelState.KeyOutOfRange)]
    [InlineData("2147483648", ItemClassLabelState.FieldUnavailable)]
    [InlineData("-2147483649", ItemClassLabelState.FieldUnavailable)]
    public void Signed32_source_domain_precedes_the_target_bounds(string cls, ItemClassLabelState? expected) =>
        Assert.Equal(expected, ItemClassifications.SourceKey([new("Item", "ClassID", 1, cls), new("Item", "SubclassID", 2, "1")], true).State);

    // Was ExactLabelsRetainNumericEvidenceOrderDuplicatesMissingItemsAndUnsupportedSubclassOffline. v2 resolves the subclass; a
    // missing item has no detail (Absent), so MissingItem has no layer 6 form. A target that isn't readable keeps its state.
    [Fact]
    public async Task Exact_labels_keep_their_numeric_evidence_and_an_unreadable_target_keeps_its_table_state()
    {
        var world = new ItemsWorld().With(ItemTable([ItemOf(1, "2", "7")]), SparseTable([]), ClassTable([Row(99, 2, "Weapon")]),
            SubclassTable([Sub(5, 2, 7, "Polearm")]));
        var labels = await LabelsAsync(world, 1);
        Assert.Equal((ItemClassLabelState.Ready, "Weapon", 99u), (labels.Class.State, labels.Class.Text, labels.Class.Target!.Row.Id));
        Assert.Equal("2", Assert.Single(labels.Class.SourceKeys).Value);
        Assert.Equal((ItemClassLabelState.Ready, "Polearm"), (labels.Subclass.State, labels.Subclass.Text));
        Assert.Equal(["2", "7"], labels.Subclass.SourceKeys.Select(key => key.Value));

        var offline = new ItemsWorld().With(ItemTable([ItemOf(1, "2", "7")]), SparseTable([]), ClassTable([], TableState.NeedsNetwork),
            SubclassTable([], TableState.Encrypted));
        await using var items = await offline.OpenAsync();
        var result = await items.GetAsync(1, Now, default);
        Assert.Equal(ItemResultState.Found, result.State);
        var unread = result.Detail!.Labels;
        Assert.Equal((ItemClassLabelState.TableNotReadable, TableState.NeedsNetwork), (unread.Class.State, unread.Class.Table!.State));
        Assert.Equal((ItemClassLabelState.TableNotReadable, TableState.Encrypted), (unread.Subclass.State, unread.Subclass.Table!.State));
        Assert.Equal("2", Assert.Single(unread.Class.SourceKeys).Value);
        var absent = (await LabelsAsync([ItemOf(1, "2")]))[0];
        Assert.Equal((ItemClassLabelState.TableNotReadable, TableState.NoDefinition), (absent.Class.State, absent.Class.Table!.State));
    }

    [Fact]
    public async Task A_key_scan_that_hits_its_bound_is_a_scan_limit_not_not_found()
    {
        Db2Row[] rows = [.. Enumerable.Range(1, 10).Select(i => Row((uint)i, i == 10 ? 2 : 50 + i, $"Row {i}"))];
        var bounded = new ItemsWorld(new ItemsOptions { MaxLabelScanRows = 4 }, maxScanRows: 2)
            .With(ItemTable([ItemOf(1, "2"), ItemOf(2, "3")]), SparseTable([]), ClassTable(rows));
        await using (var items = await bounded.OpenAsync())
        {
            var limit = (await items.GetAsync(1, Now, default)).Detail!.Labels.Class;
            Assert.Equal((ItemClassLabelState.ScanLimit, ItemClassifications.ScanLimitCode), (limit.State, limit.Code));
            Assert.True(limit.Scanned >= 4 && limit.Scanned < 10);
            Assert.Equal(ItemClassLabelState.ScanLimit, (await items.GetAsync(2, Now, default)).Detail!.Labels.Class.State);
        }
        var paged = new ItemsWorld(maxScanRows: 2).With(ItemTable([ItemOf(1, "2"), ItemOf(2, "3")]), SparseTable([]), ClassTable(rows));
        await using var all = await paged.OpenAsync();
        var found = (await all.GetAsync(1, Now, default)).Detail!.Labels.Class;
        Assert.Equal(("Row 10", 10L), (found.Text, found.Scanned));
        Assert.Equal(ItemClassLabelState.NotFound, (await all.GetAsync(2, Now, default)).Detail!.Labels.Class.State);
    }

    // D456: ItemClass gets a new content key after the first scan page, so the next page's identity no longer fits.
    [Fact]
    public async Task A_target_that_changes_between_scan_pages_is_stale()
    {
        Db2Row[] rows = [.. Enumerable.Range(1, 10).Select(i => Row((uint)i, i == 10 ? 2 : 50 + i, $"Row {i}"))];
        var world = new ItemsWorld(maxScanRows: 2);
        FakeTable[] before = [ItemTable([ItemOf(1, "2")]), SparseTable([]), ClassTable(rows) with { ContentKey = new string('1', 32) }];
        world.With(before);
        var swaps = 0;
        var tables = new SwappingTables(world.Tables, "ItemClass", () =>
        {
            swaps++;
            world.Tables.Add(Build, Locale, [before[0], before[1], before[2] with { ContentKey = new string('2', 32) }]);
        });
        var service = new ItemsService(tables, world.Files, new BlpImages(new InMemoryGeneratedStore()));
        var opened = await service.ForAsync(Build, Locale, default);
        await using var items = opened.Items!;
        var result = await items.GetAsync(1, Now, default);
        Assert.Equal(ItemResultState.Found, result.State);
        var label = result.Detail!.Labels.Class;
        Assert.Equal(ItemClassLabelState.Stale, label.State);
        Assert.Null(label.Text);
        Assert.Equal(2L, label.Scanned);
        Assert.Equal(1, swaps);
    }

    /// <summary>Layer 5 tables that run <paramref name="swap"/> once, right after the first page of <paramref name="table"/>.</summary>
    private sealed class SwappingTables(ITables inner, string table, Action swap) : ITables
    {
        public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var opened = await inner.ForAsync(build, locale, cancellationToken);
            return opened.Tables is null ? opened : opened with { Tables = new Handle(opened.Tables, table, swap) };
        }

        private sealed class Handle(IBuildTables inner, string table, Action swap) : IBuildTables
        {
            private int pages;

            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken) =>
                inner.ListAsync(filter, priority, cancellationToken);
            public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) =>
                inner.CheckAsync(table, priority, cancellationToken);
            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) =>
                inner.SchemaAsync(table, priority, cancellationToken);
            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken) =>
                inner.GetAsync(table, id, priority, cancellationToken);
            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken) =>
                inner.FollowAsync(request, priority, cancellationToken);
            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken) =>
                inner.ReferencedByAsync(request, priority, cancellationToken);
            public Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken) =>
                inner.PreloadAsync(progress, cancellationToken);

            public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken)
            {
                var page = await inner.PageAsync(request, priority, cancellationToken);
                if (request.Table.Name == table && ++pages == 1) swap();
                return page;
            }
        }
    }
}
