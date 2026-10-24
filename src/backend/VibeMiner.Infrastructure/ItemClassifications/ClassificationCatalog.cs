using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    internal Task<IReadOnlyList<ConfigurationReceipt>> ReadItemClassificationConfigurationsAsync(CapturedOpeningContext context,
        CancellationToken token) => Schedule(() => ClassificationConfigurations(context, null), token);

    private IReadOnlyList<ConfigurationReceipt> ClassificationConfigurations(CapturedOpeningContext context, SqliteTransaction? transaction)
    {
        RequireTableWorkspace(context, transaction);
        var id = Scalar("""
            SELECT id FROM workspace_admissions WHERE build_id=$b AND product=$p AND region=$r AND source_reference=$ref
            ORDER BY id DESC LIMIT 1
            """, transaction, ("$b", context.Build.Id), ("$p", context.Build.ProductId), ("$r", context.Reference.Region),
            ("$ref", Serialize(context.Reference)));
        if (id is null) throw ClassificationPolicy.Stale();
        var receipts = new List<ConfigurationReceipt>(2);
        using (var command = Command("""
            SELECT role,key,sha256,byte_length,source_url,retrieved_at,verified_at,parsed_field_count
            FROM workspace_configurations WHERE admission_id=$id ORDER BY role LIMIT 3
            """, transaction, ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (receipts.Count == 2) throw ClassificationPolicy.Invalid();
                receipts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7)));
            }
        }
        if (receipts.Count != 2 || receipts.Count(c => c.Role == "buildConfig" && c.Key == context.Build.BuildConfig) != 1 ||
            receipts.Count(c => c.Role == "cdnConfig" && c.Key == context.Build.CdnConfig) != 1) throw ClassificationPolicy.Invalid();
        foreach (var receipt in receipts)
        {
            using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision FROM configuration_objects WHERE key=$k", transaction, ("$k", receipt.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || receipt.Sha256 != reader.GetString(0) || receipt.ByteLength != reader.GetInt32(1) ||
                receipt.SourceUrl != reader.GetString(2) || receipt.RetrievedAt != reader.GetString(3) ||
                reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision || receipt.ParsedFieldCount is < 0 or > 4096 ||
                !DownloadUtc(receipt.VerifiedAt)) throw ClassificationPolicy.Invalid();
        }
        return receipts.AsReadOnly();
    }
    internal Task<ClassificationIndexArtifact?> FindItemClassificationArtifactAsync(string key, CancellationToken token) => Schedule<ClassificationIndexArtifact?>(() =>
    {
        var artifact = ClassificationDescriptor(key, null);
        if (artifact is not null && artifact.ContentKey != key) throw ClassificationPolicy.Invalid();
        return artifact;
    }, token);
    private ClassificationIndexArtifact? ClassificationDescriptor(string key, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT descriptor,length(CAST(descriptor AS BLOB)) FROM item_classification_artifacts WHERE content_key=$k", transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(1) is < 1 or > ClassificationPolicy.MaxLiveDescriptorBytes) throw ClassificationPolicy.Invalid();
        ClassificationIndexArtifact artifact;
        try { artifact = JsonSerializer.Deserialize<ClassificationIndexArtifact>(reader.GetString(0), ClassificationPolicy.Json) ?? throw ClassificationPolicy.Invalid(); }
        catch (JsonException) { throw ClassificationPolicy.Invalid(); }
        ClassificationPolicy.Validate(artifact); return artifact;
    }
    internal Task RecordItemClassificationArtifactAsync(ClassificationIndexArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ClassificationPolicy.Validate(artifact);
        using var transaction = Db.BeginTransaction();
        var previous = ClassificationDescriptor(artifact.ContentKey, transaction);
        if (previous is not null && (previous.Sha256 != artifact.Sha256 || previous.ByteLength != artifact.ByteLength)) throw ClassificationPolicy.Invalid();
        var descriptor = JsonSerializer.Serialize(artifact, ClassificationPolicy.Json);
        if (Encoding.UTF8.GetByteCount(descriptor) > ClassificationPolicy.MaxLiveDescriptorBytes) throw ClassificationPolicy.Invalid();
        Execute("INSERT OR IGNORE INTO item_classification_artifacts VALUES($k,$d)", transaction, ("$k", artifact.ContentKey), ("$d", descriptor));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    internal Task<StoredClassificationAdmission?> ReadItemClassificationAdmissionAsync(CapturedOpeningContext context,
        string locale, ItemClassificationKind kind, CancellationToken token) => Schedule<StoredClassificationAdmission?>(() =>
    {
        RequireTableWorkspace(context, null);
        using var command = Command("""
            SELECT content_key,prepared_at FROM item_classification_admissions
            WHERE build_id=$b AND region=$r AND source_reference=$ref AND locale=$l AND kind=$kind
            """, null, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
            ("$l", locale), ("$kind", kind.ToString()));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var key = reader.GetString(0); var at = reader.GetString(1); reader.Close();
        var artifact = ClassificationDescriptor(key, null) ?? throw ClassificationPolicy.Invalid();
        if (!DownloadUtc(at) || artifact.Binding.Kind != kind || artifact.Binding.Context.ResearchLocale != locale) throw ClassificationPolicy.Invalid();
        RequireClassificationTarget(context, artifact, null); return new(artifact, at);
    }, token);
    internal Task CommitItemClassificationAdmissionAsync(CapturedOpeningContext context, ItemsSnapshot currentItems,
        ClassificationIndexArtifact artifact, string preparedAt, CancellationToken token) => Schedule(() =>
    {
        ClassificationPolicy.Validate(artifact);
        if (!DownloadUtc(preparedAt)) throw ClassificationPolicy.Invalid();
        using var transaction = Db.BeginTransaction();
        RequireIconFeature(context, currentItems, transaction);
        RequireClassificationItemSources(context, currentItems, transaction);
        RequireClassificationTarget(context, artifact, transaction);
        var registered = ClassificationDescriptor(artifact.ContentKey, transaction) ?? throw ClassificationPolicy.Invalid();
        if (registered.Sha256 != artifact.Sha256 || registered.ByteLength != artifact.ByteLength) throw ClassificationPolicy.Invalid();
        Execute("""
            INSERT INTO item_classification_admissions VALUES($b,$r,$ref,$l,$kind,$k,$at)
            ON CONFLICT(build_id,region,source_reference,locale,kind) DO UPDATE SET content_key=excluded.content_key,prepared_at=excluded.prepared_at
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
            ("$l", artifact.Binding.Context.ResearchLocale), ("$kind", artifact.Binding.Kind.ToString()), ("$k", artifact.ContentKey), ("$at", preparedAt));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private void RequireClassificationItemSources(CapturedOpeningContext context, ItemsSnapshot snapshot, SqliteTransaction transaction)
    {
        foreach (var source in new[] { snapshot.ItemSource, snapshot.SparseSource })
        {
            using var command = Command("""
                SELECT content_key,source,association FROM table_admissions
                WHERE build_id=$b AND region=$r AND source_reference=$ref AND table_id=$t AND locale=$l
                """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
                ("$t", source.TableId), ("$l", snapshot.Context.ResearchLocale));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != source.ArtifactId ||
                !ClassificationPolicy.SameAuthority(Deserialize<ContentReceipt>(reader.GetString(1)), source.Source) ||
                !ClassificationPolicy.SameAuthority(Deserialize<SelectedFileAssociation>(reader.GetString(2)), source.Association))
                throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The exact Item source or association changed before classification admission.");
        }
    }

    internal Task<IReadOnlyList<ItemClassificationProblem?>> ReauthorizeItemClassificationBatchAsync(
        CapturedOpeningContext context, ItemsSnapshot currentItems, IReadOnlyList<ClassificationIndexArtifact> artifacts, CancellationToken token)
    {
        if (artifacts is null || artifacts.Count > 2) throw ClassificationPolicy.Invalid();
        var expected = artifacts.ToArray();
        if (expected.Any(a => a is null || a.Binding is null) || expected.Select(a => a.Binding.Kind).Distinct().Count() != expected.Length) throw ClassificationPolicy.Invalid();
        foreach (var artifact in expected) ClassificationPolicy.Validate(artifact);
        return Schedule<IReadOnlyList<ItemClassificationProblem?>>(() =>
        {
            using var transaction = Db.BeginTransaction();
            // One coherent authority point, after all external eligibility checks; Items always fences first.
            RequireIconFeature(context, currentItems, transaction);
            RequireClassificationItemSources(context, currentItems, transaction);
            var problems = new ItemClassificationProblem?[expected.Length];
            for (var i = 0; i < expected.Length; i++)
            {
                try
                {
                    var artifact = expected[i];
                    RequireClassificationTarget(context, artifact, transaction);
                    var admitted = Scalar("""
                        SELECT content_key FROM item_classification_admissions
                        WHERE build_id=$b AND region=$r AND source_reference=$ref AND locale=$l AND kind=$kind
                        """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
                        ("$l", currentItems.Context.ResearchLocale), ("$kind", artifact.Binding.Kind.ToString())) as string;
                    if (admitted != artifact.ContentKey) throw ClassificationPolicy.Stale();
                    var registered = ClassificationDescriptor(artifact.ContentKey, transaction);
                    if (registered is null || registered.Sha256 != artifact.Sha256 || registered.ByteLength != artifact.ByteLength) throw ClassificationPolicy.Invalid();
                }
                catch (DiscoveryException error) when (!error.Code.StartsWith("ITEMS_", StringComparison.Ordinal) && error.Code != "ENGINE_STOPPING")
                { problems[i] = new(error.Code, error.Message, error.Retryable); }
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return Array.AsReadOnly(problems);
        }, token);
    }

    // Used again immediately before delivery; source authority never uses normalized identity bytes.
    internal Task ReauthorizeItemClassificationTargetAsync(CapturedOpeningContext context, ClassificationIndexArtifact artifact,
        CancellationToken token) => Schedule(() => { RequireClassificationTarget(context, artifact, null); return true; }, token);
    private void RequireClassificationTarget(CapturedOpeningContext context, ClassificationIndexArtifact artifact, SqliteTransaction? transaction)
    {
        var b = artifact.Binding; var c = b.Context;
        if (c.BuildIdentity != context.Build.Id || c.ProductId != context.Build.ProductId || c.SourceRegion != context.Reference.Region ||
            b.BuildVersion != context.Build.Version || b.SourceReference != context.Reference || c.ResearchLocale != "enUS") throw ClassificationPolicy.Stale();
        var configurations = ClassificationConfigurations(context, transaction);
        foreach (var receipt in b.Configurations)
        {
            var current = configurations.SingleOrDefault(value => value.Role == receipt.Role);
            if (current is null || !ClassificationPolicy.SameAuthority(receipt, current)) throw ClassificationPolicy.Invalid();
        }
        using var command = Command("""
            SELECT a.content_key,a.source,a.association,r.descriptor FROM table_admissions a
            JOIN table_artifacts r ON r.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.table_id=$t AND a.locale=$l
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
            ("$t", b.Source.TableId), ("$l", c.ResearchLocale));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != b.Source.ArtifactId ||
            !ClassificationPolicy.SameAuthority(Deserialize<ContentReceipt>(reader.GetString(1)), b.Source.Source) ||
            !ClassificationPolicy.SameAuthority(Deserialize<SelectedFileAssociation>(reader.GetString(2)), b.Source.Association) ||
            Serialize(Deserialize<TableRowsArtifact>(reader.GetString(3))) != Serialize(b.Rows)) throw ClassificationPolicy.Stale();
        reader.Close();
        // Another saved copy of encoding or ROOT is a precise mismatch, checked before the generic table association integrity.
        foreach (var metadata in new[] { b.Source.Association.EncodingSource, b.Source.Association.RootSource })
        {
            var original = Scalar("SELECT receipt FROM metadata_objects WHERE ekey=$k", transaction, ("$k", metadata.EncodingKey)) as string;
            if (original is null) throw ClassificationPolicy.Invalid();
            if (!MetadataReceiptIdentity.Same(Deserialize<MetadataReceipt>(original), metadata))
                throw MetadataReceiptIdentity.Mismatch("ITEM_CLASSIFICATION_SOURCE_RECEIPT_MISMATCH", "An Item classification table", metadata);
        }
        ValidateTableSourceAssociation(b.Source.Source, b.Source.Association, transaction);
        var canonical = Scalar("SELECT receipt FROM content_objects WHERE ekey=$k", transaction, ("$k", b.Source.Source.EncodingKey)) as string;
        if (canonical is null || !ClassificationPolicy.SameAuthority(Deserialize<ContentReceipt>(canonical), b.Source.Source)) throw ClassificationPolicy.Invalid();
    }
}
