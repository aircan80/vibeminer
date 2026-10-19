using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record StoredItemsAdmission(ItemsProjectionArtifact Artifact, string PreparedAt);

public sealed partial class DiscoveryCatalog
{
    internal Task<ItemsProjectionArtifact?> FindItemsArtifactAsync(string contentKey, CancellationToken token) => Schedule(() =>
    {
        if (!DownloadHash(contentKey, 64)) throw new DiscoveryException("INVALID_REQUEST", "Choose an exact Items feature artifact.");
        var saved = Scalar("SELECT descriptor FROM items_artifacts WHERE content_key=$k", null, ("$k", contentKey)) as string;
        if (saved is null) return null;
        var artifact = Deserialize<ItemsProjectionArtifact>(saved); ItemsProjection.ValidateArtifact(artifact);
        if (artifact.ContentKey != contentKey) throw TableIntegrity("An Items artifact has a conflicting immutable identity.");
        return artifact;
    }, token);
    internal Task RecordItemsArtifactAsync(ItemsProjectionArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ItemsProjection.ValidateArtifact(artifact); var descriptor = Serialize(artifact);
        using var transaction = Db.BeginTransaction();
        var saved = Scalar("SELECT descriptor FROM items_artifacts WHERE content_key=$k", transaction, ("$k", artifact.ContentKey)) as string;
        if (saved is not null)
        {
            // Original source PreparedAt belongs to its admission, not a reason to rewrite immutable bytes.
            var original = Deserialize<ItemsProjectionArtifact>(saved); ItemsProjection.ValidateArtifact(original);
            if (original.Sha256 != artifact.Sha256 || original.ByteLength != artifact.ByteLength)
                throw TableIntegrity("An exact Items interpretation cannot be reassociated with different immutable bytes.");
        }
        Execute("INSERT OR IGNORE INTO items_artifacts VALUES($k,$d)", transaction, ("$k", artifact.ContentKey), ("$d", descriptor));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    internal Task<StoredItemsAdmission?> ReadItemsAdmissionAsync(CapturedOpeningContext context, string locale, CancellationToken token) => Schedule<StoredItemsAdmission?>(() =>
    {
        RequireTableWorkspace(context, null);
        using var command = Command("""
            SELECT f.descriptor,a.prepared_at FROM items_admissions a JOIN items_artifacts f ON f.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.locale=$l
            """, null, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)), ("$l", locale));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var artifact = Deserialize<ItemsProjectionArtifact>(reader.GetString(0)); var preparedAt = reader.GetString(1); reader.Close();
        ItemsProjection.ValidateArtifact(artifact); RequireItemsContext(context, locale, artifact);
        if (!DownloadUtc(preparedAt)) throw TableIntegrity("The Items admission has invalid original publication time.");
        if (!ItemsSourcesCurrent(context, locale, artifact, null)) return null;
        return new(artifact, preparedAt);
    }, token);
    internal Task CommitItemsAdmissionAsync(CapturedOpeningContext context, string locale, ItemsProjectionArtifact artifact,
        string preparedAt, CancellationToken token) => Schedule(() =>
    {
        ItemsProjection.ValidateArtifact(artifact); RequireItemsContext(context, locale, artifact);
        if (!DownloadUtc(preparedAt)) throw TableIntegrity("The Items publication time is invalid.");
        using var transaction = Db.BeginTransaction(); RequireTableWorkspace(context, transaction);
        if (!ItemsSourcesCurrent(context, locale, artifact, transaction))
            throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The Items prerequisites changed before feature admission.");
        var saved = Scalar("SELECT descriptor FROM items_artifacts WHERE content_key=$k", transaction, ("$k", artifact.ContentKey)) as string;
        if (saved is null || Deserialize<ItemsProjectionArtifact>(saved).Sha256 != artifact.Sha256)
            throw TableIntegrity("The complete immutable Items artifact must be registered before admission.");
        Execute("""
            INSERT INTO items_admissions VALUES($b,$r,$ref,$l,$k,$at)
            ON CONFLICT(build_id,region,source_reference,locale) DO UPDATE SET content_key=excluded.content_key,prepared_at=excluded.prepared_at
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
            ("$l", locale), ("$k", artifact.ContentKey), ("$at", preparedAt));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    private static void RequireItemsContext(CapturedOpeningContext opening, string locale, ItemsProjectionArtifact artifact)
    {
        var context = artifact.Binding.Context;
        if (context.ProductId != opening.Build.ProductId || context.BuildIdentity != opening.Build.Id ||
            context.SourceRegion != opening.Reference.Region || context.ResearchLocale != locale || locale != "enUS")
            throw TableIntegrity("The Items admission differs from its exact opening source and locale.");
    }
    private bool ItemsSourcesCurrent(CapturedOpeningContext context, string locale, ItemsProjectionArtifact artifact, SqliteTransaction? transaction)
    {
        foreach (var source in new[] { artifact.Binding.ItemSource, artifact.Binding.SparseSource })
        {
            var current = Scalar("""
                SELECT content_key FROM table_admissions WHERE build_id=$b AND region=$r AND source_reference=$ref AND table_id=$t AND locale=$l
                """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)), ("$t", source.TableId), ("$l", locale)) as string;
            if (current != source.ArtifactId) return false;
        }
        return true;
    }
}
