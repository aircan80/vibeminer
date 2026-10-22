namespace VibeMiner.Domain;

/// <summary>The declared integer domain of the followed target column, when the pinned layout gives one.</summary>
public sealed record TableFollowRange(string Minimum, string Maximum);

/// <summary>
/// Target rows whose declared target column equals one exact source value. Page is the matching rows (same shape
/// as tables.rows) and its TotalCount is the exact match count among the target's admitted rows; Scope says
/// whether those rows are the complete table or only its readable sections. Page is null unless State is ready.
/// </summary>
public sealed record TableFollowPage(string ProductId, string BuildIdentity, string SourceRegion, string ResearchLocale,
    string PolicyRevision, string TableId, string ArtifactId, int ColumnIndex, string Column, string Value,
    TableReference Reference, string TargetTableId, string TargetArtifactId, int? TargetColumnIndex, bool TargetIsId,
    TableFollowRange? TargetRange, string Scope, string State, string After, TableDataPage? Page,
    TableReferenceTextProblem? Problem);

public static class TableFollowStates
{
    public const string Ready = "ready", KeyOutOfRange = "keyOutOfRange", Unindexed = "unindexed", Unsupported = "unsupported";
}
