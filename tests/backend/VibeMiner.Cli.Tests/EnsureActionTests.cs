using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>
/// <c>g</c> and <c>G</c> in the Files views: Ensure as the store's writer over its own file system, the confirm, the filter it
/// passes, progress, each outcome line, busy, a failed open, cancel, and the reload showing what got stored.
/// </summary>
public sealed class EnsureActionTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32), "us");
    private static readonly BuildRef Big = new("wow", new string('c', 32), new string('b', 32), "us");
    private static readonly FilesTarget Target = new(Build, "enUS");

    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new() { ProgressInterval = TimeSpan.FromTicks(1) };
    private readonly WriterFiles writer;
    private readonly ReadOnlyFiles readOnly;
    private readonly BrowseSession session;
    private readonly List<Navigator> navigators = [];
    /// <summary>The writer side's closes, in order: its handle, its file system, its store.</summary>
    private readonly List<string> closed = [];
    private int writerFileSystems;
    private int readerFileSystems;

    public EnsureActionTests()
    {
        memory.Add(Build, ["enUS", "deDE"],
        [
            new FakeFile(10, Storage: FileStorage.Stored, Locales: ["enUS"], Content: Bytes(16)),
            new FakeFile(11, Locales: ["enUS"], Content: Bytes(20)),
            new FakeFile(12, Locales: ["enUS"], Content: Bytes(30)),
            new FakeFile(13, Locales: ["enUS"], NotInEncoding: true),
            new FakeFile(14, Size: 40, Locales: ["enUS"]),
            new FakeFile(15, Locales: ["enUS"], Content: Bytes(50), ReadFailure: new FakeReadFailure(FileReadState.Missing, "CDN_404")),
            new FakeFile(16, Locales: ["deDE"], Content: Bytes(60)),
        ]);
        memory.Add(Big, ["enUS"], Enumerable.Range(1, 600).Select(i => new FakeFile((uint)i, Content: Bytes(1))));
        memory.Names.Use(new Dictionary<uint, string>
        {
            [10] = "interface/a.blp", [11] = "interface/b.blp", [12] = "sound/c.ogg", [14] = "sound/d.ogg", [15] = "sound/e.ogg", [16] = "sound/f.ogg",
        });
        writer = new WriterFiles(memory, closed);
        readOnly = new ReadOnlyFiles(memory);
        stores.Disposing = isWriter => { if (isWriter) lock (closed) closed.Add("store"); };
        session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), Path.Combine(Data, ObjectStore.DefaultFolderName))
        {
            // The handle being opened is the last one asked for: the writer's for an ensure, the session cache's otherwise.
            FileSystem = (_, _, _, _) =>
            {
                if (!stores.Opened[^1].Writer) { Interlocked.Increment(ref readerFileSystems); return Task.FromResult<IFileSystem>(readOnly); }
                Interlocked.Increment(ref writerFileSystems);
                return Task.FromResult<IFileSystem>(writer);
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var nav in navigators) await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync();
    }

    private static byte[] Bytes(int length) => Enumerable.Repeat((byte)0x41, length).ToArray();

    private async Task<Navigator> NavAsync(BrowseView view)
    {
        var nav = new Navigator(new ViewRegistry().Add(new("v", "", () => view)), session, a => a());
        navigators.Add(nav);
        Assert.True(nav.Go("v"));
        await nav.Loading.WaitAsync(Guard);
        Assert.Null(nav.Current.Error);
        return nav;
    }

    private Task<Navigator> ListAsync(BuildRef? build = null) => NavAsync(new FilesView(new(build ?? Build, "enUS"), "12.0.1.60000"));

    /// <summary>Waits for the task and the reload after it; returns the status line, checked to fit 80 columns.</summary>
    private static async Task<string> EndedAsync(Navigator nav)
    {
        await nav.Tasks.Current.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
        Fits(nav.Tasks.Status);
        return nav.Tasks.Status;
    }

    private static void Fits(string? line) => Assert.True(line is null || line.Length <= 80, $"{line?.Length} columns: {line}");

    private static void Select(Navigator nav, string id) => nav.Current.Selected = nav.Current.Visible.ToList().FindIndex(r => r.Cells[0] == id);

    private static string StoredCell(Navigator nav, string id) => nav.Current.Visible.Single(r => r.Cells[0] == id).Cells[4];

    /// <summary>Waits until the status line is <paramref name="expected"/> (progress arrives on a pool thread).</summary>
    private static async Task StatusAsync(Navigator nav, string expected)
    {
        var until = DateTime.UtcNow + Guard;
        while (nav.Tasks.Status != expected)
        {
            if (DateTime.UtcNow > until) Assert.Fail($"Status stayed {nav.Tasks.Status}, wanted {expected}");
            await Task.Delay(5);
        }
    }

    private void AssertWriterClosedInOrder()
    {
        lock (closed) Assert.Equal(["handle", "files", "store"], closed);
    }

    [Fact]
    public async Task GOnAListRowEnsuresThatFileAsTheWriterAndTheReloadShowsItStored()
    {
        var nav = await ListAsync();
        Assert.Equal("no", StoredCell(nav, "11"));
        Select(nav, "11");
        Assert.True(nav.TryAction('g'));
        Assert.Equal("ensure: done, 1 file stored (0 already), 0 B fetched", await EndedAsync(nav));
        Assert.Equal([(11u, EnsurePriority.Interactive)], memory.EnsureRequests);
        var asked = Assert.Single(writer.Selections);
        Assert.Equal(EnsureSelectionKind.One, asked.Kind);
        Assert.Equal(11u, asked.Files![0].FileDataId);
        // The writer's own file system, opened once and closed handle first, store last; the session's cache stays read-only.
        Assert.Equal(1, writerFileSystems);
        Assert.Equal([true], stores.Opened.Where(o => o.Writer).Select(o => o.Writer));
        AssertWriterClosedInOrder();
        Assert.Equal("yes", StoredCell(nav, "11"));
        Assert.Equal("no", StoredCell(nav, "12"));
    }

    [Fact]
    public async Task GWithNoRowSaysSoAndStartsNothing()
    {
        var nav = await NavAsync(new FilesView(new(Build, "frFR"), "x"));
        Assert.Empty(nav.Current.Visible);
        Assert.True(nav.TryAction('g'));
        Assert.Equal("No file selected.", nav.Message);
        Assert.Null(nav.Tasks.Running);
        Assert.DoesNotContain(stores.Opened, o => o.Writer);
    }

    [Fact]
    public async Task GInTheDetailEnsuresTheFileAndTheReloadRetriesTheFailedRead()
    {
        var listed = new BuildFile(11, "interface/b.blp", FilePathSource.Listfile, 20, new string('0', 32), FileFormat.Unknown, FileFormatSource.None,
            FileStorage.NotStored, false);
        var view = new FileView(Target, listed);
        var nav = await NavAsync(view);
        var text = nav.Current.Content!.Text!;
        Assert.Contains("Stored       no (browse reads only stored bytes; g fetches the file)", text);
        Assert.EndsWith("00000000  not stored (OBJECT_ACCESS_NEEDS_NETWORK); g fetches it", text);

        Assert.True(nav.TryAction('g'));
        Assert.Equal("ensure: done, 1 file stored (0 already), 0 B fetched", await EndedAsync(nav));
        Assert.Equal([(11u, EnsurePriority.Interactive)], memory.EnsureRequests);
        text = nav.Current.Content!.Text!;
        Assert.Contains("Stored       yes", text);
        Assert.Contains("00000000  41 41 41 41", text);
        Assert.EndsWith("End of the file.", text);
        AssertWriterClosedInOrder();
    }

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("yes please")]
    public async Task GAsksFirstAndAnythingButYStartsNothing(string answer)
    {
        var nav = await ListAsync();
        Assert.True(nav.TryAction('G'));
        Assert.Equal("ensure all enUS files (writer lock)? y/N", nav.Asking!.Question);
        Assert.Equal("", nav.Asking.Initial);
        nav.Answer(answer);
        Assert.Equal("ensure all: not started; nothing fetched.", nav.Message);
        Assert.Null(nav.Tasks.Running);
        Assert.DoesNotContain(stores.Opened, o => o.Writer);
        Assert.Empty(memory.EnsureRequests);
    }

    [Fact]
    public async Task GWithYEnsuresEveryMatchingFileInTheBackgroundAndSaysWhatFailed()
    {
        var nav = await ListAsync();
        Assert.True(nav.TryAction('G'));
        nav.Answer(" Y ");
        Assert.Equal("ensure: incomplete, 3 of 6 stored; 1 missing, 1 need network, 1 not in encoding.", await EndedAsync(nav));
        // 10 was stored; 13 has no encoded copy, so neither is asked for. deDE's 16 isn't selected.
        Assert.Equal([(11u, EnsurePriority.Background), (12u, EnsurePriority.Background), (14u, EnsurePriority.Background), (15u, EnsurePriority.Background)],
            memory.EnsureRequests);
        Assert.Equal(["yes", "yes", "yes", "no copy", "no", "no"], nav.Current.Visible.Select(r => r.Cells[4]));
        AssertWriterClosedInOrder();
    }

    [Fact]
    public async Task GPassesTheViewsFiltersButNotItsPage()
    {
        var nav = await ListAsync(Big);
        Assert.True(nav.TryAction('n'));
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("501", nav.Current.Visible[0].Cells[0]);
        Assert.True(nav.TryAction('G'));
        nav.Answer("y");
        Assert.Equal("ensure: done, 600 files stored (0 already), 0 B fetched", await EndedAsync(nav));
        var asked = Assert.Single(writer.Selections);
        Assert.Equal(EnsureSelectionKind.Matching, asked.Kind);
        Assert.Equal(0u, asked.Filter!.FromId);
        Assert.Equal(uint.MaxValue, asked.Filter.ToId);
        Assert.Equal(600, memory.EnsureRequests.Count);

        var small = await ListAsync();
        small.SetFilter("interface/");
        await small.Loading.WaitAsync(Guard);
        Assert.True(small.TryAction('s'));
        await small.Loading.WaitAsync(Guard);
        Assert.True(small.TryAction('s'));
        await small.Loading.WaitAsync(Guard);
        Assert.True(small.TryAction('t'));
        small.Answer("blp");
        await small.Loading.WaitAsync(Guard);
        Assert.Equal(["11"], small.Current.Visible.Select(r => r.Cells[0]));
        Assert.True(small.TryAction('G'));
        // The filters are cut so the shell, which shows at most MaxQuestion columns, still shows the y/N choice.
        Assert.Equal("ensure all enUS, path inter... (writer lock)? y/N", small.Asking!.Question);
        Assert.Equal(BrowseShell.MaxQuestion, small.Asking.Question.Length);
        small.Answer("y");
        Assert.Equal("ensure: done, 1 file stored (0 already), 0 B fetched", await EndedAsync(small));
        var filter = writer.Selections[^1].Filter!;
        Assert.Equal("interface/", filter.PathPrefix);
        Assert.Equal([FileFormat.Blp], filter.Formats!);
        Assert.Equal([FileStorage.NotStored], filter.Storage!);
        Assert.Equal(0u, filter.FromId);
        Assert.Equal((11u, EnsurePriority.Background), memory.EnsureRequests[^1]);
    }

    [Fact]
    public async Task GsQuestionCutsALongPathToFit()
    {
        var nav = await ListAsync();
        nav.SetFilter("interface/" + new string('x', 100));
        await nav.Loading.WaitAsync(Guard);
        Assert.True(nav.TryAction('G'));
        var question = nav.Asking!.Question;
        Assert.Equal(BrowseShell.MaxQuestion, question.Length);
        Assert.StartsWith("ensure all enUS, path ", question, StringComparison.Ordinal);
        Assert.EndsWith("... (writer lock)? y/N", question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressLinesShowFilesAndBytesWhileItRunsAndXCancels()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.BeforeFetch = async (id, _, ct) => { held.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var nav = await ListAsync();
        Assert.True(nav.TryAction('G'));
        nav.Answer("y");
        await held.Task.WaitAsync(Guard);
        // Listed: 10 already stored and 13 settled; 11 is being fetched.
        await StatusAsync(nav, "ensure: 2 of 6 files, 16 B of 156 B; x cancels");
        Assert.True(nav.Tasks.Cancel());
        Assert.Equal("ensure: canceled, 1 of 6 files stored; what got stored stays.", await EndedAsync(nav));
        Assert.Equal("no", StoredCell(nav, "11"));
        AssertWriterClosedInOrder();
    }

    [Fact]
    public async Task ABusyStoreSaysSoAndFetchesNothing()
    {
        var nav = await ListAsync();
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Select(nav, "11");
        Assert.True(nav.TryAction('g'));
        Assert.Equal(EnsureTask.Busy, await EndedAsync(nav));
        Assert.Equal(0, writerFileSystems);
        Assert.Empty(memory.EnsureRequests);
        Assert.Equal("no", StoredCell(nav, "11"));
    }

    [Fact]
    public async Task AFailedOpenNamesTheStateAndFetchesNothing()
    {
        var nav = await ListAsync();
        writer.OpenFails = new(Build, "enUS", FilesOpenState.NeedsNetwork, null, "CDN_OFFLINE", "offline", null);
        Select(nav, "11");
        Assert.True(nav.TryAction('g'));
        Assert.Equal("ensure: no network or CDN route for the build (CDN_OFFLINE); nothing fetched.", await EndedAsync(nav));
        Assert.Empty(memory.EnsureRequests);
        // No handle to close: the file system, then the store.
        lock (closed) Assert.Equal(["files", "store"], closed);
    }

    [Fact]
    public void AnUnsupportedFileIsSaidAsUnsupportedNotRefused() =>
        Assert.Equal("ensure: incomplete, 1 of 3 stored; 1 refused, 1 unsupported.",
            EnsureTask.Line(Result(EnsureOutcome.Incomplete, 0, FileEnsureState.Stored, FileEnsureState.Unsupported, FileEnsureState.Refused)));

    [Fact]
    public void NotOpenedLinesNameEachState()
    {
        Assert.Equal("ensure: no deDE files in this build; nothing fetched.",
            EnsureTask.NotOpened(new(Build, "deDE", FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", null, null)));
        Assert.Equal("ensure: can't open the build: Corrupt (ENCODING_BAD); nothing fetched.",
            EnsureTask.NotOpened(new(Build, "enUS", FilesOpenState.Corrupt, null, "ENCODING_BAD", null, null)));
        Assert.Equal("ensure: can't open the build: Encrypted (no code); nothing fetched.",
            EnsureTask.NotOpened(new(Build, "enUS", FilesOpenState.Encrypted, null, null, null, null)));
    }

    private static EnsureResult Result(EnsureOutcome outcome, long cdn, params FileEnsureState[] states) =>
        new(outcome, new(states.Length, states.Length, 0, 0, cdn, true),
            states.Select((s, i) => new FileEnsured(FileRef.Id((uint)i), (uint)i, s, null)).ToArray());

    [Fact]
    public void EachOutcomeHasItsLine()
    {
        Assert.Equal("ensure: done, 3 files stored (1 already), 2.0 MiB fetched",
            EnsureTask.Line(Result(EnsureOutcome.Completed, 2L << 20, FileEnsureState.Stored, FileEnsureState.Stored, FileEnsureState.AlreadyStored)));
        Assert.Equal("ensure: incomplete, 1 of 9 stored; 1 missing, 1 need network, 1 unavailable, ...",
            EnsureTask.Line(Result(EnsureOutcome.Incomplete, 0, FileEnsureState.Stored, FileEnsureState.Missing, FileEnsureState.NeedsNetwork,
                FileEnsureState.Unavailable, FileEnsureState.Refused, FileEnsureState.Refused, FileEnsureState.StoreFailed, FileEnsureState.NotInEncoding,
                FileEnsureState.UnknownFile)));
        Assert.Equal("ensure: incomplete, 0 of 3 stored; 1 unavailable, 1 refused, 1 store failed.",
            EnsureTask.Line(Result(EnsureOutcome.Incomplete, 0, FileEnsureState.StoreFailed, FileEnsureState.Refused, FileEnsureState.Unavailable)));
        Assert.Equal("ensure: incomplete, 0 of 2 stored; 1 not in locale, 1 unknown.",
            EnsureTask.Line(Result(EnsureOutcome.Incomplete, 0, FileEnsureState.NotInLocale, FileEnsureState.UnknownFile)));
        Assert.Equal("ensure: canceled, 2 of 4 files stored; what got stored stays.",
            EnsureTask.Line(Result(EnsureOutcome.Canceled, 0, FileEnsureState.Stored, FileEnsureState.AlreadyStored, FileEnsureState.Canceled, FileEnsureState.Missing)));
    }

    [Fact]
    public void UnavailableForWantOfACdnRouteIsSaidAsThat()
    {
        // The QA case on #175: the build opened in a region with no usable route, so the fetch had no CDN context.
        var r = Result(EnsureOutcome.Incomplete, 0, FileEnsureState.Unavailable, FileEnsureState.Unavailable);
        r = r with { Files = [r.Files[0] with { Code = "OBJECT_ACCESS_NO_CDN_CONTEXT" }, r.Files[1] with { Code = "CDN_TIMEOUT" }] };
        Assert.Equal("ensure: incomplete, 0 of 2 stored; 1 no CDN route, 1 unavailable.", EnsureTask.Line(r));
    }

    [Fact]
    public async Task EscBackToTheListAfterGInTheDetailShowsTheFileStoredAndClearsTheEndedLine()
    {
        var nav = await ListAsync();
        Select(nav, "11");
        Assert.True(nav.Drill(nav.Current.Selected));
        await nav.Loading.WaitAsync(Guard);
        Assert.IsType<FileView>(nav.Current.View);
        Assert.True(nav.TryAction('g'));
        Assert.Equal("ensure: done, 1 file stored (0 already), 0 B fetched", await EndedAsync(nav));

        Assert.True(nav.Back());
        await nav.Loading.WaitAsync(Guard);
        Assert.IsType<FilesView>(nav.Current.View);
        Assert.Equal("yes", StoredCell(nav, "11"));
        Assert.Equal("", nav.Tasks.Status);
    }

    [Fact]
    public async Task ARunningTasksLineStaysWhenTheUserMovesOn()
    {
        var nav = await ListAsync();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stores.BeforeOpen = async (isWriter, ct) => { if (isWriter) { held.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        Select(nav, "11");
        Assert.True(nav.TryAction('g'));
        await held.Task.WaitAsync(Guard);
        Assert.True(nav.Drill(nav.Current.Selected));
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("ensure: taking the writer lock", nav.Tasks.Status);
        Assert.True(nav.Tasks.Cancel());
        Assert.Equal("ensure canceled.", await EndedAsync(nav));
        stores.BeforeOpen = null;
    }

    [Fact]
    public void ACancelWhileStillListingSaysTheTotalIsOnlyWhatWasListed()
    {
        var listing = Result(EnsureOutcome.Canceled, 0, FileEnsureState.Stored, FileEnsureState.Canceled, FileEnsureState.Canceled);
        listing = listing with { Final = listing.Final with { Listed = false } };
        Assert.Equal("ensure: canceled, 1 of 3 listed stored; what got stored stays.", EnsureTask.Line(listing));
    }

    [Fact]
    public void ProgressLinesSayListingUntilTheTotalsAreFinalAndFitWithThePrefix()
    {
        Assert.Equal("120 of 500 files, 3.1 MiB of 9.0 MiB; x cancels",
            EnsureTask.ProgressLine(new(120, 500, 3_250_586, 9L << 20, 0, Listed: true)));
        Assert.Equal("listing: 120 of 500 files, 9.0 MiB so far; x cancels",
            EnsureTask.ProgressLine(new(120, 500, 3_250_586, 9L << 20, 0, Listed: false)));
        foreach (var listed in new[] { true, false })
        {
            var line = "ensure: " + EnsureTask.ProgressLine(new(1_234_567, 1_234_567, 1023L << 30, 1023L << 30, 0, listed));
            Fits(line);
        }
    }

    [Fact]
    public void HugeCountsStillFit()
    {
        var many = Enumerable.Repeat(FileEnsureState.Missing, 3).Concat([FileEnsureState.NeedsNetwork, FileEnsureState.Unavailable, FileEnsureState.Refused,
            FileEnsureState.StoreFailed, FileEnsureState.NotInEncoding, FileEnsureState.NotInLocale, FileEnsureState.UnknownFile]).ToArray();
        foreach (var outcome in Enum.GetValues<EnsureOutcome>()) Fits(EnsureTask.Line(Result(outcome, 1023L << 40, many)));
        Fits(EnsureTask.NotOpened(new(Build, "enUS", FilesOpenState.StoreFailed, null, new string('C', 90), null, null)));
    }

    // ---------------------------------------------------------------- N: the community file names (#182)

    private static readonly Dictionary<uint, string> Listfile = new()
    {
        [10] = "interface/icons/a.blp", [11] = "Interface/Icons/b.blp", [12] = "sound/c.ogg", [14] = "sound/d.ogg",
    };

    private static string Note(Navigator nav) => nav.Current.Content!.Note!;

    [Fact]
    public async Task NFetchesTheNamesAsTheWriterThenPathsShowAndSlashFiltersByPrefix()
    {
        memory.Names.State = FileNamesState.NotReady;
        var asked = new List<(bool Force, bool Writer)>();
        memory.Names.OnUpdate = (force, _) =>
        {
            lock (asked) asked.Add((force, stores.Opened[^1].Writer));
            memory.Names.Use(Listfile);
            return Task.CompletedTask;
        };
        var nav = await ListAsync();
        Assert.Contains("Names not loaded: N fetches them.", Note(nav));
        Assert.All(nav.Current.Visible, r => Assert.Equal("", r.Cells[1]));
        Assert.Contains(nav.Help().Split('\n'), l => l == "  N        names: fetch file names from wow-listfile (writer lock)");

        Assert.True(nav.TryAction('N'));
        Assert.Equal(NamesTask.Name, nav.Tasks.Running);
        Assert.Equal("names: ready, 4 names (fake).", await EndedAsync(nav));
        // One unforced update, through the writer's own file system, which closed before its store.
        Assert.Equal([(false, true)], asked);
        Assert.Equal(1, writerFileSystems);
        lock (closed) Assert.Equal(["files", "store"], closed);
        Assert.Equal(1, stores.Open);
        // The session's read-only file system was dropped, so the reload made a new one that loads the new copy.
        Assert.Equal(2, readerFileSystems);
        Assert.DoesNotContain("Names", Note(nav));
        Assert.Equal("interface/icons/a.blp", nav.Current.Visible.Single(r => r.Cells[0] == "10").Cells[1]);

        nav.SetFilter("interface/icons/");
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal(["10", "11"], nav.Current.Visible.Select(r => r.Cells[0]));
    }

    [Fact]
    public async Task XCancelsTheNamesFetchAndTheWriterLockGoes()
    {
        memory.Names.State = FileNamesState.NotReady;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.Names.OnUpdate = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var nav = await ListAsync();
        Assert.True(nav.TryAction('N'));
        await started.Task.WaitAsync(Guard);
        await StatusAsync(nav, "names: checking and fetching wow-listfile from GitHub; x cancels");
        Assert.True(nav.Tasks.Cancel());
        Assert.Equal("names canceled.", await EndedAsync(nav));
        Assert.Equal(0, session.WriterLeases);
        Assert.Equal(1, stores.Open);
        Assert.Contains("Names not loaded: N fetches them.", Note(nav));
    }

    [Fact]
    public async Task NWhenTheStoreIsBusyOrTheSwitchIsOffFetchesNothing()
    {
        memory.Names.State = FileNamesState.NotReady;
        var nav = await ListAsync();
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.True(nav.TryAction('N'));
        Assert.Equal(NamesTask.Busy, await EndedAsync(nav));
        Assert.Equal(0, memory.Names.Updates);

        stores.WriterFailure = null;
        session.NameSwitch = new Switch(false);
        var writers = stores.Opened.Count(o => o.Writer);
        Assert.True(nav.TryAction('N'));
        Assert.Equal(NamesTask.Off, await EndedAsync(nav));
        Assert.Equal(writers, stores.Opened.Count(o => o.Writer));
        Assert.Equal(0, memory.Names.Updates);
    }

    [Fact]
    public void NamesLinesSayEachOutcomeAndFit()
    {
        Assert.Equal("names: ready, 1 name (v1).", NamesTask.Line(new(FileNamesState.Ready, "v1", Count: 1)));
        Assert.Equal("names: kept v1 (2,000); the update failed (LISTFILE_FETCH_FAILED).",
            NamesTask.Line(new(FileNamesState.Ready, "v1", Count: 2_000, Code: "LISTFILE_FETCH_FAILED")));
        Assert.Equal("names: failed (CDN_OFFLINE); N tries again.", NamesTask.Line(new(FileNamesState.Failed, Code: "CDN_OFFLINE")));
        Assert.Equal(NamesTask.NoStore, NamesTask.Line(new(FileNamesState.Off)));
        var huge = new string('C', 90);
        foreach (var state in Enum.GetValues<FileNamesState>()) Fits(NamesTask.Line(new(state, huge, Count: int.MaxValue, Code: huge)));
        foreach (var line in new[] { NamesTask.Busy, NamesTask.Off, NamesTask.NoStore }) Fits(line);
    }

    private sealed class Switch(bool on) : IFileNameSwitch
    {
        public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken) => Task.FromResult(on);
    }

    [Fact]
    public async Task HelpListsTheEnsureKeysAndKeepsExport()
    {
        var list = await ListAsync();
        var help = list.Help();
        Assert.Contains("  g        ensure: fetch the selected file so it is stored (writer lock)", help);
        Assert.Contains("  G        ensure all: fetch every file the filters match (asks first)", help);
        var file = await NavAsync(new FileView(Target, new BuildFile(10, null, FilePathSource.None, 16, new string('0', 32), FileFormat.Unknown,
            FileFormatSource.None, FileStorage.Stored, false)));
        help = file.Help();
        Assert.Contains("  g        ensure: fetch this file so it is stored (writer lock)", help);
        Assert.Contains("  e        export the whole file to a new file at a path you give", help);
    }

    [Fact]
    public async Task EveryHelpLineOfTheFilesViewsFitsTheHelpDialog()
    {
        var list = await ListAsync();
        var file = await NavAsync(new FileView(Target, new BuildFile(10, null, FilePathSource.None, 16, new string('0', 32), FileFormat.Unknown,
            FileFormatSource.None, FileStorage.Stored, false)));
        foreach (var nav in new[] { list, file })
            Assert.All(nav.Help().Split('\n'), l => Assert.True(l.Length <= FilesView.HelpWidth, $"{l.Length} columns: {l}"));
    }

    /// <summary>The writer's file system: records each ensure's selection, can fail the open, and records closes.</summary>
    private sealed class WriterFiles(IFileSystem inner, List<string> closed) : IFileSystem, IAsyncDisposable
    {
        private readonly List<EnsureSelection> selections = [];
        public IReadOnlyList<EnsureSelection> Selections { get { lock (selections) return [.. selections]; } }
        public FilesOpenResult? OpenFails { get; set; }
        public IFileNames Names => inner.Names;

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            if (OpenFails is { } failed) return failed;
            var result = await inner.ForAsync(build, locale, cancellationToken);
            return result.Files is { } f ? result with { Files = new Handle(this, f) } : result;
        }

        public ValueTask DisposeAsync() { Close("files"); return ValueTask.CompletedTask; }
        private void Close(string what) { lock (closed) closed.Add(what); }

        private sealed class Handle(WriterFiles owner, IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() { owner.Close("handle"); return inner.DisposeAsync(); }
            public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
            public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) => inner.ReadRangeAsync(file, offset, length, cancellationToken);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken)
            {
                lock (owner.selections) owner.selections.Add(selection);
                return inner.EnsureAsync(selection, priority, progress, cancellationToken);
            }
        }
    }

    /// <summary>
    /// The session's read-only file system as the real one behaves: a read of a file that isn't stored answers NeedsNetwork (the
    /// fake's own reads would fetch it), and an ensure through it is a test failure.
    /// </summary>
    private sealed class ReadOnlyFiles(IFileSystem inner) : IFileSystem
    {
        public IFileNames Names => inner.Names;

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var result = await inner.ForAsync(build, locale, cancellationToken);
            return result.Files is { } f ? result with { Files = new Handle(f) } : result;
        }

        private sealed class Handle(IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public async Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) =>
                await StoredAsync(file, cancellationToken) ? await inner.GetAsync(file, cancellationToken) : NotStored(0);
            public async Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) =>
                await StoredAsync(file, cancellationToken) ? await inner.ReadRangeAsync(file, offset, length, cancellationToken) : NotStored(offset);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The session's read-only file system never ensures.");

            private async Task<bool> StoredAsync(FileRef file, CancellationToken ct) => (await inner.StatAsync(file, ct)).File?.Storage == FileStorage.Stored;
            private static FileRead NotStored(long offset) =>
                FileRead.Failed(FileReadState.NeedsNetwork, offset, "OBJECT_ACCESS_NEEDS_NETWORK", "Not stored, and this source is local only.");
        }
    }
}
