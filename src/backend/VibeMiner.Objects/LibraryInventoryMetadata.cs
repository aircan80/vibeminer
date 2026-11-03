using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

internal static class LibraryInventoryMetadata
{
    internal const string Schema = """
        CREATE TABLE library_inventory_generation(id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner TEXT NOT NULL, scope INTEGER NOT NULL CHECK(scope IN (0,1)), producer TEXT NOT NULL,
            role INTEGER NOT NULL CHECK(role IN (0,1)), state INTEGER NOT NULL CHECK(state IN (0,1,2)),
            key_count INTEGER NOT NULL DEFAULT 0 CHECK(key_count>=0), key_digest INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX library_inventory_generation_identity ON library_inventory_generation(owner,scope,producer,role,id);
        CREATE TABLE library_inventory_identity(owner TEXT NOT NULL, scope INTEGER NOT NULL CHECK(scope IN (0,1)),
            producer TEXT NOT NULL, role INTEGER NOT NULL CHECK(role IN (0,1)), latest INTEGER NOT NULL,
            active INTEGER, PRIMARY KEY(owner,scope,producer,role)) WITHOUT ROWID;
        CREATE TABLE library_inventory_key(generation INTEGER NOT NULL REFERENCES library_inventory_generation(id),
            k BLOB NOT NULL CHECK(length(k)=18), PRIMARY KEY(generation,k)) WITHOUT ROWID;
        """;

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, LibraryClaim claim, string sql)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$o", claim.Owner); cmd.Parameters.AddWithValue("$s", (int)claim.Scope);
        cmd.Parameters.AddWithValue("$p", claim.Producer); cmd.Parameters.AddWithValue("$r", (int)claim.Role);
        return cmd;
    }

    internal static long Version(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT version FROM library_format WHERE id=1";
        return cmd.ExecuteScalar() is long v ? v : throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The authoritative library format is missing; preserve the library for repair.");
    }

    internal static void Require(SqliteConnection c, SqliteTransaction? tx = null)
    {
        var version = Version(c, tx);
        if (version == 1) throw new ObjectStoreException(ObjectStoreCodes.InventoryUpgradeRequired,
            "Expected inventories require an explicit metadata upgrade. Reopen the library with UpgradeLibraryInventory=true; preserve its index and packs.");
        if (version != 2) throw new ObjectStoreException(version > 2 ? ObjectStoreCodes.NewerFormat : ObjectStoreCodes.IndexUnreadable,
            "The authoritative inventory format is unsupported; preserve the library.");
    }

    internal static long? Active(SqliteConnection c, SqliteTransaction tx, LibraryClaim claim)
    {
        using var cmd = Command(c, tx, claim, "SELECT active FROM library_inventory_identity WHERE owner=$o AND scope=$s AND producer=$p AND role=$r");
        var active = cmd.ExecuteScalar();
        if (active is null or DBNull) return null;
        if (active is long id && id > 0) return id;
        throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable,
            "The authoritative inventory's active generation is malformed; preserve the library for repair.");
    }

    private static void CheckTotals(long count, long digest, long recordedCount, long recordedDigest)
    {
        if (count != recordedCount || digest != recordedDigest)
            throw new ArgumentException("Inventory expected keys do not match their committed count and digest.");
    }

    internal static void Validate(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        // Cap identity text/key allocations before reading arbitrary metadata into managed memory.
        foreach (var table in new[] { "library_inventory_identity", "library_inventory_generation" })
        {
            using var bounds = c.CreateCommand(); bounds.Transaction = tx;
            bounds.CommandText = $"""
                SELECT EXISTS(SELECT 1 FROM {table} WHERE typeof(owner)!='text' OR
                length(CAST(owner AS BLOB)) NOT BETWEEN 1 AND 512 OR instr(owner,char(0))!=0 OR
                typeof(producer)!='text' OR length(CAST(producer AS BLOB)) NOT BETWEEN 1 AND 128 OR instr(producer,char(0))!=0)
                """;
            if ((long)bounds.ExecuteScalar()! != 0) throw new ArgumentException("Malformed inventory identities.");
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = $"SELECT owner,scope,producer,role FROM {table}";
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.GetValue(0) is not string owner || rows.GetValue(1) is not long scope || scope is < 0 or > 1 ||
                    rows.GetValue(2) is not string producer || rows.GetValue(3) is not long role || role is < 0 or > 1)
                    throw new ArgumentException("Malformed inventory identity.");
                _ = new LibraryClaim(owner, (LibraryOwnerScope)scope, producer, (LibraryObjectRole)role);
            }
        }
        using (var bounds = c.CreateCommand())
        {
            bounds.Transaction = tx;
            bounds.CommandText = "SELECT EXISTS(SELECT 1 FROM library_inventory_key WHERE typeof(k)!='blob' OR length(k)!=18 OR typeof(generation)!='integer' OR generation<=0)";
            if ((long)bounds.ExecuteScalar()! != 0) throw new ArgumentException("Malformed inventory key.");
        }
        using (var cmd = c.CreateCommand())
        {
            // One ordered stream covers both populated and empty generations with constant managed memory.
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT g.id,g.key_count,g.key_digest,k.k FROM library_inventory_generation g
                LEFT JOIN library_inventory_key k ON k.generation=g.id ORDER BY g.id,k.k
                """;
            using var rows = cmd.ExecuteReader();
            long previousGeneration = 0, recordedCount = 0, recordedDigest = 0, count = 0, digest = 0;
            ObjectKey previous = default;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (rows.GetValue(0) is not long generation || generation <= 0)
                    throw new ArgumentException("Malformed inventory generation.");
                if (generation != previousGeneration)
                {
                    if (previousGeneration != 0) CheckTotals(count, digest, recordedCount, recordedDigest);
                    if (rows.GetValue(1) is not long expectedCount || expectedCount < 0 || rows.GetValue(2) is not long expectedDigest)
                        throw new ArgumentException("Malformed inventory key totals.");
                    recordedCount = expectedCount; recordedDigest = expectedDigest; count = 0; digest = 0;
                    previousGeneration = generation;
                }
                if (rows.IsDBNull(3)) continue;
                var bytes = rows.GetFieldValue<byte[]>(3); var key = ObjectKey.FromIndexKey(bytes);
                LibraryInventory.ValidateKey(key);
                if (count > 0 && key.CompareTo(previous) <= 0) throw new ArgumentException("Duplicate inventory keys.");
                count = checked(count + 1); digest ^= ObjectIndex.InventoryKeyDigest(bytes); previous = key;
            }
            if (previousGeneration != 0) CheckTotals(count, digest, recordedCount, recordedDigest);
        }
        using var coverage = c.CreateCommand(); coverage.Transaction = tx;
        coverage.CommandText = """
            SELECT EXISTS(SELECT 1 FROM library_inventory_generation WHERE typeof(id)!='integer' OR id<=0 OR typeof(state)!='integer' OR state NOT IN (0,1,2))
            OR EXISTS(SELECT 1 FROM library_inventory_key k WHERE NOT EXISTS(SELECT 1 FROM library_inventory_generation g WHERE g.id=k.generation))
            OR EXISTS(SELECT 1 FROM library_inventory_identity i WHERE typeof(latest)!='integer' OR latest<=0 OR
                (active IS NOT NULL AND (typeof(active)!='integer' OR active<=0)) OR NOT EXISTS(
                SELECT 1 FROM library_inventory_generation g INDEXED BY library_inventory_generation_identity WHERE g.id=i.latest
                AND g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role)
                OR EXISTS(SELECT 1 FROM library_inventory_generation g INDEXED BY library_inventory_generation_identity
                    WHERE g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role AND g.id>i.latest)
                OR (active IS NOT NULL AND NOT EXISTS(SELECT 1 FROM library_inventory_generation g WHERE g.id=i.active AND g.state=1
                    AND g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role)))
            OR EXISTS(SELECT 1 FROM library_inventory_identity i WHERE EXISTS(
                SELECT 1 FROM library_inventory_generation g INDEXED BY library_inventory_generation_identity
                WHERE g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role
                    AND g.state=1 AND (i.active IS NULL OR g.id>i.active)))
            OR EXISTS(SELECT 1 FROM library_inventory_generation g WHERE NOT EXISTS(SELECT 1 FROM library_inventory_identity i
                WHERE g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role))
            OR EXISTS(SELECT 1 FROM library_inventory_identity GROUP BY owner,scope,producer,role HAVING COUNT(*)!=1)
            OR EXISTS(SELECT 1 FROM library_inventory_generation GROUP BY id HAVING COUNT(*)!=1)
            """;
        if ((long)coverage.ExecuteScalar()! != 0) throw new ArgumentException("Inventory generations, keys and active identities disagree.");
        // AUTOINCREMENT is authoritative: a malformed sequence could reuse a retained token.
        using var sequence = c.CreateCommand(); sequence.Transaction = tx;
        sequence.CommandText = """
            SELECT (SELECT COUNT(*) FROM library_inventory_generation)>0 AND NOT EXISTS(
                SELECT 1 FROM sqlite_sequence WHERE name='library_inventory_generation' AND typeof(seq)='integer'
                AND seq>=(SELECT COALESCE(MAX(id),0) FROM library_inventory_generation))
            """;
        if ((long)sequence.ExecuteScalar()! != 0) throw new ArgumentException("Inventory generation sequence is missing or stale.");
    }
}
