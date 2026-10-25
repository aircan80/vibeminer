using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private readonly object geometryLocatorOwner = new();
    private sealed record GeometrySelection(CapturedOpeningContext Opening, CreaturesArtifact Core,
        CreatureDisplaySlot Selected, string CreatureId, CreatureDisplayCatalogBinding Display,
        CreatureDisplayCatalogBinding Model, uint ModelFileDataId);
    private sealed record GeometryRawLocator(string ArtifactId, string Authority, string Descriptor, string Capsule, string BindingSha256);

    internal async Task<GeometryLocator?> FindCreatureGeometryLocatorAsync(GeometryDisplayAuthorization selection,
        string? expectedArtifactId, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (expectedArtifactId is not null && !DownloadHash(expectedArtifactId, 64)) throw GeometryInvalid();
        var frozen = await GeometryFreezeSelectionAsync(selection, budget, token);
        var key = await GeometryLocatorKeyAsync(frozen, budget, token);
        var raw = await Schedule(() =>
        {
            using var transaction = Db.BeginTransaction(); GeometryRequireSelection(frozen, transaction, token);
            var result = GeometryReadLocator(key, transaction);
            if (expectedArtifactId is not null && (result is null || result.ArtifactId != expectedArtifactId)) throw GeometryStale();
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }, token);
        if (raw is null) return null;
        var source = await GeometrySourceCapsuleCodec.ParseAsync(Encoding.UTF8.GetBytes(raw.Capsule), budget, token);
        GeometryArtifactDescriptor descriptor;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); descriptor = GeometryDecode<GeometryArtifactDescriptor>(Encoding.UTF8.GetBytes(raw.Descriptor));
            GeometryDescriptor(descriptor, source.Capsule, token);
            descriptor = GeometryFreezeDescriptor(descriptor);
            if (source.AuthoritySha256 != raw.Authority || descriptor.Manifest.ArtifactId != raw.ArtifactId || descriptor.Manifest.BindingSha256 != raw.BindingSha256) throw GeometryInvalid();
            GeometrySourceSelection(frozen, source.Capsule);
        }
        var sources = await GeometrySourceCapsuleCodec.ProjectSourcesAsync(source.Capsule, budget, token);
        token.ThrowIfCancellationRequested();
        return GeometryLocator.FromCatalog(geometryLocatorOwner, key, descriptor, source, raw.Descriptor, raw.Capsule, sources);
    }

    internal async Task FinalizeCreatureGeometryReuseAsync(GeometryDisplayAuthorization selection, GeometryLocator retained,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (retained is null || !retained.OwnedBy(geometryLocatorOwner)) throw GeometryInvalid();
        var frozen = await GeometryFreezeSelectionAsync(selection, budget, token);
        var key = await GeometryLocatorKeyAsync(frozen, budget, token);
        var source = await GeometrySourceCapsuleCodec.ParseAsync(retained.CanonicalUtf8, budget, token);
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); GeometrySourceSelection(frozen, source.Capsule);
            GeometryDescriptor(retained.Descriptor, source.Capsule, token);
            if (key != retained.Key || source.AuthoritySha256 != retained.AuthoritySha256) throw GeometryStale();
        }
        await Schedule(() =>
        {
            using var transaction = Db.BeginTransaction();
            GeometryRequireAll(frozen, source.Capsule, retained.Descriptor, transaction, token);
            var current = GeometryReadLocator(key, transaction) ?? throw GeometryStale();
            if (current.ArtifactId != retained.Descriptor.Manifest.ArtifactId || current.BindingSha256 != retained.Descriptor.Manifest.BindingSha256 || current.Authority != source.AuthoritySha256 ||
                current.Descriptor != retained.OriginalDescriptor || current.Capsule != retained.OriginalCapsule) throw GeometryStale();
            token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
        }, token);
    }

    internal async Task<GeometryLocator?> FinalizeCreatureGeometryAsync(GeometryDisplayAuthorization selection, GeometrySourceClosure closure,
        GeometryArtifactDescriptor? artifact, GeometryFinalAction action, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (closure is null || action is not (GeometryFinalAction.Publish or GeometryFinalAction.ValidateNoDeclaredTriangles) ||
            (action == GeometryFinalAction.Publish) != (artifact is not null)) throw GeometryInvalid();
        var frozen = await GeometryFreezeSelectionAsync(selection, budget, token);
        var key = await GeometryLocatorKeyAsync(frozen, budget, token);
        var source = await GeometrySourceCapsuleCodec.ParseAsync(closure.CanonicalUtf8, budget, token);
        GeometryArtifactDescriptor? descriptor = null; string? descriptorText = null;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); GeometrySourceSelection(frozen, source.Capsule);
            if (source.AuthoritySha256 != closure.AuthoritySha256 || !CreatureAuthority(source.Capsule, closure.Capsule) ||
                ModelGeometryArtifacts.ComputeBindingSha256(closure.DataBinding, token) != closure.DataBindingSha256 ||
                closure.DataBinding != GeometryBinding(source.Capsule)) throw GeometryInvalid();
            if (artifact is not null)
            {
                var bytes = GeometryBytes(artifact, GeometryDescriptorBytes); descriptorText = Encoding.UTF8.GetString(bytes);
                descriptor = GeometryDecode<GeometryArtifactDescriptor>(bytes); GeometryDescriptor(descriptor, source.Capsule, token);
                descriptor = GeometryFreezeDescriptor(descriptor);
                if (descriptor.Manifest.BindingSha256 != closure.DataBindingSha256) throw GeometryInvalid();
            }
            else if (source.Capsule.Counts.SectionTriangleIndices != 0) throw GeometryInvalid();
        }
        var capsuleText = Encoding.UTF8.GetString(source.CanonicalUtf8.Span);
        // Include the three materialized scalar identities that the bounded SQL reader charges.
        if (Encoding.UTF8.GetByteCount(capsuleText) + (descriptorText is null ? 0 : Encoding.UTF8.GetByteCount(descriptorText)) + 192 > (320 << 10)) throw GeometryLimit();
        GeometryRawLocator? previous = null; var canonicalSource = source; var canonicalCapsule = capsuleText;
        if (action == GeometryFinalAction.Publish)
        {
            previous = await Schedule(() => { using var tx = Db.BeginTransaction(); var raw = GeometryReadLocator(key, tx); token.ThrowIfCancellationRequested(); tx.Commit(); return raw; }, token);
            if (previous is not null)
            {
                var first = await GeometrySourceCapsuleCodec.ParseAsync(Encoding.UTF8.GetBytes(previous.Capsule), budget, token);
                using (await budget.EnterAsync(token))
                {
                    token.ThrowIfCancellationRequested(); GeometrySourceSelection(frozen, first.Capsule);
                    // Existing verification timestamps may advance on a cold B opening. All other
                    // original facts and ordering remain strict; retain the first complete capsule.
                    if (first.AuthoritySha256 != previous.Authority || !CreatureAuthority(first.Capsule, source.Capsule) ||
                        previous.ArtifactId != descriptor!.Manifest.ArtifactId || previous.BindingSha256 != descriptor.Manifest.BindingSha256 ||
                        previous.Descriptor != descriptorText) throw GeometryStale();
                }
                canonicalSource = first; canonicalCapsule = previous.Capsule;
            }
        }
        var publicSources = action == GeometryFinalAction.Publish ? await GeometrySourceCapsuleCodec.ProjectSourcesAsync(canonicalSource.Capsule, budget, token) : null;
        return await Schedule(() =>
        {
            using var transaction = Db.BeginTransaction(); GeometryRequireAll(frozen, source.Capsule, descriptor, transaction, token);
            if (action == GeometryFinalAction.Publish)
            {
                var manifest = descriptor!.Manifest;
                using (var command = Command("""
                    SELECT artifact_id,descriptor,length(CAST(artifact_id AS BLOB)),length(CAST(descriptor AS BLOB))
                    FROM geometry_artifacts WHERE data_binding_sha256=$b
                    """, transaction, ("$b", manifest.BindingSha256)))
                using (var reader = command.ExecuteReader()) if (reader.Read())
                {
                    var remaining = GeometryDescriptorBytes + 64; GeometrySizes(reader, [2, 3], ref remaining);
                    if (reader.GetInt64(2) != 64 || reader.GetString(0) != manifest.ArtifactId || reader.GetString(1) != descriptorText) throw GeometryInvalid();
                }
                using (var command = Command("SELECT data_binding_sha256,descriptor,length(CAST(data_binding_sha256 AS BLOB)),length(CAST(descriptor AS BLOB)) FROM geometry_artifacts WHERE artifact_id=$id", transaction, ("$id", manifest.ArtifactId)))
                using (var reader = command.ExecuteReader()) if (reader.Read())
                {
                    var remaining = GeometryDescriptorBytes + 64; GeometrySizes(reader, [2, 3], ref remaining);
                    if (reader.GetInt64(2) != 64 || reader.GetString(0) != manifest.BindingSha256 || reader.GetString(1) != descriptorText) throw GeometryInvalid();
                }
                var existing = GeometryReadLocator(key, transaction);
                if (existing != previous) throw GeometryStale();
                Execute("INSERT OR IGNORE INTO geometry_artifacts VALUES($i,$b,$d)", transaction,
                    ("$i", manifest.ArtifactId), ("$b", manifest.BindingSha256), ("$d", descriptorText));
                Execute("INSERT OR IGNORE INTO geometry_source_locators VALUES($k,$i,$a,$c)", transaction,
                    ("$k", key), ("$i", manifest.ArtifactId), ("$a", canonicalSource.AuthoritySha256), ("$c", canonicalCapsule));
            }
            token.ThrowIfCancellationRequested(); transaction.Commit();
            return descriptor is null ? null : GeometryLocator.FromCatalog(geometryLocatorOwner, key, descriptor, canonicalSource, descriptorText!, canonicalCapsule, publicSources!);
        }, token);
    }

    private GeometryRawLocator? GeometryReadLocator(string key, SqliteTransaction transaction)
    {
        using var command = Command("""
            SELECT l.artifact_id,l.authority_sha256,a.descriptor,l.capsule,a.data_binding_sha256,
                length(CAST(l.artifact_id AS BLOB)),length(CAST(l.authority_sha256 AS BLOB)),
                length(CAST(a.descriptor AS BLOB)),length(CAST(l.capsule AS BLOB)),length(CAST(a.data_binding_sha256 AS BLOB))
            FROM geometry_source_locators l LEFT JOIN geometry_artifacts a ON a.artifact_id=l.artifact_id WHERE l.locator_key=$k
            """, transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var remaining = (320 << 10); GeometrySizes(reader, [5, 6, 7, 8, 9], ref remaining);
        if (reader.GetInt64(5) != 64 || reader.GetInt64(6) != 64 || reader.GetInt64(9) != 64 ||
            reader.GetInt64(7) > GeometryDescriptorBytes || reader.GetInt64(8) > GeometryPairBytes) throw GeometryInvalid();
        var result = new GeometryRawLocator(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        if (!DownloadHash(result.ArtifactId, 64) || !DownloadHash(result.Authority, 64) || !DownloadHash(reader.GetString(4), 64)) throw GeometryInvalid();
        return result;
    }

    private static async Task<GeometrySelection> GeometryFreezeSelectionAsync(GeometryDisplayAuthorization selection,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (selection?.Core?.Opening?.Build is null || selection.Core.Opening.Reference is null || selection.Core.Artifact is null || selection.Core.Snapshot is null || selection.Core.Summary is null ||
            selection.Selected is null || selection.Display?.Binding is null || selection.Model?.Binding is null || selection.ModelFileDataId == 0) throw GeometryInvalid();
        CreaturesArtifact core; CreaturesSnapshot snapshot;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); var coreBytes = CreaturesPolicy.Bytes(selection.Core.Artifact); GeometryPreflight<CreaturesArtifact>(coreBytes);
            core = CreatureJson<CreaturesArtifact>(Encoding.UTF8.GetString(coreBytes)); CreaturesPolicy.Validate(core);
        }
        await Task.Yield();
        using (await budget.EnterAsync(token))
        { token.ThrowIfCancellationRequested(); snapshot = GeometryDecode<CreaturesSnapshot>(GeometryBytes(selection.Core.Snapshot, GeometrySourceBytes)); }
        await Task.Yield();
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested();
            var summary = GeometryDecode<CreatureSummary>(GeometryBytes(selection.Core.Summary, 8 << 10));
            if (snapshot.Fields?.DisplayIds is null || snapshot.Fields.Probabilities is null || snapshot.Fields.Facts is null ||
                snapshot.CreatureSource?.Columns is null || snapshot.Counts is null || summary.DisplaySlots is null || summary.DisplaySlots.Any(s => s is null) || summary.Facts is null || summary.Name is null ||
                snapshot.FeatureRevision != CreaturesPolicy.FeatureRevision || snapshot.QueryPolicy != CreaturesPolicy.QueryPolicy || snapshot.SemanticProfile != CreaturesPolicy.SemanticProfile ||
                !DownloadUtc(snapshot.PreparedAt) || !CreatureAuthority(snapshot.CreatureSource, core.Binding.Source) || snapshot.Counts != core.Counts ||
                !CreatureAuthority(snapshot.Fields, CreaturesPolicy.FieldMap(core.Binding))) throw GeometryInvalid();
            var copied = selection.Core with { Artifact = core, Snapshot = snapshot, Summary = summary };
            var request = new CreatureDisplayMetadataRequest(core.Binding.Context, core.ContentKey, selection.Core.Summary.Id, selection.Selected.Slot);
            if (CreatureDisplayPolicy.Select(copied, request) != selection.Selected || selection.Selected.State != CreatureDisplayState.Declared ||
                snapshot.ArtifactId != core.ContentKey || snapshot.Context != core.Binding.Context) throw GeometryInvalid();
            if (selection.Model.Edge != selection.ModelFileDataId.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw GeometryInvalid();
        }
        await Task.Yield(); CreatureDisplayCatalogBinding display, model;
        using (await budget.EnterAsync(token)) { token.ThrowIfCancellationRequested(); display = selection.Display.Binding with { Admission = DisplayCopy(selection.Display.Binding.Admission) }; }
        await Task.Yield();
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); model = selection.Model.Binding with { Admission = DisplayCopy(selection.Model.Binding.Admission) };
            GeometryProbe(selection.Display with { Binding = display }, CreatureDisplayTargetRole.Display, selection.Selected.DisplayId, core.Binding.Context);
            GeometryProbe(selection.Model with { Binding = model }, CreatureDisplayTargetRole.Model, selection.Display.Edge!, core.Binding.Context);
        }
        token.ThrowIfCancellationRequested();
        return new(selection.Core.Opening with { Build = selection.Core.Opening.Build with { }, Reference = selection.Core.Opening.Reference with { } },
            core, selection.Selected with { }, selection.Core.Summary.Id, display, model, selection.ModelFileDataId);
    }

    private static void GeometryProbe(CreatureDisplayProbe probe, CreatureDisplayTargetRole role, string expectedRow, CreaturesContext context)
    {
        var row = probe.Target?.Row; var admission = probe.Binding?.Admission; var rows = admission?.Artifact;
        if (probe.Failure is not null || probe.Problem is not null || probe.Target?.Role != role || probe.Target.MatchCount != "1" || row?.Source is null || row.EdgeValue is null ||
            probe.Binding?.TableId != CreatureDisplayPolicy.Table(role) || rows?.Binding is null || rows.Header?.Columns is null || rows.Header.Columns.Any(c => c is null) ||
            probe.Target.Snapshot is not { } snapshot || snapshot.ArtifactId != rows.ContentKey || snapshot.PreparedAt != admission!.PreparedAt ||
            snapshot.ProductId != context.ProductId || snapshot.BuildIdentity != context.BuildIdentity || snapshot.SourceRegion != context.SourceRegion ||
            snapshot.ResearchLocale != context.ResearchLocale || snapshot.TableId != probe.Binding.TableId ||
            row.Source.TableId != probe.Binding.TableId || row.Source.ArtifactId != rows.ContentKey || row.Source.RowId != expectedRow ||
            row.EdgeValue.TableId != probe.Binding.TableId || row.EdgeValue.Value != probe.Edge || row.Source.ColumnIndex != row.EdgeValue.ColumnIndex ||
            row.Source.ColumnIndex is not { } column || column < 0 || column >= rows.Header.Columns.Count ||
            rows.Header.Columns[column].Name != CreatureDisplayPolicy.Field(role) || rows.Header.Columns[column].Kind != "integer" ||
            rows.Header.Columns[column].IsId || rows.Header.Columns[column].Interpretation is not null || row.IsCopy != (row.CopySourceId is not null) ||
            row.CopySourceId is { } copy && (!uint.TryParse(copy, out var copyId) || copyId.ToString(System.Globalization.CultureInfo.InvariantCulture) != copy) ||
            !CreatureDisplayPolicy.IsLayout(role, rows.Header.LayoutHash) || rows.Binding.DefinitionSha256 != CreatureDisplayPolicy.Definition(role) ||
            rows.Binding.ProviderRevision != BundledTableDefinitions.Revision || rows.Binding.DecoderRevision != (admission!.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
            !uint.TryParse(probe.Edge, out var value) || value == 0 || value.ToString(System.Globalization.CultureInfo.InvariantCulture) != probe.Edge ||
            role == CreatureDisplayTargetRole.Display && value > ushort.MaxValue) throw GeometryInvalid();
    }

    private static async Task<string> GeometryLocatorKeyAsync(GeometrySelection selection, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        byte[] bytes;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); var binding = selection.Core.Binding;
            MetadataReceipt Metadata(MetadataReceipt original) => original with { VerifiedAt = "", Location = MetadataSourceLocation(original) };
            bytes = GeometryBytes(new { revision = "geometry-source-locator-v1", binding.Context, binding.SourceReference,
                configurations = binding.Configurations.OrderBy(c => c.Role, StringComparer.Ordinal).Select(c => c with { VerifiedAt = "" }).ToArray(),
                encoding = Metadata(binding.Source.Association.EncodingSource), root = Metadata(binding.Source.Association.RootSource),
                selection.ModelFileDataId, source = MetadataAcquirer.SourceRevision, configuration = ConfigurationWorkspacePreparer.VerifierRevision,
                variant = AssetResolutionSession.VariantPolicy, encodingLookup = ResolverArtifacts.EncodingRevision,
                rootLookup = AssetResolutionSessions.RootArtifactRevision, encodingSelection = AssetResolutionSession.EncodingSelectionPolicy,
                model = M2ModelReader.Revision, skin = M2GeometrySkinReader.Revision, interpretation = M2GeometrySkin.InterpretationPolicy,
                coordinate = M2StaticGeometry.CoordinatePolicy, artifact = ModelGeometryArtifacts.Revision, edge = ModelGeometryArtifacts.LinePolicy }, GeometrySourceBytes);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (await budget.EnterAsync(token)) { token.ThrowIfCancellationRequested(); hash.AppendData("vibeminer-geometry-source-locator-v1\0"u8); }
        for (var at = 0; at < bytes.Length; at += 65536)
        {
            await Task.Yield(); using (await budget.EnterAsync(token)) { token.ThrowIfCancellationRequested(); hash.AppendData(bytes.AsSpan(at, Math.Min(65536, bytes.Length - at))); }
        }
        using (await budget.EnterAsync(token)) { token.ThrowIfCancellationRequested(); return Convert.ToHexStringLower(hash.GetHashAndReset()); }
    }
}
