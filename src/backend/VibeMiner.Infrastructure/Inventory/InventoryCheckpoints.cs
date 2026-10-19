using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record InventoryCheckpointCounts(long ContentRecords, long CandidateReferences,
    long EncodingObjects, long OriginRecords, long TagRecords, long ResolvedRecords, long LastOriginId);
internal sealed record InventoryCheckpoint(string Kind, MetadataReceipt? Source,
    InventoryCheckpointCounts Counts, string Sha256);
internal sealed record ResumeInventoryState(long Generation,
    IReadOnlyDictionary<string, InventoryCheckpoint> Checkpoints, long LastOriginId,
    IReadOnlyDictionary<string, long> FamilyCounts);

public sealed partial class DiscoveryCatalog
{
    internal const string InventoryCheckpointRevision = "inventory-checkpoint-v1";
    private static readonly string[] CheckpointKinds = ["encoding-content", "encoding", "root", "install", "download",
        "download-resolved", "size", "size-resolved", "root-resolved", "install-resolved"];
    private static readonly string[] CheckpointFamilies = ["root", "install", "download", "size"];
    private void UpgradeInventoryCheckpoints()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE inventory_checkpoints(g INTEGER NOT NULL,kind TEXT NOT NULL,revision TEXT NOT NULL,
                receipt TEXT,proof TEXT NOT NULL,sha256 TEXT NOT NULL,completed_at TEXT NOT NULL,
                PRIMARY KEY(g,kind),FOREIGN KEY(g) REFERENCES inventory_generations(id) ON DELETE CASCADE);
            CREATE TABLE inventory_merge_results(g INTEGER NOT NULL,family TEXT NOT NULL,id INTEGER NOT NULL,
                ekey TEXT NOT NULL,encoded INTEGER NOT NULL,proof BLOB NOT NULL,PRIMARY KEY(g,family,id),
                FOREIGN KEY(g) REFERENCES inventory_generations(id) ON DELETE CASCADE);
            CREATE TABLE inventory_merge_state(g INTEGER NOT NULL,family TEXT NOT NULL,total INTEGER NOT NULL,
                matched INTEGER NOT NULL DEFAULT 0,saved INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(g,family),
                FOREIGN KEY(g) REFERENCES inventory_generations(id) ON DELETE CASCADE);
            CREATE TABLE inventory_resolution_cursor(g INTEGER NOT NULL,family TEXT NOT NULL,
                after INTEGER NOT NULL,completed INTEGER NOT NULL,PRIMARY KEY(g,family),
                FOREIGN KEY(g) REFERENCES inventory_generations(id) ON DELETE CASCADE);
            PRAGMA user_version=5;
            """, transaction);
        transaction.Commit();
    }

    internal async Task<InventoryCheckpoint> SaveInventoryCheckpointAsync(long generation, string kind,
        MetadataReceipt? source, CancellationToken token, Func<string, Task>? progress = null)
    {
        ValidateCheckpointKind(kind);
        ValidateCheckpointSource(kind, source);
        var counts = await Schedule(() =>
        {
            RequireStagingInventory(generation);
            ValidateCheckpointAssociation(source, null);
            ValidateCheckpointDependencies(generation, kind);
            ValidateEncodingCheckpointSource(generation, kind, source, null);
            return CheckpointCounts(generation, kind, null);
        }, token);
        var digest = await ComputeCheckpointDigestAsync(generation, kind, token, progress);
        return await Schedule(() =>
        {
            RequireStagingInventory(generation);
            using var tx = Db.BeginTransaction();
            ValidateCheckpointAssociation(source, tx);
            ValidateCheckpointDependencies(generation, kind, tx);
            ValidateEncodingCheckpointSource(generation, kind, source, tx);
            if (counts != CheckpointCounts(generation, kind, tx))
                throw CheckpointError("Checkpoint rows changed before completion could be committed.");
            var existing = ReadCheckpoint(generation, kind, tx);
            if (existing is not null)
            {
                if (existing.Counts != counts || existing.Sha256 != digest || !SameCheckpointSource(existing.Source, source))
                    throw CheckpointError("A completed preparation checkpoint cannot be replaced by different facts.");
                return existing;
            }
            token.ThrowIfCancellationRequested();
            Execute("INSERT INTO inventory_checkpoints VALUES($g,$k,$r,$s,$p,$h,$t)", tx,
                ("$g", generation), ("$k", kind), ("$r", InventoryCheckpointRevision),
                ("$s", source is null ? null : Serialize(source)), ("$p", Serialize(counts)),
                ("$h", digest), ("$t", DateTimeOffset.UtcNow.ToString("O")));
            tx.Commit();
            return new InventoryCheckpoint(kind, source, counts, digest);
        }, token);
    }

    // Schema upgrades must remain small enough for engine startup. Build this potentially large
    // secondary index only during visible preparation, on the existing bounded database worker.
    internal async Task EnsureInventoryReferenceIndexAsync(CancellationToken token, Func<string, Task>? progress = null)
    {
        var exists = await Schedule(() => Scalar("SELECT 1 FROM sqlite_master WHERE type='index' AND name='inventory_reference'") is not null, token);
        if (exists) return;
        if (progress is not null) await progress("Preparing ordered inventory lookup index");
        await Schedule(() =>
        {
            token.ThrowIfCancellationRequested();
            Execute("CREATE INDEX IF NOT EXISTS inventory_reference ON inventory_origins(g,family,reference,id)");
            return true;
        }, token);
        token.ThrowIfCancellationRequested();
    }

    internal async Task<ResumeInventoryState?> FindResumableInventoryAsync(CapturedOpeningContext context,
        CancellationToken token, Func<string, Task>? progress = null)
    {
        var candidate = await Schedule(() =>
        {
            using var command = Command("""
                SELECT i.id FROM inventory_generations i WHERE i.build_id=$b AND i.region=$r AND i.profile=$p
                  AND i.source=$s AND i.state IN ('staging','abandoned')
                  AND EXISTS(SELECT 1 FROM inventory_checkpoints c WHERE c.g=i.id AND c.revision=$v)
                ORDER BY i.id DESC LIMIT 1
                """, null, ("$b", context.Build.Id), ("$r", context.Reference.Region),
                ("$p", InventoryWorkspacePreparer.Profile), ("$s", Serialize(context.Reference)), ("$v", InventoryCheckpointRevision));
            return command.ExecuteScalar() is { } id ? (long?)Convert.ToInt64(id) : null;
        }, token);
        if (candidate is not { } generation) return null;
        var checkpoints = await Schedule(() =>
        {
            var result = new Dictionary<string, InventoryCheckpoint>(StringComparer.Ordinal);
            using var command = Command("SELECT kind FROM inventory_checkpoints WHERE g=$g ORDER BY kind", null, ("$g", generation));
            var kinds = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (kinds.Count >= CheckpointKinds.Length) throw CheckpointError("Too many saved preparation checkpoints exist.");
                kinds.Add(reader.GetString(0));
            }
            foreach (var kind in kinds)
            {
                token.ThrowIfCancellationRequested();
                ValidateCheckpointKind(kind);
                var checkpoint = ReadCheckpoint(generation, kind, null)!;
                ValidateCheckpointSource(kind, checkpoint.Source);
                ValidateCheckpointAssociation(checkpoint.Source, null);
                ValidateCheckpointDependencies(generation, kind);
                ValidateEncodingCheckpointSource(generation, kind, checkpoint.Source, null);
                if (CheckpointCounts(generation, kind, null) != checkpoint.Counts)
                    throw CheckpointError("A completed preparation checkpoint has lost or changed rows.");
                result.Add(kind, checkpoint);
            }
            return result;
        }, token);
        foreach (var checkpoint in checkpoints.Values)
        {
            token.ThrowIfCancellationRequested();
            if (progress is not null) await progress("Verifying saved " + checkpoint.Kind + " preparation checkpoint");
            if (checkpoint.Sha256 != await ComputeCheckpointDigestAsync(generation, checkpoint.Kind, token, progress))
                throw CheckpointError("A completed preparation checkpoint failed its saved integrity proof.");
        }
        return await Schedule(() =>
        {
            using var tx = Db.BeginTransaction();
            // Exact source and state are checked again after bounded verification yielded the worker.
            var changed = ExecuteCheckpointUpdate("""
                UPDATE inventory_generations SET state='staging' WHERE id=$g AND build_id=$b AND region=$r
                  AND profile=$p AND source=$s AND state IN ('staging','abandoned')
                """, tx, ("$g", generation), ("$b", context.Build.Id), ("$r", context.Reference.Region),
                ("$p", InventoryWorkspacePreparer.Profile), ("$s", Serialize(context.Reference)));
            if (changed != 1) throw CheckpointError("Preparation checkpoint ownership changed during restore.");
            var familyCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var family in CheckpointFamilies)
                if (checkpoints.TryGetValue(family, out var checkpoint)) familyCounts.Add(family, checkpoint.Counts.OriginRecords);
            var lastId = CheckpointFamilies.Where(checkpoints.ContainsKey).Select(family => checkpoints[family].Counts.LastOriginId).DefaultIfEmpty().Max();
            token.ThrowIfCancellationRequested();
            tx.Commit();
            return new ResumeInventoryState(generation, new ReadOnlyDictionary<string, InventoryCheckpoint>(checkpoints), lastId,
                new ReadOnlyDictionary<string, long>(familyCounts));
        }, token);
    }

    internal async Task ResetIncompleteInventoryAsync(long generation, CancellationToken token, Func<string, Task>? progress = null)
    {
        var kinds = await Schedule(() =>
        {
            RequireStagingInventory(generation);
            var result = new HashSet<string>(StringComparer.Ordinal);
            using var command = Command("SELECT kind FROM inventory_checkpoints WHERE g=$g AND revision=$r", null,
                ("$g", generation), ("$r", InventoryCheckpointRevision));
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }, token);
        var operations = new List<(string Table, string? Family)>();
        if (!kinds.Contains("encoding-content")) operations.AddRange([("en_content", null), ("en_candidates", null)]);
        if (!kinds.Contains("encoding")) operations.Add(("en_objects", null));
        foreach (var family in CheckpointFamilies)
            if (!kinds.Contains(family)) operations.AddRange([("inventory_origins", family), ("inventory_tags", family)]);
        foreach (var group in operations.GroupBy(item => item.Family ?? "encoding"))
        {
            var total = await Schedule(() => group.Sum(item => Convert.ToInt64(Scalar(
                $"SELECT COUNT(*) FROM {item.Table} WHERE g=$g{(item.Family is null ? "" : " AND family=$f")}", null,
                ("$g", generation), ("$f", item.Family)))), token);
            long completed = 0;
            var label = $"Cleaning up unfinished {group.Key} index";
            if (progress is not null) await progress(InventoryProgress.Format(label, 0, total, "records"));
            var lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var (table, family) in group)
            {
                while (true)
                {
                    var removed = await Schedule(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        RequireStagingInventory(generation);
                        using var command = Command($"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE g=$g{(family is null ? "" : " AND family=$f")} LIMIT 8192)",
                            null, ("$g", generation));
                        if (family is not null) command.Parameters.AddWithValue("$f", family);
                        return command.ExecuteNonQuery();
                    }, token);
                    completed += removed;
                    if (progress is not null && (completed == total || System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 500))
                    {
                        await progress(InventoryProgress.Format(label, completed, total, "records"));
                        lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    if (removed < 8192) break;
                }
            }
        }
    }

    private static void ValidateCheckpointKind(string kind)
    {
        if (!CheckpointKinds.Contains(kind, StringComparer.Ordinal)) throw CheckpointError("A preparation checkpoint kind is unsupported.");
    }
    private void ValidateCheckpointDependencies(long generation, string kind, SqliteTransaction? tx = null)
    {
        string? dependency = kind switch
        {
            "encoding-content" => null, "encoding" => "encoding-content",
            "root" or "install" or "download" or "size" => "encoding",
            _ when kind.EndsWith("-resolved", StringComparison.Ordinal) => kind[..^9],
            _ => throw CheckpointError("A preparation checkpoint dependency is unsupported.")
        };
        if (dependency is not null && Scalar("SELECT 1 FROM inventory_checkpoints WHERE g=$g AND kind=$k AND revision=$r", tx,
            ("$g", generation), ("$k", dependency), ("$r", InventoryCheckpointRevision)) is null)
            throw CheckpointError("A preparation checkpoint is missing its completed prerequisite.");
    }
    private void ValidateEncodingCheckpointSource(long generation, string kind, MetadataReceipt? source, SqliteTransaction? tx)
    {
        if (kind == "encoding" && !SameCheckpointObject(ReadCheckpoint(generation, "encoding-content", tx)?.Source, source))
            throw CheckpointError("Encoding content and object indexes must describe the same complete source.");
    }
    private void ValidateCheckpointAssociation(MetadataReceipt? source, SqliteTransaction? tx)
    {
        if (source is null) return;
        var encoded = Scalar("SELECT receipt FROM metadata_objects WHERE ekey=$e", tx, ("$e", source.EncodingKey)) as string;
        MetadataReceipt? saved;
        try { saved = encoded is null ? null : JsonSerializer.Deserialize<MetadataReceipt>(encoded, Json); }
        catch (JsonException) { throw CheckpointError("A checkpoint source association is malformed."); }
        if (!SameCheckpointObject(saved, source) || saved is null ||
            saved.ParserRevision != MetadataAcquirer.SourceRevision && saved.ParserRevision != source.Role + "-v1")
            throw CheckpointError("A checkpoint source differs from its original verified object association.");
    }
    private InventoryCheckpoint? ReadCheckpoint(long generation, string kind, SqliteTransaction? tx)
    {
        using var command = Command("SELECT revision,receipt,proof,sha256 FROM inventory_checkpoints WHERE g=$g AND kind=$k", tx,
            ("$g", generation), ("$k", kind));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != InventoryCheckpointRevision) throw CheckpointError("A preparation checkpoint revision is unsupported.");
        try
        {
            var source = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<MetadataReceipt>(reader.GetString(1), Json);
            var counts = JsonSerializer.Deserialize<InventoryCheckpointCounts>(reader.GetString(2), Json) ?? throw CheckpointError("Preparation checkpoint counts are missing.");
            var sha = reader.GetString(3);
            if (!CheckpointHex(sha, 64) || new[] { counts.ContentRecords, counts.CandidateReferences, counts.EncodingObjects,
                counts.OriginRecords, counts.TagRecords, counts.ResolvedRecords, counts.LastOriginId }.Any(value => value < 0))
                throw CheckpointError("Preparation checkpoint proof is malformed.");
            return new(kind, source, counts, sha);
        }
        catch (JsonException) { throw CheckpointError("Preparation checkpoint data could not be decoded."); }
    }
    private InventoryCheckpointCounts CheckpointCounts(long generation, string kind, SqliteTransaction? tx)
    {
        long Count(string table, string extra = "") => Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table} WHERE g=$g{extra}", tx, ("$g", generation)));
        if (kind == "encoding-content") return new(Count("en_content"), Count("en_candidates"), 0, 0, 0, 0, 0);
        if (kind == "encoding") return new(0, 0, Count("en_objects"), 0, 0, 0, 0);
        var resolved = kind.EndsWith("-resolved", StringComparison.Ordinal);
        var family = resolved ? kind[..^9] : kind;
        var total = Count("inventory_origins", " AND family='" + family + "'");
        var matched = resolved ? Count("inventory_origins", " AND family='" + family + "' AND ekey IS NOT NULL AND encoded IS NOT NULL" +
            (family is "root" or "install" ? " AND decoded IS NOT NULL" : "")) : 0;
        if (resolved && matched != total) throw CheckpointError("An unresolved family cannot have a completed resolution checkpoint.");
        var lastId = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(id),0) FROM inventory_origins WHERE g=$g AND family=$f", tx,
            ("$g", generation), ("$f", family)));
        return new(0, 0, 0, total, resolved ? 0 : Count("inventory_tags", " AND family='" + family + "'"), matched, lastId);
    }
    private static bool SameCheckpointSource(MetadataReceipt? first, MetadataReceipt? second) => SameCheckpointObject(first, second) &&
        first?.ParserRevision == second?.ParserRevision;
    private static bool SameCheckpointObject(MetadataReceipt? first, MetadataReceipt? second) => first is null ? second is null :
        second is not null && first.Role == second.Role && first.ContentKey == second.ContentKey && first.EncodingKey == second.EncodingKey &&
        first.Sha256 == second.Sha256 && first.EncodedBytes == second.EncodedBytes && first.DecodedBytes == second.DecodedBytes &&
        first.SourceUrl == second.SourceUrl && first.RetrievedAt == second.RetrievedAt;
    private static void ValidateCheckpointSource(string kind, MetadataReceipt? receipt)
    {
        if (kind.EndsWith("-resolved", StringComparison.Ordinal))
        {
            if (receipt is not null) throw CheckpointError("Resolution checkpoints use their source checkpoint rather than a new receipt.");
            return;
        }
        var role = kind == "encoding-content" ? "encoding" : kind;
        if (receipt is null || receipt.Role != role || !CheckpointHex(receipt.ContentKey, 32) || !CheckpointHex(receipt.EncodingKey, 32) ||
            !CheckpointHex(receipt.Sha256, 64) || receipt.ContentKey.All(ch => ch == '0') || receipt.EncodingKey.All(ch => ch == '0') ||
            !long.TryParse(receipt.EncodedBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var encoded) || encoded < 9 || encoded > (512L << 20) ||
            !long.TryParse(receipt.DecodedBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var decoded) || decoded < 1 || decoded > (1L << 30) ||
            receipt.EncodedBytes != encoded.ToString(CultureInfo.InvariantCulture) || receipt.DecodedBytes != decoded.ToString(CultureInfo.InvariantCulture) ||
            !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !(url.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" ||
                Regex.IsMatch(url.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            !Regex.IsMatch(url.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + receipt.EncodingKey[..2] + "/" +
                receipt.EncodingKey[2..4] + "/" + receipt.EncodingKey + "$", RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(receipt.RetrievedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var retrieved) || retrieved.Offset != TimeSpan.Zero ||
            !DateTimeOffset.TryParse(receipt.VerifiedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var verified) || verified.Offset != TimeSpan.Zero ||
            receipt.ParserRevision != role + "-v1" && !(kind == "encoding-content" && receipt.ParserRevision == MetadataAcquirer.SourceRevision))
            throw CheckpointError("A source checkpoint requires its exact supported verified metadata receipt.");
    }
    private static bool CheckpointHex(string value, int length) => value is not null && value.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private int ExecuteCheckpointUpdate(string sql, SqliteTransaction? tx, params (string Name, object? Value)[] values)
    { using var command = Command(sql, tx, values); return command.ExecuteNonQuery(); }
    private static DiscoveryException CheckpointError(string message) => new("LOCAL_CHECKPOINT_CORRUPT", message);
}
