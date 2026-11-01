using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Backend.Tests;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Primitives.Tests;

/// <summary>
/// Opt-in scale checks (docs/TESTING.md). <c>VIBEMINER_OBJECT_STORE_SCALE=N</c> runs them over N million objects (3 for everyday
/// checks, about one Retail build; 10 for sign-off and for changes to index, lookup or rebuild code; 30 only as an optional extreme run).
/// The store is built once per
/// size in the main checkout's <c>.local/scale-store/</c> and reused (<c>VIBEMINER_OBJECT_STORE_SCALE_REBUILD=1</c> rebuilds it).
/// </summary>
public sealed class ScaleFactAttribute : FactAttribute
{
    public ScaleFactAttribute()
    {
        if (ObjectStoreScaleTests.Millions is null) Skip = "Opt-in: set VIBEMINER_OBJECT_STORE_SCALE to the size in millions (3 everyday, 10 for sign-off, 30 optional extreme).";
    }
}

/// <summary>The Scan rebuild at scale: only with <c>VIBEMINER_OBJECT_STORE_SCALE_SCAN=1</c> as well (13 minutes at 30M).</summary>
public sealed class ScaleScanFactAttribute : FactAttribute
{
    public ScaleScanFactAttribute()
    {
        if (ObjectStoreScaleTests.Millions is null || Environment.GetEnvironmentVariable("VIBEMINER_OBJECT_STORE_SCALE_SCAN") != "1")
            Skip = "Opt-in: set VIBEMINER_OBJECT_STORE_SCALE and VIBEMINER_OBJECT_STORE_SCALE_SCAN=1 (the rebuild takes about 13 minutes at 30M).";
    }
}

[Trait("Speed", "Slow")]
public sealed class ObjectStoreScaleTests(ITestOutputHelper output)
{
    /// <summary>Millions of objects asked for, or null when the scale checks are off. A value that isn't a number means 3.</summary>
    internal static int? Millions => Environment.GetEnvironmentVariable("VIBEMINER_OBJECT_STORE_SCALE") is { Length: > 0 } v
        ? int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 3
        : null;

    private Task<string> CachedAsync() => ScaleStore.EnsureAsync(RepositoryPaths.Root, Millions!.Value,
        Environment.GetEnvironmentVariable("VIBEMINER_OBJECT_STORE_SCALE_REBUILD") == "1", output.WriteLine);

    [ScaleFact]
    public async Task LookupsStayUnderTenMicrosecondsAndTheOpenCheckPassesAtScale()
    {
        var total = Millions!.Value * 1_000_000L;
        var sw = Stopwatch.StartNew();
        var dir = await CachedAsync();
        output.WriteLine($"store ready in {sw.Elapsed.TotalSeconds:F1} s");
        // Reads only: the cached store is shared, so it is opened read-only. It was closed cleanly, so only the cheap check runs.
        sw.Restart();
        await using var s = await ObjectStore.OpenReadOnlyAsync(dir);
        output.WriteLine($"open after a clean close (cheap check only) {sw.Elapsed.TotalSeconds * 1000:F0} ms");
        Assert.False(s.OpenReport.FullIndexCheck);
        // After an unclean shutdown the open runs the full check: measured on a copy whose writer was killed.
        using (var t = new TempDirectory())
        {
            var copy = await ScaleStore.CopyAsync(dir, t.Path);
            var killed = await ObjectStore.OpenAsync(copy);
            killed.Kill();
            sw.Restart();
            await using (var unclean = await ObjectStore.OpenAsync(copy))
            {
                output.WriteLine($"open after an unclean shutdown (full check) {sw.Elapsed.TotalSeconds:F1} s");
                Assert.True(unclean.OpenReport.FullIndexCheck);
                Assert.False(unclean.OpenReport.IndexRebuilt);
            }
            sw.Restart();
            await using (var clean = await ObjectStore.OpenAsync(copy)) output.WriteLine($"writer open after its clean close {sw.Elapsed.TotalSeconds * 1000:F0} ms");
        }
        // Where the full check goes: SQLite's integrity_check on its own; the rest is the totals over every row.
        sw.Restart();
        using (var c = new SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA integrity_check";
            Assert.Equal("ok", cmd.ExecuteScalar());
        }
        output.WriteLine($"  integrity_check alone {sw.Elapsed.TotalSeconds:F1} s");
        var rng = new Random(5);
        for (var i = 0; i < 100_000; i++) await s.HasAsync(ScaleStore.Tiny((long)(rng.NextDouble() * total)).Key);
        foreach (var (label, absent) in new[] { ("present", false), ("absent", true) })
        {
            var lat = new double[100_000];
            for (var i = 0; i < lat.Length; i++)
            {
                var key = absent ? new ObjectKey(ObjectKeyType.Encoded, RandomNumberGenerator.GetBytes(16)) : ScaleStore.Tiny((long)(rng.NextDouble() * total)).Key;
                var start = Stopwatch.GetTimestamp();
                Assert.Equal(!absent, await s.HasAsync(key));
                lat[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
            Array.Sort(lat);
            output.WriteLine($"Has, {label}: p50 {lat[lat.Length / 2]:F1} µs, p99 {lat[lat.Length * 99 / 100]:F1} µs");
            Assert.True(lat[lat.Length / 2] < 10, $"median {label} lookup {lat[lat.Length / 2]:F1} µs");
        }
        var reads = new double[20_000];
        for (var i = 0; i < reads.Length; i++)
        {
            var o = ScaleStore.Tiny((long)(rng.NextDouble() * total));
            var start = Stopwatch.GetTimestamp();
            Assert.Equal(o.Bytes.ToArray(), (await s.ReadAsync(o.Key)).Bytes);
            reads[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        Array.Sort(reads);
        output.WriteLine($"Read: p50 {reads[reads.Length / 2]:F1} µs, p99 {reads[reads.Length * 99 / 100]:F1} µs");
    }

    [ScaleScanFact]
    public async Task ScanRebuildsTheSameIndexAtScale()
    {
        var dir = await CachedAsync();
        using var t = new TempDirectory();
        var sw = Stopwatch.StartNew();
        var copy = await ScaleStore.CopyAsync(dir, t.Path);  // Scan writes: never on the shared cache
        output.WriteLine($"copied in {sw.Elapsed.TotalSeconds:F1} s");
        var before = Digest(copy);
        sw.Restart();
        await using (var s = await ObjectStore.OpenAsync(copy))
        {
            output.WriteLine($"writer open {sw.Elapsed.TotalSeconds:F1} s");
            Assert.False(s.OpenReport.IndexRebuilt);
            sw.Restart();
            var r = await s.ScanAsync();
            output.WriteLine($"Scan {sw.Elapsed.TotalSeconds:F0} s, {r.Live:N0} live");
            Assert.Equal(Millions!.Value * 1_000_000L, r.Live);
        }
        Assert.Equal(before, Digest(copy));
    }

    /// <summary>An order-independent digest of every object row, so millions of rows compare without holding them.</summary>
    private static string Digest(string dir)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT k, pack, offset, length, sha256 FROM objects ORDER BY k";
        using var r = cmd.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long n = 0;
        while (r.Read())
        {
            hash.AppendData(r.GetFieldValue<byte[]>(0)); hash.AppendData(BitConverter.GetBytes(r.GetInt64(1)));
            hash.AppendData(BitConverter.GetBytes(r.GetInt64(2))); hash.AppendData(BitConverter.GetBytes(r.GetInt64(3)));
            hash.AppendData(r.GetFieldValue<byte[]>(4)); n++;
        }
        return $"{n}:{Convert.ToHexString(hash.GetHashAndReset())}";
    }
}
