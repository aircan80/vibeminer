using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>A retained appearance: Ready carries the binding needed to re-verify its VMA1 bytes; a retained refusal has neither.</summary>
internal sealed record GeometryAppearanceDescriptor(CreatureGeometryAppearance Appearance, AppearanceBinding? Binding);
internal sealed record GeometryAppearanceRecord(string Key, string GeometryArtifactId, GeometryAppearanceDescriptor Descriptor, string Original);

public sealed partial class DiscoveryCatalog
{
    private void UpgradeCreatureGeometryAppearance()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE geometry_appearances(appearance_key TEXT PRIMARY KEY,
                geometry_artifact_id TEXT NOT NULL,artifact_id TEXT,descriptor TEXT NOT NULL,
                FOREIGN KEY(geometry_artifact_id) REFERENCES geometry_artifacts(artifact_id));
            PRAGMA user_version=19;
            """, transaction);
        transaction.Commit();
    }

    internal async Task<GeometryAppearanceRecord?> FindCreatureGeometryAppearanceAsync(GeometryDisplayAuthorization selection,
        GeometryLocator geometry, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (geometry is null || !geometry.OwnedBy(geometryLocatorOwner)) throw GeometryInvalid();
        var key = await GeometryAppearanceKeyAsync(selection, geometry, budget, token);
        var raw = await Schedule(() =>
        {
            using var transaction = Db.BeginTransaction(); var row = GeometryReadAppearance(key, transaction);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return row;
        }, token);
        // A row for another geometry artifact is not this selection's appearance; it is never adopted.
        if (raw is null || raw.Value.Geometry != geometry.Descriptor.Manifest.ArtifactId) return null;
        GeometryAppearanceDescriptor descriptor;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested();
            descriptor = GeometryAppearanceValidate(GeometryDecode<GeometryAppearanceDescriptor>(Encoding.UTF8.GetBytes(raw.Value.Descriptor)), geometry, selection);
            if (raw.Value.Artifact != descriptor.Appearance.Manifest?.ArtifactId) throw GeometryInvalid();
        }
        return new(key, raw.Value.Geometry, descriptor, raw.Value.Descriptor);
    }

    /// <summary>One transaction: the selection's CDI/CMD admissions, the geometry locator, known invalidity of the VMA1 file and
    /// every texture source, then insert (or confirm, when <paramref name="insert"/> is false) the canonical-first row.</summary>
    internal async Task<GeometryAppearanceRecord> FinalizeCreatureGeometryAppearanceAsync(GeometryDisplayAuthorization selection,
        GeometryLocator geometry, GeometryAppearanceDescriptor descriptor, bool insert, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (geometry is null || !geometry.OwnedBy(geometryLocatorOwner)) throw GeometryInvalid();
        var frozen = await GeometryFreezeSelectionAsync(selection, budget, token);
        var key = await GeometryAppearanceKeyAsync(selection, geometry, budget, token);
        string text;
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested(); descriptor = GeometryAppearanceValidate(descriptor, geometry, selection);
            text = Encoding.UTF8.GetString(GeometryBytes(descriptor, GeometryDescriptorBytes));
        }
        var artifact = descriptor.Appearance.Manifest; var geometryArtifact = geometry.Descriptor.Manifest.ArtifactId;
        return await Schedule(() =>
        {
            using var transaction = Db.BeginTransaction(); GeometryRequireSelection(frozen, transaction, token);
            var locator = GeometryReadLocator(geometry.Key, transaction) ?? throw GeometryStale();
            if (locator.ArtifactId != geometryArtifact || locator.Authority != geometry.AuthoritySha256) throw GeometryStale();
            if (artifact is not null)
            {
                GeometryRequireNoInvalidity(artifact.Sha256, artifact.ByteLength, transaction);
                foreach (var texture in descriptor.Binding!.Textures) GeometryRequireNoInvalidity(texture.Source.EncodedSha256, texture.Source.EncodedBytes, transaction);
            }
            var existing = GeometryReadAppearance(key, transaction);
            GeometryAppearanceRecord result;
            if (existing is { } row)
            {
                if (row.Geometry != geometryArtifact || row.Artifact != artifact?.ArtifactId || !insert && row.Descriptor != text) throw GeometryStale();
                result = new(key, row.Geometry, row.Descriptor == text ? descriptor : GeometryAppearanceValidate(
                    GeometryDecode<GeometryAppearanceDescriptor>(Encoding.UTF8.GetBytes(row.Descriptor)), geometry, selection), row.Descriptor);
            }
            else
            {
                if (!insert) throw GeometryStale();
                Execute("INSERT INTO geometry_appearances VALUES($k,$g,$a,$d)", transaction,
                    ("$k", key), ("$g", geometryArtifact), ("$a", (object?)artifact?.ArtifactId ?? DBNull.Value), ("$d", text));
                result = new(key, geometryArtifact, descriptor, text);
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }, token);
    }

    private (string Geometry, string? Artifact, string Descriptor)? GeometryReadAppearance(string key, SqliteTransaction transaction)
    {
        using var command = Command("""
            SELECT geometry_artifact_id,artifact_id,descriptor,length(CAST(geometry_artifact_id AS BLOB)),
                length(CAST(artifact_id AS BLOB)),length(CAST(descriptor AS BLOB))
            FROM geometry_appearances WHERE appearance_key=$k
            """, transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var remaining = GeometryDescriptorBytes + 256; GeometrySizes(reader, [3, 5], ref remaining);
        if (reader.GetInt64(3) != 64 || !reader.IsDBNull(1) && reader.GetInt64(4) != 64) throw GeometryInvalid();
        var artifact = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!DownloadHash(reader.GetString(0), 64) || artifact is not null && !DownloadHash(artifact, 64)) throw GeometryInvalid();
        return (reader.GetString(0), artifact, reader.GetString(2));
    }

    // Keyed by display: the geometry locator, the exact CreatureDisplayInfo rows artifact, row and texture fields, every revision
    // and the D095 texture variant policy, so a later variant rule never reuses an appearance chosen under this one.
    private static async Task<string> GeometryAppearanceKeyAsync(GeometryDisplayAuthorization selection, GeometryLocator geometry,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        using (await budget.EnterAsync(token))
        {
            token.ThrowIfCancellationRequested();
            if (selection?.Display?.Binding?.Admission?.Artifact is null || selection.Selected is null) throw GeometryInvalid();
            var bytes = GeometryBytes(new
            {
                revision = "geometry-appearance-key-v1", geometry = geometry.Key, geometryArtifact = geometry.Descriptor.Manifest.ArtifactId,
                display = selection.Display.Binding.Admission.Artifact.ContentKey, row = selection.Selected.DisplayId, textures = selection.Display.Textures,
                policy = GeometryAppearanceSources.Policy, material = M2Appearance.MaterialPolicy, reader = M2Appearance.ReaderRevision,
                texture = ModelAppearanceArtifacts.TexturePolicy, decoder = Blp2Decoder.Revision, artifact = ModelAppearanceArtifacts.Revision,
                variant = RootTextureVariants.Policy
            }, GeometrySourceBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData("vibeminer-geometry-appearance-key-v1\0"u8); hash.AppendData(bytes);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private static GeometryAppearanceDescriptor GeometryAppearanceValidate(GeometryAppearanceDescriptor descriptor, GeometryLocator geometry,
        GeometryDisplayAuthorization selection)
    {
        if (descriptor?.Appearance is null) throw GeometryInvalid();
        CreatureGeometryAppearance appearance;
        try { appearance = CreatureGeometryValidation.Snapshot(descriptor.Appearance); }
        catch (DiscoveryException) { throw GeometryInvalid(); }
        var display = GeometryAppearanceSources.Display(selection);
        if (appearance.State == CreatureGeometryAppearanceState.NotPrepared || !(appearance.Display is null && display is null || SameDisplay(appearance.Display, display))) throw GeometryInvalid();
        if (appearance.Manifest is { } manifest)
        {
            var binding = descriptor.Binding ?? throw GeometryInvalid();
            if (manifest.GeometryBindingSha256 != geometry.Descriptor.Manifest.BindingSha256 || binding.GeometryBindingSha256 != manifest.GeometryBindingSha256 ||
                manifest.DisplayId.ToString(CultureInfo.InvariantCulture) != selection.Selected.DisplayId || binding.DisplayId != manifest.DisplayId ||
                ModelAppearanceArtifacts.ComputeBindingSha256(binding) != manifest.BindingSha256 || binding.Textures.Count != manifest.TextureCount) throw GeometryInvalid();
        }
        else if (descriptor.Binding is not null) throw GeometryInvalid();
        return descriptor with { Appearance = appearance };
    }
    private static bool SameDisplay(CreatureGeometryAppearanceDisplay? a, CreatureGeometryAppearanceDisplay? b) =>
        a is not null && b is not null && a.DisplayId == b.DisplayId && a.ExtendedDisplayInfoId == b.ExtendedDisplayInfoId && a.TextureVariations.SequenceEqual(b.TextureVariations);
}
