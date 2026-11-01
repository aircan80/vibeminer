using System.Globalization;
using VibeMiner.Concepts.Items;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// item-facts-v1 (D098, D454), ported from the old ItemFactsTests to <see cref="ItemsService.GetAsync"/> over the layer 5 fakes.
/// The authored layouts and rows are the old ones. Synthetic data only.
/// </summary>
public sealed class ItemFactsTests
{
    // Authored layouts: a Retail-like Sparse with AllowableRaces[2]; a 1.15.9-shaped Classic pair (Item with DamageType[0-4] and
    // RequiredLevel, ItemSparse with AllowableRace[0-1], stat type and amount arrays, Flags[5], one DamageType and RequiredLevel);
    // and a minimal layout with one unarrayed AllowableRace column.
    private static readonly string[] ItemNames = ["ID", "ClassID", "SubclassID", "InventoryType", "IconFileDataID", "ContentTuningID"];
    private static readonly string[] RetailSparse = ["ID", "Description_lang", "Display_lang", "OverallQualityID", "ItemLevel", "InventoryType",
        "ContentTuningID", "Bonding", "AllowableRaces[0]", "AllowableRaces[1]", "StatModifier_bonusStat[0]", "StatModifier_bonusStat[1]",
        "StatModifier_bonusStat[2]", "SellPrice"];
    private static readonly string[] SingleRaceSparse = ["ID", "Description_lang", "Display_lang", "OverallQualityID", "ItemLevel", "InventoryType",
        "Bonding", "AllowableRace", "SellPrice"];
    private static readonly string[] ClassicItem = ["ID", "ClassID", "SubclassID", "InventoryType", "IconFileDataID", "RequiredLevel",
        .. Enumerable.Range(0, 5).Select(i => $"DamageType[{i}]")];
    private static readonly string[] ClassicSparse = ["ID", "Description_lang", "Display_lang", "OverallQualityID", "ItemLevel", "InventoryType",
        "RequiredLevel", "Bonding", "AllowableRace[0]", "AllowableRace[1]", "DamageType",
        .. Enumerable.Range(0, 10).Select(i => $"StatModifier_bonusStat[{i}]"), .. Enumerable.Range(0, 10).Select(i => $"StatModifier_bonusAmount[{i}]"),
        .. Enumerable.Range(0, 5).Select(i => $"Flags[{i}]")];

    private static Db2Column[] Columns(string[] names) =>
        [.. names.Select(name => new Db2Column(name, name.EndsWith("_lang", StringComparison.Ordinal) ? "string" : "integer", name == "ID"))];

    private static readonly Db2Row ItemRow = new(900003, false, null, ["900003", "4", "2", "5", "800003", "0"]);

    private static Db2Row RetailRow(string inventoryType = "5", string quality = "3", string bonding = "2", IReadOnlyList<Db2TextIssue>? issues = null) =>
        new(900003, false, null, ["900003", "Authored text", "Authored coat", quality, "45", inventoryType, "0", bonding, "-1", "-2", "7", "4", "-1", "9650"], issues);

    // 19019-shaped Classic rows (authored values): stat types 3, 7 with flat amounts 5, 8; Item.DamageType[1] 3, ItemSparse.DamageType 0.
    private static Db2Row ClassicItemRow(string level = "60") => new(19019, false, null, ["19019", "2", "7", "13", "800001", level, "0", "3", "0", "0", "0"]);

    private static Db2Row ClassicSparseRow(string level = "60")
    {
        var stats = new[] { "3", "7", "-1", "-1", "-1", "-1", "-1", "-1", "-1", "-1" };
        var amounts = new[] { "5", "8", "0", "0", "0", "0", "0", "0", "0", "0" };
        return new(19019, false, null, ["19019", "", "Authored blade", "5", "80", "13", level, "1", "-1", "0", "0", .. stats, .. amounts, "0", "0", "0", "0", "0"]);
    }

    private static async Task<ItemFactSet> FactsAsync(string[] sparseNames, Db2Row? sparse, TableState sparseState = TableState.Ready,
        string[]? itemNames = null, Db2Row? item = null)
    {
        item ??= ItemRow;
        var world = new ItemsWorld().With(ItemTable([item], columns: Columns(itemNames ?? ItemNames)),
            SparseTable(sparse is null ? [] : [sparse], sparseState, Columns(sparseNames)));
        await using var items = await world.OpenAsync();
        var result = await items.GetAsync(item.Id, TablePriority.Interactive, default);
        Assert.Equal(ItemResultState.Found, result.State);
        return result.Detail!.Facts;
    }

    private static ItemFact Fact(ItemFactSet facts, string key) => Assert.Single(facts.Facts, fact => fact.Key == key);

    [Fact]
    public async Task Keys_follow_the_wire_order_and_every_value_names_its_exact_source_cell()
    {
        var facts = await FactsAsync(RetailSparse, RetailRow());
        Assert.Equal(ItemFactsState.Ready, facts.State);
        Assert.Equal(ItemFacts.Keys.Select(key => key.Key), facts.Facts.Select(fact => fact.Key));
        Assert.Equal(48, facts.Facts.Count);
        var level = Fact(facts, "itemLevel");
        Assert.Equal((ItemFactState.Value, ItemFactAgreement.Single), (level.State, level.Agreement));
        var cell = Assert.Single(level.Values);
        Assert.Equal(("ItemSparse", "ItemLevel", 4, 900003u, "45"), (cell.Table, cell.Column, cell.ColumnIndex, cell.RowId, cell.Value));
        Assert.Null(cell.Label);
        var cls = Assert.Single(Fact(facts, "class").Values);
        Assert.Equal(("Item", "ClassID", 1, "4"), (cls.Table, cls.Column, cls.ColumnIndex, cls.Value));
    }

    [Fact]
    public async Task Item_and_ItemSparse_values_of_one_meaning_agree_or_disagree_and_both_stay()
    {
        var agree = Fact(await FactsAsync(RetailSparse, RetailRow()), "inventoryType");
        Assert.Equal(ItemFactAgreement.Agree, agree.Agreement);
        Assert.Equal(["Item", "ItemSparse"], agree.Values.Select(value => value.Table));
        Assert.All(agree.Values, value => Assert.Equal("Chest", value.Label));
        var disagree = Fact(await FactsAsync(RetailSparse, RetailRow(inventoryType: "21")), "inventoryType");
        Assert.Equal(ItemFactAgreement.Disagree, disagree.Agreement);
        Assert.Equal(["5", "21"], disagree.Values.Select(value => value.Value));
        Assert.Equal(["Chest", "Main hand"], disagree.Values.Select(value => value.Label));
    }

    // Was Absent_sparse_row_is_missing_with_complete_scope_and_unknown_with_partial_scope: complete is a Ready table, partial a
    // Partial one; a keyed row whose key isn't known is its own Protected state.
    [Fact]
    public async Task Absent_sparse_row_is_missing_in_a_ready_table_unknown_in_a_partial_one_and_protected_under_a_key()
    {
        var missing = await FactsAsync(RetailSparse, null);
        Assert.Equal(ItemSparseState.Missing, missing.Sparse);
        Assert.Equal(ItemFactState.Missing, Fact(missing, "quality").State); Assert.Empty(Fact(missing, "quality").Values);
        Assert.Equal(ItemFactState.Missing, Fact(missing, "description").State);
        var inventory = Fact(missing, "inventoryType");
        Assert.Equal((ItemFactState.Value, ItemFactAgreement.Single), (inventory.State, inventory.Agreement));
        Assert.Equal("Item", Assert.Single(inventory.Values).Table);
        var unknown = await FactsAsync(RetailSparse, null, TableState.Partial);
        Assert.Equal(ItemSparseState.Unknown, unknown.Sparse);
        Assert.Equal(ItemFactState.Unknown, Fact(unknown, "quality").State); Assert.Empty(Fact(unknown, "sellPrice").Values);
        Assert.Equal(ItemSparseState.Present, (await FactsAsync(RetailSparse, RetailRow())).Sparse);

        var world = new ItemsWorld().With(ItemTable([ItemRow], columns: Columns(ItemNames)),
            SparseTable([], TableState.Partial, Columns(RetailSparse), new Dictionary<uint, string> { [900003] = Key }));
        await using var items = await world.OpenAsync();
        var keyed = (await items.GetAsync(900003, TablePriority.Interactive, default)).Detail!;
        Assert.Equal(ItemSparseState.Protected, keyed.Facts.Sparse);
        Assert.Equal(ItemFactState.Protected, Fact(keyed.Facts, "quality").State);
        Assert.Equal(Key, keyed.Summary.SparseKey);
    }

    [Fact]
    public async Task An_unreadable_sparse_makes_its_fields_unknown_never_unsupported()
    {
        var facts = await FactsAsync(RetailSparse, null, TableState.NeedsNetwork);
        Assert.Equal(ItemSparseState.Unknown, facts.Sparse);
        Assert.Equal(ItemFactState.Unknown, Fact(facts, "statPercentEditor").State);
        Assert.Equal(ItemFactState.Value, Fact(facts, "class").State);
    }

    [Fact]
    public async Task Fields_absent_from_both_admitted_layouts_are_unsupported_not_zero()
    {
        var facts = await FactsAsync(SingleRaceSparse, new(900003, false, null, ["900003", "", "Authored coat", "3", "45", "5", "2", "-1", "9650"]));
        foreach (var key in new[] { "statPercentEditor", "playerLevelCurve", "flags", "statType" })
        {
            var fact = Fact(facts, key);
            Assert.Equal(ItemFactState.Unsupported, fact.State); Assert.Empty(fact.Values);
        }
        Assert.Equal(ItemFactState.Value, Fact(facts, "description").State);
        Assert.Equal("", Assert.Single(Fact(facts, "description").Values).Value);
    }

    [Fact]
    public async Task Arrays_keep_index_order_and_Retail_AllowableRaces_Classic_AllowableRace_arrays_and_a_single_column_all_map_to_races()
    {
        var retail = await FactsAsync(RetailSparse, RetailRow());
        Assert.Equal(["AllowableRaces[0]", "AllowableRaces[1]"], Fact(retail, "allowableRaces").Values.Select(value => value.Column));
        Assert.Equal(["-1", "-2"], Fact(retail, "allowableRaces").Values.Select(value => value.Value));
        Assert.Equal([10, 11, 12], Fact(retail, "statType").Values.Select(value => value.ColumnIndex));
        Assert.Equal(["7", "4", "-1"], Fact(retail, "statType").Values.Select(value => value.Value));
        var single = await FactsAsync(SingleRaceSparse, new(900003, false, null, ["900003", "x", "y", "3", "45", "5", "2", "-1", "9650"]));
        Assert.Equal("AllowableRace", Assert.Single(Fact(single, "allowableRaces").Values).Column);
        var classic = await FactsAsync(ClassicSparse, ClassicSparseRow(), itemNames: ClassicItem, item: ClassicItemRow());
        Assert.Equal(["AllowableRace[0]", "AllowableRace[1]"], Fact(classic, "allowableRaces").Values.Select(value => value.Column));
        Assert.Equal(5, Fact(classic, "flags").Values.Count);
    }

    [Theory]
    [InlineData("quality", "8", "WoW Token")]
    [InlineData("quality", "11", null)]
    [InlineData("bonding", "4", "Quest item")]
    [InlineData("bonding", "8", null)]
    public async Task Labels_come_only_from_the_reviewed_lists_and_only_for_listed_values(string key, string value, string? label)
    {
        var facts = await FactsAsync(RetailSparse, key == "quality" ? RetailRow(quality: value) : RetailRow(bonding: value));
        Assert.Equal(label, Assert.Single(Fact(facts, key).Values).Label);
        Assert.All(Fact(facts, "sellPrice").Values, cell => Assert.Null(cell.Label));
        Assert.All(Fact(facts, "class").Values, cell => Assert.Null(cell.Label));
    }

    [Fact]
    public void Label_lists_are_ascending_canonical_values_and_the_one_source_for_fact_labels()
    {
        var lists = ItemFacts.LabelLists;
        Assert.Equal("item-labels-v1", lists.LabelRevision);
        foreach (var (list, count) in new[] { (lists.Quality, 9), (lists.InventoryType, 31), (lists.Bonding, 5), (lists.Trigger, 3) })
            Assert.Equal(Enumerable.Range(0, count).Select(value => value.ToString(CultureInfo.InvariantCulture)), list.Select(label => label.Value));
        Assert.Equal("Profession gear", lists.InventoryType[30].Label);
        Assert.Equal(["On use", "On equip", "Chance on hit"], lists.Trigger.Select(label => label.Label));
        Assert.All(lists.Quality, label => Assert.Equal(label.Label, ItemFacts.Quality[label.Value]));
        Assert.DoesNotContain(lists.Bonding, label => label.Value == "8");
    }

    [Fact]
    public async Task Invalid_text_is_its_own_state_and_never_an_empty_string_value()
    {
        var facts = await FactsAsync(RetailSparse, RetailRow(issues: [new(1, "DB2_TEXT_ENCODING_INVALID", "c328")]));
        var description = Fact(facts, "description");
        Assert.Equal(ItemFactState.InvalidText, description.State);
        var cell = Assert.Single(description.Values);
        Assert.Equal("", cell.Value); Assert.Null(cell.Label);
        Assert.Equal(ItemFactState.Value, Fact(facts, "itemLevel").State);
    }

    [Fact]
    public async Task Classic_flat_stat_amounts_are_their_own_fact_beside_the_stat_types()
    {
        var facts = await FactsAsync(ClassicSparse, ClassicSparseRow(), itemNames: ClassicItem, item: ClassicItemRow());
        var keys = facts.Facts.Select(fact => fact.Key).ToList();
        Assert.Equal(keys.IndexOf("statType") + 1, keys.IndexOf("statAmount"));
        Assert.Equal(["3", "7"], Fact(facts, "statType").Values.Take(2).Select(value => value.Value));
        var amount = Fact(facts, "statAmount");
        Assert.Equal((ItemFactState.Value, ItemFactAgreement.Single), (amount.State, amount.Agreement));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"StatModifier_bonusAmount[{i}]"), amount.Values.Select(value => value.Column));
        Assert.Equal(["5", "8"], amount.Values.Take(2).Select(value => value.Value));
        Assert.Equal(ItemFactState.Unsupported, Fact(facts, "statPercentEditor").State);
        Assert.Equal(ItemFactState.Unsupported, Fact(await FactsAsync(RetailSparse, RetailRow()), "statAmount").State);
    }

    [Fact]
    public async Task Item_and_ItemSparse_copies_of_different_shapes_stay_separate_and_equal_shapes_still_compare()
    {
        var facts = await FactsAsync(ClassicSparse, ClassicSparseRow(), itemNames: ClassicItem, item: ClassicItemRow());
        var damage = Fact(facts, "damageType");
        Assert.Equal((ItemFactState.Value, ItemFactAgreement.Separate), (damage.State, damage.Agreement));
        Assert.Equal(["Item", "Item", "Item", "Item", "Item", "ItemSparse"], damage.Values.Select(value => value.Table));
        Assert.Equal(["DamageType[0]", "DamageType[1]", "DamageType[2]", "DamageType[3]", "DamageType[4]", "DamageType"], damage.Values.Select(value => value.Column));
        Assert.Equal(["0", "3", "0", "0", "0", "0"], damage.Values.Select(value => value.Value));
        // Classic RequiredLevel is in both tables with one cell each: it agrees or disagrees like InventoryType.
        Assert.Equal(ItemFactAgreement.Agree, Fact(facts, "requiredLevel").Agreement);
        var differing = Fact(await FactsAsync(ClassicSparse, ClassicSparseRow("58"), itemNames: ClassicItem, item: ClassicItemRow()), "requiredLevel");
        Assert.Equal(ItemFactAgreement.Disagree, differing.Agreement); Assert.Equal(["60", "58"], differing.Values.Select(value => value.Value));
        // Without the ItemSparse row only Item's copy is left: single.
        Assert.Equal(ItemFactAgreement.Single, Fact(await FactsAsync(ClassicSparse, null, itemNames: ClassicItem, item: ClassicItemRow()), "damageType").Agreement);
    }

    // Was A_fact_with_more_than_sixteen_cells_fails_instead_of_truncating: the cap is now a named state of the facts, and the rest
    // of the detail still answers. Display_lang is added so the layout names items (the old test read facts without a name).
    [Fact]
    public async Task A_fact_with_more_than_sixteen_cells_is_a_limit_state_instead_of_truncating()
    {
        string[] sparse = ["ID", "Display_lang", .. Enumerable.Range(0, 17).Select(i => $"Flags[{i}]")];
        var row = new Db2Row(900003, false, null, ["900003", "Coat", .. Enumerable.Repeat("0", 17)]);
        var facts = await FactsAsync(sparse, row);
        Assert.Equal((ItemFactsState.LimitExceeded, ItemFacts.LimitCode), (facts.State, facts.Code));
        Assert.Empty(facts.Facts);
        string[] sixteen = ["ID", "Display_lang", .. Enumerable.Range(0, 16).Select(i => $"Flags[{i}]")];
        var fits = await FactsAsync(sixteen, new(900003, false, null, ["900003", "Coat", .. Enumerable.Repeat("0", 16)]));
        Assert.Equal(16, Fact(fits, "flags").Values.Count);
    }
}
