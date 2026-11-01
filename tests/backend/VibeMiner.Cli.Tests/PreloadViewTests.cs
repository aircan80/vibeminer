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
        builds.Add(Build, new BuildsFakeBuild(new BuildMetadata(Version, default, default, default, 3, 2, ["enUS"]), []));
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

    private const string Downloading = "preload 1.15.9.70576: downloading 120 of 900 files, 3.1 MiB of 90.0 MiB";

    [Fact]
    public async Task MOpeningTheBuildStartsThePreloadOverTheWriterAndItRunsToTheEnd()
    {
        await FilesAsync();
        Assert.Equal("", nav.PreloadStatus);
        await MetadataAsync();
        Assert.Equal("metadata: stored, 2 files, 3 archives (us).", nav.Tasks.Status);
        await Until(() => nav.PreloadStatus == Downloading);
        Fits(nav.PreloadStatus);
        Assert.Equal([(Build, "enUS")], tables.Opened);
        Assert.Equal(1, composed);
        Assert.True(session.Tables!.Writer);
        // The composition keeps its writer lease; the metadata task's went at its end. Browsing goes on meanwhile.
        Assert.Null(nav.Tasks.Running);
        Assert.Equal(["5"], nav.Current.Visible.Select(r => r.Cells[0]));

        tables.Release();
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: completed, 870 tables");
    }

    [Fact]
    public async Task PStopsThePreloadAndPAgainResumesIt()
    {
        await FilesAsync();
        await MetadataAsync();
        await Until(() => nav.PreloadStatus == Downloading);
        Assert.Contains(nav.Help().Split('\n'), l => l.StartsWith("  P        preload: stop or resume", StringComparison.Ordinal));

        Assert.True(nav.TryAction('P'));
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: stopped at 120 of 900 files; P resumes");
        await session.Preloads.Pending.WaitAsync(Guard);
        Assert.Equal(1, tables.Canceled);

        Assert.True(nav.TryAction('P'));
        await Until(() => nav.PreloadStatus == Downloading);
        Assert.Equal(2, tables.Opened.Count);
        tables.Release();
        await Until(() => nav.PreloadStatus == "preload 1.15.9.70576: completed, 870 tables");
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
            await Until(() => buildNav.PreloadStatus.EndsWith("stopped at 120 of 900 files; P resumes", StringComparison.Ordinal));
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
        Assert.Equal("preload 1.2.3.45678: opening the build", SessionPreloads.ProgressLine("1.2.3.45678", "enUS", P(TablePreloadStage.Opening)));
        Assert.Equal("preload 1.2.3.45678: downloading 120 of 900 files, 3.1 MiB of 90.0 MiB",
            SessionPreloads.ProgressLine("1.2.3.45678", "enUS", P(TablePreloadStage.Downloading)));
        Assert.Equal("preload 1.2.3.45678 deDE: preparing 300 of 870 tables", SessionPreloads.ProgressLine("1.2.3.45678", "deDE", P(TablePreloadStage.Preparing)));
        Assert.Equal("preload 1.2.3.45678: completed, 870 tables (12 failed)", SessionPreloads.ResultLine("1.2.3.45678", "enUS", R(TablePreloadOutcome.Completed)));
        Assert.Equal("preload 1.2.3.45678: incomplete, 12 of 870 tables and 3 files failed; P retries",
            SessionPreloads.ResultLine("1.2.3.45678", "enUS", R(TablePreloadOutcome.Incomplete)));
        Assert.Equal("preload 1.2.3.45678: stopped at 300 of 870 tables; P resumes", SessionPreloads.ResultLine("1.2.3.45678", "enUS", R(TablePreloadOutcome.Canceled)));
        Assert.Equal("preload 1.2.3.45678: failed (OBJECT_ACCESS_NEEDS_NETWORK); P retries",
            SessionPreloads.ResultLine("1.2.3.45678", "enUS", R(TablePreloadOutcome.Failed, code: "OBJECT_ACCESS_NEEDS_NETWORK")));
        var longest = SessionPreloads.ResultLine("11.22.33.444444 zhCN", "zhCN",
            new(TablePreloadOutcome.Incomplete, new(TablePreloadStage.Preparing, long.MaxValue, long.MaxValue, long.MaxValue, 0, 0, 0, int.MaxValue, int.MaxValue, int.MaxValue), [], null, null, null));
        Fits(longest);
        Assert.EndsWith("...", longest);
    }

    /// <summary>
    /// Opens <see cref="Build"/>'s tables; a handle's preload reports Downloading 120 of 900 files, then waits for
    /// <see cref="Release"/> (ending Completed with 870 tables) or its cancellation (ending Canceled).
    /// </summary>
    private sealed class ScriptedTables : ITables
    {
        private readonly Lock gate = new();
        private readonly List<(BuildRef, string)> opened = [];
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int canceled, disposed;

        public IReadOnlyList<(BuildRef, string)> Opened { get { lock (gate) return [.. opened]; } }
        public int Canceled => Volatile.Read(ref canceled);
        public int Disposed => Volatile.Read(ref disposed);
        public Action? OnCanceled { get; set; }
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
                progress?.Report(downloading);
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
