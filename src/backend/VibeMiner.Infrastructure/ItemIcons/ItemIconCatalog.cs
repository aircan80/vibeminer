using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    internal Task<IconPngArtifact?> FindIconPngAsync(string requestKey, CancellationToken token) => Schedule<IconPngArtifact?>(() =>
    {
        using var command = Command("SELECT descriptor,length(CAST(descriptor AS BLOB)) FROM icon_artifacts WHERE request_key=$k", null, ("$k", requestKey));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(1) > ItemIconAdmissionPolicy.MaxDescriptorBytes) throw ItemIconAdmissionPolicy.Invalid();
        var artifact = ReadIconDescriptor<IconPngArtifact>(reader.GetString(0));
        if (artifact is null || artifact.RequestKey != requestKey) throw ItemIconAdmissionPolicy.Invalid();
        return artifact;
    }, token);

    internal Task<StoredItemIcon?> FindItemIconAsync(CapturedOpeningContext opening, ItemsSnapshot snapshot,
        uint fileDataId, CancellationToken token) => Schedule<StoredItemIcon?>(() =>
    {
        RequireIconFeature(opening, snapshot, null);
        using var command = Command("""
            SELECT a.descriptor,length(CAST(a.descriptor AS BLOB)) FROM item_icon_admissions a
            WHERE a.feature_artifact=$f AND a.fdid=$id
            """, null, ("$f", snapshot.ArtifactId), ("$id", (long)fileDataId));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(1) > ItemIconAdmissionPolicy.MaxDescriptorBytes) throw ItemIconAdmissionPolicy.Invalid();
        var admission = ReadIconDescriptor<ItemIconAdmission>(reader.GetString(0)); reader.Close();
        if (admission is null) throw ItemIconAdmissionPolicy.Invalid();
        using var artifactCommand = Command("SELECT descriptor,length(CAST(descriptor AS BLOB)) FROM icon_artifacts WHERE request_key=$k", null,
            ("$k", admission.PngRequestKey));
        using var artifactReader = artifactCommand.ExecuteReader();
        if (!artifactReader.Read() || artifactReader.GetInt64(1) > ItemIconAdmissionPolicy.MaxDescriptorBytes) throw ItemIconAdmissionPolicy.Invalid();
        var saved = new StoredItemIcon(admission, ReadIconDescriptor<IconPngArtifact>(artifactReader.GetString(0)));
        ItemIconAdmissionPolicy.Validate(saved, snapshot, opening, fileDataId);
        ValidateIconConfigurations(saved.Admission.Association, null);
        ValidateIconMetadata(saved.Admission.Association, null);
        ValidateIconContent(saved.Admission.Source, null); return saved;
    }, token);

    internal Task CommitItemIconAsync(CapturedOpeningContext opening, ItemsSnapshot snapshot,
        StoredItemIcon saved, CancellationToken token) => Schedule(() =>
    {
        ItemIconAdmissionPolicy.Validate(saved, snapshot, opening, saved.Admission.FileDataId);
        var descriptor = Serialize(saved.Admission); var png = Serialize(saved.Artifact);
        if (Encoding.UTF8.GetByteCount(descriptor) > ItemIconAdmissionPolicy.MaxDescriptorBytes ||
            Encoding.UTF8.GetByteCount(png) > ItemIconAdmissionPolicy.MaxDescriptorBytes)
            throw new DiscoveryException("ITEM_ICON_ASSOCIATION_LIMIT", "The complete icon source association exceeds its bounded descriptor allowance.");
        using var transaction = Db.BeginTransaction(); RequireIconFeature(opening, snapshot, transaction);
        ValidateIconConfigurations(saved.Admission.Association, transaction);
        ValidateIconMetadata(saved.Admission.Association, transaction);
        ValidateIconContent(saved.Admission.Source, transaction);
        var previous = Scalar("SELECT descriptor FROM icon_artifacts WHERE request_key=$k", transaction,
            ("$k", saved.Artifact.RequestKey)) as string;
        if (previous is not null && previous != png) throw ItemIconAdmissionPolicy.Invalid();
        Execute("INSERT OR IGNORE INTO icon_artifacts VALUES($k,$d)", transaction, ("$k", saved.Artifact.RequestKey), ("$d", png));
        var original = Scalar("SELECT descriptor FROM item_icon_admissions WHERE feature_artifact=$f AND fdid=$id", transaction,
            ("$f", snapshot.ArtifactId), ("$id", (long)saved.Admission.FileDataId)) as string;
        if (original is not null)
        {
            var retained = ReadIconDescriptor<ItemIconAdmission>(original);
            ItemIconAdmissionPolicy.Validate(new(retained, saved.Artifact), snapshot, opening, saved.Admission.FileDataId);
            ValidateIconConfigurations(retained.Association, transaction);
            ValidateIconMetadata(retained.Association, transaction);
            ValidateIconContent(retained.Source, transaction);
        }
        Execute("INSERT OR IGNORE INTO item_icon_admissions VALUES($f,$id,$d)", transaction,
            ("$f", snapshot.ArtifactId), ("$id", (long)saved.Admission.FileDataId), ("$d", descriptor));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private static T ReadIconDescriptor<T>(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, Json);
            if (value is null) throw ItemIconAdmissionPolicy.Invalid();
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        { throw ItemIconAdmissionPolicy.Invalid(); }
    }
    private void ValidateIconContent(ContentReceipt source, SqliteTransaction? transaction)
    {
        var original = Scalar("SELECT receipt FROM content_objects WHERE ekey=$k", transaction, ("$k", source.EncodingKey)) as string;
        if (original is null || source with { VerifiedAt = "" } != ReadIconDescriptor<ContentReceipt>(original) with { VerifiedAt = "" })
            throw ItemIconAdmissionPolicy.Invalid();
    }

    private void ValidateIconMetadata(AssetSourceAssociation source, SqliteTransaction? transaction)
    {
        try
        {
            // Reuse the catalog's original-receipt and exact official loose/archive URL policy.
            ValidateDownloadSourceAssociation(source.EncodingSource, transaction);
            ValidateDownloadSourceAssociation(source.RootSource, transaction);
        }
        catch (DiscoveryException) { throw ItemIconAdmissionPolicy.Invalid(); }
    }

    private void ValidateIconConfigurations(AssetSourceAssociation source, SqliteTransaction? transaction)
    {
        foreach (var receipt in source.Configurations)
        {
            using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision FROM configuration_objects WHERE key=$k", transaction, ("$k", receipt.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision) throw ItemIconAdmissionPolicy.Invalid();
            ItemIconAdmissionPolicy.ValidateConfiguration(receipt, new(receipt.Key, reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }
    }

    private void RequireIconFeature(CapturedOpeningContext opening, ItemsSnapshot snapshot, SqliteTransaction? transaction)
    {
        RequireTableWorkspace(opening, transaction);
        var saved = Scalar("""
            SELECT f.descriptor FROM items_admissions a JOIN items_artifacts f ON f.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.locale=$l AND a.content_key=$f
            """, transaction, ("$b", opening.Build.Id), ("$r", opening.Reference.Region), ("$ref", Serialize(opening.Reference)),
            ("$l", snapshot.Context.ResearchLocale), ("$f", snapshot.ArtifactId)) as string;
        if (saved is null) throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The exact Items feature changed before icon admission.");
        var artifact = Deserialize<ItemsProjectionArtifact>(saved);
        RequireItemsContext(opening, snapshot.Context.ResearchLocale, artifact);
        if (!ItemsSourcesCurrent(opening, snapshot.Context.ResearchLocale, artifact, transaction))
            throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The Items prerequisites changed before icon admission.");
    }
}
