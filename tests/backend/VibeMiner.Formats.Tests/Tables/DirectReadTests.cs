using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using VibeMiner.Generated;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// Direct reads (D330) in the real <see cref="TablesService"/>: while a table isn't in the generated store, an Interactive unfiltered
/// page, row by ID or schema reads the DB2 file and starts the build detached in a Background slot. Builds are held at gates in the
/// generated store; the timeouts only stop a deadlocked test. Synthetic data only.
/// </summary>
public sealed class DirectReadTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const TablePriority Later = TablePriority.Background;
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];

    private sealed record World(SyntheticWorld Synthetic, IBuildTables Tables, Gates Gates, TableWorkScheduler Scheduler, DisposeWatch Watch)
    {
        public async Task<IBuildTables> HandleAsync() => (await Synthetic.Service.ForAsync(Build, "enUS", Ct)).Tables!;
    }

    /// <summary>Tables 1 to <paramref name="count"/>, each with its own rows (so its own content), stored and not built yet.</summary>
    private static async Task<World> OpenAsync(int count = 1, int maxBackground = 2, int maxQueued = TableWorkScheduler.DefaultMaxQueued)
    {
        var scheduler = new TableWorkScheduler(maxBackground: maxBackground, maxQueued: maxQueued);
        var tables = Enumerable.Range(1, count).Select(i => (uint)i).Select(id => new SyntheticTable(id, $"T{id}", SyntheticTables.Dbd(Plain),
            new FakeFile(id, Content: SyntheticTables.Wdc5(Plain, [new(id, [$"{id}", $"{id * 7}"])], null).Bytes, Storage: FileStorage.Stored))).ToList();
        DisposeWatch? watch = null;
        var world = await SyntheticTables.CreateAsync(Build, tables, options: o => o with { Scheduler = scheduler }, wrap: inner => watch = new DisposeWatch(inner));
        var handle = (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;
        var gates = new Gates();
        await foreach (var entry in handle.ListAsync(TableListFilter.All, Now, Ct)) gates.Ids[entry.ContentKey!] = entry.FileDataId;
        world.Generated.Decoding = gates.DecodingAsync;
        world.Generated.Building = gates.Building;
        return new(world, handle, gates, scheduler, watch!);
    }

    private static Task Idle(TableWorkScheduler scheduler) =>
        scheduler.WhenAsync(() => scheduler.Running(Now) == 0 && scheduler.Running(Later) == 0 && scheduler.Queued(Now) == 0 && scheduler.Queued(Later) == 0)
            .WaitAsync(Guard);

    private sealed class Gates
    {
        public ConcurrentDictionary<string, uint> Ids { get; } = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource> held = new(), started = new(), asked = new();
        private readonly ConcurrentDictionary<uint, int> decodes = new(), asks = new();

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds table <paramref name="id"/>'s decode until <see cref="Release"/>.</summary>
        public void Hold(uint id) => held[id] = Signal();
        public void Release(uint id) => held[id].TrySetResult();
        /// <summary>Once table <paramref name="id"/>'s decode started <paramref name="times"/> times.</summary>
        public Task Started(uint id, int times = 1) => started.GetOrAdd(id * 1000 + (uint)times, _ => Signal()).Task;
        /// <summary>Once table <paramref name="id"/> was asked to be built <paramref name="times"/> times.</summary>
        public Task Asked(uint id, int times) => asked.GetOrAdd(id * 1000 + (uint)times, _ => Signal()).Task;
        public int Decodes(uint id) => decodes.GetValueOrDefault(id);
        public int Asks(uint id) => asks.GetValueOrDefault(id);

        public Task DecodingAsync(GeneratedTableSource source)
        {
            var id = Ids[source.ContentKey];
            var n = decodes.AddOrUpdate(id, 1, (_, k) => k + 1);
            started.GetOrAdd(id * 1000 + (uint)n, _ => Signal()).TrySetResult();
            return held.TryGetValue(id, out var gate) ? gate.Task : Task.CompletedTask;
        }

        public void Building(GeneratedTableSource source)
        {
            var id = Ids[source.ContentKey];
            var n = asks.AddOrUpdate(id, 1, (_, k) => k + 1);
            asked.GetOrAdd(id * 1000 + (uint)n, _ => Signal()).TrySetResult();
        }
    }

    /// <summary>Layer 4 with each build handle's dispose signalled; <see cref="OnClose"/> runs as one closes, before it does.</summary>
    private sealed class DisposeWatch(IFileSystem inner) : IFileSystem
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> closed = new();
        private int closes;
        public IFileNames Names => inner.Names;
        public Action? OnClose { get; set; }
        public int Closes => Volatile.Read(ref closes);
        /// <summary>Once <paramref name="times"/> build handles closed.</summary>
        public Task Closed(int times) => closed.GetOrAdd(times, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var opened = await inner.ForAsync(build, locale, cancellationToken);
            return opened.Files is { } files ? opened with { Files = new Handle(this, files) } : opened;
        }

        private sealed class Handle(DisposeWatch owner, IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;

            public async ValueTask DisposeAsync()
            {
                owner.OnClose?.Invoke();
                await inner.DisposeAsync();
                var n = Interlocked.Increment(ref owner.closes);
                owner.closed.GetOrAdd(n, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }

            public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                await foreach (var file in inner.ListAsync(filter, cancellationToken)) yield return file;
            }

            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
                inner.EnsureAsync(selection, priority, progress, cancellationToken);
            public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
            public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) =>
                inner.ReadRangeAsync(file, offset, length, cancellationToken);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
        }
    }

    // 1. A first page answers at once; the build runs in the background, then the store serves.

    [Fact]
    public async Task AFirstPageAnswersWhileItsBuildIsHeldAndTheNextPageComesFromTheStore()
    {
        var w = await OpenAsync();
        await using var _ = w.Tables;
        w.Gates.Hold(1);

        var page = await w.Tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (page.State, page.Table.State));
        Assert.Equal(["1", "7"], page.Rows.Single().Row.Values);

        // The build took a Background slot; the page's Interactive slot is free again.
        await w.Gates.Started(1).WaitAsync(Guard);
        await w.Scheduler.WhenAsync(() => w.Scheduler.Running(Now) == 0).WaitAsync(Guard);
        Assert.Equal((1, 0), (w.Scheduler.Running(Later), w.Scheduler.Running(Now)));
        Assert.Equal(1, w.Gates.Decodes(1));
        Assert.Equal(1, w.Gates.Asks(1));

        w.Gates.Release(1);
        await Idle(w.Scheduler);
        Assert.True(w.Synthetic.Store.Count > 0);

        // Served from the store: no byte of the DB2 file read, no decode, the same identity the direct page gave.
        var reads = w.Synthetic.Counted.Reads;
        var stored = await w.Tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (stored.State, stored.Table.State));
        Assert.Equal(["1", "7"], stored.Rows.Single().Row.Values);
        Assert.Equal(page.Identity, stored.Identity);
        Assert.Equal(reads, w.Synthetic.Counted.Reads);
        Assert.Equal(1, w.Gates.Decodes(1));
    }

    // 2. One build however many direct calls, across handles of one service.

    [Fact]
    public async Task ManyDirectCallsOnTwoHandlesStartOneBuildAndAnInteractiveCheckJoinsIt()
    {
        var w = await OpenAsync();
        await using var _ = w.Tables;
        await using var second = await w.HandleAsync();
        w.Gates.Hold(1);

        var pages = new List<Task<TablePage>>();
        var rows = new List<Task<TableRowResult>>();
        var schemas = new List<Task<TableSchemaResult>>();
        foreach (var tables in new[] { w.Tables, second })
            for (var i = 0; i < 4; i++)
            {
                pages.Add(tables.PageAsync(new(1), Now, Ct));
                rows.Add(tables.GetAsync(1, 1, Now, Ct));
                schemas.Add(tables.SchemaAsync(1, Now, Ct));
            }

        // Every call answers while the one build is held.
        foreach (var page in await Task.WhenAll(pages).WaitAsync(Guard))
            Assert.Equal((TablePageState.Ok, TableState.Ready, 1), (page.State, page.Table.State, page.Rows.Count));
        foreach (var row in await Task.WhenAll(rows).WaitAsync(Guard))
            Assert.Equal((TableRowOutcome.Found, TableState.Ready), (row.Outcome, row.Table.State));
        foreach (var schema in await Task.WhenAll(schemas).WaitAsync(Guard))
        {
            Assert.Equal(TableState.Ready, schema.Table.State);
            Assert.NotNull(schema.Columns);
            Assert.NotNull(schema.Schema);
        }
        await w.Gates.Started(1).WaitAsync(Guard);
        Assert.Equal(1, w.Gates.Decodes(1));
        Assert.Equal(1, w.Gates.Asks(1));
        Assert.Equal(1, w.Scheduler.Running(Later));

        // A check doesn't read directly: it joins the running build.
        var check = second.CheckAsync(1, Now, Ct);
        await w.Gates.Asked(1, 2).WaitAsync(Guard);
        Assert.False(check.IsCompleted);
        w.Gates.Release(1);
        Assert.Equal(TableState.Ready, (await check.WaitAsync(Guard)).State);
        await Idle(w.Scheduler);
        Assert.Equal(1, w.Gates.Decodes(1));
        Assert.Equal(2, w.Gates.Asks(1));
    }

    // 3. The build outlives the handle that started it.

    [Fact]
    public async Task AHandleDisposedAfterADirectPageClosesItsFilesOnlyOnceTheBuildIsStored()
    {
        var w = await OpenAsync();
        w.Gates.Hold(1);
        var page = await w.Tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal(TablePageState.Ok, page.State);
        await w.Tables.DisposeAsync();
        await w.Gates.Started(1).WaitAsync(Guard);
        Assert.Equal(0, w.Watch.Closes);

        var storedAtClose = -1;
        w.Watch.OnClose = () => storedAtClose = w.Synthetic.Store.Count;
        w.Gates.Release(1);
        await w.Watch.Closed(1).WaitAsync(Guard);
        Assert.True(storedAtClose > 0, "The layer 4 handle closed before the table was stored.");

        // Another handle of the service finds it stored.
        await using var next = await w.HandleAsync();
        var reads = w.Synthetic.Counted.Reads;
        var again = await next.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, page.Identity), (again.State, again.Identity));
        Assert.Equal(reads, w.Synthetic.Counted.Reads);
        Assert.Equal(1, w.Gates.Decodes(1));
    }

    // 4. A full Background lane: the build queues behind earlier work, or isn't started at all; the page never waits.

    [Fact]
    public async Task ADirectReadQueuesItsBuildBehindQueuedBackgroundWorkWithoutWaiting()
    {
        var w = await OpenAsync(3, maxBackground: 1);
        await using var _ = w.Tables;
        w.Gates.Hold(1); w.Gates.Hold(2);
        var b1 = w.Tables.CheckAsync(1, Later, Ct);
        await w.Gates.Started(1).WaitAsync(Guard);
        var b2 = w.Tables.CheckAsync(2, Later, Ct);
        await w.Scheduler.WhenAsync(() => w.Scheduler.Queued(Later) == 1).WaitAsync(Guard);

        var page = await w.Tables.PageAsync(new(3), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (page.State, page.Table.State));
        Assert.Equal(["3", "21"], page.Rows.Single().Row.Values);
        await w.Scheduler.WhenAsync(() => w.Scheduler.Queued(Later) == 2).WaitAsync(Guard);
        Assert.False(b1.IsCompleted || b2.IsCompleted);

        // First come, first served: table 2's build, then table 3's.
        w.Gates.Release(1);
        Assert.Equal(TableState.Ready, (await b1.WaitAsync(Guard)).State);
        await w.Gates.Started(2).WaitAsync(Guard);
        Assert.Equal(0, w.Gates.Decodes(3));
        w.Gates.Release(2);
        Assert.Equal(TableState.Ready, (await b2.WaitAsync(Guard)).State);
        await w.Gates.Started(3).WaitAsync(Guard);
        await Idle(w.Scheduler);
        Assert.Equal(1, w.Gates.Decodes(3));
    }

    [Fact]
    public async Task ADirectReadAnswersWhenTheBackgroundQueueIsFullAndALaterOneStartsTheBuild()
    {
        var w = await OpenAsync(2, maxBackground: 1, maxQueued: 0);
        await using var _ = w.Tables;
        w.Gates.Hold(1);
        var b1 = w.Tables.CheckAsync(1, Later, Ct);
        await w.Gates.Started(1).WaitAsync(Guard);
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Scheduler.Entering = priority =>
        {
            if (priority == Later) refused.TrySetResult();
            return Task.CompletedTask;
        };

        var page = await w.Tables.PageAsync(new(2), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (page.State, page.Table.State));
        await refused.Task.WaitAsync(Guard);
        w.Scheduler.Entering = null;
        w.Gates.Release(1);
        Assert.Equal(TableState.Ready, (await b1.WaitAsync(Guard)).State);
        await Idle(w.Scheduler);
        Assert.Equal(0, w.Gates.Decodes(2));

        // The refused build has ended once the handle's files close. A queue that was full is no failure: the next page still
        // reads directly and starts the build.
        await w.Tables.DisposeAsync();
        await w.Watch.Closed(1).WaitAsync(Guard);
        await using var tables = await w.HandleAsync();
        w.Gates.Hold(2);
        var next = await tables.PageAsync(new(2), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (next.State, next.Table.State));
        await w.Gates.Started(2).WaitAsync(Guard);
        Assert.Equal(1, w.Gates.Asks(2));
        w.Gates.Release(2);
        await Idle(w.Scheduler);
        Assert.Equal(1, w.Gates.Decodes(2));
    }

    // 5. A failed background build sends later calls to the generated path; only Interactive unfiltered calls read directly.

    [Fact]
    public async Task AfterABackgroundBuildFailsCallsAnswerTheGeneratedPathsFailureUntilABuildSucceeds()
    {
        var w = await OpenAsync();
        w.Synthetic.Generated.AfterDecode = () => Task.FromException(new GeneratedStoreException("TEST_STORE_FAILED", "Injected store failure."));
        var direct = await w.Tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (direct.State, direct.Table.State));
        // The detached build ends (and is remembered as failed) before the handle's files close.
        await w.Tables.DisposeAsync();
        await w.Watch.Closed(1).WaitAsync(Guard);
        Assert.Equal(1, w.Gates.Asks(1));

        await using var tables = await w.HandleAsync();
        var page = await tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.NotReadable, TableState.StoreFailed, "TEST_STORE_FAILED"), (page.State, page.Table.State, page.Table.Reason?.Code));
        Assert.Empty(page.Rows);
        var check = await tables.CheckAsync(1, Now, Ct).WaitAsync(Guard);
        Assert.Equal((check.State, check.Reason?.Code), (page.Table.State, page.Table.Reason?.Code));
        var row = await tables.GetAsync(1, 1, Now, Ct).WaitAsync(Guard);
        Assert.Equal((TableRowOutcome.Unknown, TableState.StoreFailed), (row.Outcome, row.Table.State));
        var schema = await tables.SchemaAsync(1, Now, Ct).WaitAsync(Guard);
        Assert.Equal(TableState.StoreFailed, schema.Table.State);
        Assert.Null(schema.Columns);
        // Each of the four waited for a build of its own: none read directly or started a background one.
        Assert.Equal(5, w.Gates.Asks(1));
        Assert.Equal(5, w.Gates.Decodes(1));

        // A build that succeeds forgets the failure: with the table gone from the store again, a page reads directly once more.
        w.Synthetic.Generated.AfterDecode = null;
        var built = await tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (built.State, built.Table.State));
        Assert.Equal(6, w.Gates.Asks(1));
        w.Synthetic.Store.Clear();
        w.Gates.Hold(1);
        var again = await tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, TableState.Ready), (again.State, again.Table.State));
        await w.Gates.Started(1, 7).WaitAsync(Guard);
        w.Gates.Release(1);
        await Idle(w.Scheduler);
        Assert.Equal(7, w.Gates.Asks(1));
    }

    [Fact]
    public async Task FilteredPagesAndBackgroundCallsWaitForTheBuildInsteadOfReadingDirectly()
    {
        // Each Background call joins the build in a Background slot of its own.
        var w = await OpenAsync(maxBackground: 3);
        await using var _ = w.Tables;
        w.Gates.Hold(1);

        var filtered = w.Tables.PageAsync(new(1, Filters: [new("Value", TableFilterOp.Equals, "7")]), Now, Ct);
        await w.Gates.Started(1).WaitAsync(Guard);
        var page = w.Tables.PageAsync(new(1), Later, Ct);
        var row = w.Tables.GetAsync(1, 1, Later, Ct);
        var schema = w.Tables.SchemaAsync(1, Later, Ct);
        await w.Gates.Asked(1, 4).WaitAsync(Guard);
        Assert.False(filtered.IsCompleted || page.IsCompleted || row.IsCompleted || schema.IsCompleted);

        w.Gates.Release(1);
        Assert.Equal((TablePageState.Ok, 1), ((await filtered.WaitAsync(Guard)).State, (await filtered).Rows.Count));
        Assert.Equal(TablePageState.Ok, (await page.WaitAsync(Guard)).State);
        Assert.Equal(TableRowOutcome.Found, (await row.WaitAsync(Guard)).Outcome);
        Assert.NotNull((await schema.WaitAsync(Guard)).Columns);
        await Idle(w.Scheduler);
        // One decode, and no background build asked for it.
        Assert.Equal(1, w.Gates.Decodes(1));
        Assert.Equal(4, w.Gates.Asks(1));
    }

    // 6. The direct caller's cancellation isn't the build's.

    [Fact]
    public async Task CancelingTheDirectCallerDoesNotCancelItsQueuedOrRunningBuild()
    {
        var w = await OpenAsync(2, maxBackground: 1);
        await using var _ = w.Tables;
        w.Gates.Hold(1); w.Gates.Hold(2);
        var b2 = w.Tables.CheckAsync(2, Later, Ct);
        await w.Gates.Started(2).WaitAsync(Guard);

        using var cancel = new CancellationTokenSource();
        var page = await w.Tables.PageAsync(new(1), Now, cancel.Token).WaitAsync(Guard);
        Assert.Equal(TablePageState.Ok, page.State);
        // Canceled while its build waits for a Background slot: it stays queued.
        await w.Scheduler.WhenAsync(() => w.Scheduler.Queued(Later) == 1).WaitAsync(Guard);
        await cancel.CancelAsync();
        Assert.Equal(1, w.Scheduler.Queued(Later));

        w.Gates.Release(2);
        await b2.WaitAsync(Guard);
        await w.Gates.Started(1).WaitAsync(Guard);
        w.Gates.Release(1);
        await Idle(w.Scheduler);

        var reads = w.Synthetic.Counted.Reads;
        var stored = await w.Tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal((TablePageState.Ok, page.Identity), (stored.State, stored.Identity));
        Assert.Equal(reads, w.Synthetic.Counted.Reads);
        Assert.Equal(1, w.Gates.Decodes(1));
    }
}
