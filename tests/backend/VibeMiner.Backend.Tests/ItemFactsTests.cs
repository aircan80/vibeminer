using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>item-facts-v1 (#61): the reviewed projection of one item's Item and ItemSparse rows.</summary>
public sealed class ItemFactsTests
{
    private static readonly ItemsContext Context = new("wow", new string('a', 64), "us", "enUS");
    private static readonly string Artifact = new('b', 64);
    private const string Time = "2026-10-19T00:00:00Z";

    // Authored layouts: a Retail-like Sparse with AllowableRaces[2]; a 1.15.9-shaped Classic pair (Item with DamageType[0–4] and
    // RequiredLevel, ItemSparse with AllowableRace[0–1], stat type and amount arrays, Flags[5], one DamageType and RequiredLevel);
    // and a minimal layout with one unarrayed AllowableRace column.
    private static readonly string[] ItemColumns = ["ID", "ClassID", "SubclassID", "InventoryType", "IconFileDataID", "ContentTuningID"];
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

    private static TableDataSnapshot Source(string tableId, string[] columns, string scope = "complete")
    {
        var metadata = new MetadataReceipt("encoding", new string('c', 32), new string('d', 32), new string('e', 64),
            "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", Time, Time, "authored-v1");
        return new TableDataSnapshot(Context.ProductId, Context.BuildIdentity, Context.SourceRegion, tableId, Context.ResearchLocale,
            new string('c', 64), Time, "996192AA", new string('f', 64), new string('a', 40), "authored-db2-v1",
            new(new string('c', 32), new string('d', 32), new string('e', 64), "10", "10", metadata.SourceUrl,
                Time, Time, "authored-v1", new("loose", "0", "10", null)),
            new("841626", "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []),
            columns.Select(name => new TableDataColumn(name, name.EndsWith("_lang", StringComparison.Ordinal) ? "string" : "integer", name == "ID", null)).ToArray(),
            new(scope, "0", "0", "0", scope == "complete" ? "0" : "2", "0", "0", "0", "0"), "0");
    }
    private static ItemsSnapshot Snapshot(string[] sparseColumns, string scope = "complete", string[]? itemColumns = null) =>
        new(Context, Artifact, Time, "items-projection-v2", "ordinal-ignorecase-contains-v1",
            Source("50238ec2", itemColumns ?? ItemColumns), Source("919be54e", sparseColumns, scope) with { ArtifactId = new string('d', 64) },
            new("0", "0", "0", "0", "0", "0", "0", "0"), []);
    // 19019-shaped Classic rows (authored values): stat types 3, 7 with flat amounts 5, 8; Item.DamageType[1] 3, ItemSparse.DamageType 0.
    private static ItemDetail ClassicDetail(string itemLevel = "60", string sparseLevel = "60")
    {
        var item = new TableDataRow("19019", false, null, ["19019", "2", "7", "13", "800001", itemLevel, "0", "3", "0", "0", "0"]);
        var stats = new[] { "3", "7", "-1", "-1", "-1", "-1", "-1", "-1", "-1", "-1" };
        var amounts = new[] { "5", "8", "0", "0", "0", "0", "0", "0", "0", "0" };
        var sparse = new TableDataRow("19019", false, null,
            ["19019", "", "Authored blade", "5", "80", "13", sparseLevel, "1", "-1", "0", "0", .. stats, .. amounts, "0", "0", "0", "0", "0"]);
        return new(new("19019", new(ItemNameState.Valid, "Authored blade"), [], "800001"), item, sparse);
    }
    private static readonly TableDataRow ItemRow = new("900003", false, null, ["900003", "4", "2", "5", "800003", "0"]);
    private static ItemDetail Detail(TableDataRow? sparse) =>
        new(new("900003", new(ItemNameState.Valid, "Authored coat"), [], "800003"), ItemRow, sparse);
    private static TableDataRow RetailRow(string inventoryType = "5", string quality = "3", string bonding = "2", IReadOnlyList<TableTextIssue>? issues = null) =>
        new("900003", false, null, ["900003", "Authored text", "Authored coat", quality, "45", inventoryType, "0", bonding, "-1", "-2", "7", "4", "-1", "9650"], issues);
    private static ItemFact Fact(IReadOnlyList<ItemFact> facts, string key) => Assert.Single(facts, fact => fact.Key == key);

    [Fact]
    public void Keys_follow_the_wire_order_and_every_value_names_its_exact_source_cell()
    {
        var facts = ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow()));
        Assert.Equal(ItemFacts.Keys.Select(key => key.Key), facts.Select(fact => fact.Key));
        Assert.Equal(48, facts.Count);
        var level = Fact(facts, "itemLevel");
        Assert.Equal("value", level.State); Assert.Equal("single", level.Agreement);
        var cell = Assert.Single(level.Values);
        Assert.Equal(("919be54e", "ItemSparse", "ItemLevel", 4, "900003", "45"), (cell.TableId, cell.Table, cell.Column, cell.ColumnIndex, cell.RecordId, cell.Value));
        Assert.Null(cell.Label);
        var cls = Assert.Single(Fact(facts, "class").Values);
        Assert.Equal(("50238ec2", "Item", "ClassID", 1, "4"), (cls.TableId, cls.Table, cls.Column, cls.ColumnIndex, cls.Value));
    }

    [Fact]
    public void Item_and_ItemSparse_values_of_one_meaning_agree_or_disagree_and_both_stay()
    {
        var agree = Fact(ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow())), "inventoryType");
        Assert.Equal("agree", agree.Agreement);
        Assert.Equal(new[] { "Item", "ItemSparse" }, agree.Values.Select(value => value.Table));
        Assert.All(agree.Values, value => Assert.Equal("Chest", value.Label));
        var disagree = Fact(ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow(inventoryType: "21"))), "inventoryType");
        Assert.Equal("disagree", disagree.Agreement);
        Assert.Equal(new[] { "5", "21" }, disagree.Values.Select(value => value.Value));
        Assert.Equal(new[] { "Chest", "Main hand" }, disagree.Values.Select(value => value.Label));
    }

    [Fact]
    public void Absent_sparse_row_is_missing_with_complete_scope_and_unknown_with_partial_scope()
    {
        var missing = ItemFacts.Project(Snapshot(RetailSparse), Detail(null));
        Assert.Equal("missing", Fact(missing, "quality").State); Assert.Empty(Fact(missing, "quality").Values);
        Assert.Equal("missing", Fact(missing, "description").State);
        var inventory = Fact(missing, "inventoryType");
        Assert.Equal("value", inventory.State); Assert.Equal("single", inventory.Agreement);
        Assert.Equal("Item", Assert.Single(inventory.Values).Table);
        var unknown = ItemFacts.Project(Snapshot(RetailSparse, "unencryptedSections"), Detail(null));
        Assert.Equal("unknown", Fact(unknown, "quality").State); Assert.Empty(Fact(unknown, "sellPrice").Values);
        Assert.Equal("missing", ItemFacts.SparseState(Snapshot(RetailSparse), Detail(null)));
        Assert.Equal("unknown", ItemFacts.SparseState(Snapshot(RetailSparse, "unencryptedSections"), Detail(null)));
        Assert.Equal("present", ItemFacts.SparseState(Snapshot(RetailSparse), Detail(RetailRow())));
    }

    [Fact]
    public void Fields_absent_from_both_admitted_layouts_are_unsupported_not_zero()
    {
        var facts = ItemFacts.Project(Snapshot(SingleRaceSparse), Detail(new("900003", false, null, ["900003", "", "Authored coat", "3", "45", "5", "2", "-1", "9650"])));
        foreach (var key in new[] { "statPercentEditor", "playerLevelCurve", "flags", "statType" })
        { var fact = Fact(facts, key); Assert.Equal("unsupported", fact.State); Assert.Empty(fact.Values); }
        Assert.Equal("value", Fact(facts, "description").State);
        Assert.Equal("", Assert.Single(Fact(facts, "description").Values).Value);
    }

    [Fact]
    public void Arrays_keep_index_order_and_Retail_AllowableRaces_Classic_AllowableRace_arrays_and_a_single_column_all_map_to_races()
    {
        var retail = ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow()));
        Assert.Equal(new[] { "AllowableRaces[0]", "AllowableRaces[1]" }, Fact(retail, "allowableRaces").Values.Select(value => value.Column));
        Assert.Equal(new[] { "-1", "-2" }, Fact(retail, "allowableRaces").Values.Select(value => value.Value));
        Assert.Equal(new[] { 10, 11, 12 }, Fact(retail, "statType").Values.Select(value => value.ColumnIndex));
        Assert.Equal(new[] { "7", "4", "-1" }, Fact(retail, "statType").Values.Select(value => value.Value));
        var single = ItemFacts.Project(Snapshot(SingleRaceSparse), Detail(new("900003", false, null, ["900003", "x", "y", "3", "45", "5", "2", "-1", "9650"])));
        Assert.Equal("AllowableRace", Assert.Single(Fact(single, "allowableRaces").Values).Column);
        var classic = ItemFacts.Project(Snapshot(ClassicSparse, itemColumns: ClassicItem), ClassicDetail());
        Assert.Equal(new[] { "AllowableRace[0]", "AllowableRace[1]" }, Fact(classic, "allowableRaces").Values.Select(value => value.Column));
        Assert.Equal(5, Fact(classic, "flags").Values.Count);
    }

    [Theory]
    [InlineData("quality", "8", "WoW Token")]
    [InlineData("quality", "11", null)]
    [InlineData("bonding", "4", "Quest item")]
    [InlineData("bonding", "8", null)]
    public void Labels_come_only_from_the_reviewed_lists_and_only_for_listed_values(string key, string value, string? label)
    {
        var row = key == "quality" ? RetailRow(quality: value) : RetailRow(bonding: value);
        var facts = ItemFacts.Project(Snapshot(RetailSparse), Detail(row));
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
            Assert.Equal(Enumerable.Range(0, count).Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)), list.Select(label => label.Value));
        Assert.Equal("Profession gear", lists.InventoryType[30].Label);
        Assert.Equal(new[] { "On use", "On equip", "Chance on hit" }, lists.Trigger.Select(label => label.Label));
        Assert.All(lists.Quality, label => Assert.Equal(label.Label, ItemFacts.Quality[label.Value]));
        Assert.DoesNotContain(lists.Bonding, label => label.Value == "8");
    }

    [Fact]
    public void Invalid_text_is_its_own_state_and_never_an_empty_string_value()
    {
        var facts = ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow(issues: [new(1, "DB2_TEXT_ENCODING_INVALID", "c328")])));
        var description = Fact(facts, "description");
        Assert.Equal("invalidText", description.State);
        Assert.Equal("", Assert.Single(description.Values).Value);
        Assert.Equal("value", Fact(facts, "itemLevel").State);
    }

    [Fact]
    public async Task Service_refuses_a_stale_artifact_and_returns_null_facts_outside_the_admitted_scope()
    {
        var store = new Store(Snapshot(RetailSparse)); await using var service = new ItemsService(store);
        var stale = await Assert.ThrowsAsync<DiscoveryException>(() => service.FactsAsync(new(Context, new string('e', 64), "900003")));
        Assert.Equal("ITEMS_ARTIFACT_STALE", stale.Code); Assert.Equal(0, store.Inspects);
        store.Detail = null;
        var outside = await service.FactsAsync(new(Context, Artifact, "77"));
        Assert.Null(outside.Facts); Assert.Equal("item-facts-v1", outside.PolicyRevision); Assert.Equal("item-labels-v1", outside.LabelRevision);
        store.Detail = Detail(RetailRow());
        var found = await service.FactsAsync(new(Context, Artifact, "900003"));
        Assert.Equal("present", found.Sparse); Assert.Equal(48, found.Facts!.Count);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.FactsAsync(new(Context, Artifact, "01")));
    }

    [Fact]
    public void Classic_flat_stat_amounts_are_their_own_fact_beside_the_stat_types()
    {
        var facts = ItemFacts.Project(Snapshot(ClassicSparse, itemColumns: ClassicItem), ClassicDetail());
        var keys = facts.Select(fact => fact.Key).ToList();
        Assert.Equal(keys.IndexOf("statType") + 1, keys.IndexOf("statAmount"));
        Assert.Equal(new[] { "3", "7" }, Fact(facts, "statType").Values.Take(2).Select(value => value.Value));
        var amount = Fact(facts, "statAmount");
        Assert.Equal("value", amount.State); Assert.Equal("single", amount.Agreement);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"StatModifier_bonusAmount[{i}]"), amount.Values.Select(value => value.Column));
        Assert.Equal(new[] { "5", "8" }, amount.Values.Take(2).Select(value => value.Value));
        Assert.Equal("unsupported", Fact(facts, "statPercentEditor").State);
        Assert.Equal("unsupported", Fact(ItemFacts.Project(Snapshot(RetailSparse), Detail(RetailRow())), "statAmount").State);
    }

    [Fact]
    public void Item_and_ItemSparse_copies_of_different_shapes_stay_separate_and_equal_shapes_still_compare()
    {
        var facts = ItemFacts.Project(Snapshot(ClassicSparse, itemColumns: ClassicItem), ClassicDetail());
        var damage = Fact(facts, "damageType");
        Assert.Equal("value", damage.State); Assert.Equal("separate", damage.Agreement);
        Assert.Equal(new[] { "Item", "Item", "Item", "Item", "Item", "ItemSparse" }, damage.Values.Select(value => value.Table));
        Assert.Equal(new[] { "DamageType[0]", "DamageType[1]", "DamageType[2]", "DamageType[3]", "DamageType[4]", "DamageType" }, damage.Values.Select(value => value.Column));
        Assert.Equal(new[] { "0", "3", "0", "0", "0", "0" }, damage.Values.Select(value => value.Value));
        // Classic RequiredLevel is in both tables with one cell each: it agrees or disagrees like InventoryType.
        Assert.Equal("agree", Fact(facts, "requiredLevel").Agreement);
        var differing = Fact(ItemFacts.Project(Snapshot(ClassicSparse, itemColumns: ClassicItem), ClassicDetail(sparseLevel: "58")), "requiredLevel");
        Assert.Equal("disagree", differing.Agreement); Assert.Equal(new[] { "60", "58" }, differing.Values.Select(value => value.Value));
        // Without the ItemSparse row only Item's copy is left: single.
        Assert.Equal("single", Fact(ItemFacts.Project(Snapshot(ClassicSparse, itemColumns: ClassicItem), ClassicDetail() with { SparseRow = null }), "damageType").Agreement);
    }

    [Fact]
    public void A_fact_with_more_than_sixteen_cells_fails_instead_of_truncating()
    {
        string[] sparse = ["ID", .. Enumerable.Range(0, 17).Select(i => $"Flags[{i}]")];
        var row = new TableDataRow("900003", false, null, ["900003", .. Enumerable.Repeat("0", 17)]);
        var error = Assert.Throws<DiscoveryException>(() => ItemFacts.Project(Snapshot(sparse), Detail(row)));
        Assert.Equal("ITEMS_FACTS_LIMIT", error.Code);
    }

    [Fact]
    public async Task Service_refuses_a_reply_over_the_items_budget_without_truncating()
    {
        var huge = new string('x', 600 << 10);
        var store = new Store(Snapshot(RetailSparse)) { Detail = Detail(new("900003", false, null, ["900003", huge, "Authored coat", "3", "45", "5", "0", "2", "-1", "-2", "7", "4", "-1", "9650"])) };
        await using var service = new ItemsService(store);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => service.FactsAsync(new(Context, Artifact, "900003")));
        Assert.Equal("ITEMS_FACTS_LIMIT", error.Code);
        store.Detail = Detail(RetailRow());
        Assert.NotNull((await service.FactsAsync(new(Context, Artifact, "900003"))).Facts);
    }

    [Fact]
    public async Task Outside_the_Item_scope_the_ItemSparse_row_is_looked_up_not_assumed_absent()
    {
        var store = new Store(Snapshot(RetailSparse)) { Detail = null, SparseRow = true }; await using var service = new ItemsService(store);
        var sparseOnly = await service.FactsAsync(new(Context, Artifact, "77"));
        Assert.Null(sparseOnly.Facts); Assert.Equal("present", sparseOnly.Sparse); Assert.Equal(1, store.SparseLookups);
        store.SparseRow = false;
        Assert.Equal("missing", (await service.FactsAsync(new(Context, Artifact, "77"))).Sparse);
        var partial = new Store(Snapshot(RetailSparse, "unencryptedSections")) { Detail = null }; await using var partialService = new ItemsService(partial);
        Assert.Equal("unknown", (await partialService.FactsAsync(new(Context, Artifact, "77"))).Sparse);
        store.Detail = Detail(RetailRow()); var lookups = store.SparseLookups;
        await service.FactsAsync(new(Context, Artifact, "900003")); Assert.Equal(lookups, store.SparseLookups);
    }

    [Fact]
    public async Task Host_advertises_item_facts_and_routes_exact_parameters_only()
    {
        var store = new Store(Snapshot(RetailSparse)) { Detail = Detail(RetailRow()) }; await using var items = new ItemsService(store);
        var scope = new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale, artifactId = Artifact, itemId = "900003" };
        var replies = await Run(items, new { protocolVersion = 1, id = "hello", method = "hello", @params = new { } },
            new { protocolVersion = 1, id = "facts", method = "items.facts", @params = scope },
            new { protocolVersion = 1, id = "extra", method = "items.facts", @params = new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale, artifactId = Artifact, itemId = "900003", columns = "all" } },
            new { protocolVersion = 1, id = "labels", method = "items.labels", @params = new { } },
            new { protocolVersion = 1, id = "labelsExtra", method = "items.labels", @params = new { locale = "deDE" } });
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "item-facts-v1");
        var result = replies["facts"].GetProperty("result");
        Assert.Equal("present", result.GetProperty("sparse").GetString());
        var quality = result.GetProperty("facts").EnumerateArray().Single(fact => fact.GetProperty("key").GetString() == "quality");
        var value = quality.GetProperty("values")[0];
        Assert.Equal("Rare", value.GetProperty("label").GetString());
        Assert.Equal("OverallQualityID", value.GetProperty("column").GetString());
        Assert.Equal(3, value.GetProperty("columnIndex").GetInt32());
        Assert.False(result.TryGetProperty("triggerLabels", out _));
        Assert.Equal("INVALID_REQUEST", replies["extra"].GetProperty("error").GetProperty("code").GetString());
        var labels = replies["labels"].GetProperty("result");
        Assert.Equal("item-labels-v1", labels.GetProperty("labelRevision").GetString());
        Assert.Equal(9, labels.GetProperty("quality").GetArrayLength());
        Assert.Equal("WoW Token", labels.GetProperty("quality")[8].GetProperty("label").GetString());
        Assert.Equal("8", labels.GetProperty("quality")[8].GetProperty("value").GetString());
        Assert.Equal("On use", labels.GetProperty("trigger")[0].GetProperty("label").GetString());
        Assert.Equal("INVALID_REQUEST", replies["labelsExtra"].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, store.Inspects);
        var none = await Run(null, new { protocolVersion = 1, id = "hello", method = "hello", @params = new { } },
            new { protocolVersion = 1, id = "facts", method = "items.facts", @params = scope });
        Assert.DoesNotContain(none["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "item-facts-v1");
        Assert.Equal("ITEMS_UNAVAILABLE", none["facts"].GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<Dictionary<string, JsonElement>> Run(ItemsService? items, params object[] requests)
    {
        var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), items: items);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, json))) + "\n"));
        using var output = new MemoryStream(); await host.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(reply => reply.GetProperty("id").GetString()!);
    }
    private sealed class Store(ItemsSnapshot snapshot) : IItemsStore
    {
        internal int Inspects, SparseLookups; internal ItemDetail? Detail; internal bool SparseRow;
        public Task<bool> SparseRowExistsAsync(ItemInspectRequest request, CancellationToken token) { SparseLookups++; return Task.FromResult(SparseRow); }
        public Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token) => Task.FromResult(new ItemsReadiness(snapshot, []));
        public Task<ItemsSnapshot> PrepareAsync(ItemsContext context, Func<ItemsPreparationProgress, Task> progress, CancellationToken token) =>
            throw new InvalidOperationException("Facts never prepare data.");
        public Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token) => throw new InvalidOperationException();
        public Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token)
        { Inspects++; return Task.FromResult(new ItemInspectResult(request.Context, request.ArtifactId, request.ItemId, Detail)); }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
