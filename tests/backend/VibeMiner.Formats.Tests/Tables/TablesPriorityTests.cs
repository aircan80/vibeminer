using System.Collections.Concurrent;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// Priority in the real <see cref="TablesService"/> (D263): builds held at gates in the generated store, the order read from the
/// scheduler's state. The timeouts only stop a deadlocked test. Synthetic data only.
/// </summary>
public sealed class TablesPriorityTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const TablePriority Later = TablePriority.Background;
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];

    /// <summary>Tables 1 to <paramref name="count"/>, each with its own rows (so its own content), stored and not built yet.</summary>
    private static async Task<(IBuildTables Tables, Gates Gates, TableWorkScheduler Scheduler)> OpenAsync(int count, int maxBackground = 2)
    {
        var scheduler = new TableWorkScheduler(maxBackground: maxBackground);
        var tables = Enumerable.Range(1, count).Select(i => (uint)i).Select(id => new SyntheticTable(id, $"T{id}", SyntheticTables.Dbd(Plain),
            new FakeFile(id, Content: SyntheticTables.Wdc5(Plain, [new(id, [$"{id}", $"{id * 7}"])], null).Bytes, Storage: FileStorage.Stored))).ToList();
        var world = await SyntheticTables.CreateAsync(Build, tables, options: o => o with { Scheduler = scheduler });
        var handle = (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;
        var gates = new Gates();
        await foreach (var entry in handle.ListAsync(TableListFilter.All, Now, Ct)) gates.Ids[entry.ContentKey!] = entry.FileDataId;
        world.Generated.Decoding = gates.DecodingAsync;
        world.Generated.Building = gates.Building;
        return (handle, gates, scheduler);
    }

    private sealed class Gates
    {
        public ConcurrentDictionary<string, uint> Ids { get; } = new();
        private readonly ConcurrentDictionary<uint, TaskCompletionSource> held = new(), started = new(), asked = new();
        private readonly ConcurrentDictionary<uint, int> decodes = new(), asks = new();

        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Holds table <paramref name="id"/>'s decode until <see cref="Release"/>.</summary>
        public void Hold(uint id) => held[id] = Signal();
        public void Release(uint id) => held[id].TrySetResult();
        public Task Started(uint id) => started.GetOrAdd(id, _ => Signal()).Task;
        /// <summary>Once table <paramref name="id"/> was asked to be built <paramref name="times"/> times.</summary>
        public Task Asked(uint id, int times) => asked.GetOrAdd((uint)(id * 1000 + times), _ => Signal()).Task;
        public int Decodes(uint id) => decodes.GetValueOrDefault(id);

        public Task DecodingAsync(GeneratedTableSource source)
        {
            var id = Ids[source.ContentKey];
            decodes.AddOrUpdate(id, 1, (_, n) => n + 1);
            started.GetOrAdd(id, _ => Signal()).TrySetResult();
            return held.TryGetValue(id, out var gate) ? gate.Task : Task.CompletedTask;
        }

        public void Building(GeneratedTableSource source)
        {
            var id = Ids[source.ContentKey];
            var n = asks.AddOrUpdate(id, 1, (_, k) => k + 1);
            asked.GetOrAdd((uint)(id * 1000 + n), _ => Signal()).TrySetResult();
        }
    }

    [Fact]
    public async Task AnInteractivePageIsServedWhileBackgroundBuildsHoldEverySlotAndMoreWait()
    {
        var (tables, gates, scheduler) = await OpenAsync(5);
        await using var _ = tables;
        gates.Hold(1); gates.Hold(2);
        var b1 = tables.CheckAsync(1, Later, Ct);
        var b2 = tables.CheckAsync(2, Later, Ct);
        await Task.WhenAll(gates.Started(1), gates.Started(2)).WaitAsync(Guard);
        var b3 = tables.CheckAsync(3, Later, Ct);
        await scheduler.WhenAsync(() => scheduler.Queued(Later) == 1).WaitAsync(Guard);

        // Table 5 isn't built yet either: the page builds it in an Interactive slot.
        var page = await tables.PageAsync(new(5), Now, Ct).WaitAsync(Guard);
        Assert.Equal(TablePageState.Ok, page.State);
        Assert.Equal(["5", "35"], page.Rows.Single().Row.Values);
        Assert.Equal(TableState.Ready, page.Table.State);
        Assert.False(b1.IsCompleted || b2.IsCompleted || b3.IsCompleted);
        Assert.Equal(0, gates.Decodes(3));

        // The queued Background build starts once a Background slot frees.
        gates.Release(1);
        Assert.Equal(TableState.Ready, (await b1.WaitAsync(Guard)).State);
        Assert.Equal(TableState.Ready, (await b3.WaitAsync(Guard)).State);
        Assert.False(b2.IsCompleted);
        gates.Release(2);
        Assert.Equal(TableState.Ready, (await b2.WaitAsync(Guard)).State);
        Assert.Equal((0, 0), (scheduler.Running(Later), scheduler.Queued(Later)));
    }

    [Fact]
    public async Task AnInteractiveCallerJoinsABackgroundBuildWithoutWaitingForQueuedBackgroundWork()
    {
        var (tables, gates, scheduler) = await OpenAsync(3);
        await using var _ = tables;
        gates.Hold(1); gates.Hold(2); gates.Hold(3);
        var b1 = tables.CheckAsync(1, Later, Ct);
        var b2 = tables.CheckAsync(2, Later, Ct);
        await Task.WhenAll(gates.Started(1), gates.Started(2)).WaitAsync(Guard);
        var b3 = tables.CheckAsync(3, Later, Ct);
        await scheduler.WhenAsync(() => scheduler.Queued(Later) == 1).WaitAsync(Guard);

        var joined = tables.SchemaAsync(1, Now, Ct);
        await gates.Asked(1, 2).WaitAsync(Guard);
        Assert.Equal(1, scheduler.Running(Now));
        gates.Release(1);

        var schema = await joined.WaitAsync(Guard);
        Assert.Equal(TableState.Ready, schema.Table.State);
        Assert.Equal(TableState.Ready, (await b1.WaitAsync(Guard)).State);
        // One build served both, and the gated Background work still waits.
        Assert.Equal(1, gates.Decodes(1));
        Assert.False(b2.IsCompleted || b3.IsCompleted);

        gates.Release(2); gates.Release(3);
        await Task.WhenAll(b2, b3).WaitAsync(Guard);
        Assert.Equal((0, 0, 0), (scheduler.Running(Now), scheduler.Running(Later), scheduler.Queued(Later)));
    }

    [Fact]
    public async Task ACheckCanceledWhileQueuedLeavesTheQueueAndBuildsNothing()
    {
        var (tables, gates, scheduler) = await OpenAsync(2, maxBackground: 1);
        await using var _ = tables;
        gates.Hold(1);
        var b1 = tables.CheckAsync(1, Later, Ct);
        await gates.Started(1).WaitAsync(Guard);
        using var cancel = new CancellationTokenSource();
        var b2 = tables.CheckAsync(2, Later, cancel.Token);
        await scheduler.WhenAsync(() => scheduler.Queued(Later) == 1).WaitAsync(Guard);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b2.WaitAsync(Guard));
        Assert.Equal(0, scheduler.Queued(Later));
        gates.Release(1);
        await b1.WaitAsync(Guard);
        Assert.Equal(0, gates.Decodes(2));
        Assert.Equal(0, scheduler.Running(Later));
    }

    private static async Task<IBuildTables> HandleAsync(TableWorkScheduler scheduler, params SyntheticTable[] tables)
    {
        var world = await SyntheticTables.CreateAsync(Build, tables, options: o => o with { Scheduler = scheduler });
        return (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;
    }

    private static SyntheticTable Big(uint id, int rows) => new(id, $"T{id}", SyntheticTables.Dbd(Plain), new FakeFile(id,
        Content: SyntheticTables.Wdc5(Plain, [.. Enumerable.Range(1, rows).Select(r => new ScenarioRow((uint)r, [$"{r}", $"{r * 3}"]))], null).Bytes,
        Storage: FileStorage.Stored));

    [Fact]
    public async Task ABackgroundScanLetsQueuedInteractiveWorkGoBetweenChunks()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 1, maxBackground: 1);
        await using var tables = await HandleAsync(scheduler, Big(1, 2100));
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(1, Now, Ct)).State);

        // Once the scan has read its first chunk, Interactive work queues: the scan's next chunk waits for it.
        var held = await scheduler.EnterAsync(Now, Ct);
        Task<TableWorkSlot>? interactive = null;
        var backgroundEnters = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.Entering = priority =>
        {
            if (priority == Later && ++backgroundEnters == 2)
            {
                interactive = scheduler.EnterAsync(Now, Ct).AsTask();
                second.TrySetResult();
            }
            return Task.CompletedTask;
        };
        // The last row is the only match, so the scan reads all three chunks.
        var scan = tables.PageAsync(new(1, Filters: [new("Value", TableFilterOp.Equals, $"{2100 * 3}")]), Later, Ct);
        await second.Task.WaitAsync(Guard);
        await scheduler.WhenAsync(() => scheduler.Queued(Later) == 1).WaitAsync(Guard);
        Assert.Equal((0, 1), (scheduler.Running(Later), scheduler.Queued(Now)));
        Assert.False(scan.IsCompleted);

        held.Dispose();
        using (await interactive!.WaitAsync(Guard))
        {
            var page = await scan.WaitAsync(Guard);
            Assert.Equal((TablePageState.Ok, 2100u), (page.State, page.Rows.Single().Row.Id));
        }
        Assert.Equal(3, backgroundEnters);
    }

    [Fact]
    public async Task StoredPagesAndRowsByIdNeedNoSlot()
    {
        var scheduler = new TableWorkScheduler();
        await using var tables = await HandleAsync(scheduler, Big(1, 3));
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(1, Now, Ct)).State);
        var slots = new List<TableWorkSlot>();
        for (var i = 0; i < scheduler.MaxInteractive; i++) slots.Add(await scheduler.EnterAsync(Now, Ct));
        for (var i = 0; i < scheduler.MaxBackground; i++) slots.Add(await scheduler.EnterAsync(Later, Ct));

        var page = await tables.PageAsync(new(1), Now, Ct).WaitAsync(Guard);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(TableRowOutcome.Found, (await tables.GetAsync(1, 2, Later, Ct).WaitAsync(Guard)).Outcome);
        Assert.Equal((0, 0), (scheduler.Queued(Now), scheduler.Queued(Later)));
        foreach (var slot in slots) slot.Dispose();
    }

    [Fact]
    public async Task AProtectedIdReadsTheFileInASlot()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 1);
        await using var tables = await HandleAsync(scheduler, SyntheticTables.From(new ScenarioTable(1, "T1", Plain,
            [new(1, ["1", "7"]), new(2, ["2", "8"])], new ScenarioKeyedSection(Key, [10, 11]))));
        Assert.Equal(TableState.Partial, (await tables.CheckAsync(1, Now, Ct)).State);
        var held = await scheduler.EnterAsync(Now, Ct);

        // A stored row needs none; a protected ID reads the file's index, so it waits for the slot.
        Assert.Equal(TableRowOutcome.Found, (await tables.GetAsync(1, 1, Now, Ct).WaitAsync(Guard)).Outcome);
        var keyed = tables.GetAsync(1, 10, Now, Ct);
        await scheduler.WhenAsync(() => scheduler.Queued(Now) == 1).WaitAsync(Guard);
        Assert.False(keyed.IsCompleted);
        held.Dispose();
        var result = await keyed.WaitAsync(Guard);
        Assert.Equal((TableRowOutcome.Protected, Key), (result.Outcome, result.ProtectedKey));
    }

    [Fact]
    public async Task ServicesShareTheSlotsOfTheSchedulerTheyAreGiven()
    {
        var scheduler = new TableWorkScheduler(maxBackground: 1);
        using var held = await scheduler.EnterAsync(Later, Ct);
        var tables = new SyntheticTable(1, "T1", SyntheticTables.Dbd(Plain),
            new FakeFile(1, Content: SyntheticTables.Wdc5(Plain, [new(1, ["1", "7"])], null).Bytes, Storage: FileStorage.Stored));
        var world = await SyntheticTables.CreateAsync(Build, [tables], options: o => o with { Scheduler = scheduler });
        await using var handle = (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;

        var check = handle.CheckAsync(1, Later, Ct);
        await scheduler.WhenAsync(() => scheduler.Queued(Later) == 1).WaitAsync(Guard);
        Assert.False(check.IsCompleted);
        held.Dispose();
        Assert.Equal(TableState.Ready, (await check.WaitAsync(Guard)).State);
    }
}
