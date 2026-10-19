namespace VibeMiner.Domain;

public enum CreatureDisplayMetadataState
{
    NotPrepared, CreatureNotFoundInAdmittedScope, NoDeclaredDisplay, InvalidDisplayReference,
    DisplayMissingInAdmittedScope, DisplayAmbiguous, NoDeclaredModel, ModelMissingInAdmittedScope,
    ModelAmbiguous, NoDeclaredModelFile, MetadataResolved, Protected, Unsupported, Unavailable, Corrupt, ResourceLimit
}
public enum CreatureDisplayAbsence { UnknownOutsideReadableScope, AbsentFromCompleteSource }
public enum CreatureDisplayAppearance { NotEvaluated }
public enum CreatureDisplayTargetRole { Display, Model }
public sealed record CreatureDisplayMetadataProblem(string Code, string Message, bool Retryable,
    CreatureDisplayTargetRole? Target = null);
public sealed record CreatureDisplayTargetReference(string TableId, string ArtifactId, string RowId, int? ColumnIndex = null);
public sealed record CreatureDisplayTargetValue(string TableId, int ColumnIndex, string Value);
public sealed record CreatureDisplayRowEvidence(CreatureDisplayTargetReference Source,
    bool IsCopy, string? CopySourceId, CreatureDisplayTargetValue EdgeValue);
public sealed record CreatureDisplayTarget(CreatureDisplayTargetRole Role, TableDataSnapshot Snapshot,
    string? MatchCount, CreatureDisplayRowEvidence? Row = null);
public sealed record CreatureDisplayMetadataResult(CreaturesContext Context, string ArtifactId, string CreatureId,
    int Slot, string MetadataPolicy, OpeningSourceReference Opening, CreatureSourceReference CreatureSource,
    TableDataCoverage CreatureCoverage, CreatureDisplaySlot? Selected, CreatureDisplayMetadataState State,
    CreatureDisplayAppearance Appearance, IReadOnlyList<CreatureDisplayTarget> Sources,
    CreatureDisplayAbsence? Absence = null, string? ModelId = null, string? ModelFileDataId = null,
    string? RequiredTableId = null, CreatureDisplayMetadataProblem? Problem = null);
