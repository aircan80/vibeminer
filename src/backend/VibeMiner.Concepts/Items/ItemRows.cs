using VibeMiner.Formats;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Items;

/// <summary>A column-shape or cell problem: one of the old Items codes, kept by name. Caught at the call and returned as a state.</summary>
internal sealed class ItemsSourceException(string code, string message) : Exception(message)
{
    public const string NameUnsupported = "ITEMS_NAME_UNSUPPORTED";
    public const string SchemaAmbiguous = "ITEMS_SCHEMA_AMBIGUOUS";
    public const string SourceInvalid = "ITEMS_SOURCE_INVALID";

    public string Code { get; } = code;
}

/// <summary>
/// Which header columns the Items rules read (items-projection-v2's picks, D044): a field is one plain column of its exact name
/// and kind (no raw-physical interpretation); a field the layout lacks is skipped; two such columns are ambiguous. -1 means none.
/// </summary>
internal sealed record ItemLayout(IReadOnlyList<Db2Column> ItemColumns, int[] ItemFields, int Icon, IReadOnlyList<Db2Column>? SparseColumns,
    int[] SparseFields, int Name)
{
    public const string ItemTable = "Item";
    public const string SparseTable = "ItemSparse";
    private static readonly string[] ItemFieldNames = ["ClassID", "SubclassID", "InventoryType"];
    private static readonly string[] SparseFieldNames = ["OverallQualityID", "ItemLevel", "InventoryType"];

    /// <summary>The layout from the two tables' header columns. <paramref name="sparse"/> is null when ItemSparse isn't readable.</summary>
    public static ItemLayout Resolve(IReadOnlyList<Db2Column> item, IReadOnlyList<Db2Column>? sparse)
    {
        var itemFields = Fields(item, ItemFieldNames);
        var icon = Column(item, "IconFileDataID", "integer");
        if (sparse is null) return new(item, itemFields, icon, null, [], -1);
        var name = Column(sparse, "Display_lang", "string");
        if (name < 0) throw new ItemsSourceException(ItemsSourceException.NameUnsupported, "ItemSparse has no plain Display_lang text column.");
        return new(item, itemFields, icon, sparse, Fields(sparse, SparseFieldNames), name);
    }

    private static int[] Fields(IReadOnlyList<Db2Column> columns, string[] names) =>
        [.. names.Select(name => Column(columns, name, "integer")).Where(index => index >= 0)];

    private static int Column(IReadOnlyList<Db2Column> columns, string name, string kind)
    {
        var found = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (column.Name != name || column.Kind != kind || column.Interpretation is not null) continue;
            if (found >= 0) throw new ItemsSourceException(ItemsSourceException.SchemaAmbiguous, $"Items field {name} has more than one source column.");
            found = i;
        }
        return found;
    }

    /// <summary>The name from an ItemSparse row: Invalid on a text issue in Display_lang, Empty for "", else Valid (whitespace too).</summary>
    public ItemName NameOf(Db2Row sparse)
    {
        var value = Cell(sparse, Name);
        if (sparse.TextIssues?.Any(issue => issue.ColumnIndex == Name) == true) return new(ItemNameState.Invalid, null);
        return value.Length == 0 ? new(ItemNameState.Empty, "") : new(ItemNameState.Valid, value);
    }

    /// <summary>
    /// The summary of an Item row and its ItemSparse lookup. <paramref name="sparse"/> is the found row, else the outcome says
    /// which name state stands in for it.
    /// </summary>
    public ItemSummary Summary(Db2Row item, Db2Row? sparse, TableRowOutcome sparseOutcome, string? sparseKey)
    {
        var values = new List<ItemSourceValue>(6);
        foreach (var index in ItemFields) values.Add(new(ItemTable, ItemColumns[index].Name, index, Integer(item, index)));
        ItemName name;
        if (sparse is not null)
        {
            name = NameOf(sparse);
            foreach (var index in SparseFields) values.Add(new(SparseTable, SparseColumns![index].Name, index, Integer(sparse, index)));
        }
        else name = new(sparseOutcome switch
        {
            TableRowOutcome.Absent => ItemNameState.MissingSparse,
            TableRowOutcome.Protected => ItemNameState.SparseProtected,
            _ => ItemNameState.SparseUnknown,
        }, null);
        return new(item.Id, item.IsCopy, item.CopySourceId, name, values, Icon < 0 ? null : IconOf(Cell(item, Icon)),
            sparse is null && sparseOutcome == TableRowOutcome.Protected ? sparseKey : null);
    }

    /// <summary>An entry whose Item row couldn't be read: its ItemSparse row (or lookup outcome) only, and the Item outcome.</summary>
    public ItemSummary Unread(uint id, TableRowOutcome itemOutcome, string? itemKey, Db2Row? sparse, TableRowOutcome sparseOutcome, string? sparseKey)
    {
        var state = itemOutcome == TableRowOutcome.Protected ? ItemRowState.Protected : ItemRowState.Unknown;
        var values = new List<ItemSourceValue>(3);
        ItemName name;
        if (sparse is not null)
        {
            name = NameOf(sparse);
            foreach (var index in SparseFields) values.Add(new(SparseTable, SparseColumns![index].Name, index, Integer(sparse, index)));
        }
        else name = new(sparseOutcome switch
        {
            TableRowOutcome.Absent => ItemNameState.MissingSparse,
            TableRowOutcome.Protected => ItemNameState.SparseProtected,
            _ => ItemNameState.SparseUnknown,
        }, null);
        return new(id, sparse?.IsCopy ?? false, sparse?.CopySourceId, name, values, null,
            sparse is null && sparseOutcome == TableRowOutcome.Protected ? sparseKey : null, state,
            state == ItemRowState.Protected ? itemKey : null);
    }

    private static string Cell(Db2Row row, int index) => index < row.Values.Count ? row.Values[index]
        : throw new ItemsSourceException(ItemsSourceException.SourceInvalid, "A source row doesn't line up with its header columns.");

    private static string Integer(Db2Row row, int index)
    {
        var value = Cell(row, index);
        return Canonical(value, out _) ? value
            : throw new ItemsSourceException(ItemsSourceException.SourceInvalid, "A source-backed integer summary field is not a canonical decoded integer.");
    }

    // A canonical decimal int64 or uint64, as the reader writes every integer.
    private static bool Canonical(string value, out Int128 number) =>
        TableQueries.TryParseInteger(value, out number) && number >= long.MinValue && number <= ulong.MaxValue;

    // A canonical uint32 is kept; a canonical integer outside uint32 (a negative one) is no icon field; anything else is invalid.
    private static uint? IconOf(string value)
    {
        if (!Canonical(value, out var number))
            throw new ItemsSourceException(ItemsSourceException.SourceInvalid, "The icon source is not a canonical decoded integer.");
        return number >= 0 && number <= uint.MaxValue ? (uint)number : null;
    }
}
