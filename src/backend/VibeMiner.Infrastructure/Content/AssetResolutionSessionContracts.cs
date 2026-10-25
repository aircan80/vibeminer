using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public enum AssetResolutionStatus { Ready, MissingMembership, AmbiguousVariants, EncodingUnresolved }

public sealed record AssetSourceAssociation(WorkspaceRequest Request, string ResearchLocale,
    OpeningSourceReference SourceReference, IReadOnlyList<ConfigurationReceipt> Configurations,
    MetadataReceipt EncodingSource, MetadataReceipt RootSource, string VariantPolicy,
    IReadOnlyList<RootTargetMatch> Memberships,
    string EncodingLookupRevision = ResolverArtifacts.EncodingRevision,
    string RootLookupRevision = AssetResolutionSessions.RootArtifactRevision,
    string EncodingSelectionPolicy = AssetResolutionSession.EncodingSelectionPolicy);

/// <summary>A selected source expectation, not acquired or readable asset bytes. Missing membership
/// is observed only in this exact authenticated complete ROOT source. An ambiguous result whose memberships
/// differ only in the HighResTexture bit also carries <see cref="TextureVariant"/>; only geometry textures may use it (D095).</summary>
public sealed record AssetResolutionResult(uint FileDataId, AssetResolutionStatus Status,
    MetadataExpectation? Expectation, AssetSourceAssociation Association, AssetTextureVariant? TextureVariant = null);

/// <summary>The D095 standard-resolution choice and its encoding expectation; null expectation means its content key
/// has no encoding row in this build.</summary>
public sealed record AssetTextureVariant(RootTextureVariantChoice Choice, MetadataExpectation? Expectation);

public sealed record AssetResolutionBatch(IReadOnlyList<AssetResolutionResult> Results, bool ScannedRoot);
