using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using BuildsFakeBuild = VibeMiner.Builds.Testing.FakeBuild;
using FakeFile = VibeMiner.Files.Testing.FakeFile;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Tables;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Terminal.Gui.Testing;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>
/// The table preload in <c>vm browse</c> (#159): <c>m</c> starts it over the session's tables composition, <c>P</c> stops and
/// resumes it, the status line says where it is, and browse exiting stops it. Over a scripted <see cref="ITables"/>; no network.
/// </summary>
public sealed class PreloadViewTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private const string Version = "1.15.9.70576";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly string B = new('b', 32);
    private static readonly BuildRef Build = new("wow", B, B, "us");

    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new();
    private readonly InMemoryBuilds builds = new();
    private readonly ScriptedTables tables = new();
    private readonly BrowseSession session;
    private readonly Navigator nav;
    private int composed, closed;

    public PreloadViewTests()
    {
        builds.Add(Build, new BuildsFakeBuild(new BuildMetadata(Version, default, default, default, 1_402, 159_749, ["enUS"]), []));
        builds.BeforeOpen = (_, _) =>
        {
            memory.Add(Build, ["enUS"], [new FakeFile(5, Size: 1)]);
            return Task.CompletedTask;
        };
        session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), Path.Combine(Data, ObjectStore.DefaultFolderName))
        {
            FileSystem = (_, _, _, _) => Task.FromResult<IFileSystem>(memory),
            Builds = _ => builds,
        };
        session.UseTables((handle, _, _, _) =>
        {
            Interlocked.Increment(ref composed);
            return Task.FromResult(new TablesLease(tables, async () =>
            {
                Interlocked.Increment(ref closed);
                await handle.DisposeAsync();
            }));
        });
        session.Preloads.Options = new TablePreloadOptions { ProgressInterval = TimeSpan.Zero };
        nav = new Navigator(new ViewRegistry().Add(new("files", "", () => new FilesView(new(Build, "enUS"), Version))), session, a => a());
    }

    public async ValueTask DisposeAsync()
    {
        tables.Release();
        await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync().AsTask().WaitAsync(Guard);
    }

    private async Task FilesAsync()
    {
        Assert.True(nav.Go("files"));
        await nav.Loading.WaitAsync(Guard);
    }

    /// <summary>m, waited for: the metadata task and the reload after it.</summary>
    private async Task MetadataAsync()
    {
        Assert.True(nav.TryAction('m'));
        await nav.Tasks.Current.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
    }

    /// <summary>Waits until <paramref name="done"/> holds, checked on every preload change; the timeout is a deadlock guard.</summary>
    private async Task Until(Func<bool> done)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check() { if (done()) met.TrySetResult(); }
        session.Preloads.Changed += Check;
        try
        {
            Check();
            await met.Task.WaitAsync(Guard);
        }
        finally { session.Preloads.Changed -= Check; }
    }

    private static void Fits(string line) => Assert.True(line.Length <= 80, $"{line.Length} columns: {line}");

    private const string Metadata = "metadata: stored, 159,749 files, 1,402 archives (us).";
    private const string Downloading = "preload 1.15.9.70576: downloading 120/900 files, 3.1/90.0 MiB";

    [Fact]
    public async Task MOpeningTheBuildStartsThePreloadOverTheWriterAndItRunsToTheEnd()
    {
        await FilesAsync();
        Assert.Equal("", nav.PreloadStatus);
        await MetadataAsync();
        Assert.Equal(Metadata, nav.Tasks.Status);
        await Until(() => nav.PreloadStatus == Downloading);
        Fits(nav.PreloadStatus);
        Assert.Equal([(Build, "enUS")], tables.Opened);
        Assert.Equal(1, composed);
        Assert.True(session.Tables!.Writer);
        // The composition keeps its writer lease; the metadata task's went at its end. Browsing goes on meanwhile.
        Assert.Null(nav.Tasks.Running);
        Assert.Equal(["5"], nav.Current.Visible.Select(r => r.Cells[0]));

        tables.Release();
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: done, 870 tables");
    }

    [Fact]
    public async Task PStopsThePreloadAndPAgainResumesIt()
    {
        await FilesAsync();
        await MetadataAsync();
        await Until(() => nav.PreloadStatus == Downloading);
        Assert.Contains(nav.Help().Split('\n'), l => l.StartsWith("  P        preload: stop or resume", StringComparison.Ordinal));

        Assert.True(nav.TryAction('P'));
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: stopped at 120/900 files; P resumes");
        await session.Preloads.Pending.WaitAsync(Guard);
        Assert.Equal(1, tables.Canceled);

        Assert.True(nav.TryAction('P'));
        await Until(() => nav.PreloadStatus == Downloading);
        Assert.Equal(2, tables.Opened.Count);
        tables.Release();
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: done, 870 tables");
        // One composition for the session: the resume reused it.
        Assert.Equal(1, composed);
    }

    [Fact]
    public async Task PWithNoPreloadStartsOneAndTheBuildViewTogglesTheLatest()
    {
        var buildNav = new Navigator(new ViewRegistry().Add(new("build", "", () => new BuildView(Catalogued))), session, a => a());
        try
        {
            Assert.True(buildNav.Go("build"));
            await buildNav.Loading.WaitAsync(Guard);
            Assert.True(buildNav.TryAction('P'));
            Assert.Equal(BuildView.NoPreload, buildNav.Message);
            Fits(BuildView.NoPreload);
            Assert.Contains(buildNav.Current.Visible, r => r.Cells[0] == "Preload");

            await FilesAsync();
            Assert.True(nav.TryAction('P'));
            await Until(() => nav.PreloadStatus == Downloading);
            // The build's detail view shows the same line and stops it.
            Assert.Equal(Downloading, buildNav.PreloadStatus);
            Assert.True(buildNav.TryAction('P'));
            Assert.Null(buildNav.Message);
            await Until(() => buildNav.PreloadStatus.EndsWith("stopped at 120/900 files; P resumes", StringComparison.Ordinal));
        }
        finally { await buildNav.DisposeAsync().AsTask().WaitAsync(Guard); }
    }

    private static readonly CatalogBuild Catalogued = new("wow", "id", Version, B, null, new Dictionary<string, string>(), default, default, false, []);

    [Fact]
    public async Task AReadOnlyCompositionRefusesWithALine()
    {
        // The app held the writer at the first :tables load, so the session's composition is read-only until browse restarts.
        stores.WriterFailure = ObjectStoreCodes.Busy;
        await using (await session.OpenTables!(session, default)) { }
        Assert.False(session.Tables!.Writer);
        stores.WriterFailure = null;

        await FilesAsync();
        await MetadataAsync();
        Assert.Equal("preload 1.15.9.70576: not started, the store is read-only (writer busy)", nav.PreloadStatus);
        Fits(nav.PreloadStatus);
        Assert.Empty(tables.Opened);
    }

    [Fact]
    public async Task ExitStopsAndAwaitsThePreloadBeforeTheCompositionCloses()
    {
        await FilesAsync();
        await MetadataAsync();
        await Until(() => nav.PreloadStatus == Downloading);
        tables.OnCanceled = () => Assert.Equal(0, Volatile.Read(ref closed));

        await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync().AsTask().WaitAsync(Guard);
        Assert.Equal(1, tables.Canceled);
        Assert.Equal(1, tables.Disposed);
        Assert.Equal(1, closed);
        Assert.Null(tables.CanceledFailure);
        // Nothing holds the store: the composition's writer lease went with it.
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public void LinesNameEachStageAndOutcomeAndFit()
    {
        TablePreloadProgress P(TablePreloadStage stage) => new(stage, 120, 900, 3, 3_250_000, 94_371_840, 3_250_000, 300, 870, 12);
        TablePreloadResult R(TablePreloadOutcome o, TablePreloadStage stage = TablePreloadStage.Preparing, string? code = null) =>
            new(o, P(stage), [], null, code, null);
        string Progress(string locale, TablePreloadStage stage) => SessionPreloads.ProgressLine("1.2.3.45678", locale, P(stage)).Text;
        string Result(TablePreloadResult r) => SessionPreloads.ResultLine("1.2.3.45678", "enUS", r).Text;
        Assert.Equal("preload 1.2.3.45678: opening the build", Progress("enUS", TablePreloadStage.Opening));
        Assert.Equal("preload 1.2.3.45678: downloading 120/900 files, 3.1/90.0 MiB", Progress("enUS", TablePreloadStage.Downloading));
        Assert.Equal("preload 1.2.3.45678 deDE: preparing 300/870 tables", Progress("deDE", TablePreloadStage.Preparing));
        Assert.Equal("preload 1.2.3.45678: done, 870 tables (12 failed)", Result(R(TablePreloadOutcome.Completed)));
        Assert.Equal("preload 1.2.3.45678: incomplete, 12/870 tables, 3 files failed; P retries", Result(R(TablePreloadOutcome.Incomplete)));
        Assert.Equal("preload 1.2.3.45678: stopped at 300/870 tables; P resumes", Result(R(TablePreloadOutcome.Canceled)));
        Assert.Equal("preload 1.2.3.45678: failed (OBJECT_ACCESS_NEEDS_NETWORK); P retries",
            Result(R(TablePreloadOutcome.Failed, code: "OBJECT_ACCESS_NEEDS_NETWORK")));
        // A resume's ensure before it has counted anything: never "0 of 0 files".
        Assert.Equal("preload 1.2.3.45678: checking stored files",
            SessionPreloads.ProgressLine("1.2.3.45678", "enUS", new(TablePreloadStage.Downloading, 0, 0, 0, 0, 0, 0, 0, 1132, 0)).Text);
        // Stopped while opening or before layer 4 counted anything: not "0/0 files".
        Assert.Equal("preload 1.2.3.45678: stopped before downloading; P resumes",
            Result(new(TablePreloadOutcome.Canceled, new(TablePreloadStage.Opening, 0, 0, 0, 0, 0, 0, 0, 0, 0), [], null, null, null)));
        Assert.Equal("preload 1.2.3.45678: downloading 3/900 files, 900.0 KiB/90.0 MiB",
            SessionPreloads.ProgressLine("1.2.3.45678", "enUS", new(TablePreloadStage.Downloading, 3, 900, 0, 921_600, 94_371_840, 0, 0, 0, 0)).Text);

        // Retail's longest end fits 80 columns whole: nothing is cut (QA on #159 saw "P ret...").
        var retail = SessionPreloads.ResultLine("11.2.5.63906", "enUS",
            new(TablePreloadOutcome.Incomplete, new(TablePreloadStage.Preparing, 1132, 1132, 12, 0, 0, 0, 1132, 1132, 1132), [], null, null, null));
        Assert.Equal("preload 11.2.5.63906: incomplete, 1132/1132 tables, 12 files failed; P retries", retail.Fit(80));

        // Too long for the room: the version is cut from its front, then left out; the counts stay.
        var huge = SessionPreloads.ResultLine("11.22.33.444444", "zhCN",
            new(TablePreloadOutcome.Incomplete, new(TablePreloadStage.Preparing, long.MaxValue, long.MaxValue, long.MaxValue, 0, 0, 0, int.MaxValue, int.MaxValue, int.MaxValue), [], null, null, null));
        var body = "incomplete, 2147483647/2147483647 tables, 9223372036854775807 files failed; P retries";
        Assert.Equal($"preload zhCN: {body}", huge.Fit(80 + 20));
        Assert.Equal($"preload ...444444 zhCN: {body}", huge.Fit(huge.Shortest.Length + 10));
        Assert.Equal(huge.Text, huge.Fit(200));
        Fits(huge.Fit(80));
        var preparing = SessionPreloads.ProgressLine("1.15.9.70576", "enUS", new(TablePreloadStage.Preparing, 0, 0, 0, 0, 0, 0, 310, 1132, 0));
        Assert.Equal("preload ...70576: preparing 310/1132 tables", preparing.Fit(43));
        Assert.Equal("preload: preparing 310/1132 tables", preparing.Fit(35));
    }

    [Fact]
    public void TheStatusLinePutsARunningPreloadFirstAndCutsOlderText()
    {
        const string metadata = "metadata: stored, 159,749 files, 1,402 archives (us).";
        var running = SessionPreloads.ProgressLine(Version, "enUS", new(TablePreloadStage.Preparing, 0, 0, 0, 0, 0, 0, 310, 1132, 0));
        var line = StatusLine.Compose(80, metadata, false, running, []);
        Fits(line);
        Assert.StartsWith("preload 1.15.9.70576: preparing 310/1132 tables  |  metadata: stored", line, StringComparison.Ordinal);

        // A running task and a running preload: the preload keeps its counts and the task's line is cut.
        var ensure = "ensure: 120 of 500 files, 3.1 MiB of 9.0 MiB; x cancels";
        line = StatusLine.Compose(80, ensure, true, running, ["/dbfiles (3 rows)"]);
        Fits(line);
        Assert.StartsWith("preload 1.15.9.70576: preparing 310/1132 tables  |  ensure: ", line, StringComparison.Ordinal);

        // An ended preload comes before the task line that started it; at 200 columns every part shows whole.
        var ended = SessionPreloads.ResultLine("11.2.5.63906", "enUS",
            new(TablePreloadOutcome.Incomplete, new(TablePreloadStage.Preparing, 1132, 1132, 12, 0, 0, 0, 1132, 1132, 1132), [], null, null, null));
        line = StatusLine.Compose(200, metadata, false, ended, []);
        Assert.Equal($"{ended.Text}  |  {metadata}  |  {StatusLine.KeysHint}", line);
        Assert.Equal($"? keys  q quit", StatusLine.Compose(80, "", false, null, []));
    }

    [Fact]
    public async Task At80x24TheFilesViewShowsTheRunningPreloadsStageAndCountsAfterM()
    {
        // QA on #159: the metadata line took the width and "preload 1.15.9.70576:" was cut before its stage and counts.
        tables.Reports =
        [
            new(TablePreloadStage.Downloading, 0, 0, 0, 0, 0, 0, 0, 1132, 0),
            new(TablePreloadStage.Downloading, 968, 968, 0, 8_283_750, 8_283_750, 8_283_750, 0, 1132, 0),
            new(TablePreloadStage.Preparing, 968, 968, 0, 8_283_750, 8_283_750, 8_283_750, 310, 1132, 0),
        ];
        const string preparing = "preload 1.15.9.70576: preparing 310/1132 tables";
        static bool Idle(BrowseShell sh) => !sh.StatusText.Contains("loading...");
        BrowseShellSmokeTests.Step[] steps =
        [
            new("files", (s, sh) => Idle(sh) && BrowseShellSmokeTests.Line(s, 0).Contains("files"), a => a.InjectKey(new Terminal.Gui.Input.Key('m'))),
            new("preparing", (s, sh) => Idle(sh) && BrowseShellSmokeTests.Line(s, 23).Contains("preparing 310/1132 tables")
                && sh.StatusText.Contains("metadata: stored"), a => a.InjectKey(new Terminal.Gui.Input.Key('P'))),
            new("stopped", (s, _) => BrowseShellSmokeTests.Line(s, 23).Contains("stopped at 310/1132 tables; P resumes"),
                a => a.InjectKey(new Terminal.Gui.Input.Key('q'))),
        ];
        var wide = new List<string>();
        steps = [.. steps.Select(st => st with
        {
            Ready = (s, sh) =>
            {
                if (!st.Ready(s, sh)) return false;
                wide.AddRange(sh.ShownLines.Where(l => l.Length > 80).Select(l => $"{st.Name}: {l}"));
                return true;
            },
        })];
        var registry = new ViewRegistry().Add(new("files", "", () => new FilesView(new(Build, "enUS"), Version)));
        var (shots, failure, stopped, shown) = BrowseShellSmokeTests.Drive(session, registry, "files", steps);
        await shown.DisposeAsync().AsTask().WaitAsync(Guard);

        Assert.True(failure is null, failure);
        Assert.True(stopped);
        Assert.Empty(wide);
        var status = BrowseShellSmokeTests.Line(shots["preparing"], 23);
        Assert.StartsWith($"{preparing}  |  metadata: stored, 159,749", status, StringComparison.Ordinal);
        Assert.Equal(80, status.Length);
        Assert.StartsWith("preload 1.15.9.70576: stopped at 310/1132 tables; P resumes", BrowseShellSmokeTests.Line(shots["stopped"], 23),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Opens <see cref="Build"/>'s tables; a handle's preload reports Downloading 120 of 900 files, then waits for
    /// <see cref="Release"/> (ending Completed with 870 tables) or its cancellation (ending Canceled).
    /// </summary>
    internal sealed class ScriptedTables : ITables
    {
        private readonly Lock gate = new();
        private readonly List<(BuildRef, string)> opened = [];
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int canceled, disposed;

        public IReadOnlyList<(BuildRef, string)> Opened { get { lock (gate) return [.. opened]; } }
        public int Canceled => Volatile.Read(ref canceled);
        public int Disposed => Volatile.Read(ref disposed);
        public Action? OnCanceled { get; set; }
        /// <summary>What a preload reports before it waits; the last one is its end when stopped. Null: Downloading 120 of 900.</summary>
        public TablePreloadProgress[]? Reports { get; set; }
        public Exception? CanceledFailure { get; private set; }

        public void Release() => release.TrySetResult();

        public Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            lock (gate) opened.Add((build, locale));
            return Task.FromResult(new TablesOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale), null, null, null));
        }

        private sealed class Handle(ScriptedTables owner, BuildRef build, string locale) : IBuildTables
        {
            public BuildRef Build => build;
            public string Locale => locale;
            public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.disposed); return ValueTask.CompletedTask; }

            public async Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken)
            {
                var downloading = new TablePreloadProgress(TablePreloadStage.Downloading, 120, 900, 0, 3_250_000, 94_371_840, 3_250_000, 0, 0, 0);
                foreach (var report in owner.Reports ?? [downloading])
                {
                    progress?.Report(report);
                    downloading = report;
                }
                try { await owner.release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref owner.canceled);
                    try { owner.OnCanceled?.Invoke(); }
                    catch (Exception e) { owner.CanceledFailure = e; }
                    return new(TablePreloadOutcome.Canceled, downloading, [], null, null, null);
                }
                var done = new TablePreloadProgress(TablePreloadStage.Preparing, 900, 900, 0, 94_371_840, 94_371_840, 94_371_840, 870, 870, 0);
                progress?.Report(done);
                return new(TablePreloadOutcome.Completed, done, [], null, null, null);
            }

            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
