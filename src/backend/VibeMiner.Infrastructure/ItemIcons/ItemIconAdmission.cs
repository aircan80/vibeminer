using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record ItemIconAdmission(ItemsContext Context, string FeatureArtifact, uint FileDataId,
    MetadataExpectation Expectation, AssetSourceAssociation Association, ContentReceipt Source, string PngRequestKey);
internal sealed record StoredItemIcon(ItemIconAdmission Admission, IconPngArtifact Artifact);

internal static class ItemIconAdmissionPolicy
{
    internal const int MaxDescriptorBytes = 4 << 20;
    internal static void Validate(StoredItemIcon saved, ItemsSnapshot snapshot, CapturedOpeningContext opening, uint fileDataId)
    {
        if (saved is null || saved.Admission is null || saved.Artifact is null || saved.Artifact.Proof is null ||
            saved.Admission.Source is null || saved.Admission.Expectation is null || saved.Admission.Association is null ||
            saved.Admission.Association.Configurations is null || saved.Admission.Association.Memberships is null ||
            saved.Admission.Association.EncodingSource is null || saved.Admission.Association.RootSource is null)
            throw Invalid();
        var a = saved.Admission; var source = a.Source; var expected = a.Expectation; var association = a.Association;
        if (a.Context != snapshot.Context || a.FeatureArtifact != snapshot.ArtifactId || a.FileDataId != fileDataId || fileDataId == 0 ||
            association.Request != Workspace(snapshot.Context) || association.ResearchLocale != snapshot.Context.ResearchLocale ||
            association.SourceReference != opening.Reference || opening.Build.Id != snapshot.Context.BuildIdentity ||
            opening.Build.ProductId != snapshot.Context.ProductId || opening.Reference.Region != snapshot.Context.SourceRegion ||
            association.VariantPolicy != AssetResolutionSession.VariantPolicy ||
            association.EncodingLookupRevision != ResolverArtifacts.EncodingRevision ||
            association.RootLookupRevision != AssetResolutionSessions.RootArtifactRevision ||
            association.EncodingSelectionPolicy != AssetResolutionSession.EncodingSelectionPolicy)
            throw Invalid();
        if (association.Configurations.Count != 2 ||
            !Configuration(association, "buildConfig", opening.Build.BuildConfig) || !Configuration(association, "cdnConfig", opening.Build.CdnConfig))
            throw Invalid();
        foreach (var table in new[] { snapshot.ItemSource, snapshot.SparseSource })
            if (!MetadataReceiptIdentity.Same(association.EncodingSource, table.Association.EncodingSource) ||
                !MetadataReceiptIdentity.Same(association.RootSource, table.Association.RootSource)) throw Invalid();
        if (association.Memberships.Count is < 1 or > 4096 || association.Memberships.Any(member =>
            member is null || member.Entry is null || member.BlockOrdinal < 0 || member.EntryOrdinal < 0 || member.Entry.FileDataId != fileDataId ||
            member.Entry.ContentKey != expected.ContentKey)) throw Invalid();
        if (expected.Role != "asset" || expected.ContentKey != source.ContentKey || expected.EncodingKey != source.EncodingKey ||
            expected.EncodedBytes.ToString(CultureInfo.InvariantCulture) != source.EncodedBytes ||
            expected.DecodedBytes.ToString(CultureInfo.InvariantCulture) != source.DecodedBytes ||
            source.Projection is not null || source.VerifierRevision != SelectedFileAcquirer.VerifierRevision)
            throw Invalid();
        var png = saved.Artifact; var proof = png.Proof;
        if (a.PngRequestKey != IconArtifacts.ComputeKey(source) || png.RequestKey != a.PngRequestKey ||
            proof.RequestKey != png.RequestKey || png.ArtifactId != IconArtifacts.ProofId(proof) ||
            proof.Source != new IconSourceBinding(source.ContentKey, source.EncodingKey, source.Sha256,
                expected.EncodedBytes, expected.DecodedBytes, source.VerifierRevision)) throw Invalid();
    }
    internal static void ValidateConfiguration(ConfigurationReceipt receipt, StoredConfiguration original)
    {
        if (receipt.Key != original.Key || receipt.Sha256 != original.Sha256 || receipt.ByteLength != original.ByteLength ||
            receipt.SourceUrl != original.SourceUrl || receipt.RetrievedAt != original.RetrievedAt || receipt.ParsedFieldCount < 1)
            throw Invalid();
    }
    private static bool Configuration(AssetSourceAssociation source, string role, string key) =>
        source.Configurations.Count(value => value is not null && value.Role == role && value.Key == key &&
            value.Sha256 is { Length: 64 } && value.ByteLength > 0) == 1;
    internal static WorkspaceRequest Workspace(ItemsContext context) => new(context.ProductId, context.BuildIdentity, context.SourceRegion);
    internal static DiscoveryException Invalid() => new("ITEM_ICON_ASSOCIATION_INVALID", "The retained icon differs from its exact current feature, source membership or PNG proof.");
}
