using Xunit;
using System.Collections.Concurrent;
using System.Diagnostics;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using static VibeMiner.Backend.Tests.ObjectStoreTestKit;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// Crashes at every step of append, sync, commit, pack roll, compaction and rebuild, two ways: an in-process fault hook that drops
/// the store as a kill would, and a real SIGKILL of a child process stopped at the same point. After each, every object the store
/// confirmed must read back exactly, every confirmed delete must hold, Verify must find nothing, and Scan must rebuild the same index.
/// </summary>
public sealed class ObjectStoreCrashTests
{
    // Points the workload reaches, with how many times to let each pass before stopping (early, and deep into the run).
    public static TheoryData<string, int> Points()
    {
        var data = new TheoryData<string, int>();
        foreach (var p in new[] { StorePoint.EntryStarted, StorePoint.EntryHalfWritten, StorePoint.EntryWritten,
                     StorePoint.BeforePackSync, StorePoint.AfterPackSync, StorePoint.InsideIndexCommit, StorePoint.AfterIndexCommit })
            foreach (var n in new[] { 1, 7, 40 }) data.Add(p.ToString(), n);
        foreach (var p in new[] { StorePoint.PackFileWritten, StorePoint.PackListed })
            foreach (var n in new[] { 1, 4, 9 }) data.Add(p.ToString(), n);
        foreach (var p in new[] { StorePoint.CompactTargetListed, StorePoint.CompactCopied, StorePoint.CompactSwitched, StorePoint.CompactOldDeleted, StorePoint.StagingAppended })
            foreach (var n in new[] { 1, 2 }) data.Add(p.ToString(), n);
        data.Add(nameof(StorePoint.RebuildBuilt), 1);
        data.Add(nameof(StorePoint.RebuildSwapped), 1);
        return data;
    }

    [Theory]
    [MemberData(nameof(Points))]
    public async Task AnInProcessCrashAtAnyStepLosesNoConfirmedObject(string point, int occurrence)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var target = Enum.Parse<StorePoint>(point);
        var seen = 0;
        var options = Workload.Options with { Fault = p => { if (p == target && Interlocked.Increment(ref seen) == occurrence) throw new SimulatedCrashException(p); } };
        var lines = new ConcurrentQueue<string>();
        var store = await ObjectStore.OpenAsync(dir, options);
        var crashed = false;
        try { await Workload.RunAsync(store, lines.Enqueue, 3); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Closed) { crashed = true; }
        catch (SimulatedCrashException) { crashed = true; store.Kill(); }  // a step on the caller's side (staging)
        Assert.True(crashed, $"the workload never reached {point} #{occurrence}");
        await store.DisposeAsync();
        await CheckWorkloadAsync(dir, lines);
    }

    [Theory]
    [InlineData(nameof(StorePoint.RecoveryTruncated))]
    [InlineData(nameof(StorePoint.InsideIndexCommit))]
    [InlineData(nameof(StorePoint.AfterPackSync))]
    public async Task ACrashDuringRecoveryLosesNothingEither(string point)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var lines = new ConcurrentQueue<string>();
        var seen = 0;
        // Stop with a synced but uncommitted batch in the pack, so recovery has whole entries to index and commit.
        var first = Workload.Options with { Fault = p => { if (p == StorePoint.AfterPackSync && Interlocked.Increment(ref seen) == 10) throw new SimulatedCrashException(p); } };
        await using (var store = await ObjectStore.OpenAsync(dir, first))
            await Assert.ThrowsAsync<ObjectStoreException>(() => Workload.RunAsync(store, lines.Enqueue, 2));
        var target = Enum.Parse<StorePoint>(point);
        var again = Workload.Options with { Fault = p => { if (p == target) throw new SimulatedCrashException(p); } };
        await Assert.ThrowsAsync<SimulatedCrashException>(() => ObjectStore.OpenAsync(dir, again));
        await CheckWorkloadAsync(dir, lines);
    }

    // The real kills cover each step early and a little later; the in-process run covers the deep cases.
    public static TheoryData<string, int> KillPoints()
    {
        var data = new TheoryData<string, int>();
        foreach (var row in Points()) if ((int)row[1] <= 7) data.Add((string)row[0], (int)row[1]);
        return data;
    }

    [Theory]
    [MemberData(nameof(KillPoints))]
    public async Task ASigkillAtAnyStepLosesNoConfirmedObject(string point, int occurrence)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var (lines, at) = await RunChildAsync(dir, point, occurrence, iterations: 3, killAfter: null);
        Assert.True(at, $"the child never reached {point} #{occurrence}");
        await CheckWorkloadAsync(dir, lines);
    }

    [Fact]
    public async Task SigkillsAtRandomMomentsLoseNoConfirmedObject()
    {
        var rng = new Random(20261031);
        for (var trial = 0; trial < 6; trial++)
        {
            using var t = new TempDirectory();
            var dir = StoreDir(t);
            var (lines, _) = await RunChildAsync(dir, "none", 0, iterations: 1000, killAfter: TimeSpan.FromMilliseconds(rng.Next(150, 2500)));
            Assert.Contains(lines, l => l.StartsWith("COMMITTED", StringComparison.Ordinal));
            await CheckWorkloadAsync(dir, lines);
        }
    }

    /// <summary>Starts the crash writer, waits until it reports the point (or for the delay), SIGKILLs it and returns what it confirmed.</summary>
    private static async Task<(List<string> lines, bool reachedPoint)> RunChildAsync(string dir, string point, int occurrence, int iterations, TimeSpan? killAfter)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = dotnetRoot is null ? "dotnet" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { typeof(Workload).Assembly.Location, dir, point, occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture), iterations.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var lines = new List<string>();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderr = process.StandardError.ReadToEndAsync();
        var reader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                lock (lines) lines.Add(line);
                if (line == "OPEN") opened.TrySetResult();
                if (line.StartsWith("AT ", StringComparison.Ordinal)) reached.TrySetResult();
                if (line == "DONE") reached.TrySetCanceled();
            }
            opened.TrySetCanceled(); reached.TrySetCanceled();
        });
        var hit = false;
        try
        {
            if (killAfter is { } delay) { await opened.Task.WaitAsync(TimeSpan.FromSeconds(60)); await Task.Delay(delay); }
            else { await reached.Task.WaitAsync(TimeSpan.FromSeconds(120)); hit = true; }
        }
        catch (TaskCanceledException) { }
        finally
        {
            if (!process.HasExited) process.Kill();  // SIGKILL on Linux and macOS
            await process.WaitForExitAsync();
            await reader;
        }
        Assert.True(killAfter is not null || hit || lines.Contains("DONE"), $"child failed: {await stderr}");
        lock (lines) return (lines.ToList(), hit);
    }
}
