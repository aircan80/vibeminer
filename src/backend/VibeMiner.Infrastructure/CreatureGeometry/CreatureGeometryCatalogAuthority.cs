using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private void GeometryRequireSelection(GeometrySelection selection, SqliteTransaction transaction, CancellationToken token)
    {
        var remaining = CreaturesPolicy.MaxDescriptorBytes;
        RequireCreatureFeature(selection.Opening, selection.Core, transaction, ref remaining);
        foreach (var target in new[] { selection.Display, selection.Model })
        {
            token.ThrowIfCancellationRequested();
            var current = DisplayAdmission(selection.Opening, target.TableId, transaction) ?? throw GeometryStale();
            if (current.PreparedAt != target.Admission.PreparedAt || !CreatureAuthority(current.Artifact, target.Admission.Artifact) ||
                !CreatureAuthority(current.Source, target.Admission.Source) || !CreatureAuthority(current.Association, target.Admission.Association)) throw GeometryStale();
        }
    }

    private void GeometryRequireAll(GeometrySelection selection, GeometrySourceCapsulePair source,
        GeometryArtifactDescriptor? artifact, SqliteTransaction transaction, CancellationToken token)
    {
        GeometryRequireSelection(selection, transaction, token);
        var identities = new HashSet<(string Sha, long Bytes)>();
        void Add(string sha, long bytes)
        { if (!DownloadHash(sha, 64) || bytes is < 1 or > VerifiedObjectStore.MaxObjectBytes) throw GeometryInvalid(); identities.Add((sha, bytes)); }
        void Metadata(MetadataReceipt receipt) => Add(receipt.Sha256, DownloadNumber(receipt.EncodedBytes));
        void Content(ContentReceipt receipt) => Add(receipt.Sha256, DownloadNumber(receipt.EncodedBytes));
        void Rows(TableRowsArtifact rows) => Add(rows.Sha256, rows.ByteLength);
        Add(selection.Core.Sha256, selection.Core.ByteLength); Rows(selection.Core.Binding.Rows); Content(selection.Core.Binding.Source.Source);
        Metadata(selection.Core.Binding.Source.Association.EncodingSource); Metadata(selection.Core.Binding.Source.Association.RootSource);
        foreach (var configuration in selection.Core.Binding.Configurations) Add(configuration.Sha256, configuration.ByteLength);
        foreach (var target in new[] { selection.Display, selection.Model })
        { Rows(target.Admission.Artifact); Content(target.Admission.Source); Metadata(target.Admission.Association.EncodingSource); Metadata(target.Admission.Association.RootSource); }
        for (var i = 0; i < 2; i++)
        {
            token.ThrowIfCancellationRequested(); var capsule = i == 0 ? source.Model : source.Skin; var remaining = GeometrySourceBytes;
            var originals = GeometryOriginalSources(capsule.Association, capsule.Expectation, capsule.Receipt, transaction, ref remaining, insert: false);
            if (!CreatureAuthority(originals.EncodingLookup, source.Resolvers[0]) || !CreatureAuthority(originals.RootLookup, source.Resolvers[1])) throw GeometryStale();
            Content(originals.Receipt); Metadata(capsule.Association.EncodingSource); Metadata(capsule.Association.RootSource);
            foreach (var configuration in capsule.Association.Configurations) Add(configuration.Sha256, configuration.ByteLength);
            Add(originals.EncodingLookup.DecodedSha256, originals.EncodingLookup.DecodedBytes); Add(originals.RootLookup.DecodedSha256, originals.RootLookup.DecodedBytes);
        }
        if (artifact is not null) Add(artifact.Manifest.Sha256, artifact.Manifest.ByteLength);
        if (identities.Count > 32) throw GeometryLimit();
        foreach (var identity in identities)
        { token.ThrowIfCancellationRequested(); GeometryRequireNoInvalidity(identity.Sha, identity.Bytes, transaction); }
    }

    private void GeometryRequireNoInvalidity(string sha, long bytes, SqliteTransaction transaction)
    {
        using var command = Command("""
            SELECT reason,observed_at,revision,length(CAST(reason AS BLOB)),length(CAST(observed_at AS BLOB))
            FROM object_invalidity WHERE sha256=$h AND byte_length=$n
            """, transaction, ("$h", sha), ("$n", bytes));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return;
        if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.GetInt64(3) is < 1 or > 64 || reader.GetInt64(4) is < 1 or > 128) throw GeometryInvalid();
        var reason = reader.GetString(0); var at = reader.GetString(1); var revision = reader.GetInt64(2); reader.Close();
        ValidateObjectInvalidity(sha, bytes, reason);
        if (!DownloadUtc(at) || revision is < 1 || revision > CurrentObjectIntegrityEpoch(transaction)) throw GeometryInvalid();
        throw GeometryCorrupt();
    }

    private static void GeometrySourceSelection(GeometrySelection selection, GeometrySourceCapsulePair source)
    {
        var binding = selection.Core.Binding; var context = source.SourceContext;
        if (source.Model is null || source.Skin is null || source.Resolvers is null || source.Resolvers.Count != 2 || source.Resolvers.Any(r => r is null) ||
            source.Counts is null || context is null || context.Request is null || source.Model.FileDataId != selection.ModelFileDataId ||
            source.MainSkinOrdinal != 0 || source.MainSkinFileDataId == 0 || source.Skin.FileDataId != source.MainSkinFileDataId ||
            source.Model.Role != "model" || source.Skin.Role != "skin" || context.Opening != selection.Opening.Reference ||
            context.ResearchLocale != binding.Context.ResearchLocale || context.Request.ProductId != binding.Context.ProductId ||
            context.Request.BuildIdentity != binding.Context.BuildIdentity || context.Request.SourceRegion != binding.Context.SourceRegion ||
            context.BuildConfigurationKey != selection.Opening.Build.BuildConfig || context.CdnConfigurationKey != selection.Opening.Build.CdnConfig ||
            source.InterpretationPolicy != M2GeometrySkin.InterpretationPolicy || source.VariantPolicy != AssetResolutionSession.VariantPolicy ||
            source.EncodingSelectionPolicy != AssetResolutionSession.EncodingSelectionPolicy) throw GeometryStale();
        foreach (var node in new[] { source.Model, source.Skin })
        {
            GeometryAssociationShape(node.Association, node.Expectation, node.Receipt);
            if (node.Association.Request != context.Request || node.Association.ResearchLocale != context.ResearchLocale || node.Association.SourceReference != context.Opening ||
                node.Association.Memberships.Any(member => member.Entry.FileDataId != node.FileDataId) ||
                !ItemIconAdmissionPolicy.SameMetadata(node.Association.EncodingSource, binding.Source.Association.EncodingSource) ||
                !ItemIconAdmissionPolicy.SameMetadata(node.Association.RootSource, binding.Source.Association.RootSource)) throw GeometryStale();
            foreach (var original in binding.Configurations)
            {
                var actual = node.Association.Configurations.SingleOrDefault(c => c.Role == original.Role);
                if (actual is null || !CreatureAuthority(actual, original) || original.Role == "buildConfig" && original.Sha256 != context.BuildConfigurationSha256 ||
                    original.Role == "cdnConfig" && original.Sha256 != context.CdnConfigurationSha256) throw GeometryStale();
            }
        }
        if (source.Resolvers[0].Source.Role != "encoding" || source.Resolvers[0].ParserRevision != ResolverArtifacts.EncodingRevision ||
            source.Resolvers[1].Source.Role != "root" || source.Resolvers[1].ParserRevision != AssetResolutionSessions.RootArtifactRevision) throw GeometryInvalid();
    }

    private static GeometrySourceBinding GeometryBinding(GeometrySourceCapsulePair source)
    {
        GeometryOriginalSource Original(GeometrySourceCapsule node) => new(node.Receipt.ContentKey, node.Receipt.EncodingKey,
            node.Receipt.Sha256, DownloadNumber(node.Receipt.EncodedBytes), DownloadNumber(node.Receipt.DecodedBytes), node.Receipt.VerifierRevision);
        return new(Original(source.Model), Original(source.Skin), source.Model.FileDataId, source.Skin.FileDataId, source.MainSkinOrdinal);
    }

    private static void GeometryDescriptor(GeometryArtifactDescriptor artifact, GeometrySourceCapsulePair source, CancellationToken token)
    {
        if (artifact?.Binding is null || artifact.Manifest is not { } manifest || manifest.Bounds?.Min is null || manifest.Bounds.Max is null ||
            manifest.Bounds.Min.Count != 3 || manifest.Bounds.Max.Count != 3 || manifest.Evaluations is null ||
            manifest.Positions is null || manifest.LineIndices is null || manifest.Sections is null ||
            !DownloadHash(manifest.ArtifactId, 64) || !DownloadHash(manifest.BindingSha256, 64) || !DownloadHash(manifest.Sha256, 64) ||
            manifest.ByteLength is < 128 or > (2 << 20) || manifest.FormatVersion != 1 || manifest.HeaderBytes != 128 ||
            manifest.Scope != "unfiltered-declared-sections" || manifest.CoordinatePolicy != M2StaticGeometry.CoordinatePolicy ||
            manifest.ModelReaderRevision != M2ModelReader.Revision || manifest.SkinReaderRevision != M2GeometrySkinReader.Revision ||
            manifest.InterpretationPolicy != M2GeometrySkin.InterpretationPolicy || manifest.ArtifactRevision != ModelGeometryArtifacts.Revision ||
            manifest.EdgePolicy != ModelGeometryArtifacts.LinePolicy || manifest.MainSkinOrdinal != 0 || artifact.Binding != GeometryBinding(source) ||
            manifest.BindingSha256 != ModelGeometryArtifacts.ComputeBindingSha256(artifact.Binding, token) ||
            manifest.ModelFileDataId != source.Model.FileDataId || manifest.SkinFileDataId != source.Skin.FileDataId ||
            manifest.VertexCount != source.Counts.Vertices || manifest.VertexLookupCount != source.Counts.VertexLookup ||
            manifest.SourceTriangleIndexCount != source.Counts.GlobalTriangleIndices || manifest.SectionIndexCount != source.Counts.SectionTriangleIndices ||
            manifest.SectionCount != source.Counts.Sections || manifest.VertexCount is < 1 or > 65536 || manifest.VertexLookupCount is < 1 or > 65536 ||
            manifest.SourceTriangleIndexCount is < 3 or > 131070 || manifest.SourceTriangleIndexCount % 3 != 0 ||
            manifest.SectionIndexCount is < 3 or > 131070 || manifest.SectionIndexCount % 3 != 0 || manifest.LineIndexCount != manifest.SectionIndexCount * 2 ||
            manifest.SectionCount is < 1 or > 256 || manifest.Positions.Offset != 128 || manifest.Positions.ByteLength != manifest.VertexCount * 12 ||
            manifest.LineIndices.Offset != manifest.Positions.Offset + manifest.Positions.ByteLength || manifest.LineIndices.ByteLength != manifest.LineIndexCount * 4 ||
            manifest.Sections.Offset != manifest.LineIndices.Offset + manifest.LineIndices.ByteLength || manifest.Sections.ByteLength != manifest.SectionCount * 32 ||
            (long)manifest.Sections.Offset + manifest.Sections.ByteLength != manifest.ByteLength ||
            manifest.Evaluations.Material != "notEvaluated" || manifest.Evaluations.Pose != "notEvaluated" ||
            manifest.Evaluations.Visibility != "notEvaluated" || manifest.Evaluations.Appearance != "notEvaluated") throw GeometryInvalid();
        for (var i = 0; i < 3; i++)
        {
            var min = manifest.Bounds.Min[i]; var max = manifest.Bounds.Max[i];
            if (!float.IsFinite(min) || !float.IsFinite(max) || min > max ||
                min == 0 && BitConverter.SingleToInt32Bits(min) != 0 || max == 0 && BitConverter.SingleToInt32Bits(max) != 0) throw GeometryInvalid();
        }
        _ = GeometryBytes(artifact, GeometryDescriptorBytes);
    }
    private static GeometryArtifactDescriptor GeometryFreezeDescriptor(GeometryArtifactDescriptor artifact) => artifact with
    {
        Manifest = artifact.Manifest with { Bounds = new(Array.AsReadOnly(artifact.Manifest.Bounds.Min.ToArray()), Array.AsReadOnly(artifact.Manifest.Bounds.Max.ToArray())) }
    };
}
