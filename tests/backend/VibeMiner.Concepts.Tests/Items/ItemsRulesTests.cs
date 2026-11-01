using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// The Items game rules (D044, D450-D452), ported from the old ItemsProjectionTests and ItemIconsStoreTests to the real
/// <see cref="ItemsService"/> over the layer 5 and 4 fakes. Synthetic data only.
/// </summary>
public sealed class ItemsRulesTests
{
    private const TablePriority Now = TablePriority.Interactive;

    private static Task<ItemsPage> List(IBuildItems items, ItemQuery query) => items.ListAsync(query, Now, default);

    // Was AnchorJoinIncludesCopyTargetsAndCountsMissingSparseOnlyAndTypedNames (the join counts aren't kept in layer 6).
    [Fact]
    public async Task Item_anchors_a_left_join_to_sparse_with_copies_typed_names_and_no_sparse_only_rows()
    {
        var world = new ItemsWorld().With(
            ItemTable([Item(0), Item(1), Item(uint.MaxValue), Item(2, true, 0)]),
            SparseTable([Sparse(0, "Sword"), Sparse(1, ""), Sparse(3, "Sparse only"), Sparse(2, "", true, 0, [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")])]));
        await using var items = await world.OpenAsync();
        var page = await List(items, new());
        Assert.Equal([0u, 1, uint.MaxValue, 2], page.Entries.Select(row => row.Id));
        Assert.Equal([ItemNameState.Valid, ItemNameState.Empty, ItemNameState.MissingSparse, ItemNameState.Invalid], page.Entries.Select(row => row.Name.State));
        Assert.Null(page.Entries[2].Name.Value); Assert.Null(page.Entries[3].Name.Value); Assert.Equal("", page.Entries[1].Name.Value);
        Assert.Equal((true, 0u), (page.Entries[3].IsCopy, page.Entries[3].CopySourceId));
        var detail = (await items.GetAsync(2, Now, default)).Detail!;
        Assert.True(detail.ItemRow.Row.IsCopy); Assert.Equal(0u, detail.ItemRow.Row.CopySourceId);
        Assert.True(detail.SparseRow!.Row.IsCopy); Assert.Equal(0u, detail.SparseRow.Row.CopySourceId);
        Assert.Single(detail.SparseRow.Row.TextIssues!);
        Assert.Equal(ItemResultState.Absent, (await items.GetAsync(3, Now, default)).State);
        Assert.Empty((await List(items, new("Sparse only"))).Entries);
    }

    // Was NamesUseOnlyDisplayWhitespaceIsValidAndDescriptionIssueDoesNotInvalidateName.
    [Fact]
    public async Task Names_come_only_from_display_whitespace_is_valid_and_a_description_issue_doesnt_count()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1), Item(2)]),
            SparseTable([Sparse(1, "  "), Sparse(2, "Actual", issues: [new(2, "DB2_TEXT_ENCODING_INVALID", "ff")])]));
        await using var items = await world.OpenAsync();
        Assert.Single((await List(items, new("ACTUAL"))).Entries);
        Assert.Single((await List(items, new("  "))).Entries);
        Assert.Empty((await List(items, new("Description"))).Entries);
        Assert.Equal("Actual", (await items.GetAsync(2, Now, default)).Detail!.Summary.Name.Value);
    }

    // Was SearchUsesLiteralDotnetOrdinalIgnoreCase.
    [Theory]
    [InlineData("STRASSE", 0)]
    [InlineData("STRAßE", 1)]
    [InlineData("%_\\\"", 1)]
    [InlineData("é𝄞", 1)]
    public async Task Search_is_a_literal_dotnet_ordinal_ignore_case_match(string query, int count)
    {
        var world = new ItemsWorld().With(ItemTable([Item(1), Item(2)]), SparseTable([Sparse(1, "Straße %_\\\""), Sparse(2, "Café𝄞")]));
        await using var items = await world.OpenAsync();
        Assert.Equal(count, (await List(items, new(query))).Entries.Count);
    }

    // Was DirectoryAndNameBlockBoundariesPreserveMultibyteMatchesWithoutPerRowReads: the bounded scan now spans layer 5 pages and calls.
    [Fact]
    public async Task A_search_scans_a_bounded_number_of_rows_per_call_and_its_cursor_is_bound_to_the_request()
    {
        const int count = 6000;
        var world = new ItemsWorld(new ItemsOptions { MaxScanRows = 2000 }).With(
            ItemTable([.. Enumerable.Range(1, count).Select(i => Item((uint)i))]),
            SparseTable([.. Enumerable.Range(1, count).Select(i => Sparse((uint)i, new string('é', 130) + (i is 1500 or 1501 or 2999 or 6000 ? "𝄞MATCH" : "other")))]));
        await using var items = await world.OpenAsync();
        var first = await List(items, new("𝄞match", Limit: 2));
        Assert.Equal([1500u, 1501], first.Entries.Select(row => row.Id));
        Assert.Equal(1501, first.Scanned);
        Assert.NotNull(first.Next);
        // The scan budget runs out before the limit: fewer entries, and a cursor to go on.
        var second = await List(items, new("𝄞match", Limit: 2, After: first.Next));
        Assert.Equal(2999u, Assert.Single(second.Entries).Id); Assert.Equal(2000, second.Scanned); Assert.NotNull(second.Next);
        var third = await List(items, new("𝄞match", Limit: 2, After: second.Next));
        Assert.Empty(third.Entries); Assert.Equal(2000, third.Scanned); Assert.NotNull(third.Next);
        var last = await List(items, new("𝄞match", Limit: 2, After: third.Next));
        Assert.Equal(6000u, Assert.Single(last.Entries).Id); Assert.Null(last.Next);
        Assert.Equal(ItemsPageState.InvalidCursor, (await List(items, new("other", Limit: 2, After: first.Next))).State);
        Assert.Equal("INVALID_CURSOR", (await List(items, new("𝄞match", Limit: 1, After: first.Next))).Code);
    }

    [Fact]
    public async Task A_changed_table_makes_its_cursor_stale_and_never_re_bases_it()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1), Item(2), Item(3)]), SparseTable([Sparse(1, "a"), Sparse(2, "b"), Sparse(3, "c")]));
        await using var items = await world.OpenAsync();
        var list = await List(items, new(Limit: 1));
        var search = await List(items, new("a", Limit: 1));
        Assert.NotNull(search.Next);
        world.With(ItemTable([Item(1), Item(2), Item(3)], contentKey: new string('e', 32)), SparseTable([Sparse(1, "a"), Sparse(2, "b"), Sparse(3, "c")]));
        Assert.Equal(ItemsPageState.Stale, (await List(items, new(Limit: 1, After: list.Next))).State);
        // A search scans ItemSparse; a changed Item table is stale too.
        Assert.Equal(ItemsPageState.Stale, (await List(items, new("a", Limit: 1, After: search.Next))).State);
    }

    // Was ContextCoverageAndRawPhysicalClaimsRemainExact... : a raw-physical column isn't the field, and a Partial ItemSparse is unknown, never missing.
    [Fact]
    public async Task A_raw_physical_column_is_skipped_and_a_partial_sparse_is_unknown_or_protected_not_missing()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1), Item(2), Item(3)]),
            SparseTable([Sparse(1, "Name")], TableState.Partial, SparseColumns(rawQuality: true), new Dictionary<uint, string> { [2] = Key }));
        await using var items = await world.OpenAsync();
        var page = await List(items, new());
        Assert.Equal([ItemNameState.Valid, ItemNameState.SparseProtected, ItemNameState.SparseUnknown], page.Entries.Select(row => row.Name.State));
        Assert.Equal([null, Key, null], page.Entries.Select(row => row.SparseKey));
        Assert.DoesNotContain(page.Entries[0].Values, value => value.Column == "OverallQualityID");
        Assert.Contains(page.Entries[0].Values, value => value.Table == "ItemSparse" && value.Column == "ItemLevel" && value.Value == "500");
        Assert.Equal(TableState.Partial, page.Sparse!.State);
    }

    [Fact]
    public async Task A_partial_item_table_lists_its_readable_rows_and_names_the_rest()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1)], TableState.Partial, protectedIds: new Dictionary<uint, string> { [2] = Key }),
            SparseTable([Sparse(1, "One"), Sparse(2, "Two")]));
        await using var items = await world.OpenAsync();
        Assert.Equal([1u], (await List(items, new())).Entries.Select(row => row.Id));
        var hidden = await items.GetAsync(2, Now, default);
        Assert.Equal((ItemResultState.Protected, Key), (hidden.State, hidden.ProtectedKey));
        Assert.Equal(ItemResultState.Unknown, (await items.GetAsync(3, Now, default)).State);
    }

    // Review fix: only a proven absence drops a name match or an exact ID; an Item row a Partial Item can't read is listed as such.
    [Fact]
    public async Task A_match_or_exact_id_whose_item_row_cant_be_read_is_listed_with_the_item_outcome()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1)], TableState.Partial, protectedIds: new Dictionary<uint, string> { [2] = Key }),
            SparseTable([Sparse(1, "One sword"), Sparse(2, "Two sword"), Sparse(3, "Three sword")]));
        await using var items = await world.OpenAsync();
        var search = await List(items, new("sword"));
        Assert.Equal([1u, 2, 3], search.Entries.Select(row => row.Id));
        Assert.Equal([ItemRowState.Found, ItemRowState.Protected, ItemRowState.Unknown], search.Entries.Select(row => row.Item));
        Assert.Equal([null, Key, null], search.Entries.Select(row => row.ItemKey));
        Assert.Equal("Two sword", search.Entries[1].Name.Value);
        Assert.Null(search.Entries[1].IconFileDataId);
        Assert.All(search.Entries[1].Values, value => Assert.Equal("ItemSparse", value.Table));
        var hidden = Assert.Single((await List(items, new(ExactId: 2))).Entries);
        Assert.Equal((ItemRowState.Protected, Key, "Two sword"), (hidden.Item, hidden.ItemKey, hidden.Name.Value));
        var unknown = Assert.Single((await List(items, new(ExactId: 9))).Entries);
        Assert.Equal((ItemRowState.Unknown, ItemNameState.MissingSparse), (unknown.Item, unknown.Name.State));
        // In a Ready Item, a sparse-only match and an unknown ID are proven absent, so they stay out.
        var ready = new ItemsWorld().With(ItemTable([Item(1)]), SparseTable([Sparse(1, "One sword"), Sparse(2, "Two sword")]));
        await using var complete = await ready.OpenAsync();
        Assert.Equal([1u], (await List(complete, new("sword"))).Entries.Select(row => row.Id));
        Assert.Empty((await List(complete, new(ExactId: 2))).Entries);
    }

    [Fact]
    public async Task An_unreadable_item_table_keeps_its_state_and_an_unreadable_sparse_only_blocks_search()
    {
        var notInBuild = new ItemsWorld().With(ItemTable([], TableState.NotInBuild), SparseTable([Sparse(1, "One")]));
        await using (var items = await notInBuild.OpenAsync())
        {
            var page = await List(items, new());
            Assert.Equal((ItemsPageState.ItemNotReadable, TableState.NotInBuild), (page.State, page.Item!.State));
            Assert.Equal(ItemResultState.ItemNotReadable, (await items.GetAsync(1, Now, default)).State);
        }
        var noSparse = new ItemsWorld().With(ItemTable([Item(1)]), SparseTable([], TableState.NeedsNetwork));
        await using (var items = await noSparse.OpenAsync())
        {
            var page = await List(items, new());
            Assert.Equal(ItemNameState.SparseUnknown, Assert.Single(page.Entries).Name.State);
            Assert.Equal(TableState.NeedsNetwork, page.Sparse!.State);
            var search = await List(items, new("One"));
            Assert.Equal((ItemsPageState.SparseNotReadable, TableState.NeedsNetwork), (search.State, search.Sparse!.State));
        }
    }

    [Fact]
    public async Task Column_shape_problems_are_named_states_with_the_old_codes()
    {
        Db2Column[] noName = [.. SparseColumns().Select(column => column.Name == "Display_lang" ? column with { Kind = "integer" } : column)];
        var unsupported = new ItemsWorld().With(ItemTable([Item(1)]), SparseTable([Sparse(1, "One")], columns: noName));
        await using (var items = await unsupported.OpenAsync())
        {
            var page = await List(items, new());
            Assert.Equal((ItemsPageState.NameUnsupported, "ITEMS_NAME_UNSUPPORTED"), (page.State, page.Code));
            Assert.Equal((ItemResultState.NameUnsupported, "ITEMS_NAME_UNSUPPORTED"), ((await items.GetAsync(1, Now, default)).State, (await items.GetAsync(1, Now, default)).Code));
        }
        Db2Column[] twice = [.. ItemColumns.Select(column => column.Name == "SubclassID" ? column with { Name = "ClassID" } : column)];
        var ambiguous = new ItemsWorld().With(ItemTable([Item(1)], columns: twice), SparseTable([Sparse(1, "One")]));
        await using (var items = await ambiguous.OpenAsync())
            Assert.Equal((ItemsPageState.SchemaAmbiguous, "ITEMS_SCHEMA_AMBIGUOUS"), ((await List(items, new())).State, (await List(items, new())).Code));
        var invalid = new ItemsWorld().With(ItemTable([Item(1, classId: "02")]), SparseTable([Sparse(1, "One")]));
        await using (var items = await invalid.OpenAsync())
        {
            Assert.Equal((ItemsPageState.SourceInvalid, "ITEMS_SOURCE_INVALID"), ((await List(items, new())).State, (await List(items, new())).Code));
            Assert.Equal(ItemResultState.SourceInvalid, (await items.GetAsync(1, Now, default)).State);
        }
        var badIcon = new ItemsWorld().With(ItemTable([Item(1, icon: "1.5")]), SparseTable([Sparse(1, "One")]));
        await using (var items = await badIcon.OpenAsync())
            Assert.Equal(ItemsPageState.SourceInvalid, (await List(items, new())).State);
    }

    [Fact]
    public async Task A_field_the_layout_lacks_is_skipped_and_a_missing_icon_column_is_no_field()
    {
        Db2Column[] lean = [new("ID", "integer", true), new("ClassID", "integer", false)];
        var world = new ItemsWorld().With(ItemTable([new(1, false, null, ["1", "4"])], columns: lean), SparseTable([Sparse(1, "One")]));
        await using var items = await world.OpenAsync();
        var row = Assert.Single((await List(items, new())).Entries);
        Assert.Equal(["ClassID", "OverallQualityID", "ItemLevel", "InventoryType"], row.Values.Select(value => value.Column));
        Assert.Null(row.IconFileDataId);
        Assert.Equal(ItemIconState.FieldUnavailable, (await items.GetAsync(1, Now, default)).Detail!.Icon.State);
    }

    // Was InspectPreservesTenAlignedStatSlotsMaskWordsSentinelsAndConflictingSourceValues.
    [Fact]
    public async Task A_detail_keeps_the_source_rows_exactly_and_attributes_each_summary_cell()
    {
        var sparse = Sparse(1, "Name");
        var values = sparse.Values.ToArray(); values[5] = "17";
        var world = new ItemsWorld().With(ItemTable([Item(1)]), SparseTable([sparse with { Values = values }]));
        await using var items = await world.OpenAsync();
        var detail = (await items.GetAsync(1, Now, default)).Detail!;
        var columns = SparseColumns();
        string Value(string column) => detail.SparseRow!.Row.Values[Array.FindIndex(columns, c => c.Name == column)];
        Assert.Equal(columns.Length, detail.SparseRow!.Row.Values.Count);
        for (var slot = 0; slot < 10; slot++)
        {
            Assert.Equal(slot == 0 ? "-1" : N(slot), Value($"StatModifier_bonusStat[{slot}]"));
            Assert.Equal(N(100 + slot), Value($"StatPercentEditor[{slot}]"));
            Assert.Equal(N(200 + slot), Value($"StatPercentageOfSocket[{slot}]"));
        }
        Assert.Equal("4294967295", Value("Flags[0]")); Assert.Equal("2147483648", Value("AllowableRaces[1]")); Assert.Equal("-1", Value("RequiredLevel"));
        Assert.Contains(detail.Summary.Values, v => v.Table == "Item" && v.Column == "InventoryType" && v.Value == "13");
        Assert.Contains(detail.Summary.Values, v => v.Table == "ItemSparse" && v.ColumnIndex == 5 && v.Value == "17");
    }

    // Was UnsupportedOptionalIconIntegerDoesNotDiscardUsableItemOrRawEvidence.
    [Fact]
    public async Task An_out_of_range_icon_is_no_field_and_keeps_the_item_and_its_raw_cell()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1, icon: "-1")]), SparseTable([Sparse(1, "Name")]));
        await using var items = await world.OpenAsync();
        var row = Assert.Single((await List(items, new())).Entries);
        Assert.Null(row.IconFileDataId); Assert.Equal("Name", row.Name.Value);
        var detail = (await items.GetAsync(1, Now, default)).Detail!;
        Assert.Equal("-1", detail.ItemRow.Row.Values[4]);
        Assert.Equal(ItemIconState.FieldUnavailable, detail.Icon.State);
    }

    // Was IconAuthorizationReadsOnlyBoundedPayloads... (ItemsProjectionTests) and Exact_visible_batch_..._keeps_optional_outcomes_independent
    // (ItemIconsStoreTests): every item's icon is its own outcome, and only a detail reads the file.
    [Fact]
    public async Task Icon_outcomes_are_independent_per_item_and_only_a_detail_reads_the_file()
    {
        var protectedIcon = new FakeFile(806, Content: Blp(4, 4), Storage: FileStorage.Stored, Holes: [new FileHole(0, 148, Key)]);
        var world = new ItemsWorld().With(
            ItemTable([Item(1, icon: "800"), Item(2, icon: "800"), Item(3, icon: "800"), Item(4, icon: "801"), Item(5, icon: "802"),
                Item(6, icon: "803"), Item(7, icon: "806"), Item(8, icon: "0"), Item(9, icon: "4294967296")]),
            SparseTable([Sparse(1, "Name")]))
            .WithFiles(Icon(800, 4, 2), new FakeFile(801, Content: Blp(4, 4, encoding: 4), Storage: FileStorage.Stored),
                new FakeFile(803, Size: null, NotInEncoding: true), protectedIcon);
        await using var items = await world.OpenAsync();
        var listed = await List(items, new());
        Assert.Equal([800u, 800, 800, 801, 802, 803, 806, 0, null], listed.Entries.Select(row => row.IconFileDataId));
        var icons = new List<ItemIcon>();
        for (uint id = 1; id <= 9; id++) icons.Add((await items.GetAsync(id, Now, default)).Detail!.Icon);
        Assert.All(icons.Take(3), icon => Assert.Equal((ItemIconState.Ready, 4u, 2u), (icon.State, icon.Header!.Width, icon.Header.Height)));
        // The CKey of the variant layer 4 picked (D453): the fake's is the content's MD5.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Blp(4, 2))), icons[0].ContentKey);
        Assert.Null(icons[4].ContentKey);
        Assert.Equal((ItemIconState.Unsupported, BlpImageState.Unsupported), (icons[3].State, icons[3].Image));
        Assert.Equal(4u, icons[3].Header!.Width);
        Assert.Equal((ItemIconState.FileFailed, FileReadState.UnknownFile), (icons[4].State, icons[4].FileState));
        Assert.Equal((ItemIconState.FileFailed, FileReadState.NotInEncoding), (icons[5].State, icons[5].FileState));
        Assert.Equal(ItemIconState.Protected, icons[6].State); Assert.Equal([Key], icons[6].HoleKeys);
        Assert.Equal(ItemIconState.NoIcon, icons[7].State);
        Assert.Equal(ItemIconState.FieldUnavailable, icons[8].State);
        Assert.Equal(ItemResultState.Absent, (await items.GetAsync(99, Now, default)).State);
    }

    [Fact]
    public async Task A_list_never_opens_the_files_and_a_build_whose_files_dont_open_is_files_not_open()
    {
        var world = new ItemsWorld().With(ItemTable([Item(1, icon: "800")]), SparseTable([Sparse(1, "Name")]));
        // Tables are seeded; the layer 4 fake has no such build, so its open answers NeedsNetwork.
        await using var items = await world.OpenAsync();
        Assert.Single((await List(items, new())).Entries);
        var icon = (await items.GetAsync(1, Now, default)).Detail!.Icon;
        Assert.Equal((ItemIconState.FilesNotOpen, 800u), (icon.State, icon.FileDataId));
        Assert.NotNull(icon.Code);
    }

    [Fact]
    public async Task An_exact_id_seeks_one_row_without_scanning()
    {
        var world = new ItemsWorld().With(ItemTable([.. Enumerable.Range(1, 100).Select(i => Item((uint)i))]), SparseTable([Sparse(42, "Answer")]));
        await using var items = await world.OpenAsync();
        var before = world.Tables.Calls("PageAsync");
        var page = await List(items, new(ExactId: 42));
        Assert.Equal("Answer", Assert.Single(page.Entries).Name.Value);
        Assert.Equal(before, world.Tables.Calls("PageAsync"));
        Assert.Equal(ItemsPageState.InvalidCursor, (await List(items, new(ExactId: 42, After: (await List(items, new(Limit: 1))).Next))).State);
    }
}
