using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>m</c> in the Files view: fetches what a build's open needs (both configs, ENCODING, ROOT, every archive index and the
/// file-index) as the store's writer, through layer 3's <see cref="IBuilds.OpenAsync"/> over the writer's own objects. Takes the
/// writer lock for the run and lets it go at the end; x cancels it, and what got stored stays. The view showing when it ends
/// reloads, and the session's read-only store sees the writer's commits, so the files list then opens. A build that opens then
/// starts its table preload for the view's locale (<see cref="SessionPreloads"/>, D360), while the writer lease is still held, so
/// the session's tables composition opens over the writer. Every line fits 80 columns.
/// </summary>
internal static class MetadataTask
{
    public const string Name = "metadata";

    /// <summary>The lock is taken, by whom, and that nothing was fetched.</summary>
    public const string Busy = "metadata: store busy (is VibeMiner running?); nothing fetched.";

    /// <summary><c>m</c> on a files target; <paramref name="label"/> (the version name) names its table preload.</summary>
    public static KeyTask Start(FilesTarget target, string label) => new(Name, (session, progress, ct) => RunAsync(session, target, label, progress, ct));

    private static async Task<string> RunAsync(BrowseSession session, FilesTarget target, string label, IProgress<string> progress, CancellationToken ct)
    {
        var build = target.Build;
        IStoreHandle store;
        progress.Report("taking the writer lock");
        try { store = await session.OpenWriterAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { return Busy; }
        await using (store)
        {
            progress.Report($"configs, ENCODING, ROOT, indexes ({build.Region ?? "us"}); x cancels");
            var opened = await session.Builds(store.Objects).OpenAsync(build, ct).ConfigureAwait(false);
            if (opened.State == BuildOpenState.Open)
            {
                progress.Report("starting the tables preload");
                await session.Preloads.StartAsync(build, target.Locale, label, ct).ConfigureAwait(false);
            }
            return Line(opened);
        }
    }

    /// <summary>The one status line the fetch ends with.</summary>
    internal static string Line(BuildOpenResult r) => EnsureTask.Clip(r switch
    {
        { State: BuildOpenState.Open, Opened: { } open } => string.Create(CultureInfo.InvariantCulture,
            $"metadata: stored, {open.Metadata.FileCount:N0} {(open.Metadata.FileCount == 1 ? "file" : "files")}, {open.Metadata.ArchiveCount:N0} archives ({r.Build.Region ?? "us"})."),
        { State: BuildOpenState.NeedsNetwork } => $"metadata: no network or CDN route at {r.Part} ({r.Code ?? "no code"}).",
        _ => $"metadata: can't open: {r.State} at {r.Part} ({r.Code ?? "no code"}).",
    }, FilesView.Width);
}
