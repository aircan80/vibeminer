using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VibeMiner.Builds;

public sealed partial class BuildRegistry
{
    private const string Schema = """
        CREATE TABLE registry(id INTEGER PRIMARY KEY CHECK(id=1),registry_id TEXT NOT NULL,entry_count INTEGER NOT NULL,digest BLOB NOT NULL);
        CREATE TABLE builds(id TEXT PRIMARY KEY,data TEXT NOT NULL,sha256 BLOB NOT NULL) WITHOUT ROWID;
        """;
    private const string Columns = """
        id,data,sha256,
        typeof(id)='text' AND length(CAST(id AS BLOB))=64,
        typeof(data)='text' AND length(CAST(data AS BLOB)) BETWEEN 1 AND 524288,
        typeof(sha256)='blob' AND length(sha256)=32
        """;
    private static SqliteConnection Connect(string path, SqliteOpenMode mode)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 0 }.ToString());
        c.Open(); return c;
    }
    private static object? Scalar(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; return cmd.ExecuteScalar(); }
    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    private static BuildRegistry OpenCore(string directory, BuildRegistryOptions options, CancellationToken ct)
    {
        FileStream? held = null; SqliteConnection? c = null;
        var operational = false;
        try
        {
            Directory.CreateDirectory(directory);
            try { held = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) { throw new BuildRegistryException(BuildRegistryCodes.Busy, "Another registry writer holds the supplied directory.", e); }
            var path = Path.Combine(directory, FileName); var marker = Path.Combine(directory, MarkerName);
            var exists = File.Exists(path); var marked = File.Exists(marker);
            string id;
            if (exists || marked)
            {
                if (!exists || !marked || new FileInfo(marker).Length != 32) throw Unreadable("The registry initialization is incomplete or its marker is missing.");
                id = Encoding.ASCII.GetString(File.ReadAllBytes(marker));
                if (!Hex32(id)) throw Unreadable("The registry marker is malformed.");
                // Refuse before a writable SQLite open, journal changes, interruption recovery or any metadata mutation.
                using var probe = Connect(path, SqliteOpenMode.ReadOnly);
                ValidateDatabase(probe, id, ct);
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any(p => Path.GetFileName(p) != "writer.lock")) throw Unreadable("The registry directory is not empty.");
                ct.ThrowIfCancellationRequested(); id = Guid.NewGuid().ToString("N");
                c = Connect(path, SqliteOpenMode.ReadWriteCreate);
                Exec(c, "PRAGMA synchronous=FULL");
                using (var tx = c.BeginTransaction())
                {
                    Exec(c, Schema, tx);
                    Exec(c, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion}", tx);
                    using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO registry VALUES(1,$id,0,$digest)";
                    cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$digest", new byte[32]); cmd.ExecuteNonQuery(); tx.Commit();
                }
                using var mark = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                mark.Write(Encoding.ASCII.GetBytes(id)); mark.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            c ??= Connect(path, SqliteOpenMode.ReadWrite);
            ValidateDatabase(c, id, ct);
            operational = true;
            SyncRegistryDirectories(directory, options, ct);
            Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON");
            var registry = new BuildRegistry(c, held, options, id);
            // Paging avoids retaining all abandoned attempts. Nothing is exposed until every Loading row is interrupted.
            string? after = null;
            while (true)
            {
                var page = registry.Page(128, after, ct);
                foreach (var stored in page.Values)
                {
                    after = stored.Entry.Identity.Id;
                    if (stored.Entry.State != BuildLoadState.Loading) continue;
                    var now = options.Time.GetUtcNow();
                    registry.Save(stored with { Entry = stored.Entry with { State = BuildLoadState.Interrupted, UpdatedAt = now,
                        FinishedAt = now, Diagnostic = new("REGISTRY_RESTART", "The previous registry writer ended before this attempt completed.") } }, ct);
                    options.Fault?.Invoke(BuildRegistryPoint.RecoveryInterrupted);
                }
                if (!page.More) break;
            }
            registry.loop = Task.Run(registry.RunAsync); c = null; held = null;
            return registry;
        }
        catch (OperationCanceledException) { throw; }
        catch (BuildRegistryException) { throw; }
        catch (Exception e) when (operational && e is IOException or UnauthorizedAccessException or SqliteException)
        { throw Map(e); }
        catch (Exception e) { throw Unreadable("The authoritative build registry cannot be opened.", e); }
        finally { c?.Dispose(); held?.Dispose(); }
    }

    private static void SyncRegistryDirectories(string directory, BuildRegistryOptions options, CancellationToken ct)
    {
        // Always repeat these barriers, including after a failed initialization that left a valid marker. A marker's presence
        // alone does not prove its containing directory or a newly created ancestor survived a durable directory sync.
        ct.ThrowIfCancellationRequested();
        options.Fault?.Invoke(BuildRegistryPoint.BeforeDirectorySync);
        ct.ThrowIfCancellationRequested();
        RegistryDirectorySync.Sync(directory);
        options.Fault?.Invoke(BuildRegistryPoint.DirectorySynced);
        for (var parent = Directory.GetParent(directory); parent is not null; parent = parent.Parent)
        {
            ct.ThrowIfCancellationRequested();
            options.Fault?.Invoke(BuildRegistryPoint.BeforeAncestorSync);
            ct.ThrowIfCancellationRequested();
            RegistryDirectorySync.Sync(parent.FullName);
            options.Fault?.Invoke(BuildRegistryPoint.AncestorSynced);
        }
        ct.ThrowIfCancellationRequested();
        options.Fault?.Invoke(BuildRegistryPoint.InitializationDurable);
        ct.ThrowIfCancellationRequested();
    }

    private static (string Id, long Count, byte[] Digest) Metadata(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT registry_id,CASE WHEN typeof(entry_count)='integer' THEN entry_count ELSE NULL END,digest,typeof(registry_id)='text' AND length(CAST(registry_id AS BLOB))=32,
                typeof(digest)='blob' AND length(digest)=32 FROM registry WHERE id=1
            """;
        using var row = cmd.ExecuteReader();
        if (!row.Read() || row.GetValue(3) is not long s || s != 1 || row.GetValue(4) is not long d || d != 1 ||
            row.GetValue(1) is not long count || count < 0) throw Unreadable("The registry header is malformed.");
        var id = row.GetString(0); if (!Hex32(id)) throw Unreadable("The registry identity is malformed.");
        return (id, count, row.GetFieldValue<byte[]>(2));
    }
    private static void ValidateDatabase(SqliteConnection c, string expectedId, CancellationToken ct)
    {
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(c.Handle));
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!Equals(Scalar(c, "PRAGMA quick_check"), "ok")) throw Unreadable("SQLite integrity checks failed.");
            if (!Equals(Scalar(c, "PRAGMA encoding"), "UTF-8")) throw Unreadable("The registry SQLite encoding is foreign.");
            if (Scalar(c, "PRAGMA application_id") is not long app || app != ApplicationId) throw Unreadable("The file is not a build registry.");
            var foundVersion = Scalar(c, "PRAGMA user_version");
            if (foundVersion is not long version || version != SchemaVersion)
                throw new BuildRegistryException(foundVersion is long newer && newer > SchemaVersion ? BuildRegistryCodes.Newer : BuildRegistryCodes.Unreadable, "The registry format is unsupported. Preserve its files.");
            if (Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") is not long tables || tables != 2 ||
                Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('registry','builds')") is not long named || named != 2)
                throw Unreadable("The registry schema is incomplete or foreign.");
            if (Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type!='table'") is not long extras || extras != 0)
                throw Unreadable("The registry has unexpected schema objects.");
            foreach (var ddl in Schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var table = ddl.StartsWith("CREATE TABLE registry", StringComparison.Ordinal) ? "registry" : "builds";
                using var shape = c.CreateCommand();
                shape.CommandText = "SELECT CASE WHEN typeof(sql)='text' AND length(CAST(sql AS BLOB))<=4096 THEN sql ELSE NULL END FROM sqlite_master WHERE type='table' AND name=$name";
                shape.Parameters.AddWithValue("$name", table);
                if (shape.ExecuteScalar() is not string sql || sql != ddl) throw Unreadable("The authoritative registry schema does not match its format.");
            }
            using var tx = c.BeginTransaction(deferred: true);
            var meta = Metadata(c, tx);
            if (meta.Id != expectedId || Scalar(c, "SELECT COUNT(*) FROM registry", tx) is not long headerCount || headerCount != 1)
                throw Unreadable("The registry marker and metadata disagree.");
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = $"SELECT {Columns} FROM builds ORDER BY id";
            using var rows = cmd.ExecuteReader(); long count = 0; var digest = new byte[32]; string? previous = null;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested(); var stored = ReadRow(rows);
                if (previous is not null && string.CompareOrdinal(previous, stored.Entry.Identity.Id) >= 0) throw Unreadable("Duplicate or unordered registry identities.");
                previous = stored.Entry.Identity.Id; count = checked(count + 1); Xor(digest, rows.GetFieldValue<byte[]>(2));
            }
            if (count != meta.Count || !CryptographicOperations.FixedTimeEquals(digest, meta.Digest)) throw Unreadable("Registry entries do not match their committed count and digest.");
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (BuildRegistryException e) when (e.Code == BuildRegistryCodes.Validation) { throw Unreadable("A saved registry entry is invalid.", e); }
        catch (BuildRegistryException) { throw; }
        catch (Exception e) when (e is SqliteException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw Unreadable("Registry metadata is malformed.", e); }
    }
    private static Stored ReadRow(SqliteDataReader row)
    {
        try
        {
            for (var i = 3; i <= 5; i++) if (row.GetValue(i) is not long valid || valid != 1) throw Unreadable("A registry entry has an invalid storage class or size.");
            var id = row.GetString(0); Id(id);
            var data = row.GetString(1); var hash = row.GetFieldValue<byte[]>(2);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(data)), hash)) throw Unreadable("A registry entry checksum failed.");
            // Inspect the bounded wire document before materializing a caller-shaped dictionary.
            using (var document = JsonDocument.Parse(data))
            {
                var fields = document.RootElement.GetProperty("Entry").GetProperty("Identity").GetProperty("Fields");
                if (fields.ValueKind != JsonValueKind.Object) throw Unreadable("Saved identity fields are malformed.");
                var count = 0;
                foreach (var field in fields.EnumerateObject())
                {
                    if (++count > BuildRegistryLimits.MaxFields) throw Unreadable("Too many saved identity fields.");
                    Text(field.Name, BuildRegistryLimits.MaxFieldNameBytes, "saved field name");
                    if (field.Value.ValueKind != JsonValueKind.String) throw Unreadable("A saved identity field is not text.");
                    Text(field.Value.GetString(), BuildRegistryLimits.MaxFieldValueBytes, "saved field value", empty: true);
                }
            }
            var stored = Validate(JsonSerializer.Deserialize<Stored>(data, Json)!);
            if (stored.Entry.Identity.Id != id) throw Unreadable("A saved entry key disagrees with its canonical identity.");
            return stored;
        }
        catch (BuildRegistryException e) when (e.Code == BuildRegistryCodes.Validation) { throw Unreadable("A saved registry entry is invalid.", e); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { throw Unreadable("A saved registry entry is malformed.", e); }
    }
    private Stored? Read(string id, CancellationToken ct)
    {
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        try
        {
            ct.ThrowIfCancellationRequested(); using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM builds WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
            using var rows = cmd.ExecuteReader(); return rows.Read() ? ReadRow(rows) : null;
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }
    private (List<Stored> Values, bool More) Page(int limit, string? after, CancellationToken ct)
    {
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle));
        try
        {
            ct.ThrowIfCancellationRequested(); using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM builds" + (after is null ? "" : " WHERE id>$after") + " ORDER BY id LIMIT $n";
            if (after is not null) cmd.Parameters.AddWithValue("$after", after); cmd.Parameters.AddWithValue("$n", limit + 1);
            using var rows = cmd.ExecuteReader(); var entries = new List<Stored>(limit); long bytes = 0;
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (entries.Count == limit) return (entries, true);
                var stored = ReadRow(rows); var length = Encoding.UTF8.GetByteCount(rows.GetString(1));
                if (entries.Count > 0 && bytes + length > BuildRegistryLimits.MaxPageBytes) return (entries, true);
                bytes += length; entries.Add(stored);
            }
            return (entries, false);
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
    }
    private static void Xor(byte[] target, byte[] value) { for (var i = 0; i < 32; i++) target[i] ^= value[i]; }
    private void Save(Stored value, CancellationToken ct)
    {
        value = Validate(value); var data = JsonSerializer.Serialize(value, Json);
        if (Encoding.UTF8.GetByteCount(data) > BuildRegistryLimits.MaxEntryBytes) throw Invalid("The encoded registry entry is too large.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        options.Fault?.Invoke(BuildRegistryPoint.BeforeCommit); ct.ThrowIfCancellationRequested();
        using var tx = connection.BeginTransaction();
        using (var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(connection.Handle)))
        {
            try
            {
                var meta = Metadata(connection, tx);
                if (meta.Id != registryId) throw Unreadable("The registry identity changed while open.");
                var count = meta.Count; var digest = meta.Digest;
                using var prior = connection.CreateCommand(); prior.Transaction = tx;
                prior.CommandText = "SELECT sha256,typeof(sha256)='blob' AND length(sha256)=32 FROM builds WHERE id=$id";
                prior.Parameters.AddWithValue("$id", value.Entry.Identity.Id);
                using (var row = prior.ExecuteReader())
                {
                    if (row.Read()) { if (row.GetValue(1) is not long valid || valid != 1) throw Unreadable("An entry checksum is malformed."); Xor(digest, row.GetFieldValue<byte[]>(0)); }
                    else count = checked(count + 1);
                }
                Xor(digest, hash);
                using var write = connection.CreateCommand(); write.Transaction = tx;
                write.CommandText = "INSERT INTO builds VALUES($id,$data,$hash) ON CONFLICT(id) DO UPDATE SET data=excluded.data,sha256=excluded.sha256";
                write.Parameters.AddWithValue("$id", value.Entry.Identity.Id); write.Parameters.AddWithValue("$data", data); write.Parameters.AddWithValue("$hash", hash); write.ExecuteNonQuery();
                using var totals = connection.CreateCommand(); totals.Transaction = tx;
                totals.CommandText = "UPDATE registry SET entry_count=$count,digest=$digest WHERE id=1";
                totals.Parameters.AddWithValue("$count", count); totals.Parameters.AddWithValue("$digest", digest); totals.ExecuteNonQuery(); ct.ThrowIfCancellationRequested();
            }
            catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }
        // No cancellation registration or token check spans the durable commit boundary.
        options.Fault?.Invoke(BuildRegistryPoint.InsideCommit); tx.Commit(); options.Fault?.Invoke(BuildRegistryPoint.AfterCommit);
    }
}
