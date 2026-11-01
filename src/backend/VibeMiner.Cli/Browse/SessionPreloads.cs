using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The table preloads of a browse session (D360, #159): one <see cref="TablePreloads"/> over the session's one tables composition
/// (<see cref="SessionTables"/>), so a table opened while a preload runs goes through the same layer 2 and jumps its queue. It runs
/// beside <see cref="BackgroundTasks"/>, not as one of them, so browsing and the other keys stay usable. <c>m</c> starts one for
/// the build it opened; <c>P</c> stops or resumes it. Only over the writer: a read-only composition (the app holds the lock)
/// refuses with a line. <see cref="Changed"/> is raised on any thread; the navigator posts and coalesces the redraw. Disposed with
/// the session, before the composition: every job is stopped and awaited.
/// </summary>
internal sealed class SessionPreloads(BrowseSession session) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BuildRef Build, string Locale), Entry> entries = [];
    private readonly HashSet<Task> pending = [];
    private readonly SemaphoreSlim opening = new(1, 1);
    private readonly CancellationTokenSource closing = new();
    private TablePreloads? preloads;
    private TablesLease? lease;
    private Entry? shown;
    private bool shownEnded;
    private long touched;
    private bool disposed;

    /// <summary>Raised on any thread after a preload's line may have changed.</summary>
    public event Action? Changed;

    /// <summary>Options for the session's <see cref="TablePreloads"/>. A test seam.</summary>
    internal TablePreloadOptions? Options { get; set; }

    /// <summary>Every start and stop still running, for tests and shutdown. Never faults.</summary>
    internal Task Pending { get { lock (gate) return Task.WhenAll([.. pending]); } }

    /// <summary>
    /// The status line's preload part: the last preload started or stopped, while it runs; its end until <see cref="ClearEnded"/>.
    /// Empty when there is nothing to show.
    /// </summary>
    public string Status
    {
        get
        {
            lock (gate)
            {
                if (shown is null) return "";
                var line = Line(shown);
                return shownEnded && !Running(shown) ? "" : line;
            }
        }
    }

    /// <summary>The line of this build and locale's preload, running or ended; null when this session has none.</summary>
    public string? LineOf(BuildRef build, string locale)
    {
        lock (gate) return entries.TryGetValue((build, locale), out var e) ? Line(e) : null;
    }

    /// <summary>The line of the most recently touched preload of a build in any region and locale; null when none.</summary>
    public string? LineOf(string product, string buildConfig)
    {
        lock (gate) return Latest(product, buildConfig) is { } e ? Line(e) : null;
    }

    /// <summary>Hides an ended preload's line from <see cref="Status"/> once it has been seen; a running one's stays.</summary>
    public void ClearEnded()
    {
        lock (gate) if (shown is not null && !Running(shown)) shownEnded = true;
    }

    /// <summary>
    /// Starts the build and locale's preload, or keeps the running one. Leases the composition (opening it over the writer when
    /// it isn't open yet); the line says why when it doesn't start. Nothing happens when the session has no tables composition.
    /// </summary>
    public Task StartAsync(BuildRef build, string locale, string label, CancellationToken ct)
    {
        var start = StartCoreAsync(build, locale, label, ct);
        Track(start);
        return start;
    }

    private async Task StartCoreAsync(BuildRef build, string locale, string label, CancellationToken ct)
    {
        if (session.Tables is not { } tables) return;
        Entry entry;
        lock (gate)
        {
            if (disposed) return;
            entry = Touch(build, locale, label);
            entry.Starting = true;
        }
        Raise();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, closing.Token);
            await opening.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                lock (gate) if (disposed) return;
                if (preloads is null)
                {
                    var leased = await tables.LeaseAsync(session, linked.Token).ConfigureAwait(false);
                    if (tables.Writer != true)
                    {
                        // The composition stays read-only until browse restarts; nothing is kept.
                        await leased.DisposeAsync().ConfigureAwait(false);
                        lock (gate) entry.Note = Says(entry, "not started, the store is read-only (writer busy)");
                        return;
                    }
                    lease = leased;
                    preloads = new TablePreloads(leased.Tables, Options);
                }
                var progress = new Forward(this, entry);
                var job = preloads.Start(build, locale, progress);
                lock (gate)
                {
                    if (entry.Job == job) return;
                    entry.Job = job;
                    entry.Progress = job.Latest;
                }
                _ = EndedAsync(entry, job);
            }
            finally { opening.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || closing.IsCancellationRequested)
        {
            lock (gate) if (entry.Job is null) entry.Note = Says(entry, "not started (canceled)");
        }
        catch (Exception e)
        {
            lock (gate) entry.Note = Says(entry, $"not started, {BrowseSession.Explain(e)}");
        }
        finally
        {
            lock (gate) entry.Starting = false;
            Raise();
        }
    }

    /// <summary>Keeps the job's one terminal result on its entry (the job's completion never faults).</summary>
    private async Task EndedAsync(Entry entry, TablePreloadJob job)
    {
        var result = await job.Completion.ConfigureAwait(false);
        lock (gate)
        {
            if (entry.Job != job) return;
            entry.Job = null;
            entry.Stopping = false;
            entry.Result = result;
        }
        Raise();
    }

    /// <summary>
    /// <c>P</c> on a build and locale: stops its running preload (what is stored and prepared stays), else starts it again. Runs
    /// on the UI thread and doesn't wait; the line shows what happens.
    /// </summary>
    public void Toggle(BuildRef build, string locale, string label)
    {
        var stop = false;
        lock (gate)
        {
            if (disposed) return;
            entries.TryGetValue((build, locale), out var e);
            if (e is not null && (e.Starting || e.Stopping || e.Job is not null))
            {
                Touch(build, locale, label);
                if (!e.Starting && !e.Stopping)
                {
                    e.Stopping = true;
                    Track(StopAsync(build, locale));
                }
                stop = true;
            }
        }
        if (stop) Raise();
        else _ = StartAsync(build, locale, label, CancellationToken.None);
    }

    /// <summary><c>P</c> on a build's detail view: toggles its latest preload. False when this session has none for the build.</summary>
    public bool Toggle(string product, string buildConfig)
    {
        Entry? e;
        lock (gate) e = Latest(product, buildConfig);
        if (e is null) return false;
        Toggle(e.Build, e.Locale, e.Label);
        return true;
    }

    private async Task StopAsync(BuildRef build, string locale)
    {
        TablePreloads? running;
        await opening.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { running = disposed ? null : preloads; }
        finally { opening.Release(); }
        // The job's completion clears Stopping and keeps its result.
        if (running is not null) await running.StopAsync(build, locale).ConfigureAwait(false);
    }

    private void Track(Task work)
    {
        lock (gate) pending.Add(work);
        _ = work.ContinueWith(t => { lock (gate) pending.Remove(t); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Under gate.
    private Entry Touch(BuildRef build, string locale, string label)
    {
        if (!entries.TryGetValue((build, locale), out var e)) entries[(build, locale)] = e = new Entry(build, locale, label);
        e.Note = null;
        e.Touched = ++touched;
        shown = e;
        shownEnded = false;
        return e;
    }

    // Under gate.
    private Entry? Latest(string product, string buildConfig) =>
        entries.Values.Where(e => e.Build.Product == product && e.Build.BuildConfig == buildConfig).MaxBy(e => e.Touched);

    private static bool Running(Entry e) => e.Starting || e.Job is not null;

    private void Raise() => Changed?.Invoke();

    /// <summary>The line's start: <c>preload</c>, the version and the locale unless it is enUS.</summary>
    private static string Says(Entry e, string what) => Says(e.Label, e.Locale, what);

    private static string Says(string label, string locale, string what) =>
        EnsureTask.Clip($"preload {label}{(locale == FilesView.DefaultLocale ? "" : $" {locale}")}: {what}", FilesView.Width);

    // Under gate.
    private static string Line(Entry e) =>
        e.Note ?? (e.Starting ? Says(e, "starting")
            : e.Job is not null ? (e.Stopping ? Says(e, "stopping") : ProgressLine(e.Label, e.Locale, e.Progress))
            : e.Result is { } r ? ResultLine(e.Label, e.Locale, r) : Says(e, "not started"));

    /// <summary>
    /// A running preload, e.g. <c>preload 1.2.3.45678: downloading 120 of 900 files, 3.1 MiB of 90.0 MiB</c> or
    /// <c>preload 1.2.3.45678: preparing 300 of 870 tables</c>; fits 80 columns.
    /// </summary>
    internal static string ProgressLine(string label, string locale, TablePreloadProgress p) => Says(label, locale, p.Stage switch
    {
        TablePreloadStage.Opening => "opening the build",
        TablePreloadStage.Downloading => string.Create(CultureInfo.InvariantCulture,
            $"downloading {p.FilesDone:N0} of {p.FilesTotal:N0} files, {VmCommands.Size(p.BytesDone)} of {VmCommands.Size(p.BytesTotal)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"preparing {p.TablesDone:N0} of {p.TablesTotal:N0} tables"),
    });

    /// <summary>
    /// An ended preload, e.g. <c>preload 1.2.3.45678: completed, 870 tables</c> or
    /// <c>preload 1.2.3.45678: incomplete, 12 of 870 tables and 3 files failed; P retries</c>; fits 80 columns.
    /// </summary>
    internal static string ResultLine(string label, string locale, TablePreloadResult r)
    {
        var f = r.Final;
        return Says(label, locale, r.Outcome switch
        {
            TablePreloadOutcome.Completed => string.Create(CultureInfo.InvariantCulture,
                $"completed, {f.TablesTotal:N0} tables{(f.TablesFailed > 0 ? $" ({f.TablesFailed:N0} failed)" : "")}"),
            TablePreloadOutcome.Incomplete => string.Create(CultureInfo.InvariantCulture,
                $"incomplete, {f.TablesFailed:N0} of {f.TablesTotal:N0} tables and {f.FilesFailed:N0} files failed; P retries"),
            TablePreloadOutcome.Canceled => f.Stage == TablePreloadStage.Preparing
                ? string.Create(CultureInfo.InvariantCulture, $"stopped at {f.TablesDone:N0} of {f.TablesTotal:N0} tables; P resumes")
                : string.Create(CultureInfo.InvariantCulture, $"stopped at {f.FilesDone:N0} of {f.FilesTotal:N0} files; P resumes"),
            _ => $"failed ({r.Code ?? r.OpenState?.ToString() ?? "no code"}); P retries",
        });
    }

    /// <summary>Stops every preload and waits for it, then lets the composition lease go. Call before the composition closes.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        closing.Cancel();
        await Pending.ConfigureAwait(false);
        await opening.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (preloads is not null) await preloads.DisposeAsync().ConfigureAwait(false);
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            preloads = null;
            lease = null;
        }
        finally { opening.Release(); }
    }

    private sealed class Entry(BuildRef build, string locale, string label)
    {
        public BuildRef Build { get; } = build;
        public string Locale { get; } = locale;
        public string Label { get; } = label;
        public long Touched { get; set; }
        public bool Starting { get; set; }
        public bool Stopping { get; set; }
        public TablePreloadJob? Job { get; set; }
        public TablePreloadProgress Progress { get; set; }
        public TablePreloadResult? Result { get; set; }
        /// <summary>Why the last start didn't run (read-only, a failure); cleared by the next start or stop.</summary>
        public string? Note { get; set; }
    }

    /// <summary>The job's progress, on the pool thread it arrives on: kept under the lock, then <see cref="Changed"/>.</summary>
    private sealed class Forward(SessionPreloads owner, Entry entry) : IProgress<TablePreloadProgress>
    {
        public void Report(TablePreloadProgress value)
        {
            lock (owner.gate) entry.Progress = value;
            owner.Raise();
        }
    }
}
