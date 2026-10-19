using System.Collections;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class SharedAssetContextsTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static Task Ignore(string _) => Task.CompletedTask;

    [Fact]
    public async Task SamePinnedOpeningIsSingleFlightAndIdleIconHandleDoesNotBlockModelOrSharedCache()
    {
        await using var fixture = new Fixture();
        var entered = NewSignal(); var release = NewSignal();
        fixture.BeforePrepare = async _ => { entered.TrySetResult(); await release.Task; };
        var icons = fixture.Open(); await entered.Task.WaitAsync(Deadline);
        var models = fixture.Open(); release.SetResult();
        await using var iconHandle = await icons.WaitAsync(Deadline);
        await using var modelHandle = await models.WaitAsync(Deadline);
        Assert.NotSame(iconHandle, modelHandle); Assert.Equal(1, fixture.Preparations);
        var result = await modelHandle.ResolveAsync(new uint[] { 1 }, Ignore);
        Assert.Equal(AssetResolutionStatus.Ready, Assert.Single(result.Results).Status);
        var source = Assert.Single(fixture.Sources);
        Assert.Equal(1, source.Root.Passes);
        Assert.False((await iconHandle.ResolveAsync(new uint[] { 1 }, Ignore)).ScannedRoot);
        Assert.Equal(1, source.Root.Passes);
        await iconHandle.DisposeAsync();
        Assert.True(source.Root.CanRead);
        Assert.Equal(2U, Assert.Single((await modelHandle.ResolveAsync(new uint[] { 2 }, Ignore)).Results).FileDataId);
    }

    [Fact]
    public async Task CancelOneOpeningConsumerKeepsSingleFlightAndSurvivingFeatureAlive()
    {
        await using var fixture = new Fixture();
        var entered = NewSignal(); var release = NewSignal();
        fixture.BeforePrepare = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var stop = new CancellationTokenSource();
        var first = fixture.Open(token: stop.Token); await entered.Task.WaitAsync(Deadline);
        var second = fixture.Open(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        Assert.False(second.IsCompleted); release.SetResult();
        await using var surviving = await second.WaitAsync(Deadline);
        Assert.Equal(1, fixture.Preparations);
        Assert.Single((await surviving.ResolveAsync(new uint[] { 1 }, Ignore)).Results);
    }

    [Fact]
    public async Task ActiveSubsetJoinsFrozenBatchButNewTargetsWaitForAnotherScanAndInputsAreCopied()
    {
        await using var fixture = new Fixture();
        await using var firstHandle = await fixture.Open(); await using var secondHandle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var read = source.Root.BlockNextRead();
        var supplied = new uint[] { 2, 1, 1 };
        var first = firstHandle.ResolveAsync(supplied, Ignore); await read.Entered.Task.WaitAsync(Deadline);
        supplied[0] = 99;
        var subset = secondHandle.ResolveAsync(new uint[] { 1 }, Ignore);
        var later = secondHandle.ResolveAsync(new uint[] { 3 }, Ignore);
        read.Release.SetResult();
        Assert.Equal(new uint[] { 1, 2 }, (await first.WaitAsync(Deadline)).Results.Select(value => value.FileDataId));
        Assert.Equal(1U, Assert.Single((await subset.WaitAsync(Deadline)).Results).FileDataId);
        Assert.Equal(3U, Assert.Single((await later.WaitAsync(Deadline)).Results).FileDataId);
        Assert.Equal(2, source.Root.Passes);
    }

    [Fact]
    public async Task CanceledJoinedBatchConsumerAndObserverFailureDoNotCancelSurvivor()
    {
        await using var fixture = new Fixture();
        await using var firstHandle = await fixture.Open(); await using var secondHandle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var read = source.Root.BlockNextRead();
        using var stop = new CancellationTokenSource();
        var first = firstHandle.ResolveAsync(new uint[] { 1, 2 }, Ignore, stop.Token);
        await read.Entered.Task.WaitAsync(Deadline);
        var survivor = secondHandle.ResolveAsync(new uint[] { 1 }, Ignore);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        Assert.False(survivor.IsCompleted); read.Release.SetResult();
        Assert.Single((await survivor.WaitAsync(Deadline)).Results); Assert.Equal(1, source.Root.Passes);

        var secondRead = source.Root.BlockNextRead(); var failObserver = NewSignal();
        var failed = firstHandle.ResolveAsync(new uint[] { 3, 4 }, async _ => { await failObserver.Task; throw new IOException("Authored observer failure."); });
        await secondRead.Entered.Task.WaitAsync(Deadline);
        var survivingAgain = secondHandle.ResolveAsync(new uint[] { 4 }, Ignore);
        failObserver.SetResult(); secondRead.Release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => failed.WaitAsync(Deadline));
        Assert.Single((await survivingAgain.WaitAsync(Deadline)).Results);
        Assert.Equal(2, source.Root.Passes);
    }

    [Fact]
    public async Task BlockedObserverRetainsItsSubscriptionButNotRootOrCpuCapacity()
    {
        await using var fixture = new Fixture();
        await using var icons = await fixture.Open(); await using var model = await fixture.Open();
        var entered = NewSignal(); var release = NewSignal();
        var slow = icons.ResolveAsync(new uint[] { 1 }, async _ => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(Deadline);
        var result = await model.ResolveAsync(new uint[] { 2 }, Ignore).WaitAsync(Deadline);
        Assert.Single(result.Results); Assert.False(slow.IsCompleted);
        Assert.Equal(2, Assert.Single(fixture.Sources).Root.Passes);
        release.SetResult(); await slow.WaitAsync(Deadline);
    }

    [Fact]
    public async Task GlobalSubscriptionLimitCountsJoinedAndStoppingConsumersUntilCallbacksRetire()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var read = source.Root.BlockNextRead();
        var observerEntered = NewSignal(); var observerRelease = NewSignal();
        using var stop = new CancellationTokenSource();
        var first = handle.ResolveAsync(new uint[] { 1 }, async _ => { observerEntered.TrySetResult(); await observerRelease.Task; }, stop.Token);
        await read.Entered.Task.WaitAsync(Deadline); await observerEntered.Task.WaitAsync(Deadline);
        var joined = Enumerable.Range(0, 7).Select(_ => handle.ResolveAsync(new uint[] { 1 }, Ignore)).ToArray();
        stop.Cancel();
        var capacity = await Assert.ThrowsAsync<DiscoveryException>(() => handle.ResolveAsync(new uint[] { 2 }, Ignore));
        Assert.Equal("ASSET_BATCH_CAPACITY", capacity.Code); Assert.True(capacity.Retryable); Assert.False(first.IsCompleted);
        observerRelease.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        var admitted = handle.ResolveAsync(new uint[] { 2 }, Ignore);
        read.Release.SetResult(); await Task.WhenAll(joined).WaitAsync(Deadline); await admitted.WaitAsync(Deadline);
        Assert.Equal(2, source.Root.Passes);
    }

    [Fact]
    public async Task LastConsumerCancellationAndLastHandleDisposalWaitForActualReadRetirement()
    {
        await using var fixture = new Fixture(); var handle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var read = source.Root.BlockNextRead();
        using var stop = new CancellationTokenSource();
        var batch = handle.ResolveAsync(new uint[] { 1 }, Ignore, stop.Token);
        await read.Entered.Task.WaitAsync(Deadline); stop.Cancel();
        var disposing = handle.DisposeAsync().AsTask();
        Assert.False(batch.IsCompleted); Assert.False(disposing.IsCompleted); Assert.True(source.Root.CanRead);
        read.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.WaitAsync(Deadline));
        await disposing.WaitAsync(Deadline); Assert.False(source.Root.CanRead); Assert.False(source.Encoding.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => handle.ResolveAsync(new uint[] { 1 }, Ignore));
    }

    [Fact]
    public async Task TwoExactContextsStayPinnedAndThirdIsRejectedUntilActualDrain()
    {
        await using var fixture = new Fixture();
        var a = await fixture.Open("a"); await using var b = await fixture.Open("b");
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open("c"));
        Assert.Equal("ASSET_CONTEXT_CAPACITY", failure.Code);
        Assert.NotEqual(a.SourceContext.SourceReference, b.SourceContext.SourceReference);
        var first = await a.ResolveAsync(new uint[] { 1 }, Ignore);
        var second = await b.ResolveAsync(new uint[] { 1 }, Ignore);
        Assert.Equal("a", first.Results[0].Association.SourceReference.Generation);
        Assert.Equal("b", second.Results[0].Association.SourceReference.Generation);
        Assert.Equal(2, fixture.Preparations);
        await a.DisposeAsync(); await using var c = await fixture.Open("c"); Assert.Equal(3, fixture.Preparations);
    }

    [Fact]
    public async Task EightFeatureSlotsIncludeOpeningAndDisposingHandles()
    {
        await using var fixture = new Fixture(); var entered = NewSignal(); var release = NewSignal();
        fixture.BeforePrepare = async _ => { entered.TrySetResult(); await release.Task; };
        var opening = Enumerable.Range(0, 8).Select(_ => fixture.Open()).ToArray();
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal("ASSET_HANDLE_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open())).Code);
        release.SetResult(); var handles = await Task.WhenAll(opening).WaitAsync(Deadline);
        var source = Assert.Single(fixture.Sources); var read = source.Root.BlockNextRead();
        var running = handles[0].ResolveAsync(new uint[] { 1 }, Ignore);
        await read.Entered.Task.WaitAsync(Deadline); var disposing = handles[0].DisposeAsync().AsTask();
        Assert.Equal("ASSET_HANDLE_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open())).Code);
        read.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Deadline));
        await disposing.WaitAsync(Deadline); await using var replacement = await fixture.Open();
        foreach (var handle in handles) await handle.DisposeAsync();
    }

    [Fact]
    public async Task PendingTailCoalescesWithin128AndEarlierOtherContextTurnCannotBeOvertaken()
    {
        await using var fixture = new Fixture(); await using var a = await fixture.Open("a"); await using var b = await fixture.Open("b");
        var firstSource = fixture.Sources[0]; var read = firstSource.Root.BlockNextRead();
        var first = a.ResolveAsync(new uint[] { 1 }, Ignore); await read.Entered.Task.WaitAsync(Deadline);
        var order = new List<string>();
        firstSource.Root.OnPass = () => order.Add("a"); fixture.Sources[1].Root.OnPass = () => order.Add("b");
        var nextA = a.ResolveAsync(new uint[] { 2 }, Ignore);
        var nextB = b.ResolveAsync(new uint[] { 1 }, Ignore);
        var tailA = a.ResolveAsync(new uint[] { 3 }, Ignore);
        var coalesced = a.ResolveAsync(new uint[] { 4 }, Ignore);
        read.Release.SetResult(); await Task.WhenAll(first, nextA, nextB, tailA, coalesced).WaitAsync(Deadline);
        Assert.Equal(new[] { "a", "b", "a" }, order); Assert.Equal(3, firstSource.Root.Passes);
        var before = firstSource.Root.Passes;
        var blocker = fixture.Registry.RootTurnAsync("grammar", async _ => { await Task.Yield(); return true; }, default);
        await blocker;
        var ids = Enumerable.Range(10, 128).Select(value => (uint)value).ToArray();
        var full = a.ResolveAsync(ids, Ignore); var outside = a.ResolveAsync(new uint[] { 500 }, Ignore);
        await Task.WhenAll(full, outside).WaitAsync(Deadline); Assert.Equal(before + 2, firstSource.Root.Passes);
    }

    [Fact]
    public async Task ColdGrammarAndLookupUseSameFifoButColdNetworkWaitHoldsNoTraversalTurn()
    {
        await using var fixture = new Fixture(); await using var ready = await fixture.Open("a");
        var networkEntered = NewSignal(); var networkRelease = NewSignal();
        fixture.BeforePrepare = async ct => { networkEntered.TrySetResult(); await networkRelease.Task.WaitAsync(ct); };
        var cold = fixture.Open("b"); await networkEntered.Task.WaitAsync(Deadline);
        Assert.Single((await ready.ResolveAsync(new uint[] { 1 }, Ignore).WaitAsync(Deadline)).Results);
        fixture.GrammarQueued = NewSignal();
        var rootRead = fixture.Sources[0].Root.BlockNextRead();
        var lookup = ready.ResolveAsync(new uint[] { 2 }, Ignore); await rootRead.Entered.Task.WaitAsync(Deadline);
        networkRelease.SetResult(); await fixture.GrammarQueued.Task.WaitAsync(Deadline);
        Assert.False(cold.IsCompleted); rootRead.Release.SetResult();
        await lookup.WaitAsync(Deadline); await using var opened = await cold.WaitAsync(Deadline);
        Assert.Equal(1, fixture.MaximumConcurrentPreparations);
    }

    [Fact]
    public async Task CacheHitsNeedNoTurnAndEmptySortedAndLyingCountInputsPreserveBounds()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        await handle.ResolveAsync(new uint[] { 1 }, Ignore);
        var read = Assert.Single(fixture.Sources).Root.BlockNextRead();
        var busy = handle.ResolveAsync(new uint[] { 2 }, Ignore); await read.Entered.Task.WaitAsync(Deadline);
        Assert.False((await handle.ResolveAsync(new uint[] { 1 }, Ignore).WaitAsync(Deadline)).ScannedRoot);
        Assert.Empty((await handle.ResolveAsync(Array.Empty<uint>(), Ignore)).Results);
        Assert.Equal("ASSET_BATCH_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => handle.ResolveAsync(new LyingCount(), Ignore))).Code);
        read.Release.SetResult(); await busy.WaitAsync(Deadline);
    }

    [Fact]
    public async Task ShutdownDuringOpeningWaitsCleanupAndClosesAdmissions()
    {
        await using var fixture = new Fixture(); var entered = NewSignal(); var release = NewSignal();
        fixture.BeforePrepare = async _ => { entered.TrySetResult(); await release.Task; };
        var opening = fixture.Open(); await entered.Task.WaitAsync(Deadline);
        var shutdown = fixture.Registry.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        await shutdown.WaitAsync(Deadline);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Open());
    }

    [Fact]
    public async Task CancellationAtRootEofCannotCacheProvisionalSuccessOrAbsence()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var eof = source.Root.BlockAtEof();
        using var stop = new CancellationTokenSource();
        var resolving = handle.ResolveAsync(new uint[] { 1, 99 }, Ignore, stop.Token);
        await eof.Entered.Task.WaitAsync(Deadline); stop.Cancel();
        await eof.Canceled.Task.WaitAsync(Deadline); Assert.False(resolving.IsCompleted);
        eof.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(Deadline));
        var retry = await handle.ResolveAsync(new uint[] { 1, 99 }, Ignore);
        Assert.True(retry.ScannedRoot); Assert.Equal(2, source.Root.Passes);
        Assert.Equal(new[] { AssetResolutionStatus.Ready, AssetResolutionStatus.MissingMembership }, retry.Results.Select(value => value.Status));
    }

    [Fact]
    public async Task CancellationAfterCacheCommitKeepsProvenCacheButWaitsItsOwnObserver()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        var source = Assert.Single(fixture.Sources); var entered = NewSignal(); var release = NewSignal();
        using var stop = new CancellationTokenSource();
        var resolving = handle.ResolveAsync(new uint[] { 1 }, async _ => { entered.TrySetResult(); await release.Task; }, stop.Token);
        await entered.Task.WaitAsync(Deadline);
        // The same-ID request either joins the active frozen batch or reads its completed cache;
        // either outcome proves the first scan committed before initiating cancellation.
        await handle.ResolveAsync(new uint[] { 1 }, Ignore).WaitAsync(Deadline);
        stop.Cancel(); Assert.False(resolving.IsCompleted); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(Deadline));
        Assert.False((await handle.ResolveAsync(new uint[] { 1 }, Ignore)).ScannedRoot); Assert.Equal(1, source.Root.Passes);
    }

    [Fact]
    public async Task CanceledPendingBatchRetiresWithoutWaitingForUnrelatedActiveRead()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        var read = Assert.Single(fixture.Sources).Root.BlockNextRead();
        var active = handle.ResolveAsync(new uint[] { 1 }, Ignore); await read.Entered.Task.WaitAsync(Deadline);
        using var stop = new CancellationTokenSource(); var queued = handle.ResolveAsync(new uint[] { 2 }, Ignore, stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
        Assert.False(active.IsCompleted); read.Release.SetResult(); await active.WaitAsync(Deadline);
        Assert.Equal(1, Assert.Single(fixture.Sources).Root.Passes);
    }

    [Fact]
    public async Task DrainingSourceStillOwnsContextAndFeatureAdmissionsUntilLeaseDisposalCompletes()
    {
        await using var fixture = new Fixture(); var a = await fixture.Open("a"); await using var b = await fixture.Open("b");
        var cleanup = fixture.Sources[0].Root.BlockDisposal();
        var retiring = a.DisposeAsync().AsTask(); await cleanup.Entered.Task.WaitAsync(Deadline);
        Assert.Equal("ASSET_CONTEXT_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open("c"))).Code);
        Assert.Equal("ASSET_CONTEXT_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open("a"))).Code);
        cleanup.Release.SetResult(); await retiring.WaitAsync(Deadline); await using var c = await fixture.Open("c");
    }

    [Fact]
    public async Task ProgressKeepsOnlyRunningAndLatestUpdateAndDrainOwnsCallbackLifetime()
    {
        var entered = NewSignal(); var release = NewSignal(); var updates = new List<string>();
        using var observer = new AssetProgressObserver(async stage =>
        {
            updates.Add(stage);
            if (stage == "first") { entered.SetResult(); await release.Task; }
        });
        await observer.PostAsync("first"); await entered.Task.WaitAsync(Deadline);
        for (var index = 0; index < 1000; index++) await observer.PostAsync(index.ToString());
        var draining = observer.CloseAsync(discard: false); Assert.False(draining.IsCompleted);
        release.SetResult(); await draining.WaitAsync(Deadline); Assert.Equal(new[] { "first", "999" }, updates);
    }

    [Fact]
    public async Task CallerDeadlineWithdrawsOnlyItsJoinedSubscription()
    {
        await using var fixture = new Fixture(); await using var handle = await fixture.Open();
        var read = Assert.Single(fixture.Sources).Root.BlockNextRead();
        var clock = new DeadlineClock(); using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1), clock);
        var timed = handle.ResolveAsync(new uint[] { 1, 2 }, Ignore, deadline.Token);
        await read.Entered.Task.WaitAsync(Deadline);
        var survivor = handle.ResolveAsync(new uint[] { 1 }, Ignore);
        clock.Expire(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timed.WaitAsync(Deadline));
        Assert.False(survivor.IsCompleted); read.Release.SetResult();
        Assert.Single((await survivor.WaitAsync(Deadline)).Results); Assert.Equal(1, Assert.Single(fixture.Sources).Root.Passes);
    }

    [Fact]
    public async Task OpeningAndBatchPoolsHaveSeparateBoundsAndColdPreparationIsSerialized()
    {
        await using var fixture = new Fixture(); await using var ready = await fixture.Open("a");
        var read = Assert.Single(fixture.Sources).Root.BlockNextRead();
        var batches = Enumerable.Range(0, 8).Select(_ => ready.ResolveAsync(new uint[] { 1 }, Ignore)).ToArray();
        await read.Entered.Task.WaitAsync(Deadline);
        var prepEntered = NewSignal(); var prepRelease = NewSignal();
        fixture.BeforePrepare = async ct => { prepEntered.TrySetResult(); await prepRelease.Task.WaitAsync(ct); };
        var openings = Enumerable.Range(0, 7).Select(_ => fixture.Open("b")).ToArray();
        await prepEntered.Task.WaitAsync(Deadline);
        Assert.Equal("ASSET_HANDLE_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open("b"))).Code);
        Assert.Equal("ASSET_BATCH_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => ready.ResolveAsync(new uint[] { 2 }, Ignore))).Code);
        Assert.Equal(1, fixture.MaximumConcurrentPreparations);
        read.Release.SetResult(); await Task.WhenAll(batches).WaitAsync(Deadline);
        prepRelease.SetResult(); var handles = await Task.WhenAll(openings).WaitAsync(Deadline);
        foreach (var handle in handles) await handle.DisposeAsync();
    }

    [Fact]
    public async Task FailedLeaseCleanupRemainsChargedAndShutdownReportsTheFailure()
    {
        var fixture = new Fixture();
        var a = await fixture.Open("a"); var b = await fixture.Open("b");
        fixture.Sources[0].Root.FailDisposalAfterClose = true;
        await Assert.ThrowsAsync<IOException>(() => a.DisposeAsync().AsTask());
        Assert.Equal("ASSET_CONTEXT_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Open("c"))).Code);
        await Assert.ThrowsAsync<IOException>(() => fixture.Registry.DisposeAsync().AsTask());
        foreach (var source in fixture.Sources) source.Delete();
        // The faulted context was never re-admitted, even though this authored failure closed
        // its OS handle before reporting the uncertain retirement result.
        Assert.Equal(2, fixture.Preparations);
        GC.KeepAlive(b);
    }

    [Fact]
    public async Task ThrowingCancellationNotificationStillAwaitsOpeningCleanupAndShutdown()
    {
        await using var fixture = new Fixture(); var entered = NewSignal(); var canceled = NewSignal(); var release = NewSignal();
        fixture.BeforePrepare = async ct =>
        {
            using var notification = ct.Register(() => { canceled.TrySetResult(); throw new InvalidOperationException("Authored cancellation callback."); });
            entered.TrySetResult(); await release.Task; ct.ThrowIfCancellationRequested();
        };
        var opening = fixture.Open(); await entered.Task.WaitAsync(Deadline);
        var shutdown = fixture.Registry.DisposeAsync().AsTask(); await canceled.Task.WaitAsync(Deadline);
        Assert.False(shutdown.IsCompleted); Assert.False(opening.IsCompleted);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        await shutdown.WaitAsync(Deadline);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Open());
    }

    [Fact]
    public async Task ThrowingBatchCancellationCallbackCannotReleaseSubscriptionBeforeActualReadCleanup()
    {
        await using var fixture = new Fixture(); var handle = await fixture.Open();
        var read = Assert.Single(fixture.Sources).Root.BlockNextRead();
        fixture.Sources[0].Root.ThrowCancellationNotification = true;
        var resolving = handle.ResolveAsync(new uint[] { 1 }, Ignore); await read.Entered.Task.WaitAsync(Deadline);
        var disposal = handle.DisposeAsync().AsTask(); await read.Canceled.Task.WaitAsync(Deadline);
        Assert.False(disposal.IsCompleted); Assert.False(resolving.IsCompleted);
        read.Release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(Deadline));
        await disposal.WaitAsync(Deadline); Assert.False(fixture.Sources[0].Root.CanRead);
    }

    [Fact]
    public async Task DeadlineDisposalWaitsForActualCancellationNotificationRetirement()
    {
        using var source = new CancellationTokenSource();
        var clock = new DeadlineClock(); var entered = NewSignal(); var callbackGate = new object(); var release = false;
        using var callback = source.Token.Register(() =>
        {
            // CancellationToken callbacks are synchronous. This authored callback parks on a
            // controlled monitor solely to prove async ownership of callback retirement.
            entered.TrySetResult();
            lock (callbackGate) while (!release) Monitor.Wait(callbackGate);
        });
        var deadline = AssetCancellation.After(source, TimeSpan.FromMinutes(1), clock);
        clock.Expire(); await entered.Task.WaitAsync(Deadline);
        var retiring = deadline.DisposeAsync().AsTask();
        try { Assert.True(source.IsCancellationRequested); Assert.False(retiring.IsCompleted); }
        finally { lock (callbackGate) { release = true; Monitor.PulseAll(callbackGate); } }
        await retiring.WaitAsync(Deadline); await deadline.DisposeAsync();
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private DeadlineTimer? timer;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timer = new(callback, state);
        internal void Expire() => timer!.Fire();
        private sealed class DeadlineTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            internal void Fire() { if (!disposed) callback(state); }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class LyingCount : IReadOnlyCollection<uint>
    {
        public int Count => 1;
        public IEnumerator<uint> GetEnumerator() => Enumerable.Repeat(1U, 129).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly AssetResolutionRegistry Registry = new();
        internal readonly List<SourceFixture> Sources = [];
        internal Func<CancellationToken, Task>? BeforePrepare;
        internal int Preparations, MaximumConcurrentPreparations;
        private int activePreparations;
        internal TaskCompletionSource GrammarQueued = NewSignal();
        internal Task<AssetResolutionSession> Open(string generation = "a", Func<string, Task>? progress = null, CancellationToken token = default) =>
            Registry.OpenAsync(_ => Task.FromResult(Key(generation)), async (key, report, ct) =>
            {
                Preparations++; MaximumConcurrentPreparations = Math.Max(MaximumConcurrentPreparations, ++activePreparations);
                try
                {
                    if (BeforePrepare is not null) await BeforePrepare(ct);
                    await report("Preparing authored source"); ct.ThrowIfCancellationRequested();
                    var grammar = Registry.RootTurnAsync(key, _ => Task.FromResult(true), ct); GrammarQueued.TrySetResult(); await grammar;
                    var source = await SourceFixture.CreateAsync(key); Sources.Add(source); return source.Source;
                }
                finally { activePreparations--; }
            }, progress ?? Ignore, token);
        private static AssetSourceContextKey Key(string generation)
        {
            var data = InventoryFixture.Create();
            return new(new("wow", data.BuildIdentity, "us"), "enUS", new(generation, "us", new string('1', 64), 0, new string('2', 64), 0), data.BuildKey, data.CdnKey);
        }
        public async ValueTask DisposeAsync()
        { await Registry.DisposeAsync(); foreach (var source in Sources) source.Delete(); }
    }

    private sealed class SourceFixture
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "vibeminer-shared-assets-" + Guid.NewGuid().ToString("N"));
        internal CountingInput Root = null!, Encoding = null!;
        internal AssetResolutionSource Source = null!;
        internal static async Task<SourceFixture> CreateAsync(AssetSourceContextKey key)
        {
            var fixture = new SourceFixture(); Directory.CreateDirectory(fixture.path);
            var data = InventoryFixture.Create(); using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("TSFM"u8); writer.Write(24U); writer.Write(2U); writer.Write(4U); writer.Write(0U); writer.Write(0U);
                for (uint id = 1; id <= 4; id++)
                { writer.Write(1U); writer.Write(1U); writer.Write(VibeMiner.Formats.Wow.RootInventoryReader.NoNames); writer.Write(0U); writer.Write((byte)0); writer.Write(id); writer.Write(Convert.FromHexString(data.PayloadA.ContentKey)); }
            }
            var root = bytes.ToArray(); await File.WriteAllBytesAsync(Path.Combine(fixture.path, "root"), root);
            await File.WriteAllBytesAsync(Path.Combine(fixture.path, "encoding"), data.Metadata["encoding"].Decoded);
            fixture.Root = new(Path.Combine(fixture.path, "root")); fixture.Encoding = new(Path.Combine(fixture.path, "encoding"));
            var lookup = await EncodingFileLookup.OpenAsync(fixture.Encoding, new());
            const string now = "2026-10-19T00:00:00+00:00";
            MetadataReceipt Receipt(InventoryFixture.FixtureObject value) => new(value.Role, value.ContentKey, value.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(value.Encoded)), value.Encoded.Length.ToString(), value.Decoded.Length.ToString(), InventoryFixture.Url("data", value.EncodingKey), now, now, value.Role + "-v1");
            var enReceipt = Receipt(data.Metadata["encoding"]);
            var rootReceipt = Receipt(data.Metadata["root"]) with { ContentKey = Convert.ToHexStringLower(MD5.HashData(root)) };
            var context = new EncodedSourceContext("wow", data.BuildIdentity, "us", new string('3', 64), key.Reference);
            fixture.Source = new(new(enReceipt, lookup, fixture.Encoding, () => { }), fixture.Root,
                new(key.Request, key.Locale, key.Reference, [], enReceipt, rootReceipt, AssetResolutionSession.VariantPolicy, []),
                context, new(), default, () => { });
            return fixture;
        }
        internal void Delete() => Directory.Delete(path, true);
    }
    private sealed class CountingInput(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        65_536, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        internal int Passes;
        internal Action? OnPass;
        internal bool FailDisposalAfterClose, ThrowCancellationNotification;
        private (TaskCompletionSource Entered, TaskCompletionSource Release, TaskCompletionSource Canceled)? nextRead;
        private bool atEof;
        private (TaskCompletionSource Entered, TaskCompletionSource Release)? disposal;
        internal (TaskCompletionSource Entered, TaskCompletionSource Release, TaskCompletionSource Canceled) BlockNextRead()
        { var value = (NewSignal(), NewSignal(), NewSignal()); nextRead = value; atEof = false; return value; }
        internal (TaskCompletionSource Entered, TaskCompletionSource Release, TaskCompletionSource Canceled) BlockAtEof()
        { var value = BlockNextRead(); atEof = true; return value; }
        internal (TaskCompletionSource Entered, TaskCompletionSource Release) BlockDisposal()
        { var value = (NewSignal(), NewSignal()); disposal = value; return value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position == 0) { Passes++; OnPass?.Invoke(); }
            if (nextRead is { } read && (!atEof || Position == Length))
            {
                nextRead = null; read.Entered.SetResult();
                using var cancellation = token.Register(() =>
                {
                    read.Canceled.TrySetResult();
                    if (ThrowCancellationNotification) throw new InvalidOperationException("Authored read cancellation callback.");
                });
                await read.Release.Task;
            }
            return await base.ReadAsync(buffer, token);
        }
        public override async ValueTask DisposeAsync()
        {
            if (disposal is { } cleanup) { disposal = null; cleanup.Entered.SetResult(); await cleanup.Release.Task; }
            await base.DisposeAsync();
            if (FailDisposalAfterClose) throw new IOException("Authored uncertain lease retirement.");
        }
    }
}
