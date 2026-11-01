using System.Diagnostics;
using System.Runtime.InteropServices;
using VibeMiner.Builds;
using VibeMiner.Formats;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Primitives.Tests.Generated;

// Record file benchmarks over a real GeneratedStore (#179). Synthetic table-like rows only; never reads a profile, a game install or the CDN.
// Usage: VibeMiner.GeneratedStore.Benchmark <200k|1m> [parent folder, default the system temp folder] [cold] [tables]. The store goes in
// a new folder under the parent that is deleted afterwards. "cold" (Linux) drops the store's files from the page cache before
// reopening. "tables" measures layer 5's GeneratedTables (Db2Row pages) instead of bare record files.
var counts = new Dictionary<string, int> { ["200k"] = 200_000, ["1m"] = 1_000_000 };
if (args.Length < 1 || !counts.TryGetValue(args[0].ToLowerInvariant(), out var count)) { Console.Error.WriteLine("usage: <200k|1m> [parent folder] [cold] [tables]"); return 2; }
var cold = args.Skip(1).Contains("cold");
var tables = args.Skip(1).Contains("tables");
var parent = args.Skip(1).FirstOrDefault(a => a is not ("cold" or "tables")) is { } p ? Path.GetFullPath(p) : Path.GetTempPath();
var dir = Path.Combine(parent, "vibeminer-bench-" + Guid.NewGuid().ToString("N"));
Console.WriteLine($"{count:N0} records in {dir}{(cold ? ", cold" : "")}{(tables ? ", tables" : "")}, {Environment.ProcessorCount} CPUs, .NET {Environment.Version}");
try
{
    var folder = Path.Combine(dir, GeneratedStore.DefaultFolderName);
    if (tables) await Tables(folder, count, cold); else await Run(folder, count, cold);
}
finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
return 0;

// Flushes each file and asks the kernel to drop its cached pages (posix_fadvise DONTNEED; no root needed for our own files).
static void DropCache(string folder)
{
    foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Flush(flushToDisk: true);
        if (Native.posix_fadvise((int)fs.SafeFileHandle.DangerousGetHandle(), 0, 0, 4) != 0) throw new InvalidOperationException($"fadvise failed on {path}");
    }
}

static async Task Run(string folder, int count, bool cold)
{
    var key = GeneratedKey.Hash("benchmark-table"u8);
    var records = SyntheticRows.Generate(count).ToList();
    var ids = records.Select(r => r.Id).ToArray();

    RecordFileInfo info;
    double writeSeconds;
    await using (var store = await GeneratedStore.OpenAsync(folder))
    {
        var sw = Stopwatch.StartNew();
        using (var writer = new RecordFileWriter(store))
        {
            foreach (var (id, bytes) in records) await writer.AddAsync(id, bytes);
            info = await writer.CompleteAsync(key, "{\"table\":\"synthetic\"}"u8.ToArray());
        }
        writeSeconds = sw.Elapsed.TotalSeconds;
    }
    var stored = info.CompressedBytes + info.IdIndexBytes + info.ManifestBytes;
    Console.WriteLine($"write {writeSeconds:F2} s, {info.UncompressedBytes / 1e6 / writeSeconds:F1} MB/s of records");
    Console.WriteLine($"records {info.UncompressedBytes:N0} B, stored {stored:N0} B (blocks {info.CompressedBytes:N0}, id index {info.IdIndexBytes:N0}, manifest {info.ManifestBytes:N0}), ratio {(double)info.UncompressedBytes / stored:F2}x");
    Console.WriteLine($"id index {(double)info.IdIndexBytes / info.Count:F2} B/record; {info.Blocks} blocks, {info.IdChunks} id chunks, {info.Segments} segments");

    // Reopen: a fresh store and record file, page cache warm (the data was just written) unless cold.
    if (cold) DropCache(folder);
    var sw2 = Stopwatch.StartNew();
    await using var reopened = await GeneratedStore.OpenAsync(folder);
    Console.WriteLine($"store open {sw2.Elapsed.TotalMilliseconds:F2} ms");
    var opens = new double[21];
    RecordFile? file = null;
    for (var i = 0; i < opens.Length; i++)
    {
        var t = Stopwatch.GetTimestamp();
        var open = await RecordFile.OpenAsync(reopened, key);
        opens[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        if (open.State != GeneratedReadState.Present) throw new InvalidOperationException($"open: {open.State} {open.Problem}");
        file = open.File;
    }
    Console.WriteLine($"RecordFile.OpenAsync first {opens[0]:F2} ms, median of the next 20 {opens.Skip(1).Order().ElementAt(10):F2} ms");

    var f = file!;
    async Task Page(Random r)
    {
        var page = await f.ReadAsync(r.NextInt64(f.Count - 50 + 1), 50);
        if (page.Count != 50) throw new InvalidOperationException("short page");
    }
    async Task Find(Random r)
    {
        var id = ids[r.Next(ids.Length)];
        var found = await f.FindAsync(id);
        if (found?.Id != id) throw new InvalidOperationException($"id {id} not found");
    }

    await Report(cold, Page, Find, "page 50");
}

// Layer 5: Db2Rows (25 string values, the SyntheticRows shape) stored through GeneratedTables, then 50-row pages and finds.
static async Task Tables(string folder, int count, bool cold)
{
    var source = new GeneratedTableSource(new BuildRef("wow", "0123456789abcdef0123456789abcdef", "fedcba9876543210fedcba9876543210"), "enUS",
        "00112233445566778899aabbccddeeff", "benchmark-definition", new Db2Limits());
    var copies = count / 50; // 2% copies, after the physical rows as the reader orders them
    var physical = count - copies;
    var ids = new uint[count];
    long textBytes = 0;
    async IAsyncEnumerable<Db2Row> Rows()
    {
        var sources = new List<Db2Row>(copies);
        var pick = new Random(5);
        var i = 0;
        foreach (var (id, bytes) in SyntheticRows.Generate(physical))
        {
            textBytes += bytes.Length;
            var row = new Db2Row(id, false, null, System.Text.Encoding.UTF8.GetString(bytes).Split('\t'));
            if (sources.Count < copies && pick.Next(40) == 0) sources.Add(row);
            ids[i++] = id;
            yield return row;
        }
        while (sources.Count < copies) sources.Add(sources[pick.Next(sources.Count)]); // a row can be the source of several copies
        var next = ids[physical - 1];
        foreach (var s in sources)
        {
            next += (uint)pick.Next(1, 4);
            string[] values = [.. s.Values];
            values[0] = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ids[i++] = next;
            yield return new Db2Row(next, true, s.Id, values);
        }
        await Task.CompletedTask;
    }
    var header = new Db2TableHeader(5, "11223344", "01020304", physical, copies, 100,
        [.. Enumerable.Range(0, 25).Select(c => new Db2Column(c == 0 ? "ID" : $"Field{c}", c is >= 1 and <= 3 ? "string" : "int", c == 0))], "WDC5");
    var content = new GeneratedTableContent(header, [new Db2Section(0, physical, copies, "0000000000000000", false)],
        new Db2Completion(count, physical, copies, 0, 0, 0, "complete", 0), [], Rows());

    double storeSeconds;
    await using (var store = await GeneratedStore.OpenAsync(folder))
    {
        var sw = Stopwatch.StartNew();
        var stored = await new GeneratedTables(store).StoreAsync(source, TactKeyStatus.None, content);
        storeSeconds = sw.Elapsed.TotalSeconds;
        if (stored.RowCount != count) throw new InvalidOperationException($"stored {stored.RowCount} rows");
        var stats = await store.StatsAsync();
        Console.WriteLine($"store {storeSeconds:F2} s, {count / storeSeconds:N0} rows/s, {textBytes / 1e6 / storeSeconds:F1} MB/s of row text ({textBytes:N0} B); store holds {stats.LiveBytes:N0} B live, ratio {(double)textBytes / stats.LiveBytes:F2}x");
    }

    if (cold) DropCache(folder);
    var sw2 = Stopwatch.StartNew();
    await using var reopened = await GeneratedStore.OpenAsync(folder);
    Console.WriteLine($"store open {sw2.Elapsed.TotalMilliseconds:F2} ms");
    var generated = new GeneratedTables(reopened);
    var opens = new double[21];
    GeneratedTable? table = null;
    for (var i = 0; i < opens.Length; i++)
    {
        var t = Stopwatch.GetTimestamp();
        table = await generated.OpenAsync(source, TactKeyStatus.None) ?? throw new InvalidOperationException("the table didn't open");
        opens[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }
    Console.WriteLine($"GeneratedTables.OpenAsync first {opens[0]:F2} ms, median of the next 20 {opens.Skip(1).Order().ElementAt(10):F2} ms");

    var tb = table!;
    async Task Page(Random r)
    {
        var page = await tb.ReadPageAsync(r.NextInt64(tb.RowCount - 50 + 1), 50);
        if (page.Count != 50 || page[0].Values.Count != 25) throw new InvalidOperationException("short page");
    }
    async Task Find(Random r)
    {
        var id = ids[r.Next(ids.Length)];
        var found = await tb.FindAsync(id);
        if (found?.Id != id) throw new InvalidOperationException($"id {id} not found");
    }
    await Report(cold, Page, Find, "page 50 rows");
}

static async Task Report(bool cold, Func<Random, Task> Page, Func<Random, Task> Find, string pageName)
{
    if (cold)
    {
        // No warm-up: the first 500 random pages, most of which touch a block not read yet.
        var first = await Measure(Page, 1, 500);
        Array.Sort(first);
        Console.WriteLine($"cold pages, first 500: p50 {first[250]:F1} µs, p90 {first[450]:F1} µs, p99 {first[495]:F1} µs, max {first[^1]:F1} µs");
    }
    Console.WriteLine($"GC: {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");
    Console.WriteLine("| op | callers | n | p50 µs | p90 µs | p99 µs | max µs | KiB alloc/op | gen0/1/2 GCs | GC pause ms |");
    Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|");
    foreach (var (name, op) in new (string, Func<Random, Task>)[] { (pageName, Page), ("find", Find) })
        foreach (var callers in new[] { 1, 8 })
        {
            await Measure(op, callers, 2_000); // warm-up
            var (allocated, g0, g1, g2, paused) = (GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
            var samples = await Measure(op, callers, 20_000);
            var perOp = (GC.GetTotalAllocatedBytes(true) - allocated) / 1024.0 / samples.Length;
            var gcs = $"{GC.CollectionCount(0) - g0}/{GC.CollectionCount(1) - g1}/{GC.CollectionCount(2) - g2} | {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:F0}";
            Array.Sort(samples);
            double P(double q) => samples[Math.Min(samples.Length - 1, (int)(q * samples.Length))];
            Console.WriteLine($"| {name} | {callers} | {samples.Length:N0} | {P(0.5):F1} | {P(0.9):F1} | {P(0.99):F1} | {samples[^1]:F1} | {perOp:F1} | {gcs} |");
        }
}

// Each caller runs perCaller operations back to back; returns every operation's latency in µs.
static async Task<double[]> Measure(Func<Random, Task> op, int callers, int perCaller)
{
    var all = new double[callers * perCaller];
    await Task.WhenAll(Enumerable.Range(0, callers).Select(c => Task.Run(async () =>
    {
        var r = new Random(1000 + c);
        for (var i = 0; i < perCaller; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await op(r);
            all[c * perCaller + i] = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
        }
    })));
    return all;
}

internal static class Native
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int posix_fadvise(int fd, long offset, long len, int advice);
}
