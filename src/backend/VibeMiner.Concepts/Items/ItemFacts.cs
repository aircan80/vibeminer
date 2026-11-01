using System.Collections.Frozen;
using System.Globalization;
using VibeMiner.Formats;

namespace VibeMiner.Concepts.Items;

/// <summary>Whether the item's ItemSparse row was found. Missing only when ItemSparse is Ready; Protected is a keyed section whose key isn't known (D454).</summary>
public enum ItemSparseState
{
    Present,
    Missing,
    /// <summary>Outside a Partial ItemSparse's readable sections, or ItemSparse isn't readable. Never proof of absence.</summary>
    Unknown,
    /// <summary>In a keyed section of ItemSparse whose key isn't known; <see cref="ItemSummary.SparseKey"/> names it.</summary>
    Protected,
}

public enum ItemFactState
{
    Value,
    /// <summary>Neither table's layout has the field. Never zero.</summary>
    Unsupported,
    /// <summary>Only ItemSparse has the field and ItemSparse is Ready with no row for the item.</summary>
    Missing,
    /// <summary>Only ItemSparse could have the field and its row (or its layout) isn't readable.</summary>
    Unknown,
    /// <summary>Only ItemSparse has the field and its row is in a keyed section whose key isn't known.</summary>
    Protected,
    /// <summary>A source cell has a text issue; its value is "" and it has no label.</summary>
    InvalidText,
}

/// <summary>How Item's and ItemSparse's cells of one fact relate.</summary>
public enum ItemFactAgreement
{
    /// <summary>Only one table has cells for it.</summary>
    Single,
    /// <summary>Both have it with different cell counts (Classic <c>Item.DamageType[0-4]</c> beside <c>ItemSparse.DamageType</c>); kept apart.</summary>
    Separate,
    Agree,
    Disagree,
}

/// <summary>One source cell of a fact: its table's definition name, header column and index, row ID and exact value, and a reviewed label when the value has one.</summary>
public sealed record ItemFactValue(string Table, string Column, int ColumnIndex, uint RowId, string Value, string? Label);

/// <summary>One fact. <see cref="Values"/> are Item's cells then ItemSparse's, each table in column order; empty for Missing, Unknown, Protected and Unsupported.</summary>
public sealed record ItemFact(string Key, ItemFactState State, ItemFactAgreement Agreement, IReadOnlyList<ItemFactValue> Values);

public enum ItemFactsState
{
    /// <summary><see cref="ItemFactSet.Facts"/> has every key of <see cref="ItemFacts.Keys"/> in order.</summary>
    Ready,
    /// <summary>ITEMS_FACTS_LIMIT: a fact has more than <see cref="ItemFacts.MaximumValues"/> cells. Nothing is truncated: no facts.</summary>
    LimitExceeded,
    /// <summary>ITEMS_SOURCE_INVALID: a source row doesn't line up with its header columns.</summary>
    SourceInvalid,
}

/// <summary>An item's facts (item-facts-v1, D098, D454). Facts never block the rest of the detail.</summary>
public sealed record ItemFactSet(ItemFactsState State, ItemSparseState Sparse, IReadOnlyList<ItemFact> Facts, string? Code = null, string? Message = null);

/// <summary>One reviewed label: an exact canonical decimal value and its text.</summary>
public sealed record ItemLabel(string Value, string Label);

/// <summary>The reviewed label lists (item-labels-v1), values ascending from 0.</summary>
public sealed record ItemLabelLists(string LabelRevision, IReadOnlyList<ItemLabel> Quality, IReadOnlyList<ItemLabel> InventoryType,
    IReadOnlyList<ItemLabel> Bonding, IReadOnlyList<ItemLabel> Trigger);

/// <summary>
/// item-facts-v1 (old Application/ItemFacts.cs, D098): a fixed projection of one item's Item and ItemSparse rows. Every value
/// keeps its exact source cell; nothing is calculated, scaled or inferred. Labels come only from <see cref="LabelLists"/> and
/// only for exact listed values.
/// </summary>
public static class ItemFacts
{
    public const string PolicyRevision = "item-facts-v1";
    public const string LabelRevision = "item-labels-v1";
    public const string LimitCode = "ITEMS_FACTS_LIMIT";
    public const int MaximumValues = 16;

    /// <summary>Fact keys in order, each with the source field names (any of them, in either table, <c>[n]</c> suffixes stripped) it reads.</summary>
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
        [.. names.Select((name, index) => new ItemLabel(index.ToString(CultureInfo.InvariantCulture), name))];

    private static FrozenDictionary<string, string> Map(IEnumerable<ItemLabel> labels) =>
        labels.ToFrozenDictionary(label => label.Value, label => label.Label, StringComparer.Ordinal);

    /// <summary>The reviewed lists. Bonding 7-9 and ItemEffect.TriggerType 3-7 stay unlabelled until reviewed.</summary>
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
    public static FrozenDictionary<string, string> Trigger { get; } = Map(LabelLists.Trigger);

    private static string? Label(string key, string value) => key switch
    {
        "quality" => Quality.GetValueOrDefault(value),
        "inventoryType" => InventoryType.GetValueOrDefault(value),
        "bonding" => Bonding.GetValueOrDefault(value),
        _ => null,
    };

    /// <summary>
    /// How the item's ItemSparse lookup went, from its name state, which already says so: Valid, Empty and Invalid names come from
    /// a found row (Present). Never Missing unless ItemSparse is complete.
    /// </summary>
    public static ItemSparseState SparseState(ItemNameState name) => name switch
    {
        ItemNameState.MissingSparse => ItemSparseState.Missing,
        ItemNameState.SparseProtected => ItemSparseState.Protected,
        ItemNameState.SparseUnknown => ItemSparseState.Unknown,
        _ => ItemSparseState.Present,
    };

    /// <summary>
    /// The facts of an Item row and its ItemSparse row. <paramref name="sparseColumns"/> is null when ItemSparse isn't readable;
    /// <paramref name="sparse"/> is the found row, else <paramref name="sparseState"/> says why there is none.
    /// </summary>
    public static ItemFactSet Project(IReadOnlyList<Db2Column> itemColumns, IReadOnlyList<Db2Column>? sparseColumns, Db2Row item, Db2Row? sparse,
        ItemSparseState sparseState)
    {
        ArgumentNullException.ThrowIfNull(itemColumns); ArgumentNullException.ThrowIfNull(item);
        if ((sparse is null) != (sparseState != ItemSparseState.Present) || sparse is not null && sparseColumns is null)
            throw new ArgumentException("The ItemSparse row and its state disagree.", nameof(sparseState));
        if (item.Values.Count != itemColumns.Count || sparse is not null && sparse.Values.Count != sparseColumns!.Count)
            return new(ItemFactsState.SourceInvalid, sparseState, [], ItemsSourceException.SourceInvalid, "An item row differs from its header columns.");
        var result = new List<ItemFact>(Keys.Count);
        foreach (var (key, fields) in Keys)
        {
            var itemCells = Cells(itemColumns, item, ItemLayout.ItemTable, fields, key);
            var sparseFields = sparseColumns is null ? null : Columns(sparseColumns, fields);
            var sparseCells = sparse is null ? [] : Cells(sparseColumns!, sparse, ItemLayout.SparseTable, fields, key);
            if (itemCells.Count + sparseCells.Count > MaximumValues)
                return new(ItemFactsState.LimitExceeded, sparseState, [], LimitCode, "An item fact has more source cells than its bound.");
            ItemFactState state;
            if (itemCells.Count == 0 && sparseFields is { Count: 0 }) state = ItemFactState.Unsupported;
            else if (itemCells.Count == 0 && sparse is null) state = sparseState switch
            {
                ItemSparseState.Missing => ItemFactState.Missing,
                ItemSparseState.Protected => ItemFactState.Protected,
                _ => ItemFactState.Unknown,
            };
            else if (Invalid(item, itemCells) || sparse is not null && Invalid(sparse, sparseCells)) state = ItemFactState.InvalidText;
            else state = ItemFactState.Value;
            // Same shape compares element by element; a different shape stays apart as Separate, each cell with its own table.
            var agreement = itemCells.Count == 0 || sparseCells.Count == 0 ? ItemFactAgreement.Single
                : itemCells.Count != sparseCells.Count ? ItemFactAgreement.Separate
                : itemCells.Select(v => v.Value).SequenceEqual(sparseCells.Select(v => v.Value), StringComparer.Ordinal) ? ItemFactAgreement.Agree
                : ItemFactAgreement.Disagree;
            IReadOnlyList<ItemFactValue> values = state is ItemFactState.Value or ItemFactState.InvalidText ? [.. itemCells, .. sparseCells] : [];
            result.Add(new(key, state, agreement, values));
        }
        return new(ItemFactsState.Ready, sparseState, result);
    }

    private static List<int> Columns(IReadOnlyList<Db2Column> columns, string[] fields)
    {
        var found = new List<int>();
        for (var index = 0; index < columns.Count; index++)
        {
            var name = columns[index].Name;
            var bracket = name.IndexOf('[', StringComparison.Ordinal);
            if (fields.Contains(bracket < 0 ? name : name[..bracket], StringComparer.Ordinal)) found.Add(index);
        }
        return found;
    }

    private static List<ItemFactValue> Cells(IReadOnlyList<Db2Column> columns, Db2Row row, string table, string[] fields, string key)
    {
        var indexes = Columns(columns, fields);
        var cells = new List<ItemFactValue>(indexes.Count);
        foreach (var index in indexes)
        {
            var invalid = row.TextIssues?.Any(issue => issue.ColumnIndex == index) == true;
            var value = invalid ? "" : row.Values[index];
            cells.Add(new(table, columns[index].Name, index, row.Id, value, invalid ? null : Label(key, value)));
        }
        return cells;
    }

    private static bool Invalid(Db2Row row, List<ItemFactValue> cells) =>
        row.TextIssues is { Count: > 0 } issues && cells.Any(cell => issues.Any(issue => issue.ColumnIndex == cell.ColumnIndex));
}
