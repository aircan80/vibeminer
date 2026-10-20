using VibeMiner.Application;
using VibeMiner.Domain;
using System.Threading.Channels;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureDisplayServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly CreaturesContext Context = new("wow", new('a', 64), "us", "enUS");
    private static readonly CreatureDisplayMetadataRequest Request = new(Context, new('b', 64), "123", 0);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Theory]
    [InlineData(-1, "1")]
    [InlineData(4, "1")]
    [InlineData(0, "01")]
    [InlineData(0, "-1")]
    [InlineData(0, "4294967296")]
    public async Task InvalidSelectionFailsBeforeOptionalWork(int slot, string id)
    {
        var store = new Store(); await using var service = new CreatureDisplayMetadataService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.PrepareDisplayAsync(Request with { Slot = slot, CreatureId = id }))).Code);
        Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task LocalReadNeverSubscribesToPreparation()
    {
        var store = new Store(); await using var service = new CreatureDisplayMetadataService(store);
        await service.ReadDisplayAsync(Request);
        Assert.Equal(1, store.Reads); Assert.Equal(0, store.Prepares);
    }
    [Fact]
    public async Task TwoSubscriptionsAreIndependentAndThirdIsImmediatelyRefused()
    {
        var release = Signal(); var entered = Signal(); var store = new Store();
        store.Prepare = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result; };
        await using var service = new CreatureDisplayMetadataService(store); using var stop = new CancellationTokenSource();
        var first = service.PrepareDisplayAsync(Request, token: stop.Token); await entered.Task.WaitAsync(Deadline);
        var second = service.PrepareDisplayAsync(Request with { CreatureId = "124", Slot = 3 });
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted); release.SetResult(); await second.WaitAsync(Deadline);
        Assert.Equal(2, store.Prepares);
    }
    [Fact]
    public async Task ActualStoreCleanupRemainsChargedDuringCancellationAndShutdown()
    {
        var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal();
        var store = new Store(); store.Prepare = async (_, _, token) =>
        { entered.TrySetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupEntered.TrySetResult(); await cleanup.Task; } return Result; };
        var service = new CreatureDisplayMetadataService(store); using var stop = new CancellationTokenSource();
        var first = service.PrepareDisplayAsync(Request, token: stop.Token); var second = service.PrepareDisplayAsync(Request);
        await entered.Task.WaitAsync(Deadline); stop.Cancel(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(first.IsCompleted);
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); cleanup.SetResult();
        await closing.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadDisplayAsync(Request))).Code);
    }
    [Fact]
    public async Task TwoLocalReadsIncludeCanceledCleanupAndLeaveMetadataAdmissionAvailable()
    {
        var entered = Signal(); var never = Signal(); var cleanup = Signal(); var cleaning = Signal();
        var store = new Store(); store.Read = async (_, token) =>
        { entered.TrySetResult(); try { await never.Task.WaitAsync(token); } finally { cleaning.TrySetResult(); await cleanup.Task; } return Result; };
        var service = new CreatureDisplayMetadataService(store); using var stop = new CancellationTokenSource();
        var first = service.ReadDisplayAsync(Request, stop.Token); var second = service.ReadDisplayAsync(Request);
        await entered.Task.WaitAsync(Deadline); stop.Cancel(); await cleaning.Task.WaitAsync(Deadline);
        Assert.Equal("CREATURES_QUERY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadDisplayAsync(Request))).Code);
        await service.PrepareDisplayAsync(Request); var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
        cleanup.SetResult(); await closing.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }
    [Fact]
    public async Task CompletedProducerKeepsSubscriptionThroughBlockedObserver()
    {
        var firstEntered = Signal(); var secondEntered = Signal(); var observerRelease = Signal(); var sourceRelease = Signal();
        var store = new Store(); store.Prepare = async (_, report, token) =>
        { await report(new("Reading", "rows", 0, 10, "bfdaf9f1")); await sourceRelease.Task.WaitAsync(token); return Result; };
        var service = new CreatureDisplayMetadataService(store);
        try
        {
            var first = service.PrepareDisplayAsync(Request, async _ => { firstEntered.TrySetResult(); await observerRelease.Task; });
            var second = service.PrepareDisplayAsync(Request, async _ => { secondEntered.TrySetResult(); await observerRelease.Task; });
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(Deadline);
            var producers = new Task<CreatureDisplayMetadataResult>[2];
            for (var i = 0; i < producers.Length; i++) producers[i] = await store.ProducerTasks.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            sourceRelease.SetResult(); await Task.WhenAll(producers).WaitAsync(Deadline);
            Assert.All(producers, producer => Assert.True(producer.IsCompletedSuccessfully));
            // Actual store tasks have succeeded; only observer retirement may keep these subscriptions alive.
            await service.ReadDisplayAsync(Request); Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
            var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); observerRelease.SetResult();
            await Task.WhenAll(first, second).WaitAsync(Deadline); await closing.WaitAsync(Deadline);
        }
        finally { sourceRelease.TrySetResult(); observerRelease.TrySetResult(); await service.DisposeAsync(); }
    }
    [Fact]
    public async Task ThrowingObserverDoesNotPoisonOtherConsumerOrProducer()
    {
        var emit = Signal(); var release = Signal(); var observed = Signal(); var store = new Store();
        store.Prepare = async (_, report, token) => { await emit.Task.WaitAsync(token); await report(new("Reading", "rows", 1, 1)); await release.Task.WaitAsync(token); return Result; };
        await using var service = new CreatureDisplayMetadataService(store);
        var bad = service.PrepareDisplayAsync(Request, _ => throw new InvalidOperationException("observer"));
        var good = service.PrepareDisplayAsync(Request, _ => { observed.TrySetResult(); return Task.CompletedTask; });
        emit.SetResult(); await observed.Task.WaitAsync(Deadline); release.SetResult(); await Task.WhenAll(bad, good).WaitAsync(Deadline);
    }
    [Fact]
    public async Task ThrowingCancellationCallbackCannotBypassActualCleanup()
    {
        var entered = Signal(); var callbackInvoked = Signal(); var producerMayExit = Signal();
        var cleanupEntered = Signal(); var cleanup = Signal(); var store = new Store();
        store.Prepare = async (_, _, token) =>
        {
            using var registration = token.Register(() =>
            {
                try { throw new InvalidOperationException("cancellation observer"); }
                finally { callbackInvoked.TrySetResult(); }
            });
            entered.TrySetResult();
            try
            {
                // Keep the registration alive independently of token continuations. The test
                // observes the throwing callback before it allows producer cleanup to begin.
                await producerMayExit.Task;
            }
            finally { cleanupEntered.TrySetResult(); await cleanup.Task; }
            token.ThrowIfCancellationRequested(); return Result;
        };
        var service = new CreatureDisplayMetadataService(store); using var stop = new CancellationTokenSource();
        try
        {
            var call = service.PrepareDisplayAsync(Request, token: stop.Token); await entered.Task.WaitAsync(Deadline);
            stop.Cancel(); await callbackInvoked.Task.WaitAsync(Deadline);
            Assert.False(cleanupEntered.Task.IsCompleted); Assert.False(call.IsCompleted);
            var peer = service.PrepareDisplayAsync(Request with { CreatureId = "124" });
            producerMayExit.SetResult(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(call.IsCompleted);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
            var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); cleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer); await closing.WaitAsync(Deadline);
        }
        finally { producerMayExit.TrySetResult(); cleanup.TrySetResult(); await service.DisposeAsync(); }
    }
    [Fact]
    public async Task SlowObserverCoalescesOneThousandValuesAndCannotDelayUsefulPeerOrProducer()
    {
        var clock = new ManualTimerClock(); var sourceRelease = Signal(); var aEntered = Signal(); var aRelease = Signal(); var bFirst = Signal(); var bLatest = Signal();
        var aSeen = new List<long?>(); var bSeen = new List<long?>();
        Func<CreaturesPreparationProgress, Task>? emitA = null, emitB = null;
        var store = new Store(); store.Prepare = async (request, report, token) =>
        { if (request.CreatureId == "123") emitA = report; else emitB = report; await sourceRelease.Task.WaitAsync(token); return Result; };
        var service = new CreatureDisplayMetadataService(store, clock);
        try
        {
            var a = service.PrepareDisplayAsync(Request, async p => { aSeen.Add(p.Completed); aEntered.TrySetResult(); await aRelease.Task; });
            var b = service.PrepareDisplayAsync(Request with { CreatureId = "124" }, p =>
            { bSeen.Add(p.Completed); if (p.Completed == 0) bFirst.TrySetResult(); if (p.Completed == 1000) bLatest.TrySetResult(); return Task.CompletedTask; });
            await emitA!(new("Reading", "rows", 0, 1000)); await emitB!(new("Reading", "rows", 0, 1000));
            await Task.WhenAll(aEntered.Task, bFirst.Task).WaitAsync(Deadline);
            // B's pump arms its throttle timer after its first callback returns. Advancing before that would arm it at 200 ms, which never fires.
            Assert.Equal(TimeSpan.FromMilliseconds(100).Ticks, await clock.Armed().WaitAsync(Deadline));
            for (var i = 1; i <= 1000; i++)
            { Assert.True(emitA(new("Reading", "rows", i, 1000)).IsCompletedSuccessfully); Assert.True(emitB(new("Reading", "rows", i, 1000)).IsCompletedSuccessfully); }
            clock.Advance(TimeSpan.FromMilliseconds(100)); await bLatest.Task.WaitAsync(Deadline);
            Assert.Equal(new long?[] { 0, 1000 }, bSeen); Assert.Single(aSeen);
            var producers = new Task<CreatureDisplayMetadataResult>[2];
            for (var i = 0; i < producers.Length; i++) producers[i] = await store.ProducerTasks.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            sourceRelease.SetResult(); await Task.WhenAll(producers).WaitAsync(Deadline);
            Assert.All(producers, producer => Assert.True(producer.IsCompletedSuccessfully));
            await b.WaitAsync(Deadline); Assert.False(a.IsCompleted);
            await service.PrepareDisplayAsync(Request with { CreatureId = "125" }).WaitAsync(Deadline);
            var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); aRelease.SetResult();
            await a.WaitAsync(Deadline); await closing.WaitAsync(Deadline);
            // A's close runs on its own continuation and can lose to the pump's next check, so A may get the one value it held. Never more.
            Assert.True(aSeen.SequenceEqual([0]) || aSeen.SequenceEqual([0, 1000]), $"A saw [{string.Join(", ", aSeen)}]");
        }
        finally { sourceRelease.TrySetResult(); aRelease.TrySetResult(); await service.DisposeAsync(); }
    }
    [Fact]
    public async Task HeldCancellationNotificationRemainsChargedAndRunsOutsideServiceLocks()
    {
        var entered = Signal(); var notification = Signal(); using var notificationRelease = new ManualResetEventSlim();
        var store = new Store(); store.Prepare = async (request, _, token) =>
        {
            var canceled = new TaskCompletionSource<CreatureDisplayMetadataResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() =>
            {
                if (request.CreatureId == "123") { notification.TrySetResult(); if (!notificationRelease.Wait(Deadline)) throw new TimeoutException(); }
                canceled.TrySetCanceled(token);
            });
            entered.TrySetResult(); return await canceled.Task;
        };
        var service = new CreatureDisplayMetadataService(store); using var stop = new CancellationTokenSource();
        var first = service.PrepareDisplayAsync(Request, token: stop.Token); await entered.Task.WaitAsync(Deadline);
        var second = service.PrepareDisplayAsync(Request with { CreatureId = "124" }); stop.Cancel();
        try
        {
            await notification.Task.WaitAsync(Deadline); Assert.False(first.IsCompleted);
            await service.ReadDisplayAsync(Request).WaitAsync(Deadline);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
        }
        finally { notificationRelease.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); var close = service.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); await close.WaitAsync(Deadline);
    }
    [Theory]
    [InlineData(-1, 10)]
    [InlineData(11, 10)]
    public async Task DishonestProgressIsRejectedBeforeObserver(int completed, int total)
    {
        var store = new Store(); store.Prepare = async (_, report, _) => { await report(new("Reading", "rows", completed, total)); return Result; };
        await using var service = new CreatureDisplayMetadataService(store);
        Assert.Equal("CREATURES_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareDisplayAsync(Request))).Code);
    }
    private static readonly CreatureDisplayMetadataResult Result = new(Context, Request.ArtifactId, Request.CreatureId, 0,
        CreatureDisplayMetadataService.MetadataPolicy, null!, null!, null!, null, CreatureDisplayMetadataState.NotPrepared,
        CreatureDisplayAppearance.NotEvaluated, [], RequiredTableId: "bfdaf9f1");
    private sealed class Store : ICreatureDisplayMetadataStore
    {
        internal int Reads, Prepares;
        internal readonly Channel<Task<CreatureDisplayMetadataResult>> ProducerTasks = Channel.CreateBounded<Task<CreatureDisplayMetadataResult>>(2);
        internal int Calls => Reads + Prepares;
        internal Func<CreatureDisplayMetadataRequest, CancellationToken, Task<CreatureDisplayMetadataResult>> Read = (_, _) => Task.FromResult(Result);
        internal Func<CreatureDisplayMetadataRequest, Func<CreaturesPreparationProgress, Task>, CancellationToken, Task<CreatureDisplayMetadataResult>> Prepare = (_, _, _) => Task.FromResult(Result);
        public Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request, CancellationToken token)
        { Interlocked.Increment(ref Reads); return Read(request, token); }
        public Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Prepares); var producer = Prepare(request, progress, token);
            if (!ProducerTasks.Writer.TryWrite(producer)) throw new InvalidOperationException("Authored producer-task capture exceeded two unobserved registrations.");
            return producer;
        }
    }
}
