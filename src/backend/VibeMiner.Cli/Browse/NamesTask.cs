using System.Globalization;
using VibeMiner.Files.Names;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>N</c> in the Files view: the community file names (wow-listfile, D105) as the store's writer. Reads the names switch
/// first (off: nothing is taken or fetched), then takes the writer lock, makes layer 4 over the writer's own objects and runs one
/// unforced <see cref="IFileNames.UpdateAsync"/>: a check of the newest release and a download of the parts not staged yet. x
/// cancels it; staged parts stay and the next run resumes. Once the update returns the session's read-only file system is
/// dropped, so the reload loads the new copy. Each run makes its own file system, so a failure kept in memory by an earlier run
/// (and its 15 min back-off) never stops the next <c>N</c>. Every line fits 80 columns.
/// </summary>
internal static class NamesTask
{
    public const string Name = "names";
    private const int Width = FilesView.Width;

    /// <summary>The lock is taken, by whom, and that nothing was fetched.</summary>
    public const string Busy = "names: store busy (is VibeMiner running?); nothing fetched.";
    /// <summary>The switch is off.</summary>
    public const string Off = "names: off in Settings (community listfile); nothing fetched.";
    /// <summary>Off by the end of the update: the switch went off meanwhile, or layer 4 has no generated store to keep them in.</summary>
    public const string NoStore = "names: off in Settings, or the generated store is busy; nothing loaded.";

    public static KeyTask Start() => new(Name, RunAsync);

    private static async Task<string> RunAsync(BrowseSession session, IProgress<string> progress, CancellationToken ct)
    {
        if (!await On(session.NameSwitch, ct).ConfigureAwait(false)) return Off;
        IStoreHandle store;
        progress.Report("taking the writer lock");
        try { store = await session.OpenWriterAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { return Busy; }
        FileNamesStatus status;
        await using (store)
        {
            var files = await session.FileSystem(store.Objects, session.Builds(store.Objects), session.NameSwitch, ct).ConfigureAwait(false);
            try
            {
                progress.Report("checking and fetching wow-listfile from GitHub; x cancels");
                status = await files.Names.UpdateAsync(force: false, ct).ConfigureAwait(false);
            }
            finally
            {
                if (files is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false);
                else if (files is IDisposable d) d.Dispose();
            }
        }
        // The session's file system keeps the names it loaded; the next load makes a new one, which reads the new copy.
        await session.InvalidateFilesAsync().ConfigureAwait(false);
        return Line(status);
    }

    /// <summary>The switch as layer 4 reads it: false, a null task or a throw is off.</summary>
    private static async Task<bool> On(IFileNameSwitch names, CancellationToken ct)
    {
        try { return await (names.NamesEnabledAsync(ct) ?? Task.FromResult(false)).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    /// <summary>The one status line an update ends with.</summary>
    internal static string Line(FileNamesStatus s) => EnsureTask.Clip(s switch
    {
        { State: FileNamesState.Off } => NoStore,
        { State: FileNamesState.Ready, Code: null } => string.Create(CultureInfo.InvariantCulture,
            $"names: ready, {s.Count:N0} {(s.Count == 1 ? "name" : "names")} ({s.Tag ?? "no tag"})."),
        { State: FileNamesState.Ready } => string.Create(CultureInfo.InvariantCulture,
            $"names: kept {s.Tag ?? "no tag"} ({s.Count:N0}); the update failed ({s.Code})."),
        { State: FileNamesState.Failed } => $"names: failed ({s.Code ?? "no code"}); N tries again.",
        _ => $"names: not loaded ({s.Code ?? "no code"}); N tries again.",
    }, Width);
}
