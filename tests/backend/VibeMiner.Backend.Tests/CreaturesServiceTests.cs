using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreaturesServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly CreaturesContext Context = new("wow", new('a', 64), "us", "enUS");
    private static readonly CreaturesSnapshot Snapshot = new(Context, new('b', 64), "2026-10-19T00:00:00Z", "creatures-core-v1", "primary-name-ordinal-ignorecase-contains-v1",
        "creatures-direct-display-v1", null!, new("0", "0", "0", "0", "0", "0", "0"), null!);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("01")] [InlineData("-1")] [InlineData("4294967296")] [InlineData("1\n")]
    public async Task InvalidIdsFailBeforeStore(string id)
    {
        var store = new Store(); await using var service = new CreaturesService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.InspectAsync(new(Context, Snapshot.ArtifactId, id)))).Code);
        Assert.Equal(0, store.Queries);
    }
    [Fact]
    public async Task QueryValidationPreservesLiteralUnicodeAndDistinctExactMode()
    {
        var store = new Store(); await using var service = new CreaturesService(store);
        foreach (var query in new[] { "  123  %_\\\"", "Straße", string.Concat(Enumerable.Repeat("𝄞", 128)) })
            await service.ReadPageAsync(new(Context, Snapshot.ArtifactId, query));
        foreach (var query in new[] { "\0", "\ud800", "\udc00", new string('a', 129) })
            await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadPageAsync(new(Context, Snapshot.ArtifactId, query)));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadPageAsync(new(Context, Snapshot.ArtifactId, "1", "1")));
        await service.InspectAsync(new(Context, Snapshot.ArtifactId, "0")); await service.InspectAsync(new(Context, Snapshot.ArtifactId, "4294967295"));
        Assert.Equal(0, store.Prepares);
    }
    [Fact]
    public async Task ThirtyTwoActualPreparationSubscriptionsShareAndOneCancelPreservesProducer()
    {
        var store = new Store(); var entered = Signal(); var release = Signal(); CancellationToken producer = default;
        store.Prepare = async (_, _, token) => { producer = token; entered.TrySetResult(); await release.Task.WaitAsync(token); return Snapshot; };
        await using var service = new CreaturesService(store); using var stop = new CancellationTokenSource();
        var canceled = service.PrepareAsync(Context, cancellationToken: stop.Token); await entered.Task.WaitAsync(Deadline);
        var others = Enumerable.Range(0, 31).Select(_ => service.PrepareAsync(Context)).ToList();
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled); Assert.False(producer.IsCancellationRequested);
        others.Add(service.PrepareAsync(Context)); release.SetResult(); await Task.WhenAll(others).WaitAsync(Deadline); Assert.Equal(1, store.Prepares);
    }
    [Fact]
    public async Task FinalStopAndShutdownAwaitActualProducerCleanup()
    {
        var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal(); var store = new Store();
        store.Prepare = async (_, _, token) => { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.SetResult(); await cleanup.Task; } return Snapshot; };
        var service = new CreaturesService(store); using var stop = new CancellationTokenSource();
        var accepted = service.PrepareAsync(Context, cancellationToken: stop.Token); await entered.Task.WaitAsync(Deadline); stop.Cancel();
        await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(accepted.IsCompleted);
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
        var shutdown = service.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted); cleanup.SetResult();
        await shutdown.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(Context))).Code);
    }
    [Fact]
    public async Task FourQueriesIncludeCanceledDrainingWorkAndDoNotBlockOnPreparation()
    {
        var store = new Store(); var entered = Signal(); var never = Signal(); var cleanupEntered = Signal(); var cleanup = Signal();
        store.Read = async token => { entered.TrySetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.TrySetResult(); await cleanup.Task; } return new(null, CreatureDependencyState.NotPrepared); };
        var prepRelease = Signal(); store.Prepare = async (_, _, token) => { await prepRelease.Task.WaitAsync(token); return Snapshot; };
        var service = new CreaturesService(store); using var stop = new CancellationTokenSource();
        var prep = service.PrepareAsync(Context); var first = service.ReadAsync(Context, stop.Token);
        var remaining = Enumerable.Range(0, 3).Select(_ => service.ReadAsync(Context)).ToArray(); await entered.Task.WaitAsync(Deadline);
        stop.Cancel(); await cleanupEntered.Task.WaitAsync(Deadline);
        Assert.Equal("CREATURES_QUERY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(Context))).Code);
        Assert.False(first.IsCompleted); Assert.False(prep.IsCompleted);
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); cleanup.SetResult(); prepRelease.SetResult();
        await closing.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        foreach (var query in remaining) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        try { await prep; } catch (OperationCanceledException) { }
    }
    [Fact]
    public async Task ClosingObserverKeepsThirtySecondSlotChargedUntilActualDrain()
    {
        var prior = SynchronizationContext.Current; var context = new QueuedContext(); var callbackEntered = Signal(); var progressRelease = Signal(); var sourceRelease = Signal();
        var store = new Store(); store.Prepare = async (_, report, token) => { await report(new("Reading", "rows", 0, 1)); await sourceRelease.Task.WaitAsync(token); return Snapshot; };
        SynchronizationContext.SetSynchronizationContext(context); var service = new CreaturesService(store); using var stop = new CancellationTokenSource();
        Task<CreaturesSnapshot>? canceled = null, refused = null; Task<CreaturesSnapshot>[] survivors = []; Task? closing = null;
        try
        {
            canceled = service.PrepareAsync(Context, async _ => { callbackEntered.SetResult(); await progressRelease.Task; }, stop.Token);
            survivors = Enumerable.Range(0, 31).Select(_ => service.PrepareAsync(Context)).ToArray(); context.Drain(); Assert.True(callbackEntered.Task.IsCompleted);
            stop.Cancel(); context.Drain(); Assert.False(canceled.IsCompleted); refused = service.PrepareAsync(Context); Assert.True(refused.IsFaulted);
            closing = service.DisposeAsync().AsTask(); context.Drain(); Assert.False(closing.IsCompleted);
        }
        finally
        {
            progressRelease.TrySetResult(); sourceRelease.TrySetResult(); context.Drain(); closing ??= service.DisposeAsync().AsTask(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(prior);
        }
        await context.DrainUntilAsync(() => canceled!.IsCompleted && survivors.All(t => t.IsCompleted), Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled!);
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => refused!)).Code);
        foreach (var survivor in survivors) { try { await survivor; } catch (OperationCanceledException) { } }
        await context.DrainUntilAsync(() => closing.IsCompleted, Deadline);
        await closing;
    }
    [Fact]
    public async Task ProgressObserversAreIndependentLatestOnlyAndTenHertzWithNoProducerBackpressure()
    {
        var clock = new ManualClock(); var store = new Store(); var sourceReady = Signal(); var sourceRelease = Signal();
        Func<CreaturesPreparationProgress, Task>? emit = null;
        store.Prepare = async (_, report, token) => { emit = report; sourceReady.TrySetResult(); await sourceRelease.Task.WaitAsync(token); return Snapshot; };
        var prior = SynchronizationContext.Current; var queue = new QueuedContext();
        var aEntered = Signal(); var aRelease = Signal(); var a = new List<CreaturesPreparationProgress>(); var b = new List<CreaturesPreparationProgress>();
        var service = new CreaturesService(store, clock); Task<CreaturesSnapshot>? first = null, second = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            first = service.PrepareAsync(Context, async p => { a.Add(p); aEntered.TrySetResult(); await aRelease.Task; });
            second = service.PrepareAsync(Context, p => { b.Add(p); return Task.CompletedTask; });
            Assert.True(sourceReady.Task.IsCompleted);
            Assert.True(emit!(new("Reading", "rows", 0, 1000)).IsCompletedSuccessfully);
            queue.Drain(); Assert.True(aEntered.Task.IsCompleted); Assert.Single(b);
            for (var i = 1; i <= 1000; i++) Assert.True(emit(new("Indexing", "rows", i, 1000)).IsCompletedSuccessfully);
            queue.Drain(); Assert.Single(a); Assert.Single(b);
            clock.Advance(TimeSpan.FromMilliseconds(99)); queue.Drain(); Assert.Single(b);
            clock.Advance(TimeSpan.FromMilliseconds(1)); queue.Drain();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        await queue.DrainUntilAsync(() => b.Count == 2, Deadline);
        Assert.Equal(1000, b[1].Completed); Assert.Equal("Indexing", b[1].Stage); Assert.Single(a);
        sourceRelease.SetResult(); await queue.DrainUntilAsync(() => second!.IsCompleted, Deadline);
        Assert.False(first!.IsCompleted); // Producer success and the useful consumer are independent of A's callback.
        var replacement = service.PrepareAsync(Context); await replacement.WaitAsync(Deadline); Assert.Equal(2, store.Prepares);
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        aRelease.SetResult(); await queue.DrainUntilAsync(() => first.IsCompleted, Deadline);
        await first; await second!; await closing.WaitAsync(Deadline); Assert.Single(a);
    }
    [Fact]
    public async Task FailedObserverWithdrawsWithoutFailingProducerOrOtherObservers()
    {
        var prior = SynchronizationContext.Current; var queue = new QueuedContext(); var release = Signal(); var good = Signal();
        var store = new Store(); Func<CreaturesPreparationProgress, Task>? report = null;
        store.Prepare = async (_, progress, ct) => { report = progress; await release.Task.WaitAsync(ct); return Snapshot; };
        var service = new CreaturesService(store); Task<CreaturesSnapshot>? bad = null, useful = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            bad = service.PrepareAsync(Context, _ => throw new InvalidOperationException("observer"));
            useful = service.PrepareAsync(Context, _ => { good.SetResult(); return Task.CompletedTask; });
            await report!(new("Reading", "rows")); queue.Drain(); Assert.True(good.Task.IsCompleted);
            release.SetResult(); queue.Drain();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        await queue.DrainUntilAsync(() => bad!.IsCompleted && useful!.IsCompleted, Deadline);
        Assert.Same(Snapshot, await bad!); Assert.Same(Snapshot, await useful!); await service.DisposeAsync();
    }
    [Fact]
    public async Task CancellationNotificationsRunOutsideLocksAndRetireBeforeProducerSlot()
    {
        var store = new Store(); var entered = Signal(); var notification = Signal(); using var release = new ManualResetEventSlim();
        var producerRelease = Signal(); var cleanupEntered = Signal(); var cleanupRelease = Signal(); var notificationExited = Signal();
        CancellationTokenRegistration registration = default; Exception? callbackFailure = null;
        Task<CreaturesSnapshot>? producer = null;
        async Task<CreaturesSnapshot> Produce(CancellationToken ct)
        {
            // The test owns this registration through notification retirement. Producer cleanup
            // must not remove it before invocation or block on disposing the running callback.
            registration = ct.Register(() =>
            {
                try { notification.TrySetResult(); if (!release.Wait(Deadline)) throw new TimeoutException("Notification release guard."); }
                catch (Exception failure) { callbackFailure = failure; throw; }
                finally { notificationExited.TrySetResult(); }
            });
            entered.TrySetResult();
            try { await producerRelease.Task; ct.ThrowIfCancellationRequested(); return Snapshot; }
            finally { cleanupEntered.TrySetResult(); await cleanupRelease.Task; }
        }
        store.Prepare = (_, _, ct) => producer = Produce(ct);
        var service = new CreaturesService(store); using var stop = new CancellationTokenSource();
        var accepted = service.PrepareAsync(Context, cancellationToken: stop.Token); await entered.Task.WaitAsync(Deadline); stop.Cancel();
        try
        {
            await notification.Task.WaitAsync(Deadline); Assert.False(accepted.IsCompleted);
            await service.ReadAsync(Context).WaitAsync(Deadline); // No lock held across the arbitrary notification.
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
            producerRelease.SetResult(); await cleanupEntered.Task.WaitAsync(Deadline);
            Assert.False(accepted.IsCompleted); cleanupRelease.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => producer!.WaitAsync(Deadline));
            // Actual source cleanup has finished; notification debt alone still reserves the slot.
            Assert.False(accepted.IsCompleted);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
            release.Set(); await notificationExited.Task.WaitAsync(Deadline); Assert.Null(callbackFailure);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted.WaitAsync(Deadline));
        }
        finally
        {
            producerRelease.TrySetResult(); cleanupRelease.TrySetResult(); release.Set();
            try { await service.DisposeAsync().AsTask().WaitAsync(Deadline); }
            finally { registration.Dispose(); }
        }
    }
    [Fact]
    public async Task ThirtyTwoCompletedGenerationsRemainChargedThroughTheirBlockedObservers()
    {
        var store = new Store(); var release = Signal(); var entered = new List<TaskCompletionSource>(); var source = new List<TaskCompletionSource>();
        store.Prepare = async (_, report, _) => { var done = Signal(); source.Add(done); await report(new("Reading", "rows")); await done.Task; return Snapshot; };
        var service = new CreaturesService(store); var prior = SynchronizationContext.Current; var queue = new QueuedContext(); var calls = new List<Task<CreaturesSnapshot>>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(queue);
            for (var i = 0; i < 32; i++)
            {
                var callback = Signal(); entered.Add(callback);
                calls.Add(service.PrepareAsync(Context, async _ => { callback.TrySetResult(); await release.Task; }));
                queue.Drain(); Assert.True(callback.Task.IsCompleted); source[i].SetResult();
                // The next accepted source generation proves the preceding producer retired; it must not release the subscription.
                queue.Drain();
            }
            var refused = service.PrepareAsync(Context); Assert.True(refused.IsFaulted);
            Assert.Equal("CREATURES_BUSY", refused.Exception!.GetBaseException() is DiscoveryException error ? error.Code : "unexpected");
        }
        finally { SynchronizationContext.SetSynchronizationContext(prior); }
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); release.SetResult();
        await queue.DrainUntilAsync(() => calls.All(c => c.IsCompleted), Deadline); await closing.WaitAsync(Deadline);
        await Task.WhenAll(calls); Assert.Equal(32, store.Prepares);
    }
    [Fact]
    public async Task ThrowingProducerCancellationNotificationDoesNotBypassCleanup()
    {
        var store = new Store(); var entered = Signal(); var notification = Signal(); var cleanup = Signal(); var cleanupEntered = Signal();
        store.Prepare = async (_, _, token) =>
        {
            using var registration = token.Register(() => { notification.TrySetResult(); throw new InvalidOperationException("notification"); }); entered.SetResult();
            // The callback itself releases the independent producer gate, so this using cannot
            // remove the throwing registration before the notification has actually run.
            try { await notification.Task; token.ThrowIfCancellationRequested(); } finally { cleanupEntered.SetResult(); await cleanup.Task; }
            return Snapshot;
        };
        var service = new CreaturesService(store); using var stop = new CancellationTokenSource(); var accepted = service.PrepareAsync(Context, cancellationToken: stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline); stop.Cancel(); await notification.Task.WaitAsync(Deadline);
            await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(accepted.IsCompleted);
            var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); cleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted.WaitAsync(Deadline)); await closing.WaitAsync(Deadline);
        }
        finally { notification.TrySetResult(); cleanup.TrySetResult(); await service.DisposeAsync().AsTask().WaitAsync(Deadline); }
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
    private sealed class Store : ICreaturesStore
    {
        internal int Prepares, Queries;
        internal Func<CreaturesContext, Func<CreaturesPreparationProgress, Task>, CancellationToken, Task<CreaturesSnapshot>> Prepare = (_, _, _) => Task.FromResult(Snapshot);
        internal Func<CancellationToken, Task<CreaturesReadiness>> Read = _ => Task.FromResult(new CreaturesReadiness(null, CreatureDependencyState.NotPrepared));
        public Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token) { Queries++; return Read(token); }
        public Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token) { Prepares++; return Prepare(context, progress, token); }
        public Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token)
        { Queries++; return Task.FromResult(new CreaturesPage(request.Context, request.ArtifactId, request.Query, request.ExactCreatureId, [], null)); }
        public Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token)
        { Queries++; return Task.FromResult(new CreatureInspectResult(request.Context, request.ArtifactId, request.CreatureId, CreatureInspectState.NotFoundInAdmittedScope, null!, null)); }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = [];
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Enqueue((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal void Drain() { for (var count = 0; Run(); count++) if (count > 10_000) throw new InvalidOperationException("Continuation queue did not retire."); }
        internal async Task DrainUntilAsync(Func<bool> complete, TimeSpan timeout)
        { using var stop = new CancellationTokenSource(timeout); while (!complete()) if (!Run()) await posted.WaitAsync(stop.Token).ConfigureAwait(false); }
        private bool Run()
        {
            (SendOrPostCallback Callback, object? State) work; lock (pending) { if (!pending.TryDequeue(out work)) return false; }
            var previous = Current; SetSynchronizationContext(this); try { work.Callback(work.State); return true; } finally { SetSynchronizationContext(previous); }
        }
    }
}
