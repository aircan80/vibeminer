using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ArchiveProofCacheTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Shared_fetch_promotes_and_demotes_with_remaining_waiters_without_duplicate_request()
    {
        await using var cache = new ArchiveProofCache();
        var bulk = new Work(AcquisitionPriority.Background); var foreground = new Work(AcquisitionPriority.Interactive);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAcquisitionWorkContext? captured = null; var calls = 0;
        async Task<(string, long)> Fetch(IAcquisitionWorkContext work, CancellationToken token)
        { calls++; captured = work; await release.Task.WaitAsync(token); return ("authenticated", 32); }
        var first = cache.GetAsync("context|official-index|footer-toc", bulk, Fetch, default);
        using var cancellation = new CancellationTokenSource();
        var second = cache.GetAsync("context|official-index|footer-toc", foreground, Fetch, cancellation.Token);
        Assert.Equal(AcquisitionPriority.Interactive, captured!.Snapshot.Priority);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(AcquisitionPriority.Background, captured.Snapshot.Priority);
        release.SetResult(); Assert.Equal("authenticated", await first.WaitAsync(Deadline));
        Assert.Equal("authenticated", await cache.GetAsync("context|official-index|footer-toc", foreground, Fetch, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Byte_budget_evicts_idle_proofs_and_native_absence_is_context_scoped_and_expires()
    {
        var clock = new Clock(); await using var cache = new ArchiveProofCache(maximumBytes: 256, timeProvider: clock);
        var calls = 0;
        Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); calls++; return Task.FromResult(("proof", 256L)); }
        _ = await cache.GetAsync("a", null, Fetch, default);
        _ = await cache.GetAsync("b", null, Fetch, default);
        _ = await cache.GetAsync("a", null, Fetch, default);
        Assert.Equal(3, calls);
        Task<(string, long)> Missing(IAcquisitionWorkContext _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); calls++; throw new DiscoveryException("CONTENT_SOURCE_MISSING", "Authored missing native source."); }
        await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("source-A|url", null, Missing, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("source-A|url", null, Missing, default));
        Assert.Equal(4, calls);
        clock.Advance();
        await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("source-A|url", null, Missing, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("source-B|url", null, Missing, default));
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task Pending_requests_are_bounded_and_last_waiter_cancellation_retires_the_fetch()
    {
        await using var cache = new ArchiveProofCache(maximumPending: 1);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ("unreachable", 1); }
            finally { canceled.TrySetResult(); }
        }
        using var cancellation = new CancellationTokenSource();
        var first = cache.GetAsync("a", null, Fetch, cancellation.Token);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("b", null, Fetch, default));
        Assert.Equal("CONTENT_LOCATOR_CAPACITY", failure.Code);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await canceled.Task.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Last_waiter_and_concurrent_disposers_wait_until_shared_fetch_cleanup_really_finishes()
    {
        var cache = new ArchiveProofCache(maximumPending: 1);
        var enteredCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ("unreachable", 1); }
            finally { enteredCleanup.TrySetResult(); await releaseCleanup.Task; }
        }
        using var cancellation = new CancellationTokenSource();
        var pending = cache.GetAsync("source", null, Fetch, cancellation.Token);
        cancellation.Cancel(); await enteredCleanup.Task.WaitAsync(Deadline);
        Assert.False(pending.IsCompleted);
        var refusal = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("source", null, Fetch, default));
        Assert.Equal("CONTENT_LOCATOR_CAPACITY", refusal.Code);
        var first = cache.DisposeAsync().AsTask(); var second = cache.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        releaseCleanup.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Success_racing_last_waiter_cancellation_does_not_leave_a_permanent_stopping_entry()
    {
        await using var cache = new ArchiveProofCache(maximumPending: 1);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            calls++;
            if (calls == 1)
            {
                using var observation = token.Register(() => observed.TrySetResult());
                await release.Task; // Model valid authentication completing concurrently with cancellation.
            }
            return ("valid proof", 16);
        }
        using var cancellation = new CancellationTokenSource();
        var first = cache.GetAsync("same-context-and-span", null, Fetch, cancellation.Token);
        cancellation.Cancel(); await observed.Task.WaitAsync(Deadline);
        Assert.False(first.IsCompleted);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("valid proof", await cache.GetAsync("same-context-and-span", null, Fetch, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Native_metadata_admission_preserves_foreground_positions_under_bulk_saturation()
    {
        await using var cache = new ArchiveProofCache(maximumPending: 4, maximumBackgroundPending: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        { await release.Task.WaitAsync(token); return ("proof", 16); }
        var bulk = new Work(AcquisitionPriority.Background); var foreground = new Work(AcquisitionPriority.Interactive);
        var first = cache.GetAsync("bulk-1", bulk, Fetch, default);
        var second = cache.GetAsync("bulk-2", bulk, Fetch, default);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("bulk-3", bulk, Fetch, default));
        Assert.Equal("CONTENT_LOCATOR_CAPACITY", failure.Code);
        var interactive = cache.GetAsync("foreground", foreground, Fetch, default);
        release.SetResult();
        Assert.Equal("proof", await interactive.WaitAsync(Deadline));
        await Task.WhenAll(first, second).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Different_foreground_producers_reuse_authenticated_directory_and_page_requests()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync(archiveEncoding: true);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await using var source = await plans.AcquireAsync(fixture.Source);
        var producer = new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans);
        var work = new Work(AcquisitionPriority.Interactive, source.Plan.Source);
        var item = fixture.Data.Metadata["encoding"];
        _ = await producer.ProduceAsync(new(item.EncodingKey, item.Encoded.Length), work, default);
        var indexRequests = fixture.Handler.Requests.Count(url => url.EndsWith(".index", StringComparison.Ordinal));
        Assert.Equal(3, indexRequests);
        foreach (var key in new[] { new string('0', 31) + "1", new string('0', 31) + "2" })
        {
            var failure = await Assert.ThrowsAsync<DiscoveryException>(() => producer.ProduceAsync(new(key, 9), work, default));
            Assert.Equal("CONTENT_NOT_LOCATED", failure.Code);
        }
        Assert.Equal(indexRequests, fixture.Handler.Requests.Count(url => url.EndsWith(".index", StringComparison.Ordinal)));
        Assert.Equal(1, plans.ConfigurationLoads);
    }

    [Fact]
    public async Task Throwing_cancellation_callback_preserves_canceled_consumer_and_waits_for_actual_producer_cleanup()
    {
        var cache = new ArchiveProofCache(maximumPending: 1);
        var enteredCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            using var throwing = token.Register(() => { notification.TrySetResult(); throw new InvalidOperationException("Authored notification failure."); });
            // Cancellation alone cannot retire this using before the callback actually throws.
            try { await notification.Task; token.ThrowIfCancellationRequested(); return ("unreachable", 1); }
            finally { enteredCleanup.TrySetResult(); await releaseCleanup.Task; }
        }
        using var cancellation = new CancellationTokenSource();
        var pending = cache.GetAsync("throwing", null, Fetch, cancellation.Token);
        Exception? outcome = null; bool completedBeforeCleanup = false;
        try
        {
            await cancellation.CancelAsync(); await notification.Task.WaitAsync(Deadline); await enteredCleanup.Task.WaitAsync(Deadline);
            completedBeforeCleanup = pending.IsCompleted;
            var refusal = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("other", null, Fetch, default));
            Assert.Equal("CONTENT_LOCATOR_CAPACITY", refusal.Code);
        }
        finally
        {
            notification.TrySetResult(); releaseCleanup.TrySetResult();
            outcome = await Record.ExceptionAsync(() => pending.WaitAsync(Deadline));
            // The original defect also faults shutdown after a callback throws. Always retire the
            // held test producer before recording that failure, so a red run leaves no blocked work.
            await Record.ExceptionAsync(() => cache.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
        Assert.False(completedBeforeCleanup);
        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
    }

    [Fact]
    public async Task Shutdown_notifies_all_producers_before_waiting_for_blocked_callback_and_both_cleanup_paths()
    {
        var cache = new ArchiveProofCache(maximumPending: 2);
        using var releaseNotification = new ManualResetEventSlim();
        var enteredNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecondCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string, long)> First(IAcquisitionWorkContext _, CancellationToken token)
        {
            token.Register(() =>
            {
                enteredNotification.TrySetResult();
                if (!releaseNotification.Wait(Deadline)) throw new TimeoutException("Authored notification deadlock guard.");
                throw new InvalidOperationException("Authored notification failure after release.");
            });
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ("unreachable", 1); }
            finally { enteredFirstCleanup.TrySetResult(); await releaseFirstCleanup.Task; }
        }
        async Task<(string, long)> Second(IAcquisitionWorkContext _, CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return ("unreachable", 1); }
            finally { enteredSecondCleanup.TrySetResult(); await releaseSecondCleanup.Task; }
        }
        var first = cache.GetAsync("first", null, First, default);
        var second = cache.GetAsync("second", null, Second, default);
        // Run the public call from an async yield so the original synchronous Cancel callback
        // cannot block this test's gate owner before it can release the diagnostic callback.
        async Task Shutdown() { await Task.Yield(); await cache.DisposeAsync(); }
        var shutdown = Shutdown();
        try
        {
            await enteredNotification.Task.WaitAsync(Deadline);
            await enteredSecondCleanup.Task.WaitAsync(Deadline);
            await enteredFirstCleanup.Task.WaitAsync(Deadline);
            Assert.False(shutdown.IsCompleted);
            releaseFirstCleanup.TrySetResult();
            Assert.False(first.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            releaseNotification.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
            Assert.False(shutdown.IsCompleted);
            releaseSecondCleanup.TrySetResult();
            await shutdown.WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Deadline));
        }
        finally
        {
            releaseNotification.Set(); releaseFirstCleanup.TrySetResult(); releaseSecondCleanup.TrySetResult();
            await Record.ExceptionAsync(() => Task.WhenAll(first, second, shutdown).WaitAsync(Deadline));
        }
    }

    [Fact]
    public async Task Finished_producer_keeps_capacity_and_concurrent_shutdown_charged_until_existing_notification_retires()
    {
        var cache = new ArchiveProofCache(maximumPending: 1, maximumEntries: 1);
        using var releaseNotification = new ManualResetEventSlim();
        var enteredNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Admit and complete on the default scheduler without xUnit's synchronization context.
        // This task deliberately allows inline continuations: completing it is then a checkpoint
        // after CreateAsync's producer continuation/finally, not just the fixture's task outcome.
        var producer = new TaskCompletionSource<(string, long)>();
        Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            token.Register(() =>
            {
                enteredNotification.TrySetResult();
                if (!releaseNotification.Wait(Deadline)) throw new TimeoutException("Authored notification deadlock guard.");
            });
            return producer.Task;
        }
        using var cancellation = new CancellationTokenSource();
        var pending = await Task.Factory.StartNew(() => cache.GetAsync("finishing", null, Fetch, cancellation.Token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var cancel = cancellation.CancelAsync();
        Task firstShutdown = Task.CompletedTask, secondShutdown = Task.CompletedTask;
        try
        {
            await enteredNotification.Task.WaitAsync(Deadline);
            await Task.Run(() => producer.SetResult(("proof", 16)));
            Assert.False(pending.IsCompleted);
            var refusal = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("other", null,
                (_, _) => Task.FromResult(("other proof", 16L)), default));
            Assert.Equal("CONTENT_LOCATOR_CAPACITY", refusal.Code);
            firstShutdown = cache.DisposeAsync().AsTask(); secondShutdown = cache.DisposeAsync().AsTask();
            Assert.False(firstShutdown.IsCompleted); Assert.False(secondShutdown.IsCompleted);
            releaseNotification.Set();
            await Task.WhenAll(firstShutdown, secondShutdown, cancel).WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        }
        finally
        {
            releaseNotification.Set(); producer.TrySetResult(("proof", 16));
            await Record.ExceptionAsync(() => Task.WhenAll(pending, cancel, firstShutdown, secondShutdown).WaitAsync(Deadline));
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Cancellation_callback_can_reenter_cache_from_another_thread_without_holding_its_gate()
    {
        var cache = new ArchiveProofCache(maximumPending: 2);
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProducer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reentryRequested = new ManualResetEventSlim();
        using var threadFinished = new ManualResetEventSlim();
        // Start a dedicated observer before cancellation. A synchronous callback must not wait
        // for another pool worker while the wider test cohort is using that same thread pool.
        var observer = new Thread(() =>
        {
            try
            {
                if (!reentryRequested.Wait(Deadline)) throw new TimeoutException("Authored reentry rendezvous guard.");
                var proof = cache.GetAsync("reentrant", null,
                    (_, _) => Task.FromResult(("reentrant proof", 16L)), default);
                Assert.True(proof.IsCompleted, "An authored immediate proof should complete on the observer thread.");
                Assert.Equal("reentrant proof", proof.GetAwaiter().GetResult());
                reentered.TrySetResult();
            }
            catch (Exception failure) { reentered.TrySetException(failure); }
            finally { threadFinished.Set(); }
        }) { IsBackground = true, Name = "Archive proof cache reentry observer" };
        observer.Start();
        async Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            using var registration = token.Register(() =>
            {
                // The observer must acquire the cache gate while this callback is active. Report
                // the guard explicitly because production correctly observes callback exceptions.
                reentryRequested.Set();
                if (!threadFinished.Wait(Deadline))
                {
                    var failure = new TimeoutException("Authored callback reentry deadlock guard.");
                    reentered.TrySetException(failure); throw failure;
                }
            });
            // Cancellation alone cannot dispose the earlier callback registration before it is
            // invoked. The test retires this producer only after the callback has proved reentry.
            await releaseProducer.Task;
            token.ThrowIfCancellationRequested(); return ("unreachable", 1);
        }
        using var cancellation = new CancellationTokenSource();
        var pending = cache.GetAsync("source", null, Fetch, cancellation.Token);
        try
        {
            await cancellation.CancelAsync();
            await reentered.Task.WaitAsync(Deadline);
            Assert.False(pending.IsCompleted);
            releaseProducer.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        }
        finally
        {
            releaseProducer.TrySetResult(); reentryRequested.Set();
            Exception? disposalFailure = null;
            try { await cache.DisposeAsync().AsTask().WaitAsync(Deadline); }
            catch (Exception failure) { disposalFailure = failure; throw; }
            finally
            {
                if (!observer.Join(Deadline))
                {
                    var failure = new TimeoutException("The dedicated reentry observer must actually retire.");
                    if (disposalFailure is not null) throw new AggregateException(disposalFailure, failure);
                    throw failure;
                }
            }
        }
    }

    [Fact]
    public async Task Already_completed_cached_proof_has_no_live_producer_to_notify_on_shutdown()
    {
        var cache = new ArchiveProofCache(); var notifications = 0;
        Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            token.Register(() => { Interlocked.Increment(ref notifications); throw new InvalidOperationException("Retired callback."); });
            return Task.FromResult(("authenticated", 16L));
        }
        Assert.Equal("authenticated", await cache.GetAsync("complete", null, Fetch, default));
        await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task Expired_missing_proof_cannot_remove_blocked_notification_debt_or_escape_shutdown()
    {
        var clock = new Clock();
        var cache = new ArchiveProofCache(maximumPending: 1, maximumEntries: 1, timeProvider: clock);
        using var releaseNotification = new ManualResetEventSlim();
        var enteredNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Match the explicit default-scheduler producer checkpoint used above, so clock expiry
        // happens after the cache records its negative outcome while notification is still held.
        var producer = new TaskCompletionSource<(string, long)>();
        Task<(string, long)> Fetch(IAcquisitionWorkContext _, CancellationToken token)
        {
            token.Register(() =>
            {
                enteredNotification.TrySetResult();
                if (!releaseNotification.Wait(Deadline)) throw new TimeoutException("Authored notification deadlock guard.");
            });
            return producer.Task;
        }
        using var cancellation = new CancellationTokenSource();
        var pending = await Task.Factory.StartNew(() => cache.GetAsync("missing", null, Fetch, cancellation.Token),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var cancel = cancellation.CancelAsync();
        Task shutdown = Task.CompletedTask;
        try
        {
            await enteredNotification.Task.WaitAsync(Deadline);
            await Task.Run(() => producer.SetException(new DiscoveryException("CONTENT_SOURCE_MISSING", "Authored unavailable native source.")));
            clock.Advance();
            var refusal = await Assert.ThrowsAsync<DiscoveryException>(() => cache.GetAsync("missing", null,
                (_, _) => Task.FromResult(("replacement", 16L)), default));
            Assert.Equal("CONTENT_LOCATOR_CAPACITY", refusal.Code);
            shutdown = cache.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.False(pending.IsCompleted);
            releaseNotification.Set();
            await Task.WhenAll(shutdown, cancel).WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline));
        }
        finally
        {
            releaseNotification.Set(); producer.TrySetException(new OperationCanceledException());
            await Record.ExceptionAsync(() => Task.WhenAll(pending, cancel, shutdown).WaitAsync(Deadline));
            await cache.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance() => now += TimeSpan.FromSeconds(31);
    }
    private sealed class Work(AcquisitionPriority priority, EncodedSourceContext? source = null) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(0, priority, source is null ? [] : [source]);
        public Task WaitForChangeAsync(long revision, CancellationToken token = default) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }
}
