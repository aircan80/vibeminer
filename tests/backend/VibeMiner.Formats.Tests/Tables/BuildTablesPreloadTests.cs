using System.Collections.Concurrent;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// What only the real preload does (D360): one Background ensure over layer 4, Background checks in scheduler slots, and an
/// Interactive call that never waits behind either. Gates are test-owned; the timeouts only stop a deadlocked test. Synthetic data only.
/// </summary>
public sealed class BuildTablesPreloadTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const TablePriority Now = TablePriority.Interactive;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];

    private static byte[] Bytes(uint id) => SyntheticTables.Wdc5(Plain, [new(1, ["1", $"{id}"]), new(2, ["2", $"{id * 7}"])], null).Bytes;

    private static SyntheticTable Table(uint id, FileStorage storage = FileStorage.Stored, FakeReadFailure? failure = null) =>
        new(id, $"T{id}", SyntheticTables.Dbd(Plain), new FakeFile(id, Content: Bytes(id), Storage: storage, ReadFailure: failure));

    private static async Task<IBuildTables> OpenAsync(SyntheticWorld world) => (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<Dictionary<string, uint>> IdsByContentAsync(IBuildTables tables)
    {
        var ids = new Dictionary<string, uint>();
        await foreach (var entry in tables.ListAsync(TableListFilter.All, Now, Ct)) if (entry.ContentKey is { } key) ids[key] = entry.FileDataId;
        return ids;
    }

    private sealed class Reports : IProgress<TablePreloadProgress>
    {
        private readonly List<TablePreloadProgress> seen = [];
        public void Report(TablePreloadProgress value) { lock (seen) seen.Add(value); }
        public TablePreloadProgress[] All { get { lock (seen) return [.. seen]; } }
    }

    [Fact]
    public async Task EnsuresEveryListedFileInOneBackgroundCall()
    {
        var world = await SyntheticTables.CreateAsync(Build,
        [
            Table(1), Table(2, FileStorage.NotStored),
            // A DB2 the manifest doesn't name: its file is ensured, nothing reads it.
            new(3, null, null, new FakeFile(3, Content: Bytes(3), Storage: FileStorage.NotStored)),
            new(4, "T4", SyntheticTables.Dbd(Plain), null),
            new(5, "T5", SyntheticTables.Dbd(Plain), new FakeFile(5, Content: Bytes(5), Locales: ["deDE"])),
        ]);
        await using var tables = await OpenAsync(world);
        var result = await tables.PreloadAsync(null, Ct);

        Assert.Equal(TablePreloadOutcome.Completed, result.Outcome);
        var (selection, priority) = Assert.Single(world.Counted.Ensures);
        Assert.Equal((EnsureSelectionKind.Many, EnsurePriority.Background), (selection.Kind, priority));
        Assert.Equal([1u, 2, 3], selection.Files!.Select(file => file.FileDataId!.Value));
        Assert.Equal([2u, 3], world.Files.EnsureFetches);
        Assert.All(world.Files.EnsureRequests, request => Assert.Equal(EnsurePriority.Background, request.Priority));
        Assert.Equal((3L, 3L, 0L, 2, 2, 0), (result.Final.FilesDone, result.Final.FilesTotal, result.Final.FilesFailed,
            result.Final.TablesDone, result.Final.TablesTotal, result.Final.TablesFailed));
        Assert.Equal(2, world.Generated.Decodes);
    }

    [Fact]
    public async Task ASecondPreloadFetchesAndPreparesNothing()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(1), Table(2, FileStorage.NotStored), Table(3, FileStorage.NotStored)]);
        await using (var first = await OpenAsync(world))
            Assert.Equal(TablePreloadOutcome.Completed, (await first.PreloadAsync(null, Ct)).Outcome);
        var (fetches, decodes) = (world.Files.EnsureFetches.Count, world.Generated.Decodes);

        // A new handle, as a new preload job would open: the store and the generated store say what is done.
        await using var tables = await OpenAsync(world);
        var again = await tables.PreloadAsync(null, Ct);
        Assert.Equal(TablePreloadOutcome.Completed, again.Outcome);
        Assert.Equal((3, 3), (again.Final.TablesDone, again.Final.TablesTotal));
        Assert.Equal((fetches, decodes), (world.Files.EnsureFetches.Count, world.Generated.Decodes));
    }

    [Fact]
    public async Task CanceledMidDownloadKeepsWhatIsStoredAndResumes()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(1, FileStorage.NotStored), Table(2, FileStorage.NotStored), Table(3, FileStorage.NotStored)]);
        var held = Signal();
        world.Files.BeforeFetch = async (id, _, token) =>
        {
            if (id != 2) return;
            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await using var tables = await OpenAsync(world);
        using var stop = new CancellationTokenSource();
        var reports = new Reports();
        var preload = tables.PreloadAsync(reports, stop.Token);
        await held.Task.WaitAsync(Guard);
        await stop.CancelAsync();
        var canceled = await preload.WaitAsync(Guard);

        Assert.Equal(TablePreloadOutcome.Canceled, canceled.Outcome);
        Assert.Equal(TablePreloadStage.Downloading, canceled.Final.Stage);
        Assert.Equal(canceled.Final, reports.All[^1]);
        Assert.Equal(0L, canceled.Final.FilesFailed);
        Assert.Equal(0, world.Generated.Decodes);
        Assert.Equal([1u], world.Files.EnsureFetches);

        world.Files.BeforeFetch = null;
        var resumed = await tables.PreloadAsync(null, Ct);
        Assert.Equal(TablePreloadOutcome.Completed, resumed.Outcome);
        Assert.Equal([1u, 2, 3], world.Files.EnsureFetches);
        Assert.Equal(3, world.Generated.Decodes);
    }

    [Fact]
    public async Task AnInteractivePageAnswersWhileThePreloadsEnsureIsHeld()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(1, FileStorage.NotStored)]);
        var held = Signal();
        var gate = Signal();
        world.Files.BeforeFetch = async (_, _, token) =>
        {
            held.TrySetResult();
            await gate.Task.WaitAsync(token);
        };
        await using var tables = await OpenAsync(world);
        var preload = tables.PreloadAsync(null, Ct);
        await held.Task.WaitAsync(Guard);

        var page = await tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal(TablePageState.Ok, page.State);
        Assert.Equal([1u, 2], page.Rows.Select(row => row.Row.Id));
        Assert.False(preload.IsCompleted);

        gate.TrySetResult();
        var result = await preload.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task AnInteractiveCheckIsNotBlockedBehindPreparing()
    {
        var scheduler = new TableWorkScheduler();
        var world = await SyntheticTables.CreateAsync(Build, [Table(1), Table(2), Table(3)], options: o => o with { Scheduler = scheduler });
        await using var tables = await OpenAsync(world);
        var ids = await IdsByContentAsync(tables);
        var gate = Signal();
        var started = new ConcurrentDictionary<uint, TaskCompletionSource>();
        TaskCompletionSource Started(uint id) => started.GetOrAdd(id, _ => Signal());
        world.Generated.Decoding = source =>
        {
            var id = ids[source.ContentKey];
            Started(id).TrySetResult();
            return id is 1 or 2 ? gate.Task : Task.CompletedTask;
        };

        var preload = tables.PreloadAsync(null, Ct);
        await Task.WhenAll(Started(1).Task, Started(2).Task).WaitAsync(Guard);
        Assert.Equal(BuildTables.PreloadChecks, scheduler.Running(TablePriority.Background));

        var entry = await tables.CheckAsync(3, Now, Ct).WaitAsync(Guard);
        Assert.Equal(TableState.Ready, entry.State);
        Assert.False(preload.IsCompleted);

        gate.TrySetResult();
        var result = await preload.WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Completed, result.Outcome);
        // Table 3 was built once, by the Interactive check; the preload found it in the generated store.
        Assert.Equal(3, world.Generated.Decodes);
    }

    [Fact]
    public async Task AFullBackgroundQueueWaitsInsteadOfFailing()
    {
        // One Background slot and no queue: the second worker is refused (TABLES_BUSY) until the first frees its slot.
        var scheduler = new TableWorkScheduler(maxBackground: 1, maxQueued: 0);
        var world = await SyntheticTables.CreateAsync(Build, [Table(1), Table(2), Table(3), Table(4)], options: o => o with { Scheduler = scheduler });
        await using var tables = await OpenAsync(world);
        var result = await tables.PreloadAsync(null, Ct).WaitAsync(Guard);
        Assert.Equal(TablePreloadOutcome.Completed, result.Outcome);
        Assert.Equal((4, 4, 0), (result.Final.TablesDone, result.Final.TablesTotal, result.Final.TablesFailed));
    }

    [Fact]
    public async Task FailedTablesLandInTheResult()
    {
        var world = await SyntheticTables.CreateAsync(Build,
        [
            Table(1),
            // Stored by the ensure (nothing is decoded there), corrupt once read.
            Table(2, FileStorage.NotStored, new(FileReadState.Corrupt, "FILES_CORRUPT")),
            // Never stored: not opened, it takes the ensure's state.
            Table(3, FileStorage.NotStored, new(FileReadState.Missing, "OBJECT_ACCESS_MISSING")),
        ]);
        await using var tables = await OpenAsync(world);
        var reports = new Reports();
        var result = await tables.PreloadAsync(reports, Ct);

        Assert.Equal(TablePreloadOutcome.Incomplete, result.Outcome);
        Assert.Equal([(2u, TableState.Corrupt, "FILES_CORRUPT"), (3u, TableState.Missing, "OBJECT_ACCESS_MISSING")],
            result.Tables.Select(entry => (entry.FileDataId, entry.State, entry.Reason?.Code)));
        Assert.All(result.Tables, entry => Assert.True(entry.Checked));
        Assert.Equal((3L, 1L, 3, 3, 2), (result.Final.FilesDone, result.Final.FilesFailed, result.Final.TablesDone, result.Final.TablesTotal,
            result.Final.TablesFailed));
        Assert.Equal(result.Final, reports.All[^1]);
    }

    [Fact]
    public async Task ALayer2FailureInTheEnsureIsFailed()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(1, FileStorage.NotStored)]);
        world.Files.BeforeFetch = (_, _, _) => throw new VibeMiner.Objects.ObjectStoreException("OBJECT_STORE_IO", "The disk is gone.");
        await using var tables = await OpenAsync(world);
        var result = await tables.PreloadAsync(null, Ct);
        Assert.Equal((TablePreloadOutcome.Failed, "OBJECT_STORE_IO"), (result.Outcome, result.Code));
    }
}
