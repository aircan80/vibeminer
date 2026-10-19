namespace VibeMiner.Domain;

public enum CreatureGeometryState
{
    NotPrepared, MetadataNotResolved, Ready, NoDeclaredTriangles, MissingMembership,
    AmbiguousVariants, EncodingUnresolved, Protected, Unsupported, Unavailable, Corrupt, ResourceLimit
}
public enum CreatureGeometrySourceRole { Model, Skin }
public enum CreatureGeometryDependency { Metadata, Model, Skin, Artifact }
public sealed record CreatureGeometryTarget(CreatureDisplayTargetRole Role, string TableId, string ArtifactId,
    TableDataCoverage Coverage, string? MatchCount, CreatureDisplayRowEvidence? Row = null);
public sealed record CreatureGeometryMetadata(CreatureDisplayMetadataState State,
    CreatureSourceReference CreatureSource, TableDataCoverage CreatureCoverage, CreatureDisplaySlot? Selected,
    IReadOnlyList<CreatureGeometryTarget> Targets, CreatureDisplayAbsence? Absence = null,
    string? ModelId = null, string? ModelFileDataId = null, string? RequiredTableId = null,
    CreatureDisplayAppearance Appearance = CreatureDisplayAppearance.NotEvaluated);
public sealed record CreatureGeometrySource(CreatureGeometrySourceRole Role, string FileDataId,
    string ContentKey, string EncodingKey, string EncodedSha256, long EncodedBytes, long DecodedBytes,
    string FullVerifierRevision, string AssociationSha256, string VariantPolicy,
    string EncodingLookupRevision, string RootLookupRevision, string EncodingSelectionPolicy);
public sealed record CreatureGeometryProblem(string Code, string Message, bool Retryable,
    CreatureGeometryDependency? Dependency = null, CreatureDisplayTargetRole? Target = null);
public sealed record CreatureGeometryProgress(string Stage, string Label, string Unit,
    long? Completed = null, long? Total = null, CreatureGeometryDependency? Dependency = null);
public sealed record CreatureGeometryResult(CreaturesContext Context, string ArtifactId, string CreatureId,
    int Slot, string Policy, OpeningSourceReference Opening, CreatureGeometryState State,
    CreatureGeometryMetadata Metadata, IReadOnlyList<CreatureGeometrySource> Sources,
    CreatureGeometryManifest? Manifest = null, CreatureGeometryProblem? Problem = null);
