using System.Collections;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsStoreLifetimeTests
{
    [Fact]
    public async Task StoreShutdownWithdrawsOnlyItsSharedPrerequisiteConsumersAndPreservesExternalWork()
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false);
        var emitter = new TaskCompletionSource<Func<string, Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal(); var canceled = 0;
        f.DuringPrepare = async (_, report, token) =>
        { using var registration = token.Register(() => Interlocked.Increment(ref canceled)); emitter.TrySetResult(report); await release.Task; };
        var context = f.Selection.Context;
        var external = f.H.Tables.PrepareAsync(new(context.ProductId, context.BuildIdentity, context.SourceRegion, "00cb674f", context.ResearchLocale), null, default);
        var joined = new[] { Signal(), Signal() };
        var own = Enumerable.Range(0, 2).Select(i => f.Store.PrepareLinksAsync(f.Selection, _ => { joined[i].TrySetResult(); return Task.CompletedTask; }, default)).ToArray();
        try
        {
            var emit = await emitter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!joined.All(signal => signal.Task.IsCompleted))
            { deadline.Token.ThrowIfCancellationRequested(); await emit("joined"); await Task.Yield(); }
            Assert.Equal(0, f.Store.Pressure.Queries); Assert.Equal(0, f.Store.Pressure.BridgePins);
            await f.Store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var task in own) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, canceled); Assert.False(external.IsCompleted);
            Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
            release.TrySetResult(); Assert.Equal("00cb674f", (await external).TableId);
            Assert.Single(f.Prepared); await f.Reopen();
            Assert.Equal(ItemEffectsReadinessState.NotPrepared, (await f.Store.ReadAsync(f.Selection, default)).State);
        }
        finally
        {
            release.TrySetResult(); foreach (var task in own) try { await task; } catch (OperationCanceledException) { }
            await external;
        }
    }

    [Fact]
    public async Task SingleSharedCpuGateNeverSpansNestedCasRowsOrAwaitedCatalogWork()
    {
        await using var f = await ItemEffectsStoreFixture.Create(singleCpu: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, deadline.Token);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), deadline.Token);
        var effect = f.Desired[ItemEffectsSourceRole.Effect];
        var names = await f.Store.ReadNamesAsync(new(f.Selection, ready.Snapshot.ArtifactId, effect.Artifact.ContentKey,
            page.Edges.Select(edge => edge.Key).ToArray()), deadline.Token);
        Assert.Contains(names.Outcomes, outcome => outcome.State == ItemEffectNameState.Ready);
        using var witness = await f.Resources.EnterCpuAsync(null, deadline.Token);
        Assert.Equal(0, f.Store.Pressure.Queries);
    }

    [Fact]
    public async Task SourceOnlyPromotionCancellationRetainsBuilderUntilActualNotificationAndThenCanRetry()
    {
        await using var f = await ItemEffectsStoreFixture.Create(); await f.Store.DisposeAsync();
        var entered = Signal(); var notified = Signal(); using var release = new ManualResetEventSlim(); var creates = 0;
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            new(Create: async token =>
            {
                if (Interlocked.Increment(ref creates) != 1) return await f.H.Objects.CreateStagingFileAsync(token);
                var stopped = Signal();
                using var registration = token.Register(() => { notified.TrySetResult(); stopped.TrySetCanceled(token); release.Wait(); throw new InvalidOperationException("Authored promotion notification."); });
                entered.TrySetResult(); await stopped.Task; throw new InvalidOperationException("Canceled factory cannot return a file.");
            }));
        Assert.Equal(ItemEffectsReadinessState.NotPrepared, (await f.Store.ReadAsync(f.Selection, default)).State);
        using var cancel = new CancellationTokenSource();
        var pending = f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await cancel.CancelAsync();
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted); Assert.True(f.Store.Pressure.Builder); Assert.Equal(1, f.Store.Pressure.BridgeSlots);
            Assert.Equal(ItemEffectsReadinessState.NotPrepared, (await f.Store.ReadAsync(f.Selection, default)).State);
            Assert.Equal(2, f.Store.Pressure.BridgeSlots);
            Assert.Equal("ITEM_EFFECTS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default))).Code);
            release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (f.Store.Pressure.Preparations != 0) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            var retry = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            Assert.Equal(ItemEffectsReadinessState.Ready, retry.State); Assert.False(f.Store.Pressure.Builder); Assert.Equal(2, creates);
        }
        finally { release.Set(); try { await pending; } catch (OperationCanceledException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningAndRetiringSourceSlotsStayChargedAndShutdownStartsEveryNotification(bool targetRole)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        ItemEffectDetailsRequest? details = null;
        if (targetRole)
        {
            var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot!.ArtifactId), default);
            details = new(f.Selection, links.Snapshot.ArtifactId, [page.Edges[0].Key]);
        }
        await f.Store.DisposeAsync();
        var opened = new[] { Signal(), Signal() }; var notified = new[] { Signal(), Signal() };
        var release = new[] { new ManualResetEventSlim(), new ManualResetEventSlim() }; var opens = 0;
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            openSource: async (source, token) =>
            {
                if (targetRole && source.Artifact.Header.TableHash.Equals("00cb674f", StringComparison.OrdinalIgnoreCase))
                    return await f.H.Objects.OpenVerifiedAsync(source.Source.Sha256, long.Parse(source.Source.EncodedBytes, System.Globalization.CultureInfo.InvariantCulture), f.Resources, null, token);
                var ordinal = Interlocked.Increment(ref opens) - 1;
                var input = await f.H.Objects.OpenVerifiedAsync(source.Source.Sha256,
                    long.Parse(source.Source.EncodedBytes, System.Globalization.CultureInfo.InvariantCulture), f.Resources, null, token);
                var stopped = Signal();
                try
                {
                    using var registration = token.Register(() =>
                    {
                        notified[ordinal].TrySetResult(); stopped.TrySetCanceled(token);
                        release[ordinal].Wait();
                        if (ordinal == 0) throw new InvalidOperationException("Authored notification failure.");
                    });
                    opened[ordinal].TrySetResult(); await stopped.Task; return input;
                }
                catch { await input.DisposeAsync(); throw; }
            });
        using var cancel = new CancellationTokenSource();
        Task Run(CancellationToken token) => targetRole ? f.Store.ReadDetailsAsync(details!, token) : f.Store.ReadAsync(f.Selection, token);
        int Slots() => targetRole ? f.Store.Pressure.TargetSlots : f.Store.Pressure.BridgeSlots;
        var first = Run(cancel.Token); Task? second = null; Task? stopping = null;
        try
        {
            await opened[0].Task.WaitAsync(TimeSpan.FromSeconds(5)); await cancel.CancelAsync();
            await notified[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted); Assert.Equal(1, Slots());
            if (targetRole) await f.Publish(ItemEffectsSourceRole.Effect, await f.Source(ItemEffectsSourceRole.Effect, f.Rows[ItemEffectsSourceRole.Effect], variant: "second"));
            second = Run(default); await opened[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Slots());
            if (targetRole) await f.Publish(ItemEffectsSourceRole.Effect, await f.Source(ItemEffectsSourceRole.Effect, f.Rows[ItemEffectsSourceRole.Effect], variant: "third"));
            Assert.Equal("ITEM_EFFECTS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => Run(default))).Code);
            Assert.Equal(2, opens);
            stopping = f.Store.DisposeAsync().AsTask();
            // The first callback remains held and will throw; peer cancellation still starts.
            await notified[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted); Assert.Equal(2, opens); Assert.False(second.IsCompleted);
            release[0].Set(); release[1].Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(first.IsCompleted); Assert.True(second.IsCompleted); Assert.Equal(0, Slots());
        }
        finally
        {
            foreach (var signal in release) signal.Set();
            try { await first; } catch (OperationCanceledException) { }
            if (second is not null) try { await second; } catch (OperationCanceledException) { }
            if (stopping is not null) await stopping;
            foreach (var signal in release) signal.Dispose();
        }
    }

    [Fact]
    public async Task SharedPrerequisiteProgressIsIndependentLatestOnlyAndTenHertz()
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false);
        await f.Store.DisposeAsync(); var clock = new ManualClock();
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, clock);
        var emitter = new TaskCompletionSource<Func<string, Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerRelease = Signal(); var firstRelease = Signal();
        var entered = new[] { Signal(), Signal() }; var second = new[] { Signal(), Signal() };
        var stages = new[] { new System.Collections.Concurrent.ConcurrentQueue<string>(), new System.Collections.Concurrent.ConcurrentQueue<string>() };
        f.DuringPrepare = async (_, report, _) => { emitter.TrySetResult(report); await producerRelease.Task; };
        var pending = Enumerable.Range(0, 2).Select(i => f.Store.PrepareLinksAsync(f.Selection, async progress =>
        {
            stages[i].Enqueue(progress.Stage); entered[i].TrySetResult();
            if (stages[i].Count == 1 && i == 0) await firstRelease.Task;
            if (stages[i].Count == 2) second[i].TrySetResult();
        }, default)).ToArray();
        try
        {
            var emit = await emitter.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!entered.All(signal => signal.Task.IsCompleted))
            { deadline.Token.ThrowIfCancellationRequested(); await emit("join"); await Task.Yield(); }
            for (var i = 0; i < 1000; i++) await emit("latest-" + i);
            Assert.All(stages, values => Assert.Single(values));
            firstRelease.TrySetResult();
            while (clock.Pending != 2) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            clock.Advance(TimeSpan.FromMilliseconds(99)); Assert.All(stages, values => Assert.Single(values));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            foreach (var signal in second) await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(stages, values => Assert.Equal(new[] { values.First(), "latest-999" }, values.ToArray()));
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            producerRelease.TrySetResult();
            foreach (var task in pending) Assert.Equal(ItemEffectsReadinessState.Ready, (await task).State);
            Assert.Single(f.Prepared);
        }
        finally
        {
            firstRelease.TrySetResult(); producerRelease.TrySetResult();
            foreach (var task in pending) try { await task; } catch (DiscoveryException) { }
        }
    }

    [Fact]
    public async Task DifferentItemsShareOnlyPhysicalBuildWhileEachConsumerCommitsItsOwnAuthority()
    {
        await using var f = await ItemEffectsStoreFixture.Create(twoItems: true);
        await f.Store.DisposeAsync();
        var entered = Signal(); var release = Signal(); var creates = 0;
        f.Store = new(f.H.Catalog, f.H.Objects, f.H.Store, f.H.Tables, f.Resources, TimeProvider.System,
            new(Create: async token => { Interlocked.Increment(ref creates); entered.TrySetResult(); await release.Task; return await f.H.Objects.CreateStagingFileAsync(token); }));
        var first = f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Task<ItemEffectsReadiness>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Sql("UPDATE items_admissions SET prepared_at=$v", "2026-04-05T06:07:08.0000000+00:00");
            second = f.Store.PrepareLinksAsync(f.Selection with { ItemId = "2" }, _ => Task.CompletedTask, default);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (f.Store.Pressure.BridgePins != 2) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            Assert.Equal(1, creates); Assert.Equal(1, f.Store.Pressure.BridgeSlots);
            release.TrySetResult();
            Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => first)).Code);
            var result = await second;
            Assert.Equal(ItemEffectsReadinessState.Ready, result.State); Assert.Equal("0", result.ObservedAssociations);
            Assert.Equal("2", result.Selection.ItemId); Assert.Equal(1, creates);
            Assert.NotNull(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
        }
        finally
        {
            release.TrySetResult(); try { await first; } catch (DiscoveryException) { }
            if (second is not null) try { await second; } catch (DiscoveryException) { }
        }
    }

    [Fact]
    public async Task DirectAdmissionPrecedesHostileRequestCaptureAndShutdownWaitsActualReturnedTasks()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var inputs = Enumerable.Range(0, 4).Select(_ => new HeldEdges()).ToArray();
        var pending = inputs.Select(input => f.Store.ReadDetailsAsync(new(f.Selection, new string('a', 64), input), default)).ToArray();
        Task? stopping = null;
        try
        {
            foreach (var input in inputs) await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, f.Store.Pressure.Operations); Assert.Equal(0, f.Store.Pressure.Queries);
            var refused = new HeldEdges();
            Assert.Equal("ITEM_EFFECTS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.ReadDetailsAsync(new(f.Selection, new string('a', 64), refused), default))).Code);
            Assert.Equal(0, refused.CountReads);
            stopping = f.Store.DisposeAsync().AsTask(); Assert.False(stopping.IsCompleted);
            foreach (var input in inputs) input.Release.Set();
            foreach (var operation in pending) Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => operation)).Code);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(pending, operation => Assert.True(operation.IsCompleted));
            Assert.Equal(0, f.Store.Pressure.Operations); Assert.All(inputs, input => Assert.Equal(1, input.CountReads));
        }
        finally
        {
            foreach (var input in inputs) input.Release.Set();
            foreach (var operation in pending) try { await operation; } catch (DiscoveryException) { }
            if (stopping is not null) await stopping;
            foreach (var input in inputs) input.Release.Dispose();
        }
    }

    [Fact]
    public async Task ClosingProgressCallbacksKeepBothPreparationCreditsWithoutBlockingIndexOrLocalReads()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var entered = new[] { Signal(), Signal() }; var release = new[] { Signal(), Signal() };
        var pending = Enumerable.Range(0, 2).Select(i => f.Store.PrepareLinksAsync(f.Selection, async _ =>
        { entered[i].TrySetResult(); await release[i].Task; }, default)).ToArray();
        Task? stopping = null;
        try
        {
            foreach (var signal in entered) await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, deadline.Token) is null)
            { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            Assert.All(pending, operation => Assert.False(operation.IsCompleted));
            Assert.Equal(2, f.Store.Pressure.Preparations);
            Assert.Equal("ITEM_EFFECTS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default))).Code);
            Assert.Equal(ItemEffectsReadinessState.Ready, (await f.Store.ReadAsync(f.Selection, default)).State);
            stopping = f.Store.DisposeAsync().AsTask(); Assert.False(stopping.IsCompleted);
            foreach (var signal in release) signal.TrySetResult();
            foreach (var operation in pending) try { await operation; } catch (OperationCanceledException) { }
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(pending, operation => Assert.True(operation.IsCompleted));
            Assert.Equal(0, f.Store.Pressure.Preparations); Assert.Equal(0, f.Store.Pressure.Operations);
        }
        finally
        {
            foreach (var signal in release) signal.TrySetResult();
            foreach (var operation in pending) try { await operation; } catch (OperationCanceledException) { }
            if (stopping is not null) await stopping;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        internal int Pending { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer; }
        internal void Advance(TimeSpan amount)
        {
            Timer[] ready; lock (gate) { ticks += amount.Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToArray(); foreach (var timer in ready) timer.Due = long.MaxValue; }
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
    private sealed class HeldEdges : IReadOnlyList<ItemEffectsEdgeKey>
    {
        internal readonly TaskCompletionSource Entered = Signal();
        internal readonly ManualResetEventSlim Release = new();
        internal int CountReads;
        public int Count { get { Interlocked.Increment(ref CountReads); Entered.TrySetResult(); Release.Wait(); throw new InvalidOperationException("Authored hostile collection."); } }
        public ItemEffectsEdgeKey this[int index] => throw new InvalidOperationException();
        public IEnumerator<ItemEffectsEdgeKey> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
