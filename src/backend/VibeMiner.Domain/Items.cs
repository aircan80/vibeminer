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
/// <summary>One source cell behind an item fact (items.facts, item-facts-v1).</summary>
public sealed record ItemFactValue(string TableId, string Table, string Column, int ColumnIndex,
    string RecordId, string Value, string? Label);
/// <summary>A reviewed projection of one meaning (quality, required level, ...) over the item's Item and ItemSparse rows.
/// State: value, missing, unknown, unsupported or invalidText. Agreement: single (one table), agree or disagree (both tables,
/// same element count) or separate (both tables, different element counts, e.g. Classic Item.DamageType[0–4] beside ItemSparse.DamageType).</summary>
public sealed record ItemFact(string Key, string State, string Agreement, IReadOnlyList<ItemFactValue> Values);
/// <summary>One reviewed label: an exact canonical decimal source value and its name.</summary>
public sealed record ItemLabel(string Value, string Label);
/// <summary>items.labels (item-facts-v1): the reviewed label lists, one source of truth with the fact labels.</summary>
public sealed record ItemLabelLists(string LabelRevision, IReadOnlyList<ItemLabel> Quality,
    IReadOnlyList<ItemLabel> InventoryType, IReadOnlyList<ItemLabel> Bonding, IReadOnlyList<ItemLabel> Trigger);
/// <summary>Sparse: present, missing (absent with complete coverage) or unknown (absent with partial coverage).
/// Facts are null exactly when the item is outside the admitted Item scope; Sparse is then looked up (ItemSparse-only rows exist).</summary>
public sealed record ItemFactsResult(ItemsContext Context, string ArtifactId, string ItemId,
    string PolicyRevision, string LabelRevision, string Sparse, IReadOnlyList<ItemFact>? Facts);
public sealed record ItemsPreparationProgress(string Stage, string Unit,
    long? Completed = null, long? Total = null, string? TableId = null);
