namespace VibeMiner.Domain;

public enum CreatureGeometryState
{
    NotPrepared, MetadataNotResolved, Ready, NoDeclaredTriangles, MissingMembership,
    AmbiguousVariants, EncodingUnresolved, Protected, Unsupported, Unavailable, Corrupt, ResourceLimit
}
public enum CreatureGeometrySourceRole { Model, Skin }
public enum CreatureGeometryDependency { Metadata, Model, Skin, Artifact, Texture }
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
    CreatureGeometryManifest? Manifest = null, CreatureGeometryProblem? Problem = null,
    CreatureGeometryAppearance? Appearance = null);

public enum CreatureGeometryAppearanceState
{
    NotPrepared, Ready, CharacterModel, MultiTexture, BlendUnsupported, TextureWeight, TextureTransform, MaterialUnsupported,
    TextureUnresolved, TextureMissing, TextureAmbiguous, TextureProtected, TextureUnsupported, TextureCorrupt, TextureUnavailable,
    ResourceLimit, Corrupt, CombinerUnsupported
}
public enum CreatureGeometryTextureBinding { Txid, Variation }
public enum CreatureGeometryTextureState { Decoded, Unresolved, NotReached, Missing, Ambiguous, Protected, Unsupported, Corrupt, Unavailable, ResourceLimit }
/// <summary>The CreatureDisplayInfo row values the texture chain reads. TextureVariations keeps all four positions, zeros included.</summary>
public sealed record CreatureGeometryAppearanceDisplay(string DisplayId, string ExtendedDisplayInfoId, IReadOnlyList<string> TextureVariations);
public sealed record CreatureGeometryTextureImage(int Width, int Height, int MipLevel, int BaseWidth, int BaseHeight, string Encoding);
public sealed record CreatureGeometryTextureSource(string ContentKey, string EncodingKey, string EncodedSha256, long EncodedBytes,
    long DecodedBytes, string FullVerifierRevision);
/// <summary>The ROOT variant a texture used when its memberships differ only in the HighResTexture bit (D095): the chosen
/// standard-resolution content key and flags, and the high-resolution variant left out. Null when ROOT has one content key.</summary>
public sealed record CreatureGeometryTextureVariant(string Policy, string ContentKey, uint ContentFlags, string HighResolutionContentKey, uint HighResolutionContentFlags);
public sealed record CreatureGeometryTexture(int Ordinal, int Type, CreatureGeometryTextureBinding Binding, int? Variation,
    string? FileDataId, CreatureGeometryTextureState State, CreatureGeometryTextureImage? Image = null, CreatureGeometryTextureSource? Source = null,
    CreatureGeometryTextureVariant? Variant = null);
/// <summary>Declared parts the textured draw leaves out; all are notEvaluated and never drawn. BlendedBatches are
/// batches with blend mode 2-7 (D094); EnvTransforms are env-map stages drawn without their UV animation.</summary>
public sealed record CreatureGeometryEffects(int Particles, int Ribbons, int Lights, int ShadowBatches, int UnbatchedSections,
    int BlendedBatches = 0, int EnvTransforms = 0);
public sealed record CreatureGeometryAppearanceProblem(string Code, string Message, bool Retryable, int? BatchOrdinal = null, int? TextureOrdinal = null);
public sealed record CreatureGeometryAppearance(string Policy, CreatureGeometryAppearanceState State,
    CreatureGeometryAppearanceDisplay? Display, IReadOnlyList<CreatureGeometryTexture> Textures, CreatureGeometryEffects? Effects,
    CreatureGeometryAppearanceManifest? Manifest = null, CreatureGeometryAppearanceProblem? Problem = null);
