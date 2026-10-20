using System.Threading.Channels;
using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemClassificationsServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ItemsContext Context = new("wow", new('a', 64), "us", "enUS");
    private static ItemClassificationBatchRequest Request(params string[] ids) => new(Context, new('b', 64), ids);
    private static ItemClassificationBatch Result(ItemClassificationBatchRequest request) => new(request.Context, request.ArtifactId,
        request.PolicyRevision, [], request.ItemIds.Select(id => new ItemClassificationOutcome(id, true,
            new(ItemClassificationState.Unavailable, []), request.IncludeSubclass ? new(ItemClassificationState.Unsupported, []) : null)).ToArray());
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("")] [InlineData("01")] [InlineData("-1")] [InlineData("4294967296")] [InlineData("1\n")]
    public async Task Invalid_unsigned_ids_never_reach_store(string id)
    {
        var store = new Store(); await using var service = new ItemClassificationsService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request(id)))).Code);
        Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task Limits_context_and_read_capability_are_validated_before_store()
    {
        var store = new Store(); await using var service = new ItemClassificationsService(store);
        foreach (var request in new[] { Request("0") with { ArtifactId = new('B', 64) }, Request("1") with { Context = Context with { ResearchLocale = "frFR" } },
            Request(Enumerable.Repeat("1", 51).ToArray()), Request("1") with { ItemIds = null! } })
            await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(request));
        Assert.Equal(0, store.Calls);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request()));
        Assert.Equal(new[] { "0", "4294967295" }, (await service.PrepareBatchAsync(Request("0", "4294967295"))).Outcomes.Select(value => value.ItemId));
    }
    [Fact]
    public async Task Exactly_ordered_batches_share_producer_and_preserve_duplicates()
    {
        var store = new Store(); var release = Signal(); var entered = Signal();
        store.Prepare = async (request, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store);
        var first = service.PrepareBatchAsync(Request("2", "1", "2")); await entered.Task.WaitAsync(Deadline);
        var second = service.PrepareBatchAsync(Request("2", "1", "2")); Assert.Equal(1, store.Calls);
        release.SetResult(); Assert.Same(await first, await second);
        Assert.Equal(new[] { "2", "1", "2" }, (await first).Outcomes.Select(value => value.ItemId));
    }
    [Fact]
    public async Task Admission_clones_mutable_caller_ids_and_reversed_order_is_a_distinct_queued_demand()
    {
        var calls = Channel.CreateUnbounded<Call>(); var store = new Store();
        store.Prepare = async (request, _, token) => { var call = new Call(request); await calls.Writer.WriteAsync(call, token); await call.Release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store);
        var ids = new[] { "2", "1", "2" }; var first = service.PrepareBatchAsync(Request(ids));
        var call = await calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline); ids[0] = "9";
        var next = service.PrepareBatchAsync(Request("1", "2", "2")); Assert.Equal(1, store.Calls);
        call.Release.SetResult(); Assert.Equal("2", (await first).Outcomes[0].ItemId);
        var secondCall = await calls.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(new[] { "1", "2", "2" }, secondCall.Request.ItemIds); secondCall.Release.SetResult(); await next;
    }
    [Fact]
    public async Task Four_distinct_batches_and_eight_shared_consumers_bound_all_waiters()
    {
        var entered = Signal(); var release = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store);
        var consumers = Enumerable.Range(0, 5).Select(_ => service.PrepareBatchAsync(Request("1"))).ToArray();
        await entered.Task.WaitAsync(Deadline);
        var queued = new[] { "2", "3", "4" }.Select(id => service.PrepareBatchAsync(Request(id))).ToArray();
        Assert.Equal("ITEM_CLASSIFICATIONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("1")))).Code);
        Assert.Equal("ITEM_CLASSIFICATIONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("5")))).Code);
        Assert.Equal(1, store.Calls); release.SetResult(); await Task.WhenAll(consumers.Concat(queued)).WaitAsync(Deadline); Assert.Equal(4, store.Calls);
    }
    [Fact]
    public async Task Canceling_one_shared_consumer_preserves_other_consumer_and_producer()
    {
        var entered = Signal(); var release = Signal(); var store = new Store(); CancellationToken producer = default;
        store.Prepare = async (request, _, token) => { producer = token; entered.SetResult(); await release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        var first = service.PrepareBatchAsync(Request("1"), token: cancellation.Token); await entered.Task.WaitAsync(Deadline);
        var survivor = service.PrepareBatchAsync(Request("1")); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(producer.IsCancellationRequested);
        release.SetResult(); await survivor.WaitAsync(Deadline); Assert.Equal(1, store.Calls);
    }
    [Fact]
    public async Task Final_consumer_waits_actual_cleanup_before_next_batch_can_start()
    {
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) =>
        { entered.TrySetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupStarted.TrySetResult(); await cleanup.Task; } return Result(request); };
        await using var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        var first = service.PrepareBatchAsync(Request("1"), token: cancellation.Token); await entered.Task.WaitAsync(Deadline); cancellation.Cancel();
        await cleanupStarted.Task.WaitAsync(Deadline); Assert.False(first.IsCompleted);
        Assert.Equal("ITEM_CLASSIFICATIONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("1")))).Code);
        store.Prepare = (request, _, _) => Task.FromResult(Result(request)); var next = service.PrepareBatchAsync(Request("2"));
        Assert.Equal(1, store.Calls); cleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("2", Assert.Single((await next.WaitAsync(Deadline)).Outcomes).ItemId);
    }
    [Fact]
    public async Task Canceling_queued_demand_never_runs_that_store_request()
    {
        var entered = Signal(); var release = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store); var first = service.PrepareBatchAsync(Request("1")); await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource(); var queued = service.PrepareBatchAsync(Request("2"), token: cancellation.Token);
        cancellation.Cancel(); Assert.False(queued.IsCompleted); release.SetResult(); await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(1, store.Calls);
    }
    [Fact]
    public async Task Source_failure_releases_queue_and_failed_observer_does_not_fail_other_consumer()
    {
        var entered = Signal(); var release = Signal(); var store = new Store(); var reports = 0;
        store.Prepare = async (request, report, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); await report(new("Reading", "icons", 0, 1)); return Result(request); };
        await using var service = new ItemClassificationsService(store);
        var first = service.PrepareBatchAsync(Request("1"), _ => Task.FromException(new IOException("authored observer")));
        await entered.Task.WaitAsync(Deadline); var second = service.PrepareBatchAsync(Request("1"), _ => { reports++; return Task.CompletedTask; });
        release.SetResult(); await Task.WhenAll(first, second); Assert.Equal(1, reports);
        var failure = new DiscoveryException("AUTHORED_FAILURE", "authored"); store.Prepare = (_, _, _) => Task.FromException<ItemClassificationBatch>(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("2"))));
        store.Prepare = (request, _, _) => Task.FromResult(Result(request)); await service.PrepareBatchAsync(Request("2"));
    }
    [Fact]
    public async Task Concurrent_progress_is_coalesced_and_late_progress_is_fenced()
    {
        var entered = Signal(); var coalesced = Signal(); var release = Signal(); var count = 0;
        Func<ItemClassificationProgress, Task>? late = null; var store = new Store();
        store.Prepare = async (request, report, _) =>
        { late = report; var active = report(new("Resolving", "icons", 0, 2)); await entered.Task; await report(new("Resolving", "icons", 1, 2)); coalesced.SetResult(); await active; return Result(request); };
        await using var service = new ItemClassificationsService(store);
        var loading = service.PrepareBatchAsync(Request("1"), async _ => { count++; entered.SetResult(); await release.Task; });
        await coalesced.Task.WaitAsync(Deadline); release.SetResult(); await loading.WaitAsync(Deadline);
        await late!(new("Resolving", "icons", 2, 2)); Assert.Equal(1, count);
    }
    [Fact]
    public async Task Shutdown_cancels_running_and_queued_demands_waits_cleanup_and_refuses_reads()
    {
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) => { entered.SetResult(); try { await never.Task.WaitAsync(token); } finally { cleanupStarted.SetResult(); await cleanup.Task; } return Result(request); };
        var service = new ItemClassificationsService(store); var first = service.PrepareBatchAsync(Request("1")); await entered.Task.WaitAsync(Deadline);
        var queued = service.PrepareBatchAsync(Request("2")); var stopping = service.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(stopping.IsCompleted); Assert.False(first.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("3")))).Code);
        cleanup.SetResult(); await stopping.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(1, store.Calls); await service.DisposeAsync();
    }
    [Fact]
    public async Task Shutdown_never_admits_a_queued_demand_while_it_is_still_canceling_earlier_demands()
    {
        // Regression for #22. Shutdown cancels its demands one at a time. The running producer can
        // retire before shutdown reaches the queued demand's token; the worker must not admit it.
        var prior = SynchronizationContext.Current; var context = new QueuedContext();
        var entered = Signal(); var notified = Signal(); using var shutdownReturned = new ManualResetEventSlim();
        var queuedEntered = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) =>
        {
            if (request.ItemIds[0] != "1") { queuedEntered.TrySetResult(); return Result(request); }
            // Not disposed here: disposing waits for a running callback. The service owns the source.
            token.Register(() => { notified.TrySetResult(); shutdownReturned.Wait(Deadline); });
            entered.SetResult(); await notified.Task; token.ThrowIfCancellationRequested(); return Result(request);
        };
        var service = new ItemClassificationsService(store); var first = service.PrepareBatchAsync(Request("1"));
        await entered.Task.WaitAsync(Deadline); var queued = service.PrepareBatchAsync(Request("2")); Task stopping;
        SynchronizationContext.SetSynchronizationContext(context);
        // The callback holds the first cancellation open until DisposeAsync has yielded, so its
        // continuation is parked on the queued context before it can cancel the second demand.
        try { stopping = service.DisposeAsync().AsTask(); }
        finally { SynchronizationContext.SetSynchronizationContext(prior); shutdownReturned.Set(); }
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Deadline));
            Assert.False(queuedEntered.Task.IsCompleted, "The queued demand reached the store after shutdown began.");
            Assert.False(stopping.IsCompleted);
        }
        finally { await context.DrainUntilAsync(stopping, Deadline); }
        Assert.Equal(1, store.Calls);
    }
    [Fact]
    public async Task Detaching_one_consumer_restores_global_capacity_across_four_batches()
    {
        var entered = Signal(); var release = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(request); };
        await using var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        var detached = service.PrepareBatchAsync(Request("1"), token: cancellation.Token);
        var remaining = Enumerable.Range(0, 4).Select(_ => service.PrepareBatchAsync(Request("1"))).ToList();
        await entered.Task.WaitAsync(Deadline);
        remaining.AddRange(new[] { "2", "3", "4" }.Select(id => service.PrepareBatchAsync(Request(id))));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("4")));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => detached);
        remaining.Add(service.PrepareBatchAsync(Request("4"))); Assert.Equal(1, store.Calls);
        release.SetResult(); await Task.WhenAll(remaining).WaitAsync(Deadline); Assert.Equal(4, store.Calls);
    }
    [Fact]
    public async Task Faulty_cancellation_callbacks_do_not_skip_last_consumer_or_shutdown_cleanup()
    {
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var notification = Signal(); var store = new Store();
        store.Prepare = async (request, _, token) =>
        {
            using var callback = token.Register(() => { notification.TrySetResult(); throw new InvalidOperationException("authored cancellation callback"); });
            // Only actual callback invocation releases this independent producer gate.
            entered.TrySetResult(); try { await notification.Task; token.ThrowIfCancellationRequested(); }
            finally { cleanupStarted.TrySetResult(); await cleanup.Task; } return Result(request);
        };
        var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        var first = service.PrepareBatchAsync(Request("1"), token: cancellation.Token); await entered.Task.WaitAsync(Deadline);
        try
        {
            var queued = service.PrepareBatchAsync(Request("2")); cancellation.Cancel(); await notification.Task.WaitAsync(Deadline);
            await cleanupStarted.Task.WaitAsync(Deadline);
            Assert.False(first.IsCompleted); var stopping = service.DisposeAsync().AsTask(); Assert.False(stopping.IsCompleted);
            cleanup.SetResult(); await stopping.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued); Assert.Equal(1, store.Calls);
        }
        finally { notification.TrySetResult(); cleanup.TrySetResult(); await service.DisposeAsync().AsTask().WaitAsync(Deadline); }
    }
    [Fact]
    public async Task Canceled_consumer_keeps_global_capacity_until_its_blocked_progress_observer_closes()
    {
        var prior = SynchronizationContext.Current; var context = new QueuedContext();
        var progressRelease = Signal(); var sourceRelease = Signal(); var callbackEntered = Signal();
        var store = new Store();
        store.Prepare = async (request, report, token) =>
        { await report(new("Reading", "icons", 0, 1)); await sourceRelease.Task.WaitAsync(token); return Result(request); };
        SynchronizationContext.SetSynchronizationContext(context);
        var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        Task<ItemClassificationBatch>? canceled = null, refused = null, recovered = null;
        Task<ItemClassificationBatch>[] survivors = []; Task? shutdown = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            canceled = service.PrepareBatchAsync(Request("1"), async _ => { callbackEntered.SetResult(); await progressRelease.Task; }, cancellation.Token);
            survivors = Enumerable.Range(0, 7).Select(_ => service.PrepareBatchAsync(Request("1"))).ToArray();
            context.Drain(); Assert.True(callbackEntered.Task.IsCompleted);
            cancellation.Cancel(); context.Drain(); Assert.False(canceled.IsCompleted);
            refused = service.PrepareBatchAsync(Request("1"));
            Assert.True(refused.IsFaulted, "The blocked closing observer must still reserve the eighth consumer slot.");
            progressRelease.SetResult(); context.Drain(); Assert.True(canceled.IsCompleted);
            recovered = service.PrepareBatchAsync(Request("1")); Assert.False(recovered.IsCompleted);
            sourceRelease.SetResult();
            SynchronizationContext.SetSynchronizationContext(prior);
            // Finish the surviving consumers before disposing their service; releasing the
            // source gate does not establish that its asynchronous continuations have retired.
            await context.DrainUntilAsync(Task.WhenAll(survivors.Append(recovered)), Deadline);
        }
        finally
        {
            progressRelease.TrySetResult(); sourceRelease.TrySetResult(); context.Drain();
            shutdown = service.DisposeAsync().AsTask(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(prior);
        }
        await context.DrainUntilAsync(shutdown, Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled!);
        Assert.Equal("ITEM_CLASSIFICATIONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => refused!)).Code);
        await shutdown.WaitAsync(Deadline);
        Assert.Equal(1, store.Calls);
    }
    [Fact]
    public async Task Last_canceling_consumer_keeps_capacity_until_producer_cleanup_retires()
    {
        var prior = SynchronizationContext.Current; var context = new QueuedContext();
        var cleanupStarted = Signal(); var cleanupRelease = Signal(); var queuedRelease = Signal(); var never = Signal();
        var store = new Store();
        store.Prepare = async (request, _, token) =>
        {
            if (request.ItemIds[0] == "1")
                try { await never.Task.WaitAsync(token); }
                finally { cleanupStarted.SetResult(); await cleanupRelease.Task; }
            else await queuedRelease.Task.WaitAsync(token);
            return Result(request);
        };
        SynchronizationContext.SetSynchronizationContext(context);
        var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        Task<ItemClassificationBatch>? canceled = null, refused = null, recovered = null;
        Task<ItemClassificationBatch>[] queued = []; Task? shutdown = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            canceled = service.PrepareBatchAsync(Request("1"), token: cancellation.Token); context.Drain();
            cancellation.Cancel();
            SynchronizationContext.SetSynchronizationContext(prior);
            await context.DrainUntilAsync(() => cleanupStarted.Task.IsCompleted, Deadline);
            SynchronizationContext.SetSynchronizationContext(context);
            Assert.False(canceled.IsCompleted);
            queued = Enumerable.Range(0, 7).Select(_ => service.PrepareBatchAsync(Request("2"))).ToArray();
            refused = service.PrepareBatchAsync(Request("2"));
            Assert.True(refused.IsFaulted, "The producer-retiring consumer must still reserve its global slot.");
            cleanupRelease.SetResult();
            SynchronizationContext.SetSynchronizationContext(prior);
            await context.DrainUntilAsync(() => canceled.IsCompleted, Deadline);
            SynchronizationContext.SetSynchronizationContext(context);
            recovered = service.PrepareBatchAsync(Request("2")); Assert.False(recovered.IsCompleted);
            queuedRelease.SetResult();
            SynchronizationContext.SetSynchronizationContext(prior);
            // A drain sees only currently posted continuations. Await the actual successful
            // terminals before finally starts shutdown, which correctly cancels unfinished work.
            await context.DrainUntilAsync(Task.WhenAll(queued.Append(recovered)), Deadline);
        }
        finally
        {
            cleanupRelease.TrySetResult(); queuedRelease.TrySetResult(); context.Drain();
            shutdown = service.DisposeAsync().AsTask(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(prior);
        }
        await context.DrainUntilAsync(shutdown, Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled!);
        Assert.Equal("ITEM_CLASSIFICATIONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => refused!)).Code);
        await shutdown.WaitAsync(Deadline);
        Assert.Equal(2, store.Calls);
    }
    [Fact]
    public async Task Shutdown_drains_canceling_observers_even_when_their_continuations_run_after_the_worker()
    {
        var prior = SynchronizationContext.Current; var context = new QueuedContext();
        var progressRelease = Signal(); var callbackEntered = Signal(); var store = new Store();
        store.Prepare = async (request, report, token) =>
        { await report(new("Reading", "icons", 0, 1)); token.ThrowIfCancellationRequested(); return Result(request); };
        SynchronizationContext.SetSynchronizationContext(context);
        var service = new ItemClassificationsService(store); using var cancellation = new CancellationTokenSource();
        Task<ItemClassificationBatch>? canceled = null; Task? shutdown = null;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            canceled = service.PrepareBatchAsync(Request("1"), async _ => { callbackEntered.SetResult(); await progressRelease.Task; }, cancellation.Token);
            context.Drain(); Assert.True(callbackEntered.Task.IsCompleted);
            cancellation.Cancel(); context.Drain(); Assert.False(canceled.IsCompleted);
            shutdown = service.DisposeAsync().AsTask(); context.Drain(); Assert.False(shutdown.IsCompleted);
            progressRelease.SetResult();
            // Prefer newer producer/disposal continuations while an older observer-close
            // continuation remains queued. Shutdown may finish only after that consumer retires.
            while (context.RunNewest())
                if (shutdown.IsCompleted) Assert.True(canceled.IsCompleted, "Shutdown completed before its canceled consumer retired.");
        }
        finally
        {
            progressRelease.TrySetResult(); context.Drain();
            shutdown ??= service.DisposeAsync().AsTask(); context.Drain();
            SynchronizationContext.SetSynchronizationContext(prior);
        }
        await shutdown.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled!);
    }
    [Fact]
    public async Task UnknownPolicyAndUnsupportedV2FailBeforeStoreAndPolicySeparatesSharedDemands()
    {
        var store = new Store(); await using var service = new ItemClassificationsService(store);
        Assert.False(service.SupportsSubclassLabels);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("1") with { PolicyRevision = "future" }))).Code);
        Assert.Equal("ITEM_SUBCLASS_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareBatchAsync(Request("1") with { PolicyRevision = "classification-label-index-v2" }))).Code);
        Assert.Equal(0, store.Calls); store.Support = true; Assert.True(service.SupportsSubclassLabels);
        var entered = Signal(); var release = Signal();
        store.Prepare = async (r, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(r); };
        var legacy = service.PrepareBatchAsync(Request("1") with { IncludeSubclass = true }); await entered.Task.WaitAsync(Deadline);
        var next = service.PrepareBatchAsync(Request("1") with { IncludeSubclass = true, PolicyRevision = "classification-label-index-v2" });
        release.SetResult(); Assert.Equal("classification-label-index-v1", (await legacy).PolicyRevision);
        Assert.Equal("classification-label-index-v2", (await next).PolicyRevision); Assert.Equal(2, store.Calls);
    }
    [Fact]
    public async Task Terminal_completion_without_another_post_wakes_the_continuation_pump()
    {
        var context = new QueuedContext(); var posted = Signal(); var terminal = Signal();
        context.Post(_ => posted.SetResult(), null);
        var pumping = context.DrainUntilAsync(terminal.Task, Deadline);
        Assert.True(posted.Task.IsCompleted); Assert.False(pumping.IsCompleted);
        // The final queued callback has already run. Completing outside that context must wake
        // the pump without a further Post; the old predicate-only loop could wait until timeout.
        terminal.SetResult(); await pumping.WaitAsync(Deadline);
    }
    // A deterministic continuation queue establishes that cancellation has entered its async
    // cleanup before checking admission, without sleeps or scheduler-timing assumptions.
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> pending = [];
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Add((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal async Task DrainUntilAsync(Func<bool> completed, TimeSpan deadline)
        {
            using var timeout = new CancellationTokenSource(deadline);
            while (!completed())
                if (!Run(newest: false)) await posted.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        internal async Task DrainUntilAsync(Task terminal, TimeSpan deadline)
        {
            using var timeout = new CancellationTokenSource(deadline);
            while (!terminal.IsCompleted)
            {
                if (Run(newest: false)) continue;
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                var nextPost = posted.WaitAsync(waiting.Token);
                try { await Task.WhenAny(nextPost, terminal).WaitAsync(timeout.Token).ConfigureAwait(false); }
                finally
                {
                    // The terminal can finish without posting to this context. Retire the losing
                    // semaphore wait so it cannot consume a later notification or outlive the pump.
                    await waiting.CancelAsync();
                    try { await nextPost.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                }
            }
            await terminal.ConfigureAwait(false);
        }
        internal bool RunNewest() => Run(newest: true);
        internal void Drain()
        {
            for (var count = 0; Run(newest: false); count++)
                if (count > 10_000) throw new InvalidOperationException("Authored continuation queue did not retire.");
        }
        private bool Run(bool newest)
        {
            (SendOrPostCallback Callback, object? State) work;
            lock (pending)
            {
                if (pending.Count == 0) return false;
                var index = newest ? pending.Count - 1 : 0; work = pending[index]; pending.RemoveAt(index);
            }
            var previous = Current; SetSynchronizationContext(this);
            try { work.Callback(work.State); return true; }
            finally { SetSynchronizationContext(previous); }
        }
    }
    private sealed class Call(ItemClassificationBatchRequest request) { internal ItemClassificationBatchRequest Request = request; internal TaskCompletionSource Release = Signal(); }
    private sealed class Store : IItemClassificationsStore
    {
        internal int Calls;
        internal bool Support;
        public bool SupportsSubclassLabels => Support;
        internal Func<ItemClassificationBatchRequest, Func<ItemClassificationProgress, Task>, CancellationToken, Task<ItemClassificationBatch>> Prepare = (request, _, _) => Task.FromResult(Result(request));
        public Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request, Func<ItemClassificationProgress, Task> progress, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Prepare(request, progress, token); }
    }
}
