using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Items;

/// <summary>
/// Layer 6: a build's items per build and locale (D044, D450): Item left joined to ItemSparse by row ID, names from
/// <c>ItemSparse.Display_lang</c> only. Implementations: <see cref="ItemsService"/> over layer 5, and the in-memory fake in
/// <c>VibeMiner.Concepts.Testing</c>; one contract suite runs against both. Table, column and file problems never throw: they
/// come back as named states. Calls throw only for bad arguments (<see cref="ArgumentException"/>), cancellation, a disposed
/// handle (<see cref="ObjectDisposedException"/>) and what layer 5 throws (<see cref="TablesBusyException"/>).
/// </summary>
public interface IItems
{
    /// <summary>Opens the build's tables for one locale (a <see cref="FileLocales"/> name). Opening reads no table.</summary>
    Task<ItemsOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken);
}

/// <summary>The outcome of <see cref="IItems.ForAsync"/>. <see cref="Items"/> is set only when <see cref="State"/> is Open; a failure keeps layer 4's state, code and time.</summary>
public sealed record ItemsOpenResult(BuildRef Build, string Locale, FilesOpenState State, IBuildItems? Items, string? Code, string? Message,
    DateTimeOffset? ObservedAt);

/// <summary>One build and locale's items. Calls may run concurrently. Dispose the handle when done: every later call throws <see cref="ObjectDisposedException"/>.</summary>
public interface IBuildItems : IAsyncDisposable
{
    BuildRef Build { get; }
    string Locale { get; }

    /// <summary>One page of items. See <see cref="ItemQuery"/> and <see cref="ItemsPage"/>. Reads no icon.</summary>
    Task<ItemsPage> ListAsync(ItemQuery query, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>One item with its source rows and its icon's header (inspected here, never in a list).</summary>
    Task<ItemResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken);
}

/// <summary>
/// A list request. <see cref="Query"/> is a literal name substring (ordinal, ignore case; empty lists every item), at most 128
/// Unicode scalars and 512 UTF-8 bytes, no NUL and no lone surrogate. <see cref="ExactId"/> lists only that item and can't be
/// combined with a non-empty query. <see cref="Limit"/> is 1 to 50. <see cref="After"/> is the <see cref="ItemsPage.Next"/> of
/// the previous page of the same request; see <see cref="ItemQueries.Validate"/>.
/// </summary>
public sealed record ItemQuery(string Query = "", uint? ExactId = null, int Limit = ItemQueries.MaxLimit, string? After = null);

public enum ItemsPageState
{
    Ok,
    /// <summary>The Item table isn't readable (not Ready or Partial); <see cref="ItemsPage.Item"/> says why.</summary>
    ItemNotReadable,
    /// <summary>A name search needs ItemSparse, and it isn't readable; <see cref="ItemsPage.Sparse"/> says why.</summary>
    SparseNotReadable,
    /// <summary>ITEMS_NAME_UNSUPPORTED: ItemSparse is readable but has no one plain string column <c>Display_lang</c>.</summary>
    NameUnsupported,
    /// <summary>ITEMS_SCHEMA_AMBIGUOUS: a field this reads has more than one column of its name and kind.</summary>
    SchemaAmbiguous,
    /// <summary>ITEMS_SOURCE_INVALID: a summary or icon cell isn't a canonical decimal integer.</summary>
    SourceInvalid,
    /// <summary>A table's identity changed since the cursor was made; the page is never re-based.</summary>
    Stale,
    /// <summary>INVALID_CURSOR: the cursor belongs to another request, or points past the table.</summary>
    InvalidCursor,
}

/// <summary>
/// A page's outcome. Only Ok carries entries, in the scanned table's row order (D451). <see cref="Next"/> continues the same
/// request, null only at the end: a page stops after <see cref="ItemQueries.MaxLimit"/> matches or when its row scan budget is
/// spent, so it can come back with fewer entries (or none) and still continue. <see cref="Scanned"/> is the rows looked at.
/// <see cref="Item"/> and <see cref="Sparse"/> are the tables' checked entries (null when the call didn't reach them); a Partial
/// table lists its readable rows and its counts say the scope.
/// </summary>
public sealed record ItemsPage(ItemsPageState State, IReadOnlyList<ItemSummary> Entries, string? Next, long Scanned, TableEntry? Item,
    TableEntry? Sparse, string? Code = null, string? Message = null);

public enum ItemNameState
{
    Valid,
    /// <summary>The Display_lang text has length 0.</summary>
    Empty,
    /// <summary>The Display_lang cell has a text issue.</summary>
    Invalid,
    /// <summary>ItemSparse has no row for the ID, and ItemSparse is complete (Ready): absence is proven.</summary>
    MissingSparse,
    /// <summary>The ItemSparse row is outside the readable sections of a Partial table, or ItemSparse isn't readable. Never proof of absence.</summary>
    SparseUnknown,
    /// <summary>The ItemSparse row is in a keyed section whose key isn't known; <see cref="ItemSummary.SparseKey"/> names it.</summary>
    SparseProtected,
}

/// <summary>An item name. <see cref="Value"/> is set for Valid (whitespace is Valid) and "" for Empty, else null.</summary>
public sealed record ItemName(ItemNameState State, string? Value);

/// <summary>One source cell of a summary field: the table's definition name, the header column and its exact value.</summary>
public sealed record ItemSourceValue(string Table, string Column, int ColumnIndex, string Value);

/// <summary>
/// One listed item. <see cref="Values"/> are Item's ClassID, SubclassID and InventoryType, then ItemSparse's OverallQualityID,
/// ItemLevel and InventoryType, each only when its table's layout has that plain integer column (and the ItemSparse ones only
/// when the row was found). <see cref="IconFileDataId"/> is <c>Item.IconFileDataID</c> when it is a uint32; null when the
/// column is missing or the value is out of range. <see cref="SparseKey"/> is set only for SparseProtected. <see cref="Item"/> is
/// Found for every row read from Item; a name match or exact ID whose Item row can't be read (a Partial Item) is listed as
/// Unknown or Protected (<see cref="ItemKey"/> names the key) with only its ItemSparse cells, never dropped as if absent.
/// </summary>
public sealed record ItemSummary(uint Id, bool IsCopy, uint? CopySourceId, ItemName Name, IReadOnlyList<ItemSourceValue> Values,
    uint? IconFileDataId, string? SparseKey = null, ItemRowState Item = ItemRowState.Found, string? ItemKey = null);

/// <summary>Whether a listed entry's Item row was read. Only a proven absence (a Ready Item) leaves an ID out of a list.</summary>
public enum ItemRowState
{
    Found,
    /// <summary>Outside the readable sections of a Partial Item table. Never proof of absence.</summary>
    Unknown,
    /// <summary>In a keyed section of Item whose key isn't known; <see cref="ItemSummary.ItemKey"/> names it.</summary>
    Protected,
}

public enum ItemResultState
{
    Found,
    /// <summary>No Item row has the ID, and Item is complete (Ready). An ItemSparse-only row is never an item.</summary>
    Absent,
    /// <summary>Not in the readable sections of a Partial Item table. Never proof of absence.</summary>
    Unknown,
    /// <summary>The ID is in a keyed section of Item whose key isn't known; <see cref="ItemResult.ProtectedKey"/> names it.</summary>
    Protected,
    ItemNotReadable,
    NameUnsupported,
    SchemaAmbiguous,
    SourceInvalid,
}

/// <summary>An item by ID. <see cref="Detail"/> is set only for Found.</summary>
public sealed record ItemResult(ItemResultState State, ItemDetail? Detail, TableEntry? Item, TableEntry? Sparse, string? ProtectedKey = null,
    string? Code = null, string? Message = null);

/// <summary>
/// One item's detail: the summary, its source rows exactly as layer 5 read them, its icon, its facts (D454) and its class and
/// subclass labels (D455). Facts and labels are states of their own and never block the detail. Later slices add effects as
/// further members. <see cref="Icons"/> are the icons with the field each came from: <see cref="Icon"/> (Item.IconFileDataID)
/// first, then the default appearance's DefaultIconFileDataID when there is one (D469); none is picked as "the" icon.
/// <see cref="Appearances"/> are the item's appearances and model files (D467-D470), never in a list.
/// </summary>
public sealed record ItemDetail(ItemSummary Summary, TableRow ItemRow, TableRow? SparseRow, ItemIcon Icon, ItemFactSet Facts, ItemClassLabels Labels)
{
    public required IReadOnlyList<ItemIconSourced> Icons { get; init; }
    public required ItemAppearanceSet Appearances { get; init; }
}

public enum ItemIconState
{
    /// <summary>Item has no IconFileDataID integer column, or the value isn't a uint32.</summary>
    FieldUnavailable,
    /// <summary>IconFileDataID is 0.</summary>
    NoIcon,
    /// <summary>The build's files didn't open; <see cref="ItemIcon.Code"/> is layer 4's.</summary>
    FilesNotOpen,
    /// <summary>The BLP header was read and passes the decoder's checks.</summary>
    Ready,
    Unsupported,
    Invalid,
    MipUnavailable,
    LimitExceeded,
    /// <summary>The header lies in holes; <see cref="ItemIcon.HoleKeys"/> names the keys.</summary>
    Protected,
    /// <summary>Layer 4 didn't give the bytes; <see cref="ItemIcon.FileState"/> says why (UnknownFile, NeedsNetwork, Missing...).</summary>
    FileFailed,
}

/// <summary>
/// An item's icon (D453). <see cref="Image"/> is layer 5's <see cref="BlpImageState"/> when the file was inspected.
/// <see cref="Header"/> is set whenever the BLP2 header was read (so an unsupported profile is named), <see cref="Encoding"/>
/// and <see cref="MipCount"/> only for Ready. <see cref="ContentKey"/> is the CKey of the ROOT variant layer 4 picked for this
/// locale (D160, D453), when the build lists the file. Codes, messages and times are the layer's that said so.
/// </summary>
public sealed record ItemIcon(ItemIconState State, uint? FileDataId, BlpImageState? Image = null, BlpHeader? Header = null,
    Blp2Encoding? Encoding = null, int? MipCount = null, FileReadState? FileState = null, IReadOnlyList<string>? HoleKeys = null,
    string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null, string? ContentKey = null);
