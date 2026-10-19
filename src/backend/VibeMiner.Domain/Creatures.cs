namespace VibeMiner.Domain;

public sealed record CreaturesContext(string ProductId, string BuildIdentity, string SourceRegion, string ResearchLocale);
public enum CreatureNameState { Valid, Empty, Invalid }
public enum CreatureDependencyState { NotPrepared, Ready }
public enum CreatureDisplayState { Declared, NoDeclaredDisplay, InvalidDisplayReference }
public enum CreatureInspectState { Found, NotFoundInAdmittedScope }
public sealed record CreatureSourceReference(string TableId, string ArtifactId, string RowId, int? ColumnIndex = null);
public sealed record CreatureNameIssue(string Code, CreatureSourceReference Source);
public sealed record CreatureName(CreatureNameState State, string? Value, CreatureNameIssue? Issue = null);
public sealed record CreatureFieldReference(string TableId, int ColumnIndex);
public sealed record CreatureSourceValue(string TableId, int ColumnIndex, string Value);
public sealed record CreatureDisplaySlot(int Slot, string DisplayId, string Probability, CreatureDisplayState State,
    CreatureFieldReference DisplayField, CreatureFieldReference ProbabilityField);
public sealed record CreatureSummary(string Id, CreatureName Name, IReadOnlyList<CreatureSourceValue> Facts,
    IReadOnlyList<CreatureDisplaySlot> DisplaySlots);
public sealed record CreatureCounts(string Rows, string ValidNames, string EmptyNames, string InvalidNames,
    string PositiveDisplaySlots, string ZeroDisplaySlots, string InvalidDisplaySlots);
public sealed record CreatureFieldMap(CreatureFieldReference Name, CreatureFieldReference NameAlt,
    CreatureFieldReference Title, CreatureFieldReference TitleAlt, IReadOnlyList<CreatureFieldReference> Facts,
    IReadOnlyList<CreatureFieldReference> DisplayIds, IReadOnlyList<CreatureFieldReference> Probabilities);
public sealed record CreaturesSnapshot(CreaturesContext Context, string ArtifactId, string PreparedAt,
    string FeatureRevision, string QueryPolicy, string SemanticProfile, TableDataSnapshot CreatureSource,
    CreatureCounts Counts, CreatureFieldMap Fields);
public sealed record CreaturesReadiness(CreaturesSnapshot? Snapshot, CreatureDependencyState DependencyState,
    string? SourceArtifactId = null);
public sealed record CreaturesPage(CreaturesContext Context, string ArtifactId, string Query, string? ExactCreatureId,
    IReadOnlyList<CreatureSummary> Entries, string? NextCursor);
public sealed record CreatureDetail(CreatureSummary Summary, TableDataRow CreatureRow, CreatureSourceReference Source);
public sealed record CreatureInspectResult(CreaturesContext Context, string ArtifactId, string CreatureId,
    CreatureInspectState State, TableDataCoverage Coverage, CreatureDetail? Detail);
public sealed record CreaturesPreparationProgress(string Stage, string Unit,
    long? Completed = null, long? Total = null, string? TableId = null);
