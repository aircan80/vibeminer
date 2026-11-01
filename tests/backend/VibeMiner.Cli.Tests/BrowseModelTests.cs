using System.Text;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

public sealed class BrowseModelTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private readonly FakeStores stores = new();
    private readonly ManualTime time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly Navigator navigator;

    public BrowseModelTests()
    {
        stores.Source.Time = time;
        navigator = new Navigator(ViewRegistry.Default(), new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder), a => a());
    }

    public async ValueTask DisposeAsync() => await navigator.DisposeAsync().AsTask().WaitAsync(Guard);

    /// <summary>Goes to a top view and waits for its load.</summary>
    private async Task<BrowseFrame> GoAsync(string name)
    {
        Assert.True(navigator.Go(name));
        await navigator.Loading.WaitAsync(Guard);
        return navigator.Current;
    }

    private async Task<BrowseFrame> DrillAsync(int row)
    {
        Assert.True(navigator.Drill(row));
        await navigator.Loading.WaitAsync(Guard);
        return navigator.Current;
    }

    /// <summary>Waits for the background task, then for the reload it ends with.</summary>
    private async Task SettleTaskAsync()
    {
        await navigator.Tasks.Current.WaitAsync(Guard);
        await navigator.Loading.WaitAsync(Guard);
    }

    private static List<string[]> Cells(BrowseFrame frame) => frame.Visible.Select(r => r.Cells.ToArray()).ToList();

    private async Task Observe(ObservationKind kind, string product, string reply)
    {
        stores.Source.AddReply(product, "us", kind, Encoding.UTF8.GetBytes(reply));
        await stores.Source.ObserveAsync(new(kind, product, "us"), default);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    private const string VersionsHeader = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private static readonly byte[] CdnsReply = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0\nus|tpr/wow|cdn.example.invalid\n"u8.ToArray();

    /// <summary>The us version service answers for <paramref name="product"/> with one build in us and eu.</summary>
    private void Serve(string product, string name, string build)
    {
        var versions = Encoding.UTF8.GetBytes($"{VersionsHeader}\n## seqn = 1\nus|{build}|{B}|{C}|{name.Split('.')[^1]}|{name}|{C}\neu|{build}|{B}|{C}|{name.Split('.')[^1]}|{name}|{C}\n");
        stores.Source.AddReply(product, "us", ObservationKind.Versions, versions).AddReply(product, "us", ObservationKind.Cdns, CdnsReply);
    }

    private void ServeSummary(params string[] products) => stores.Source.AddReply("", "us", ObservationKind.Summary, Encoding.UTF8.GetBytes(
        "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 9\n" + string.Concat(products.Select(p => $"{p}|1|\n{p}|2|cdn\n"))));

    private async Task ObserveServed(ObservationKind kind, string product)
    {
        await stores.Source.ObserveAsync(new(kind, product, "us"), default);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task<int> HistoryCount(HistoryKind? kind = null) => (await stores.Source.ReadHistoryAsync(new HistoryQuery(Kind: kind), default)).Count;

    [Fact]
    public void TheRegistryFindsExactNamesAndUniquePrefixes()
    {
        var registry = ViewRegistry.Default();
        Assert.Equal(["store", "history", "builds"], registry.Entries.Select(e => e.Name));
        Assert.Equal("history", registry.Find("history")?.Name);
        Assert.Equal("store", registry.Find(":st")?.Name);
        Assert.Equal("builds", registry.Find(":b")?.Name);
        Assert.Equal("builds", registry.Find(" :BUILDS ")?.Name);
        Assert.Null(registry.Find(":"));
        Assert.Null(registry.Find(":nope"));

        var ambiguous = new ViewRegistry().Add(new("store", "", () => new StoreView())).Add(new("stash", "", () => new StoreView()));
        Assert.Null(ambiguous.Find(":st"));
        Assert.Equal("stash", ambiguous.Find(":sta")?.Name);
        Assert.Throws<ArgumentException>(() => ambiguous.Add(new("store", "again", () => new StoreView())));
    }

    [Fact]
    public async Task AnUnknownViewNameSaysWhichViewsThereAre()
    {
        await GoAsync(":st");
        Assert.IsType<StoreView>(navigator.Current.View);
        Assert.False(navigator.Go(":nope"));
        Assert.Equal("No view :nope. Views: store, history, builds.", navigator.Message);
        Assert.IsType<StoreView>(navigator.Current.View);
        Assert.Single(navigator.Frames);
    }

    [Fact]
    public async Task TheStoreViewShowsTheStatsReadOnlyAndClosesTheStore()
    {
        var frame = await GoAsync(":store");
        Assert.Null(frame.Error);
        Assert.False(frame.Loading);
        var rows = Cells(frame);
        Assert.Contains(["Data folder", $"{Data} (VIBEMINER_DATA_DIR)"], rows);
        Assert.Contains(["Store", $"{StoreFolder} (read-only)"], rows);
        Assert.Contains(["Packs", "2"], rows);
        Assert.Contains(["Objects", "1,234"], rows);
        Assert.Contains(["History records", "56"], rows);
        Assert.Contains(["Pack bytes", "3.0 MiB"], rows);
        Assert.Contains(["Live bytes", "2.0 MiB"], rows);
        Assert.Contains(["Created", "2026-10-31 12:00 UTC"], rows);
        Assert.Contains(["Last scrub", "not recorded"], rows);
        Assert.Equal([(StoreFolder, false)], stores.Opened);
        Assert.Equal(0, stores.Open);
        Assert.False(navigator.TryAction('r'));
    }

    [Fact]
    public async Task HistoryCountsPerProductAndDrillsToEntriesAndTheRawReply()
    {
        await Observe(ObservationKind.Versions, "wow", "first wow versions");
        await Observe(ObservationKind.Cdns, "wow", "wow cdns");
        await Observe(ObservationKind.Versions, "wow", "Region!STRING:0\r\nus\u0001x\tend\n");
        await Observe(ObservationKind.Summary, "", "summary");
        await Observe(ObservationKind.Cdns, "wowt", "wowt cdns");

        var top = await GoAsync(":history");
        Assert.Null(top.Error);
        Assert.Equal(["Product", "Name", "Versions", "CDNs", "Other", "Last (UTC)"], top.Content!.Columns);
        Assert.Equal(
        [
            ["(summary)", "version-service summary", "0", "0", "1", "2026-01-01 00:03"],
            ["wow", "Retail", "2", "1", "0", "2026-01-01 00:02"],
            ["wowt", "PTR", "0", "1", "0", "2026-01-01 00:04"],
        ], Cells(top));
        Assert.Equal("5 entries. Enter lists a product's replies.", top.Content.Note);

        var entries = await DrillAsync(1);
        Assert.Equal("history > wow", navigator.Path);
        Assert.Equal(["2026-01-01 00:02", "2026-01-01 00:01", "2026-01-01 00:00"], entries.Visible.Select(r => r.Cells[0]));
        Assert.Equal(["versions", "cdns", "versions"], entries.Visible.Select(r => r.Cells[1]));
        Assert.All(entries.Visible, r => Assert.Equal("us", r.Cells[2]));
        Assert.Equal("3 replies, newest first. Enter shows the raw reply.", entries.Content!.Note);

        var reply = await DrillAsync(0);
        Assert.Equal("history > wow > versions 2026-01-01 00:02", navigator.Path);
        Assert.Empty(reply.Content!.Rows);
        Assert.Equal("Region!STRING:0\nus.x\tend\n", reply.Content.Text);
        Assert.StartsWith("26 B. https://us.version.battle.net/v2/products/wow/versions (us), document ", reply.Content.Note);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AnEmptyHistorySaysSo()
    {
        var frame = await GoAsync(":history");
        Assert.Empty(frame.Content!.Rows);
        Assert.Equal("No history recorded.", frame.Content.Note);
    }

    [Fact]
    public void PrintableShowsControlCharactersAsDotsAndDropsCarriageReturns()
    {
        Assert.Equal("a\nb.[31m\tc..é", ReplyView.Printable("a\r\nb\u001b[31m\tc\u0000\u007fé"u8));
        Assert.Equal("", ReplyView.Printable("\r\r"u8));
    }

    [Fact]
    public async Task TheFilterNarrowsRowsAndEscClearsItBeforeGoingBack()
    {
        await Observe(ObservationKind.Versions, "wow", "a");
        await Observe(ObservationKind.Versions, "wowt", "b");
        await Observe(ObservationKind.Summary, "", "c");
        var top = await GoAsync(":history");
        Assert.Equal(3, top.Visible.Count);

        navigator.SetFilter("  PTR ");
        Assert.Equal("PTR", top.Filter);
        Assert.Equal(["wowt"], top.Visible.Select(r => r.Cells[0]));
        navigator.SetFilter("SUMMARY");
        Assert.Equal(["(summary)"], top.Visible.Select(r => r.Cells[0]));
        navigator.SetFilter("nothing matches");
        Assert.Empty(top.Visible);
        Assert.False(navigator.Drill(0));

        Assert.True(navigator.Back());
        Assert.Equal("", top.Filter);
        Assert.Equal(3, top.Visible.Count);
        Assert.Single(navigator.Frames);
        Assert.False(navigator.Back());

        await DrillAsync(2);
        Assert.Equal(2, navigator.Frames.Count);
        navigator.SetFilter("versions");
        Assert.True(navigator.Back());
        Assert.Equal(2, navigator.Frames.Count);
        Assert.True(navigator.Back());
        Assert.Same(top, navigator.Current);
        Assert.Equal(2, top.Selected);
        Assert.False(top.Loading);
        Assert.False(navigator.Back());
    }

    [Fact]
    public async Task AMissingStoreIsAnErrorForStoreAndHistoryButANoteForBuilds()
    {
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        foreach (var name in new[] { ":store", ":history" })
        {
            var frame = await GoAsync(name);
            Assert.Null(frame.Content);
            Assert.False(frame.Loading);
            Assert.StartsWith($"{ObjectStoreCodes.Missing}: No object store at {StoreFolder}.", frame.Error);
            Assert.Contains("Nothing was created", frame.Error);
        }
        var builds = await GoAsync(":builds");
        Assert.Null(builds.Error);
        Assert.Empty(builds.Content!.Rows);
        Assert.Equal($"No object store at {StoreFolder} yet. r fetches builds; that takes the writer lock and creates it.", builds.Content.Note);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Empty(stores.WriterCreate);
    }

    [Fact]
    public async Task BuildsListsReviewedProductsTheNewOnesAndDrillsToABuild()
    {
        Serve("wow", "12.0.1.60000", A);
        Serve("wowt", "12.0.5.61000", B);
        await ObserveServed(ObservationKind.Versions, "wow");
        await ObserveServed(ObservationKind.Cdns, "wow");
        await ObserveServed(ObservationKind.Versions, "wowt");
        ServeSummary("wow", "wowt", "wowz", "agent");
        await ObserveServed(ObservationKind.Summary, "");

        var top = await GoAsync(":builds");
        Assert.Null(top.Error);
        Assert.Equal(["Product", "Id", "Builds", "Current", "Last (UTC)", "Problems"], top.Content!.Columns);
        var rows = Cells(top);
        Assert.Equal(Products.Reviewed.Select(p => p.Id).Append("wowz"), rows.Select(r => r[1]));
        Assert.Equal(["Retail", "wow", "1", "12.0.1.60000", "2026-01-01 00:00", ""], rows[0]);
        Assert.Equal(["PTR", "wowt", "1", "12.0.5.61000", "2026-01-01 00:02", ""], rows[1]);
        var ptr2 = rows.Single(r => r[1] == "wowxptr");
        Assert.Equal(["PTR 2", "wowxptr", "-", "", "no history", ""], ptr2);
        Assert.NotEqual(rows[1][0], ptr2[0]);
        Assert.Equal(["new, not reviewed", "wowz", "-", "", "2026-01-01 00:03", ""], rows[^1]);
        Assert.Null(top.Visible[^1].Drill);
        Assert.Contains("summary: wowz (not reviewed", top.Content.Note);
        Assert.DoesNotContain("agent", top.Content.Note);

        var product = await DrillAsync(0);
        Assert.Equal("builds > wow", navigator.Path);
        var build = Assert.Single(Cells(product));
        Assert.Equal(["12.0.1.60000", "yes", "eu us", "2026-01-01 00:00", "2026-01-01 00:00", A], build);
        Assert.StartsWith("Retail: 1 builds, last versions reply 2026-01-01 00:00 UTC.", product.Content!.Note);

        var detail = await DrillAsync(0);
        Assert.Equal("builds > wow > 12.0.1.60000", navigator.Path);
        var fields = Cells(detail);
        Assert.Contains(["Product", "wow"], fields);
        Assert.Contains(["Version", "12.0.1.60000"], fields);
        Assert.Contains(["Build config", A], fields);
        Assert.Contains(["Product config", C], fields);
        Assert.Contains(["Current", "yes"], fields);
        Assert.Equal(["region eu", "region us"], fields.Where(f => f[0].StartsWith("region ", StringComparison.Ordinal)).Select(f => f[0]));
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Equal(0, stores.Open);
    }

    /// <summary>Goes to builds > wow > its one build, with only the versions reply stored (no cdns, nothing of the build).</summary>
    private async Task<BrowseFrame> RetailBuildAsync()
    {
        Serve("wow", "12.0.1.60000", A);
        await ObserveServed(ObservationKind.Versions, "wow");
        await GoAsync(":builds");
        await DrillAsync(0);
        return await DrillAsync(0);
    }

    [Fact]
    public async Task ABuildShowsTheDownloadedRowAndDOnUnstoredMetadataSaysSoReadOnly()
    {
        var detail = await RetailBuildAsync();
        Assert.Contains(["Downloaded", "d measures (opens the build read-only)"], Cells(detail));

        Assert.True(navigator.TryAction('d'));
        await SettleTaskAsync();
        Assert.Equal("downloaded: metadata not stored (NeedsNetwork, BuildConfig); nothing fetched.", navigator.Tasks.Status);
        Assert.True(navigator.Tasks.Status!.Length <= 80);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Empty(stores.WriterCreate);
        Assert.Equal(0, stores.Open);
        Assert.IsType<BuildView>(navigator.Current.View);
    }

    [Fact]
    public async Task CancelingDownloadedEndsWithCanceledAndClosesTheStore()
    {
        await RetailBuildAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stores.BeforeOpen = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        Assert.True(navigator.TryAction('d'));
        await started.Task.WaitAsync(Guard);
        Assert.Equal("downloaded", navigator.Tasks.Running);

        stores.BeforeOpen = null;
        Assert.True(navigator.Tasks.Cancel());
        await SettleTaskAsync();
        Assert.Equal("downloaded canceled.", navigator.Tasks.Status);
        Assert.Null(navigator.Tasks.Running);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public void DownloadedLinesFitTheStatusAndNameTheState()
    {
        var line = BuildView.Measured(new DownloadedFigure(1_204_331, 2_900_112, 77_500_000_000, 188_000_000_000), "us");
        Assert.Equal("downloaded: 41.2% of 175.1 GiB, 1,204,331 of 2,900,112 objects (us)", line);
        Assert.True(line.Length <= 80);
        Assert.Equal("downloaded: 0.0% of 0 B, 0 of 0 objects (eu)", BuildView.Measured(new DownloadedFigure(0, 0, 0, 0), "eu"));

        var build = new BuildRef("wow", A, B, "us");
        Assert.True(BuildView.NotOpened(new(build, BuildOpenState.NeedsNetwork, null, BuildPart.ArchiveIndex, "OBJECT_ACCESS_NEEDS_NETWORK", "x", null)).Length <= 80);
        Assert.Equal("downloaded: metadata not stored (NeedsNetwork, Encoding); nothing fetched.",
            BuildView.NotOpened(new(build, BuildOpenState.NeedsNetwork, null, BuildPart.Encoding, "OBJECT_ACCESS_NEEDS_NETWORK", "x", null)));
        Assert.Equal("downloaded: can't open: Corrupt at Root (BLTE_CHECKSUM).",
            BuildView.NotOpened(new(build, BuildOpenState.Corrupt, null, BuildPart.Root, "BLTE_CHECKSUM", "x", null)));
    }

    [Fact]
    public async Task BuildsWithNoSummarySaysRFetchesOne()
    {
        var top = await GoAsync(":builds");
        Assert.Equal(Products.Reviewed.Count, top.Visible.Count);
        Assert.All(Cells(top), r => Assert.Equal(("-", "no history"), (r[2], r[4])));
        Assert.Equal("No summary recorded; r fetches one.", top.Content!.Note);
    }

    [Fact]
    public async Task RefreshRunsAsTheWriterRecordsTheRepliesAndReloadsTheView()
    {
        Serve("wow", "12.0.1.60000", A);
        Serve("wowt", "12.0.5.61000", B);
        ServeSummary("wow", "wowz");
        var top = await GoAsync(":builds");
        Assert.Equal("-", top.Visible[0].Cells[2]);

        Assert.True(navigator.TryAction('r'));
        await SettleTaskAsync();
        Assert.Null(navigator.Tasks.Running);
        Assert.StartsWith("refresh done at ", navigator.Tasks.Status);
        Assert.EndsWith($": {Products.Ids.Count}/{Products.Ids.Count} products; new: wowz.", navigator.Tasks.Status);
        Assert.Contains((StoreFolder, true), stores.Opened);
        Assert.Equal([true], stores.WriterCreate);
        Assert.Equal(2, await HistoryCount(HistoryKind.Versions));
        Assert.Equal(2, await HistoryCount(HistoryKind.Cdns));
        Assert.Equal(1, await HistoryCount(HistoryKind.Summary));
        Assert.Equal(0, stores.Open);

        var reloaded = navigator.Current;
        Assert.IsType<BuildsView>(reloaded.View);
        Assert.False(reloaded.Loading);
        Assert.Equal(["Retail", "wow", "1", "12.0.1.60000"], reloaded.Visible[0].Cells.Take(4));
        Assert.Equal(["PTR", "wowt", "1", "12.0.5.61000"], reloaded.Visible[1].Cells.Take(4));
        Assert.Equal("wowz", reloaded.Visible[^1].Cells[1]);
    }

    [Fact]
    public async Task ARefreshNamesTheFailedObservationsOwnState()
    {
        // Versions arrive, cdns fail without a code: the line must name the cdns state, not the versions' Present.
        Serve("wow", "12.0.1.60000", A);
        stores.ObservationScript = r => r is { Kind: ObservationKind.Cdns, Product: "wow" } ? new ObservationResult(ObjectState.StoreFailed) : null;
        await GoAsync(":builds");
        Assert.True(navigator.TryAction('r'));
        await SettleTaskAsync();
        Assert.Contains($"{Products.Ids.Count - 1}/{Products.Ids.Count} products, failed wow (StoreFailed);", navigator.Tasks.Status);
    }

    [Fact]
    public async Task RefreshWhileTheAppHoldsTheWriterLockSaysSoAndFetchesNothing()
    {
        Serve("wow", "12.0.1.60000", A);
        stores.WriterFailure = ObjectStoreCodes.Busy;
        await GoAsync(":builds");
        Assert.True(navigator.TryAction('r'));
        await SettleTaskAsync();
        Assert.Equal(BuildsView.BusyMessage, navigator.Tasks.Status);
        Assert.Null(navigator.Tasks.Running);
        Assert.Equal(0, await HistoryCount());
        Assert.Equal(0, stores.Open);
        Assert.Null(navigator.Current.Error);
    }

    [Fact]
    public async Task ASecondRefreshWhileOneRunsIsRefused()
    {
        Serve("wow", "12.0.1.60000", A);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stores.BeforeOpen = async (writer, ct) =>
        {
            if (!writer) return;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        await GoAsync(":builds");
        Assert.True(navigator.TryAction('r'));
        await started.Task.WaitAsync(Guard);
        Assert.Equal("refresh", navigator.Tasks.Running);

        var first = navigator.Tasks.Current;
        navigator.TryAction('r');
        Assert.Equal("refresh is already running; x cancels it.", navigator.Tasks.Status);
        Assert.Same(first, navigator.Tasks.Current);
        Assert.Single(stores.WriterCreate);

        release.SetResult();
        await SettleTaskAsync();
        Assert.StartsWith("refresh done at ", navigator.Tasks.Status);
        Assert.Single(stores.WriterCreate);
        Assert.Equal(1, await HistoryCount(HistoryKind.Versions));
    }

    [Fact]
    public async Task CancelingARefreshEndsWithCanceledAndWritesNothing()
    {
        Serve("wow", "12.0.1.60000", A);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stores.BeforeOpen = async (writer, ct) =>
        {
            if (!writer) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        await GoAsync(":builds");
        Assert.False(navigator.Tasks.Cancel());
        Assert.True(navigator.TryAction('r'));
        await started.Task.WaitAsync(Guard);

        Assert.True(navigator.Tasks.Cancel());
        await SettleTaskAsync();
        Assert.Equal("refresh canceled.", navigator.Tasks.Status);
        Assert.Null(navigator.Tasks.Running);
        Assert.False(navigator.Tasks.Cancel());
        Assert.Equal(0, await HistoryCount());
        Assert.Equal(0, stores.Open);
        Assert.IsType<BuildsView>(navigator.Current.View);
        Assert.NotNull(navigator.Current.Content);
    }

    [Fact]
    public async Task ALoadThatEndsAfterLeavingItsViewIsDropped()
    {
        await Observe(ObservationKind.Versions, "wow", "a");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, so the store load really finishes after the navigator has moved on.
        stores.BeforeOpen = async (_, _) => { started.TrySetResult(); await release.Task; };

        Assert.True(navigator.Go(":store"));
        var stale = navigator.Loading;
        var storeFrame = navigator.Current;
        await started.Task.WaitAsync(Guard);
        Assert.True(storeFrame.Loading);

        stores.BeforeOpen = null;
        var history = await GoAsync(":history");
        Assert.Equal(["wow"], history.Visible.Select(r => r.Cells[0]));

        release.SetResult();
        await stale.WaitAsync(Guard);
        Assert.Same(history, navigator.Current);
        Assert.Single(navigator.Frames);
        Assert.IsType<HistoryView>(navigator.Current.View);
        Assert.Equal(["Product", "Name", "Versions", "CDNs", "Other", "Last (UTC)"], navigator.Current.Content!.Columns);
        Assert.Null(storeFrame.Content);
        Assert.False(storeFrame.Loading);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AKeyTheViewDoesNotHaveDoesNothing()
    {
        await GoAsync(":builds");
        Assert.False(navigator.TryAction('z'));
        Assert.False(navigator.TryAction('R'));
        Assert.Equal("", navigator.Tasks.Status);
        await GoAsync(":history");
        Assert.False(navigator.TryAction('r'));
        Assert.Empty(stores.WriterCreate);
    }

    /// <summary>vm with the browse runner given or not, over a plain console pinned here (no ANSI whatever the environment says).</summary>
    private async Task<(int Code, string Error, BrowseSession? Session)> RunVm(bool terminal, params string[] args)
    {
        var error = new StringWriter();
        static Spectre.Console.IAnsiConsole Plain(TextWriter writer)
        {
            var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
            {
                Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No,
                ColorSystem = Spectre.Console.ColorSystemSupport.NoColors, Interactive = Spectre.Console.InteractionSupport.No,
            });
            console.Profile.Width = 250;
            return console;
        }
        BrowseSession? got = null;
        var env = new CliEnvironment("linux", name => name == "VIBEMINER_DATA_DIR" ? Data : null, "/home/nobody-cli-test");
        var code = await new VmCommands(env, stores, Plain(new StringWriter()), Plain(error), new StringWriter(),
            terminal ? (session, _) => { got = session; return Task.FromResult(VmCommands.Ok); } : null).RunAsync(args, default);
        return (code, error.ToString(), got);
    }

    [Fact]
    public async Task BrowseGetsTheResolvedStoreAndNeedsATerminal()
    {
        var (code, _, session) = await RunVm(true, "browse");
        Assert.Equal(VmCommands.Ok, code);
        Assert.Equal(StoreFolder, session!.StoreFolder);
        Assert.Equal(DataFolderSource.Override, session.Folder.Source);
        Assert.Empty(stores.Opened);

        var (noTerminal, error, _) = await RunVm(false, "browse");
        Assert.Equal(VmCommands.Failed, noTerminal);
        Assert.Contains("BROWSE_NEEDS_TERMINAL", error);

        var (json, _, none) = await RunVm(true, "--json", "browse");
        Assert.Equal(VmCommands.Usage, json);
        Assert.Null(none);
    }
}
