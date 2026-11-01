using System.Globalization;
using VibeMiner.Files;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>g</c> and <c>G</c>: Ensure (D250) as the store's writer, the one place the Files views fetch. Takes the writer lock, makes
/// layer 4 over the writer's own objects (never the session's read-only cache), opens the build (fetching metadata it lacks) and
/// runs one <see cref="IBuildFiles.EnsureAsync"/>. Every line it reports or ends with fits 80 columns. The view showing when it
/// ends reloads; the session's read-only store sees the writer's commits, so Stored shows the new state.
/// </summary>
internal static class EnsureTask
{
    public const string Name = "ensure";
    private const int Width = FilesView.Width;
    /// <summary>The progress room: the task's <c>ensure: </c> prefix comes in front.</summary>
    private const int ProgressWidth = Width - 8;

    /// <summary>The lock is taken, by whom, and that nothing was fetched.</summary>
    public const string Busy = "ensure: store busy (is VibeMiner running?); nothing fetched.";

    public static KeyTask Start(FilesTarget target, EnsureSelection selection, EnsurePriority priority) =>
        new(Name, (session, progress, ct) => RunAsync(session, target, selection, priority, progress, ct));

    private static async Task<string> RunAsync(BrowseSession session, FilesTarget target, EnsureSelection selection, EnsurePriority priority,
        IProgress<string> progress, CancellationToken ct)
    {
        IStoreHandle store;
        progress.Report("taking the writer lock");
        try { store = await session.OpenWriterAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { return Busy; }
        await using (store)
        {
            var files = session.FileSystem(store.Objects, session.Builds(store.Objects), session.NameSwitch);
            try
            {
                progress.Report("opening the build: ENCODING, ROOT, indexes; x cancels");
                var opened = await files.ForAsync(target.Build, target.Locale, ct).ConfigureAwait(false);
                if (opened.Files is not { } handle) return NotOpened(opened);
                // The handle goes first, then the file system, then the store (the await using above).
                await using (handle)
                {
                    var result = await handle.EnsureAsync(selection, priority, new Forward(p => progress.Report(ProgressLine(p))), ct)
                        .ConfigureAwait(false);
                    return Line(result);
                }
            }
            finally
            {
                if (files is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false);
                else if (files is IDisposable d) d.Dispose();
            }
        }
    }

    /// <summary>
    /// A running ensure's progress, after the task's <c>ensure: </c> prefix: <c>120 of 500 files, 3.1 MiB of 9.0 MiB; x cancels</c>,
    /// or while a filter is still being listed <c>listing: 120 of 500 files, 9.0 MiB so far; x cancels</c>.
    /// </summary>
    internal static string ProgressLine(EnsureProgress p) => Clip(p.Listed
        ? string.Create(CultureInfo.InvariantCulture,
            $"{p.FilesDone:N0} of {p.FilesTotal:N0} files, {VmCommands.Size(p.BytesDone)} of {VmCommands.Size(p.BytesTotal)}; x cancels")
        : string.Create(CultureInfo.InvariantCulture,
            $"listing: {p.FilesDone:N0} of {p.FilesTotal:N0} files, {VmCommands.Size(p.BytesTotal)} so far; x cancels"), ProgressWidth);

    /// <summary>The failing states in the order the incomplete line names them, with their words.</summary>
    private static readonly (FileEnsureState State, string Words)[] Failing =
    [
        (FileEnsureState.Missing, "missing"),
        (FileEnsureState.NeedsNetwork, "need network"),
        (NoRoute, "no CDN route"),
        (FileEnsureState.Unavailable, "unavailable"),
        (FileEnsureState.Refused, "refused"),
        (FileEnsureState.StoreFailed, "store failed"),
        (FileEnsureState.NotInEncoding, "not in encoding"),
        (FileEnsureState.NotInLocale, "not in locale"),
        (FileEnsureState.UnknownFile, "unknown"),
    ];

    private const string NoCdnContext = "OBJECT_ACCESS_NO_CDN_CONTEXT";
    /// <summary>A count key of its own for Unavailable with <see cref="NoCdnContext"/>; no real state has this value.</summary>
    private const FileEnsureState NoRoute = (FileEnsureState)byte.MaxValue;

    /// <summary>The one status line an ensure ends with, per outcome.</summary>
    internal static string Line(EnsureResult r)
    {
        long stored = 0, already = 0;
        var counts = new Dictionary<FileEnsureState, long>();
        foreach (var f in r.Files)
        {
            if (f.State == FileEnsureState.Stored) stored++;
            else if (f.State == FileEnsureState.AlreadyStored) already++;
            // Unavailable for want of a CDN route is said as that (QA on #175), not as the CDN failing.
            var state = f is { State: FileEnsureState.Unavailable, Code: NoCdnContext } ? NoRoute : f.State;
            counts[state] = counts.GetValueOrDefault(state) + 1;
        }
        var have = stored + already;
        var total = r.Files.Count;
        switch (r.Outcome)
        {
            case EnsureOutcome.Completed:
                return Clip(string.Create(CultureInfo.InvariantCulture,
                    $"ensure: done, {have:N0} {Files(have)} stored ({already:N0} already), {VmCommands.Size(r.Final.CdnBytes)} fetched"), Width);
            case EnsureOutcome.Canceled:
                // Canceled while still listing, the total is only what was listed so far.
                var of = r.Final.Listed ? "files" : "listed";
                return Clip(string.Create(CultureInfo.InvariantCulture,
                    $"ensure: canceled, {have:N0} of {total:N0} {of} stored; what got stored stays."), Width);
            default:
                var line = string.Create(CultureInfo.InvariantCulture, $"ensure: incomplete, {have:N0} of {total:N0} stored; ");
                var parts = Failing.Where(s => counts.GetValueOrDefault(s.State) > 0)
                    .Select(s => string.Create(CultureInfo.InvariantCulture, $"{counts[s.State]:N0} {s.Words}")).ToList();
                // As many as fit with the closing ".", then "..." for the rest.
                for (var i = 0; i < parts.Count; i++)
                {
                    var next = line + (i == 0 ? "" : ", ") + parts[i];
                    var end = i == parts.Count - 1 ? "." : ", ...";
                    if (next.Length + end.Length > Width) return Clip(line + (i == 0 ? "..." : ", ..."), Width);
                    line = next;
                }
                return Clip(line + ".", Width);
        }
    }

    /// <summary>An open over the writer that isn't Open. With the writer, NeedsNetwork means no network or no CDN route.</summary>
    internal static string NotOpened(FilesOpenResult r) => Clip(r.State switch
    {
        FilesOpenState.NeedsNetwork => $"ensure: no network or CDN route for the build ({r.Code ?? "no code"}); nothing fetched.",
        FilesOpenState.LocaleNotInBuild => $"ensure: no {r.Locale} files in this build; nothing fetched.",
        _ => $"ensure: can't open the build: {r.State} ({r.Code ?? "no code"}); nothing fetched.",
    }, Width);

    private static string Files(long n) => n == 1 ? "file" : "files";

    /// <summary>Cuts a line to <paramref name="width"/> columns with "...".</summary>
    internal static string Clip(string line, int width) => line.Length <= width ? line : line[..(width - 3)] + "...";

    /// <summary>Forwards layer 4's progress on its own thread; the task's progress posts it to the UI thread.</summary>
    private sealed class Forward(Action<EnsureProgress> report) : IProgress<EnsureProgress>
    {
        public void Report(EnsureProgress value) => report(value);
    }
}
