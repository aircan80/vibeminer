using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ItemEffectsSelection Selection = ItemEffectsValidationTests.Selection;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ItemEffectsReadiness ReadyToPrepare(ItemEffectsSelection selection) => new(selection, true, ItemEffectsReadinessState.NotPrepared);

    [Fact]
    public async Task CompletedPublishersKeepFourSharedCreditsUntilTheirWriterRetires()
    {
        var store = new Store(); var service = new ItemEffectsService(store);
        var leases = Enumerable.Range(0, 4).Select(_ => service.Admit(ItemEffectsOperation.Read)).ToArray();
        foreach (var lease in leases) await service.ReadAsync(lease, Selection);
        Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.Read)).Code);
        await leases[0].DisposeAsync(); var replacement = service.Admit(ItemEffectsOperation.Read);
        var shutdown = service.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.Read)).Code);
        foreach (var lease in leases) await lease.DisposeAsync(); await replacement.DisposeAsync();
        await shutdown.WaitAsync(Deadline); Assert.Equal(4, store.Reads);
    }

    [Fact]
    public async Task OnlyThreePrepareKindsShareTwoLongCreditsAndRefusalDoesNotConsumeLocalHeadroom()
    {
        var service = new ItemEffectsService(new Store());
        var links = service.Admit(ItemEffectsOperation.PrepareLinks); var details = service.Admit(ItemEffectsOperation.PrepareDetails);
        Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.PrepareNames)).Code);
        var read = service.Admit(ItemEffectsOperation.Read); var names = service.Admit(ItemEffectsOperation.ReadNames);
        Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.ReadPage)).Code);
        await links.DisposeAsync(); var longReplacement = service.Admit(ItemEffectsOperation.PrepareNames);
        foreach (var lease in new[] { details, read, names, longReplacement }) await lease.DisposeAsync();
        await service.DisposeAsync();
    }

    [Fact]
    public async Task WrongOwnerAndKindDoNotConsumeTheOriginalSingleAttempt()
    {
        await using var a = new ItemEffectsService(new Store()); await using var b = new ItemEffectsService(new Store());
        await using var lease = a.Admit(ItemEffectsOperation.Read);
        Assert.Equal("ITEM_EFFECTS_LEASE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => b.ReadAsync(lease, Selection))).Code);
        Assert.Equal("ITEM_EFFECTS_LEASE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => a.PrepareLinksAsync(lease, Selection))).Code);
        await a.ReadAsync(lease, Selection);
        Assert.Equal("ITEM_EFFECTS_LEASE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => a.ReadAsync(lease, Selection))).Code);
    }

    [Fact]
    public async Task ValidationFailureConsumesAttemptAndRetiresWithoutInventingAWorker()
    {
        var store = new Store(); await using var service = new ItemEffectsService(store); await using var lease = service.Admit(ItemEffectsOperation.Read);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(lease, Selection with { ItemId = "-1" }))).Code);
        Assert.Equal("ITEM_EFFECTS_LEASE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(lease, Selection))).Code);
        await lease.DisposeAsync().AsTask().WaitAsync(Deadline); Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedAndCanceledTerminalPublishersKeepTheirCreditAfterOperationRetires(bool canceled)
    {
        var store = new Store(); var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read);
        using var stop = new CancellationTokenSource(); if (canceled) stop.Cancel();
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(lease, Selection, stop.Token));
        else await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(lease, Selection with { ItemId = "-1" }));
        var queued = Enumerable.Range(0, 3).Select(_ => service.Admit(ItemEffectsOperation.Read)).ToArray();
        Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.Read)).Code);
        var closing = service.DisposeAsync().AsTask(); foreach (var other in queued) await other.DisposeAsync();
        Assert.False(closing.IsCompleted); // Simulated error/cancel terminal Flush still owns lease.
        await lease.DisposeAsync(); await closing.WaitAsync(Deadline); Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task DisposeWinningClaimStartsNoStoreAndConcurrentDisposalsAreIdempotent()
    {
        var store = new Store(); await using var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read);
        var a = lease.DisposeAsync().AsTask(); var b = lease.DisposeAsync().AsTask(); Assert.Same(a, b); await a;
        Assert.Equal("ITEM_EFFECTS_LEASE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(lease, Selection))).Code);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task ClaimedWorkRetainsLeaseThroughCancellationAndActualStoreCleanup()
    {
        var store = new Store(); var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal();
        store.Read = async (selection, token) =>
        { entered.TrySetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.TrySetResult(); await cleanup.Task; } return ReadyToPrepare(selection); };
        var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read);
        var work = service.ReadAsync(lease, Selection); await entered.Task.WaitAsync(Deadline);
        var retiring = lease.DisposeAsync().AsTask(); await cleanupEntered.Task.WaitAsync(Deadline);
        var shutdown = service.DisposeAsync().AsTask();
        try { Assert.False(work.IsCompleted); Assert.False(retiring.IsCompleted); Assert.False(shutdown.IsCompleted); }
        finally { cleanup.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(Deadline));
        await Task.WhenAll(retiring, shutdown).WaitAsync(Deadline);
    }

    [Fact]
    public async Task CancellationCallbacksAreOutsideLocksAndTheirDebtSurvivesFinishedWorker()
    {
        var store = new Store(); var entered = Signal(); var notified = Signal(); var producerRelease = Signal();
        using var notificationRelease = new ManualResetEventSlim(); CancellationTokenRegistration registration = default;
        Task<ItemEffectsReadiness>? producer = null;
        async Task<ItemEffectsReadiness> Produce(ItemEffectsSelection selection, CancellationToken token)
        {
            // Own registration beyond source completion so CancelAsync LIFO cannot remove it before invocation.
            registration = token.Register(() => { notified.TrySetResult(); if (!notificationRelease.Wait(Deadline)) throw new TimeoutException(); });
            entered.TrySetResult(); await producerRelease.Task; token.ThrowIfCancellationRequested(); return ReadyToPrepare(selection);
        }
        store.Read = (selection, token) => producer = Produce(selection, token);
        var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read); using var stop = new CancellationTokenSource();
        var work = service.ReadAsync(lease, Selection, stop.Token); await entered.Task.WaitAsync(Deadline); stop.Cancel();
        try
        {
            await notified.Task.WaitAsync(Deadline);
            var local = service.Admit(ItemEffectsOperation.Read); await local.DisposeAsync(); // No service lock around arbitrary notification.
            producerRelease.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer!.WaitAsync(Deadline));
            Assert.False(work.IsCompleted);
            var retirement = lease.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted);
            notificationRelease.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(Deadline));
            await retirement.WaitAsync(Deadline);
        }
        finally
        { producerRelease.TrySetResult(); notificationRelease.Set(); await lease.DisposeAsync().AsTask().WaitAsync(Deadline); await service.DisposeAsync(); registration.Dispose(); }
    }

    [Fact]
    public async Task ThrowingCancellationCallbackCannotBypassWorkerCleanup()
    {
        var store = new Store(); var entered = Signal(); var notified = Signal(); var release = Signal(); CancellationTokenRegistration registration = default;
        store.Read = async (selection, token) =>
        { registration = token.Register(() => { notified.TrySetResult(); throw new InvalidOperationException("notification"); }); entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); return ReadyToPrepare(selection); };
        var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read);
        var work = service.ReadAsync(lease, Selection); await entered.Task.WaitAsync(Deadline);
        var retirement = lease.DisposeAsync().AsTask();
        try { await notified.Task.WaitAsync(Deadline); Assert.False(retirement.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(Deadline)); await retirement.WaitAsync(Deadline);
        await service.DisposeAsync(); registration.Dispose();
    }

    [Fact]
    public async Task CleanupFailureRemainsChargedAndShutdownObservesTheDebt()
    {
        var store = new Store(); store.Read = (_, _) => throw new DiscoveryException("ITEM_EFFECTS_CLEANUP_FAILED", "bounded cleanup debt");
        var service = new ItemEffectsService(store); var failed = service.Admit(ItemEffectsOperation.Read);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(failed, Selection));
        Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => failed.DisposeAsync().AsTask())).Code);
        var other = Enumerable.Range(0, 3).Select(_ => service.Admit(ItemEffectsOperation.Read)).ToArray();
        Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.Read)).Code);
        foreach (var lease in other) await lease.DisposeAsync();
        Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => service.DisposeAsync().AsTask())).Code);
    }

    [Fact]
    public async Task EarlyCanceledAttemptAndAdmittedUnclaimedShutdownHaveNoAdapterWork()
    {
        var store = new Store(); var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.Read);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(lease, Selection, stop.Token));
        var unclaimed = service.Admit(ItemEffectsOperation.Read); var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        await lease.DisposeAsync(); Assert.False(closing.IsCompleted); await unclaimed.DisposeAsync(); await closing;
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task BlockedObserverCannotBackpressureProducerOrAnotherOperationAndKeepsLongCredit()
    {
        var source = Signal(); var callback = Signal(); var callbackRelease = Signal(); var sourceRelease = Signal();
        var store = new Store(); Func<ItemEffectsProgress, Task>? report = null;
        store.Prepare = async (selection, progress, _) => { report = progress; source.TrySetResult(); await sourceRelease.Task; return ReadyToPrepare(selection); };
        var service = new ItemEffectsService(store); var a = service.Admit(ItemEffectsOperation.PrepareLinks); var b = service.Admit(ItemEffectsOperation.PrepareLinks);
        var work = service.PrepareLinksAsync(a, Selection, async _ => { callback.TrySetResult(); await callbackRelease.Task; });
        await source.Task.WaitAsync(Deadline); await report!(new("Reading", "rows", 0, 1000)); await callback.Task.WaitAsync(Deadline);
        for (var i = 1; i <= 1000; i++) Assert.True(report(new("Reading", "rows", i, 1000)).IsCompletedSuccessfully);
        var local = service.Admit(ItemEffectsOperation.Read); await service.ReadAsync(local, Selection); await local.DisposeAsync();
        sourceRelease.TrySetResult();
        try
        {
            Assert.False(work.IsCompleted);
            Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.PrepareNames)).Code);
            var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            callbackRelease.TrySetResult(); await work.WaitAsync(Deadline); await a.DisposeAsync(); await b.DisposeAsync(); await closing.WaitAsync(Deadline);
        }
        finally { callbackRelease.TrySetResult(); sourceRelease.TrySetResult(); await a.DisposeAsync(); await b.DisposeAsync(); await service.DisposeAsync(); }
    }

    [Fact]
    public async Task ObserverFailureDoesNotEraseTerminalResultAndInvalidProgressIsRejected()
    {
        var store = new Store(); var callback = Signal(); var release = Signal();
        store.Prepare = async (selection, report, _) => { await report(new("Reading", "rows")); await callback.Task; await release.Task; return ReadyToPrepare(selection); };
        var service = new ItemEffectsService(store); var lease = service.Admit(ItemEffectsOperation.PrepareLinks);
        var work = service.PrepareLinksAsync(lease, Selection, _ => { callback.TrySetResult(); throw new InvalidOperationException("observer"); });
        await callback.Task.WaitAsync(Deadline); release.TrySetResult(); Assert.Equal(ItemEffectsReadinessState.NotPrepared, (await work.WaitAsync(Deadline)).State);
        await lease.DisposeAsync();
        store.Prepare = async (selection, report, _) => { await report(new("Reading", "rows", 2, 1)); return ReadyToPrepare(selection); };
        var invalid = service.Admit(ItemEffectsOperation.PrepareLinks);
        Assert.Equal("ITEM_EFFECTS_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareLinksAsync(invalid, Selection))).Code);
        await invalid.DisposeAsync(); await service.DisposeAsync();
    }

    [Theory]
    [InlineData(9_007_199_254_740_991, true)]
    [InlineData(9_007_199_254_740_992, false)]
    [InlineData(long.MaxValue, false)]
    public async Task NumericProgressCountersStayWithinExactJavascriptIntegerDomain(long counter, bool valid)
    {
        var store = new Store();
        store.Prepare = async (selection, report, _) => { await report(new("Reading", "rows", counter, counter)); return ReadyToPrepare(selection); };
        await using var service = new ItemEffectsService(store); await using var lease = service.Admit(ItemEffectsOperation.PrepareLinks);
        if (valid) await service.PrepareLinksAsync(lease, Selection);
        else Assert.Equal("ITEM_EFFECTS_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareLinksAsync(lease, Selection))).Code);
        store.Prepare = async (selection, report, _) => { await report(new("Reading", "rows", null, counter)); return ReadyToPrepare(selection); };
        await using var totalOnly = service.Admit(ItemEffectsOperation.PrepareLinks);
        if (valid) await service.PrepareLinksAsync(totalOnly, Selection);
        else Assert.Equal("ITEM_EFFECTS_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareLinksAsync(totalOnly, Selection))).Code);
    }

    [Theory]
    [InlineData(ItemEffectsOperation.Read)] [InlineData(ItemEffectsOperation.PrepareLinks)]
    [InlineData(ItemEffectsOperation.ReadPage)] [InlineData(ItemEffectsOperation.ReadDetails)]
    [InlineData(ItemEffectsOperation.PrepareDetails)] [InlineData(ItemEffectsOperation.ReadNames)] [InlineData(ItemEffectsOperation.PrepareNames)]
    public async Task EveryOperationUsesItsExactPortAndKeepsItsPublicationAttempt(ItemEffectsOperation operation)
    {
        var store = new Store(); await using var service = new ItemEffectsService(store); await using var lease = service.Admit(operation);
        var details = ItemEffectsResultValidationTests.Details; var names = ItemEffectsResultValidationTests.Names;
        object result = operation switch
        {
            ItemEffectsOperation.Read => await service.ReadAsync(lease, Selection),
            ItemEffectsOperation.PrepareLinks => await service.PrepareLinksAsync(lease, Selection),
            ItemEffectsOperation.ReadPage => await service.ReadPageAsync(lease, new(Selection, details.BridgeIndexArtifactId)),
            ItemEffectsOperation.ReadDetails => await service.ReadDetailsAsync(lease, details),
            ItemEffectsOperation.PrepareDetails => await service.PrepareDetailsAsync(lease, details),
            ItemEffectsOperation.ReadNames => await service.ReadNamesAsync(lease, names),
            _ => await service.PrepareNamesAsync(lease, names)
        };
        Assert.NotNull(result); Assert.Equal(operation, Assert.Single(store.Calls));
    }

    [Fact]
    public async Task ClaimedHostileValidationRetainsCreditUntilItActuallyReturns()
    {
        var entered = Signal(); using var validationRelease = new ManualResetEventSlim(); var store = new Store(); var service = new ItemEffectsService(store);
        var lease = service.Admit(ItemEffectsOperation.ReadDetails);
        var entries = new HeldCount(entered, validationRelease);
        // Only the test runs arbitrary hostile caller code on a worker; production never does Task.Run.
        var work = Task.Run(() => service.ReadDetailsAsync(lease, ItemEffectsResultValidationTests.Details with { Edges = entries }));
        await entered.Task.WaitAsync(Deadline); var retirement = lease.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            var others = Enumerable.Range(0, 3).Select(_ => service.Admit(ItemEffectsOperation.Read)).ToArray();
            Assert.Equal("ITEM_EFFECTS_BUSY", Assert.Throws<DiscoveryException>(() => service.Admit(ItemEffectsOperation.Read)).Code);
            foreach (var other in others) await other.DisposeAsync();
        }
        finally { validationRelease.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(Deadline));
        await retirement.WaitAsync(Deadline); await service.DisposeAsync(); Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task LatestProgressOverwritesAndCannotExceedTenHertzEvenAcrossStageChanges()
    {
        var clock = new ManualClock(); var queue = new QueuedContext(); var previous = SynchronizationContext.Current;
        var sourceRelease = Signal(); var store = new Store(); Func<ItemEffectsProgress, Task>? report = null; var values = new List<ItemEffectsProgress>();
        store.Prepare = async (selection, emit, _) => { report = emit; await sourceRelease.Task; return ReadyToPrepare(selection); };
        var service = new ItemEffectsService(store, clock); var lease = service.Admit(ItemEffectsOperation.PrepareLinks); Task<ItemEffectsReadiness>? work = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            work = service.PrepareLinksAsync(lease, Selection, value => { values.Add(value); return Task.CompletedTask; });
            await report!(new("Reading", "rows", 0, 1000)); queue.Drain(); Assert.Single(values);
            for (var i = 1; i <= 1000; i++) await report(new("Indexing", "rows", i, 1000));
            queue.Drain(); Assert.Single(values);
            clock.Advance(TimeSpan.FromMilliseconds(99)); queue.Drain(); Assert.Single(values);
            clock.Advance(TimeSpan.FromMilliseconds(1)); queue.Drain();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            await queue.DrainUntilAsync(() => values.Count == 2); Assert.Equal(1000, values[1].Completed); Assert.Equal("Indexing", values[1].Stage);
        }
        finally
        {
            sourceRelease.TrySetResult(); await queue.DrainUntilAsync(() => work!.IsCompleted); await work!;
            await lease.DisposeAsync(); await service.DisposeAsync();
        }
    }

    private sealed class HeldCount(TaskCompletionSource entered, ManualResetEventSlim release) : IReadOnlyList<ItemEffectsEdgeKey>
    {
        public int Count { get { entered.TrySetResult(); if (!release.Wait(Deadline)) throw new TimeoutException(); return 1; } }
        public ItemEffectsEdgeKey this[int index] => new("0");
        public IEnumerator<ItemEffectsEdgeKey> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Store : IItemEffectsStore
    {
        internal int Reads;
        internal readonly List<ItemEffectsOperation> Calls = [];
        internal Func<ItemEffectsSelection, CancellationToken, Task<ItemEffectsReadiness>> Read = (selection, _) => Task.FromResult(ReadyToPrepare(selection));
        internal Func<ItemEffectsSelection, Func<ItemEffectsProgress, Task>, CancellationToken, Task<ItemEffectsReadiness>> Prepare = (selection, _, _) => Task.FromResult(ReadyToPrepare(selection));
        public Task<ItemEffectsReadiness> ReadAsync(ItemEffectsSelection selection, CancellationToken token) { Calls.Add(ItemEffectsOperation.Read); Reads++; return Read(selection, token); }
        public Task<ItemEffectsReadiness> PrepareLinksAsync(ItemEffectsSelection selection, Func<ItemEffectsProgress, Task> progress, CancellationToken token) { Calls.Add(ItemEffectsOperation.PrepareLinks); return Prepare(selection, progress, token); }
        public Task<ItemEffectsPage> ReadPageAsync(ItemEffectsPageRequest request, CancellationToken token)
        { Calls.Add(ItemEffectsOperation.ReadPage); return Task.FromResult(new ItemEffectsPage(request.Selection, request.BridgeIndexArtifactId, ItemEffectsValidation.OrderPolicy, ItemEffectsResultValidationTests.Source(ItemEffectsSourceRole.Bridge), "0", [], null)); }
        public Task<ItemEffectDetailsResult> ReadDetailsAsync(ItemEffectDetailsRequest request, CancellationToken token) { Calls.Add(ItemEffectsOperation.ReadDetails); return Task.FromResult(Details(request)); }
        public Task<ItemEffectDetailsResult> PrepareDetailsAsync(ItemEffectDetailsRequest request, Func<ItemEffectsProgress, Task> progress, CancellationToken token) { Calls.Add(ItemEffectsOperation.PrepareDetails); return Task.FromResult(Details(request)); }
        public Task<ItemEffectNamesResult> ReadNamesAsync(ItemEffectNamesRequest request, CancellationToken token) { Calls.Add(ItemEffectsOperation.ReadNames); return Task.FromResult(Names(request)); }
        public Task<ItemEffectNamesResult> PrepareNamesAsync(ItemEffectNamesRequest request, Func<ItemEffectsProgress, Task> progress, CancellationToken token) { Calls.Add(ItemEffectsOperation.PrepareNames); return Task.FromResult(Names(request)); }
        private static ItemEffectDetailsResult Details(ItemEffectDetailsRequest request) => new(request.Selection, request.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
            new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotPrepared), request.Edges.Select(e => new ItemEffectOutcome(e, "0", ItemEffectState.NotPrepared)).ToArray());
        private static ItemEffectNamesResult Names(ItemEffectNamesRequest request) => new(request.Selection, request.BridgeIndexArtifactId, request.ItemEffectArtifactId, request.PolicyRevision,
            [new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotPrepared), new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.NotChecked)],
            request.Edges.Select(e => new ItemEffectNameOutcome(e, new(ItemEffectState.NotPrepared, "0"), ItemEffectNameState.EffectNotReady)).ToArray());
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer; }
        internal void Advance(TimeSpan amount)
        {
            Timer[] ready; lock (gate) { ticks += amount.Ticks; ready = timers.Where(t => t.Due <= ticks).ToArray(); foreach (var timer in ready) timer.Due = long.MaxValue; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state; internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return true; }
            public void Dispose() { lock (clock.gate) Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = [];
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Enqueue((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal void Drain() { for (var count = 0; Run(); count++) if (count > 10_000) throw new InvalidOperationException("Continuation queue did not retire."); }
        internal async Task DrainUntilAsync(Func<bool> complete)
        { using var stop = new CancellationTokenSource(Deadline); while (!complete()) if (!Run()) await posted.WaitAsync(stop.Token).ConfigureAwait(false); }
        private bool Run()
        {
            (SendOrPostCallback Callback, object? State) work; lock (pending) { if (!pending.TryDequeue(out work)) return false; }
            var previous = Current; SetSynchronizationContext(this); try { work.Callback(work.State); return true; } finally { SetSynchronizationContext(previous); }
        }
    }
}
