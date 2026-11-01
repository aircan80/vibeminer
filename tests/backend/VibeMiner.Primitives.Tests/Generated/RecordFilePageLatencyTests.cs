using System.Diagnostics;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Primitives.Tests.Generated;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RecordFilePageLatencyCollection { public const string Name = "Record file page latency"; }

/// <summary>
/// #179: a 50-row page reads in under 1 ms at p99, measured on a real GeneratedStore holding 200k synthetic table-like rows, reopened,
/// page cache warm. tools/benchmarks/GeneratedStore has the fuller numbers.
/// </summary>
[Trait("Speed", "Slow"), Collection(RecordFilePageLatencyCollection.Name)]
public sealed class RecordFilePageLatencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AFiftyRecordPageReadsUnderOneMillisecondAtP99()
    {
        using var t = new TempDirectory();
        var folder = Path.Combine(t.Path, GeneratedStore.DefaultFolderName);
        var key = GeneratedKey.Hash("page-latency"u8);
        await using (var store = await GeneratedStore.OpenAsync(folder))
        using (var writer = new RecordFileWriter(store))
        {
            foreach (var (id, bytes) in SyntheticRows.Generate(200_000)) await writer.AddAsync(id, bytes);
            await writer.CompleteAsync(key, "{}"u8.ToArray());
        }

        await using var reopened = await GeneratedStore.OpenAsync(folder);
        var open = await RecordFile.OpenAsync(reopened, key);
        Assert.Equal(GeneratedReadState.Present, open.State);
        var file = open.File!;
        Assert.Equal(200_000, file.Count);

        var r = new Random(179);
        for (var i = 0; i < 1_000; i++) await file.ReadAsync(r.NextInt64(file.Count - 49), 50); // warm-up
        var best = double.MaxValue;
        for (var round = 1; round <= 3; round++)
        {
            var samples = new double[5_000];
            for (var i = 0; i < samples.Length; i++)
            {
                var first = r.NextInt64(file.Count - 49);
                var start = Stopwatch.GetTimestamp();
                var page = await file.ReadAsync(first, 50);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert.Equal(50, page.Count);
                Assert.Equal(first, page[0].Ordinal);
            }
            Array.Sort(samples);
            var p99 = samples[(int)(samples.Length * 0.99)];
            best = Math.Min(best, p99);
            output.WriteLine($"round {round}, 50-record page over 200k records, {samples.Length} reads: p50 {samples[samples.Length / 2]:F3} ms, p99 {p99:F3} ms, max {samples[^1]:F3} ms");
        }
        // 1 ms is the target on the test machine; shared CI runners (CI=true) only get a 5 ms sanity bound.
        var bound = Environment.GetEnvironmentVariable("CI") == "true" ? 5.0 : 1.0;
        output.WriteLine($"best p99 {best:F3} ms, bound {bound} ms");
        Assert.True(best < bound, $"best p99 of 3 rounds {best:F3} ms is not under {bound} ms");
    }
}
