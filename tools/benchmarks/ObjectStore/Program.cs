using System.Diagnostics;
using System.Security.Cryptography;
using VibeMiner.Objects;

// Layer 1 benchmarks (#153). Synthetic headerless BLTE objects only; never reads a profile, a game install or the CDN.
// Usage: VibeMiner.ObjectStore.Benchmark <append|recovery|lookups> <new empty folder> [millions for lookups]
if (args.Length < 2) { Console.Error.WriteLine("usage: <append|recovery> <new empty folder> | lookups <new empty folder> [millions, default 3] [rebuild] [scan]"); return 2; }
var dir = Path.GetFullPath(args[1]);
if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) { Console.Error.WriteLine("The folder must be new or empty."); return 2; }
Console.WriteLine($"{args[0]} in {dir}, {Environment.ProcessorCount} CPUs, .NET {Environment.Version}");
switch (args[0])
{
    case "append": await Append(dir); break;
    case "recovery": await Recovery(dir); break;
    case "lookups": await Lookups(dir, args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 3, args.Contains("rebuild"), args.Contains("scan")); break;
    default: Console.Error.WriteLine("unknown command"); return 2;
}
return 0;

// The owner-profile size shape from phase 0: p50 23 KiB, p90 74 KiB, p99 800 KiB, capped at 16 MiB here.
static long Size(Random r)
{
    (double q, double v)[] q = [(0, 64), (0.5, 23 * 1024), (0.9, 74 * 1024), (0.99, 800 * 1024), (0.999, 8 << 20), (1, 16 << 20)];
    var u = r.NextDouble();
    for (var i = 1; i < q.Length; i++)
        if (u <= q[i].q) { var t = (u - q[i - 1].q) / (q[i].q - q[i - 1].q); return Math.Max(9, (long)Math.Exp(Math.Log(q[i - 1].v) + t * (Math.Log(q[i].v) - Math.Log(q[i - 1].v)))); }
    return 16 << 20;
}

static PutObject Object(byte[] pool, long length, long seed)
{
    var b = new byte[length];
    pool.AsSpan((int)(seed * 7919 % (pool.Length - length)), (int)length).CopyTo(b);
    "BLTE"u8.CopyTo(b); b.AsSpan(4, 4).Clear(); b[8] = (byte)'N';
    BitConverter.TryWriteBytes(b.AsSpan(9, Math.Min(8, (int)length - 9)), seed);
    return new(new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b)), b.Length, b);
}

static async Task Append(string dir)
{
    var pool = new byte[64 << 20]; new Random(7).NextBytes(pool);
    var rng = new Random(42);
    var objects = Enumerable.Range(0, 20_000).Select(i => Object(pool, Size(rng), i)).ToList();
    var bytes = objects.Sum(o => o.Bytes.Length);
    Console.WriteLine($"objects {objects.Count}, {bytes >> 20} MiB, mean {bytes / objects.Count / 1024.0:F1} KiB; default commit rule (128 objects or 16 MiB, 250 ms)");
    Console.WriteLine("| callers | batch | s | obj/s | MiB/s |");
    Console.WriteLine("|---|---|---|---|---|");
    foreach (var (callers, batch) in new[] { (1, 128), (8, 16), (32, 4) })
    {
        var run = Path.Combine(dir, $"append-{callers}");
        await using var store = await ObjectStore.OpenAsync(run);
        var sw = Stopwatch.StartNew();
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, callers).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var start = Interlocked.Add(ref next, batch) - batch + 1;
                if (start >= objects.Count) return;
                await store.PutAsync(objects.Skip(start).Take(batch).ToList());
            }
        })));
        var s = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"| {callers} | {batch} | {s:F1} | {objects.Count / s:F0} | {bytes / 1048576.0 / s:F0} |");
    }
}

static async Task Recovery(string dir)
{
    var pool = new byte[64 << 20]; new Random(7).NextBytes(pool);
    var rng = new Random(43);
    var store = Path.Combine(dir, "store");
    long written = 0; var n = 0;
    await using (var s = await ObjectStore.OpenAsync(store, new ObjectStoreOptions { MaxPackBytes = 1L << 30 }))
        while (written < (4L << 30))
        {
            var batch = Enumerable.Range(0, 128).Select(_ => Object(pool, Size(rng), n++)).ToList();
            written += batch.Sum(o => o.Bytes.Length);
            await s.PutAsync(batch);
        }
    // A full commit window past the last commit: synced but never committed, as a kill after the pack sync leaves it.
    var stop = 0;
    var crash = new ObjectStoreOptions { MaxPackBytes = 1L << 30, CommitDelay = TimeSpan.FromHours(1), Fault = p => { if (p == StorePoint.AfterPackSync && Interlocked.Increment(ref stop) == 1) throw new SimulatedCrashException(p); } };
    var crashed = await ObjectStore.OpenAsync(store, crash);
    long tail = 0; var tailObjects = new List<PutObject>();
    while (tail < 16L << 20) { var o = Object(pool, Size(rng), n++); tail += o.Bytes.Length; tailObjects.Add(o); }
    try { await crashed.PutAsync(tailObjects, commitNow: true); } catch (ObjectStoreException) { }
    var packs = Directory.GetFiles(Path.Combine(store, "packs"));
    Console.WriteLine($"store {written >> 20} MiB in {packs.Length} packs of up to 1 GiB, {n} objects; tail {tailObjects.Count} objects, {tail >> 20} MiB uncommitted");
    var sw = Stopwatch.StartNew();
    await using (var s = await ObjectStore.OpenAsync(store))
        Console.WriteLine($"| reopen with tail recovery | {sw.Elapsed.TotalSeconds:F2} s | recovered {s.OpenReport.RecoveredEntries} entries, truncated {s.OpenReport.TruncatedBytes} B |");
    sw.Restart();
    await using (var s = await ObjectStore.OpenAsync(store))
    {
        Console.WriteLine($"| clean reopen | {sw.Elapsed.TotalSeconds:F2} s | |");
        sw.Restart();
        var r = await s.ScanAsync();
        Console.WriteLine($"| Scan (rebuild index, SHA-256 + blocks of every entry) | {sw.Elapsed.TotalSeconds:F2} s | {r.Entries} entries, {r.Live} live |");
        sw.Restart();
        var v = await s.VerifyAsync();
        Console.WriteLine($"| Verify (scrub every object) | {sw.Elapsed.TotalSeconds:F2} s | {v.Checked} checked, {v.CorruptCount} corrupt |");
    }
    foreach (var f in Directory.GetFiles(store, "objects.sqlite3*")) File.Delete(f);
    sw.Restart();
    await using (var s = await ObjectStore.OpenAsync(store))
        Console.WriteLine($"| open with the index deleted (rebuild from packs) | {sw.Elapsed.TotalSeconds:F2} s | rebuilt {s.OpenReport.IndexRebuilt} |");
    Console.WriteLine("Page cache warm: the packs were just written. Cold numbers need a cache drop, which needs root.");
}

static async Task Lookups(string dir, int millions, bool rebuild, bool scan)
{
    // The shared scale store (tests/backend/VibeMiner.Objects.CrashWriter/ScaleStore.cs): built once per size, reused after.
    var repository = AppContext.BaseDirectory;
    while (!File.Exists(Path.Combine(repository, "VibeMiner.slnx"))) repository = Path.GetDirectoryName(repository.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new InvalidOperationException("Run from a checkout.");
    var total = millions * 1_000_000L;
    var run = Stopwatch.StartNew();
    var store = await VibeMiner.Objects.Testing.ScaleStore.EnsureAsync(repository, millions, rebuild, Console.WriteLine);
    var indexBytes = new FileInfo(Path.Combine(store, "objects.sqlite3")).Length;
    Console.WriteLine($"{total:N0} objects; index {indexBytes >> 20} MiB, {indexBytes / (double)total:F1} B/row");
    var sw = Stopwatch.StartNew();
    await using (var s = await ObjectStore.OpenReadOnlyAsync(store))
    {
        Console.WriteLine($"| open after a clean close | {sw.Elapsed.TotalSeconds * 1000:F0} ms | cheap check: schema, store and pack rows |");
        var rng = new Random(5);
        for (var i = 0; i < 200_000; i++) await s.HasAsync(VibeMiner.Objects.Testing.ScaleStore.Tiny((long)(rng.NextDouble() * total)).Key);  // warm the page cache
        foreach (var (label, absent) in new[] { ("Has, present key", false), ("Has, absent key", true) })
        {
            var lat = new double[200_000];
            for (var i = 0; i < lat.Length; i++)
            {
                var key = absent ? new ObjectKey(ObjectKeyType.Encoded, RandomNumberGenerator.GetBytes(16)) : VibeMiner.Objects.Testing.ScaleStore.Tiny((long)(rng.NextDouble() * total)).Key;
                var t = Stopwatch.GetTimestamp();
                if (await s.HasAsync(key) == absent) throw new InvalidOperationException("wrong answer");
                lat[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            }
            Array.Sort(lat);
            Console.WriteLine($"| {label} | p50 {lat[lat.Length / 2]:F1} µs | p99 {lat[lat.Length * 99 / 100]:F1} µs |");
        }
        var reads = new double[100_000];
        for (var i = 0; i < reads.Length; i++)
        {
            var key = VibeMiner.Objects.Testing.ScaleStore.Tiny((long)(rng.NextDouble() * total)).Key;
            var t = Stopwatch.GetTimestamp();
            if ((await s.ReadAsync(key)).State != ReadState.Present) throw new InvalidOperationException("missing");
            reads[i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
        }
        Array.Sort(reads);
        Console.WriteLine($"| Read, 17-byte object | p50 {reads[reads.Length / 2]:F1} µs | p99 {reads[reads.Length * 99 / 100]:F1} µs |");
    }
    // After an unclean shutdown the open runs the full check (integrity_check, totals over every row): a killed writer on a copy.
    var crashCopy = await VibeMiner.Objects.Testing.ScaleStore.CopyAsync(store, Path.Combine(dir, "unclean"));
    var killed = await ObjectStore.OpenAsync(crashCopy);
    killed.Kill();
    sw.Restart();
    await using (var unclean = await ObjectStore.OpenAsync(crashCopy))
        Console.WriteLine($"| open after an unclean shutdown | {sw.Elapsed.TotalSeconds:F1} s | full check ran: {unclean.OpenReport.FullIndexCheck} |");
    if (scan)
    {
        // Scan writes, so it runs on a reflink copy in the work folder, never on the cache.
        var copy = await VibeMiner.Objects.Testing.ScaleStore.CopyAsync(store, Path.Combine(dir, "scan"));
        sw.Restart();
        await using var w = await ObjectStore.OpenAsync(copy);
        var r = await w.ScanAsync();
        Console.WriteLine($"| Scan rebuild | {sw.Elapsed.TotalSeconds:F0} s | {r.Live:N0} live, {r.Corrupt} corrupt |");
    }
    Console.WriteLine($"whole run {run.Elapsed.TotalSeconds:F0} s");
}
