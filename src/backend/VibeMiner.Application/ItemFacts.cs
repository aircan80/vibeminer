using System.Collections.Frozen;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// item-facts-v1: a reviewed, engine-owned projection of one item's admitted Item and ItemSparse rows (#61).
/// Every value keeps its exact source cell. Nothing is calculated, scaled or inferred; labels come only from the
/// reviewed lists below (item-labels-v1) and only for exact listed values.
/// </summary>
public static class ItemFacts
{
    public const string PolicyRevision = "item-facts-v1";
    public const string LabelRevision = "item-labels-v1";
    public const string ItemTable = "50238ec2";
    public const string SparseTable = "919be54e";
    public const int MaximumValues = 16;
    /// <summary>The complete reply shares the 512 KiB Items response budget, less the same conservative envelope reserve
    /// as items.inspect (escaped request ID and wrapper).</summary>
    public const int MaximumResultBytes = (512 << 10) - 2048;
    private static readonly System.Text.Json.JsonSerializerOptions BudgetJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>Returns the result when its serialized form fits the reply budget; never truncates.</summary>
    public static ItemFactsResult Bounded(ItemFactsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result, BudgetJson).Length > MaximumResultBytes)
            throw new DiscoveryException("ITEMS_FACTS_LIMIT", "The item facts exceed the bounded Items reply. Inspect the source tables separately.");
        return result;
    }

    /// <summary>Fact keys in wire order, each with the source field names (any of them, in either table) it reads.</summary>
    public static IReadOnlyList<(string Key, string[] Fields)> Keys { get; } =
    [
        ("description", ["Description_lang"]), ("display1", ["Display1_lang"]), ("display2", ["Display2_lang"]),
        ("display3", ["Display3_lang"]), ("icon", ["IconFileDataID"]),
        ("quality", ["OverallQualityID"]), ("itemLevel", ["ItemLevel"]), ("itemSquishEra", ["ItemSquishEraID"]),
        ("contentTuning", ["ContentTuningID"]), ("playerLevelCurve", ["PlayerLevelToItemLevelCurveID"]),
        ("itemLevelOffsetCurve", ["ItemLevelOffsetCurveID"]), ("itemLevelOffset", ["ItemLevelOffsetItemLevel"]),
        ("class", ["ClassID"]), ("subclass", ["SubclassID"]), ("inventoryType", ["InventoryType"]),
        ("requiredLevel", ["RequiredLevel"]), ("requiredSkill", ["RequiredSkill"]), ("requiredSkillRank", ["RequiredSkillRank"]),
        ("requiredAbility", ["RequiredAbility"]), ("minFaction", ["MinFactionID"]), ("minReputation", ["MinReputation"]),
        ("allowableClass", ["AllowableClass"]), ("allowableRaces", ["AllowableRaces", "AllowableRace"]),
        ("bonding", ["Bonding"]), ("stackable", ["Stackable"]), ("maxCount", ["MaxCount"]), ("containerSlots", ["ContainerSlots"]),
        ("buyPrice", ["BuyPrice"]), ("sellPrice", ["SellPrice"]), ("vendorStackCount", ["VendorStackCount"]),
        ("durationInInventory", ["DurationInInventory"]),
        // statAmount: Classic layouts (e.g. B51F7C79) store flat stat amounts; Retail stores allocation inputs instead.
        ("statType", ["StatModifier_bonusStat"]), ("statAmount", ["StatModifier_bonusAmount"]), ("statPercentEditor", ["StatPercentEditor"]),
        ("statSocketShare", ["StatPercentageOfSocket"]), ("socketType", ["SocketType"]),
        ("socketBonus", ["Socket_match_enchantment_ID", "Socket_match_enchantment_id"]),
        ("gemProperties", ["Gem_properties", "GemProperties"]),
        ("damageType", ["DamageType"]), ("itemDelay", ["ItemDelay"]), ("dmgVariance", ["DmgVariance"]), ("itemRange", ["ItemRange"]),
        ("flags", ["Flags"]), ("zoneBound", ["ZoneBound"]), ("instanceBound", ["InstanceBound"]),
        ("requiredHoliday", ["RequiredHoliday"]), ("priceVariance", ["PriceVariance"]), ("priceRandomValue", ["PriceRandomValue"]),
    ];

    private static ItemLabel[] List(params string[] names) =>
        names.Select((name, index) => new ItemLabel(index.ToString(System.Globalization.CultureInfo.InvariantCulture), name)).ToArray();
    private static FrozenDictionary<string, string> Map(IEnumerable<ItemLabel> labels) =>
        labels.ToFrozenDictionary(label => label.Value, label => label.Label, StringComparer.Ordinal);

    /// <summary>The reviewed lists (item-labels-v1). Bonding 7–9 and ItemEffect.TriggerType 3–7 stay unlabelled until reviewed.</summary>
    public static ItemLabelLists LabelLists { get; } = new(LabelRevision,
        List("Poor", "Common", "Uncommon", "Rare", "Epic", "Legendary", "Artifact", "Heirloom", "WoW Token"),
        List("Not equippable", "Head", "Neck", "Shoulder", "Shirt", "Chest", "Waist", "Legs", "Feet", "Wrist", "Hands", "Finger",
            "Trinket", "One-hand", "Shield", "Ranged", "Back", "Two-hand", "Bag", "Tabard", "Robe", "Main hand", "Off hand",
            "Held in off-hand", "Ammo", "Thrown", "Ranged right", "Quiver", "Relic", "Profession tool", "Profession gear"),
        List("No binding", "Binds when picked up", "Binds when equipped", "Binds when used", "Quest item"),
        List("On use", "On equip", "Chance on hit"));
    public static FrozenDictionary<string, string> Quality { get; } = Map(LabelLists.Quality);
    public static FrozenDictionary<string, string> InventoryType { get; } = Map(LabelLists.InventoryType);
    public static FrozenDictionary<string, string> Bonding { get; } = Map(LabelLists.Bonding);

    private static string? Label(string key, string value) => key switch
    {
        "quality" => Quality.GetValueOrDefault(value),
        "inventoryType" => InventoryType.GetValueOrDefault(value),
        "bonding" => Bonding.GetValueOrDefault(value),
        _ => null,
    };

    /// <summary>The item's ItemSparse row: present, missing (complete Sparse coverage) or unknown (partial coverage).</summary>
    public static string SparseState(ItemsSnapshot snapshot, ItemDetail detail) =>
        detail.SparseRow is not null ? "present" : snapshot.SparseSource.Coverage.Scope == "complete" ? "missing" : "unknown";

    public static IReadOnlyList<ItemFact> Project(ItemsSnapshot snapshot, ItemDetail detail)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(detail);
        if (snapshot.ItemSource.TableId != ItemTable || snapshot.SparseSource.TableId != SparseTable)
            throw new DiscoveryException("ITEMS_SOURCE_INVALID", "Item facts require the exact Item and ItemSparse admissions.");
        var sparse = SparseState(snapshot, detail);
        var result = new List<ItemFact>(Keys.Count);
        foreach (var (key, fields) in Keys)
        {
            var item = Cells(snapshot.ItemSource, detail.ItemRow, "Item", fields, key);
            var sparseColumns = Columns(snapshot.SparseSource, fields);
            var sparseCells = detail.SparseRow is null ? [] : Cells(snapshot.SparseSource, detail.SparseRow, "ItemSparse", fields, key);
            var values = item.Concat(sparseCells).ToArray();
            if (values.Length > MaximumValues)
                throw new DiscoveryException("ITEMS_FACTS_LIMIT", "An item fact has more source cells than its bound.");
            string state;
            if (item.Count == 0 && sparseColumns.Count == 0) state = "unsupported";
            else if (item.Count == 0 && detail.SparseRow is null) state = sparse;
            else if (Invalid(detail.ItemRow, item) || detail.SparseRow is not null && Invalid(detail.SparseRow, sparseCells)) state = "invalidText";
            else state = "value";
            // Both tables carry the field: same shape compares element by element; a different shape (Classic
            // Item.DamageType[0–4] beside ItemSparse.DamageType) stays apart as "separate", each cell with its own table.
            var agreement = item.Count == 0 || sparseCells.Count == 0 ? "single"
                : item.Count != sparseCells.Count ? "separate"
                : item.Select(v => v.Value).SequenceEqual(sparseCells.Select(v => v.Value), StringComparer.Ordinal) ? "agree" : "disagree";
            result.Add(new(key, state, agreement, state is "missing" or "unknown" ? [] : values));
        }
        return result;
    }

    private static List<int> Columns(TableDataSnapshot source, string[] fields)
    {
        var columns = new List<int>();
        for (var index = 0; index < source.Columns.Count; index++)
        {
            var name = source.Columns[index].Name; var bracket = name.IndexOf('[');
            if (fields.Contains(bracket < 0 ? name : name[..bracket], StringComparer.Ordinal)) columns.Add(index);
        }
        return columns;
    }

    private static List<ItemFactValue> Cells(TableDataSnapshot source, TableDataRow row, string table, string[] fields, string key)
    {
        var columns = Columns(source, fields);
        if (row.Values.Count != source.Columns.Count)
            throw new DiscoveryException("ITEMS_SOURCE_INVALID", "An item row differs from its admitted column layout.");
        var result = new List<ItemFactValue>(columns.Count);
        foreach (var index in columns)
        {
            var invalid = row.TextIssues?.Any(issue => issue.ColumnIndex == index) == true;
            var value = invalid ? "" : row.Values[index];
            result.Add(new(source.TableId, table, source.Columns[index].Name, index, row.Id, value, invalid ? null : Label(key, value)));
        }
        return result;
    }

    private static bool Invalid(TableDataRow row, List<ItemFactValue> cells) =>
        row.TextIssues is { Count: > 0 } issues && cells.Any(cell => issues.Any(issue => issue.ColumnIndex == cell.ColumnIndex));
}
