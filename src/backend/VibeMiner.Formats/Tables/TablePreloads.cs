using VibeMiner.Builds;
using VibeMiner.Files;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// <see cref="MaxJobs"/>: build and locale preloads running at once. <see cref="ProgressInterval"/>: least time between two
/// progress reports (stage changes and the final one are always reported). <see cref="Time"/> times them.
/// </summary>
public sealed record TablePreloadOptions
{
    public int MaxJobs { get; init; } = 4;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeProvider Time { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (MaxJobs < 1 || ProgressInterval < TimeSpan.Zero || Time is null)
            throw new ArgumentException("Table preload options need a positive job limit, a non-negative interval and a clock.");
    }
}

/// <summary>
/// Per build and locale table preloads (D360), run like Download all (D230). A job opens the build's tables through
/// <see cref="ITables.ForAsync"/> (stage Opening), then runs the handle's <see cref="IBuildTables.PreloadAsync"/> (Downloading,
/// then Preparing) and disposes the handle. Stopping (<see cref="StopAsync"/>) keeps what was stored and prepared, and a new
/// job starts from there. One job per build and locale; a finished job leaves the table, so <see cref="Start"/> runs it again.
/// </summary>
public sealed class TablePreloads : IAsyncDisposable
{
    /// <summary>Terminal results kept for <see cref="StopAsync"/> and <see cref="Last"/> after their job left the table.</summary>
    private const int KeptResults = 64;
    private readonly ITables tables;
    private readonly TablePreloadOptions options;
    private readonly Lock gate = new();
    private readonly Dictionary<Key, TablePreloadJob> running = [];
    private readonly Dictionary<Key, TablePreloadResult> results = [];
    private readonly Queue<Key> resultOrder = new();
    private readonly CancellationTokenSource stop = new();
    private bool disposed;

    public TablePreloads(ITables tables, TablePreloadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        this.tables = tables;
        this.options = options ?? new();
        this.options.Validate();
    }

    /// <summary>
    /// Starts preloading the build's tables for the locale (a <see cref="FileLocales"/> name; an unregistered one throws
    /// <see cref="ArgumentException"/>), or returns its running job. Throws <see cref="InvalidOperationException"/> past
    /// <see cref="TablePreloadOptions.MaxJobs"/>. <paramref name="progress"/> is called on a thread-pool thread, never under the
    /// job's lock or on the preload's own threads, one call at a time and in order; a slow subscriber only sees fewer, newer
    /// reports (stage changes and the final one are always delivered, the final last). Its exceptions are swallowed. The job's
    /// <see cref="TablePreloadJob.Completion"/> completes after the final report was delivered. A running job keeps the
    /// subscriber it was started with.
    /// </summary>
    public TablePreloadJob Start(BuildRef build, string locale, IProgress<TablePreloadProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!FileLocales.IsKnown(locale)) throw new ArgumentException("The locale isn't registered.", nameof(locale));
        var key = new Key(build, locale);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running.TryGetValue(key, out var job)) return job;
            if (running.Count >= options.MaxJobs) throw new InvalidOperationException($"Table preloads run for at most {options.MaxJobs} builds and locales at once.");
            job = new TablePreloadJob(build, locale, tables, options, progress, CancellationTokenSource.CreateLinkedTokenSource(stop.Token));
            running[key] = job;
            job.Completion = Task.Run(() => job.RunAsync(Retire));
            return job;
        }
    }

    /// <summary>The build and locale's running job, or null.</summary>
    public TablePreloadJob? Find(BuildRef build, string locale)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(locale);
        lock (gate) return running.GetValueOrDefault(new Key(build, locale));
    }

    /// <summary>
    /// Stops the build and locale's job: cancels it and returns once it has fully stopped (its handle disposed), with its one
    /// terminal result (whatever it reached when it finished first). With no job running, the last job's result, or null.
    /// </summary>
    public async Task<TablePreloadResult?> StopAsync(BuildRef build, string locale)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(locale);
        var key = new Key(build, locale);
        TablePreloadJob? job;
        TablePreloadResult? last;
        lock (gate)
        {
            running.TryGetValue(key, out job);
            results.TryGetValue(key, out last);
        }
        if (job is null) return last;
        job.Cancel();
        return await job.Completion.ConfigureAwait(false);
    }

    /// <summary>The last finished job's result for the build and locale (kept for the last 64 pairs), or null.</summary>
    public TablePreloadResult? Last(BuildRef build, string locale)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(locale);
        lock (gate) return results.GetValueOrDefault(new Key(build, locale));
    }

    /// <summary>Stops every job and waits until each has stopped.</summary>
    public async ValueTask DisposeAsync()
    {
        TablePreloadJob[] jobs;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            jobs = running.Values.ToArray();
        }
        stop.Cancel();
        foreach (var job in jobs) await job.Completion.ConfigureAwait(false);
        stop.Dispose();
    }

    // Runs on the job before its Completion completes, so whoever awaited it sees the job gone and its result kept.
    private void Retire(TablePreloadJob job, TablePreloadResult result)
    {
        var key = new Key(job.Build, job.Locale);
        lock (gate)
        {
            if (running.TryGetValue(key, out var current) && current == job) running.Remove(key);
            if (!results.ContainsKey(key))
            {
                resultOrder.Enqueue(key);
                if (resultOrder.Count > KeptResults) results.Remove(resultOrder.Dequeue());
            }
            results[key] = result;
        }
    }

    private readonly record struct Key(BuildRef Build, string Locale);
}

/// <summary>One build and locale's table preload. <see cref="Completion"/> never faults; <see cref="Latest"/> is always current.</summary>
public sealed class TablePreloadJob
{
    private const string FailedCode = "TABLE_PRELOAD_FAILED";
    private const string StoppedMessage = "The table preload was stopped.";
    private readonly ITables tables;
    private readonly TablePreloadOptions options;
    private readonly IProgress<TablePreloadProgress>? progress;
    private readonly CancellationTokenSource cancel;
    // Serialises Cancel with the source's Dispose; nothing else takes it.
    private readonly Lock cancelGate = new();
    private bool cancelDisposed;
    // Guards latest and the outbox below. Never held while the subscriber runs.
    private readonly Lock gate = new();
    private TablePreloadProgress latest;
    private long lastDelivered;
    private bool delivered, finished;
    // Reports waiting for the subscriber, oldest first. A Kept one (a stage change or the final) is never replaced; a coalesced
    // one at the end is replaced by a newer one, so it holds at most four Kept plus one.
    private readonly List<(TablePreloadProgress Report, bool Kept)> outbox = [];
    private bool draining;
    private TaskCompletionSource? drained;

    internal TablePreloadJob(BuildRef build, string locale, ITables tables, TablePreloadOptions options, IProgress<TablePreloadProgress>? progress,
        CancellationTokenSource cancel)
    {
        Build = build; Locale = locale;
        this.tables = tables; this.options = options; this.progress = progress; this.cancel = cancel;
        latest = new(TablePreloadStage.Opening, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    public BuildRef Build { get; }
    public string Locale { get; }
    public Task<TablePreloadResult> Completion { get; internal set; } = null!;
    public TablePreloadProgress Latest { get { lock (gate) return latest; } }

    internal void Cancel()
    {
        lock (cancelGate)
            if (!cancelDisposed) cancel.Cancel();
    }

    internal async Task<TablePreloadResult> RunAsync(Action<TablePreloadJob, TablePreloadResult> retire)
    {
        var ct = cancel.Token;
        Outcome outcome;
        try { outcome = await RunCoreAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = new(TablePreloadOutcome.Canceled, null, [], null, null, StoppedMessage); }
        catch (Exception e) { outcome = new(TablePreloadOutcome.Failed, null, [], null, FailedCode, $"{e.GetType().Name}: {e.Message}"); }
        var result = Finish(outcome);
        await DrainedAsync().ConfigureAwait(false);
        retire(this, result);
        lock (cancelGate)
        {
            cancelDisposed = true;
            cancel.Dispose();
        }
        return result;
    }

    private async Task<Outcome> RunCoreAsync(CancellationToken ct)
    {
        Publish(latest, force: true);
        ct.ThrowIfCancellationRequested();
        var open = await tables.ForAsync(Build, Locale, ct).ConfigureAwait(false);
        if (open.State != FilesOpenState.Open || open.Tables is null)
        {
            if (open.Tables is { } stray) await stray.DisposeAsync().ConfigureAwait(false);
            return new(TablePreloadOutcome.Failed, null, [], open.State,
                open.Code ?? (open.State == FilesOpenState.Open ? FailedCode : null),
                open.Message ?? (open.State == FilesOpenState.Open ? "The build opened without tables." : null));
        }
        await using var handle = open.Tables;
        var inner = await handle.PreloadAsync(new Forward(this), ct).ConfigureAwait(false);
        return new(inner.Outcome, inner.Final, inner.Tables, inner.OpenState, inner.Code, inner.Message);
    }

    // The preload may call it from any thread. Never waits for the subscriber.
    private void OnReport(TablePreloadProgress value)
    {
        lock (gate)
        {
            if (finished) return;
            var next = Merge(latest, value);
            if (next == latest) return;
            Publish(next, force: next.Stage != latest.Stage);
        }
    }

    // Lock is re-entrant, so callers already holding the gate may call it. Only queues the report; Drain delivers it.
    private void Publish(TablePreloadProgress next, bool force)
    {
        lock (gate)
        {
            if (finished) return;
            latest = next;
            var now = options.Time.GetTimestamp();
            if (!force && delivered && options.Time.GetElapsedTime(lastDelivered, now) < options.ProgressInterval) return;
            delivered = true; lastDelivered = now;
            Queue(latest, kept: force);
        }
    }

    private TablePreloadResult Finish(Outcome outcome)
    {
        lock (gate)
        {
            if (outcome.Final is { } final) latest = Merge(latest, final);
            finished = true;
            Queue(latest, kept: true);
            return new(outcome.Value, latest, outcome.Tables, outcome.OpenState, outcome.Code, outcome.Message);
        }
    }

    /// <summary>No field goes down and the stage never moves back.</summary>
    private static TablePreloadProgress Merge(TablePreloadProgress a, TablePreloadProgress b) =>
        new(b.Stage > a.Stage ? b.Stage : a.Stage, Math.Max(a.FilesDone, b.FilesDone), Math.Max(a.FilesTotal, b.FilesTotal),
            Math.Max(a.FilesFailed, b.FilesFailed), Math.Max(a.BytesDone, b.BytesDone), Math.Max(a.BytesTotal, b.BytesTotal),
            Math.Max(a.CdnBytes, b.CdnBytes), Math.Max(a.TablesDone, b.TablesDone), Math.Max(a.TablesTotal, b.TablesTotal),
            Math.Max(a.TablesFailed, b.TablesFailed));

    // Under gate.
    private void Queue(TablePreloadProgress report, bool kept)
    {
        if (progress is null) return;
        if (!kept && outbox.Count > 0 && !outbox[^1].Kept) outbox[^1] = (report, false);
        else outbox.Add((report, kept));
        if (draining) return;
        draining = true;
        ThreadPool.QueueUserWorkItem(static job => job.Drain(), this, preferLocal: false);
    }

    // One drainer at a time, on a pool thread, delivering in queue order outside the gate.
    private void Drain()
    {
        while (true)
        {
            TablePreloadProgress next;
            lock (gate)
            {
                if (outbox.Count == 0)
                {
                    draining = false;
                    drained?.TrySetResult();
                    drained = null;
                    return;
                }
                next = outbox[0].Report;
                outbox.RemoveAt(0);
            }
            try { progress!.Report(next); }
            catch { /* a subscriber's failure never stops the job */ }
        }
    }

    private Task DrainedAsync()
    {
        lock (gate)
            return draining ? (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
    }

    private sealed record Outcome(TablePreloadOutcome Value, TablePreloadProgress? Final, IReadOnlyList<TableEntry> Tables, FilesOpenState? OpenState,
        string? Code, string? Message);

    private sealed class Forward(TablePreloadJob job) : IProgress<TablePreloadProgress>
    {
        public void Report(TablePreloadProgress value) => job.OnReport(value);
    }
}
