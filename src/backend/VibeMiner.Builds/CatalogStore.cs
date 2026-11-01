using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>A versions history entry the catalog has read, with what it said.</summary>
internal sealed record ProjectedEntry(HistoryEntry Entry, VersionsRead Read);

/// <summary>The catalog store can't be used. Codes: <c>CATALOG_NEWER</c>, <c>CATALOG_UNREADABLE</c>.</summary>
public sealed class CatalogStoreException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Layer 3's SQLite catalog store. A cache of the history log: everything in it is rebuilt from layer 1. Never deletes or
/// rewrites a file it can't read or that a newer engine wrote; it refuses it.
/// </summary>
/// <remarks>
/// Schema version 1 (user_version), application_id <see cref="ApplicationId"/>. Keys are 16-byte BLOBs with their type and
/// namespace beside them. Builds are keyed by identity id; their fields are the identity's sorted fields as a JSON object.
/// Entries hold versions replies only; <c>seqn</c> is the numeric <c>## seqn</c> and <c>readable</c> is 0 when the whole reply
/// had a problem.
/// </remarks>
internal sealed class CatalogStore : IAsyncDisposable
{
    public const int SchemaVersion = 1;
    /// <summary>"VMcs" big-endian: marks the file as a VibeMiner catalog store.</summary>
    public const int ApplicationId = 0x564D6373;

    private const string Schema = """
        CREATE TABLE entries(id INTEGER PRIMARY KEY, record BLOB NOT NULL CHECK(length(record) = 16), record_type INTEGER NOT NULL,
            record_ns INTEGER NOT NULL, kind INTEGER NOT NULL, product TEXT NOT NULL, region TEXT NOT NULL, observed_at INTEGER NOT NULL,
            url TEXT NOT NULL, document BLOB NOT NULL CHECK(length(document) = 16), document_type INTEGER NOT NULL,
            document_ns INTEGER NOT NULL, seqn INTEGER, readable INTEGER NOT NULL, UNIQUE(record, record_type, record_ns));
        CREATE INDEX entries_order ON entries(product, observed_at, record);
        CREATE INDEX entries_time ON entries(observed_at, record);
        CREATE TABLE builds(id TEXT PRIMARY KEY, product TEXT NOT NULL, canonical TEXT NOT NULL, fields TEXT NOT NULL) WITHOUT ROWID;
        CREATE TABLE sightings(entry INTEGER NOT NULL REFERENCES entries(id), row INTEGER NOT NULL, build TEXT NOT NULL REFERENCES builds(id),
            region TEXT NOT NULL, cdn_config TEXT NOT NULL, PRIMARY KEY(entry, row)) WITHOUT ROWID;
        CREATE INDEX sightings_build ON sightings(build);
        CREATE TABLE problems(entry INTEGER NOT NULL REFERENCES entries(id), ordinal INTEGER NOT NULL, code TEXT NOT NULL,
            message TEXT NOT NULL, row INTEGER, PRIMARY KEY(entry, ordinal)) WITHOUT ROWID;
        """;

    private static readonly string[] Tables = ["builds", "entries", "problems", "sightings"];

    /// <summary>The most problems a list returns.</summary>
    public const int MaxProblems = 1000;

    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    private CatalogStore(SqliteConnection connection) => this.connection = connection;

    public static async Task<CatalogStore> OpenAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (File.Exists(path)) await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        else
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new CatalogStoreException("CATALOG_UNREADABLE", "The catalog store folder can't be created.", e); }
        }

        SqliteConnection? c = null;
        try
        {
            c = Connect(path, SqliteOpenMode.ReadWriteCreate);
            await c.OpenAsync(cancellationToken).ConfigureAwait(false);
            // Checked again inside a write transaction: a second opener racing the first waits for it and sees its schema, and
            // a file swapped in after the probe is refused too.
            using (var tx = c.BeginTransaction(deferred: false))
            {
                var (version, appId, tables) = await HeaderAsync(c, tx, cancellationToken).ConfigureAwait(false);
                if (!IsEmpty(version, appId, tables)) Refuse(version, appId, tables);
                else
                {
                    await ExecAsync(c, Schema, tx, cancellationToken).ConfigureAwait(false);
                    await ExecAsync(c, $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion}", tx, cancellationToken).ConfigureAwait(false);
                    await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await ExecAsync(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON", null, cancellationToken).ConfigureAwait(false);
            return new CatalogStore(c);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c?.Dispose();
            if (e is CatalogStoreException) throw;
            throw new CatalogStoreException("CATALOG_UNREADABLE", "The catalog store can't be opened.", e);
        }
        catch { c?.Dispose(); throw; }
    }

    /// <summary>
    /// Reads an existing file's header and schema on a read-only connection, so a refused file stays unchanged. An empty
    /// database (another opener may be creating it) passes; the writer checks it again.
    /// </summary>
    private static async Task ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length == 0) return;
        try
        {
            using var c = Connect(path, SqliteOpenMode.ReadOnly);
            await c.OpenAsync(cancellationToken).ConfigureAwait(false);
            var (version, appId, tables) = await HeaderAsync(c, null, cancellationToken).ConfigureAwait(false);
            if (!IsEmpty(version, appId, tables)) Refuse(version, appId, tables);
        }
        catch (Exception e) when (e is not (OperationCanceledException or CatalogStoreException))
        {
            throw new CatalogStoreException("CATALOG_UNREADABLE", "The catalog store can't be read.", e);
        }
    }

    private static bool IsEmpty(long version, long appId, IReadOnlySet<string> tables) => version == 0 && appId == 0 && tables.Count == 0;

    private static void Refuse(long version, long appId, IReadOnlySet<string> tables)
    {
        if (appId != ApplicationId) throw new CatalogStoreException("CATALOG_UNREADABLE", "The file isn't a VibeMiner catalog store.");
        if (version > SchemaVersion) throw new CatalogStoreException("CATALOG_NEWER", "A newer VibeMiner wrote the catalog store.");
        if (version != SchemaVersion || !Tables.All(tables.Contains))
            throw new CatalogStoreException("CATALOG_UNREADABLE", "The catalog store's schema isn't readable.");
    }

    private static async Task<(long Version, long AppId, HashSet<string> Tables)> HeaderAsync(SqliteConnection c, SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        cmd.CommandText = "PRAGMA application_id";
        var appId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false)) tables.Add(r.GetString(0));
        return (version, appId, tables);
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString();

    private static SqliteConnection Connect(string path, SqliteOpenMode mode) => new(ConnectionString(path, mode));

    private static async Task ExecAsync(SqliteConnection c, string sql, SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> UseAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task<bool> HasAsync(ObjectKey record, CancellationToken cancellationToken) => UseAsync(async ct =>
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM entries WHERE record = $r AND record_type = $t AND record_ns = $n";
        AddKey(cmd, "$r", "$t", "$n", record);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }, cancellationToken);

    /// <summary>Adds a versions entry and what it said, unless its record is there already. Throws for another kind.</summary>
    public Task AddAsync(ProjectedEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Entry.Kind != HistoryKind.Versions)
            throw new ArgumentException("The catalog store holds versions entries only.", nameof(entry));
        return UseAsync(async ct =>
        {
            var (e, read) = (entry.Entry, entry.Read);
            using var tx = connection.BeginTransaction();
            long id;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO entries(record, record_type, record_ns, kind, product, region, observed_at, url, document, document_type,
                        document_ns, seqn, readable) VALUES($r, $rt, $rn, $k, $p, $g, $o, $u, $d, $dt, $dn, $s, $ok)
                    ON CONFLICT DO NOTHING RETURNING id
                    """;
                AddKey(cmd, "$r", "$rt", "$rn", e.Record);
                AddKey(cmd, "$d", "$dt", "$dn", e.Document);
                cmd.Parameters.AddWithValue("$k", (int)e.Kind); cmd.Parameters.AddWithValue("$p", e.Product);
                cmd.Parameters.AddWithValue("$g", e.Region); cmd.Parameters.AddWithValue("$o", e.ObservedAt.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$u", e.Url); cmd.Parameters.AddWithValue("$s", (object?)read.SequenceNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ok", read.Readable ? 1 : 0);
                if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long inserted) return false;  // already added
                id = inserted;
            }
            using (var build = connection.CreateCommand())
            using (var sighting = connection.CreateCommand())
            {
                build.Transaction = tx; sighting.Transaction = tx;
                build.CommandText = "INSERT INTO builds(id, product, canonical, fields) VALUES($id, $p, $c, $f) ON CONFLICT DO NOTHING";
                sighting.CommandText = "INSERT INTO sightings(entry, row, build, region, cdn_config) VALUES($e, $row, $b, $g, $c)";
                foreach (var s in read.Sightings)
                {
                    build.Parameters.Clear();
                    build.Parameters.AddWithValue("$id", s.Identity.Id); build.Parameters.AddWithValue("$p", s.Identity.ProductId);
                    build.Parameters.AddWithValue("$c", s.Identity.Canonical);
                    build.Parameters.AddWithValue("$f", JsonSerializer.Serialize(new SortedDictionary<string, string>(
                        s.Identity.Fields.ToDictionary(), StringComparer.Ordinal)));
                    await build.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    sighting.Parameters.Clear();
                    sighting.Parameters.AddWithValue("$e", id); sighting.Parameters.AddWithValue("$row", s.Row);
                    sighting.Parameters.AddWithValue("$b", s.Identity.Id); sighting.Parameters.AddWithValue("$g", s.Region);
                    sighting.Parameters.AddWithValue("$c", s.CdnConfig);
                    await sighting.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            using (var problem = connection.CreateCommand())
            {
                problem.Transaction = tx;
                problem.CommandText = "INSERT INTO problems(entry, ordinal, code, message, row) VALUES($e, $i, $c, $m, $row)";
                for (var i = 0; i < read.Problems.Count; i++)
                {
                    var p = read.Problems[i];
                    problem.Parameters.Clear();
                    problem.Parameters.AddWithValue("$e", id); problem.Parameters.AddWithValue("$i", i);
                    problem.Parameters.AddWithValue("$c", p.Code); problem.Parameters.AddWithValue("$m", p.Message);
                    problem.Parameters.AddWithValue("$row", (object?)p.Row ?? DBNull.Value);
                    await problem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<HistoryEntry?> NewestAsync(CancellationToken cancellationToken) => UseAsync(async ct =>
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {EntryColumns} FROM entries ORDER BY observed_at DESC, record DESC LIMIT 1";
        using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadEntry(r, 0) : null;
    }, cancellationToken);

    /// <summary>
    /// The product's builds from its versions entries. Current: listed by the newest readable entry of some host, where newest
    /// is the highest numeric seqn (then later observed, then larger record) and entries without one count only when none of
    /// that host's readable entries has one. Problems: at most <see cref="MaxProblems"/>, newest entry first, then by row.
    /// </summary>
    public Task<BuildList> ListAsync(string product, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(product);
        return UseAsync(async ct =>
        {
            using var tx = connection.BeginTransaction(deferred: true);
            SqliteCommand Command(string sql)
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = tx; cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$p", product); cmd.Parameters.AddWithValue("$k", (int)HistoryKind.Versions);
                return cmd;
            }

            var rows = new List<(string Build, BuildRegion Region)>();
            var fields = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
            using (var cmd = Command("""
                WITH mine AS (SELECT id, region, observed_at, record, record_type, record_ns, seqn, readable FROM entries
                    WHERE product = $p AND kind = $k),
                newest AS (SELECT id FROM (SELECT id, ROW_NUMBER() OVER (PARTITION BY region
                    ORDER BY seqn IS NULL, seqn DESC, observed_at DESC, record DESC) AS n FROM mine WHERE readable) WHERE n = 1),
                seen AS (SELECT s.build, s.region, s.cdn_config, e.observed_at, e.record, e.record_type, e.record_ns,
                    MIN(e.observed_at) OVER g AS first_seen, MAX(s.entry IN (SELECT id FROM newest)) OVER g AS cur,
                    ROW_NUMBER() OVER (PARTITION BY s.build, s.region, s.cdn_config ORDER BY e.observed_at DESC, e.record DESC) AS n
                    FROM sightings s JOIN mine e ON e.id = s.entry WINDOW g AS (PARTITION BY s.build, s.region, s.cdn_config))
                SELECT seen.build, seen.region, seen.cdn_config, seen.first_seen, seen.observed_at, seen.cur, seen.record, seen.record_type,
                    seen.record_ns, b.fields FROM seen JOIN builds b ON b.id = seen.build WHERE seen.n = 1
                """))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var build = r.GetString(0);
                    rows.Add((build, new BuildRegion(r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
                        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), r.GetInt64(5) != 0,
                        new ObjectKey((ObjectKeyType)r.GetInt32(7), (byte[])r.GetValue(6), (byte)r.GetInt32(8)))));
                    if (!fields.ContainsKey(build))
                        fields[build] = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                            new SortedDictionary<string, string>(
                                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(9))!, StringComparer.Ordinal));
                }
            }

            var builds = rows.GroupBy(x => x.Build, StringComparer.Ordinal).Select(b =>
            {
                var regions = b.Select(x => x.Region).OrderBy(x => x.Region, StringComparer.Ordinal).ThenBy(x => x.FirstSeen)
                    .ThenBy(x => x.CdnConfig, StringComparer.Ordinal).ToList();
                var f = fields[b.Key];
                var productConfig = f.TryGetValue("ProductConfig", out var pc) && pc.Length != 0 ? pc : null;
                return new CatalogBuild(product, b.Key, f["VersionsName"], f["BuildConfig"], productConfig, f,
                    regions.Min(x => x.FirstSeen), regions.Max(x => x.LastSeen), regions.Any(x => x.Current), regions);
            }).OrderByDescending(b => b.FirstSeen).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();

            var problems = new List<CatalogProblem>();
            using (var cmd = Command("""
                SELECT p.code, p.message, p.row, e.record, e.record_type, e.record_ns FROM problems p JOIN entries e ON e.id = p.entry
                WHERE e.product = $p AND e.kind = $k ORDER BY e.observed_at DESC, e.record DESC, coalesce(p.row, -1), p.ordinal LIMIT $max
                """))
            {
                cmd.Parameters.AddWithValue("$max", MaxProblems);
                using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    problems.Add(new CatalogProblem(r.GetString(0), r.GetString(1),
                        new ObjectKey((ObjectKeyType)r.GetInt32(4), (byte[])r.GetValue(3), (byte)r.GetInt32(5)), r.IsDBNull(2) ? null : r.GetInt32(2)));
            }

            DateTimeOffset? last;
            using (var cmd = Command("SELECT MAX(observed_at) FROM entries WHERE product = $p AND kind = $k"))
                last = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

            return new BuildList(product, builds, problems, last);
        }, cancellationToken);
    }

    private const string EntryColumns = "record, record_type, record_ns, kind, product, region, observed_at, url, document, document_type, document_ns";

    private static HistoryEntry ReadEntry(SqliteDataReader r, int at) => new(
        (HistoryKind)r.GetInt32(at + 3), r.GetString(at + 4), r.GetString(at + 5),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(at + 6)), r.GetString(at + 7),
        new ObjectKey((ObjectKeyType)r.GetInt32(at + 9), (byte[])r.GetValue(at + 8), (byte)r.GetInt32(at + 10)),
        new ObjectKey((ObjectKeyType)r.GetInt32(at + 1), (byte[])r.GetValue(at), (byte)r.GetInt32(at + 2)));

    private static void AddKey(SqliteCommand cmd, string key, string type, string ns, ObjectKey value)
    {
        cmd.Parameters.AddWithValue(key, value.KeyBytes()); cmd.Parameters.AddWithValue(type, (int)value.Type);
        cmd.Parameters.AddWithValue(ns, (int)value.Namespace);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
