namespace VibeMiner.Domain;

public sealed record ItemsContext(string ProductId, string BuildIdentity,
    string SourceRegion, string ResearchLocale);
public enum ItemNameState { Valid, Empty, Invalid, MissingSparse }
public sealed record ItemName(ItemNameState State, string? Value);
public sealed record ItemsJoinCounts(string ItemRows, string SparseRows,
    string MatchedRows, string ItemWithoutSparseRows, string SparseOnlyRows,
    string ValidNames, string EmptyNames, string InvalidNames);
public sealed record ItemFieldReference(string TableId, int ColumnIndex);
public sealed record ItemPropertyGroup(string Key, IReadOnlyList<ItemFieldReference> Fields);
public sealed record ItemSourceValue(string TableId, int ColumnIndex, string Value);
public sealed record ItemsSnapshot(ItemsContext Context, string ArtifactId,
    string PreparedAt, string FeatureRevision, string QueryPolicy,
    TableDataSnapshot ItemSource, TableDataSnapshot SparseSource,
    ItemsJoinCounts Counts, IReadOnlyList<ItemPropertyGroup> PropertyGroups);
public sealed record ItemsDependencyReadiness(string TableId, string State, string? ArtifactId);
public sealed record ItemsReadiness(ItemsSnapshot? Snapshot,
    IReadOnlyList<ItemsDependencyReadiness> Dependencies);
public sealed record ItemSummary(string Id, ItemName Name,
    IReadOnlyList<ItemSourceValue> BaseValues, string? IconFileDataId);
public sealed record ItemsPage(ItemsContext Context, string ArtifactId,
    string Query, string? ExactItemId, IReadOnlyList<ItemSummary> Entries, string? NextCursor);
public sealed record ItemDetail(ItemSummary Summary, TableDataRow ItemRow, TableDataRow? SparseRow);
public sealed record ItemInspectResult(ItemsContext Context, string ArtifactId, string ItemId, ItemDetail? Detail);
public sealed record ItemsPreparationProgress(string Stage, string Unit,
    long? Completed = null, long? Total = null, string? TableId = null);
