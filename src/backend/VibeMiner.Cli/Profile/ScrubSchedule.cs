using VibeMiner.Objects;

namespace VibeMiner.Cli.Profile;

internal sealed record ScrubScheduleOptions
{
    /// <summary>Time from one finished scrub to the next.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(7);
    /// <summary>How often to ask whether interactive work is running, before and during a scrub.</summary>
    public TimeSpan IdleCheck { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>When there is no readable record, the first scrub is due this long after <see cref="ScrubSchedule.Start"/>.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Runs layer 1's scrub in the background at the lowest priority. It is due <see cref="ScrubScheduleOptions.Interval"/> after the
/// last record's finish (or <see cref="ScrubScheduleOptions.FirstDelay"/> after start without one), starts only while
/// <c>busy()</c> is false, and is canceled and later rerun from the start when interactive work begins. A finished or failed scrub
/// writes <c>scrub-v1.json</c>; a paused or stopped one writes nothing. One scrub at a time; every wait goes through the TimeProvider.
/// </summary>
internal sealed class ScrubSchedule : IAsyncDisposable
{
    // Task.Delay through a TimeProvider takes at most ~49 days; waking daily and rechecking keeps any interval safe.
    private static readonly TimeSpan MaxSleep = TimeSpan.FromDays(1);

    private readonly Func<IProgress<ScanProgress>?, CancellationToken, Task<ScrubReport>> scrub;
    private readonly string dataFolder;
    private readonly TimeProvider time;
    private readonly Func<bool> busy;
    private readonly ScrubScheduleOptions options;
    private readonly object gate = new();
    private readonly LatestProgress progress = new();
    private CancellationTokenSource? stop;
    private Task? loop;
    private bool disposed;
    private volatile ScrubRecordRead? last;
    private volatile bool running;

    public ScrubSchedule(Func<IProgress<ScanProgress>?, CancellationToken, Task<ScrubReport>> scrub, string dataFolder, TimeProvider time,
        Func<bool> busy, ScrubScheduleOptions? options = null)
    {
        this.scrub = scrub; this.dataFolder = dataFolder; this.time = time; this.busy = busy; this.options = options ?? new();
        if (this.options.Interval <= TimeSpan.Zero || this.options.IdleCheck <= TimeSpan.Zero || this.options.FirstDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Interval and IdleCheck must be positive and FirstDelay not negative.");
    }

    /// <summary>The latest record, or the state of the record file; null until the loop has read it.</summary>
    public ScrubRecordRead? Last => last;
    /// <summary>A scrub is running now.</summary>
    public bool Running => running;
    /// <summary>The latest progress of the running scrub, if it reported any.</summary>
    public ScanProgress? Progress => progress.Latest;

    /// <summary>
    /// Starts the loop. A second call while it runs does nothing; after <see cref="StopAsync"/> has finished it starts again. A call
    /// while a stop is still waiting for the loop to end throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loop is { IsCompleted: false })
            {
                if (stop is { IsCancellationRequested: true }) throw new InvalidOperationException("The scrub schedule is still stopping.");
                return;
            }
            stop?.Dispose();
            stop = new CancellationTokenSource();
            var token = stop.Token;
            var started = time.GetUtcNow();
            loop = Task.Run(() => LoopAsync(started, token), CancellationToken.None);
        }
    }

    /// <summary>Cancels the loop and any running scrub and waits for both to end. Never throws.</summary>
    public async Task StopAsync()
    {
        Task? ending;
        lock (gate)
        {
            ending = loop;
            try { stop?.Cancel(); } catch (AggregateException) { }
        }
        if (ending is not null) await ending.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await StopAsync().ConfigureAwait(false);
        lock (gate) { stop?.Dispose(); stop = null; }
    }

    private async Task LoopAsync(DateTimeOffset started, CancellationToken ct)
    {
        try
        {
            last = await ScrubRecords.ReadAsync(dataFolder, ct).ConfigureAwait(false);
            var due = last is { State: ScrubRecordState.Present, Record: { } read } ? DueAfter(read) : Plus(started, options.FirstDelay);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var wait = due - time.GetUtcNow();
                if (wait > TimeSpan.Zero) { await Task.Delay(wait < MaxSleep ? wait : MaxSleep, time, ct).ConfigureAwait(false); continue; }
                if (IsBusy()) { await Task.Delay(options.IdleCheck, time, ct).ConfigureAwait(false); continue; }
                var finished = await RunOnceAsync(ct).ConfigureAwait(false);
                if (finished is null) continue; // paused for interactive work: wait for idle and rerun from the start
                try { await ScrubRecords.WriteAsync(dataFolder, finished, ct).ConfigureAwait(false); }
                catch (Exception e) when (e is not OperationCanceledException) { } // keep the record in memory; the next scrub is still timed from it
                last = new ScrubRecordRead(ScrubRecordState.Present, finished);
                due = DueAfter(finished);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { } // the loop must never fault StopAsync; a broken folder just leaves Last as it was
    }

    /// <summary>A record's finish plus the interval, but never later than now plus the interval (a clock that jumped back, or a bad record).</summary>
    private DateTimeOffset DueAfter(ScrubRecord record)
    {
        var fromRecord = Plus(record.FinishedAt, options.Interval);
        var fromNow = Plus(time.GetUtcNow(), options.Interval);
        return fromRecord < fromNow ? fromRecord : fromNow;
    }

    private static DateTimeOffset Plus(DateTimeOffset at, TimeSpan by) => at > DateTimeOffset.MaxValue - by ? DateTimeOffset.MaxValue : at + by;

    /// <summary>One scrub. Null when it was canceled because interactive work started; throws when the schedule is stopping.</summary>
    private async Task<ScrubRecord?> RunOnceAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var polls = CancellationTokenSource.CreateLinkedTokenSource(run.Token); // ends the pending busy poll when the run ends
        running = true;
        progress.Latest = null;
        try
        {
            Task<ScrubReport> task;
            try { task = scrub(progress, run.Token); }
            catch (Exception e) { task = Task.FromException<ScrubReport>(e); }

            while (!task.IsCompleted)
            {
                var tick = Task.Delay(options.IdleCheck, time, polls.Token);
                await Task.WhenAny(task, tick).ConfigureAwait(false);
                if (task.IsCompleted) break;
                if (ct.IsCancellationRequested || IsBusy()) { run.Cancel(); break; }
            }

            polls.Cancel();
            ScrubReport report;
            try { report = await task.ConfigureAwait(false); }
            catch (Exception) when (run.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
            catch (Exception e) // an OperationCanceledException we didn't ask for is a failure too
            {
                ct.ThrowIfCancellationRequested();
                return ScrubRecord.FromFailure(e, time.GetUtcNow());
            }
            ct.ThrowIfCancellationRequested();
            return ScrubRecord.FromReport(report, time.GetUtcNow());
        }
        finally { running = false; }
    }

    /// <summary>A busy check that throws counts as busy: the scrub is the lowest priority, so it never runs over a broken check.</summary>
    private bool IsBusy()
    {
        try { return busy(); }
        catch (Exception) { return true; }
    }

    /// <summary>Keeps only the newest report; never posts to a sync context and never queues.</summary>
    private sealed class LatestProgress : IProgress<ScanProgress>
    {
        private volatile ScanProgress? latest;
        public ScanProgress? Latest { get => latest; set => latest = value; }
        public void Report(ScanProgress value) => latest = value;
    }
}
