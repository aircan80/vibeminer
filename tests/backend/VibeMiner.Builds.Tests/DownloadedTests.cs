using System.Buffers.Binary;
using System.Diagnostics;
using VibeMiner.Builds.Testing;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Builds.Tests;

/// <summary>Opt-in: <c>VIBEMINER_DOWNLOADED_SCALE=1</c> measures a Retail-sized ledger over the in-memory source.</summary>
public sealed class DownloadedScaleFactAttribute : FactAttribute
{
    public DownloadedScaleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VIBEMINER_DOWNLOADED_SCALE") != "1") Skip = "Opt-in: set VIBEMINER_DOWNLOADED_SCALE=1.";
    }
}

/// <summary>The Downloaded figure over builds opened by the real opener on the in-memory layer 2 (D137).</summary>
public sealed class DownloadedTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => CancellationToken.None;
    private readonly InMemoryObjectSource inner = new();

    private static ObjectKey Encoded(CdnKey key) => new(ObjectKeyType.Encoded, key.ToBytes());
    private static ObjectKey FileKey(SyntheticBuild b, uint id, uint locale = SyntheticBuild.EnUs) =>
        Encoded(b.Variants(id).Single(v => v.LocaleFlags == locale).EKey!.Value);
    private static long FileSize(SyntheticBuild b, uint id, uint locale = SyntheticBuild.EnUs) =>
        b.Variants(id).Single(v => v.LocaleFlags == locale).EncodedSize!.Value;
    private static long RootSize(SyntheticBuild b) => b.Objects[b.RootKey].Length;

    private async Task<IBuild> OpenAsync(SyntheticBuild b, IObjectSource? via = null)
    {
        var result = await new BuildOpener(via ?? inner).OpenAsync(b.Ref, Ct);
        Assert.True(result.State == BuildOpenState.Open, $"{result.State} {result.Code}");
        return result.Opened!;
    }

    private Task FetchAsync(ObjectKey key)
    {
        inner.Add(key, [1, 2, 3]);
        return inner.GetKeyAsync(new ObjectRequest(key), null, FetchPriority.Interactive, Ct);
    }

    /// <summary>The next Changed for <paramref name="build"/> whose figure matches.</summary>
    private static Task<DownloadedFigure> When(DownloadedFigures figures, IBuild build, Func<DownloadedFigure, bool> match)
    {
        var done = new TaskCompletionSource<DownloadedFigure>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(BuildRef b, DownloadedFigure f) { if (b == build.Build && match(f)) done.TrySetResult(f); }
        figures.Changed += Handler;
        _ = done.Task.ContinueWith(_ => figures.Changed -= Handler, TaskScheduler.Default);
        return done.Task.WaitAsync(Guard);
    }

    private static async Task<DownloadedFigure> BruteForceAsync(IObjectSource source, IBuild build)
    {
        var ledger = ((OpenedBuild)build).Ledger;
        long objects = 0, bytes = 0, total = 0;
        for (var i = 0; i < ledger.Count; i++)
        {
            total += ledger.Size(i);
            if (!(await source.HasKeysAsync([Encoded(ledger.Key(i))], Ct))[0]) continue;
            objects++; bytes += ledger.Size(i);
        }
        return new(objects, ledger.Count, bytes, total);
    }

    [Fact]
    public async Task MatchesABruteForceCount()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        inner.AddStored(FileKey(b, 100), [1]).AddStored(FileKey(b, 300), [1]);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, options: new() { BatchKeys = 2 });
        var figure = await figures.GetAsync(build, Ct);
        Assert.Equal(await BruteForceAsync(inner, build), figure);
        // Root, 100 enUS and 300 are stored; the six rows are four file variants, 200 and ROOT, sized from ENCODING.
        Assert.Equal(new DownloadedFigure(3, 6, RootSize(b) + FileSize(b, 100) + FileSize(b, 300),
            RootSize(b) + FileSize(b, 100) + FileSize(b, 100, SyntheticBuild.DeDe) + FileSize(b, 200) + FileSize(b, 300) + FileSize(b, 400)), figure);
    }

    [Fact]
    public async Task ACommitMovesEveryBuildThatSharesTheObject()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic", files:
            [SyntheticBuild.StandardFiles[0], new(600, "sound/six.ogg", new SyntheticVariant(SyntheticBuild.EnUs, 0, Placement.Archived))]).LoadInto(inner);
        var builds = new BuildOpener(inner);
        var buildA = (await builds.OpenAsync(a.Ref, Ct)).Opened!;
        var buildB = (await builds.OpenAsync(b.Ref, Ct)).Opened!;
        await using var figures = new DownloadedFigures(inner, inner);
        var beforeA = await figures.GetAsync(buildA, Ct);
        var beforeB = await figures.GetAsync(buildB, Ct);
        var size = FileSize(a, 100);
        var movedA = When(figures, buildA, f => f.StoredObjects == beforeA.StoredObjects + 1);
        var movedB = When(figures, buildB, f => f.StoredObjects == beforeB.StoredObjects + 1);
        await FetchAsync(FileKey(a, 100));
        Assert.Equal(beforeA.StoredBytes + size, (await movedA).StoredBytes);
        Assert.Equal(beforeB.StoredBytes + size, (await movedB).StoredBytes);
        Assert.Equal(await BruteForceAsync(inner, buildA), await figures.GetAsync(buildA, Ct));
        Assert.Equal(await BruteForceAsync(inner, buildB), await figures.GetAsync(buildB, Ct));
    }

    [Fact]
    public async Task NeverCountsKeysOutsideTheLedger()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner);
        var before = await figures.GetAsync(build, Ct);
        // ENCODING itself is stored but isn't one of its own rows; only ROOT counts.
        Assert.Equal(new DownloadedFigure(1, 6, RootSize(b), before.TotalBytes), before);
        var key = FileKey(b, 200).KeyBytes();
        key[15]++;
        var neighbour = new ObjectKey(ObjectKeyType.Encoded, key);
        Assert.Equal(-1, ((OpenedBuild)build).Ledger.IndexOf(neighbour));
        var moved = When(figures, build, f => f.StoredObjects != before.StoredObjects);
        await FetchAsync(neighbour);
        await FetchAsync(FileKey(b, 200));
        // Commits arrive in order, so the neighbour was seen first and moved nothing; bytes are ENCODING's size only.
        Assert.Equal(before with { StoredObjects = 2, StoredBytes = before.StoredBytes + FileSize(b, 200) }, await moved);
    }

    [Fact]
    public async Task RemoveLowersTheFigure()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        inner.AddStored(FileKey(b, 300), [1]);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner);
        var before = await figures.GetAsync(build, Ct);
        var lowered = When(figures, build, f => f.StoredObjects == before.StoredObjects - 1);
        inner.Remove(FileKey(b, 300));
        Assert.Equal(before.StoredBytes - FileSize(b, 300), (await lowered).StoredBytes);
    }

    [Fact]
    public async Task AnotherProcessWriteShowsOnlyAfterARefreshOrAReopen()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner);
        var before = await figures.GetAsync(build, Ct);
        inner.AddStored(FileKey(b, 200), [1]);
        Assert.Equal(before, await figures.GetAsync(build, Ct));
        var refreshed = await figures.RefreshAsync(build, Ct);
        Assert.Equal(before.StoredObjects + 1, refreshed.StoredObjects);
        inner.AddStored(FileKey(b, 400), [1]);
        Assert.Equal(refreshed, await figures.GetAsync(build, Ct));
        var reopened = await OpenAsync(b);
        Assert.NotSame(build, reopened);
        Assert.Equal(await BruteForceAsync(inner, reopened), await figures.GetAsync(reopened, Ct));
        Assert.Equal(before.StoredObjects + 2, (await figures.GetAsync(reopened, Ct)).StoredObjects);
    }

    [Fact]
    public async Task WithoutCommitsOnlyARefreshMovesIt()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner);
        var before = await figures.GetAsync(build, Ct);
        await FetchAsync(FileKey(b, 200));
        Assert.Equal(before, await figures.GetAsync(build, Ct));
        Assert.Equal(before.StoredObjects + 1, (await figures.RefreshAsync(build, Ct)).StoredObjects);
    }

    [Fact]
    public async Task AMissedQueueEndsWithTheRightFigure()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner, new() { QueueCapacity = 1 });
        await figures.GetAsync(build, Ct);
        // Hold the pump inside its first Changed so the next commits find the one-slot queue full.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var held = 0;
        figures.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0) return;
            entered.TrySetResult();
            release.Wait(Guard);
        };
        var complete = When(figures, build, f => f.StoredObjects == 6);
        await FetchAsync(FileKey(b, 100));
        await entered.Task.WaitAsync(Guard);
        await FetchAsync(FileKey(b, 100, SyntheticBuild.DeDe));
        await FetchAsync(FileKey(b, 200));
        await FetchAsync(FileKey(b, 300));
        await FetchAsync(FileKey(b, 400));
        release.Set();
        // Three of those commits were dropped, so only a new measurement can reach all six.
        var figure = await complete;
        Assert.Equal(figure.TotalBytes, figure.StoredBytes);
        Assert.Equal(await BruteForceAsync(inner, build), await figures.GetAsync(build, Ct));
    }

    [Fact]
    public async Task ACommitDuringAMeasurementIsNotUndone()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var source = new TestObjectSource(inner);
        var build = await OpenAsync(b, source);
        await using var figures = new DownloadedFigures(source, inner, new() { BatchKeys = 1 });
        var before = await figures.GetAsync(build, Ct);
        var key = FileKey(b, 300);

        // HasKeys answers "not stored", then the key is committed before that answer returns.
        var stale = await HoldAsync(source, key, () => figures.RefreshAsync(build, Ct), async () =>
        {
            var moved = When(figures, build, f => f.StoredObjects == before.StoredObjects + 1);
            await FetchAsync(key);
            await moved;
        });
        Assert.Equal(before.StoredObjects + 1, stale.StoredObjects);

        // And the other way: HasKeys answers "stored", then the key is removed.
        var removedStale = await HoldAsync(source, key, () => figures.RefreshAsync(build, Ct), async () =>
        {
            var moved = When(figures, build, f => f.StoredObjects == before.StoredObjects);
            inner.Remove(key);
            await moved;
        });
        Assert.Equal(before, removedStale);
        Assert.Equal(await BruteForceAsync(inner, build), await figures.GetAsync(build, Ct));
    }

    private static async Task<DownloadedFigure> HoldAsync(TestObjectSource source, ObjectKey key, Func<Task<DownloadedFigure>> measure, Func<Task> during)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterHasKeys = async (keys, ct) => { if (keys.Contains(key)) { reached.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var measuring = measure();
        await reached.Task.WaitAsync(Guard);
        await during();
        source.AfterHasKeys = null;
        release.SetResult();
        return await measuring.WaitAsync(Guard);
    }

    [Fact]
    public async Task CancellingOneWaiterLeavesTheOther()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var source = new TestObjectSource(inner);
        var build = await OpenAsync(b, source);
        source.Reset();
        await using var figures = new DownloadedFigures(source, inner);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterHasKeys = async (_, ct) => { reached.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var cts = new CancellationTokenSource();
        var cancelled = figures.GetAsync(build, cts.Token);
        var kept = figures.GetAsync(build, Ct);
        await reached.Task.WaitAsync(Guard);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        Assert.Equal(await BruteForceAsync(inner, build), await kept.WaitAsync(Guard));
        Assert.Equal(1, source.Count(nameof(IObjectSource.HasKeysAsync)));
    }

    [Fact]
    public async Task AMeasurementEveryWaiterLeftIsForgotten()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var source = new TestObjectSource(inner);
        var build = await OpenAsync(b, source);
        source.Reset();
        await using var figures = new DownloadedFigures(source, inner);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterHasKeys = async (_, ct) =>
        {
            reached.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.TrySetResult(); }
        };
        using var cts = new CancellationTokenSource();
        var cancelled = figures.GetAsync(build, cts.Token);
        await reached.Task.WaitAsync(Guard);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await stopped.Task.WaitAsync(Guard);
        source.AfterHasKeys = null;
        Assert.Equal(await BruteForceAsync(inner, build), await figures.GetAsync(build, Ct));
        Assert.Equal(2, source.Count(nameof(IObjectSource.HasKeysAsync)));
    }

    [Fact]
    public async Task AFailingHandlerDoesNotStopThePump()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner);
        var before = await figures.GetAsync(build, Ct);
        figures.Changed += (_, _) => throw new InvalidOperationException("handler failure");
        var first = When(figures, build, f => f.StoredObjects == before.StoredObjects + 1);
        await FetchAsync(FileKey(b, 200));
        await first;
        var second = When(figures, build, f => f.StoredObjects == before.StoredObjects + 2);
        await FetchAsync(FileKey(b, 300));
        await second;
    }

    [Fact]
    public async Task MeasurementsBeyondTheLimitQueue()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var source = new TestObjectSource(inner);
        var buildA = await OpenAsync(a, source);
        var buildB = await OpenAsync(b, source);
        await using var figures = new DownloadedFigures(source, inner, new() { BatchKeys = 1, MaxMeasurements = 1 });
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        figures.Queued = () => queued.TrySetResult();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inside = 0, most = 0, calls = 0;
        source.AfterHasKeys = async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inside);
            InterlockedMax(ref most, now);
            // The first call (build A's) stays inside until build B's measurement is queued behind it.
            if (Interlocked.Increment(ref calls) == 1) { reached.TrySetResult(); await queued.Task.WaitAsync(ct); }
            Interlocked.Decrement(ref inside);
        };
        var first = figures.RefreshAsync(buildA, Ct);
        await reached.Task.WaitAsync(Guard);
        var second = figures.RefreshAsync(buildB, Ct);
        Assert.Equal(await BruteForceAsync(inner, buildA), await first.WaitAsync(Guard));
        Assert.Equal(await BruteForceAsync(inner, buildB), await second.WaitAsync(Guard));
        Assert.Equal(1, most);
        Assert.Equal(12, calls);
    }

    [Fact]
    public async Task RepeatedOverflowQueuesOneMeasurementPerBuild()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var source = new TestObjectSource(inner);
        var buildA = await OpenAsync(a, source);
        var buildB = await OpenAsync(b, source);
        await using var figures = new DownloadedFigures(source, inner, new() { MaxMeasurements = 1, QueueCapacity = 1 });
        await figures.GetAsync(buildA, Ct);
        await figures.GetAsync(buildB, Ct);

        // A refresh of A holds the only slot inside HasKeys, so every re-measure stays queued.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterHasKeys = async (_, ct) => { reached.TrySetResult(); await release.Task.WaitAsync(ct); };
        var refresh = figures.RefreshAsync(buildA, Ct);
        await reached.Task.WaitAsync(Guard);

        // Each round holds the pump in a Changed handler so the one-slot queue overflows.
        TaskCompletionSource? entered = null;
        ManualResetEventSlim? pumpGate = null;
        figures.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref entered, null) is not { } e) return;
            e.TrySetResult();
            pumpGate!.Wait(Guard);
        };
        async Task OverflowAsync(Func<Task> first, params Func<Task>[] more)
        {
            using var held = new ManualResetEventSlim();
            pumpGate = held;
            var inHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref entered, inHandler);
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            figures.Remeasured = () => handled.TrySetResult();
            await first();
            await inHandler.Task.WaitAsync(Guard);
            foreach (var step in more) await step();
            held.Set();
            await handled.Task.WaitAsync(Guard);
        }
        await OverflowAsync(() => FetchAsync(FileKey(a, 100)), () => FetchAsync(FileKey(a, 200)), () => FetchAsync(FileKey(a, 300)));
        Assert.Equal(1, figures.WaitingCount(buildA.Build));
        Assert.Equal(1, figures.WaitingCount(buildB.Build));
        await OverflowAsync(() => { inner.Remove(FileKey(a, 100)); return Task.CompletedTask; },
            () => { inner.Remove(FileKey(a, 200)); return Task.CompletedTask; }, () => { inner.Remove(FileKey(a, 300)); return Task.CompletedTask; });
        Assert.Equal(1, figures.WaitingCount(buildA.Build));
        Assert.Equal(1, figures.WaitingCount(buildB.Build));

        source.AfterHasKeys = null;
        release.SetResult();
        await refresh.WaitAsync(Guard);
    }

    [Fact]
    public async Task ARefreshTheOverflowReliedOnStillRunsWhenItsCallerLeaves()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var source = new TestObjectSource(inner);
        var buildA = await OpenAsync(a, source);
        var buildB = await OpenAsync(b, source);
        await using var figures = new DownloadedFigures(source, inner, new() { MaxMeasurements = 1, QueueCapacity = 1 });
        await figures.GetAsync(buildA, Ct);
        await figures.GetAsync(buildB, Ct);

        // B's refresh holds the only slot, so A's refresh waits for it without reading.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        source.AfterHasKeys = async (_, _) => { if (Interlocked.Increment(ref calls) == 1) { reached.TrySetResult(); await release.Task; } };
        var holding = figures.RefreshAsync(buildB, Ct);
        await reached.Task.WaitAsync(Guard);
        using var leave = new CancellationTokenSource();
        var refresh = figures.RefreshAsync(buildA, leave.Token);
        Assert.Equal(1, figures.WaitingCount(buildA.Build));

        // Overflow: the pump relies on A's waiting refresh instead of starting another.
        using var pumpGate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        figures.Changed += (_, _) => { if (Interlocked.Exchange(ref held, 1) == 0) { entered.TrySetResult(); pumpGate.Wait(Guard); } };
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        figures.Remeasured = () => handled.TrySetResult();
        await FetchAsync(FileKey(a, 100));
        await entered.Task.WaitAsync(Guard);
        await FetchAsync(FileKey(a, 200));
        await FetchAsync(FileKey(a, 300));
        await FetchAsync(FileKey(a, 400));
        pumpGate.Set();
        await handled.Task.WaitAsync(Guard);
        Assert.Equal(1, figures.WaitingCount(buildA.Build));

        // The caller leaves; the measurement must still run, or the dropped commits stay missing.
        var measured = When(figures, buildA, f => f.StoredObjects == 5);
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        release.SetResult();
        await holding.WaitAsync(Guard);
        await measured;
        Assert.Equal(await BruteForceAsync(inner, buildA), await figures.GetAsync(buildA, Ct));
    }

    [Fact]
    public async Task APinnedMeasurementOfADroppedBuildStopsWhenItsLastCallerLeaves()
    {
        var a = new SyntheticBuild(buildName: "WOW-1002patch1.0.0_Synthetic", files: [SyntheticBuild.StandardFiles[1]]).LoadInto(inner);
        var b = new SyntheticBuild().LoadInto(inner);
        var c = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var source = new TestObjectSource(inner);
        var buildA = await OpenAsync(a, source);
        var buildB = await OpenAsync(b, source);
        var buildC = await OpenAsync(c, source);
        await using var figures = new DownloadedFigures(source, inner, new() { MaxMeasurements = 1, QueueCapacity = 1, MaxBuilds = 2 });
        await figures.GetAsync(buildA, Ct);
        await figures.GetAsync(buildB, Ct);

        // B's refresh holds the only slot; A's refresh waits behind it.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var readA = 0;
        source.AfterHasKeys = async (keys, _) =>
        {
            if (keys.Contains(a.RootKey)) Interlocked.Increment(ref readA);
            if (Interlocked.Increment(ref calls) == 1) { reached.TrySetResult(); await release.Task; }
        };
        var holding = figures.RefreshAsync(buildB, Ct);
        await reached.Task.WaitAsync(Guard);
        using var leave = new CancellationTokenSource();
        var refresh = figures.RefreshAsync(buildA, leave.Token);

        // Overflow, so the pump pins A's waiting refresh.
        using var pumpGate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        figures.Changed += (_, _) => { if (Interlocked.Exchange(ref held, 1) == 0) { entered.TrySetResult(); pumpGate.Wait(Guard); } };
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        figures.Remeasured = () => handled.TrySetResult();
        await FetchAsync(FileKey(b, 100));
        await entered.Task.WaitAsync(Guard);
        await FetchAsync(FileKey(b, 300));
        await FetchAsync(FileKey(b, 400));
        pumpGate.Set();
        await handled.Task.WaitAsync(Guard);
        Assert.Equal(1, figures.WaitingCount(buildA.Build));

        // B is used last, so tracking C drops A; then A's only caller leaves.
        await figures.GetAsync(buildB, Ct);
        var measuringC = figures.GetAsync(buildC, Ct);
        Assert.Equal(0, figures.WaitingCount(buildA.Build));
        leave.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        // The slot goes to C next; A's measurement, first in line, was cancelled and never reads.
        release.SetResult();
        await holding.WaitAsync(Guard);
        Assert.Equal(await BruteForceAsync(inner, buildC), await measuringC.WaitAsync(Guard));
        Assert.Equal(0, Volatile.Read(ref readA));
    }

    [Fact]
    public async Task DisposeWaitsForADroppedBuildsMeasurementAndRaisesNothingAfter()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var source = new TestObjectSource(inner);
        var buildA = await OpenAsync(a, source);
        var buildB = await OpenAsync(b, source);
        var figures = new DownloadedFigures(source, inner, new() { MaxBuilds = 1 });
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        // A's measurement (the first HasKeys) finishes only when the test says, whatever its token.
        source.AfterHasKeys = async (_, _) => { if (Interlocked.Increment(ref calls) == 1) { reached.TrySetResult(); await release.Task; } };
        var disposing = false;
        var late = 0;
        figures.Changed += (_, _) => { if (Volatile.Read(ref disposing)) Interlocked.Increment(ref late); };
        var waiter = figures.GetAsync(buildA, Ct);
        await reached.Task.WaitAsync(Guard);
        await figures.GetAsync(buildB, Ct); // drops A; its measurement keeps running for its waiter
        Volatile.Write(ref disposing, true);
        var disposed = figures.DisposeAsync().AsTask();
        Assert.False(disposed.IsCompleted);
        release.SetResult();
        await disposed.WaitAsync(Guard);
        Assert.True(waiter.IsCompleted);
        // It finished its one batch, or saw the stop; never the disposed gate.
        if (waiter.IsCompletedSuccessfully) Assert.Equal(6, (await waiter).TotalObjects);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(0, late);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        for (var seen = Volatile.Read(ref target); value > seen; seen = Volatile.Read(ref target))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }

    [Fact]
    public async Task TracksAtMostMaxBuilds()
    {
        var a = new SyntheticBuild().LoadInto(inner);
        var b = new SyntheticBuild(buildName: "WOW-1001patch1.0.0_Synthetic").LoadInto(inner);
        var buildA = await OpenAsync(a);
        var buildB = await OpenAsync(b);
        await using var figures = new DownloadedFigures(inner, inner, new() { MaxBuilds = 1 });
        await figures.GetAsync(buildA, Ct);
        var calls = inner.HasKeysCalls;
        await figures.GetAsync(buildA, Ct);
        Assert.Equal(calls, inner.HasKeysCalls);
        await figures.GetAsync(buildB, Ct);
        await figures.GetAsync(buildA, Ct);
        Assert.Equal(calls + 2, inner.HasKeysCalls);
    }

    [Fact]
    public async Task OnlyAnOpenedBuildHasALedger()
    {
        var b = new SyntheticBuild();
        var fake = (await new InMemoryBuilds().Add(b.Ref, b.ToFake()).OpenAsync(b.Ref, Ct)).Opened!;
        await using var figures = new DownloadedFigures(inner, inner);
        await Assert.ThrowsAsync<ArgumentException>(() => figures.GetAsync(fake, Ct));
    }

    private sealed class LedgerBuild(BuildRef build, ObjectLedger ledger) : IBuild, ILedgerSource
    {
        public BuildRef Build { get; } = build;
        public ObjectLedger Ledger { get; } = ledger;
        public BuildMetadata Metadata => throw new NotSupportedException();
        public CdnContext? Cdn => null;
        public FileResolution Resolve(uint fileDataId) => throw new NotSupportedException();
        public FileResolution Resolve(string path) => throw new NotSupportedException();
        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => throw new NotSupportedException();
    }

    [DownloadedScaleFact]
    [Trait("Speed", "Slow")]
    public async Task MeasuresARetailSizedLedgerUnderTenSeconds()
    {
        const int rows = 2_900_000;
        var keys = new CdnKey[rows];
        var sizes = new long[rows];
        var bytes = new byte[16];
        var shared = new byte[1];
        long stored = 0, storedBytes = 0;
        for (var i = 0; i < rows; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)i + 1);
            keys[i] = new CdnKey(bytes);
            sizes[i] = 1000 + i % 4096;
            if (i % 3 != 0) continue;
            inner.AddStored(new ObjectKey(ObjectKeyType.Encoded, bytes), shared);
            stored++; storedBytes += sizes[i];
        }
        var ledger = new ObjectLedger(keys, sizes);
        await using var figures = new DownloadedFigures(inner, inner);
        var watch = Stopwatch.StartNew();
        var figure = await figures.GetAsync(new LedgerBuild(new SyntheticBuild().Ref, ledger), Ct);
        watch.Stop();
        output.WriteLine($"Measured {rows:N0} rows in {watch.Elapsed.TotalSeconds:F2} s.");
        Assert.Equal(new DownloadedFigure(stored, rows, storedBytes, ledger.TotalBytes), figure);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }
}
