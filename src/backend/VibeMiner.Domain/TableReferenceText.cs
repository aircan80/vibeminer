namespace VibeMiner.Domain;

/// <summary>
/// Where a reference's text comes from. For a redirect (Spell.ID to SpellName) Table names the actual source.
/// MeasuredEncodedBytes is the reviewed encoded size of the text table on Retail 12.1.5.70448; LoadPolicy says
/// whether the desktop prepares it automatically or only when asked.
/// </summary>
public sealed record TableReferenceTextSource(string TableId, string Table, string TextColumn, string MatchColumn,
    string? ArtifactId, string? Scope, string MeasuredEncodedBytes, string LoadPolicy);
public sealed record TableReferenceTextProblem(string Code, string Message);
/// <summary>One declared reference column on the page. State is a column-wide fact; values carry per-key outcomes.</summary>
public sealed record TableReferenceTextColumn(int ColumnIndex, string Name, TableReference Reference, string State,
    TableReferenceTextSource? TextSource, TableReferenceTextProblem? Problem);
public sealed record TableReferenceTextRow(string Id, bool IsCopy, string? CopySourceId);
public sealed record TableReferenceTextValue(int ColumnIndex, string Value, string State, string? Text,
    string MatchCount, TableReferenceTextRow? Row);
public sealed record TableReferenceTextPage(string ProductId, string BuildIdentity, string SourceRegion,
    string TableId, string ResearchLocale, string ArtifactId, string After, string? RecordId, string PolicyRevision,
    IReadOnlyList<string> RowIds, IReadOnlyList<TableReferenceTextColumn> Columns, IReadOnlyList<TableReferenceTextValue> Values);

public static class TableReferenceTextStates
{
    // Column states.
    public const string Ready = "ready", NotPrepared = "notPrepared", NoTextRule = "noTextRule", NoDefinition = "noDefinition",
        Unsupported = "unsupported", Unindexed = "unindexed", Unreadable = "unreadable";
    // Value states.
    public const string Resolved = "resolved", Empty = "empty", NotFound = "notFound", KeyOutOfRange = "keyOutOfRange",
        Multiple = "multiple", TextUnreadable = "unreadable", TextTooLong = "textTooLong";
}
