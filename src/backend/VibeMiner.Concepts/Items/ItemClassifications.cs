using System.Globalization;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Items;

public enum ItemClassLabelState
{
    /// <summary>One target row has the key, the target is Ready, and its text is non-empty and at most 4096 UTF-16 units.</summary>
    Ready,
    /// <summary>The unique target row's text is "".</summary>
    EmptyText,
    /// <summary>The unique target row's text cell has a text issue.</summary>
    InvalidText,
    /// <summary>
    /// ITEM_CLASSIFICATION_TEXT_LIMIT: the unique text is over 4096 units; or ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED: the target
    /// lacks one plain signed 8-bit key column or one plain text column; or a layer 5 TABLE_FILTER_* code.
    /// </summary>
    Unsupported,
    /// <summary>The target is Ready and no row has the key.</summary>
    NotFound,
    /// <summary>The target is Partial and no readable row has the key, or one does: never a label and never proof of absence.</summary>
    IncompleteTarget,
    /// <summary>More than one row (copies count) has the key; <see cref="ItemClassLabel.Candidates"/> says how many. No text.</summary>
    Ambiguous,
    /// <summary>Item has no one plain key column of the reviewed width and sign, or its value isn't a canonical integer in it.</summary>
    FieldUnavailable,
    /// <summary>The key is outside the target's signed 8-bit range (ClassID -128..127, SubclassID 0..127).</summary>
    KeyOutOfRange,
    /// <summary>The target table isn't Ready or Partial; <see cref="ItemClassLabel.Table"/> keeps its layer 5 state.</summary>
    TableNotReadable,
    /// <summary>ITEM_CLASSIFICATION_SCAN_LIMIT: the key scan looked at <see cref="ItemsOptions.MaxLabelScanRows"/> rows before the table ended. Not NotFound.</summary>
    ScanLimit,
    /// <summary>The target's identity changed between scan pages.</summary>
    Stale,
}

/// <summary>
/// One classification label (D049, D455). <see cref="SourceKeys"/> are the Item cells it is keyed on. <see cref="Text"/> is set
/// for Ready and EmptyText. <see cref="Target"/> and <see cref="TextColumn"/> are the unique target row and its text column,
/// set whenever exactly one row of a Ready target has the key. <see cref="Candidates"/> is the matching rows seen (null when
/// no scan ran), <see cref="Scanned"/> the rows looked at. <see cref="Table"/> is the target's checked entry when it was asked.
/// </summary>
public sealed record ItemClassLabel(ItemClassLabelState State, IReadOnlyList<ItemSourceValue> SourceKeys, string? Text = null, TableRow? Target = null,
    int? TextColumn = null, long? Candidates = null, long Scanned = 0, TableEntry? Table = null, string? Code = null, string? Message = null);

/// <summary>An item's class label (ItemClass by ClassID) and subclass label (ItemSubClass by ClassID and SubClassID). Labels never block the item.</summary>
public sealed record ItemClassLabels(ItemClassLabel Class, ItemClassLabel Subclass);

/// <summary>
/// classification-label-index-v2 on layer 5 (old ItemClassificationsStore/V2, ClassificationIndex, CompositeClassificationIndex):
/// labels are looked up by semantic key, never by row ID, through a bounded filtered scan of the target table.
/// </summary>
public static class ItemClassifications
{
    public const string PolicyRevision = "classification-label-index-v2";
    public const string ClassTable = "ItemClass", SubclassTable = "ItemSubClass";
    public const string ProfileUnsupportedCode = "ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED";
    public const string TextLimitCode = "ITEM_CLASSIFICATION_TEXT_LIMIT";
    public const string ScanLimitCode = "ITEM_CLASSIFICATION_SCAN_LIMIT";
    public const int MaxTextUnits = 4096;
    private const int PageRows = TableQueries.MaxLimit;

    /// <summary>
    /// The item side of a label from the item's summary cells: Item.ClassID must be one canonical signed 32-bit integer and,
    /// for a subclass, Item.SubclassID one canonical unsigned 8-bit integer (else FieldUnavailable); then ClassID must be in
    /// -128..127 and SubclassID at most 127 (else KeyOutOfRange). A null state means the keys can be looked up.
    /// </summary>
    public static (ItemClassLabelState? State, int ClassId, int SubclassId, IReadOnlyList<ItemSourceValue> Keys) SourceKey(
        IReadOnlyList<ItemSourceValue> values, bool subclass)
    {
        ArgumentNullException.ThrowIfNull(values);
        var classKeys = Keys(values, "ClassID");
        var subKeys = subclass ? Keys(values, "SubclassID") : [];
        IReadOnlyList<ItemSourceValue> keys = [.. classKeys, .. subKeys];
        if (classKeys.Length != 1 || !int.TryParse(classKeys[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var classId) ||
            N(classId) != classKeys[0].Value) return (ItemClassLabelState.FieldUnavailable, 0, 0, keys);
        var subId = 0;
        if (subclass)
        {
            if (subKeys.Length != 1 || !byte.TryParse(subKeys[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || N(parsed) != subKeys[0].Value)
                return (ItemClassLabelState.FieldUnavailable, 0, 0, keys);
            subId = parsed;
        }
        if (classId is < -128 or > 127 || subId > 127) return (ItemClassLabelState.KeyOutOfRange, 0, 0, keys);
        return (null, classId, subId, keys);
    }

    private static ItemSourceValue[] Keys(IReadOnlyList<ItemSourceValue> values, string column) =>
        [.. values.Where(value => value.Table == ItemLayout.ItemTable && value.Column == column)];

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Both labels of an item. Data problems are states; this throws only for cancellation and what layer 5 throws.</summary>
    internal static async Task<ItemClassLabels> ResolveAsync(IBuildTables tables, ItemSummary summary, int maxScanRows, TablePriority priority,
        CancellationToken cancellationToken)
    {
        // Item's key fields qualify against the definition Tables chose (D101): ClassID int32 signed, SubclassID uint8. A
        // disqualified field reads as no field, as the old ItemColumn check did.
        var itemSchema = (await tables.SchemaAsync(TableRef.ForName(ItemLayout.ItemTable), priority, cancellationToken).ConfigureAwait(false)).Schema;
        var values = summary.Values.Where(value => value.Table != ItemLayout.ItemTable || value.Column switch
        {
            "ClassID" => Qualifies(itemSchema, "ClassID", 32, false),
            "SubclassID" => Qualifies(itemSchema, "SubclassID", 8, true),
            _ => true,
        }).ToArray();
        var cls = SourceKey(values, false);
        var classLabel = cls.State is { } classState ? new ItemClassLabel(classState, cls.Keys)
            : await LookupAsync(tables, ClassTable, "ClassName_lang", [("ClassID", cls.ClassId)], cls.Keys, maxScanRows, priority, cancellationToken).ConfigureAwait(false);
        var sub = SourceKey(values, true);
        var subLabel = sub.State is { } subState ? new ItemClassLabel(subState, sub.Keys)
            : await LookupAsync(tables, SubclassTable, "DisplayName_lang", [("ClassID", sub.ClassId), ("SubClassID", sub.SubclassId)], sub.Keys, maxScanRows,
                priority, cancellationToken).ConfigureAwait(false);
        return new(classLabel, subLabel);
    }

    // A field the schema declares must be one verified int of exactly this width and sign. No schema (or no width) leaves the
    // value checks of SourceKey and the target's key range as the bound.
    private static bool Qualifies(TableSchema? schema, string name, int bits, bool unsigned)
    {
        var column = schema?.Columns.FirstOrDefault(c => c.Name == name);
        if (column is null) return true;
        return column is { Kind: ColumnKind.Int, ArrayLength: 1, VerifiedName: true } && column.Bits == bits && column.Unsigned == unsigned;
    }

    private static bool TextQualifies(TableSchema? schema, string name)
    {
        var column = schema?.Columns.FirstOrDefault(c => c.Name == name);
        return column is null || column is { Kind: ColumnKind.String or ColumnKind.LocString, ArrayLength: 1, VerifiedName: true };
    }

    private static int Plain(IReadOnlyList<Db2Column> columns, string name, string kind)
    {
        var found = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name != name || columns[i].Kind != kind || columns[i].Interpretation is not null) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    private static async Task<ItemClassLabel> LookupAsync(IBuildTables tables, string tableName, string textName, (string Column, int Key)[] keys,
        IReadOnlyList<ItemSourceValue> sourceKeys, int maxScanRows, TablePriority priority, CancellationToken cancellationToken)
    {
        var table = TableRef.ForName(tableName);
        var schema = await tables.SchemaAsync(table, priority, cancellationToken).ConfigureAwait(false);
        if (!TableQueries.IsReadable(schema.Table.State)) return new(ItemClassLabelState.TableNotReadable, sourceKeys, Table: schema.Table);
        var columns = schema.Columns ?? [];
        var text = Plain(columns, textName, "string");
        if (text < 0 || !TextQualifies(schema.Schema, textName) ||
            keys.Any(key => Plain(columns, key.Column, "integer") < 0 || !Qualifies(schema.Schema, key.Column, 8, false)))
            return new(ItemClassLabelState.Unsupported, sourceKeys, Table: schema.Table, Code: ProfileUnsupportedCode,
                Message: $"{tableName} has no one plain signed 8-bit key column per key and one plain {textName} text column.");
        TableFilter[] filters = [.. keys.Select(key => new TableFilter(key.Column, TableFilterOp.Equals, N(key.Key)))];
        TableRow? first = null;
        long candidates = 0, scanned = 0, after = -1;
        string? identity = null;
        var entry = schema.Table;
        while (true)
        {
            var page = await tables.PageAsync(new TablePageRequest(table, after, PageRows, filters, identity), priority, cancellationToken).ConfigureAwait(false);
            entry = page.Table;
            switch (page.State)
            {
                case TablePageState.Ok: break;
                case TablePageState.NotReadable: return new(ItemClassLabelState.TableNotReadable, sourceKeys, Scanned: scanned, Table: entry);
                case TablePageState.BadFilter:
                    return new(ItemClassLabelState.Unsupported, sourceKeys, Scanned: scanned, Table: entry, Code: page.Code, Message: page.Message);
                // Every page after the first carries the first page's identity and the previous NextAfter, so an After past the
                // rows (InvalidCursor) means the table changed under the scan as surely as Stale does (D456).
                case TablePageState.Stale or TablePageState.InvalidCursor:
                    return new(ItemClassLabelState.Stale, sourceKeys, Scanned: scanned, Table: entry, Code: page.Code, Message: page.Message);
                default: throw new System.Diagnostics.UnreachableException($"Layer 5 page state {page.State} is not handled.");
            }
            identity = page.Identity;
            // A page always moves forward; count at least one row so the bound holds even if a page reports none.
            scanned += Math.Max(page.Scanned, 1);
            candidates += page.Rows.Count;
            first ??= page.Rows.Count > 0 ? page.Rows[0] : null;
            if (page.NextAfter is not { } next) break;
            if (scanned >= maxScanRows)
                return new(ItemClassLabelState.ScanLimit, sourceKeys, Candidates: candidates, Scanned: scanned, Table: entry, Code: ScanLimitCode,
                    Message: $"The {tableName} key scan stopped at its bound of {maxScanRows} rows before the table ended.");
            after = next;
        }
        var complete = entry.State == TableState.Ready;
        if (candidates == 0)
            return new(complete ? ItemClassLabelState.NotFound : ItemClassLabelState.IncompleteTarget, sourceKeys, Candidates: 0, Scanned: scanned, Table: entry);
        if (candidates > 1) return new(ItemClassLabelState.Ambiguous, sourceKeys, Candidates: candidates, Scanned: scanned, Table: entry);
        if (!complete) return new(ItemClassLabelState.IncompleteTarget, sourceKeys, Candidates: 1, Scanned: scanned, Table: entry);
        var row = first!.Row;
        var value = text < row.Values.Count ? row.Values[text] : "";
        var invalid = text >= row.Values.Count || row.TextIssues?.Any(issue => issue.ColumnIndex == text) == true;
        var state = invalid ? ItemClassLabelState.InvalidText : value.Length > MaxTextUnits ? ItemClassLabelState.Unsupported
            : value.Length == 0 ? ItemClassLabelState.EmptyText : ItemClassLabelState.Ready;
        return new(state, sourceKeys, state is ItemClassLabelState.Ready or ItemClassLabelState.EmptyText ? value : null, first, text, 1, scanned, entry,
            state == ItemClassLabelState.Unsupported ? TextLimitCode : null,
            state == ItemClassLabelState.Unsupported ? $"The unique label exceeds {MaxTextUnits} text units." : null);
    }
}
