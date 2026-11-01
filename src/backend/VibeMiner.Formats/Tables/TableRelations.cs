using VibeMiner.Formats.Definitions;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// Follows one foreign key column (flattened name, <c>Name[i]</c> for an array element) of row <see cref="Id"/> to the declared
/// target table and column; the target column need not be its ID. 0 is a real value, not "no reference". Several matches are all
/// returned, paged like <see cref="TablePageRequest"/>: <see cref="Limit"/> is 1 to <see cref="TableQueries.MaxLimit"/>. The full rules are <see cref="TableQueries.FollowAsync"/>.
/// </summary>
public sealed record TableFollowRequest(TableRef Table, uint Id, string Column, long After = -1, int Limit = TableQueries.DefaultLimit);

public enum TableFollowOutcome
{
    Found,
    /// <summary>The target is complete and no row matches.</summary>
    Absent,
    /// <summary>
    /// No match in a partial or unreadable target, or the target can't be looked in (not in the build, no definition, no such
    /// column, a protected ID); <see cref="TableFollowResult.Target"/> and <see cref="TableFollowResult.Code"/> say why.
    /// Found from a partial target (a non-ID target column) is a lower bound: more rows may sit in its unreadable parts.
    /// </summary>
    Unknown,
    /// <summary>The value doesn't fit the target column's declared width and sign (an ID target: 0 to uint max). Never wrapped.</summary>
    KeyOutOfRange,
    /// <summary>The column declares no foreign key in the resolved schema, or a side keeps raw evidence (<see cref="TableQueries.RelationRawCode"/>).</summary>
    NotAReference,
    /// <summary>The source row wasn't Found; <see cref="TableFollowResult.Source"/> says how.</summary>
    SourceRow,
    /// <summary>The target column isn't the ID and the target has more rows than one scan may look at.</summary>
    Unindexed,
}

/// <summary>
/// A follow's outcome. <see cref="Reference"/> is the declared target when the column has one; <see cref="Target"/> the target
/// table's entry when it was looked at. Only Found carries rows; <see cref="NextAfter"/> pages them as <see cref="TablePage"/> does.
/// Found from a Partial target (Target state Partial) by a non-ID column is a lower bound. <see cref="ProtectedKey"/> names the
/// TACT key when an ID target's row is protected (Unknown with <see cref="TableQueries.RelationProtectedCode"/>).
/// </summary>
public sealed record TableFollowResult(TableFollowOutcome Outcome, TableRowResult Source, ForeignKey? Reference, TableEntry? Target,
    IReadOnlyList<TableRow> Rows, long? NextAfter, string? Code = null, string? Message = null, string? ProtectedKey = null);

/// <summary>
/// The rows that point at row <see cref="Id"/>: from every table whose resolved schema on this build declares a foreign key to
/// this table (any target column; the value looked up is this row's value of that column). At most
/// <see cref="LimitPerSource"/> row IDs per source, 1 to <see cref="TableQueries.MaxRowsPerSource"/>. Only declared references
/// among tables with a definition count. The full rules are <see cref="TableQueries.ReferencedByAsync"/>.
/// </summary>
public sealed record TableReferencesRequest(TableRef Table, uint Id, int LimitPerSource = TableQueries.DefaultLimit);

/// <summary>How much of a source table was searched.</summary>
public enum TableReferenceScope
{
    Complete,
    /// <summary>Only the readable rows of a partial table: the IDs are a lower bound.</summary>
    Partial,
    /// <summary>Not searched: the table isn't readable (its entry says why), or <see cref="TableReferenceSource.Code"/> says why.</summary>
    NotChecked,
}

/// <summary>
/// One source column pointing at the table (an array's base name: any element). <see cref="RowIds"/> are its matching rows,
/// ascending by ordinal, at most the limit; <see cref="Truncated"/> says more matched. <see cref="Code"/> (TABLE_RELATION_*) says
/// why a readable source was NotChecked.
/// </summary>
public sealed record TableReferenceSource(TableEntry Table, string Column, ForeignKey Reference, TableReferenceScope Scope,
    IReadOnlyList<uint> RowIds, bool Truncated, string? Code = null);

public enum TableReferencesOutcome
{
    /// <summary>The sources were searched; some found rows, some couldn't be searched completely, or some tables were unresolved.</summary>
    Searched,
    /// <summary>Every candidate source was searched completely, none has a matching row, and no table was unresolved.</summary>
    NoReferences,
    /// <summary>The row itself wasn't Found; <see cref="TableReferencesResult.Row"/> says how.</summary>
    SourceRow,
}

/// <summary>
/// A referenced-by outcome: the row looked up and every candidate source, by FileDataID then column. <see cref="Unresolved"/> are
/// the listed tables whose schema couldn't be resolved without opening them (or whose definition is unusable): they may point at
/// the row, so their presence rules out NoReferences.
/// </summary>
public sealed record TableReferencesResult(TableReferencesOutcome Outcome, TableRowResult Row, IReadOnlyList<TableReferenceSource> Sources,
    IReadOnlyList<TableEntry> Unresolved);
