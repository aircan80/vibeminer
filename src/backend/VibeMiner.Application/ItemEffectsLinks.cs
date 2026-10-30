using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>One reviewed table profile in an effects chain: identity, pinned layout and definition, and its columns.
/// RawColumns may hold verified raw-width evidence (D079): since D108 such a column is raw only when its upper bits vary, and
/// otherwise reads at its declared width like any narrowed column. References are the declared column references the admitted
/// snapshot carries; a column read raw carries none.</summary>
public sealed record ItemEffectsTableProfile(string TableId, string Table, string FileDataId, string Layout, string Definition,
    IReadOnlyList<string> Columns, IReadOnlyList<TableReference?> References, IReadOnlyList<int> RawColumns);

/// <summary>How a build links an Item to its effects (#86, D104). Retail uses the ItemXItemEffect bridge. Classic
/// builds have no ItemXItemEffect and link through ItemEffect.ParentItemID: the ItemEffect row is then both the link
/// and the effect, so the bridge and effect sources are the same table.</summary>
public sealed record ItemEffectsLink(string Name, ItemEffectsTableProfile Bridge, ItemEffectsTableProfile Effect, ItemEffectsBridgeFields Fields)
{
    public bool SameTable => Bridge.TableId == Effect.TableId;
}

public static class ItemEffectsLinks
{
    private const string ItemEffectDefinition = "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c";
    private static readonly string[] Inputs = ["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec",
        "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"];
    private static TableReference R(string table) => new(table, "ID");

    public static readonly ItemEffectsTableProfile ItemXItemEffect = new("00cb674f", "ItemXItemEffect", "3177687", "96F083AD",
        "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", ["ID", "ItemEffectID", "ItemID"],
        [null, R("ItemEffect"), R("Item")], []);
    // Retail 4CA77678: Charges, SpellCategoryID and ChrSpecializationID are stored 32-bit, declared 16. D108: each narrows to
    // its declared width when its upper bits are one constant pattern (12.1.5 does), and stays raw, with no reference, otherwise.
    public static readonly ItemEffectsTableProfile RetailItemEffect = new("4002a5b1", "ItemEffect", "969941", "4CA77678", ItemEffectDefinition,
        Inputs, [null, null, null, null, null, null, R("SpellCategory"), R("Spell"), R("ChrSpecialization"), R("PlayerCondition")], [3, 6, 8]);
    // Classic 1BF9CF3A (5.5.4, 1.15.9, 2.5.5–2.5.6, 3.80.2): the same ten columns at their declared widths, then ParentItemID.
    public static readonly ItemEffectsTableProfile ClassicItemEffect = new("4002a5b1", "ItemEffect", "969941", "1BF9CF3A", ItemEffectDefinition,
        [.. Inputs, "ParentItemID"], [null, null, null, null, null, null, R("SpellCategory"), R("Spell"), R("ChrSpecialization"), R("PlayerCondition"), R("Item")], []);
    public static readonly ItemEffectsTableProfile SpellName = new("46c66698", "SpellName", "1990283", "782EE721",
        "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667", ["ID", "Name_lang"], [null, null], []);

    public static readonly ItemEffectsLink Bridge = new("itemXItemEffect", ItemXItemEffect, RetailItemEffect,
        new(new(ItemXItemEffect.TableId, 0), new(ItemXItemEffect.TableId, 1), new(ItemXItemEffect.TableId, 2)));
    public static readonly ItemEffectsLink Parent = new("parentItemId", ClassicItemEffect, ClassicItemEffect,
        new(new(ClassicItemEffect.TableId, 0), new(ClassicItemEffect.TableId, 0), new(ClassicItemEffect.TableId, 10)));
    public static readonly IReadOnlyList<ItemEffectsLink> All = [Bridge, Parent];

    public static ItemEffectsTableProfile Profile(ItemEffectsLink link, ItemEffectsSourceRole role) => role switch
    {
        ItemEffectsSourceRole.Bridge => link.Bridge, ItemEffectsSourceRole.Effect => link.Effect, ItemEffectsSourceRole.Name => SpellName,
        _ => throw new DiscoveryException("INVALID_REQUEST", "Choose one exact effects source role.")
    };
    /// <summary>The link whose bridge profile this admitted snapshot is, by exact table and layout; null for neither.</summary>
    public static ItemEffectsLink? ForBridge(string tableId, string layout) =>
        All.FirstOrDefault(link => link.Bridge.TableId == tableId && link.Bridge.Layout == layout);
    public static ItemEffectsLink? ForEffect(string tableId, string layout) =>
        All.FirstOrDefault(link => link.Effect.TableId == tableId && link.Effect.Layout == layout);
}
