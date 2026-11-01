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
    /// Null when there is nothing to show.
    /// </summary>
    public PreloadLine? Status
    {
        get
        {
            lock (gate) return shown is null || (shownEnded && !Running(shown)) ? null : Line(shown);
        }
    }

    /// <summary>The line of this build and locale's preload, running or ended; null when this session has none.</summary>
    public PreloadLine? LineOf(BuildRef build, string locale)
    {
        lock (gate) return entries.TryGetValue((build, locale), out var e) ? Line(e) : null;
    }

    /// <summary>The line of the most recently touched preload of a build in any region and locale; null when none.</summary>
    public PreloadLine? LineOf(string product, string buildConfig)
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
                        lock (gate) entry.Note = "not started, the store is read-only (writer busy)";
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
            lock (gate) if (entry.Job is null) entry.Note = "not started (canceled)";
        }
        catch (Exception e)
        {
            lock (gate) entry.Note = $"not started, {BrowseSession.Explain(e)}";
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

    /// <summary>A line saying <paramref name="what"/> of the entry; running while it starts, runs or stops.</summary>
    private static PreloadLine Says(Entry e, string what) => new(e.Label, e.Locale, what, Running(e));

    // Under gate.
    private static PreloadLine Line(Entry e) =>
        e.Note is { } note ? new(e.Label, e.Locale, note, false)
            : e.Starting ? Says(e, "starting")
            : e.Job is not null ? (e.Stopping ? Says(e, "stopping") : ProgressLine(e.Label, e.Locale, e.Progress))
            : e.Result is { } r ? ResultLine(e.Label, e.Locale, r) : Says(e, "not started");

    /// <summary>
    /// A running preload in short forms that fit 80 columns, e.g. <c>preload 1.2.3.45678: downloading 120/900 files, 3.1/90.0 MiB</c>
    /// or <c>preload 1.2.3.45678: preparing 300/870 tables</c>. Downloading before layer 4 has counted anything (a resume
    /// checking what is stored) says <c>checking stored files</c>, never 0 of 0.
    /// </summary>
    internal static PreloadLine ProgressLine(string label, string locale, TablePreloadProgress p) => new(label, locale, p.Stage switch
    {
        TablePreloadStage.Opening => "opening the build",
        TablePreloadStage.Downloading when p.FilesTotal == 0 => "checking stored files",
        TablePreloadStage.Downloading => string.Create(CultureInfo.InvariantCulture,
            $"downloading {p.FilesDone}/{p.FilesTotal} files, {Sizes(p.BytesDone, p.BytesTotal)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"preparing {p.TablesDone}/{p.TablesTotal} tables"),
    }, true);

    /// <summary>
    /// An ended preload, e.g. <c>preload 1.2.3.45678: done, 870 tables</c> or
    /// <c>preload 1.2.3.45678: incomplete, 12/870 tables, 3 files failed; P retries</c>.
    /// </summary>
    internal static PreloadLine ResultLine(string label, string locale, TablePreloadResult r)
    {
        var f = r.Final;
        return new(label, locale, r.Outcome switch
        {
            TablePreloadOutcome.Completed => string.Create(CultureInfo.InvariantCulture,
                $"done, {f.TablesTotal} tables{(f.TablesFailed > 0 ? $" ({f.TablesFailed} failed)" : "")}"),
            TablePreloadOutcome.Incomplete => string.Create(CultureInfo.InvariantCulture,
                $"incomplete, {f.TablesFailed}/{f.TablesTotal} tables, {f.FilesFailed} files failed; P retries"),
            TablePreloadOutcome.Canceled => f.Stage == TablePreloadStage.Preparing
                ? string.Create(CultureInfo.InvariantCulture, $"stopped at {f.TablesDone}/{f.TablesTotal} tables; P resumes")
                : f.FilesTotal == 0 ? "stopped before downloading; P resumes"
                : string.Create(CultureInfo.InvariantCulture, $"stopped at {f.FilesDone}/{f.FilesTotal} files; P resumes"),
            _ => $"failed ({r.Code ?? r.OpenState?.ToString() ?? "no code"}); P retries",
        }, false);
    }

    /// <summary><c>3.1/90.0 MiB</c> when both sizes have the same unit, else <c>900 KiB/1.2 MiB</c>.</summary>
    private static string Sizes(long done, long total)
    {
        var d = VmCommands.Size(done);
        var t = VmCommands.Size(total);
        var space = d.LastIndexOf(' ');
        return space > 0 && t.EndsWith(d[space..], StringComparison.Ordinal) ? $"{d[..space]}/{t}" : $"{d}/{t}";
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
