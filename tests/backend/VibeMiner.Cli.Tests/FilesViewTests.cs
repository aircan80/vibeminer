using System.Runtime.CompilerServices;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using BuildsFakeBuild = VibeMiner.Builds.Testing.FakeBuild;
using FakeFile = VibeMiner.Files.Testing.FakeFile;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The Files list in <c>vm browse</c>: paging, filters, locales and open states, over the in-memory file system.</summary>
public sealed class FilesViewTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private static readonly BuildRef Small = new("wow", A, B, "us");
    private static readonly BuildRef Big = new("wow", C, B, "us");
    private static readonly string[] Columns = ["Id", "Path", "Format", "Size", "Stored", "Enc"];

    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new();
    private readonly GatedFileSystem gated;
    private readonly BrowseSession session;
    private readonly List<Navigator> navigators = [];

    public FilesViewTests()
    {
        byte[] blp = [.. "BLP2"u8, .. new byte[12]];
        memory.Add(Small, ["enUS", "deDE"],
        [
            new FakeFile(10, Size: null, Storage: FileStorage.Stored, Locales: ["enUS"], Content: blp),
            new FakeFile(11, Size: 1_234_567, Locales: ["enUS"]),
            new FakeFile(12, Size: 42, Encrypted: true, Locales: ["enUS"]),
            new FakeFile(13, Locales: ["enUS"], NotInEncoding: true),
            new FakeFile(14, Size: 7, Locales: ["deDE"]),
        ]);
        memory.Add(Big, ["enUS"], Enumerable.Range(1, 1200).Select(i => new FakeFile((uint)i, Size: i)));
        // One listfile for both builds: the big build's ids are files/N, except the small build's 10-14 (13 has no name).
        var names = Enumerable.Range(1, 1200).ToDictionary(i => (uint)i, i => $"files/{i}");
        names[10] = "interface/icons/a.blp"; names[11] = "Interface/Icons/b.blp"; names[12] = "dbfilesclient/map.db2"; names[14] = "sound/x.ogg";
        names.Remove(13);
        memory.Names.Use(names);
        gated = new GatedFileSystem(memory);
        session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), Path.Combine(Data, ObjectStore.DefaultFolderName))
        {
            FileSystem = (_, _, _, _) => Task.FromResult<IFileSystem>(gated),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var nav in navigators) await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync();
    }

    private Navigator Nav(ViewRegistry registry)
    {
        var nav = new Navigator(registry, session, a => a());
        navigators.Add(nav);
        return nav;
    }

    /// <summary>A navigator on a Files view of <paramref name="build"/>, loaded.</summary>
    private async Task<Navigator> FilesAsync(BuildRef build, string locale = "enUS")
    {
        var nav = Nav(new ViewRegistry().Add(new("files", "", () => new FilesView(new(build, locale), "12.0.1.60000")))
            .Add(new("other", "", () => new FilesView(new(Small, "enUS"), "other"))));
        Assert.True(nav.Go("files"));
        await nav.Loading.WaitAsync(Guard);
        return nav;
    }

    private static async Task Key(Navigator nav, char key)
    {
        Assert.True(nav.TryAction(key));
        await nav.Loading.WaitAsync(Guard);
        Fits(nav.Message);
    }

    /// <summary>A note or message is one line of an 80-column terminal.</summary>
    private static void Fits(string? line) => Assert.True(line is null || line.Length <= 80, $"{line?.Length} columns: {line}");

    private static async Task AnswerAsync(Navigator nav, char key, string typed)
    {
        Assert.True(nav.TryAction(key));
        Assert.NotNull(nav.Asking);
        nav.Answer(typed);
        await nav.Loading.WaitAsync(Guard);
        Fits(nav.Message);
    }

    private static List<string> Ids(Navigator nav) => nav.Current.Visible.Select(r => r.Cells[0]).ToList();
    private static string Note(Navigator nav)
    {
        var note = nav.Current.Content!.Note!;
        Fits(note);
        return note;
    }

    [Fact]
    public async Task TheListShowsColumnsAndCellsAndDrillsIntoAFile()
    {
        var nav = await FilesAsync(Small);
        Assert.Null(nav.Current.Error);
        Assert.Equal("files enUS", nav.Path);
        Assert.Equal(Columns, nav.Current.Content!.Columns);
        Assert.Equal(
        [
            ["10", "interface/icons/a.blp", "Blp", "16", "yes", ""],
            ["11", "Interface/Icons/b.blp", "Blp?", "1,234,567", "no", ""],
            ["12", "dbfilesclient/map.db2", "Db2?", "42", "no", "yes"],
            ["13", "", "-", "-", "no copy", ""],
        ], nav.Current.Visible.Select(r => r.Cells.ToArray()).ToList());
        Assert.Equal("Page 1, 10-13 (last). 12.0.1.60000 enUS.", Note(nav));
        Assert.All(nav.Current.Visible, r => Assert.IsType<BuildFile>(r.Tag));
        Assert.Contains("only from the name", nav.Help());

        nav.Current.Selected = 1;
        Assert.True(nav.Drill(1));
        var file = Assert.IsType<FileView>(nav.Current.View);
        Assert.Equal(new FilesTarget(Small, "enUS"), file.Target);
        Assert.Equal(11u, file.File.FileDataId);
        Assert.Equal("files enUS > b.blp", nav.Path);
        // Browse never fetched anything or updated the names.
        Assert.Empty(stores.Source.Observed);
        Assert.Equal(0, memory.Names.Updates);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
    }

    [Fact]
    public async Task NAndPPageThroughAndStopAtEachEnd()
    {
        var nav = await FilesAsync(Big);
        Assert.Equal(FilesView.PageSize, nav.Current.Visible.Count);
        Assert.Equal(("1", "500"), (Ids(nav)[0], Ids(nav)[^1]));
        Assert.Equal("Page 1, 1-500; n next. 12.0.1.60000 enUS.", Note(nav));

        await Key(nav, 'p');
        Assert.Equal("This is the first page.", nav.Message);
        await Key(nav, 'n');
        Assert.Equal(("501", "1000"), (Ids(nav)[0], Ids(nav)[^1]));
        Assert.Equal("Page 2, 501-1000; n next, p back. 12.0.1.60000 enUS.", Note(nav));
        await Key(nav, 'n');
        Assert.Equal(200, nav.Current.Visible.Count);
        Assert.Equal("Page 3, 1001-1200 (last); p back. 12.0.1.60000 enUS.", Note(nav));
        await Key(nav, 'n');
        Assert.Equal("This is the last page.", nav.Message);
        Assert.Equal("1001", Ids(nav)[0]);
        await Key(nav, 'p');
        Assert.Equal("501", Ids(nav)[0]);
        await Key(nav, 'p');
        Assert.Equal("1", Ids(nav)[0]);
        Assert.Null(nav.Message);
    }

    [Fact]
    public async Task APathFilterIsAPrefixThatRestartsAtTheFirstPageAndEscClearsIt()
    {
        var nav = await FilesAsync(Big);
        await Key(nav, 'n');
        nav.SetFilter("FILES\\1");
        await nav.Loading.WaitAsync(Guard);
        // 1, 15-19 (10-14 aren't files/), 100-199, 1000-1200: one page from the start.
        Assert.Equal("1", Ids(nav)[0]);
        Assert.Equal(1 + 5 + 100 + 201, nav.Current.Visible.Count);
        Assert.StartsWith("Page 1, 1-1200 (last). Filter: path FILES\\1.", Note(nav));

        Assert.True(nav.Back());
        await nav.Loading.WaitAsync(Guard);
        Assert.Single(nav.Frames);
        Assert.Equal("", nav.Current.Filter);
        Assert.Equal(FilesView.PageSize, nav.Current.Visible.Count);
        Assert.Equal("Page 1, 1-500; n next. 12.0.1.60000 enUS.", Note(nav));
    }

    [Fact]
    public async Task TFiltersByFormatNamesAndRefusesUnknownOnes()
    {
        var nav = await FilesAsync(Small);
        await AnswerAsync(nav, 't', " blp, DB2 ");
        Assert.Equal(["10", "11", "12"], Ids(nav));
        Assert.Contains("Filter: Db2,Blp.", Note(nav));

        Assert.True(nav.TryAction('t'));
        Assert.Equal("Db2,Blp", nav.Asking!.Initial);
        nav.Answer("Blp,nope");
        Assert.Equal("No format nope. E.g. Db2, Blp, M2Chunked, WmoRoot, AdtRoot, Ogg, Lua.", nav.Message);
        Assert.Equal(["10", "11", "12"], Ids(nav));
        await AnswerAsync(nav, 't', "3");
        Assert.StartsWith("No format 3.", nav.Message);

        await AnswerAsync(nav, 't', "");
        Assert.Equal(["10", "11", "12", "13"], Ids(nav));
        Assert.DoesNotContain("Filter", Note(nav));
    }

    [Fact]
    public async Task SCyclesTheStoredFilter()
    {
        var nav = await FilesAsync(Small);
        await Key(nav, 's');
        Assert.Equal(["10"], Ids(nav));
        Assert.Contains("Filter: stored.", Note(nav));
        await Key(nav, 's');
        Assert.Equal(["11", "12"], Ids(nav));
        Assert.Contains("Filter: not stored.", Note(nav));
        await Key(nav, 's');
        Assert.Equal(["13"], Ids(nav));
        Assert.Contains("Filter: no encoded copy.", Note(nav));
        await Key(nav, 's');
        Assert.Equal(["10", "11", "12", "13"], Ids(nav));
    }

    [Fact]
    public async Task AFilterChangeRestartsAtTheFirstPage()
    {
        var nav = await FilesAsync(Big);
        await Key(nav, 'n');
        await Key(nav, 's');
        Assert.Equal("Page 1, no files match. Filter: stored. 12.0.1.60000 enUS.", Note(nav));
        await Key(nav, 's');
        await Key(nav, 's');
        await Key(nav, 's');
        Assert.Equal("1", Ids(nav)[0]);
    }

    [Fact]
    public async Task LPicksAKnownLocaleAndRefusesOthers()
    {
        var nav = await FilesAsync(Small);
        Assert.True(nav.TryAction('l'));
        Assert.Equal("enUS", nav.Asking!.Initial);
        nav.Answer("english");
        Assert.Equal("No locale english. ? lists them; e.g. enUS, deDE, frFR, zhCN.", nav.Message);
        // The help wraps the locales onto lines that fit 80 columns, and lists every one.
        var help = nav.Help().Split('\n');
        var first = Array.FindIndex(help, l => l.StartsWith("  l        locale: one of ", StringComparison.Ordinal));
        Assert.True(first >= 0);
        var listed = help.Skip(first).TakeWhile((l, i) => i == 0 || l.StartsWith(new string(' ', 11), StringComparison.Ordinal)).ToList();
        Assert.True(listed.Count > 1);
        Assert.All(listed, l => Fits(l));
        Assert.Equal(FileLocales.Names, string.Join(' ', listed)["  l        locale: one of ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("files enUS", nav.Path);

        await AnswerAsync(nav, 'l', "deDE");
        Assert.Equal("files deDE", nav.Path);
        Assert.Equal(["14"], Ids(nav));
        Assert.Equal(new FilesTarget(Small, "deDE"), ((FilesView)nav.Current.View).Target);

        await AnswerAsync(nav, 'l', "frFR");
        Assert.Empty(nav.Current.Visible);
        Assert.Equal("LocaleNotInBuild: no frFR files in this build; l picks another.", Note(nav));
    }

    [Theory]
    [InlineData(FilesOpenState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK", "NeedsNetwork: metadata not stored. m fetches it (writer lock).")]
    [InlineData(FilesOpenState.Missing, "OBJECT_ACCESS_MISSING", "Missing (OBJECT_ACCESS_MISSING): the CDN had no copy of a part when last asked.")]
    [InlineData(FilesOpenState.Encrypted, "BUILD_ENCRYPTED", "Encrypted (BUILD_ENCRYPTED): part of the build needs a TACT key this app lacks.")]
    [InlineData(FilesOpenState.Unavailable, "OBJECT_ACCESS_UNAVAILABLE", "Unavailable (OBJECT_ACCESS_UNAVAILABLE): can't open the build.")]
    [InlineData(FilesOpenState.Corrupt, "BLTE_CHECKSUM", "Corrupt (BLTE_CHECKSUM): can't open the build.")]
    [InlineData(FilesOpenState.Unsupported, "ROOT_VERSION", "Unsupported (ROOT_VERSION): can't open the build.")]
    [InlineData(FilesOpenState.StoreFailed, "OBJECT_STORE_FAILED", "StoreFailed (OBJECT_STORE_FAILED): can't open the build.")]
    public async Task AFailedOpenShowsAnEmptyTableWithWhatItMeans(FilesOpenState state, string code, string note)
    {
        memory.Fail(Small, state, code);
        var nav = await FilesAsync(Small);
        Assert.Null(nav.Current.Error);
        Assert.Equal(Columns, nav.Current.Content!.Columns);
        Assert.Empty(nav.Current.Visible);
        Assert.Equal(note, Note(nav));
        await Key(nav, 'n');
        Assert.Equal("This is the last page.", nav.Message);
    }

    [Fact]
    public async Task AnUnstoredBuildIsNeedsNetworkAndAMissingStoreIsTheStoreError()
    {
        var nav = await FilesAsync(new BuildRef("wow", B, B, "us"));
        Assert.StartsWith("NeedsNetwork: metadata not stored.", Note(nav));

        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        await session.InvalidateFilesAsync();
        var missing = await FilesAsync(Small);
        Assert.StartsWith("OBJECT_STORE_MISSING", missing.Current.Error);
    }

    [Theory]
    [InlineData(FileNamesState.Off, "Names off in Settings: no paths, / finds none.")]
    [InlineData(FileNamesState.NotReady, "Names not loaded: N fetches them.")]
    [InlineData(FileNamesState.Failed, "Names failed (LISTFILE_FETCH): N tries again.")]
    public async Task NamesThatDontServeAreNamedInTheNote(FileNamesState state, string said)
    {
        memory.Names.State = state;
        memory.Names.Code = "LISTFILE_FETCH";
        var nav = await FilesAsync(Small);
        // The build label goes last and is left out when it doesn't fit.
        Assert.StartsWith($"Page 1, 10-13 (last). {said}", Note(nav));
        Assert.All(nav.Current.Visible, r => Assert.Equal("", r.Cells[1]));
        nav.SetFilter("interface");
        await nav.Loading.WaitAsync(Guard);
        Assert.Empty(nav.Current.Visible);
        Assert.Equal(0, memory.Names.Updates);
    }

    // ---------------------------------------------------------------- opening from the build views and :files

    private const string VersionsHeader = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";

    private const string CdnsHeader = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0";
    private const string Netease = "cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\n";

    /// <summary>
    /// The wow versions reply lists one build (build config A, cdn config B) in each of <paramref name="regions"/>, and the cdns
    /// reply has <paramref name="cdns"/> (none when null); the files of the region <paramref name="opens"/> list file 7.
    /// </summary>
    private async Task<Navigator> RetailProductAsync(string[]? regions = null, string? cdns = "us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n",
        string opens = "us")
    {
        var rows = string.Concat((regions ?? ["us", "eu"]).Select(r => $"{r}|{A}|{B}|{C}|60000|12.0.1.60000|{C}\n"));
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, Encoding.UTF8.GetBytes($"{VersionsHeader}\n## seqn = 1\n{rows}"));
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        if (cdns is not null)
        {
            stores.Source.AddReply("wow", "us", ObservationKind.Cdns, Encoding.UTF8.GetBytes($"{CdnsHeader}\n## seqn = 1\n{cdns}"));
            await stores.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), default);
        }
        memory.Add(new BuildRef("wow", A, B, opens), ["enUS"], [new FakeFile(7, Size: 3)]);
        var nav = Nav(ViewRegistry.Default());
        Assert.True(nav.Go(":builds"));
        await nav.Loading.WaitAsync(Guard);
        Assert.True(nav.Drill(0));
        await nav.Loading.WaitAsync(Guard);
        Assert.IsType<ProductBuildsView>(nav.Current.View);
        return nav;
    }

    [Fact]
    public async Task FOnAProductBuildRowOpensItsFiles()
    {
        var nav = await RetailProductAsync();
        Assert.IsType<CatalogBuild>(nav.SelectedRow!.Tag);
        await Key(nav, 'f');
        var files = Assert.IsType<FilesView>(nav.Current.View);
        Assert.Equal(new FilesTarget(new BuildRef("wow", A, B, "us"), "enUS"), files.Target);
        Assert.Equal("builds > wow > files enUS", nav.Path);
        Assert.Equal(["7"], Ids(nav));
        Assert.Equal("Page 1, 7-7 (last). 12.0.1.60000 enUS.", Note(nav));
    }

    [Fact]
    public async Task FInTheBuildViewOpensItsFilesAndARowSaysSo()
    {
        var nav = await RetailProductAsync();
        Assert.True(nav.Drill(0));
        await nav.Loading.WaitAsync(Guard);
        Assert.Contains(nav.Current.Visible, r => r.Cells.SequenceEqual(["Files", "f lists the files (enUS)"]));
        await Key(nav, 'f');
        Assert.Equal(new FilesTarget(new BuildRef("wow", A, B, "us"), "enUS"), ((FilesView)nav.Current.View).Target);
        Assert.Equal("builds > wow > 12.0.1.60000 > files enUS", nav.Path);
    }

    [Fact]
    public async Task FSkipsAFirstRegionWhoseOnlyHostIsUnusable()
    {
        // The QA case on #175: cn sorts first and its cdns row lists only a host CdnRoute doesn't admit.
        var nav = await RetailProductAsync(["cn", "eu", "us"], Netease + "eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\nus|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        await Key(nav, 'f');
        Assert.Equal(new FilesTarget(new BuildRef("wow", A, B, "us"), "enUS"), ((FilesView)nav.Current.View).Target);
        Assert.Equal(["7"], Ids(nav));
    }

    [Fact]
    public async Task WithoutUsAFTakesTheFirstRegionWithAUsableRoute()
    {
        var nav = await RetailProductAsync(["cn", "eu"], Netease + "eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n", opens: "eu");
        await Key(nav, 'f');
        Assert.Equal(new FilesTarget(new BuildRef("wow", A, B, "eu"), "enUS"), ((FilesView)nav.Current.View).Target);
        Assert.Equal(["7"], Ids(nav));
    }

    [Fact]
    public async Task NoUsableRegionSaysSoWhenTheMetadataIsntStored()
    {
        var nav = await RetailProductAsync(["cn"], Netease, opens: "us");
        await Key(nav, 'f');
        Assert.Equal(new BuildRef("wow", A, B, "cn"), ((FilesView)nav.Current.View).Target!.Build);
        Assert.Equal("NeedsNetwork: metadata not stored and no region has a usable CDN route.", Note(nav));
        await Key(nav, 'm');
        Assert.Equal(BuildView.NoRoute("metadata"), nav.Message);
        Assert.Null(nav.Tasks.Running);
        Assert.DoesNotContain(stores.Opened, o => o.Writer);
    }

    /// <summary>A build the store lacks; <see cref="InMemoryBuilds"/> stands in for layer 3, and its open stores the build for layer 4.</summary>
    private (BuildRef Build, InMemoryBuilds Builds, List<bool> Writers) UnstoredBuild()
    {
        var build = new BuildRef("wow", B, B, "us");
        var builds = new InMemoryBuilds();
        var writers = new List<bool>();
        builds.Add(build, new BuildsFakeBuild(new BuildMetadata("1.15.9.70576", default, default, default, 3, 2, ["enUS"]), []));
        builds.BeforeOpen = (_, _) =>
        {
            lock (writers) writers.Add(stores.Opened[^1].Writer);
            memory.Add(build, ["enUS"], [new FakeFile(5, Size: 1), new FakeFile(6, Size: 2)]);
            return Task.CompletedTask;
        };
        session.Builds = _ => builds;
        return (build, builds, writers);
    }

    [Fact]
    public async Task MFetchesTheMetadataAsTheWriterThenTheListOpens()
    {
        var (build, _, writers) = UnstoredBuild();
        var nav = await FilesAsync(build);
        Assert.Equal("NeedsNetwork: metadata not stored. m fetches it (writer lock).", Note(nav));
        Assert.Contains(nav.Help().Split('\n'), l => l.StartsWith("  m        metadata: fetch configs, ENCODING, ROOT, indexes (writer lock)", StringComparison.Ordinal));

        Assert.True(nav.TryAction('m'));
        Assert.Equal("metadata", nav.Tasks.Running);
        await nav.Tasks.Current.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("metadata: stored, 2 files, 3 archives (us).", nav.Tasks.Status);
        Assert.Equal([true], writers);
        Assert.Equal([true], stores.WriterCreate);
        // Only the session's read-only handle is still open: the writer's went at the end.
        Assert.Equal(1, stores.Open);
        Assert.Equal(["5", "6"], Ids(nav));
    }

    [Fact]
    public async Task XCancelsTheMetadataFetchAndBusySaysSo()
    {
        var (build, builds, _) = UnstoredBuild();
        var nav = await FilesAsync(build);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builds.BeforeOpen = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        Assert.True(nav.TryAction('m'));
        await started.Task.WaitAsync(Guard);
        Assert.StartsWith("metadata: configs, ENCODING, ROOT, indexes (us); x cancels", nav.Tasks.Status);
        Assert.True(nav.Tasks.Cancel());
        await nav.Tasks.Current.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("metadata canceled.", nav.Tasks.Status);
        // Only the session's read-only handle is still open: the writer's went at the end.
        Assert.Equal(1, stores.Open);
        Assert.StartsWith("NeedsNetwork: metadata not stored.", Note(nav));

        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.True(nav.TryAction('m'));
        await nav.Tasks.Current.WaitAsync(Guard);
        Assert.Equal(MetadataTask.Busy, nav.Tasks.Status);
        Fits(MetadataTask.Busy);
    }

    [Fact]
    public void MetadataLinesNameTheStateAndFit()
    {
        var build = new BuildRef("wow", A, B, "us");
        Assert.Equal("metadata: no network or CDN route at BuildConfig (OBJECT_ACCESS_NO_CDN_CONTEXT).",
            MetadataTask.Line(new(build, BuildOpenState.NeedsNetwork, null, BuildPart.BuildConfig, "OBJECT_ACCESS_NO_CDN_CONTEXT", "x", null)));
        foreach (var state in Enum.GetValues<BuildOpenState>().Where(s => s != BuildOpenState.Open))
            Fits(MetadataTask.Line(new(build, state, null, BuildPart.ArchiveIndex, new string('C', 60), "x", null)));
        Assert.Equal("metadata: can't open: Corrupt at Root (BLTE_CHECKSUM).",
            MetadataTask.Line(new(build, BuildOpenState.Corrupt, null, BuildPart.Root, "BLTE_CHECKSUM", "x", null)));
    }

    [Fact]
    public void FOnABuildWithNoRegionOrNoRowSaysSo()
    {
        var build = new CatalogBuild("wow", "id", "12.0.1.60000", A, null, new Dictionary<string, string>(), default, default, false, []);
        Assert.Equal(FilesView.NoRegion, FilesView.Open(build).Say);
        Assert.Null(FilesView.Open(build).Open);
        Assert.Equal("No build selected.", FilesView.Open(null).Say);
    }

    [Fact]
    public async Task TheFilesEntrySaysHowToOpenABuildThenReopensTheLastOne()
    {
        var nav = Nav(ViewRegistry.Default());
        Assert.True(nav.Go(":files"));
        await nav.Loading.WaitAsync(Guard);
        Assert.Contains("  :files    a build's files; f in :builds opens one", nav.Help());
        Assert.Equal("files", nav.Path);
        Assert.Empty(nav.Current.Visible);
        Assert.Equal(FilesView.NoTarget, Note(nav));
        await Key(nav, 'n');
        Assert.Equal(FilesView.NoTarget, nav.Message);
        Assert.Null(session.LastFiles);

        var product = await RetailProductAsync();
        await Key(product, 'f');
        Assert.Equal(new FilesTarget(new BuildRef("wow", A, B, "us"), "enUS"), session.LastFiles!.Target);
        Assert.True(nav.Go(":files"));
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("files enUS", nav.Path);
        Assert.Equal(["7"], Ids(nav));
        Assert.Equal("Page 1, 7-7 (last). 12.0.1.60000 enUS.", Note(nav));
    }

    // ---------------------------------------------------------------- cancellation and stale loads

    [Fact]
    public async Task LeavingMidListStopsTheListingAndDropsItsResult()
    {
        var nav = await FilesAsync(Big);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated.DuringList = async ct => { gated.DuringList = null; reading.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        nav.Reload();
        var frame = nav.Current;
        await reading.Task.WaitAsync(Guard);
        Assert.True(nav.Go("other"));
        await nav.Loading.WaitAsync(Guard);
        await gated.ListEnded.Task.WaitAsync(Guard);
        Assert.Equal(1, gated.ListsCanceled);
        Assert.False(frame.Loading);
        Assert.Equal("files enUS", nav.Path);
        Assert.Null(nav.Current.Error);

        // Every lease the canceled load took was given back: closing the session closes every handle and the store.
        navigators.Remove(nav);
        await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync();
        Assert.Equal(gated.HandlesOpened, gated.HandlesClosed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AStaleLoadFinishingLateDoesntMoveThePageEnd()
    {
        var nav = await FilesAsync(Big);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated.HoldNextList = hold;
        nav.SetFilter("files/12");
        await gated.Held.Task.WaitAsync(Guard);
        var stale = nav.Loading;
        nav.SetFilter("");
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("Page 1, 1-500; n next. 12.0.1.60000 enUS.", Note(nav));
        // The filtered load ends on its last page; it must not stand for the page shown.
        hold.SetResult();
        await stale.WaitAsync(Guard);
        await Key(nav, 'n');
        Assert.Null(nav.Message);
        Assert.Equal("501", Ids(nav)[0]);
    }

    [Fact]
    public async Task AStaleLoadFinishingLateIsNotTheLastFilesOpened()
    {
        var nav = await FilesAsync(Small);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gated.HoldNextList = hold;
        nav.Reload();
        await gated.Held.Task.WaitAsync(Guard);
        var stale = nav.Loading;
        await AnswerAsync(nav, 'l', "deDE");
        Assert.Equal(new FilesTarget(Small, "deDE"), session.LastFiles!.Target);
        hold.SetResult();
        await stale.WaitAsync(Guard);
        Assert.Equal(new FilesTarget(Small, "deDE"), session.LastFiles!.Target);
        Assert.Equal(["14"], Ids(nav));
    }

    [Fact]
    public async Task PagingEndsAtTheLastFileDataId()
    {
        var edge = new BuildRef("wow", B, C, "us");
        memory.Add(edge, ["enUS"], Enumerable.Range(0, FilesView.PageSize + 1).Select(i => new FakeFile(uint.MaxValue - (uint)i)));
        var nav = await FilesAsync(edge);
        Assert.Equal($"{uint.MaxValue - 500}", Ids(nav)[0]);
        Assert.Equal($"{uint.MaxValue - 1}", Ids(nav)[^1]);
        await Key(nav, 'n');
        Assert.Equal([$"{uint.MaxValue}"], Ids(nav));
        Assert.Equal($"Page 2, {uint.MaxValue}-{uint.MaxValue} (last); p back. 12.0.1.60000 enUS.", Note(nav));
        await Key(nav, 'n');
        Assert.Equal("This is the last page.", nav.Message);
    }

    [Fact]
    public async Task TheLongestNotesAreCutToEightyColumnsStateFirst()
    {
        memory.Names.State = FileNamesState.Off;
        var nav = await FilesAsync(Big);
        await Key(nav, 'n');
        Assert.Equal("Page 2, 501-1000; n next, p back. Names off in Settings: no paths, / finds none.", Note(nav));

        memory.Names.State = FileNamesState.Failed;
        memory.Names.Code = new string('C', 90);
        nav.SetFilter(new string('x', 120));
        await nav.Loading.WaitAsync(Guard);
        await AnswerAsync(nav, 't', string.Join(",", Enum.GetNames<FileFormat>()));
        await Key(nav, 's');
        var note = Note(nav);
        // A long code is cut so the names note still fits after the page.
        Assert.StartsWith("Page 1, no files match. Names failed (CCC", note);
        Assert.EndsWith("...): N tries again.", note);
        Assert.Equal(80, note.Length);

        await AnswerAsync(nav, 't', new string('y', 200));
        Assert.StartsWith("No format yyy", nav.Message);
        await AnswerAsync(nav, 'l', new string('z', 200));
        Assert.StartsWith("No locale zzz", nav.Message);
        Assert.True(FilesView.NoTarget.Length <= 80);
        foreach (var state in Enum.GetValues<FilesOpenState>().Where(s => s != FilesOpenState.Open))
        {
            foreach (var routed in new[] { true, false })
            {
                var line = FilesView.NotOpen(new(Small, "enUS", state, null, new string('C', 60), "m", null), routed);
                Fits(line);
                Assert.StartsWith(state.ToString(), line);
            }
        }
        Assert.True(FilesView.Open(new CatalogBuild("wow", "id", "v", A, null, new Dictionary<string, string>(), default, default, false, [])).Say!.Length <= 80);
    }

    [Fact]
    public async Task AFilterSetWhileASlowLoadRunsWinsAndTheStaleResultIsDropped()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first open ignores cancellation, so the keys below land while the stale load still runs.
        gated.BeforeOpen = async () => { gated.BeforeOpen = null; entered.TrySetResult(); await release.Task; };
        var nav = Nav(new ViewRegistry().Add(new("files", "", () => new FilesView(new(Big, "enUS"), "12.0.1.60000"))));
        Assert.True(nav.Go("files"));
        await entered.Task.WaitAsync(Guard);
        var stale = nav.Loading;
        nav.SetFilter("files/12");
        for (var i = 0; i < 4; i++) Assert.True(nav.TryAction('s'));
        release.SetResult();
        await stale.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
        // 12 is named elsewhere; 120-129 and 1200.
        Assert.Equal(["120", "121", "122", "123", "124", "125", "126", "127", "128", "129", "1200"], Ids(nav));
        Assert.StartsWith("Page 1, 120-1200 (last). Filter: path files/12.", Note(nav));
    }

    /// <summary>Wraps the in-memory file system: can hold an open (ignoring cancellation) and hold a listing after its first file.</summary>
    private sealed class GatedFileSystem(IFileSystem inner) : IFileSystem
    {
        public Func<Task>? BeforeOpen { get; set; }
        public Func<CancellationToken, Task>? DuringList { get; set; }
        /// <summary>When set, the next listing waits on it before its first file, then lists ignoring cancellation: a stale load that runs on.</summary>
        public TaskCompletionSource? HoldNextList;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int opened, closed;
        public int HandlesOpened => Volatile.Read(ref opened);
        public int HandlesClosed => Volatile.Read(ref closed);
        public TaskCompletionSource ListEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int canceled;
        public int ListsCanceled => Volatile.Read(ref canceled);
        public IFileNames Names => inner.Names;

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            if (BeforeOpen is { } wait) await wait();
            var result = await inner.ForAsync(build, locale, cancellationToken);
            if (result.Files is not { } f) return result;
            Interlocked.Increment(ref opened);
            return result with { Files = new Files(this, f) };
        }

        private sealed class Files(GatedFileSystem owner, IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.closed); return inner.DisposeAsync(); }

            public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                var first = true;
                if (Interlocked.Exchange(ref owner.HoldNextList, null) is { } hold)
                {
                    owner.Held.TrySetResult();
                    await hold.Task;
                    cancellationToken = CancellationToken.None;
                }
                {
                    await foreach (var file in inner.ListAsync(filter, cancellationToken))
                    {
                        if (!first && owner.DuringList is { } wait)
                        {
                            try { await wait(cancellationToken); }
                            catch (OperationCanceledException) { Interlocked.Increment(ref owner.canceled); owner.ListEnded.TrySetResult(); throw; }
                        }
                        first = false;
                        yield return file;
                    }
                }
            }

            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
            public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) => inner.ReadRangeAsync(file, offset, length, cancellationToken);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
                inner.EnsureAsync(selection, priority, progress, cancellationToken);
        }
    }
}
