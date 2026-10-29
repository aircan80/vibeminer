namespace VibeMiner.Domain;

public enum ItemEffectsSourceRole { Bridge, Effect, Name }
public enum ItemEffectsSourceState
{ NotChecked, NotPrepared, Ready, Unavailable, Protected, Unsupported, Corrupt, ResourceLimit, Stale, NotInBuild }
public enum ItemEffectsReadinessState
{ NotPrepared, Ready, MissingItem, ItemKeyOutOfRange, Unavailable, Protected, Unsupported, Corrupt, ResourceLimit, NotInBuild }
public enum ItemEffectState
{ NotPrepared, ObservedReady, EffectKeyOutOfRange, FieldUnavailable, NotFoundInAdmittedScope,
  Ambiguous, Unavailable, Protected, Unsupported, Corrupt, ResourceLimit, Stale, NotInBuild }
public enum ItemEffectNameState
{ EffectNotReady, NotPrepared, Ready, EmptyText, InvalidText, TextTooLarge, SpellKeyOutOfRange,
  NotFoundInAdmittedScope, Ambiguous, Unavailable, Protected, Unsupported, Corrupt, ResourceLimit, Stale, NotInBuild }
public enum ItemEffectsAbsence { AbsentFromCompleteSource, UnknownOutsideReadableScope }
public enum ItemEffectEvidenceKind { StrictNumeric, RawPhysicalInteger, UnverifiedCandidate }

public sealed record ItemEffectsSelection(ItemsContext Context, string ItemsArtifactId, string ItemId);
public sealed record ItemEffectsPageRequest(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    int Limit = 16, string? After = null);
public sealed record ItemEffectsEdgeKey(string IndexOrdinal);
public sealed record ItemEffectDetailsRequest(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    IReadOnlyList<ItemEffectsEdgeKey> Edges);
public sealed record ItemEffectNamesRequest(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    string ItemEffectArtifactId, IReadOnlyList<ItemEffectsEdgeKey> Edges,
    string PolicyRevision = "spell-name-by-emitted-id-v1");

public sealed record ItemEffectsProblem(string Code, string Message, bool Retryable);
public sealed record ItemEffectsSource(ItemEffectsSourceRole Role, ItemEffectsSourceState State,
    TableDataSnapshot? Snapshot = null, string? HistoricalArtifactId = null, ItemEffectsProblem? Problem = null);
public sealed record ItemEffectsFieldReference(string TableId, int ColumnIndex);
public sealed record ItemEffectsBridgeFields(ItemEffectsFieldReference RowId,
    ItemEffectsFieldReference ItemEffectId, ItemEffectsFieldReference ItemId);
public sealed record ItemEffectsIndexSnapshot(ItemsContext Context, string ArtifactId, string PreparedAt,
    string Revision, string OrderPolicy, TableDataSnapshot BridgeSource,
    ItemEffectsBridgeFields Fields, string EmittedRows);
public sealed record ItemEffectsReadiness(ItemEffectsSelection Selection, bool Admitted,
    ItemEffectsReadinessState State, ItemEffectsIndexSnapshot? Snapshot = null,
    string? ObservedAssociations = null, ItemEffectsSource? Source = null,
    ItemEffectsProblem? Problem = null, string? Link = null);

public sealed record ItemEffectsSourceValue(string TableId, int ColumnIndex, string Value);
public sealed record ItemEffectsSourceReference(string TableId, string ArtifactId, string RowId,
    bool IsCopy, string? CopySourceId, string? SourceOrdinal = null, int? ColumnIndex = null);
public sealed record ItemEffectsEdge(ItemEffectsEdgeKey Key, ItemEffectsSourceReference Bridge,
    ItemEffectsSourceValue ParentItemId, ItemEffectsSourceValue ItemEffectId);
public sealed record ItemEffectsPage(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    string OrderPolicy, TableDataSnapshot BridgeSource, string ObservedAssociations,
    IReadOnlyList<ItemEffectsEdge> Edges, string? NextCursor);

public sealed record ItemEffectFieldEvidence(string Name, int ColumnIndex, string Value,
    ItemEffectEvidenceKind Evidence, TableColumnInterpretation? Interpretation,
    TableReference? CandidateReference = null);
public sealed record ItemEffectOutcome(ItemEffectsEdgeKey Key, string ItemEffectId,
    ItemEffectState State, string? ObservedCandidates = null,
    ItemEffectsSourceReference? Target = null, IReadOnlyList<ItemEffectFieldEvidence>? Fields = null,
    ItemEffectsAbsence? Absence = null, ItemEffectsProblem? Problem = null);
public sealed record ItemEffectDetailsResult(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    string PolicyRevision, ItemEffectsSource Source, IReadOnlyList<ItemEffectOutcome> Outcomes);

public sealed record ItemEffectSpellEvidence(ItemEffectState State, string ItemEffectId,
    string? ObservedCandidates = null, ItemEffectsSourceReference? Target = null,
    ItemEffectsSourceValue? SpellId = null, ItemEffectsAbsence? Absence = null, ItemEffectsProblem? Problem = null);
public sealed record ItemEffectNameIssue(string Code, ItemEffectsSourceReference Source);
public sealed record ItemEffectNameOutcome(ItemEffectsEdgeKey Key, ItemEffectSpellEvidence Effect,
    ItemEffectNameState State, string? ObservedCandidates = null,
    ItemEffectsSourceReference? Target = null, string? Text = null, ItemEffectNameIssue? Issue = null,
    string? TextUnits = null, ItemEffectsAbsence? Absence = null, ItemEffectsProblem? Problem = null);
public sealed record ItemEffectNamesResult(ItemEffectsSelection Selection, string BridgeIndexArtifactId,
    string ItemEffectArtifactId, string PolicyRevision, IReadOnlyList<ItemEffectsSource> Sources,
    IReadOnlyList<ItemEffectNameOutcome> Outcomes);

public sealed record ItemEffectsProgress(string Stage, string Unit, long? Completed = null,
    long? Total = null, string? TableId = null);
