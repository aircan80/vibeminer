using System.Globalization;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.ItemEffects;

/// <summary>
/// One reviewed table profile (the old ItemEffectsLinks): definition name, FileDataID, layout, columns in order, and the columns
/// that may keep raw 32-bit evidence when their upper bits don't narrow (D108, Retail ItemEffect 3, 6 and 8).
/// </summary>
internal sealed record ItemEffectProfile(string Table, uint FileDataId, string Layout, IReadOnlyList<string> Columns, IReadOnlyList<int> RawColumns)
{
    public bool IsEffect => Table == ItemEffectProfiles.EffectTable;
    public bool IsName => Table == ItemEffectProfiles.NameTable;
}

/// <summary>The reviewed profiles, the link choice (D104, D459), the source states (D460) and the input width rules (D108).</summary>
internal static class ItemEffectProfiles
{
    public const string ItemTable = "Item";
    public const string BridgeTable = "ItemXItemEffect", EffectTable = "ItemEffect", NameTable = "SpellName";
    public const string FieldUnavailableCode = "ITEM_EFFECTS_FIELD_UNAVAILABLE";
    public const string TextInvalidCode = "DB2_TEXT_ENCODING_INVALID";

    private static readonly string[] Inputs = ["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec",
        "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"];

    public static readonly ItemEffectProfile ItemXItemEffect = new(BridgeTable, 3177687, "96F083AD", ["ID", "ItemEffectID", "ItemID"], []);
    public static readonly ItemEffectProfile RetailItemEffect = new(EffectTable, 969941, "4CA77678", Inputs, [3, 6, 8]);
    public static readonly ItemEffectProfile ClassicItemEffect = new(EffectTable, 969941, "1BF9CF3A", [.. Inputs, "ParentItemID"], []);
    public static readonly ItemEffectProfile SpellName = new(NameTable, 1990283, "782EE721", ["ID", "Name_lang"], []);

    /// <summary>The column of a link row that holds the item: ItemXItemEffect.ItemID (2) or ItemEffect.ParentItemID (10).</summary>
    public static int ParentColumn(ItemEffectsLink link) => link == ItemEffectsLink.ItemXItemEffect ? 2 : 10;
    public static string ParentColumnName(ItemEffectsLink link) => link == ItemEffectsLink.ItemXItemEffect ? "ItemID" : "ParentItemID";
    public static ItemEffectProfile Bridge(ItemEffectsLink link) => link == ItemEffectsLink.ItemXItemEffect ? ItemXItemEffect : ClassicItemEffect;
    public static ItemEffectProfile Effect(ItemEffectsLink link) => link == ItemEffectsLink.ItemXItemEffect ? RetailItemEffect : ClassicItemEffect;

    /// <summary>
    /// The build's link (D104 on layer 5, D459). The ItemEffect block layer 5 resolved for this build decides: it links through
    /// ParentItemID when it stores ParentItemID as a relation, else through ItemXItemEffect. With no ItemEffect block, an
    /// ItemXItemEffect block for the build means the bridge. With neither, the link is ParentItemID, so ItemEffect is read and
    /// says why it can't be (the old undecided build reading ItemEffect first).
    /// </summary>
    public static ItemEffectsLink Choose(TableSchema? effect, TableSchema? bridge)
    {
        if (effect is not null)
            return effect.Columns.Any(column => column.Name == "ParentItemID" && column.Relation != KeyStorage.None)
                ? ItemEffectsLink.ParentItemId : ItemEffectsLink.ItemXItemEffect;
        return bridge is not null ? ItemEffectsLink.ItemXItemEffect : ItemEffectsLink.ParentItemId;
    }

    /// <summary>The reviewed definition shape of one profile column (the old ItemEffectsLinkChoice.FieldMatches).</summary>
    public static bool FieldMatches(ItemEffectProfile profile, int index, SchemaColumn field)
    {
        var name = profile.Columns[index];
        if (field.Name != name || field.ArrayLength != 1) return false;
        if (profile.IsName)
            return field.VerifiedName && field.Kind == (index == 1 ? ColumnKind.LocString : ColumnKind.Int) &&
                field.Id == (index == 0 ? KeyStorage.NonInline : KeyStorage.None) && field.Relation == KeyStorage.None &&
                (index == 1 || field.Bits == 32 && field.Unsigned == false);
        var effect = profile.IsEffect;
        var relation = effect ? name == "ParentItemID" : index == 2;
        var bits = effect ? index is 1 or 2 ? 8 : index is 3 or 6 or 8 ? 16 : 32 : 32;
        var unsigned = effect && index is 1 or 2 or 6 or 8;
        return field.VerifiedName != (effect && index == 9) && field.Kind == ColumnKind.Int &&
            field.Id == (index == 0 ? KeyStorage.NonInline : KeyStorage.None) && field.Relation == (relation ? KeyStorage.NonInline : KeyStorage.None) &&
            field.Bits == bits && field.Unsigned == unsigned;
    }

    /// <summary>
    /// Whether a readable table is the reviewed profile (the old Qualified, on layer 5): WDC5, the reviewed FileDataID and layout,
    /// a definition block whose every field has the reviewed shape, and header columns of the same names, kinds and ID. A raw
    /// column is allowed only where the profile allows it, with the reviewed raw evidence (32-bit unsigned physical, 16-bit
    /// candidate, signed only for Charges); any other column must have none.
    /// </summary>
    public static bool Qualifies(ItemEffectProfile profile, TableSchemaResult result)
    {
        var entry = result.Table;
        if (entry.Counts is not { } counts || counts.Format != "WDC5" || entry.FileDataId != profile.FileDataId ||
            !string.Equals(counts.LayoutHash, profile.Layout, StringComparison.OrdinalIgnoreCase)) return false;
        if (result.Schema is not { } schema || result.Columns is not { } columns) return false;
        var names = profile.Columns;
        if (schema.Columns.Count != names.Count || columns.Count != names.Count) return false;
        for (var i = 0; i < names.Count; i++)
        {
            var actual = columns[i];
            var text = profile.IsName && i == 1;
            if (!FieldMatches(profile, i, schema.Columns[i]) || actual.Name != names[i] || actual.Kind != (text ? "string" : "integer") || actual.IsId != (i == 0))
                return false;
            if (actual.Interpretation is { } raw)
            {
                if (!profile.RawColumns.Contains(i) || raw is not { Policy: "raw-physical-integer-v1", PhysicalBits: 32, PhysicalSigned: false, CandidateBits: 16 } ||
                    raw.CandidateUnsigned != (i != 3) || raw.Warning.Length is < 1 or > 512 || raw.Warning.Any(char.IsControl)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A checked table's source state (D460): the old acquisition mapping on layer 5's states, the table entry kept as it came.
    /// Ready and Partial go on to the profile check.
    /// </summary>
    public static ItemEffectsSource Source(ItemEffectsRole role, TableEntry table)
    {
        var definition = table.State is TableState.NoDefinition or TableState.DefinitionUnusable ? table.Reason?.DefinitionState : null;
        var state = table.State switch
        {
            TableState.Ready or TableState.Partial => ItemEffectsSourceState.Ready,
            TableState.NotInBuild => ItemEffectsSourceState.NotInBuild,
            TableState.NotInLocale => ItemEffectsSourceState.NotInLocale,
            TableState.NeedsNetwork or TableState.Missing or TableState.Unavailable or TableState.NotInEncoding or TableState.NotStored or TableState.NotChecked =>
                ItemEffectsSourceState.Unavailable,
            TableState.Encrypted or TableState.WrongKey => ItemEffectsSourceState.Protected,
            TableState.TooLarge => ItemEffectsSourceState.ResourceLimit,
            TableState.Corrupt or TableState.Rejected or TableState.StoreFailed => ItemEffectsSourceState.Corrupt,
            // D118: a failed WoWDBDefs fetch can change on Retry; a definition too large, invalid or absent upstream, fetching
            // off, or no definition at all can't until an update.
            TableState.DefinitionUnusable when table.Reason?.Code is "DEFINITION_FETCH_OFFLINE" or "DEFINITION_FETCH_FAILED" => ItemEffectsSourceState.Unavailable,
            _ => ItemEffectsSourceState.Unsupported,
        };
        return state == ItemEffectsSourceState.Ready ? new(role, state, table) : Failed(role, state, table, definition);
    }

    public static ItemEffectsSource Failed(ItemEffectsRole role, ItemEffectsSourceState state, TableEntry? table, DefinitionState? definition = null) =>
        new(role, state, table, Problem(state, definition));

    public static ItemEffectsProblem Problem(ItemEffectsSourceState state, DefinitionState? definition = null) => state switch
    {
        ItemEffectsSourceState.Unavailable => new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true, definition),
        ItemEffectsSourceState.Protected => new("ITEM_EFFECTS_SOURCE_PROTECTED", "The selected source is protected.", false),
        ItemEffectsSourceState.Unsupported => new("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported.", false, definition),
        ItemEffectsSourceState.Corrupt => new("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation.", false),
        ItemEffectsSourceState.ResourceLimit => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.", false),
        ItemEffectsSourceState.NotInBuild => new("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source.", false),
        ItemEffectsSourceState.NotInLocale => new("ITEM_EFFECTS_SOURCE_NOT_IN_LOCALE", "This build includes the selected source in other locales, not this one.", false),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>
    /// The nine inputs of a found ItemEffect row (the old EffectOutcome): 1 and 2 are u8; a raw 3, 6 or 8 is its physical u32;
    /// otherwise 6 and 8 are u16 and the rest a canonical int32, Charges (3) within int16. A text issue on an input, or a value
    /// outside its rule, makes the effect FieldUnavailable (null here).
    /// </summary>
    public static ItemEffectField[]? Fields(ItemEffectProfile profile, IReadOnlyList<Db2Column> columns, Db2Row row)
    {
        var fields = new ItemEffectField[9];
        for (var i = 1; i <= 9; i++)
        {
            var value = row.Values[i];
            var interpretation = columns[i].Interpretation;
            var raw = profile.RawColumns.Contains(i) && interpretation is not null;
            if (HasIssue(row, i) || !(i is 1 or 2 ? Unsigned(value, byte.MaxValue) : raw ? Unsigned(value, uint.MaxValue) :
                    i is 6 or 8 ? Unsigned(value, ushort.MaxValue) : i == 3 ? Signed(value, short.MinValue, short.MaxValue) : Signed(value, int.MinValue, int.MaxValue)))
                return null;
            var evidence = raw ? ItemEffectEvidence.RawPhysicalInteger : i == 9 ? ItemEffectEvidence.UnverifiedCandidate : ItemEffectEvidence.StrictNumeric;
            ForeignKey? hint = i switch { 6 => new("SpellCategory", "ID"), 8 => new("ChrSpecialization", "ID"), 9 => new("PlayerCondition", "ID"), _ => null };
            fields[i - 1] = new(profile.Columns[i], i, value, evidence, raw ? interpretation : null, hint);
        }
        return fields;
    }

    /// <summary>SpellID (column 7) when it is a canonical int32 with no text issue; the name lookup needs only it (the old SpellEvidence).</summary>
    public static string? SpellId(Db2Row row) => !HasIssue(row, 7) && Signed(row.Values[7], int.MinValue, int.MaxValue) ? row.Values[7] : null;

    public static bool HasIssue(Db2Row row, int column) => row.TextIssues?.Any(issue => issue.ColumnIndex == column) == true;

    /// <summary>A canonical decimal (<see cref="TableQueries.TryParseInteger"/>) within the bounds.</summary>
    public static bool Signed(string? value, long min, long max, out long number)
    {
        number = 0;
        if (!TableQueries.TryParseInteger(value, out var parsed) || parsed < min || parsed > max) return false;
        number = (long)parsed;
        return true;
    }

    public static bool Signed(string? value, long min, long max) => Signed(value, min, max, out _);
    private static bool Unsigned(string? value, uint max) => Signed(value, 0, max);

    public static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the text is well-formed UTF-16: every surrogate in a high-low pair.</summary>
    public static bool WellFormed(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
}
