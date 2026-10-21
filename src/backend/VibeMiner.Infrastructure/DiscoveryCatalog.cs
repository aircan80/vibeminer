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
    /// <summary>Catalog schema version. Version 16 is the first with per-ledger stored coverage (D065).</summary>
    internal const int CatalogVersion = 16;
    /// <summary>
    /// Written once when a fresh catalog is created. Older catalogs (build-identity-v2 at version 15, or none at all)
    /// never have it and are refused, never migrated.
    /// </summary>
    internal const string CatalogLineage = "stored-coverage-v1";
    private void Initialize(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        // Recognise an existing catalog on a read-only connection first. A read-write connection would checkpoint a
        // pending -wal of an older catalog into the main file on close; a refused catalog must stay byte-for-byte.
        if (File.Exists(databasePath))
        {
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            probe.Open();
            RecogniseCatalog(probe);
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        Db.Open();
        // The file can only have been created or replaced in between by another writer; check again before any write.
        var version = RecogniseCatalog(Db);
        Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA cache_size=-32768; PRAGMA temp_store=FILE; PRAGMA max_page_count=4194304;");
        if (version == CatalogVersion) return;
        if (version == 0) CreateDiscoverySchema();
        // Each step commits its own user_version. A fresh catalog interrupted between steps resumes here.
        Action[] steps = [UpgradeWorkspace, UpgradeInventory, UpgradeInventoryCheckpoints, UpgradeDownloads, UpgradeTablesData,
            UpgradeEncodedReceipts, UpgradeRetention, UpgradeItemIcons, UpgradeItemClassifications, UpgradeCreatures,
            UpgradeItemEffects, UpgradeCreatureGeometry, UpgradeStoredCoverage, CompleteCatalog];
        for (var current = Math.Max(version, 2); current < CatalogVersion; current++) steps[current - 2]();
    }
    /// <summary>Read-only recognition: returns the version, or refuses a catalog this engine must not read or change.</summary>
    private static int RecogniseCatalog(SqliteConnection db)
    {
        object? Read(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        Read("PRAGMA busy_timeout=5000;");
        var version = Convert.ToInt32(Read("PRAGMA user_version;"));
        if (version > CatalogVersion) throw new DiscoveryException("CATALOG_VERSION", "This catalog requires a newer engine.");
        if (version == 0) return version;
        var lineage = Convert.ToInt64(Read("SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='catalog_lineage')")) == 1 &&
            Read("SELECT CASE WHEN typeof(revision)='text' AND length(revision)<=64 THEN revision END FROM catalog_lineage WHERE id=1") as string == CatalogLineage;
        if (!lineage)
            throw new DiscoveryException("CATALOG_RESET_REQUIRED",
                "This catalog was written by an older engine. It was not read or changed. Start VibeMiner with a fresh catalog.");
        return version;
    }
    private void CompleteCatalog()
    {
        using var transaction = Db.BeginTransaction();
        Execute($"PRAGMA user_version={CatalogVersion};", transaction);
        transaction.Commit();
    }
    private void CreateDiscoverySchema()
    {
        using var transaction = Db.BeginTransaction();
        Execute($$"""
            CREATE TABLE catalog_lineage(id INTEGER PRIMARY KEY CHECK(id=1), revision TEXT NOT NULL);
            INSERT INTO catalog_lineage VALUES(1,'{{CatalogLineage}}');
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
            CREATE TABLE build_cdn_configs(build_id TEXT NOT NULL, cdn_config TEXT NOT NULL,
                first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                first_generation INTEGER NOT NULL, last_generation INTEGER NOT NULL,
                PRIMARY KEY(build_id,cdn_config), FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE TABLE observations(generation INTEGER NOT NULL, ordinal INTEGER NOT NULL, region TEXT NOT NULL,
                build_id TEXT NOT NULL, cdns_ordinal INTEGER, original_fields TEXT NOT NULL, route_fields TEXT,
                cdn_config TEXT NOT NULL,
                PRIMARY KEY(generation,ordinal), FOREIGN KEY(generation) REFERENCES events(generation),
                FOREIGN KEY(build_id,cdn_config) REFERENCES build_cdn_configs(build_id,cdn_config));
            CREATE INDEX observations_build_region ON observations(build_id,region,generation DESC,ordinal);
            CREATE TABLE build_regions(build_id TEXT NOT NULL,region TEXT NOT NULL,generation INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,cdns_ordinal INTEGER,PRIMARY KEY(build_id,region),
                FOREIGN KEY(build_id) REFERENCES builds(id),
                FOREIGN KEY(generation,ordinal) REFERENCES observations(generation,ordinal));
            CREATE TABLE build_cdn_config_regions(build_id TEXT NOT NULL,cdn_config TEXT NOT NULL,region TEXT NOT NULL,
                generation INTEGER NOT NULL,ordinal INTEGER NOT NULL,PRIMARY KEY(build_id,cdn_config,region),
                FOREIGN KEY(build_id,cdn_config) REFERENCES build_cdn_configs(build_id,cdn_config),
                FOREIGN KEY(generation,ordinal) REFERENCES observations(generation,ordinal));
            CREATE INDEX build_cdn_config_regions_latest ON build_cdn_config_regions(build_id,region,generation DESC,ordinal);
            CREATE TABLE baselines(product TEXT NOT NULL, region TEXT NOT NULL, generation INTEGER NOT NULL,
                PRIMARY KEY(product,region), FOREIGN KEY(product) REFERENCES products(product));
            CREATE TABLE advertisements(product TEXT NOT NULL, region TEXT NOT NULL, build_id TEXT NOT NULL,
                has_route INTEGER NOT NULL, generation INTEGER NOT NULL, cdn_config TEXT NOT NULL,
                PRIMARY KEY(product,region,build_id,cdn_config), FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE INDEX advertisements_build ON advertisements(build_id,region);
            PRAGMA user_version=2;
            """, transaction);
        transaction.Commit();
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
            // A republished CDN configuration joins the same build's history instead of making a new build.
            foreach (var cdnConfig in group.Select(row => row.CdnConfig).Distinct(StringComparer.Ordinal))
                Execute("""
                    INSERT INTO build_cdn_configs VALUES($id,$cdn,$time,$time,$g,$g)
                    ON CONFLICT(build_id,cdn_config) DO UPDATE SET last_seen=$time,last_generation=$g
                    """, transaction, ("$id", identity.Id), ("$cdn", cdnConfig), ("$time", completedAt), ("$g", generation));
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
            var cdnConfig = row.CdnConfig;
            Execute("""
                INSERT INTO observations(generation,ordinal,region,build_id,cdns_ordinal,original_fields,route_fields,cdn_config)
                VALUES($g,$ordinal,$region,$id,$cdn,$fields,$route,$config)
                """, transaction,
                ("$g", generation), ("$ordinal", row.VersionsOrdinal), ("$region", row.Region), ("$id", row.Identity.Id),
                ("$cdn", row.CdnsOrdinal), ("$fields", Serialize(row.OriginalFields)),
                ("$route", row.RouteFields is null ? null : Serialize(row.RouteFields)), ("$config", cdnConfig));
            Execute("""
                INSERT INTO build_regions VALUES($id,$region,$g,$ordinal,$cdn)
                ON CONFLICT(build_id,region) DO UPDATE SET generation=$g,ordinal=$ordinal,cdns_ordinal=$cdn
                    WHERE build_regions.generation<$g OR (build_regions.generation=$g AND build_regions.ordinal>$ordinal)
                """, transaction, ("$id", row.Identity.Id), ("$region", row.Region), ("$g", generation),
                ("$ordinal", row.VersionsOrdinal), ("$cdn", row.CdnsOrdinal));
            Execute("""
                INSERT INTO build_cdn_config_regions VALUES($id,$config,$region,$g,$ordinal)
                ON CONFLICT(build_id,cdn_config,region) DO UPDATE SET generation=$g,ordinal=$ordinal
                    WHERE build_cdn_config_regions.generation<$g OR (build_cdn_config_regions.generation=$g AND build_cdn_config_regions.ordinal>$ordinal)
                """, transaction, ("$id", row.Identity.Id), ("$config", cdnConfig), ("$region", row.Region), ("$g", generation),
                ("$ordinal", row.VersionsOrdinal));
            Execute("INSERT OR IGNORE INTO advertisements VALUES($p,$r,$id,$route,$g,$config)", transaction,
                ("$p", product), ("$r", row.Region), ("$id", row.Identity.Id), ("$route", row.CdnsOrdinal is null ? 0 : 1), ("$g", generation),
                ("$config", cdnConfig));
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
        var records = new List<BuildRecord>();
        using (var command = Command($"SELECT id,canonical,identity_fields,first_seen,last_seen,is_new AND NOT EXISTS(SELECT 1 FROM open_acknowledgements o WHERE o.build_id=b.id) FROM builds b WHERE product=$p AND id>$after AND ($exact IS NULL OR id=$exact){FilterPredicate(filter)} ORDER BY id LIMIT $limit", null,
            ("$p", product), ("$after", after), ("$exact", exactBuild), ("$limit", limit + 1)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) records.Add(new(reader.GetString(0), product, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        var builds = ProjectBuilds(records, limit, MaxBuildPageBytes, token);
        var more = records.Count > builds.Count;
        var next = more && builds.Count > 0 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(product, filter, generation, builds[^1].Id), Json)) : null;
        return new DiscoveryPage(product, builds, next, checkedAt, checkedRegions, errors, history, state);
    }

    /// <summary>Serialized build JSON one page may carry. A single larger build is refused rather than truncated.</summary>
    private const int MaxBuildPageBytes = 750_000;
    /// <summary>Serialized builds plus product statuses one cross-product page may carry, well inside the 1 MiB frame.</summary>
    internal const int MaxCatalogPageBytes = 900_000;
    private sealed record BuildRecord(string Id, string Product, string Canonical, string Fields, string First, string Last, bool New);
    private static string FilterPredicate(string filter) => filter switch
    {
        "local" => " AND (EXISTS(SELECT 1 FROM inventory_generations i WHERE i.build_id=b.id AND i.state='ready') OR EXISTS(SELECT 1 FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id WHERE a.build_id=b.id AND a.profile='home-download-v1' AND d.artifact_sha256 IS NOT NULL))",
        "advertised" => " AND EXISTS(SELECT 1 FROM advertisements a WHERE a.build_id=b.id)",
        _ => ""
    };
    /// <summary>Project up to limit records, stopping before the page would exceed its byte budget.</summary>
    private List<Build> ProjectBuilds(IReadOnlyList<BuildRecord> records, int limit, int budget, CancellationToken token)
    {
        var builds = new List<Build>();
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var size = 0;
        foreach (var row in records.Take(limit))
        {
            token.ThrowIfCancellationRequested();
            var build = ProjectBuild(row, verified, token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(build, Json).Length;
            if (size + bytes > budget)
            {
                if (builds.Count == 0) throw new DiscoveryException("RESULT_LIMIT", "This build's source projection exceeds the protocol limit.");
                break;
            }
            size += bytes; builds.Add(build);
        }
        return builds;
    }
    private Build ProjectBuild(BuildRecord row, HashSet<string> verified, CancellationToken token)
    {
        var fields = Deserialize<Dictionary<string, string>>(row.Fields);
        // Full canonical identity is retained and checked against its digest on every restore.
        var restored = BuildIdentity.FromFields(row.Product, fields);
        if (restored.Id != row.Id || restored.Canonical != row.Canonical)
            throw new DiscoveryException("CATALOG_INTEGRITY", "A saved build identity failed integrity verification.");
        var regions = new List<string>(); var advertised = new List<string>(); var routes = new List<string>();
        using (var command = Command("SELECT region FROM build_regions WHERE build_id=$id ORDER BY region", null, ("$id", row.Id)))
        using (var reader = command.ExecuteReader()) while (reader.Read()) regions.Add(reader.GetString(0));
        using (var command = Command("SELECT region,MAX(has_route) FROM advertisements WHERE build_id=$id GROUP BY region ORDER BY region", null, ("$id", row.Id)))
        using (var reader = command.ExecuteReader()) while (reader.Read())
        { advertised.Add(reader.GetString(0)); if (reader.GetBoolean(1)) routes.Add(reader.GetString(0)); }
        if (regions.Count == 0) throw new DiscoveryException("CATALOG_INTEGRITY", "Saved build observations are missing.");
        var receipts = ReadReceipts(row.Id, verified, token);
        var (cdnHistory, cdnHistoryCount) = ReadCdnConfigHistory(row.Id, verified, token);
        return new Build(row.Id, row.Product, fields["VersionsName"], fields.GetValueOrDefault("BuildId", ""),
            fields["BuildConfig"], cdnHistory[0].CdnConfig, fields.GetValueOrDefault("ProductConfig") is { Length: > 0 } productConfig ? productConfig : null, fields,
            regions, advertised, routes, row.First, row.Last, row.New, advertised.Count > 0, Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM inventory_generations WHERE build_id=$b AND state='ready') OR EXISTS(SELECT 1 FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id WHERE a.build_id=$b AND a.profile='home-download-v1' AND d.artifact_sha256 IS NOT NULL)", null, ("$b", row.Id))) == 1, "unknown", receipts, cdnHistory, cdnHistoryCount);
    }

    /// <summary>The saved outcome of a product's latest check. A product that was never checked is notChecked.</summary>
    private ProductDiscoveryStatus ReadProductStatus(string product)
    {
        string? checkedAt = null;
        string[] checkedRegions = [];
        DiscoveryProblem[] errors = [];
        var state = "notChecked";
        using (var command = Command("SELECT last_checked,checked_regions,errors,connection FROM products WHERE product=$p", null, ("$p", product)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                checkedAt = reader.IsDBNull(0) ? null : reader.GetString(0);
                checkedRegions = Deserialize<string[]>(reader.GetString(1));
                errors = Deserialize<DiscoveryProblem[]>(reader.GetString(2)); state = reader.GetString(3);
            }
        }
        var history = Convert.ToInt64(Scalar("SELECT EXISTS(SELECT 1 FROM builds WHERE product=$p)", null, ("$p", product))) == 1;
        return new(product, checkedAt, checkedRegions, errors, history, state);
    }

    /// <summary>
    /// Cross-product cursor. It binds the product filter, the visibility filter and the number of committed checks, so
    /// paging never mixes builds from before and after any product's commit. Starting a refresh does not change it.
    /// </summary>
    private sealed record CatalogCursor(string Scope, string? Product, string Filter, long Commits, int Ordinal, long Number, string After);
    private const string CatalogScope = "allProducts";
    public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) =>
        Schedule(() => ReadAllCore(product, filter, limit, cursor, token), token);
    private DiscoveryCatalogPage ReadAllCore(string? product, string filter, int limit, string? cursor, CancellationToken token)
    {
        if (product is not null) ProductRegistry.Validate(product);
        if (filter is not ("all" or "local" or "advertised") || limit is < 1 or > 100)
            throw new DiscoveryException("INVALID_REQUEST", "The discovery filter or page size is invalid.");
        var statuses = ProductRegistry.Products.Select(entry => ReadProductStatus(entry.Id)).ToArray();
        // Events are only ever inserted, one per committed check of any product.
        var commits = Convert.ToInt64(Scalar("SELECT count(*) FROM events"));
        (int Ordinal, long Number, string After)? position = null;
        if (cursor is not null)
        {
            CatalogCursor? decoded;
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                decoded = JsonSerializer.Deserialize<CatalogCursor>(Convert.FromBase64String(cursor), Json);
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            { throw new DiscoveryException("INVALID_CURSOR", "The discovery cursor is invalid."); }
            if (decoded is null || decoded.Scope != CatalogScope || decoded.Product != product || decoded.Filter != filter || decoded.Commits != commits)
                throw new DiscoveryException("STALE_CURSOR", "Discovery changed. Read the first page again.", true);
            if (decoded.After is not { Length: 64 } key || !key.All(Uri.IsHexDigit) || decoded.Ordinal < 0 ||
                decoded.Ordinal >= ProductRegistry.Products.Count || decoded.Number < -1)
                throw new DiscoveryException("INVALID_CURSOR", "The discovery cursor is invalid.");
            position = (decoded.Ordinal, decoded.Number, decoded.After);
        }
        // Registry product order, then the raw BuildId read as a number (newest first; missing or non-decimal last), then identity.
        var parameters = new List<(string, object?)> { ("$product", product), ("$start", position is null ? 1 : 0),
            ("$o", position?.Ordinal ?? 0), ("$n", position?.Number ?? 0), ("$after", position?.After ?? ""), ("$limit", limit + 1) };
        var ordinal = new StringBuilder("CASE b.product");
        for (var i = 0; i < ProductRegistry.Products.Count; i++)
        { ordinal.Append(" WHEN $p").Append(i).Append(" THEN ").Append(i); parameters.Add(($"$p{i}", ProductRegistry.Products[i].Id)); }
        ordinal.Append(" END");
        const string buildId = "json_extract(b.identity_fields,'$.BuildId')";
        const string number = $"CASE WHEN json_valid(b.identity_fields) AND typeof({buildId})='text' AND length({buildId}) BETWEEN 1 AND 18 " +
            $"AND {buildId} NOT GLOB '*[^0-9]*' THEN CAST({buildId} AS INTEGER) ELSE -1 END";
        var records = new List<(BuildRecord Record, int Ordinal, long Number)>();
        using (var command = Command($"""
            SELECT id,product,canonical,identity_fields,first_seen,last_seen,fresh,ord,number FROM (
                SELECT b.id,b.product,b.canonical,b.identity_fields,b.first_seen,b.last_seen,
                    b.is_new AND NOT EXISTS(SELECT 1 FROM open_acknowledgements o WHERE o.build_id=b.id) AS fresh,
                    {ordinal} AS ord,{number} AS number FROM builds b) b
            WHERE ord IS NOT NULL AND ($product IS NULL OR product=$product){FilterPredicate(filter)}
                AND ($start=1 OR ord>$o OR (ord=$o AND (number<$n OR (number=$n AND id>$after))))
            ORDER BY ord,number DESC,id LIMIT $limit
            """, null, parameters.ToArray()))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                records.Add((new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetBoolean(6)), reader.GetInt32(7), reader.GetInt64(8)));
            }
        var budget = Math.Min(MaxBuildPageBytes, MaxCatalogPageBytes - JsonSerializer.SerializeToUtf8Bytes(statuses, Json).Length);
        if (budget <= 0) throw new DiscoveryException("RESULT_LIMIT", "Saved product statuses exceed the protocol limit.");
        var builds = ProjectBuilds(records.Select(record => record.Record).ToArray(), limit, budget, token);
        string? next = null;
        if (records.Count > builds.Count && builds.Count > 0)
        {
            var last = records[builds.Count - 1];
            next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new CatalogCursor(CatalogScope, product, filter, commits, last.Ordinal, last.Number, last.Record.Id), Json));
        }
        return new DiscoveryCatalogPage(product, filter, builds, next, statuses, []);
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
    /// <summary>Every CDN configuration this build was advertised with, newest first, with the newest versions receipt per region.</summary>
    /// <summary>The projection carries at most this many newest CDN configurations, so republishing can never push one build past the page budget.</summary>
    internal const int MaxProjectedCdnConfigs = 16;
    private (List<CdnConfigObservation> History, int Count) ReadCdnConfigHistory(string buildId, HashSet<string> verified, CancellationToken token)
    {
        var total = Convert.ToInt32(Scalar("SELECT count(*) FROM build_cdn_configs WHERE build_id=$id", null, ("$id", buildId)));
        var configs = new List<(string Config, string First, string Last)>();
        // Newest = seen most recently; a tie in the same check goes to the one that appeared later.
        using (var command = Command("""
            SELECT CASE WHEN typeof(cdn_config)='text' AND length(cdn_config)=32 AND cdn_config NOT GLOB '*[^0-9a-f]*' THEN cdn_config END,
                first_seen,last_seen
            FROM build_cdn_configs WHERE build_id=$id
            ORDER BY last_generation DESC,first_generation DESC,cdn_config LIMIT $limit
            """, null, ("$id", buildId), ("$limit", MaxProjectedCdnConfigs)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) throw new DiscoveryException("CATALOG_INTEGRITY", "A saved CDN configuration identity is invalid.");
                configs.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        if (configs.Count == 0) throw new DiscoveryException("CATALOG_INTEGRITY", "Saved CDN configuration history is missing.");
        var history = new List<CdnConfigObservation>(configs.Count);
        var checkedGenerations = new HashSet<long>();
        foreach (var (config, first, last) in configs)
        {
            var regions = new List<string>(); var advertised = new List<string>(); var receipts = new List<SourceReceipt>();
            using (var command = Command("SELECT region FROM advertisements WHERE build_id=$id AND cdn_config=$c ORDER BY region", null, ("$id", buildId), ("$c", config)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) advertised.Add(reader.GetString(0));
            using (var command = Command($$"""
                SELECT r.region,d.url,d.retrieved_at,d.sha256,b.byte_length,r.ordinal,
                    CASE WHEN {{OpeningSourceRowValid}} THEN b.body END,r.generation,
                    CASE WHEN {{OpeningSourceRowValid}} AND d.role='versions' AND typeof(r.generation)='integer'
                        AND typeof(r.region)='text' AND length(CAST(r.region AS BLOB)) BETWEEN 2 AND 8
                        AND r.region NOT GLOB '*[^a-z]*' AND typeof(r.ordinal)='integer' AND r.ordinal BETWEEN 0 AND 9999
                        THEN 1 ELSE 0 END
                FROM build_cdn_config_regions r JOIN documents d ON d.generation=r.generation AND d.role='versions'
                    LEFT JOIN raw_documents b ON b.sha256=d.sha256
                WHERE r.build_id=$id AND r.cdn_config=$c ORDER BY r.region
                """, null, ("$id", buildId), ("$c", config)))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.GetInt32(8) != 1)
                        throw new DiscoveryException("CATALOG_INTEGRITY", "A saved CDN configuration receipt exceeds its actual byte/type or receipt bounds.");
                    var generation = reader.GetInt64(7);
                    if (checkedGenerations.Add(generation)) ValidateOpeningSourceDocuments(generation, token, requireCdns: false);
                    var hash = reader.GetString(3); var length = reader.GetInt32(4);
                    if (verified.Add(hash))
                    {
                        var body = (byte[])reader[6];
                        if (body.Length != length || Convert.ToHexStringLower(SHA256.HashData(body)) != hash)
                            throw new DiscoveryException("CATALOG_INTEGRITY", "Saved source metadata failed integrity verification.");
                    }
                    regions.Add(reader.GetString(0));
                    receipts.Add(new(reader.GetString(0), "versions", reader.GetString(1), reader.GetString(2), hash, length, reader.GetInt32(5)));
                }
            if (receipts.Count == 0) throw new DiscoveryException("CATALOG_INTEGRITY", "A saved CDN configuration has no original receipt.");
            history.Add(new(config, first, last, regions, advertised, receipts));
        }
        return (history, total);
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
