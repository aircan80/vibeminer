// Live measurement of layer 2 object access against the official CDN (#154). Developer-only; never part of npm test.
// Usage: dotnet VibeMiner.ObjectAccess.Benchmark.dll <empty-folder> [product] [region] [gapKiB...]
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Benchmark;
using VibeMiner.Objects;
using VibeMiner.Protocols.Tact;

if (args.Length < 1) { Console.Error.WriteLine("Usage: <empty-folder> [product=wow] [region=us] [gapKiB ...=256 1024]"); return 2; }
var folder = Path.GetFullPath(args[0]);
var product = args.Length > 1 ? args[1] : "wow";
var region = args.Length > 2 ? args[2] : "us";
var gaps = args.Length > 3 ? args[3..].Select(long.Parse).ToArray() : [256, 1024];
Directory.CreateDirectory(folder);
var ct = CancellationToken.None;

using var cdnHandler = CdnClient.CreateHandler(40);
using var cdnHttp = new HttpClient(cdnHandler) { Timeout = Timeout.InfiniteTimeSpan };
using var versionHandler = CdnClient.CreateHandler(4);
using var versionHttp = new HttpClient(versionHandler) { Timeout = Timeout.InfiniteTimeSpan };
var counting = new CountingCdnClient(new CdnClient(cdnHttp));
using var interactiveHandler = CdnClient.CreateHandler(8);
using var interactiveHttp = new HttpClient(interactiveHandler) { Timeout = Timeout.InfiniteTimeSpan };
var interactiveCdn = new CdnClient(interactiveHttp);
var split = Environment.GetEnvironmentVariable("VIBEMINER_BENCH_SHARED_CONNECTIONS") != "1";
IObservationSource[] observations = [new VersionServiceObservations(new VersionClient(versionHttp))];

await using var store = await ObjectStore.OpenAsync(Path.Combine(folder, "store"));
ObjectSource Source(RangePlanOptions? ranges = null, StreamOptions? streams = null) =>
    new(store, store, store, null, counting, observations, ObjectVerification.Default, new ObjectSourceOptions { Ranges = ranges ?? new(), Streams = streams ?? new() },
        split ? interactiveCdn : null);

Console.WriteLine($"# {product}/{region}, {DateTimeOffset.UtcNow:O}, {Environment.MachineName}");
var first = await MetadataPassAsync(Source(), "first");
var second = await MetadataPassAsync(Source(), "second");
Console.WriteLine($"second pass object requests: {second.Requests} (must be 0)");

// Bulk ranges: half the entries in the first 32 MiB of each archive, from 32 archives starting at VIBEMINER_BENCH_SKIP (default 0).
// Archives alternate between the gap settings so neither gets the warmer edge cache; the planner's figures for every setting
// on the same objects come first.
var used = int.TryParse(Environment.GetEnvironmentVariable("VIBEMINER_BENCH_SKIP"), out var skip) ? skip : 0;
var sampled = first.Indexes.Skip(used).Take(32).Select((p, i) =>
{
    var rng = new Random(154 + i);
    return (Slot: i % gaps.Length, Requests: p.Entries.Where(e => e.Offset < 32L << 20 && rng.Next(2) == 0)
        .Select(e => new ObjectRequest(new ObjectKey(ObjectKeyType.Encoded, e.Key.ToBytes()), e.Size, [new ArchiveLocation(p.Archive, e.Offset, e.Size)])).ToArray());
}).ToArray();
used += 32;
var all = sampled.SelectMany(x => x.Requests).ToArray();
var only = Environment.GetEnvironmentVariable("VIBEMINER_BENCH_ONLY");
foreach (var gapKiB in only == "interactive" ? [] : gaps)
{
    var plan = RangePlanner.Plan(all.Select(r => new PlanObject(new CdnKey(r.Key.KeyBytes()), r.DeclaredLength, r.Locations!)), first.Archives, new() { GapBytes = gapKiB << 10 });
    var wire = plan.Ranges.Sum(r => r.Length);
    Console.WriteLine($"plan gap {gapKiB} KiB over all {all.Length} sampled objects: {plan.Ranges.Count} requests, {wire / 1048576.0:F1} MiB, {(wire - plan.Ranges.Sum(r => r.MemberBytes)) * 100.0 / wire:F1}% gap");
}
for (var slot = 0; slot < (only == "interactive" ? 0 : gaps.Length); slot++)
{
    var gapKiB = gaps[slot];
    var source = Source(new RangePlanOptions { GapBytes = gapKiB << 10 });
    var requests = sampled.Where(x => x.Slot == slot).SelectMany(x => x.Requests).ToArray();
    var useful = requests.Sum(r => r.DeclaredLength!.Value);
    counting.Reset();
    var clock = Stopwatch.StartNew();
    var results = await source.GetKeysAsync(requests, new CdnContext(first.Route, first.Archives), FetchPriority.Background, null, ct);
    clock.Stop();
    var states = results.GroupBy(r => r.State + (r.Code is null ? "" : "/" + r.Code)).Select(g => $"{g.Key} {g.Count()}");
    Console.WriteLine($"bulk gap {gapKiB} KiB: {requests.Length} objects, {useful / 1048576.0:F1} MiB useful, {counting.Requests} requests, " +
        $"{counting.Bytes / 1048576.0:F1} MiB fetched ({(counting.Bytes - useful) * 100.0 / Math.Max(1, counting.Bytes):F1}% gap), {clock.Elapsed.TotalSeconds:F1} s, " +
        $"{useful / 1048576.0 / clock.Elapsed.TotalSeconds:F1} MiB/s useful, {counting.Bytes / 1048576.0 / clock.Elapsed.TotalSeconds:F1} MiB/s on the wire, " +
        $"streams limit {source.Streams.BackgroundLimit}; {string.Join(", ", states)}");
}

// Interactive latency while 32 background streams are busy: a bulk pass over fresh archives, and a config fetched meanwhile.
{
    // Ranges of at most 512 KiB, so 32 streams stay busy for the whole probe.
    var source = Source(new RangePlanOptions { TargetBytes = 512 << 10, CapBytes = 1 << 20 }, new() { MinBackground = 32, MaxBackground = 32 });
    var picked = first.Indexes.Skip(used).Take(8).ToArray();
    var requests = picked.SelectMany(p => p.Entries.Where(e => e.Offset < 64L << 20).Select(e =>
        new ObjectRequest(new ObjectKey(ObjectKeyType.Encoded, e.Key.ToBytes()), e.Size, [new ArchiveLocation(p.Archive, e.Offset, e.Size)]))).ToArray();
    var bulk = source.GetKeysAsync(requests, new CdnContext(first.Route, first.Archives), FetchPriority.Background, null, ct);
    while (source.Streams.BackgroundActive < 32 && !bulk.IsCompleted) await Task.Delay(10);
    var samples = new List<double>();
    {
        var probe = new ObjectKey(ObjectKeyType.Config, first.CdnConfigKey.KeyBytes());
        await store.DeleteAsync([probe]);
        var idle = Stopwatch.StartNew();
        await Source().GetKeyAsync(new(probe), new CdnContext(first.Route), FetchPriority.Interactive, ct);
        Console.WriteLine($"interactive with nothing else running: {idle.Elapsed.TotalMilliseconds:F0} ms");
    }
    for (var i = 0; i < 10; i++)
    {
        // The CDN config, deleted first so each probe is a real CDN fetch.
        var probe = new ObjectKey(ObjectKeyType.Config, first.CdnConfigKey.KeyBytes());
        await store.DeleteAsync([probe]);
        var clock = Stopwatch.StartNew();
        var r = await source.GetKeyAsync(new(probe), new CdnContext(first.Route), FetchPriority.Interactive, ct);
        var busy = source.Streams.BackgroundActive;
        if (bulk.IsCompleted) { Console.WriteLine("bulk finished before the probes did"); break; }
        samples.Add(clock.Elapsed.TotalMilliseconds);
        Console.WriteLine($"interactive while {busy} background streams busy: {r.State} in {clock.Elapsed.TotalMilliseconds:F0} ms");
        await Task.Delay(200);
    }
    var done = await bulk;
    if (samples.Count > 0)
        Console.WriteLine($"interactive ({(split ? "own connections" : "shared connections")}) median {samples.Order().ElementAt(samples.Count / 2):F0} ms, max {samples.Max():F0} ms over {samples.Count}; bulk {done.Count(r => r.IsPresent)}/{done.Count} present");
}
return 0;

async Task<Pass> MetadataPassAsync(ObjectSource source, string name)
{
    counting.Reset();
    var total = Stopwatch.StartNew();
    var versions = await source.ObserveAsync(new(ObservationKind.Versions, product, region), ct);
    var cdns = await source.ObserveAsync(new(ObservationKind.Cdns, product, region), ct);
    if (versions.Observation is null || cdns.Observation is null) throw new InvalidOperationException($"version service: {versions.Code} {cdns.Code}");
    var row = VersionTable.Parse(versions.Observation.Bytes).Rows.First(r => r["Region"] == region);
    var route = CdnsRoutes.Read(VersionTable.Parse(cdns.Observation.Bytes), out _)[region];
    var context = new CdnContext(route);
    var buildKey = ObjectKey.Parse(ObjectKeyType.Config, row["BuildConfig"]);
    var cdnKey = ObjectKey.Parse(ObjectKeyType.Config, row["CDNConfig"]);
    var configs = await source.GetKeysAsync([new(buildKey), new(cdnKey)], context, FetchPriority.Interactive, null, ct);
    Expect(configs);
    var build = TactConfigurationParser.Parse((await source.GetKeyAsync(new(buildKey), context, FetchPriority.Interactive, ct)).Bytes);
    var cdn = TactConfigurationParser.Parse((await source.GetKeyAsync(new(cdnKey), context, FetchPriority.Interactive, ct)).Bytes);
    var archives = cdn.Fields["archives"].Select(CdnKey.Parse).ToArray();
    context = new CdnContext(route, archives);
    var t = Stopwatch.StartNew();
    var indexRequests = archives.Select(a => new ObjectRequest(new ObjectKey(ObjectKeyType.ArchiveIndex, a.ToBytes())))
        .Append(new(ObjectKey.Parse(ObjectKeyType.FileIndex, cdn.Fields["file-index"][0]))).ToArray();
    var indexes = await source.GetKeysAsync(indexRequests, context, FetchPriority.Background, null, ct);
    Expect(indexes);
    var indexTime = t.Elapsed;
    var indexBytes = counting.Bytes;

    // Index membership for the metadata objects: archived ones come as ranges, the rest loose.
    var entries = new List<(CdnKey Archive, IReadOnlyList<Entry> Entries)>();
    foreach (var a in archives)
        entries.Add((a, ReadIndex((await source.GetKeyAsync(new(new ObjectKey(ObjectKeyType.ArchiveIndex, a.ToBytes())), context, FetchPriority.Background, ct)).Bytes!)));
    var located = entries.SelectMany(x => x.Entries.Select(e => (x.Archive, e))).ToLookup(x => x.e.Key);
    ObjectRequest Encoded(string ekey, long size)
    {
        var k = CdnKey.Parse(ekey);
        var at = located[k].Select(x => new ArchiveLocation(x.Archive, x.e.Offset, x.e.Size)).ToArray();
        return new(new ObjectKey(ObjectKeyType.Encoded, k.ToBytes()), size, at);
    }
    var encoding = Encoded(build.Fields["encoding"][1], long.Parse(build.Fields["encoding-size"][1]));
    t.Restart();
    Expect([await source.GetKeyAsync(encoding, context, FetchPriority.Background, ct)]);
    var encodingTime = t.Elapsed;
    // Decode ENCODING into a temp file and look ROOT up.
    var encoded = Path.Combine(folder, $"encoding-{name}.blte"); var decoded = Path.Combine(folder, $"encoding-{name}.bin");
    await using (var file = File.Create(encoded))
    {
        var buffer = new byte[4 << 20];
        for (long at = 0; at < encoding.DeclaredLength; at += buffer.Length)
        {
            var slice = buffer.AsMemory(0, (int)Math.Min(buffer.Length, encoding.DeclaredLength!.Value - at));
            Expect([await source.GetRangeAsync(encoding, at, slice, context, FetchPriority.Background, ct)]);
            await file.WriteAsync(slice, ct);
        }
    }
    await using (var input = File.OpenRead(encoded)) await using (var output = File.Create(decoded))
        await BlteDecoder.DecodeAsync(input, output, build.Fields["encoding"][1], build.Fields["encoding"][0],
            encoding.DeclaredLength!.Value, long.Parse(build.Fields["encoding-size"][0]), ct);
    EncodingEncodedEntry rootEntry;
    await using (var stream = File.OpenRead(decoded))
    {
        var lookup = await EncodingFileLookup.OpenAsync(stream, new EncodingLimits(), ct);
        var content = await lookup.FindContentAsync(build.Fields["root"][0], ct) ?? throw new InvalidOperationException("ROOT CKey not in ENCODING");
        rootEntry = await lookup.FindEncodingAsync(content.EncodingKeys[0], ct) ?? throw new InvalidOperationException("ROOT EKey not in ENCODING");
    }
    File.Delete(encoded); File.Delete(decoded);
    var rest = new[] { Encoded(rootEntry.EncodingKey, rootEntry.EncodedBytes) }
        .Concat(new[] { "download", "install", "size" }.Where(build.Fields.ContainsKey)
            // A role with only a CKey can't be fetched by key: it needs ENCODING, which this pass doesn't walk for it.
            .Where(f => build.Fields[f].Count == 2 && build.Fields.TryGetValue(f + "-size", out var sz) && sz.Count == 2)
            .Select(f => Encoded(build.Fields[f][1], long.Parse(build.Fields[f + "-size"][1])))).ToArray();
    t.Restart();
    Expect(await source.GetKeysAsync(rest, context, FetchPriority.Background, null, ct));
    var restTime = t.Elapsed;
    total.Stop();
    Console.WriteLine($"{name} pass: {counting.Requests} object requests, {counting.Bytes / 1048576.0:F1} MiB in {total.Elapsed.TotalSeconds:F1} s. " +
        $"{indexRequests.Length} indexes {indexBytes / 1048576.0:F1} MiB in {indexTime.TotalSeconds:F1} s ({indexBytes / 1048576.0 / Math.Max(0.001, indexTime.TotalSeconds):F1} MiB/s); " +
        $"ENCODING {encoding.DeclaredLength / 1048576.0:F1} MiB in {encodingTime.TotalSeconds:F1} s; ROOT, DOWNLOAD, INSTALL, SIZE in {restTime.TotalSeconds:F1} s " +
        $"({string.Join(", ", rest.Select(r => (r.Locations!.Count > 0 ? "ranged " : "loose ") + $"{r.DeclaredLength / 1048576.0:F1} MiB"))})");
    return new(route, archives, entries, new ObjectKey(ObjectKeyType.Config, cdnKey.KeyBytes()), counting.Requests);
}

static void Expect(IEnumerable<ObjectResult> results)
{
    var bad = results.Where(r => !r.IsPresent).ToArray();
    if (bad.Length > 0) throw new InvalidOperationException($"{bad.Length} not present, first {bad[0].Key}: {bad[0].State} {bad[0].Code} {bad[0].Message}");
}

static IReadOnlyList<Entry> ReadIndex(byte[] bytes)
{
    // The store already verified this index (footer, TOC, every page). Width 4: 24-byte entries, 170 per 4 KiB page.
    var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 12));
    var list = new List<Entry>((int)count);
    for (var i = 0; i < count; i++)
    {
        var at = i / 170 * 4096 + i % 170 * 24;
        list.Add(new(new CdnKey(bytes.AsSpan(at, 16)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 16)), BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 20))));
    }
    return list;
}

namespace VibeMiner.ObjectAccess.Benchmark
{
    internal readonly record struct Entry(CdnKey Key, long Size, long Offset);
    internal sealed record Pass(CdnRoute Route, CdnKey[] Archives, List<(CdnKey Archive, IReadOnlyList<Entry> Entries)> Indexes, ObjectKey CdnConfigKey, long Requests);

    /// <summary>Counts requests and body bytes; failed attempts count as requests too.</summary>
    internal sealed class CountingCdnClient(ICdnClient inner) : ICdnClient
    {
        private long requests, bytes;
        public long Requests => Interlocked.Read(ref requests);
        public long Bytes => Interlocked.Read(ref bytes);
        public void Reset() { Interlocked.Exchange(ref requests, 0); Interlocked.Exchange(ref bytes, 0); }
        public async Task<CdnReply> FetchAsync(CdnRoute route, CdnRequest request, CancellationToken cancellationToken)
        {
            try { var r = await inner.FetchAsync(route, request, cancellationToken); Count(1 + r.Failed.Count, r.Length); return r; }
            catch (CdnException ex) { Count(ex.Attempts.Count, 0); throw; }
        }
        public async Task<CdnReply> FetchToAsync(CdnRoute route, CdnRequest request, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken cancellationToken)
        {
            try { var r = await inner.FetchToAsync(route, request, sink, cancellationToken); Count(1 + r.Failed.Count, r.Length); return r; }
            catch (CdnException ex) { Count(ex.Attempts.Count, 0); throw; }
        }
        private void Count(long n, long b) { Interlocked.Add(ref requests, n); Interlocked.Add(ref bytes, b); }
    }
}
