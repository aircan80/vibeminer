namespace VibeMiner.Domain;

public enum ItemClassificationKind { Class, Subclass }
public enum ItemClassificationState
{
    Ready, EmptyText, InvalidText, MissingItem, FieldUnavailable, KeyOutOfRange,
    NotFound, Ambiguous, IncompleteTarget, Unavailable, Protected, Unsupported, Corrupt
}
public sealed record ItemClassificationProblem(string Code, string Message, bool Retryable = false);
public sealed record ItemClassificationTarget(string TableId, string RowId, int ColumnIndex,
    bool IsCopy, string? CopySourceId);
public sealed record ItemClassificationSource(ItemClassificationKind Kind, ItemClassificationState State,
    TableDataSnapshot? Snapshot, string? IndexArtifactId, ItemClassificationProblem? Problem = null);
public sealed record ItemClassificationValue(ItemClassificationState State, IReadOnlyList<ItemSourceValue> SourceKeys,
    string? Text = null, ItemClassificationTarget? Target = null, string? ObservedCandidates = null,
    ItemClassificationProblem? Problem = null);
public sealed record ItemClassificationOutcome(string ItemId, bool Admitted,
    ItemClassificationValue Class, ItemClassificationValue? Subclass = null);
public sealed record ItemClassificationBatch(ItemsContext Context, string ArtifactId, string PolicyRevision,
    IReadOnlyList<ItemClassificationSource> Sources, IReadOnlyList<ItemClassificationOutcome> Outcomes);
public sealed record ItemClassificationProgress(string Stage, string Unit,
    long? Completed = null, long? Total = null, string? TableId = null);
