using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
namespace VibeMiner.Infrastructure;

internal sealed record InventoryOrigin(long Id, string Family, string? Name, string? FileDataId,
    string? ContentKey, string? Reference, int ReferenceBytes, long? DeclaredEncoded, long? DeclaredDecoded,
    string? LocaleFlags, string? ContentFlags, string? NameHash, string Raw);
internal sealed record EncodingContentRow(string Key, long Bytes, IReadOnlyList<string> Candidates);
internal sealed record EncodingObjectRow(string Key, long Bytes);
internal sealed record InventoryCandidate(string Key, long Bytes);
internal sealed record ReadyInventory(long Generation, InventorySummary Summary, IReadOnlyList<MetadataReceipt> Metadata);

public sealed partial class DiscoveryCatalog : IInventoryCatalog
{
    // EN's one-byte count admits at most 255 candidates, with ordinals 0..254. Include that
    // bound in the forward lookup so the reverse covering index cannot win a build-wide scan
    // merely to avoid a table fetch when a fresh database has no collected planner statistics.
    internal const string RootCandidateSql = "SELECT e.ekey,e.bytes FROM en_candidates c JOIN en_objects e ON e.g=c.g AND e.ekey=c.ekey LEFT JOIN inventory_units u ON u.g=e.g AND u.ekey=e.ekey WHERE c.g=$g AND c.ckey=$c AND c.ordinal BETWEEN 0 AND 254 ORDER BY (u.ekey IS NOT NULL) DESC,EXISTS(SELECT 1 FROM inventory_available a WHERE a.g=c.g AND a.ekey=e.ekey) DESC,e.ekey LIMIT 1";

    private void UpgradeInventory()
    {
        using var tx = Db.BeginTransaction();
        Execute("""
            CREATE TABLE metadata_objects(ekey TEXT PRIMARY KEY,ckey TEXT NOT NULL,encoded INTEGER NOT NULL,receipt TEXT NOT NULL);
            CREATE TABLE inventory_generations(id INTEGER PRIMARY KEY AUTOINCREMENT,build_id TEXT NOT NULL,region TEXT NOT NULL,
                state TEXT NOT NULL,profile TEXT NOT NULL,summary TEXT,metadata TEXT,source TEXT NOT NULL,digest TEXT,
                FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE INDEX inventory_context ON inventory_generations(build_id,region,state,id DESC);
            CREATE TABLE en_content(g INTEGER NOT NULL,ckey TEXT NOT NULL,bytes INTEGER NOT NULL,PRIMARY KEY(g,ckey));
            CREATE TABLE en_candidates(g INTEGER NOT NULL,ckey TEXT NOT NULL,ordinal INTEGER NOT NULL,ekey TEXT NOT NULL,PRIMARY KEY(g,ckey,ordinal));
            CREATE INDEX en_reverse ON en_candidates(g,ekey,ckey);
            CREATE TABLE en_objects(g INTEGER NOT NULL,ekey TEXT NOT NULL,bytes INTEGER NOT NULL,PRIMARY KEY(g,ekey));
            CREATE TABLE inventory_origins(g INTEGER NOT NULL,id INTEGER NOT NULL,family TEXT NOT NULL,name TEXT,fdid TEXT,
                ckey TEXT,reference TEXT,width INTEGER NOT NULL,declared_encoded INTEGER,declared_decoded INTEGER,
                locale TEXT,flags TEXT,name_hash TEXT,raw TEXT NOT NULL,ekey TEXT,encoded INTEGER,decoded INTEGER,
                PRIMARY KEY(g,id));
            CREATE INDEX inventory_family ON inventory_origins(g,family,id);
            CREATE INDEX inventory_content ON inventory_origins(g,ckey,family);
            CREATE TABLE inventory_tags(g INTEGER NOT NULL,family TEXT NOT NULL,ordinal INTEGER NOT NULL,name TEXT NOT NULL,
                type INTEGER NOT NULL,bits BLOB NOT NULL,PRIMARY KEY(g,family,ordinal));
            CREATE TABLE inventory_logical(g INTEGER NOT NULL,ckey TEXT NOT NULL,bytes INTEGER NOT NULL,PRIMARY KEY(g,ckey));
            CREATE TABLE inventory_units(g INTEGER NOT NULL,ekey TEXT NOT NULL,bytes INTEGER NOT NULL,retained INTEGER NOT NULL DEFAULT 0,
                shared INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(g,ekey));
            CREATE TABLE inventory_available(g INTEGER NOT NULL,ekey TEXT NOT NULL,PRIMARY KEY(g,ekey));
            CREATE INDEX retained_unit_keys ON inventory_units(ekey,g);
            PRAGMA user_version=4;
            """, tx);
        tx.Commit();
    }
    internal Task<long> StartInventoryAsync(CapturedOpeningContext context, CancellationToken token) => Schedule(() =>
    {
        // Staging never becomes queryable on restart. Retained source objects can be reused by a fresh generation.
        Execute("INSERT INTO inventory_generations(build_id,region,state,profile,source) VALUES($b,$r,'staging',$p,$s)", null,
            ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$p", InventoryWorkspacePreparer.Profile), ("$s", Serialize(context.Reference)));
        return Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
    }, token);
    internal Task<ReadyInventory?> FindInventoryAsync(CapturedOpeningContext context, CancellationToken token) => Schedule(() =>
    {
        using var cmd = Command("SELECT id,summary,metadata FROM inventory_generations WHERE build_id=$b AND region=$r AND state='ready' AND profile=$p ORDER BY id DESC LIMIT 1", null,
            ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$p", InventoryWorkspacePreparer.Profile));
        using var reader = cmd.ExecuteReader(); if (!reader.Read()) return null;
        var id = reader.GetInt64(0); var summary = JsonSerializer.Deserialize<InventorySummary>(reader.GetString(1), Json)!;
        var metadata = JsonSerializer.Deserialize<List<MetadataReceipt>>(reader.GetString(2), Json)!;
        reader.Close();
        if (summary.Generation != id.ToString(CultureInfo.InvariantCulture) ||
            Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inventory_origins WHERE g=$g", null, ("$g", id))) != long.Parse(summary.MembershipCount, CultureInfo.InvariantCulture) ||
            Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inventory_units WHERE g=$g", null, ("$g", id))) != long.Parse(summary.DownloadObjectCount, CultureInfo.InvariantCulture))
            throw new DiscoveryException("CATALOG_INTEGRITY", "The saved inventory generation is incomplete.");
        return new ReadyInventory(id, summary, metadata);
    }, token);
    internal Task<MetadataReceipt?> FindMetadataAsync(string key, CancellationToken token) => Schedule(() =>
    {
        var value = Scalar("SELECT receipt FROM metadata_objects WHERE ekey=$e", null, ("$e", key));
        return value is null ? null : JsonSerializer.Deserialize<MetadataReceipt>((string)value, Json);
    }, token);
    internal Task RecordMetadataAsync(MetadataReceipt receipt, CancellationToken token) => Schedule(() =>
    {
        ValidateDownloadReceiptFacts(receipt);
        var saved = Scalar("SELECT receipt FROM metadata_objects WHERE ekey=$e", null, ("$e", receipt.EncodingKey));
        if (saved is not null)
        {
            var previous = JsonSerializer.Deserialize<MetadataReceipt>((string)saved, Json)!;
            ValidateDownloadReceiptFacts(previous);
            if (previous.ContentKey != receipt.ContentKey || previous.Sha256 != receipt.Sha256 || previous.EncodedBytes != receipt.EncodedBytes || previous.DecodedBytes != receipt.DecodedBytes)
                throw new DiscoveryException("METADATA_IDENTITY_CONFLICT", "Saved metadata conflicts with its exact encoded identity.");
        }
        // Callers record metadata only after verifying its current bytes. ROOT, for example, is a DL member.
        using var transaction = Db.BeginTransaction();
        if (saved is null)
        {
            Execute("INSERT INTO metadata_objects VALUES($e,$c,$n,$r)", transaction, ("$e", receipt.EncodingKey), ("$c", receipt.ContentKey),
                ("$n", long.Parse(receipt.EncodedBytes, CultureInfo.InvariantCulture)), ("$r", Serialize(receipt)));
            LogStoredChange(receipt.EncodingKey, transaction);
        }
        ClearObjectAbsence(receipt.EncodingKey, transaction);
        transaction.Commit();
        return true;
    }, token);
    internal Task AddEncodingAsync(long generation, IReadOnlyList<EncodingContentRow> content,
        IReadOnlyList<EncodingObjectRow> encodings, CancellationToken token) => Schedule(() =>
    {
        using var tx = Db.BeginTransaction();
        using var contents = Command("INSERT INTO en_content VALUES($g,$c,$n)", tx);
        using var candidates = Command("INSERT INTO en_candidates VALUES($g,$c,$o,$e)", tx);
        using var objects = Command("INSERT INTO en_objects VALUES($g,$e,$n)", tx);
        foreach (var row in content)
        {
            token.ThrowIfCancellationRequested();
            if(row.Candidates.Count>255)throw new DiscoveryException("INVENTORY_LIMIT","An encoding content record exceeds its supported candidate bound.");
            Insert(contents, ("$g", generation), ("$c", row.Key), ("$n", row.Bytes));
            for (var i = 0; i < row.Candidates.Count; i++) Insert(candidates, ("$g", generation), ("$c", row.Key), ("$o", i), ("$e", row.Candidates[i]));
        }
        foreach (var row in encodings) { token.ThrowIfCancellationRequested(); Insert(objects, ("$g", generation), ("$e", row.Key), ("$n", row.Bytes)); }
        tx.Commit(); return true;
    }, token);
    private static void Insert(Microsoft.Data.Sqlite.SqliteCommand command, params (string Name, object? Value)[] values)
    {
        if (command.Parameters.Count==0)
        {
            foreach (var value in values) command.Parameters.AddWithValue(value.Name,value.Value ?? DBNull.Value);
            command.Prepare();
        }
        else for (var i=0;i<values.Length;i++) command.Parameters[i].Value=values[i].Value ?? DBNull.Value;
        command.ExecuteNonQuery();
    }
    internal Task AddOriginsAsync(long generation, IReadOnlyList<InventoryOrigin> origins, CancellationToken token) => Schedule(() =>
    {
        using var tx = Db.BeginTransaction();
        using var cmd = Command("INSERT INTO inventory_origins(g,id,family,name,fdid,ckey,reference,width,declared_encoded,declared_decoded,locale,flags,name_hash,raw) VALUES($g,$i,$f,$n,$d,$c,$r,$w,$e,$b,$l,$a,$h,$raw)", tx);
        foreach (var o in origins)
        {
            token.ThrowIfCancellationRequested();
            Insert(cmd, ("$g", generation), ("$i", o.Id), ("$f", o.Family), ("$n", o.Name), ("$d", o.FileDataId), ("$c", o.ContentKey),
                ("$r", o.Reference), ("$w", o.ReferenceBytes), ("$e", o.DeclaredEncoded), ("$b", o.DeclaredDecoded), ("$l", o.LocaleFlags), ("$a", o.ContentFlags), ("$h", o.NameHash), ("$raw", o.Raw));
        }
        tx.Commit(); return true;
    }, token);
    internal Task AddTagAsync(long generation, string family, int ordinal, string name, ushort type, ReadOnlyMemory<byte> bits, CancellationToken token) => Schedule(() =>
    {
        Execute("INSERT INTO inventory_tags VALUES($g,$f,$o,$n,$t,$b)", null, ("$g", generation), ("$f", family), ("$o", ordinal), ("$n", name), ("$t", type), ("$b", bits.ToArray())); return true;
    }, token);
    internal Task<(long Decoded, IReadOnlyList<InventoryCandidate> Candidates)> ResolveContentAsync(long generation, string contentKey, CancellationToken token) => Schedule(() =>
    {
        var bytes = Scalar("SELECT bytes FROM en_content WHERE g=$g AND ckey=$c", null, ("$g", generation), ("$c", contentKey));
        if (bytes is null) throw new DiscoveryException("INVENTORY_UNRESOLVED", "A required content identity is absent from the verified encoding table.");
        var candidates = new List<InventoryCandidate>();
        using var cmd = Command("SELECT e.ekey,e.bytes FROM en_candidates c JOIN en_objects e ON e.g=c.g AND e.ekey=c.ekey WHERE c.g=$g AND c.ckey=$c AND c.ordinal BETWEEN 0 AND 254 ORDER BY e.ekey LIMIT 256", null, ("$g", generation), ("$c", contentKey));
        using var reader = cmd.ExecuteReader(); while (reader.Read()) candidates.Add(new(reader.GetString(0), reader.GetInt64(1)));
        if (candidates.Count is 0 or > 255) throw new DiscoveryException("INVENTORY_UNRESOLVED", "A required content identity has no bounded complete encoding.");
        return (Convert.ToInt64(bytes), (IReadOnlyList<InventoryCandidate>)candidates);
    }, token);
    internal async Task<int> ResolveOriginsAsync(long generation, string family, long after, CancellationToken token) =>
        (await ResolveOriginBatchAsync(generation, family, after, token)).Count;
    internal Task<(long After, long Completed)> ResolutionPositionAsync(long generation, string family, CancellationToken token) => Schedule(() =>
    {
        RequireStagingInventory(generation);
        if (family is not ("root" or "install")) throw new ArgumentException("Expected a content family.", nameof(family));
        long after = 0, completed = 0;
        using (var cmd = Command("SELECT after,completed FROM inventory_resolution_cursor WHERE g=$g AND family=$f", null, ("$g", generation), ("$f", family)))
        using (var reader = cmd.ExecuteReader()) if (reader.Read()) { after = reader.GetInt64(0); completed = reader.GetInt64(1); }
        using var check = Command("SELECT count(*),coalesce(max(id),0),coalesce(sum(CASE WHEN id>$a OR encoded IS NULL OR decoded IS NULL THEN 1 ELSE 0 END),0) FROM inventory_origins WHERE g=$g AND family=$f AND ekey IS NOT NULL", null,
            ("$g", generation), ("$f", family), ("$a", after));
        using var rows = check.ExecuteReader(); rows.Read();
        var count = rows.GetInt64(0); var last = rows.GetInt64(1); var invalid = rows.GetInt64(2); rows.Close();
        if (after < 0 || completed < 0 || count != completed || last != after || invalid != 0 ||
            Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM inventory_origins WHERE g=$g AND family=$f AND id<=$a AND ekey IS NULL)", null, ("$g", generation), ("$f", family), ("$a", after))) != 0)
            throw new DiscoveryException("CHECKPOINT_INTEGRITY", "The saved file-resolution position is inconsistent with its committed rows.");
        return (after, completed);
    }, token);
    internal Task<(int Count, long After)> ResolveOriginBatchAsync(long generation, string family, long after, CancellationToken token) => Schedule(() =>
    {
        if(family is not ("root" or "install")) throw new ArgumentException("Encoded families use the ordered resolver.",nameof(family));
        RequireStagingInventory(generation);
        var rows=new List<(long Id,string CKey,long? Declared)>();
        using(var cmd=Command("SELECT id,ckey,declared_decoded FROM inventory_origins WHERE g=$g AND family=$f AND id>$a ORDER BY id LIMIT 256",null,
            ("$g",generation),("$f",family),("$a",after)))
        using(var reader=cmd.ExecuteReader())while(reader.Read())rows.Add((reader.GetInt64(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetInt64(2)));
        using var tx=Db.BeginTransaction();
        long completed = 0, previous = 0;
        using (var cursor = Command("SELECT after,completed FROM inventory_resolution_cursor WHERE g=$g AND family=$f", tx, ("$g", generation), ("$f", family)))
        using (var reader = cursor.ExecuteReader()) if (reader.Read()) { previous = reader.GetInt64(0); completed = reader.GetInt64(1); }
        if (previous != after) throw new DiscoveryException("CHECKPOINT_INTEGRITY", "File resolution must continue from its committed position.");
        using var content=Command("SELECT bytes FROM en_content WHERE g=$g AND ckey=$c",tx,("$g",generation),("$c",""));
        using var candidate=Command(RootCandidateSql,tx,("$g",generation),("$c",""));
        using var logical=Command("INSERT OR IGNORE INTO inventory_logical VALUES($g,$c,$n)",tx,("$g",generation),("$c",""),("$n",0L));
        using var unit=Command("INSERT INTO inventory_units(g,ekey,bytes) VALUES($g,$e,$n) ON CONFLICT(g,ekey) DO UPDATE SET bytes=inventory_units.bytes WHERE inventory_units.bytes=excluded.bytes",tx,("$g",generation),("$e",""),("$n",0L));
        using var update=Command("UPDATE inventory_origins SET ekey=$e,encoded=$n,decoded=$d WHERE g=$g AND id=$i",tx,("$e",""),("$n",0L),("$d",0L),("$g",generation),("$i",0L));
        content.Prepare();candidate.Prepare();logical.Prepare();unit.Prepare();update.Prepare();
        foreach(var row in rows)
        {
            token.ThrowIfCancellationRequested();content.Parameters[1].Value=row.CKey;
            var value=content.ExecuteScalar();
            if(value is null)throw new DiscoveryException("INVENTORY_UNRESOLVED","A published content reference has no encoding mapping.");
            var decoded=Convert.ToInt64(value);
            if(row.Declared is not null && row.Declared!=decoded)throw new DiscoveryException("INVENTORY_SIZE_CONFLICT","Install and encoding decoded lengths disagree.");
            candidate.Parameters[1].Value=row.CKey;
            string key;long encoded;
            using(var reader=candidate.ExecuteReader())
            {
                if(!reader.Read())throw new DiscoveryException("INVENTORY_UNRESOLVED","A published content reference has no complete encoded representation.");
                key=reader.GetString(0);encoded=reader.GetInt64(1);
            }
            logical.Parameters[1].Value=row.CKey;logical.Parameters[2].Value=decoded;logical.ExecuteNonQuery();
            unit.Parameters[1].Value=key;unit.Parameters[2].Value=encoded;
            if(unit.ExecuteNonQuery()!=1)throw new DiscoveryException("INVENTORY_SIZE_CONFLICT","One exact encoded object has conflicting published sizes.");
            update.Parameters[0].Value=key;update.Parameters[1].Value=encoded;update.Parameters[2].Value=decoded;update.Parameters[4].Value=row.Id;
            if(update.ExecuteNonQuery()!=1)throw new DiscoveryException("CATALOG_INTEGRITY","A content membership changed during resolution.");
        }
        var next = rows.Count == 0 ? after : rows[^1].Id;
        Execute("INSERT INTO inventory_resolution_cursor(g,family,after,completed) VALUES($g,$f,$a,$n) ON CONFLICT(g,family) DO UPDATE SET after=$a,completed=$n", tx,
            ("$g", generation), ("$f", family), ("$a", next), ("$n", checked(completed + rows.Count)));
        token.ThrowIfCancellationRequested();tx.Commit();return (rows.Count,next);
    }, token);
    internal Task<IReadOnlyList<(string Key, MetadataReceipt Receipt)>> AvailableCandidatesAsync(long generation, CancellationToken token) => Schedule(() =>
    {
        var values = new List<(string, MetadataReceipt)>();
        using var cmd = Command("SELECT m.ekey,m.receipt FROM metadata_objects m WHERE EXISTS(SELECT 1 FROM en_candidates c JOIN inventory_origins o ON o.g=c.g AND o.ckey=c.ckey WHERE c.g=$g AND c.ekey=m.ekey AND o.family IN ('root','install')) OR EXISTS(SELECT 1 FROM inventory_units u WHERE u.g=$g AND u.ekey=m.ekey) LIMIT 10001", null, ("$g", generation));
        using var r = cmd.ExecuteReader(); while (r.Read()) values.Add((r.GetString(0), JsonSerializer.Deserialize<MetadataReceipt>(r.GetString(1), Json)!));
        if (values.Count > 10000) throw new DiscoveryException("INVENTORY_LIMIT", "Saved-object assessment exceeds its bound.");
        return (IReadOnlyList<(string Key, MetadataReceipt Receipt)>)values;
    }, token);
    internal Task SetAvailableAsync(long generation, IReadOnlyList<string> keys, CancellationToken token) => Schedule(() =>
    {
        using var tx = Db.BeginTransaction(); Execute("DELETE FROM inventory_available WHERE g=$g", tx, ("$g", generation));
        foreach (var key in keys) { token.ThrowIfCancellationRequested(); Execute("INSERT INTO inventory_available VALUES($g,$e)", tx, ("$g", generation), ("$e", key)); }
        tx.Commit(); return true;
    }, token);
    internal Task ValidateCandidateContentAsync(long generation, MetadataReceipt receipt, CancellationToken token) => Schedule(() =>
    {
        var conflict = Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM en_candidates c JOIN en_content e ON e.g=c.g AND e.ckey=c.ckey JOIN inventory_origins o ON o.g=c.g AND o.ckey=c.ckey WHERE c.g=$g AND c.ekey=$key AND o.family IN ('root','install') AND (e.ckey != $ckey OR e.bytes != $bytes))", null,
            ("$g", generation), ("$key", receipt.EncodingKey), ("$ckey", receipt.ContentKey), ("$bytes", long.Parse(receipt.DecodedBytes, CultureInfo.InvariantCulture))));
        if (conflict != 0) throw new DiscoveryException("INVENTORY_MAPPING_CONFLICT", "A saved encoded object proves different content than a required ROOT/install mapping.");
        return true;
    }, token);
    internal Task<long?> CandidateLengthAsync(long generation, string key, CancellationToken token) => Schedule(() =>
    {
        var value = Scalar("SELECT bytes FROM inventory_units WHERE g=$g AND ekey=$e UNION SELECT bytes FROM en_objects WHERE g=$g AND ekey=$e LIMIT 1", null, ("$g", generation), ("$e", key));
        return value is null ? (long?)null : Convert.ToInt64(value);
    }, token);
    internal Task AbandonInventoryAsync(long generation) => Schedule(() =>
    { Execute("UPDATE inventory_generations SET state='abandoned' WHERE id=$g AND state='staging'", null, ("$g", generation)); return true; }, CancellationToken.None);
    internal Task<string?> InventoryDigestAsync(long generation, CancellationToken token) => Schedule(() =>
        (string?)Scalar("SELECT digest FROM inventory_generations WHERE id=$g", null, ("$g", generation)), token);
    internal Task StoreInventoryDigestAsync(long generation, string digest, CancellationToken token) => Schedule(() =>
    { Execute("UPDATE inventory_generations SET digest=$d WHERE id=$g AND state='staging'", null, ("$g", generation), ("$d", digest)); return true; }, token);
    internal Task<IReadOnlyList<(string Key, MetadataReceipt Receipt)>> RetainedCandidatesAsync(long generation, CancellationToken token) => Schedule(() =>
    {
        var values = new List<(string, MetadataReceipt)>();
        using var cmd = Command("SELECT u.ekey,m.receipt FROM inventory_units u JOIN metadata_objects m ON m.ekey=u.ekey WHERE u.g=$g LIMIT 10001", null, ("$g", generation));
        using var r = cmd.ExecuteReader(); while (r.Read()) values.Add((r.GetString(0), JsonSerializer.Deserialize<MetadataReceipt>(r.GetString(1), Json)!));
        if (values.Count > 10000) throw new DiscoveryException("INVENTORY_LIMIT", "Saved-object verification exceeds its current assessment bound.");
        return (IReadOnlyList<(string Key, MetadataReceipt Receipt)>)values;
    }, token);
    internal Task<WorkspaceSnapshot> SealInventoryAsync(long generation, CapturedOpeningContext context,
        IReadOnlyList<ConfigurationReceipt> configurations, IReadOnlyList<MetadataReceipt> metadata, IReadOnlyList<string> verifiedKeys, CancellationToken token) => Schedule(() =>
    {
        using var tx = Db.BeginTransaction();
        if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM inventory_origins WHERE g=$g AND ekey IS NULL", tx, ("$g", generation))) != 0)
            throw new DiscoveryException("INVENTORY_UNRESOLVED", "The published inventory is incomplete.");
        string[] inconsistencies =
        [
            "SELECT EXISTS(SELECT 1 FROM inventory_origins o LEFT JOIN inventory_units u ON u.g=o.g AND u.ekey=o.ekey WHERE o.g=$g AND (u.ekey IS NULL OR o.encoded IS NULL OR u.bytes!=o.encoded))",
            "SELECT EXISTS(SELECT 1 FROM inventory_origins o LEFT JOIN en_content c ON c.g=o.g AND c.ckey=o.ckey LEFT JOIN inventory_logical l ON l.g=o.g AND l.ckey=o.ckey LEFT JOIN en_objects e ON e.g=o.g AND e.ekey=o.ekey WHERE o.g=$g AND o.family IN ('root','install') AND (c.ckey IS NULL OR l.ckey IS NULL OR e.ekey IS NULL OR o.encoded!=e.bytes OR o.decoded IS NULL OR o.decoded!=c.bytes OR l.bytes!=c.bytes OR NOT EXISTS(SELECT 1 FROM en_candidates e WHERE e.g=o.g AND e.ckey=o.ckey AND e.ordinal BETWEEN 0 AND 254 AND e.ekey=o.ekey)))",
            "SELECT EXISTS(SELECT 1 FROM inventory_origins WHERE g=$g AND family IN ('download','size') AND (encoded!=declared_encoded OR declared_encoded IS NULL OR width NOT IN (9,16) OR (family='download' AND width!=16) OR length(reference)!=width*2 OR length(ekey)!=32 OR substr(ekey,1,width*2)!=reference))",
            "SELECT EXISTS(SELECT 1 FROM inventory_origins o WHERE o.g=$g AND o.family='size' AND o.width=9 AND NOT EXISTS(SELECT 1 FROM en_objects e WHERE e.g=o.g AND e.ekey=o.ekey) AND NOT EXISTS(SELECT 1 FROM inventory_origins d WHERE d.g=o.g AND d.family='download' AND d.reference=o.ekey AND d.width=16))",
            "SELECT EXISTS(SELECT ekey FROM inventory_units WHERE g=$g EXCEPT SELECT ekey FROM inventory_origins WHERE g=$g)",
            "SELECT EXISTS(SELECT 1 FROM inventory_logical l WHERE l.g=$g AND NOT EXISTS(SELECT 1 FROM inventory_origins o WHERE o.g=l.g AND o.ckey=l.ckey AND o.family IN ('root','install')))"
        ];
        foreach (var query in inconsistencies)
        {
            token.ThrowIfCancellationRequested();
            if (Convert.ToInt64(Scalar(query, tx, ("$g", generation))) != 0)
                throw new DiscoveryException("CHECKPOINT_INTEGRITY", "The completed file mappings and download ledgers do not agree.");
        }
        Execute("UPDATE inventory_units SET retained=0,shared=0 WHERE g=$g AND retained=1", tx, ("$g", generation));
        foreach (var key in verifiedKeys)
        {
            token.ThrowIfCancellationRequested();
            Execute("UPDATE inventory_units SET retained=1,shared=EXISTS(SELECT 1 FROM inventory_units x JOIN inventory_generations y ON y.id=x.g JOIN inventory_generations z ON z.id=$g WHERE x.ekey=$e AND y.state='ready' AND y.build_id != z.build_id) WHERE g=$g AND ekey=$e", tx, ("$g", generation), ("$e", key));
        }
        long Sum(string table, string column, string extra = "") => Convert.ToInt64(Scalar($"SELECT COALESCE(SUM({column}),0) FROM {table} WHERE g=$g {extra}", tx, ("$g", generation)));
        long Count(string table, string extra = "") => Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table} WHERE g=$g {extra}", tx, ("$g", generation)));
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        var total = Sum("inventory_units", "bytes"); var retained = Sum("inventory_units", "bytes", "AND retained=1");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var summary = new InventorySummary(N(generation), now, "allPublishedVariants", N(Count("inventory_origins")), N(Count("inventory_origins", "AND family='root'")),
            N(Count("inventory_logical")), N(Count("inventory_units")), N(Sum("inventory_logical", "bytes")), N(total), N(retained), N(checked(total-retained)),
            N(Sum("inventory_units", "bytes", "AND retained=1 AND shared=1")), N(checked(metadata.Select(m => (m.Sha256, Bytes: long.Parse(m.EncodedBytes, CultureInfo.InvariantCulture))).Distinct().Sum(m => m.Bytes) + configurations.Sum(c => (long)c.ByteLength))));
        Execute("UPDATE inventory_generations SET state='ready',summary=$s,metadata=$m WHERE id=$g AND build_id=$b AND region=$r", tx,
            ("$g", generation), ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$s", Serialize(summary)), ("$m", Serialize(metadata)));
        // The previous configuration-only acknowledgement is insufficient: this write occurs only after full inventory seal.
        Execute("INSERT INTO workspace_admissions(product,build_id,region,profile,source_reference,opened_at) VALUES($p,$b,$r,$profile,$s,$t)", tx,
            ("$p", context.Build.ProductId), ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$profile", InventoryWorkspacePreparer.Profile), ("$s", Serialize(context.Reference)), ("$t", now));
        var admission = Convert.ToInt64(Scalar("SELECT last_insert_rowid()", tx));
        foreach (var c in configurations) Execute("INSERT INTO workspace_configurations VALUES($a,$r,$k,$s,$n,$u,$t,$v,$f)", tx,
            ("$a", admission), ("$r", c.Role), ("$k", c.Key), ("$s", c.Sha256), ("$n", c.ByteLength), ("$u", c.SourceUrl), ("$t", c.RetrievedAt), ("$v", c.VerifiedAt), ("$f", c.ParsedFieldCount));
        Execute("INSERT INTO open_acknowledgements VALUES($b,$t,$a) ON CONFLICT(build_id) DO UPDATE SET opened_at=$t,admission_id=$a", tx, ("$b", context.Build.Id), ("$t", now), ("$a", admission));
        token.ThrowIfCancellationRequested(); tx.Commit();
        var build = context.Build with { IsNew = false, IsLocal = true };
        return new WorkspaceSnapshot(build, now, InventoryWorkspacePreparer.Profile, "partial", "verified", "ready", "measured", "onDemand", null, null, context.Reference, configurations, summary, metadata);
    }, token);
    public Task<InventoryPage> ReadInventoryAsync(InventoryReadRequest request, CancellationToken token) => Schedule(() =>
    {
        InventoryService.Validate(request); var g = long.Parse(request.Generation, CultureInfo.InvariantCulture);
        var total = Scalar("SELECT summary FROM inventory_generations WHERE id=$g AND build_id=$b AND region=$r AND state='ready' AND profile=$p", null,
            ("$g", g), ("$b", request.BuildIdentity), ("$r", request.SourceRegion), ("$p", InventoryWorkspacePreparer.Profile));
        var product = Scalar("SELECT product FROM builds WHERE id=$b", null, ("$b", request.BuildIdentity));
        if (total is null || (string?)product != request.ProductId) throw new DiscoveryException("INVENTORY_NOT_FOUND", "This exact prepared inventory is unavailable. Open the build again.", true);
        var summary = JsonSerializer.Deserialize<InventorySummary>((string)total, Json)!;
        var entries = new List<InventoryEntry>(); var more = false; var bytes = 0;
        using var cmd = Command("SELECT o.id,o.family,o.name,o.fdid,o.ckey,o.ekey,o.decoded,o.encoded,u.retained,o.locale,o.flags,o.name_hash FROM inventory_origins o JOIN inventory_units u ON u.g=o.g AND u.ekey=o.ekey WHERE o.g=$g AND o.id>$a ORDER BY o.id LIMIT $n", null,
            ("$g", g), ("$a", request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture)), ("$n", request.Limit+1));
        using var r = cmd.ExecuteReader();
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var entry = new InventoryEntry(r.GetInt64(0).ToString(CultureInfo.InvariantCulture), r.GetString(1), Text(2), Text(3), Text(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6).ToString(CultureInfo.InvariantCulture), r.GetInt64(7).ToString(CultureInfo.InvariantCulture), r.GetBoolean(8), Text(9), Text(10), Text(11));
            var size = JsonSerializer.SerializeToUtf8Bytes(entry, Json).Length + 2;
            if (entries.Count == request.Limit || bytes + size > 750_000) { more = true; break; }
            entries.Add(entry); bytes += size;
        }
        return new InventoryPage(request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation, entries, summary.MembershipCount, more ? entries[^1].Id : null);
    }, token);
}
