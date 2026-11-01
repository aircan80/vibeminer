using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

public sealed class TablePreloadsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));
    private static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32));
    private const string Locale = "enUS";

    [Fact]
    public async Task StartingTwiceReturnsTheRunningJob()
    {
        var tables = new Tables { Preload = Stoppable };
        await using var preloads = new TablePreloads(tables);
        var job = preloads.Start(Build, Locale);
        Assert.Same(job, preloads.Start(Build, Locale));
        Assert.Same(job, preloads.Find(Build, Locale));
        Assert.Null(preloads.Find(Build, "deDE"));
        await tables.Preloading.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Canceled, (await preloads.StopAsync(Build, Locale))!.Outcome);
        Assert.Equal(1, tables.Opens);
    }

    [Fact]
    public async Task MoreJobsThanTheLimitThrow()
    {
        var tables = new Tables { Preload = Stoppable };
        await using var preloads = new TablePreloads(tables, new() { MaxJobs = 2 });
        preloads.Start(Build, Locale);
        preloads.Start(Build, "deDE");
        Assert.Throws<InvalidOperationException>(() => preloads.Start(Other, Locale));
        await preloads.StopAsync(Build, "deDE");
        Assert.Equal(Other, preloads.Start(Other, Locale).Build);
    }

    [Fact]
    public async Task BadArgumentsThrow()
    {
        Assert.Throws<ArgumentException>(() => new TablePreloads(new Tables(), new() { MaxJobs = 0 }));
        Assert.Throws<ArgumentException>(() => new TablePreloads(new Tables(), new() { ProgressInterval = TimeSpan.FromTicks(-1) }));
        await using var preloads = new TablePreloads(new Tables());
        Assert.Throws<ArgumentException>(() => preloads.Start(Build, "xxXX"));
    }

    [Fact]
    public async Task StoppingMidRunCancelsAndALaterStartRunsAgain()
    {
        var tables = new Tables { Preload = Stoppable };
        await using var preloads = new TablePreloads(tables);
        var first = preloads.Start(Build, Locale);
        await tables.Preloading.WaitAsync(Guard);

        var stopped = await preloads.StopAsync(Build, Locale);
        Assert.Equal(TablePreloadOutcome.Canceled, stopped!.Outcome);
        Assert.Same(stopped, await first.Completion);
        Assert.Null(preloads.Find(Build, Locale));
        Assert.Same(stopped, preloads.Last(Build, Locale));
        Assert.Same(stopped, await preloads.StopAsync(Build, Locale));
        Assert.Equal(1, tables.Disposals);

        tables.Preload = (progress, _) => Task.FromResult(Result(TablePreloadOutcome.Completed, Figures(TablePreloadStage.Preparing, 3)));
        var second = preloads.Start(Build, Locale);
        Assert.NotSame(first, second);
        var done = await second.Completion.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Completed, done.Outcome);
        Assert.Same(done, preloads.Last(Build, Locale));
        Assert.Equal(2, tables.Preloads);
        Assert.Equal(2, tables.Disposals);
    }

    [Fact]
    public async Task StoppingWhileOpeningCancels()
    {
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tables = new Tables
        {
            Open = async ct =>
            {
                opening.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var reports = new Reports();
        await using var preloads = new TablePreloads(tables);
        var job = preloads.Start(Build, Locale, reports);
        await opening.Task.WaitAsync(Guard);
        Assert.Equal(TablePreloadStage.Opening, job.Latest.Stage);

        var result = await preloads.StopAsync(Build, Locale);
        Assert.Equal(TablePreloadOutcome.Canceled, result!.Outcome);
        Assert.Null(result.OpenState);
        Assert.Equal(0, tables.Preloads);
        Assert.Equal(TablePreloadStage.Opening, result.Final.Stage);
        Assert.Equal(result.Final, reports.All[^1]);
    }

    [Fact]
    public async Task ABuildThatDoesNotOpenFailsWithItsState()
    {
        var tables = new Tables { Open = _ => Task.FromResult(new TablesOpenResult(Build, Locale, FilesOpenState.NeedsNetwork, null, "CDN_OFFLINE", "offline", null)) };
        var reports = new Reports();
        await using var preloads = new TablePreloads(tables);
        var result = await preloads.Start(Build, Locale, reports).Completion.WaitAsync(Guard);

        Assert.Equal(TablePreloadOutcome.Failed, result.Outcome);
        Assert.Equal(FilesOpenState.NeedsNetwork, result.OpenState);
        Assert.Equal("CDN_OFFLINE", result.Code);
        Assert.Equal("offline", result.Message);
        Assert.Equal(new TablePreloadProgress(TablePreloadStage.Opening, 0, 0, 0, 0, 0, 0, 0, 0, 0), result.Final);
        Assert.Equal([result.Final, result.Final], reports.All);
        Assert.Equal(0, tables.Preloads);
    }

    [Fact]
    public async Task AnUnexpectedThrowFailsAndStillDisposesTheHandle()
    {
        var tables = new Tables { Preload = (_, _) => throw new InvalidOperationException("broken") };
        await using var preloads = new TablePreloads(tables);
        var result = await preloads.Start(Build, Locale).Completion.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Failed, result.Outcome);
        Assert.Equal("TABLE_PRELOAD_FAILED", result.Code);
        Assert.Contains("broken", result.Message);
        Assert.Null(result.OpenState);
        Assert.Equal(1, tables.Disposals);
        Assert.Null(preloads.Find(Build, Locale));
    }

    [Fact]
    public async Task TheInnerResultIsKept()
    {
        TableEntry[] failed = [new(7, "Spell", TableState.Corrupt, true, null, null, null)];
        var tables = new Tables
        {
            Preload = (_, _) => Task.FromResult(new TablePreloadResult(TablePreloadOutcome.Incomplete, Figures(TablePreloadStage.Preparing, 2), failed, null, "X", "one table")),
        };
        await using var preloads = new TablePreloads(tables);
        var result = await preloads.Start(Build, Locale).Completion.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Incomplete, result.Outcome);
        Assert.Equal(failed, result.Tables);
        Assert.Equal(("X", "one table"), (result.Code, result.Message));
        Assert.Equal(Figures(TablePreloadStage.Preparing, 2), result.Final);
    }

    [Fact]
    public async Task ProgressNeverGoesBackwards()
    {
        var tables = new Tables
        {
            Preload = (progress, _) =>
            {
                progress!.Report(new(TablePreloadStage.Downloading, 5, 10, 1, 500, 1000, 600, 0, 0, 0));
                progress.Report(new(TablePreloadStage.Downloading, 3, 10, 0, 400, 1000, 700, 0, 0, 0));
                progress.Report(new(TablePreloadStage.Preparing, 10, 10, 1, 1000, 1000, 1100, 2, 8, 0));
                progress.Report(new(TablePreloadStage.Downloading, 4, 9, 0, 0, 0, 0, 1, 8, 1));
                // A final lower than what was shown.
                return Task.FromResult(Result(TablePreloadOutcome.Completed, new(TablePreloadStage.Preparing, 10, 10, 0, 1000, 1000, 1100, 8, 8, 0)));
            },
        };
        var reports = new Reports();
        await using var preloads = new TablePreloads(tables, new() { ProgressInterval = TimeSpan.Zero });
        var job = preloads.Start(Build, Locale, reports);
        var result = await job.Completion.WaitAsync(Guard);

        var seen = reports.All;
        for (var i = 1; i < seen.Count; i++)
        {
            var (a, b) = (seen[i - 1], seen[i]);
            Assert.True(b.Stage >= a.Stage && b.FilesDone >= a.FilesDone && b.FilesTotal >= a.FilesTotal && b.FilesFailed >= a.FilesFailed
                && b.BytesDone >= a.BytesDone && b.BytesTotal >= a.BytesTotal && b.CdnBytes >= a.CdnBytes && b.TablesDone >= a.TablesDone
                && b.TablesTotal >= a.TablesTotal && b.TablesFailed >= a.TablesFailed, $"report {i} went down: {a} -> {b}");
        }
        Assert.Equal(new TablePreloadProgress(TablePreloadStage.Preparing, 10, 10, 1, 1000, 1000, 1100, 8, 8, 1), result.Final);
        Assert.Equal(result.Final, seen[^1]);
        Assert.Equal(result.Final, job.Latest);
        Assert.Equal([TablePreloadStage.Opening, TablePreloadStage.Downloading, TablePreloadStage.Downloading, TablePreloadStage.Preparing,
            TablePreloadStage.Preparing, TablePreloadStage.Preparing], seen.Select(p => p.Stage));
    }

    [Fact]
    public async Task ProgressIsCoalescedButStageChangesAndTheFinalAreDelivered()
    {
        var clock = new ManualClock();
        var interval = TimeSpan.FromMilliseconds(100);
        var tables = new Tables
        {
            Preload = (progress, _) =>
            {
                progress!.Report(Figures(TablePreloadStage.Downloading, 1)); // stage change: delivered
                progress.Report(Figures(TablePreloadStage.Downloading, 2));  // inside the interval: dropped
                clock.Advance(interval);
                progress.Report(Figures(TablePreloadStage.Downloading, 3));  // delivered
                progress.Report(Figures(TablePreloadStage.Downloading, 4));  // dropped
                progress.Report(Figures(TablePreloadStage.Preparing, 5));    // stage change: delivered
                progress.Report(Figures(TablePreloadStage.Preparing, 6));    // dropped
                return Task.FromResult(Result(TablePreloadOutcome.Completed, Figures(TablePreloadStage.Preparing, 7)));
            },
        };
        var reports = new Reports();
        await using var preloads = new TablePreloads(tables, new() { ProgressInterval = interval, Time = clock });
        var result = await preloads.Start(Build, Locale, reports).Completion.WaitAsync(Guard);

        Assert.Equal([new(TablePreloadStage.Opening, 0, 0, 0, 0, 0, 0, 0, 0, 0), Figures(TablePreloadStage.Downloading, 1),
            Figures(TablePreloadStage.Downloading, 3), Figures(TablePreloadStage.Preparing, 5), Figures(TablePreloadStage.Preparing, 7)], reports.All);
        Assert.Equal(result.Final, reports.All[^1]);
    }

    [Fact]
    public async Task ASlowSubscriberSeesTheFinalBeforeCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reports = new Reports(_ => { entered.TrySetResult(); Assert.True(release.Wait(Guard)); });
        var tables = new Tables
        {
            Preload = (progress, _) =>
            {
                progress!.Report(Figures(TablePreloadStage.Preparing, 4));
                return Task.FromResult(Result(TablePreloadOutcome.Completed, Figures(TablePreloadStage.Preparing, 4)));
            },
        };
        await using var preloads = new TablePreloads(tables);
        var job = preloads.Start(Build, Locale, reports);
        await entered.Task.WaitAsync(Guard);

        // The preload is done, but the subscriber still holds its first report, so the job isn't.
        await tables.Preloaded.WaitAsync(Guard);
        Assert.Equal(Figures(TablePreloadStage.Preparing, 4), job.Latest);
        Assert.False(job.Completion.IsCompleted);

        release.Set();
        var result = await job.Completion.WaitAsync(Guard);
        Assert.Equal(result.Final, reports.All[^1]);
    }

    [Fact]
    public async Task AThrowingSubscriberDoesNotBreakTheJob()
    {
        var tables = new Tables
        {
            Preload = (progress, _) =>
            {
                progress!.Report(Figures(TablePreloadStage.Downloading, 1));
                return Task.FromResult(Result(TablePreloadOutcome.Completed, Figures(TablePreloadStage.Preparing, 2)));
            },
        };
        var calls = 0;
        var throwing = new Reports(_ => { }, () => { Interlocked.Increment(ref calls); throw new InvalidOperationException("subscriber"); });
        await using var preloads = new TablePreloads(tables, new() { ProgressInterval = TimeSpan.Zero });
        var job = preloads.Start(Build, Locale, throwing);
        var result = await job.Completion.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Completed, result.Outcome);
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal(result.Final, throwing.All[^1]);
    }

    [Fact]
    public async Task DisposingStopsEveryJobAndWaits()
    {
        var tables = new Tables { Preload = Stoppable };
        var preloads = new TablePreloads(tables);
        var a = preloads.Start(Build, Locale);
        var b = preloads.Start(Other, Locale);
        await tables.PreloadingCount(2).WaitAsync(Guard);

        await preloads.DisposeAsync();
        Assert.True(a.Completion.IsCompletedSuccessfully && b.Completion.IsCompletedSuccessfully);
        Assert.Equal(TablePreloadOutcome.Canceled, (await a.Completion).Outcome);
        Assert.Equal(TablePreloadOutcome.Canceled, (await b.Completion).Outcome);
        Assert.Equal(2, tables.Disposals);
        Assert.Throws<ObjectDisposedException>(() => preloads.Start(Build, Locale));
        await preloads.DisposeAsync();
    }

    private static TablePreloadProgress Figures(TablePreloadStage stage, int n) => new(stage, n, 10, 0, n * 100, 1000, n * 110, n, 10, 0);

    private static TablePreloadResult Result(TablePreloadOutcome outcome, TablePreloadProgress final) => new(outcome, final, [], null, null, null);

    /// <summary>A preload that waits for its cancellation, then answers Canceled with what it reported.</summary>
    private static async Task<TablePreloadResult> Stoppable(IProgress<TablePreloadProgress>? progress, CancellationToken ct)
    {
        var at = Figures(TablePreloadStage.Downloading, 1);
        progress?.Report(at);
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        return Result(TablePreloadOutcome.Canceled, at);
    }

    /// <summary>
    /// An <see cref="ITables"/> whose handles only preload, through <see cref="Preload"/>; everything else on them throws.
    /// <see cref="Open"/> replaces the open; by default every build opens.
    /// </summary>
    private sealed class Tables : ITables
    {
        private int opens, preloads, disposals;
        private readonly Lock gate = new();
        private readonly List<(int Count, TaskCompletionSource Seen)> waits = [];
        private readonly TaskCompletionSource preloaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task<TablesOpenResult>>? Open { get; set; }
        public Func<IProgress<TablePreloadProgress>?, CancellationToken, Task<TablePreloadResult>> Preload { get; set; } =
            (_, _) => Task.FromResult(Result(TablePreloadOutcome.Completed, Figures(TablePreloadStage.Preparing, 10)));
        public int Opens => Volatile.Read(ref opens);
        public int Preloads => Volatile.Read(ref preloads);
        public int Disposals => Volatile.Read(ref disposals);
        public Task Preloading => PreloadingCount(1);
        /// <summary>Completes once a preload call returned.</summary>
        public Task Preloaded => preloaded.Task;

        public Task PreloadingCount(int count)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (preloads >= count) seen.SetResult();
                else waits.Add((count, seen));
            }
            return seen.Task;
        }

        public Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            return Open?.Invoke(cancellationToken)
                ?? Task.FromResult(new TablesOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale), null, null, null));
        }

        private async Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken ct)
        {
            lock (gate)
            {
                preloads++;
                foreach (var w in waits.Where(w => preloads >= w.Count).ToArray()) { waits.Remove(w); w.Seen.TrySetResult(); }
            }
            try { return await Preload(progress, ct); }
            finally { preloaded.TrySetResult(); }
        }

        private sealed class Handle(Tables owner, BuildRef build, string locale) : IBuildTables
        {
            public BuildRef Build => build;
            public string Locale => locale;
            public Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken) =>
                owner.PreloadAsync(progress, cancellationToken);
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.disposals); return ValueTask.CompletedTask; }
            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Collects reports synchronously, unlike <see cref="Progress{T}"/>, which posts them. <paramref name="onFirst"/> runs inside
    /// the first report; <paramref name="after"/> runs after each one is recorded.
    /// </summary>
    private sealed class Reports(Action<TablePreloadProgress>? onFirst = null, Action? after = null) : IProgress<TablePreloadProgress>
    {
        private readonly Lock gate = new();
        private readonly List<TablePreloadProgress> all = [];
        public IReadOnlyList<TablePreloadProgress> All { get { lock (gate) return all.ToArray(); } }

        public void Report(TablePreloadProgress value)
        {
            bool first;
            lock (gate) { first = all.Count == 0; all.Add(value); }
            if (first) onFirst?.Invoke(value);
            after?.Invoke();
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
