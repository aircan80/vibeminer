using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Only bounded core descriptors/admissions; original Creature rows stay in CAS.</summary>
public sealed partial class DiscoveryCatalog
{
    private static string CreatureText(SqliteDataReader reader, int text, int length, ref int budget)
    {
        var bytes = reader.GetInt64(length);
        if (bytes < 1 || bytes > budget) throw CreaturesPolicy.Limit(); budget -= (int)bytes; return reader.GetString(text);
    }
    private static T CreatureJson<T>(string value)
    {
        try { return JsonSerializer.Deserialize<T>(value, CreaturesPolicy.Json) ?? throw CreaturesPolicy.Invalid(); }
        catch (JsonException) { throw CreaturesPolicy.Invalid(); }
    }
    private static bool CreatureAuthority<T>(T a, T b)
    {
        var left = JsonSerializer.SerializeToNode(a, CreaturesPolicy.Json); var right = JsonSerializer.SerializeToNode(b, CreaturesPolicy.Json);
        void Normalize(JsonNode? value)
        {
            if (value is JsonObject obj) foreach (var pair in obj.ToArray()) { if (pair.Key == "verifiedAt") obj[pair.Key] = ""; else Normalize(pair.Value); }
            else if (value is JsonArray array) foreach (var child in array) Normalize(child);
        }
        Normalize(left); Normalize(right); return JsonNode.DeepEquals(left, right);
    }
    private void RequireCreatureOpening(CapturedOpeningContext context, SqliteTransaction? transaction)
    {
        var reference = context.Reference;
        using var command = Command("""
            SELECT r.generation,r.ordinal,r.cdns_ordinal,v.sha256,c.sha256,length(CAST(v.sha256 AS BLOB)),length(CAST(c.sha256 AS BLOB)) FROM build_regions r
            JOIN documents v ON v.generation=r.generation AND v.role='versions'
            JOIN documents c ON c.generation=r.generation AND c.role='cdns'
            WHERE r.build_id=$b AND r.region=$r
            """, transaction, ("$b", context.Build.Id), ("$r", reference.Region));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(5) != 64 || reader.GetInt64(6) != 64 || reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture) != reference.Generation ||
            reader.GetInt32(1) != reference.VersionsRowOrdinal || reader.IsDBNull(2) || reader.GetInt32(2) != reference.CdnsRowOrdinal ||
            reader.GetString(3) != reference.VersionsSha256 || reader.GetString(4) != reference.CdnsSha256) throw CreaturesPolicy.Stale();
        reader.Close(); RequireTableWorkspace(context, transaction);
    }
    internal Task<IReadOnlyList<ConfigurationReceipt>> ReadCreatureConfigurationsAsync(CapturedOpeningContext context, CancellationToken token) =>
        Schedule(() => { var budget = CreaturesPolicy.MaxDescriptorBytes; return CreatureConfigurations(context, null, ref budget); }, token);
    private IReadOnlyList<ConfigurationReceipt> CreatureConfigurations(CapturedOpeningContext context, SqliteTransaction? transaction, ref int budget)
    {
        RequireCreatureOpening(context, transaction);
        var id = Scalar("SELECT id FROM workspace_admissions WHERE build_id=$b AND product=$p AND region=$r AND source_reference=$ref ORDER BY id DESC LIMIT 1", transaction,
            ("$b", context.Build.Id), ("$p", context.Build.ProductId), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference))) ?? throw CreaturesPolicy.Stale();
        var receipts = new List<ConfigurationReceipt>(2);
        using (var command = Command("""
            SELECT role,key,sha256,byte_length,source_url,retrieved_at,verified_at,parsed_field_count,
            length(CAST(role AS BLOB))+length(CAST(key AS BLOB))+length(CAST(sha256 AS BLOB))+length(CAST(source_url AS BLOB))+
            length(CAST(retrieved_at AS BLOB))+length(CAST(verified_at AS BLOB)) FROM workspace_configurations WHERE admission_id=$id ORDER BY role LIMIT 3
            """, transaction, ("$id", id)))
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            if (receipts.Count == 2 || reader.GetInt64(8) is < 1 || reader.GetInt64(8) > budget) throw CreaturesPolicy.Limit(); budget -= checked((int)reader.GetInt64(8));
            receipts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7)));
        }
        if (receipts.Count != 2 || receipts.Count(r => r.Role == "buildConfig" && r.Key == context.Build.BuildConfig) != 1 || receipts.Count(r => r.Role == "cdnConfig" && r.Key == context.Build.CdnConfig) != 1) throw CreaturesPolicy.Invalid();
        foreach (var receipt in receipts)
        {
            using var command = Command("""
                SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision,length(CAST(sha256 AS BLOB))+length(CAST(source_url AS BLOB))+length(CAST(retrieved_at AS BLOB))+length(CAST(verifier_revision AS BLOB))
                FROM configuration_objects WHERE key=$k
                """, transaction, ("$k", receipt.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(5) > budget || reader.GetInt64(5) < 1) throw CreaturesPolicy.Invalid(); budget -= checked((int)reader.GetInt64(5));
            if (reader.GetString(0) != receipt.Sha256 || reader.GetInt32(1) != receipt.ByteLength || reader.GetString(2) != receipt.SourceUrl || reader.GetString(3) != receipt.RetrievedAt ||
                reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision || !DownloadUtc(receipt.VerifiedAt) || receipt.ParsedFieldCount is < 0 or > 4096) throw CreaturesPolicy.Invalid();
        }
        return receipts;
    }
    internal Task<StoredTableAdmission?> ReadCreatureSourceAsync(CapturedOpeningContext context, CancellationToken token) => Schedule(() =>
    {
        var budget = CreaturesPolicy.MaxDescriptorBytes; _ = CreatureConfigurations(context, null, ref budget);
        var source = CreatureSource(context, null, ref budget);
        if (source is not null) RequireCreatureOriginals(source.Source, source.Association, null, ref budget);
        return source;
    }, token);
    private StoredTableAdmission? CreatureSource(CapturedOpeningContext context, SqliteTransaction? transaction, ref int budget)
    {
        RequireCreatureOpening(context, transaction);
        using var command = Command("""
            SELECT a.source,a.association,r.descriptor,a.prepared_at,length(CAST(a.source AS BLOB)),length(CAST(a.association AS BLOB)),length(CAST(r.descriptor AS BLOB)),length(CAST(a.prepared_at AS BLOB))
            FROM table_admissions a JOIN table_artifacts r ON r.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.table_id=$t AND a.locale='enUS'
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)), ("$t", CreaturesPolicy.Table));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        // Check every persisted JSON length before materializing any part of the admission.
        for (var i = 4; i <= 7; i++) if (reader.GetInt64(i) < 1 || reader.GetInt64(i) > budget) throw CreaturesPolicy.Limit();
        var total = checked(reader.GetInt64(4) + reader.GetInt64(5) + reader.GetInt64(6) + reader.GetInt64(7)); if (total > budget) throw CreaturesPolicy.Limit();
        var source = CreatureJson<ContentReceipt>(CreatureText(reader, 0, 4, ref budget));
        var association = CreatureJson<SelectedFileAssociation>(CreatureText(reader, 1, 5, ref budget));
        var rows = CreatureJson<TableRowsArtifact>(CreatureText(reader, 2, 6, ref budget)); var at = CreatureText(reader, 3, 7, ref budget); reader.Close();
        if (source is null || association is null || association.EncodingSource is null || association.RootSource is null || association.Memberships is null || association.Memberships.Count is < 1 or > 256 || association.Memberships.Any(m => m is null) || rows is null || rows.Binding is null || rows.Header is null || rows.Header.Columns is null || rows.Header.Columns.Count is < 1 or > 512 || rows.Header.Columns.Any(c => c is null) || rows.Completion is null) throw CreaturesPolicy.Invalid();
        ValidateTableAdmission(CreaturesPolicy.Table, "enUS", source, association, rows, at, false);
        return new(rows, source, association, at);
    }
    private CreaturesArtifact? CreatureDescriptor(string key, SqliteTransaction? transaction, ref int budget)
    {
        using var command = Command("SELECT descriptor,length(CAST(descriptor AS BLOB)) FROM creatures_artifacts WHERE content_key=$k", transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var artifact = CreatureJson<CreaturesArtifact>(CreatureText(reader, 0, 1, ref budget)); CreaturesPolicy.Validate(artifact);
        if (artifact.ContentKey != key) throw CreaturesPolicy.Invalid(); return artifact;
    }
    internal Task<CreaturesArtifact?> FindCreaturesArtifactAsync(string key, CancellationToken token) => Schedule(() =>
    { var budget = CreaturesPolicy.MaxDescriptorBytes; return CreatureDescriptor(key, null, ref budget); }, token);
    internal Task RecordCreaturesArtifactAsync(CreaturesArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        CreaturesPolicy.Validate(artifact); var budget = CreaturesPolicy.MaxDescriptorBytes; using var transaction = Db.BeginTransaction();
        var previous = CreatureDescriptor(artifact.ContentKey, transaction, ref budget);
        if (previous is not null && (previous.Sha256 != artifact.Sha256 || previous.ByteLength != artifact.ByteLength)) throw CreaturesPolicy.Invalid();
        Execute("INSERT OR IGNORE INTO creatures_artifacts VALUES($k,$d)", transaction, ("$k", artifact.ContentKey), ("$d", Encoding.UTF8.GetString(CreaturesPolicy.Bytes(artifact))));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    internal Task<StoredCreaturesAdmission?> ReadCreaturesAdmissionAsync(CapturedOpeningContext context, CancellationToken token) => Schedule(() =>
    {
        RequireCreatureOpening(context, null); var budget = CreaturesPolicy.MaxDescriptorBytes;
        using var command = Command("SELECT content_key,prepared_at,length(CAST(prepared_at AS BLOB)),length(CAST(content_key AS BLOB)) FROM creatures_admissions WHERE build_id=$b AND region=$r AND source_reference=$ref AND locale='enUS'", null,
            ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetInt64(3) != 64) throw CreaturesPolicy.Invalid();
        var key = reader.GetString(0); if (!CreaturesPolicy.Hash(key)) throw CreaturesPolicy.Invalid();
        var at = CreatureText(reader, 1, 2, ref budget); reader.Close(); if (!DownloadUtc(at)) throw CreaturesPolicy.Invalid();
        var artifact = CreatureDescriptor(key, null, ref budget) ?? throw CreaturesPolicy.Invalid(); RequireCreatureSource(context, artifact, null, ref budget); return new StoredCreaturesAdmission(artifact, at);
    }, token);
    private void RequireCreatureSource(CapturedOpeningContext context, CreaturesArtifact artifact, SqliteTransaction? transaction, ref int budget)
    {
        var b = artifact.Binding; if (b.SourceReference != context.Reference || b.Context.BuildIdentity != context.Build.Id || b.Context.ProductId != context.Build.ProductId || b.BuildVersion != context.Build.Version || b.Context.SourceRegion != context.Reference.Region) throw CreaturesPolicy.Stale();
        var configurations = CreatureConfigurations(context, transaction, ref budget);
        if (b.Configurations.Any(r => !configurations.Any(c => c.Role == r.Role && CreatureAuthority(c, r)))) throw CreaturesPolicy.Invalid();
        var current = CreatureSource(context, transaction, ref budget) ?? throw CreaturesPolicy.Stale();
        if (current.Artifact != b.Rows && Serialize(current.Artifact) != Serialize(b.Rows) || !CreatureAuthority(current.Source, b.Source.Source) || !CreatureAuthority(current.Association, b.Source.Association)) throw CreaturesPolicy.Stale();
        RequireCreatureOriginals(b.Source.Source, b.Source.Association, transaction, ref budget);
    }
    private void RequireCreatureOriginals(ContentReceipt source, SelectedFileAssociation association, SqliteTransaction? transaction, ref int budget)
    {
        var receipt = CreatureCanonical<ContentReceipt>("content_objects", source.EncodingKey, transaction, ref budget);
        ValidateContentReceipt(receipt); if (!CreatureAuthority(receipt, source)) throw CreaturesPolicy.Invalid();
        foreach (var metadata in new[] { association.EncodingSource, association.RootSource })
        {
            var original = CreatureCanonical<MetadataReceipt>("metadata_objects", metadata.EncodingKey, transaction, ref budget);
            if (!MetadataReceiptIdentity.Same(original, metadata)) throw CreaturesPolicy.ReceiptMismatch(metadata);
        }
        ValidateTableSourceAssociation(source, association, transaction);
    }
    private T CreatureCanonical<T>(string table, string key, SqliteTransaction? transaction, ref int budget)
    {
        // table is one of the two internal fixed call-site constants, never input.
        using var command = Command($"SELECT receipt,length(CAST(receipt AS BLOB)) FROM {table} WHERE ekey=$k", transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw CreaturesPolicy.Invalid(); return CreatureJson<T>(CreatureText(reader, 0, 1, ref budget));
    }
    internal Task CommitCreaturesAdmissionAsync(CapturedOpeningContext context, CreaturesArtifact artifact, string at, CancellationToken token) => Schedule(() =>
    {
        CreaturesPolicy.Validate(artifact); if (!DownloadUtc(at)) throw CreaturesPolicy.Invalid(); var budget = CreaturesPolicy.MaxDescriptorBytes; using var transaction = Db.BeginTransaction();
        RequireCreatureSource(context, artifact, transaction, ref budget); var saved = CreatureDescriptor(artifact.ContentKey, transaction, ref budget) ?? throw CreaturesPolicy.Invalid();
        if (!CreatureAuthority(saved, artifact)) throw CreaturesPolicy.Invalid();
        Execute("""
            INSERT INTO creatures_admissions VALUES($b,$r,$ref,'enUS',$k,$at) ON CONFLICT(build_id,region,source_reference,locale)
            DO UPDATE SET content_key=excluded.content_key,prepared_at=excluded.prepared_at
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)), ("$k", artifact.ContentKey), ("$at", at));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    internal Task ReauthorizeCreatureDependencyAsync(CapturedOpeningContext context, TableDataSnapshot source, CancellationToken token) => Schedule(() =>
    {
        var budget = CreaturesPolicy.MaxDescriptorBytes; using var transaction = Db.BeginTransaction();
        _ = CreatureConfigurations(context, transaction, ref budget);
        var current = CreatureSource(context, transaction, ref budget) ?? throw CreaturesPolicy.Stale();
        if (source.ArtifactId != current.Artifact.ContentKey || !CreatureAuthority(source.Source, current.Source) || !CreatureAuthority(source.Association, current.Association)) throw CreaturesPolicy.Stale();
        RequireCreatureOriginals(source.Source, source.Association, transaction, ref budget);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    internal Task ReauthorizeCreaturesAsync(CapturedOpeningContext context, CreaturesArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        var budget = CreaturesPolicy.MaxDescriptorBytes; using var transaction = Db.BeginTransaction();
        RequireCreatureFeature(context, artifact, transaction, ref budget);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    // Shared by C1 final delivery and the later C2 coherent core-plus-target transaction.
    private void RequireCreatureFeature(CapturedOpeningContext context, CreaturesArtifact artifact, SqliteTransaction transaction, ref int budget)
    {
        RequireCreatureSource(context, artifact, transaction, ref budget);
        using var command = Command("SELECT content_key,length(CAST(content_key AS BLOB)) FROM creatures_admissions WHERE build_id=$b AND region=$r AND source_reference=$ref AND locale='enUS'", transaction,
            ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw CreaturesPolicy.Stale();
        if (reader.GetInt64(1) != 64) throw CreaturesPolicy.Invalid();
        var key = reader.GetString(0); if (!CreaturesPolicy.Hash(key)) throw CreaturesPolicy.Invalid(); reader.Close();
        if (key != artifact.ContentKey) throw CreaturesPolicy.Stale(); var saved = CreatureDescriptor(key, transaction, ref budget) ?? throw CreaturesPolicy.Invalid();
        if (!CreatureAuthority(saved, artifact)) throw CreaturesPolicy.Stale();
    }
}
