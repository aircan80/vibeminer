using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Single bounded database worker owns synchronous SQLite work, outside protocol dispatch.</summary>
public sealed partial class DiscoveryCatalog : IDiscoveryCatalog, IWorkspaceCatalog
{
    private readonly Channel<Func<Task>> queue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task worker;
    private SqliteConnection? connection;
    private readonly string databasePath;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private SqliteConnection Db => connection ?? throw new InvalidOperationException("Catalog is not initialized.");

    private DiscoveryCatalog(string dataDirectory)
    {
        databasePath = Path.Combine(dataDirectory, "discovery-v1.sqlite3");
        worker = Task.Run(async () =>
        {
            try { await foreach (var command in queue.Reader.ReadAllAsync()) await command(); }
            finally { connection?.Dispose(); }
        });
    }
    public static async Task<DiscoveryCatalog> OpenAsync(string dataDirectory, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("An absolute app-owned data directory is required.", nameof(dataDirectory));
        var catalog = new DiscoveryCatalog(dataDirectory);
        try { await catalog.Schedule(() => { catalog.Initialize(dataDirectory); return true; }, token); return catalog; }
        catch { await catalog.DisposeAsync(); throw; }
    }
    private void Initialize(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        Db.Open();
        Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA cache_size=-32768; PRAGMA temp_store=FILE; PRAGMA max_page_count=4194304;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"));
        if (version > 14) throw new DiscoveryException("CATALOG_VERSION", "This catalog requires a newer engine.");
        if (version == 14) return;
        if (version == 13) { UpgradeCreatureGeometry(); return; }
        if (version == 12) { UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 11) { UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 10) { UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 9) { UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 8) { UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 7) { UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 6) { UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 5) { UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 4) { UpgradeInventoryCheckpoints(); UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 3) { UpgradeInventory(); UpgradeInventoryCheckpoints(); UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 2) { UpgradeWorkspace(); UpgradeInventory(); UpgradeInventoryCheckpoints(); UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return; }
        if (version == 1)
        {
            using var upgrade = Db.BeginTransaction();
            Execute("""
                CREATE TABLE build_regions(build_id TEXT NOT NULL,region TEXT NOT NULL,generation INTEGER NOT NULL,
                    ordinal INTEGER NOT NULL,cdns_ordinal INTEGER,PRIMARY KEY(build_id,region),
                    FOREIGN KEY(build_id) REFERENCES builds(id),
                    FOREIGN KEY(generation,ordinal) REFERENCES observations(generation,ordinal));
                INSERT INTO build_regions
                    SELECT build_id,region,generation,ordinal,cdns_ordinal FROM (
                        SELECT *,ROW_NUMBER() OVER(PARTITION BY build_id,region ORDER BY generation DESC,ordinal) AS rank
                        FROM observations) WHERE rank=1;
                PRAGMA user_version=2;
                """, upgrade);
            upgrade.Commit(); UpgradeWorkspace(); UpgradeInventory(); UpgradeInventoryCheckpoints(); UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry(); return;
        }
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE sequence(id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL);
            INSERT INTO sequence VALUES(1,0);
            CREATE TABLE products(product TEXT PRIMARY KEY, generation INTEGER NOT NULL,
                last_checked TEXT, checked_regions TEXT NOT NULL DEFAULT '[]', errors TEXT NOT NULL DEFAULT '[]',
                connection TEXT NOT NULL DEFAULT 'notChecked', visited INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE raw_documents(sha256 TEXT PRIMARY KEY, byte_length INTEGER NOT NULL, body BLOB NOT NULL);
            CREATE TABLE events(generation INTEGER PRIMARY KEY, event_id TEXT NOT NULL UNIQUE, product TEXT NOT NULL,
                completed_at TEXT NOT NULL, checked_regions TEXT NOT NULL, errors TEXT NOT NULL,
                connection TEXT NOT NULL, FOREIGN KEY(product) REFERENCES products(product));
            CREATE TABLE documents(generation INTEGER NOT NULL, role TEXT NOT NULL, url TEXT NOT NULL,
                retrieved_at TEXT NOT NULL, sha256 TEXT NOT NULL, parser_revision TEXT NOT NULL,
                PRIMARY KEY(generation, role), FOREIGN KEY(generation) REFERENCES events(generation),
                FOREIGN KEY(sha256) REFERENCES raw_documents(sha256));
            CREATE TABLE builds(id TEXT PRIMARY KEY, product TEXT NOT NULL, canonical TEXT NOT NULL,
                identity_fields TEXT NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                first_generation INTEGER NOT NULL, last_generation INTEGER NOT NULL, is_new INTEGER NOT NULL);
            CREATE INDEX builds_product_id ON builds(product,id);
            CREATE TABLE observations(generation INTEGER NOT NULL, ordinal INTEGER NOT NULL, region TEXT NOT NULL,
                build_id TEXT NOT NULL, cdns_ordinal INTEGER, original_fields TEXT NOT NULL, route_fields TEXT,
                PRIMARY KEY(generation,ordinal), FOREIGN KEY(generation) REFERENCES events(generation),
                FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE INDEX observations_build_region ON observations(build_id,region,generation DESC,ordinal);
            CREATE TABLE build_regions(build_id TEXT NOT NULL,region TEXT NOT NULL,generation INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,cdns_ordinal INTEGER,PRIMARY KEY(build_id,region),
                FOREIGN KEY(build_id) REFERENCES builds(id),
                FOREIGN KEY(generation,ordinal) REFERENCES observations(generation,ordinal));
            CREATE TABLE baselines(product TEXT NOT NULL, region TEXT NOT NULL, generation INTEGER NOT NULL,
                PRIMARY KEY(product,region), FOREIGN KEY(product) REFERENCES products(product));
            CREATE TABLE advertisements(product TEXT NOT NULL, region TEXT NOT NULL, build_id TEXT NOT NULL,
                has_route INTEGER NOT NULL, generation INTEGER NOT NULL,
                PRIMARY KEY(product,region,build_id), FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE INDEX advertisements_build ON advertisements(build_id,region);
            PRAGMA user_version=2;
            """, transaction);
        transaction.Commit();
        UpgradeWorkspace(); UpgradeInventory(); UpgradeInventoryCheckpoints(); UpgradeDownloads(); UpgradeTablesData(); UpgradeEncodedReceipts(); UpgradeRetention(); UpgradeItemIcons(); UpgradeItemClassifications(); UpgradeCreatures(); UpgradeItemEffects(); UpgradeCreatureGeometry();
    }
    private async Task<T> Schedule<T>(Func<T> operation, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(() =>
        {
            try { token.ThrowIfCancellationRequested(); completion.SetResult(operation()); }
            catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
            catch (SqliteException ex)
            {
                var (code, message, retryable) = ex.SqliteErrorCode switch
                {
                    5 or 6 => ("CATALOG_BUSY", "The discovery catalog is busy. Retry this operation.", true),
                    13 => ("STORAGE_FULL", "There is not enough space to save discovery history.", false),
                    11 or 26 => ("CATALOG_INTEGRITY", "The discovery catalog could not be verified.", false),
                    _ => ("STORAGE_FAILURE", "The discovery catalog could not complete this operation.", true)
                };
                completion.SetException(new DiscoveryException(code, message, retryable));
            }
            catch (Exception ex) { completion.SetException(ex); }
            return Task.CompletedTask;
        }, token);
        // Await retirement of queued/running work even when the caller cancels.
        return await completion.Task;
    }
    public Task<long> BeginRefreshAsync(string product, CancellationToken token) => Schedule(() =>
    {
        ProductRegistry.Validate(product);
        using var transaction = Db.BeginTransaction();
        Execute("UPDATE sequence SET value=value+1 WHERE id=1", transaction);
        var generation = Convert.ToInt64(Scalar("SELECT value FROM sequence WHERE id=1", transaction));
        Execute("INSERT INTO products(product,generation) VALUES($p,$g) ON CONFLICT(product) DO UPDATE SET generation=$g",
            transaction, ("$p", product), ("$g", generation));
        transaction.Commit();
        return generation;
    }, token);

    public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt,
        CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        if (Convert.ToInt64(Scalar("SELECT generation FROM products WHERE product=$p", transaction, ("$p", product))) != generation)
            throw new DiscoveryException("STALE_REQUEST", "A newer refresh has replaced this discovery attempt.", true);
        var checkedRegions = Serialize(attempt.CheckedRegions);
        var errors = Serialize(attempt.Errors);
        Execute("INSERT INTO events VALUES($g,$id,$p,$time,$regions,$errors,$connection)", transaction,
            ("$g", generation), ("$id", Guid.NewGuid().ToString("N")), ("$p", product), ("$time", completedAt),
            ("$regions", checkedRegions), ("$errors", errors), ("$connection", attempt.Connection));
        foreach (var document in attempt.Documents)
        {
            token.ThrowIfCancellationRequested();
            if (document.Bytes.Length > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(document.Bytes)) != document.Sha256)
                throw new DiscoveryException("CATALOG_INTEGRITY", "A source document failed integrity verification.");
            Execute("INSERT OR IGNORE INTO raw_documents VALUES($hash,$length,$body)", transaction,
                ("$hash", document.Sha256), ("$length", document.Bytes.Length), ("$body", document.Bytes));
            using (var existing = Command("SELECT byte_length,body FROM raw_documents WHERE sha256=$hash", transaction, ("$hash", document.Sha256)))
            using (var reader = existing.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt32(0) != document.Bytes.Length || !((byte[])reader[1]).AsSpan().SequenceEqual(document.Bytes))
                    throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source bytes do not match their digest.");
            }
            Execute("INSERT INTO documents VALUES($g,$role,$url,$time,$hash,$revision)", transaction,
                ("$g", generation), ("$role", document.Role), ("$url", document.Url), ("$time", document.RetrievedAt),
                ("$hash", document.Sha256), ("$revision", document.ParserRevision));
        }
        var priorBaselines = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("SELECT region FROM baselines WHERE product=$p", transaction, ("$p", product)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) priorBaselines.Add(reader.GetString(0));
        foreach (var group in attempt.Observations.GroupBy(row => row.Identity.Id))
        {
            token.ThrowIfCancellationRequested();
            var identity = group.First().Identity;
            if (identity.ProductId != product || group.Any(row => row.Identity.Canonical != identity.Canonical))
                throw new DiscoveryException("IDENTITY_COLLISION", "Observed build identities do not agree on their complete key.");
            var previous = Scalar("SELECT canonical FROM builds WHERE id=$id", transaction, ("$id", identity.Id)) as string;
            if (previous is not null && previous != identity.Canonical)
                throw new DiscoveryException("IDENTITY_COLLISION", "Stored build identity does not match its complete key.");
            Execute("""
                INSERT INTO builds VALUES($id,$p,$canonical,$fields,$time,$time,$g,$g,$new)
                ON CONFLICT(id) DO UPDATE SET last_seen=$time,last_generation=$g
                """, transaction, ("$id", identity.Id), ("$p", product), ("$canonical", identity.Canonical),
                ("$fields", Serialize(identity.Fields)), ("$time", completedAt), ("$g", generation),
                ("$new", group.Any(row => priorBaselines.Contains(row.Region)) ? 1 : 0));
        }
        foreach (var region in attempt.SuccessfulRegions)
        {
            token.ThrowIfCancellationRequested();
            Execute("DELETE FROM advertisements WHERE product=$p AND region=$r", transaction, ("$p", product), ("$r", region));
            Execute("INSERT INTO baselines VALUES($p,$r,$g) ON CONFLICT(product,region) DO UPDATE SET generation=$g", transaction,
                ("$p", product), ("$r", region), ("$g", generation));
        }
        foreach (var row in attempt.Observations)
        {
            token.ThrowIfCancellationRequested();
            Execute("INSERT INTO observations VALUES($g,$ordinal,$region,$id,$cdn,$fields,$route)", transaction,
                ("$g", generation), ("$ordinal", row.VersionsOrdinal), ("$region", row.Region), ("$id", row.Identity.Id),
                ("$cdn", row.CdnsOrdinal), ("$fields", Serialize(row.OriginalFields)),
                ("$route", row.RouteFields is null ? null : Serialize(row.RouteFields)));
            Execute("""
                INSERT INTO build_regions VALUES($id,$region,$g,$ordinal,$cdn)
                ON CONFLICT(build_id,region) DO UPDATE SET generation=$g,ordinal=$ordinal,cdns_ordinal=$cdn
                    WHERE build_regions.generation<$g OR (build_regions.generation=$g AND build_regions.ordinal>$ordinal)
                """, transaction, ("$id", row.Identity.Id), ("$region", row.Region), ("$g", generation),
                ("$ordinal", row.VersionsOrdinal), ("$cdn", row.CdnsOrdinal));
            Execute("INSERT OR IGNORE INTO advertisements VALUES($p,$r,$id,$route,$g)", transaction,
                ("$p", product), ("$r", row.Region), ("$id", row.Identity.Id), ("$route", row.CdnsOrdinal is null ? 0 : 1), ("$g", generation));
        }
        Execute("""
            UPDATE products SET last_checked=$time,checked_regions=$regions,errors=$errors,connection=$connection,
                visited=CASE WHEN $success=1 THEN 1 ELSE visited END WHERE product=$p
            """, transaction, ("$time", completedAt), ("$regions", checkedRegions), ("$errors", errors),
            ("$connection", attempt.Connection), ("$success", attempt.SuccessfulRegions.Count > 0 || attempt.Connection == "online" ? 1 : 0), ("$p", product));
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // The committed page is projected on the same worker before another request can replace it.
        return ReadCore(product, "all", 50, null, CancellationToken.None);
    }, token);

    private sealed record Cursor(string Product, string Filter, long Generation, string After);
    public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) =>
        Schedule(() => ReadCore(product, filter, limit, cursor, token), token);

    private DiscoveryPage ReadCore(string product, string filter, int limit, string? cursor, CancellationToken token, string? exactBuild = null)
    {
        ProductRegistry.Validate(product);
        if (filter is not ("all" or "local" or "advertised") || limit is < 1 or > 100)
            throw new DiscoveryException("INVALID_REQUEST", "The discovery filter or page size is invalid.");
        long generation = 0;
        string? checkedAt = null;
        string[] checkedRegions = [];
        DiscoveryProblem[] errors = [];
        var state = "notChecked";
        using (var command = Command("SELECT generation,last_checked,checked_regions,errors,connection FROM products WHERE product=$p", null, ("$p", product)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                generation = reader.GetInt64(0); checkedAt = reader.IsDBNull(1) ? null : reader.GetString(1);
                checkedRegions = Deserialize<string[]>(reader.GetString(2));
                errors = Deserialize<DiscoveryProblem[]>(reader.GetString(3)); state = reader.GetString(4);
            }
        }
        var after = "";
        if (cursor is not null)
        {
            Cursor? decoded;
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                decoded = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor), Json);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            { throw new DiscoveryException("INVALID_CURSOR", "The discovery cursor is invalid."); }
            if (decoded is null || decoded.Product != product || decoded.Filter != filter || decoded.Generation != generation)
                throw new DiscoveryException("STALE_CURSOR", "Discovery changed. Read the first page again.", true);
            if (decoded.After is not { Length: 64 } key || !key.All(Uri.IsHexDigit))
                throw new DiscoveryException("INVALID_CURSOR", "The discovery cursor is invalid.");
            after = decoded.After;
        }
        var history = Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM builds WHERE product=$p)", null, ("$p", product))) == 1;
        var records = new List<(string Id, string Canonical, string Fields, string First, string Last, bool New)>();
        var predicate = filter switch { "local" => " AND (EXISTS(SELECT 1 FROM inventory_generations i WHERE i.build_id=b.id AND i.state='ready') OR EXISTS(SELECT 1 FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id WHERE a.build_id=b.id AND a.profile='home-download-v1' AND d.artifact_sha256 IS NOT NULL))", "advertised" => " AND EXISTS(SELECT 1 FROM advertisements a WHERE a.build_id=b.id)", _ => "" };
        using (var command = Command($"SELECT id,canonical,identity_fields,first_seen,last_seen,is_new AND NOT EXISTS(SELECT 1 FROM open_acknowledgements o WHERE o.build_id=b.id) FROM builds b WHERE product=$p AND id>$after AND ($exact IS NULL OR id=$exact){predicate} ORDER BY id LIMIT $limit", null,
            ("$p", product), ("$after", after), ("$exact", exactBuild), ("$limit", limit + 1)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) records.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        var builds = new List<Build>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var size = 0;
        foreach (var row in records.Take(limit))
        {
            token.ThrowIfCancellationRequested();
            var fields = Deserialize<Dictionary<string, string>>(row.Fields);
            // Full canonical identity is retained and checked against its digest on every restore.
            var identitySource = new Dictionary<string, string>(fields, StringComparer.Ordinal) { ["Region"] = "us" };
            var restored = BuildIdentity.Create(product, identitySource);
            if (restored.Id != row.Id || restored.Canonical != row.Canonical)
                throw new DiscoveryException("CATALOG_INTEGRITY", "A saved build identity failed integrity verification.");
            var regions = new List<string>(); var advertised = new List<string>(); var routes = new List<string>();
            using (var command = Command("SELECT region FROM build_regions WHERE build_id=$id ORDER BY region", null, ("$id", row.Id)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) regions.Add(reader.GetString(0));
            using (var command = Command("SELECT region,has_route FROM advertisements WHERE build_id=$id ORDER BY region", null, ("$id", row.Id)))
            using (var reader = command.ExecuteReader()) while (reader.Read())
            { advertised.Add(reader.GetString(0)); if (reader.GetBoolean(1)) routes.Add(reader.GetString(0)); }
            if (regions.Count == 0) throw new DiscoveryException("CATALOG_INTEGRITY", "Saved build observations are missing.");
            var receipts = ReadReceipts(row.Id, verified, token);
            var build = new Build(row.Id, product, fields["VersionsName"], fields.GetValueOrDefault("BuildId", ""),
                fields["BuildConfig"], fields["CDNConfig"], fields.GetValueOrDefault("ProductConfig") is { Length: > 0 } productConfig ? productConfig : null, fields,
                regions, advertised, routes, row.First, row.Last, row.New, advertised.Count > 0, Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM inventory_generations WHERE build_id=$b AND state='ready') OR EXISTS(SELECT 1 FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id WHERE a.build_id=$b AND a.profile='home-download-v1' AND d.artifact_sha256 IS NOT NULL)", null, ("$b", row.Id))) == 1, "unknown", receipts);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(build, Json).Length;
            if (size + bytes > 750_000)
            {
                if (builds.Count == 0) throw new DiscoveryException("RESULT_LIMIT", "This build's source projection exceeds the protocol limit.");
                break;
            }
            size += bytes; builds.Add(build);
        }
        var more = records.Count > builds.Count;
        var next = more && builds.Count > 0 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(product, filter, generation, builds[^1].Id), Json)) : null;
        return new DiscoveryPage(product, builds, next, checkedAt, checkedRegions, errors, history, state);
    }

    private List<SourceReceipt> ReadReceipts(string buildId, HashSet<string> verified, CancellationToken token)
    {
        var receipts = new List<SourceReceipt>();
        // Each region projects its newest exact observation. Older/duplicate row receipts remain immutable in SQLite.
        var checkedGenerations = new Dictionary<long, bool>();
        using var command = Command($$"""
            SELECT r.region,d.role,d.url,d.retrieved_at,d.sha256,b.byte_length,
                CASE d.role WHEN 'versions' THEN r.ordinal ELSE r.cdns_ordinal END,
                CASE WHEN {{OpeningSourceRowValid}} THEN b.body END,r.generation,
                CASE WHEN {{OpeningSourceRowValid}} AND typeof(r.generation)='integer'
                    AND typeof(r.region)='text' AND length(CAST(r.region AS BLOB)) BETWEEN 2 AND 8
                    AND r.region NOT GLOB '*[^a-z]*'
                    AND typeof(CASE d.role WHEN 'versions' THEN r.ordinal ELSE r.cdns_ordinal END)='integer'
                    AND (CASE d.role WHEN 'versions' THEN r.ordinal ELSE r.cdns_ordinal END) BETWEEN 0 AND 9999
                    THEN 1 ELSE 0 END,CASE WHEN r.cdns_ordinal IS NULL THEN 0 ELSE 1 END
            FROM build_regions r JOIN documents d ON d.generation=r.generation
                LEFT JOIN raw_documents b ON b.sha256=d.sha256
            WHERE r.build_id=$id AND d.role IN ('versions','cdns') AND (d.role='versions' OR r.cdns_ordinal IS NOT NULL)
            ORDER BY r.region,d.role
            """, null, ("$id", buildId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            // Validate every row, even when a preceding region already verified this SHA.
            if (reader.GetInt32(9) != 1)
                throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source metadata exceeds its actual byte/type or receipt bounds.");
            var sourceGeneration = reader.GetInt64(8);
            var needsCdns = reader.GetInt32(10) == 1;
            if (!checkedGenerations.TryGetValue(sourceGeneration, out var checkedPair) || needsCdns && !checkedPair)
            { ValidateOpeningSourceDocuments(sourceGeneration, token, needsCdns); checkedGenerations[sourceGeneration] = needsCdns; }
            var hash = reader.GetString(4); var length = reader.GetInt32(5);
            if (verified.Add(hash))
            {
                var body = (byte[])reader[7];
                if (body.Length != length || Convert.ToHexStringLower(SHA256.HashData(body)) != hash)
                    throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source metadata failed integrity verification.");
            }
            receipts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), hash, length, reader.IsDBNull(6) ? null : reader.GetInt32(6)));
        }
        var expected = Convert.ToInt32(Scalar("""
            SELECT COALESCE(SUM(1+CASE WHEN cdns_ordinal IS NULL THEN 0 ELSE 1 END),0)
            FROM build_regions WHERE build_id=$id
            """, null, ("$id", buildId)));
        if (receipts.Count != expected || receipts.Count == 0)
            throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source receipts are missing.");
        return receipts;
    }
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    {
        var command = Db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    { using var command = Command(sql, transaction, parameters); command.ExecuteNonQuery(); }
    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string, object?)[] parameters)
    { using var command = Command(sql, transaction, parameters); return command.ExecuteScalar(); }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value)
    {
        try { return JsonSerializer.Deserialize<T>(value, Json) ?? throw new DiscoveryException("CATALOG_INTEGRITY", "Saved discovery data is incomplete."); }
        catch (JsonException) { throw new DiscoveryException("CATALOG_INTEGRITY", "Saved discovery data is invalid."); }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await worker; }
}
