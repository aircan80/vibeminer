using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>:builds</c>: one row per reviewed product from layer 3's catalog (read-only: the history log only), then the WoW products the
/// newest stored summary lists that aren't reviewed. Enter lists a product's builds. <c>r</c> refreshes as the store's writer.
/// </summary>
internal sealed class BuildsView : BrowseView
{
    public override string Title => "builds";
    public override IReadOnlyList<ViewAction> Actions => [Refresh];

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        IStoreHandle store;
        try { store = await session.OpenReadAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Missing)
        {
            return new(Columns, [], $"No object store at {session.StoreFolder} yet. r fetches builds; that takes the writer lock and creates it.");
        }
        await using (store)
        {
            var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
            var rows = new List<BrowseRow>();
            var problems = 0;
            foreach (var product in Products.Reviewed)
            {
                var list = await catalog.ListBuildsAsync(product.Id, ct).ConfigureAwait(false);
                problems += list.Problems.Count;
                var current = string.Join(' ', list.Builds.Where(b => b.Current).Select(b => b.VersionsName).Distinct());
                rows.Add(new([product.Label, product.Id, list.LastObservedAt is null ? "-" : $"{list.Builds.Count:N0}", current,
                    list.LastObservedAt is { } at ? BrowseSession.Time(at) : "no history", list.Problems.Count == 0 ? "" : $"{list.Problems.Count:N0}"],
                    () => new ProductBuildsView(product)));
            }
            var summary = await NewestSummaryAsync(store.Objects, ct).ConfigureAwait(false);
            string? summaryNote = null;
            if (summary is { Entry: var entry, Read: var read })
            {
                foreach (var id in read.NewProducts) rows.Add(new(["new, not reviewed", id, "-", "", BrowseSession.Time(entry.ObservedAt), ""]));
                summaryNote = read.NewProducts.Count > 0
                    ? $"New in the {BrowseSession.Time(entry.ObservedAt)} summary: {string.Join(", ", read.NewProducts)} (not reviewed; adding one is D017)."
                    : read.Problems.Any(p => p.Code is "SUMMARY_UNREADABLE" or "SUMMARY_COLUMNS_MISSING") ? $"The summary of {BrowseSession.Time(entry.ObservedAt)} UTC can't be read, so new products are unknown."
                    : $"Summary of {BrowseSession.Time(entry.ObservedAt)} UTC: no new WoW products.";
            }
            var note = string.Join(' ', new[]
            {
                summaryNote ?? "No summary recorded; r fetches one.",
                problems > 0 ? $"{problems:N0} catalog problem(s); Enter on a product lists them." : null,
            }.Where(s => s is not null));
            return new(Columns, rows, note);
        }
    }

    private static readonly string[] Columns = ["Product", "Id", "Builds", "Current", "Last (UTC)", "Problems"];

    /// <summary>The newest summary in the history log and what it says, read from the store; null when none was recorded.</summary>
    public static async Task<(HistoryEntry Entry, SummaryRefresh Read)?> NewestSummaryAsync(IObjectSource objects, CancellationToken ct)
    {
        HistoryEntry? newest = null, after = null;
        while (true)
        {
            var page = await objects.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Summary, After: after, Limit: HistoryView.Page), ct).ConfigureAwait(false);
            foreach (var e in page) if (newest is null || e.ObservedAt >= newest.ObservedAt) newest = e;
            if (page.Count < HistoryView.Page) break;
            after = page[^1];
        }
        if (newest is null) return null;
        var got = await objects.GetKeyAsync(new ObjectRequest(newest.Document), cdn: null, FetchPriority.Interactive, ct).ConfigureAwait(false);
        var observed = got.IsPresent && got.Bytes is { } bytes
            ? new ObservationResult(ObjectState.Present, new Observation(ObservationKind.Summary, "", newest.Region, new Uri(newest.Url), newest.ObservedAt, bytes), Recorded: true)
            : new ObservationResult(got.State, Code: got.Code, Message: got.Message);
        return (newest, Summaries.Read(observed));
    }

    /// <summary>
    /// <c>r</c>: versions and cdns for every reviewed product, then the summary, as <c>vm builds refresh</c> does. Takes the writer
    /// lock (creating a missing store) for the run and lets it go at the end; when the app holds it, says so and changes nothing.
    /// </summary>
    public static readonly ViewAction Refresh = new('r', "refresh: fetch versions, cdns and the summary (writes: takes the writer lock)", "refresh", RefreshAsync);

    /// <summary>Fits an 80-column status line: the lock is taken, by whom, that nothing changed and what to do.</summary>
    public const string BusyMessage = "refresh: store busy (is VibeMiner running?); nothing fetched. Close it, press r.";

    private static async Task<string> RefreshAsync(BrowseSession session, IProgress<string> progress, CancellationToken ct)
    {
        IStoreHandle store;
        progress.Report("taking the writer lock");
        try { store = await session.OpenWriterAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { return BusyMessage; }
        await using (store)
        {
            var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
            var failed = new List<string>();
            var ids = Products.Ids;
            for (var i = 0; i < ids.Count; i++)
            {
                progress.Report($"versions and cdns for {ids[i]} ({i + 1}/{ids.Count}); x cancels");
                var r = await catalog.RefreshAsync(ids[i], ct).ConfigureAwait(false);
                if (Failed(r.Versions) || Failed(r.Cdns))
                {
                    var bad = Failed(r.Versions) ? r.Versions : r.Cdns;
                    failed.Add($"{ids[i]} ({bad.Code ?? bad.State.ToString()})");
                }
            }
            progress.Report("the product summary");
            var summary = await catalog.RefreshSummaryAsync(ct).ConfigureAwait(false);
            var said = Failed(summary.Summary) ? $"summary failed ({summary.Summary.Code ?? summary.Summary.State.ToString()})"
                : summary.Summary.Observation is null ? "no summary served"
                : summary.NewProducts.Count == 0 ? "no new products" : $"new: {string.Join(", ", summary.NewProducts)}";
            return $"refresh done at {DateTimeOffset.UtcNow:HH:mm} UTC: {ids.Count - failed.Count}/{ids.Count} products"
                + (failed.Count == 0 ? "" : $", failed {string.Join(", ", failed)}") + $"; {said}.";
        }
    }

    /// <summary>A failure other than "the service has no such document", as <c>vm builds</c> counts them.</summary>
    private static bool Failed(ObservationResult r) => r.State is not (ObjectState.Present or ObjectState.NotFound);
}

/// <summary>One product's builds, newest first sighting first, with its catalog problems in the note. Enter shows a build.</summary>
internal sealed class ProductBuildsView(ReviewedProduct product) : BrowseView
{
    public override string Title => product.Id;
    public override IReadOnlyList<ViewAction> Actions => [BuildsView.Refresh];
    public override IReadOnlyList<ViewKey> Keys => [new('f', "files: list the selected build's files (enUS)", row => FilesView.Open(row?.Tag as CatalogBuild))];

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
        var list = await catalog.ListBuildsAsync(product.Id, ct).ConfigureAwait(false);
        var rows = list.Builds.Select(b => new BrowseRow(
            [b.VersionsName, b.Current ? "yes" : "", string.Join(' ', b.Regions.Where(g => !b.Current || g.Current).Select(g => g.Region).Distinct()),
             BrowseSession.Time(b.FirstSeen), BrowseSession.Time(b.LastSeen), b.BuildConfig],
            () => new BuildView(b), b)).ToList();
        var note = list.LastObservedAt is null ? $"{product.Label}: no versions history. r fetches it (writes)."
            : $"{product.Label}: {list.Builds.Count:N0} builds, last versions reply {BrowseSession.Time(list.LastObservedAt.Value)} UTC.";
        if (list.Problems.Count > 0)
            note += $" {list.Problems.Count:N0} problem(s): {string.Join("; ", list.Problems.Take(3).Select(p => $"{p.Code}: {p.Message}"))}";
        return new(["Version", "Current", "Regions", "First (UTC)", "Last (UTC)", "Build config"], rows, note);
    }
}

/// <summary>
/// One build: its identity fields exactly, then each region listing. <c>d</c> measures what of it is stored; <c>a</c> downloads
/// all of it as the store's writer.
/// </summary>
internal sealed class BuildView(CatalogBuild build) : BrowseView
{
    public override string Title => build.VersionsName;
    public override IReadOnlyList<ViewAction> Actions => [Downloaded, DownloadAll];
    public override IReadOnlyList<ViewKey> Keys => [new('f', "files: list this build's files (enUS)", _ => FilesView.Open(build))];

    /// <summary>
    /// <c>d</c>: opens the build read-only from the store (its ENCODING, ROOT and every archive index, so not on every load) and
    /// counts which of its objects are stored. The read-only store's source is local only: nothing is fetched or written.
    /// </summary>
    private ViewAction Downloaded => new('d', "downloaded: open the build read-only from the store and count what is stored", "downloaded", DownloadedAsync);

    private async Task<string> DownloadedAsync(BrowseSession session, IProgress<string> progress, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        // Read-only, the route doesn't matter: nothing is fetched.
        if ((await session.ChooseRegionAsync(store.Objects, build, ct).ConfigureAwait(false)).Region is not { } region) return NoRegion("downloaded");
        progress.Report($"opening {build.VersionsName} ({region.Region}) from the store: ENCODING, ROOT, indexes; x cancels");
        var opened = await session.Builds(store.Objects).OpenAsync(new BuildRef(build.Product, build.BuildConfig, region.CdnConfig, region.Region), ct)
            .ConfigureAwait(false);
        if (opened is not { State: BuildOpenState.Open, Opened: { } open }) return NotOpened(opened);
        progress.Report($"counting stored objects of {build.VersionsName}; x cancels");
        await using var figures = new DownloadedFigures(store.Objects);
        var figure = await figures.GetAsync(open, ct).ConfigureAwait(false);
        session.Remember(open.Build, build.VersionsName, figure);
        return Measured(figure, region.Region);
    }

    /// <summary>The line for a build that lists no region, after the task's name.</summary>
    internal static string NoRegion(string task) => $"{task}: no region lists a cdn config for this build; can't open it.";

    /// <summary>The line for a build none of whose regions has a usable CDN route (D280), after the task's name; fits 80 columns.</summary>
    internal static string NoRoute(string task) => $"{task}: no region of this build has a usable CDN route; nothing fetched.";

    /// <summary>
    /// <c>a</c>: Download all, as the store's writer. Opens the build over the writer's source (fetching metadata it lacks), then
    /// runs one layer 3 job and waits for its end. Cancel turns the job off and waits for it, so nothing runs once the lock goes.
    /// </summary>
    private ViewAction DownloadAll => new('a', "download all: fetch every object of the build (writes: takes the writer lock)", "download all", DownloadAllAsync);

    /// <summary>Fits an 80-column status line: the lock is taken, by whom, and that nothing was fetched.</summary>
    internal const string DownloadAllBusy = "download all: store busy (is VibeMiner running?); nothing fetched.";

    private async Task<string> DownloadAllAsync(BrowseSession session, IProgress<string> progress, CancellationToken ct)
    {
        IStoreHandle store;
        progress.Report("taking the writer lock");
        try { store = await session.OpenWriterAsync(ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { return DownloadAllBusy; }
        await using (store)
        {
            var choice = await session.ChooseRegionAsync(store.Objects, build, ct).ConfigureAwait(false);
            if (choice.Region is not { } region) return NoRegion("download all");
            if (!choice.Routed) return NoRoute("download all");
            progress.Report($"opening {build.VersionsName} ({region.Region}): ENCODING, ROOT, indexes; x cancels");
            var opened = await session.Builds(store.Objects).OpenAsync(new BuildRef(build.Product, build.BuildConfig, region.CdnConfig, region.Region), ct)
                .ConfigureAwait(false);
            if (opened is not { State: BuildOpenState.Open, Opened: { } open }) return NotOpenedForDownload(opened);
            var folder = session.StoreFolder;
            var freeSpace = session.FreeSpace;
            // Disposed before the store: it stops and awaits any job still running.
            await using var jobs = new DownloadAllJobs(store.Objects, _ => ValueTask.FromResult(freeSpace(folder)));
            var job = jobs.Start(open, new Forward(p => progress.Report(DownloadAllProgressLine(p))));
            DownloadAllResult result;
            try { result = await job.Completion.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = await jobs.StopAsync(open.Build).ConfigureAwait(false) ?? await job.Completion.ConfigureAwait(false);
            }
            if (FigureOf(result) is { } figure) session.Remember(open.Build, build.VersionsName, figure);
            return DownloadAllLine(result, region.Region);
        }
    }

    /// <summary>
    /// What the end of a Download all says is stored, as a Downloaded figure: only once its scan has finished (a finished pass, or a
    /// job past scanning), since during the scan only scanned rows count.
    /// </summary>
    internal static DownloadedFigure? FigureOf(DownloadAllResult r) =>
        r.Outcome is DownloadAllOutcome.Completed or DownloadAllOutcome.Incomplete || r.Final.Stage == DownloadAllStage.Fetching
            ? new DownloadedFigure(r.Final.StoredObjects, r.Final.TotalObjects, r.Final.StoredBytes, r.Final.TotalBytes)
            : null;

    /// <summary>
    /// A running job's progress, after the task's <c>download all: </c> prefix, e.g.
    /// <c>41.2% of 175.1 GiB, 1,204,331 of 2,900,112 objects; x cancels</c>.
    /// </summary>
    internal static string DownloadAllProgressLine(DownloadAllProgress p) => p.Stage == DownloadAllStage.Scanning
        ? string.Create(CultureInfo.InvariantCulture, $"checking what is stored: {p.StoredObjects:N0} of {p.TotalObjects:N0} found; x cancels")
        : string.Create(CultureInfo.InvariantCulture,
            $"{Share(p.StoredBytes, p.TotalBytes) * 100:0.0}% of {VmCommands.Size(p.TotalBytes)}, {p.StoredObjects:N0} of {p.TotalObjects:N0} objects; x cancels");

    /// <summary>An incomplete run's failure counts, nonzero ones only (all four if none is), in a fixed order.</summary>
    private static string FailedCounts(DownloadAllResult r)
    {
        (long Count, string Words)[] all = [(r.NotFound, "not found"), (r.Unavailable, "unavailable"), (r.Refused, "refused"), (r.Unsupported, "unsupported")];
        var shown = all.Where(c => c.Count != 0).ToArray();
        return string.Join(", ", (shown.Length == 0 ? all : shown).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Count:N0} {c.Words}")));
    }

    /// <summary>The one status line a Download all run ends with, per outcome.</summary>
    internal static string DownloadAllLine(DownloadAllResult r, string region)
    {
        var f = r.Final;
        var code = r.Code ?? "no code";
        return r.Outcome switch
        {
            DownloadAllOutcome.Completed => string.Create(CultureInfo.InvariantCulture,
                $"download all: done, {f.StoredObjects:N0} objects, {VmCommands.Size(f.StoredBytes)} stored ({region})"),
            DownloadAllOutcome.Incomplete => "download all: incomplete, " + FailedCounts(r) + ".",
            DownloadAllOutcome.Canceled => string.Create(CultureInfo.InvariantCulture,
                $"download all: canceled at {Share(f.StoredBytes, f.TotalBytes) * 100:0.0}% of {VmCommands.Size(f.TotalBytes)}; stored objects are kept."),
            DownloadAllOutcome.DiskFull => $"download all: free space short or disk full ({code}).",
            DownloadAllOutcome.StoreFailed => $"download all: the store failed ({code}).",
            DownloadAllOutcome.NeedsNetwork => $"download all: no network or CDN route ({code}).",
            _ => $"download all: failed ({code}).",
        };
    }

    /// <summary>An open over the writer that isn't Open. With the writer, NeedsNetwork means no network or no CDN route.</summary>
    internal static string NotOpenedForDownload(BuildOpenResult r) => r.State == BuildOpenState.NeedsNetwork
        ? $"download all: no network or CDN route for the {r.Part}; nothing fetched."
        : $"download all: can't open: {r.State} at {r.Part} ({r.Code ?? "no code"}).";

    private static double Share(long part, long total) => total <= 0 ? 0 : (double)part / total;

    /// <summary>Forwards the job's progress on its own thread; the task's progress posts it to the UI thread.</summary>
    private sealed class Forward(Action<DownloadAllProgress> report) : IProgress<DownloadAllProgress>
    {
        public void Report(DownloadAllProgress value) => report(value);
    }

    /// <summary>The status line for a measured build, e.g. <c>downloaded: 41.2% of 175.1 GiB, 1,204,331 of 2,900,112 objects (us)</c>.</summary>
    internal static string Measured(DownloadedFigure f, string region) => string.Create(CultureInfo.InvariantCulture,
        $"downloaded: {f.Share * 100:0.0}% of {VmCommands.Size(f.TotalBytes)}, {f.StoredObjects:N0} of {f.TotalObjects:N0} objects ({region})");

    /// <summary>
    /// The status line for an open that isn't Open. Read-only, NeedsNetwork means a part isn't stored; browse never fetches one.
    /// </summary>
    internal static string NotOpened(BuildOpenResult r) => r.State == BuildOpenState.NeedsNetwork
        ? $"downloaded: metadata not stored (NeedsNetwork, {r.Part}); nothing fetched."
        : $"downloaded: can't open: {r.State} at {r.Part} ({r.Code ?? "no code"}).";

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        var rows = new List<BrowseRow>
        {
            new(["Product", build.Product]), new(["Identity", build.Id]), new(["Version", build.VersionsName]),
            new(["Build config", build.BuildConfig]), new(["Product config", build.ProductConfig ?? "(none)"]),
            new(["Current", build.Current ? "yes" : "no"]),
            new(["First seen", $"{BrowseSession.Time(build.FirstSeen)} UTC"]), new(["Last seen", $"{BrowseSession.Time(build.LastSeen)} UTC"]),
            new(["Downloaded", "d measures (opens the build read-only)"]),
            new(["Download all", "a fetches every object (takes the writer lock)"]),
            new(["Files", "f lists the files (enUS)"]),
        };
        rows.AddRange(build.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new BrowseRow([$"field {f.Key}", f.Value])));
        rows.AddRange(build.Regions.Select(g => new BrowseRow([$"region {g.Region}",
            $"cdn {g.CdnConfig}{(g.Current ? ", current" : "")}, {BrowseSession.Time(g.FirstSeen)} to {BrowseSession.Time(g.LastSeen)}, record {g.LastRecord.KeyHex}"])));
        return Task.FromResult(new ViewContent(["Field", "Value"], rows, "Locales come from the build's own manifests, which a later view reads."));
    }
}
