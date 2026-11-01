namespace VibeMiner.Formats.Tables;

/// <summary>
/// A row and its place. <see cref="Ordinal"/> is its 0-based position in the generated table's order (physical rows, then copies).
/// Values are the reader's exact strings: exact decimal integers of any width and sign, floats round-trip ("R"), arrays flattened
/// as <c>Name[i]</c>, and invalid text as "" with a <see cref="Db2TextIssue"/>.
/// </summary>
public sealed record TableRow(long Ordinal, Db2Row Row);

/// <summary>
/// One page of a table. Rows are those with <see cref="TableRow.Ordinal"/> greater than <see cref="After"/>, in order;
/// <see cref="Limit"/> is 1 to <see cref="TableQueries.MaxLimit"/> (1000), <see cref="After"/> at least -1. All
/// <see cref="Filters"/> must hold. <see cref="Identity"/> is the identity a previous page gave: when the table's identity
/// has changed since, the page is Stale and never silently re-based.
/// </summary>
public sealed record TablePageRequest(TableRef Table, long After = -1, int Limit = TableQueries.DefaultLimit,
    IReadOnlyList<TableFilter>? Filters = null, string? Identity = null);

public enum TablePageState
{
    Ok,
    /// <summary>The table isn't readable (not Ready or Partial); <see cref="TablePage.Table"/> says why.</summary>
    NotReadable,
    /// <summary>The request's identity isn't the table's current one.</summary>
    Stale,
    /// <summary><see cref="TablePageRequest.After"/> is at or past the row count.</summary>
    InvalidCursor,
    /// <summary>A filter can't be applied; <see cref="TablePage.Code"/> says why (TABLE_FILTER_*).</summary>
    BadFilter,
}

/// <summary>
/// A page's outcome. Only Ok carries rows. <see cref="NextAfter"/> is the After of the next page, null only at the end; a
/// filtered page can come back with fewer rows than the limit (or none) and still continue. <see cref="Identity"/> is the
/// table's identity (generated source and key status) on Ok and Stale, to pass back with the next page. <see cref="Scanned"/>
/// is the rows looked at. A Partial table pages its readable rows; <see cref="TableEntry.Counts"/> says the scope.
/// </summary>
public sealed record TablePage(TableEntry Table, TablePageState State, IReadOnlyList<TableRow> Rows, long? NextAfter, string? Identity, long Scanned,
    string? Code = null, string? Message = null);

public enum TableFilterOp
{
    Equals,
    /// <summary>From <see cref="TableFilter.Value"/> to <see cref="TableFilter.Upper"/>, both inclusive.</summary>
    Between,
}

/// <summary>
/// One condition on a column: a flattened name (<c>Flags[1]</c>) or an array's base name, meaning any element.
/// <see cref="Upper"/> is set for Between only. A filter of the wrong shape throws; one that doesn't fit the column is BadFilter.
/// </summary>
public sealed record TableFilter(string Column, TableFilterOp Op, string Value, string? Upper = null);

public enum TableRowOutcome
{
    Found,
    /// <summary>No row has the ID, and the table is complete (Ready).</summary>
    Absent,
    /// <summary>The ID is in a keyed section's encrypted-ID list; <see cref="TableRowResult.ProtectedKey"/> names the key.</summary>
    Protected,
    /// <summary>Not in the readable sections of a partial table, or the table isn't readable. Never proof of absence.</summary>
    Unknown,
}

/// <summary>A row by ID. <see cref="Row"/> is set for Found, <see cref="ProtectedKey"/> for Protected.</summary>
public sealed record TableRowResult(TableEntry Table, TableRowOutcome Outcome, TableRow? Row, string? ProtectedKey);
