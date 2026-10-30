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
/// <summary>A reviewed fact of one admitted CreatureDisplayInfo or CreatureModelData row (creature-display-facts-v1).</summary>
public enum CreatureDisplayFactState { Value, Unsupported }
/// <summary>One exact source cell: the flattened column name, its index in the target snapshot's columns and the value as read.</summary>
public sealed record CreatureDisplayFactCell(string Column, int ColumnIndex, string Value);
public sealed record CreatureDisplayFact(string Key, CreatureDisplayFactState State, IReadOnlyList<CreatureDisplayFactCell> Cells);
/// <summary>The uniquely matched row of one chain role: its reviewed facts in fixed order, then every other column as read.</summary>
public sealed record CreatureDisplayRecordFacts(CreatureDisplayTargetRole Role, string TableId, string Table, string RecordId,
    IReadOnlyList<CreatureDisplayFact> Facts, IReadOnlyList<CreatureDisplayFactCell> Raw);
public sealed record CreatureDisplayFactsResult(CreatureDisplayMetadataResult Metadata, string FactsRevision,
    IReadOnlyList<CreatureDisplayRecordFacts> Records);
public sealed record CreatureLabel(string Value, string Label);
public sealed record CreatureLabelLists(string LabelRevision, IReadOnlyList<CreatureLabel> Classification);
