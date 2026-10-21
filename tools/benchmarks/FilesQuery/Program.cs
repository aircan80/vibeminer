using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Infrastructure;

// Explicit developer workload: Files query latency over a retail-sized inventory (D068). It copies the ROOT and install
// memberships of one retained app-owned inventory generation (opened read-only and immutable) into a fresh catalog in
// the scratch directory, with synthetic EKeys, sizes and stored marks, then times each filter. No HTTP, no game files.
if (args.Length != 3) throw new ArgumentException("Usage: <source-app-data-dir> <source-generation> <new-scratch-dir>");
var source = Path.Combine(Path.GetFullPath(args[0]), "discovery-v1.sqlite3");
var sourceGeneration = long.Parse(args[1], CultureInfo.InvariantCulture);
var scratch = Path.GetFullPath(args[2]);
if (Directory.Exists(scratch)) throw new ArgumentException("Choose a new scratch directory.");
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(30)); var token = stop.Token;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var json = new JsonSerializerOptions { WriteIndented = true };
var report = new Dictionary<string, object>(); var timer = Stopwatch.StartNew();
const string Build = "0000000000000000000000000000000000000000000000000000000000000020";
var catalogDirectory = Path.Combine(scratch, "catalog");

await using (var catalog = await DiscoveryCatalog.OpenAsync(catalogDirectory, token)) { }
long rows = 0, stored = 0;
using (var from = new SqliteConnection($"Data Source=file:{Uri.EscapeDataString(source).Replace("%2F", "/")}?immutable=1;Mode=ReadOnly;Pooling=False"))
using (var to = new SqliteConnection($"Data Source={Path.Combine(catalogDirectory, "discovery-v1.sqlite3")};Pooling=False"))
{
    from.Open(); to.Open();
    using (var setup = to.CreateCommand())
    {
        setup.CommandText = $"""
            PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;
            INSERT INTO builds VALUES('{Build}','wow','benchmark','{"{}"}','2026-10-20T00:00:00Z','2026-10-20T00:00:00Z',1,1,0);
            INSERT INTO inventory_generations(id,build_id,region,state,profile,summary,metadata,source) VALUES(1,'{Build}','us','ready','home-inventory-v1','{"{}"}','[]','{"{}"}');
            """;
        setup.ExecuteNonQuery();
    }
    using var read = from.CreateCommand();
    read.CommandText = "SELECT id,family,name,fdid,ckey,locale,flags,name_hash,raw FROM inventory_origins WHERE g=$g AND family IN ('root','install') ORDER BY id";
    read.Parameters.AddWithValue("$g", sourceGeneration);
    using var insert = to.CreateCommand();
    insert.CommandText = "INSERT INTO inventory_origins(g,id,family,name,fdid,ckey,width,locale,flags,name_hash,raw,ekey,encoded,decoded,fdid_n,locale_n,flags_n,grp,stored) VALUES(1,$i,$f,$n,$d,$c,0,$l,$a,$h,$raw,$e,$enc,$dec,$dn,$ln,$an,$grp,$s)";
    foreach (var name in new[] { "$i", "$f", "$n", "$d", "$c", "$l", "$a", "$h", "$raw", "$e", "$enc", "$dec", "$dn", "$ln", "$an", "$grp", "$s" }) insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
    insert.Prepare();
    var groups = new Dictionary<string, long>(StringComparer.Ordinal);
    var tx = to.BeginTransaction(); insert.Transaction = tx;
    using var r = read.ExecuteReader();
    while (r.Read())
    {
        token.ThrowIfCancellationRequested();
        var id = r.GetInt64(0); var family = r.GetString(1); var ckey = r.GetString(4); var root = family == "root";
        // Deterministic synthetic sizes and a 2% stored sample keyed by the CKey, so equal contents agree.
        var hash = BitConverter.ToUInt32(MD5.HashData(Encoding.ASCII.GetBytes(ckey)));
        var isStored = hash % 50 == 0; if (isStored) stored++;
        long? group = root ? null : groups.TryGetValue(r.GetString(2), out var first) ? first : groups[r.GetString(2)] = id;
        object?[] values = [id, family, r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), ckey, r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), ckey, 64 + hash % 4_000_000, 32 + hash % 4_000_000,
            root ? long.Parse(r.GetString(3), CultureInfo.InvariantCulture) : null, root ? long.Parse(r.GetString(5), CultureInfo.InvariantCulture) : null,
            root ? long.Parse(r.GetString(6), CultureInfo.InvariantCulture) : null, group, isStored ? 1 : 0];
        for (var i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
        insert.ExecuteNonQuery();
        if (++rows % 200_000 == 0) { tx.Commit(); tx.Dispose(); tx = to.BeginTransaction(); insert.Transaction = tx; }
    }
    tx.Commit(); tx.Dispose();
    using var checkpoint = to.CreateCommand(); checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; checkpoint.ExecuteNonQuery();
}
report["copiedMemberships"] = rows; report["storedSample"] = stored; report["copySeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 1);

await using (var catalog = await DiscoveryCatalog.OpenAsync(catalogDirectory, token))
{
    timer.Restart(); await catalog.RecordFileStatsAsync(1, token);
    report["statsSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
    report["catalogBytes"] = new FileInfo(Path.Combine(catalogDirectory, "discovery-v1.sqlite3")).Length;
    report["budgetRows"] = catalog.FilesExaminationBudget;
    var cases = new (string Name, InventoryFileFilter Filter)[]
    {
        ("all files", new()), ("family install", new(Family: "install")), ("FileDataID exact", new(FileDataId: Median(catalogDirectory))),
        ("CKey exact, most shared", new(ContentKey: MostShared(catalogDirectory))), ("CKey prefix 6", new(ContentKey: "a1b2c3")), ("CKey prefix 4", new(ContentKey: "a1b2")),
        ("locale enUS 0x2", new(LocaleFlags: "2")), ("locale enGB 0x200", new(LocaleFlags: "512")), ("locale 0x400 (absent)", new(LocaleFlags: "1024")),
        ("flags none (absent)", new(ContentFlags: "none")), ("flags 0x08000000", new(ContentFlags: "134217728")), ("flags 0x80", new(ContentFlags: "128")),
        ("flags 0x4", new(ContentFlags: "4")), ("flags 0x10000 (one row)", new(ContentFlags: "65536")),
        ("stored 2%", new(StoredOnly: true)), ("stored 2% + flags 0x80", new(ContentFlags: "128", StoredOnly: true)),
        ("stored 2% + flags 0x80000000", new(ContentFlags: "2147483648", StoredOnly: true)),
        ("enGB + 0x08000000 + stored 2%", new(LocaleFlags: "512", ContentFlags: "134217728", StoredOnly: true)),
    };
    var results = new List<object>();
    foreach (var (name, filter) in cases) results.Add(await Measure(catalog, name, filter, token));
    // Every membership stored: the stored filter becomes dense.
    using (var db = new SqliteConnection($"Data Source={Path.Combine(catalogDirectory, "discovery-v1.sqlite3")};Pooling=False"))
    { db.Open(); using var all = db.CreateCommand(); all.CommandText = "UPDATE inventory_origins SET stored=1 WHERE g=1"; all.ExecuteNonQuery(); }
    timer.Restart(); await catalog.RecordFileStatsAsync(1, token); report["statsAllStoredSeconds"] = Math.Round(timer.Elapsed.TotalSeconds, 2);
    foreach (var (name, filter) in new (string, InventoryFileFilter)[] { ("stored 100%", new(StoredOnly: true)), ("stored 100% + flags 0x10000", new(ContentFlags: "65536", StoredOnly: true)), ("stored 100% + flags 0x80", new(ContentFlags: "128", StoredOnly: true)) })
        results.Add(await Measure(catalog, name, filter, token));
    var inspect = new List<double>();
    foreach (var fdid in new[] { "1", "1000000", "3000000", "5000000" })
    {
        var t = Stopwatch.StartNew();
        try { await catalog.ReadInventoryFileAsync(new("wow", Build, "us", "1", "root:" + fdid, new(LocaleFlags: "2"), 100), token); } catch (VibeMiner.Domain.DiscoveryException) { }
        inspect.Add(Math.Round(t.Elapsed.TotalMilliseconds, 2));
    }
    report["inspectorMs"] = inspect;
    report["queries"] = results;
}
report["peakWorkingSetMiB"] = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
Console.WriteLine(JsonSerializer.Serialize(report, json));

// First page five times (median and max), then up to 200 pages or the end, counting scan-limited pages.
static async Task<object> Measure(DiscoveryCatalog catalog, string name, InventoryFileFilter filter, CancellationToken token)
{
    var first = new List<double>(); string? error = null; InventoryFilesPageSummary? summary = null;
    for (var run = 0; run < 5; run++)
    {
        var t = Stopwatch.StartNew();
        try { var page = await catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", filter, 50), token); summary ??= new(page.Entries.Count, page.ScanLimited, page.TotalCount); }
        catch (VibeMiner.Domain.DiscoveryException e) { error = e.Code; }
        first.Add(t.Elapsed.TotalMilliseconds);
    }
    first.Sort();
    if (error is not null) return new { name, error, firstPageMedianMs = Math.Round(first[2], 2) };
    string? after = null; int pages = 0, limited = 0, files = 0; double slowest = 0; var all = Stopwatch.StartNew();
    do
    {
        var t = Stopwatch.StartNew();
        var page = await catalog.ReadInventoryFilesAsync(new("wow", Build, "us", "1", filter, 50, after), token);
        slowest = Math.Max(slowest, t.Elapsed.TotalMilliseconds); pages++; files += page.Entries.Count; if (page.ScanLimited) limited++;
        after = page.NextCursor;
    } while (after is not null && pages < 200);
    return new { name, firstPageMedianMs = Math.Round(first[2], 2), firstPageMaxMs = Math.Round(first[^1], 2), firstPageFiles = summary!.Files, firstPageScanLimited = summary.Limited,
        total = summary.Total, pagesRead = pages, reachedEnd = after is null, scanLimitedPages = limited, filesRead = files, slowestPageMs = Math.Round(slowest, 2),
        meanPageMs = Math.Round(all.Elapsed.TotalMilliseconds / pages, 2) };
}
static string MostShared(string directory)
{
    using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "discovery-v1.sqlite3")};Mode=ReadOnly;Pooling=False"); db.Open();
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT ckey FROM inventory_origins WHERE g=1 AND family='root' GROUP BY ckey ORDER BY count(*) DESC LIMIT 1";
    return (string)cmd.ExecuteScalar()!;
}
static string Median(string directory)
{
    using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "discovery-v1.sqlite3")};Mode=ReadOnly;Pooling=False"); db.Open();
    using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT fdid FROM inventory_origins WHERE g=1 AND family='root' ORDER BY id LIMIT 1 OFFSET 1600000";
    return (string)cmd.ExecuteScalar()!;
}
internal sealed record InventoryFilesPageSummary(int Files, bool Limited, string? Total);
