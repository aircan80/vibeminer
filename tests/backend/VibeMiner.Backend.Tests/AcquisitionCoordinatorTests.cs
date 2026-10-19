using System.Collections.Concurrent;
using System.Threading.Channels;
using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class AcquisitionCoordinatorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly EncodedSourceContext Source = new("wow", new string('a', 64), "us", new string('b', 64));
    private static EncodedRequirement Need(int id) => new(id.ToString("x32"), 100);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AcquisitionLimits Small(int active = 1, int bulk = 1) => new(8, 4, 2, active, bulk, 4);
    private static EncodedReceipt Receipt(EncodedRequirement need) => new(need.EncodingKey, new string('c', 64),
        need.EncodedBytes, "https://us.cdn.blizzard.com/tpr/wow/data/00/00/" + need.EncodingKey,
        "2026-10-19T00:00:00.0000000Z", "2026-10-19T00:00:00.0000000Z", need.VerifierRevision,
        new("loose", "0", need.EncodedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), null));

    private sealed class Producer : IEncodedObjectProducer
    {
        internal readonly Channel<Call> Started = Channel.CreateBounded<Call>(128);
        internal readonly ConcurrentQueue<Call> Calls = new();
        internal Func<Call, Task<EncodedReceipt>>? Run;
        public async Task<EncodedReceipt> ProduceAsync(EncodedRequirement requirement, IAcquisitionWorkContext context, CancellationToken token)
        {
            var call = new Call(requirement, context, token); Calls.Enqueue(call);
            await Started.Writer.WriteAsync(call, token);
            if (Run is not null) return await Run(call);
            await call.Release.Task.WaitAsync(token); return Receipt(requirement);
        }
        internal Task<Call> Next() => Started.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }
    private sealed record Call(EncodedRequirement Need, IAcquisitionWorkContext Context, CancellationToken Token)
    { internal readonly TaskCompletionSource Release = Signal(); }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance() => Interlocked.Add(ref timestamp, 100);
    }

    [Fact]
    public async Task Shared_consumers_receive_one_physical_receipt_and_independent_cancellation()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        using var cancellation = new CancellationTokenSource();
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background);
        var started = await producer.Next();
        await using var second = service.Attach(Need(1), Source, AcquisitionPriority.Interactive);
        var waiting = second.WaitAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(started.Token.IsCancellationRequested); Assert.Single(producer.Calls);
        Assert.Equal(AcquisitionPriority.Background, started.Context.Snapshot.Priority);
        started.Release.SetResult(); Assert.Equal(Receipt(Need(1)), await first.Completion.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Reserved_active_capacity_starts_foreground_while_bulk_is_saturated()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small(2, 1));
        await using var bulk = service.Attach(Need(1), Source, AcquisitionPriority.Background);
        var first = await producer.Next();
        await using var pending = service.Attach(Need(2), Source, AcquisitionPriority.Background);
        await using var foreground = service.Attach(Need(3), Source, AcquisitionPriority.Interactive);
        var interactive = await producer.Next(); Assert.Equal(Need(3), interactive.Need);
        Assert.Equal(2, producer.Calls.Count); interactive.Release.SetResult();
        await foreground.Completion.WaitAsync(Deadline); Assert.Equal(2, producer.Calls.Count);
        first.Release.SetResult(); var later = await producer.Next(); Assert.Equal(Need(2), later.Need); later.Release.SetResult();
        await Task.WhenAll(bulk.Completion, pending.Completion).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Queued_promotion_changes_order_without_a_duplicate_producer()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        await using var blocker = service.Attach(Need(1), Source, AcquisitionPriority.Background); var first = await producer.Next();
        await using var older = service.Attach(Need(2), Source, AcquisitionPriority.Background);
        await using var promoted = service.Attach(Need(3), Source, AcquisitionPriority.Background);
        await using var screen = service.Attach(Need(3), Source, AcquisitionPriority.Interactive);
        first.Release.SetResult(); var next = await producer.Next(); Assert.Equal(Need(3), next.Need);
        next.Release.SetResult(); await Task.WhenAll(promoted.Completion, screen.Completion).WaitAsync(Deadline);
        var last = await producer.Next(); Assert.Equal(Need(2), last.Need); last.Release.SetResult();
        await older.Completion.WaitAsync(Deadline); Assert.Equal(3, producer.Calls.Count);
    }

    [Fact]
    public async Task Queued_demotion_restores_fifo_among_remaining_background_consumers()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        await using var blocker = service.Attach(Need(1), Source, AcquisitionPriority.Background); var first = await producer.Next();
        await using var older = service.Attach(Need(2), Source, AcquisitionPriority.Background);
        await using var demoted = service.Attach(Need(3), Source, AcquisitionPriority.Interactive);
        demoted.SetPriority(AcquisitionPriority.Background); first.Release.SetResult();
        var next = await producer.Next(); Assert.Equal(Need(2), next.Need); next.Release.SetResult(); await older.Completion.WaitAsync(Deadline);
        var last = await producer.Next(); Assert.Equal(Need(3), last.Need); last.Release.SetResult(); await demoted.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Inflight_priority_and_source_changes_are_observable_without_restart()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        await using var background = service.Attach(Need(1), Source, AcquisitionPriority.Background); var call = await producer.Next();
        var before = call.Context.Snapshot; var changed = call.Context.WaitForChangeAsync(before.Revision);
        var secondSource = Source with { CdnConfigurationSha256 = new string('d', 64), SourceRegion = "eu" };
        await using var foreground = service.Attach(Need(1), secondSource, AcquisitionPriority.Interactive);
        await changed.WaitAsync(Deadline); var promoted = call.Context.Snapshot;
        Assert.Equal(AcquisitionPriority.Interactive, promoted.Priority); Assert.Equal(2, promoted.Sources.Count);
        Assert.Contains(secondSource, promoted.Sources); Assert.True(promoted.Revision > before.Revision);
        var released = call.Context.WaitForChangeAsync(promoted.Revision); await foreground.DisposeAsync(); await released.WaitAsync(Deadline);
        Assert.Equal(AcquisitionPriority.Background, call.Context.Snapshot.Priority); Assert.Single(call.Context.Snapshot.Sources);
        Assert.False(call.Token.IsCancellationRequested); call.Release.SetResult(); await background.Completion.WaitAsync(Deadline);
        Assert.Single(producer.Calls);
    }

    [Fact]
    public async Task Default_state_machine_admission_does_not_starve_foreground_after_inflight_demotion()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer);
        var foreground = new List<IAcquisitionSubscription>(); var background = new List<IAcquisitionSubscription>();
        var calls = new List<Call>();
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                foreground.Add(service.Attach(Need(i), Source, AcquisitionPriority.Interactive)); calls.Add(await producer.Next());
                background.Add(service.Attach(Need(i), Source, AcquisitionPriority.Background));
            }
            foreach (var subscription in foreground) await subscription.DisposeAsync();
            Assert.All(calls, call => { Assert.Equal(AcquisitionPriority.Background, call.Context.Snapshot.Priority); Assert.False(call.Token.IsCancellationRequested); });
            await using var fresh = service.Attach(Need(20), Source, AcquisitionPriority.Interactive);
            var interactive = await producer.Next(); Assert.Equal(Need(20), interactive.Need);
            Assert.Equal(13, producer.Calls.Count);
            foreach (var call in calls) call.Release.SetResult(); interactive.Release.SetResult();
            await Task.WhenAll(background.Select(value => value.Completion).Append(fresh.Completion)).WaitAsync(Deadline);
        }
        finally
        {
            foreach (var subscription in foreground.Concat(background)) await subscription.DisposeAsync();
        }
    }

    [Fact]
    public async Task Source_A_unavailability_does_not_hide_newly_attached_valid_source_B()
    {
        var exhausted = Signal(); var producer = new Producer
        {
            Run = async call =>
            {
                var initial = call.Context.Snapshot; exhausted.SetResult();
                await call.Context.WaitForChangeAsync(initial.Revision, call.Token);
                Assert.Contains(call.Context.Snapshot.Sources, source => source.SourceRegion == "eu");
                return Receipt(call.Need);
            }
        };
        await using var service = new AcquisitionCoordinator(producer, Small());
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background); await exhausted.Task.WaitAsync(Deadline);
        await using var second = service.Attach(Need(1), Source with { SourceRegion = "eu" }, AcquisitionPriority.Interactive);
        Assert.Equal(await first.Completion.WaitAsync(Deadline), await second.Completion.WaitAsync(Deadline)); Assert.Single(producer.Calls);
    }

    [Fact]
    public async Task Failed_source_has_no_global_negative_cache_for_later_valid_context()
    {
        var attempts = 0; var producer = new Producer { Run = call =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<EncodedReceipt>(new DiscoveryException("CONTENT_UNAVAILABLE", "Authored source unavailable.", true))
                : Task.FromResult(Receipt(call.Need)) };
        await using var service = new AcquisitionCoordinator(producer, Small());
        await Assert.ThrowsAsync<DiscoveryException>(() => service.AcquireAsync(Need(1), Source, AcquisitionPriority.Background));
        var result = await service.AcquireAsync(Need(1), Source with { SourceRegion = "eu" }, AcquisitionPriority.Interactive);
        Assert.Equal(Need(1).EncodingKey, result.EncodingKey); Assert.Equal(2, producer.Calls.Count);
    }

    [Fact]
    public async Task Same_EKey_with_conflicting_length_is_rejected_before_a_second_producer()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background); var call = await producer.Next();
        var error = Assert.Throws<DiscoveryException>(() => service.Attach(Need(1) with { EncodedBytes = 101 }, Source, AcquisitionPriority.Interactive));
        Assert.Equal("ACQUISITION_SIZE_CONFLICT", error.Code); Assert.Single(producer.Calls);
        call.Release.SetResult(); await first.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Admission_reserves_capacity_for_interactive_requirements_and_bounds_subscribers()
    {
        var producer = new Producer(); var limits = new AcquisitionLimits(4, 3, 2, 1, 1, 2);
        await using var service = new AcquisitionCoordinator(producer, limits);
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background); await producer.Next();
        await using var second = service.Attach(Need(2), Source, AcquisitionPriority.Background);
        Assert.Equal("ACQUISITION_CAPACITY", Assert.Throws<DiscoveryException>(() => service.Attach(Need(3), Source, AcquisitionPriority.Background)).Code);
        await using var interactive = service.Attach(Need(3), Source, AcquisitionPriority.Interactive);
        await using var last = service.Attach(Need(4), Source, AcquisitionPriority.Interactive);
        Assert.Throws<DiscoveryException>(() => service.Attach(Need(5), Source, AcquisitionPriority.Interactive));
        await using var shared = service.Attach(Need(1), Source, AcquisitionPriority.Interactive);
        Assert.Throws<DiscoveryException>(() => service.Attach(Need(1), Source, AcquisitionPriority.Interactive));
    }

    [Fact]
    public async Task Last_cancel_waits_for_cleanup_and_refuses_reattach_until_retired()
    {
        var cleanup = Signal(); var allowCleanup = Signal(); var producer = new Producer
        {
            Run = async call =>
            {
                try { await call.Release.Task.WaitAsync(call.Token); return Receipt(call.Need); }
                finally { cleanup.SetResult(); await allowCleanup.Task; }
            }
        };
        await using var service = new AcquisitionCoordinator(producer, Small()); using var cancellation = new CancellationTokenSource();
        var result = service.AcquireAsync(Need(1), Source, AcquisitionPriority.Interactive, token: cancellation.Token);
        await producer.Next(); cancellation.Cancel(); await cleanup.Task.WaitAsync(Deadline);
        Assert.False(result.IsCompleted);
        Assert.Equal("ACQUISITION_STOPPING", Assert.Throws<DiscoveryException>(() => service.Attach(Need(1), Source, AcquisitionPriority.Interactive)).Code);
        allowCleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Concurrent_last_detach_calls_await_the_same_actual_cleanup()
    {
        var cleanup = Signal(); var release = Signal(); var producer = new Producer { Run = async call =>
        { try { await call.Release.Task.WaitAsync(call.Token); return Receipt(call.Need); } finally { cleanup.SetResult(); await release.Task; } } };
        await using var service = new AcquisitionCoordinator(producer, Small());
        var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Background); await producer.Next();
        var first = subscription.DisposeAsync().AsTask(); var second = subscription.DisposeAsync().AsTask();
        await cleanup.Task.WaitAsync(Deadline); Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Publication_winning_cancellation_remains_successful()
    {
        var cancellationSeen = Signal(); var publish = Signal(); var producer = new Producer { Run = async call =>
        { using var registration = call.Token.Register(() => cancellationSeen.SetResult()); await publish.Task; return Receipt(call.Need); } };
        await using var service = new AcquisitionCoordinator(producer, Small()); using var cancellation = new CancellationTokenSource();
        var task = service.AcquireAsync(Need(1), Source, AcquisitionPriority.Background, token: cancellation.Token);
        await producer.Next(); cancellation.Cancel(); await cancellationSeen.Task.WaitAsync(Deadline);
        publish.SetResult(); Assert.Equal(Receipt(Need(1)), await task.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Shutdown_cancels_queued_and_active_work_and_waits_for_cleanup()
    {
        var cleanup = Signal(); var finish = Signal(); var producer = new Producer { Run = async call =>
        { try { await call.Release.Task.WaitAsync(call.Token); return Receipt(call.Need); } finally { cleanup.SetResult(); await finish.Task; } } };
        var service = new AcquisitionCoordinator(producer, Small());
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background); await producer.Next();
        await using var queued = service.Attach(Need(2), Source, AcquisitionPriority.Background);
        var stopping = service.DisposeAsync().AsTask(); var repeated = service.DisposeAsync().AsTask();
        await cleanup.Task.WaitAsync(Deadline); Assert.False(stopping.IsCompleted); Assert.False(repeated.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", Assert.Throws<DiscoveryException>(() => service.Attach(Need(3), Source, AcquisitionPriority.Interactive)).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.Completion); Assert.Single(producer.Calls);
        finish.SetResult(); await Task.WhenAll(stopping, repeated).WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.Completion);
    }

    [Fact]
    public async Task Recursive_physical_acquisition_is_rejected_instead_of_deadlocking_full_capacity()
    {
        AcquisitionCoordinator? service = null; var producer = new Producer { Run = call =>
        {
            var error = Assert.Throws<DiscoveryException>(() => service!.Attach(Need(2), Source, AcquisitionPriority.Interactive));
            Assert.Equal("ACQUISITION_RECURSIVE", error.Code); return Task.FromResult(Receipt(call.Need));
        } };
        await using var owned = service = new(producer, Small());
        await service.AcquireAsync(Need(1), Source, AcquisitionPriority.Background).WaitAsync(Deadline); Assert.Single(producer.Calls);
    }

    [Fact]
    public async Task Slow_progress_observer_keeps_only_latest_update_without_blocking_production()
    {
        var entered = Signal(); var releaseObserver = Signal(); var reportsDone = Signal(); var finishProducer = Signal();
        var clock = new Clock(); var observed = new ConcurrentQueue<long?>(); var callbacks = 0;
        var producer = new Producer { Run = async call =>
        {
            await call.Context.ReportAsync(new("Downloading", "bytes", 0, 1000)); await entered.Task;
            for (var i = 1; i <= 1000; i++) { clock.Advance(); await call.Context.ReportAsync(new("Downloading", "bytes", i, 1000)); }
            reportsDone.SetResult(); await finishProducer.Task.WaitAsync(call.Token); return Receipt(call.Need);
        } };
        await using var service = new AcquisitionCoordinator(producer, Small(), clock);
        var latest = Signal(); await using var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Interactive, async progress =>
        {
            observed.Enqueue(progress.Completed); if (Interlocked.Increment(ref callbacks) == 1) { entered.SetResult(); await releaseObserver.Task; }
            if (progress.Completed == 1000) latest.SetResult();
        });
        await reportsDone.Task.WaitAsync(Deadline); Assert.Equal(1, callbacks);
        releaseObserver.SetResult(); await latest.Task.WaitAsync(Deadline); Assert.Equal(2, callbacks);
        Assert.Equal(new long?[] { 0, 1000 }, observed.ToArray()); finishProducer.SetResult(); await subscription.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Faulty_progress_observer_does_not_fail_verified_acquisition()
    {
        var called = Signal(); var producer = new Producer { Run = async call =>
        { await call.Context.ReportAsync(new("Checking", "bytes", 0, 100)); await called.Task; return Receipt(call.Need); } };
        await using var service = new AcquisitionCoordinator(producer, Small());
        var result = await service.AcquireAsync(Need(1), Source, AcquisitionPriority.Interactive, _ =>
        { called.SetResult(); throw new InvalidOperationException("Authored subscriber failure."); }).WaitAsync(Deadline);
        Assert.Equal(Receipt(Need(1)), result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_or_unit_transition_is_delivered_without_a_clock_tick_while_counts_remain_throttled(bool changeUnit)
    {
        var initialObserved = Signal(); var transitionObserved = Signal(); var reportsFinished = Signal(); var finish = Signal();
        var initial = new AcquisitionProgress("Verifying retained object", "bytes", 0, 1000);
        var waiting = changeUnit ? new AcquisitionProgress("Verifying retained object", "requirements", 0, 1000)
            : new AcquisitionProgress("Waiting for temporary storage", "bytes", 0, 1000);
        var producer = new Producer { Run = async call =>
        {
            await call.Context.ReportAsync(initial); await initialObserved.Task.WaitAsync(call.Token);
            await call.Context.ReportAsync(waiting);
            for (var i = 1; i <= 1000; i++) await call.Context.ReportAsync(waiting with { Completed = i });
            reportsFinished.SetResult(); await finish.Task.WaitAsync(call.Token); return Receipt(call.Need);
        } };
        var observed = new ConcurrentQueue<AcquisitionProgress>();
        await using var service = new AcquisitionCoordinator(producer, Small(), new Clock());
        await using var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Interactive, progress =>
        {
            observed.Enqueue(progress);
            if (progress == initial) initialObserved.TrySetResult();
            if (progress == waiting) transitionObserved.TrySetResult();
            return Task.CompletedTask;
        });
        try
        {
            await reportsFinished.Task.WaitAsync(Deadline); await transitionObserved.Task.WaitAsync(Deadline);
            Assert.Equal(new[] { initial, waiting }, observed.ToArray());
        }
        finally { finish.TrySetResult(); }
        await subscription.Completion.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Progress_dispatchers_remain_bounded_across_already_retired_producers()
    {
        var callbacks = new ConcurrentDictionary<string, TaskCompletionSource>();
        var releaseObservers = Signal(); var entered = 0; var finished = 0; var allFinished = Signal();
        var producer = new Producer { Run = async call =>
        {
            await call.Context.ReportAsync(new("Checking", "bytes", 0, 100));
            if (int.Parse(call.Need.EncodingKey, System.Globalization.NumberStyles.HexNumber) <= 32)
                await callbacks[call.Need.EncodingKey].Task.WaitAsync(call.Token);
            return Receipt(call.Need);
        } };
        await using var service = new AcquisitionCoordinator(producer, Small());
        try
        {
            for (var i = 1; i <= 64; i++)
            {
                var need = Need(i); callbacks[need.EncodingKey] = Signal();
                await service.AcquireAsync(need, Source, AcquisitionPriority.Interactive, async _ =>
                {
                    Interlocked.Increment(ref entered); callbacks[need.EncodingKey].SetResult();
                    await releaseObservers.Task;
                    if (Interlocked.Increment(ref finished) == 32) allFinished.SetResult();
                }).WaitAsync(Deadline);
            }
            Assert.Equal(32, entered); Assert.Equal(64, producer.Calls.Count);
        }
        finally { releaseObservers.TrySetResult(); }
        await allFinished.Task.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Wrong_identity_result_is_never_delivered_as_verified_success()
    {
        var producer = new Producer { Run = call => Task.FromResult(Receipt(call.Need) with { EncodedBytes = 99 }) };
        await using var service = new AcquisitionCoordinator(producer, Small());
        Assert.Equal("ACQUISITION_RESULT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.AcquireAsync(Need(1), Source, AcquisitionPriority.Interactive))).Code);
    }

    [Fact]
    public async Task Invalid_progress_terminates_producer_with_typed_outcome()
    {
        var producer = new Producer { Run = async call =>
        { await call.Context.ReportAsync(new("Checking", "bytes", 101, 100)); return Receipt(call.Need); } };
        await using var service = new AcquisitionCoordinator(producer, Small());
        Assert.Equal("ACQUISITION_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.AcquireAsync(Need(1), Source, AcquisitionPriority.Interactive))).Code);
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789", 100, "blte-encoded-v1")]
    [InlineData("00000000000000000000000000000001", 0, "blte-encoded-v1")]
    [InlineData("00000000000000000000000000000001", 536870913, "blte-encoded-v1")]
    [InlineData("00000000000000000000000000000001", 100, "unknown")]
    public async Task Invalid_requirements_never_start_a_producer(string key, long bytes, string revision)
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        Assert.Throws<DiscoveryException>(() => service.Attach(new(key, bytes, revision), Source, AcquisitionPriority.Interactive));
        Assert.Empty(producer.Calls);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("build")]
    [InlineData("region")]
    [InlineData("configuration")]
    public async Task Null_source_identity_fields_fail_with_typed_invalid_request_before_production(string field)
    {
        var source = field switch
        {
            "product" => Source with { ProductId = null! },
            "build" => Source with { BuildIdentity = null! },
            "region" => Source with { SourceRegion = null! },
            _ => Source with { CdnConfigurationSha256 = null! }
        };
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() =>
            service.Attach(Need(1), source, AcquisitionPriority.Interactive)).Code);
        Assert.Empty(producer.Calls);
    }

    [Fact]
    public async Task Malformed_optional_source_reference_is_rejected_before_production()
    {
        var valid = new OpeningSourceReference("1", Source.SourceRegion, new string('a', 64), 0, new string('b', 64), 0);
        var invalid = new[]
        {
            valid with { Generation = "0" }, valid with { Generation = "01" },
            valid with { Generation = "9223372036854775808" }, valid with { Generation = "\u0661" },
            valid with { Generation = null! }, valid with { Region = "eu" },
            valid with { VersionsSha256 = new string('A', 64) }, valid with { CdnsSha256 = null! },
            valid with { VersionsRowOrdinal = -1 }, valid with { CdnsRowOrdinal = -1 }
        };
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        foreach (var reference in invalid)
            Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() => service.Attach(Need(1),
                Source with { SourceReference = reference }, AcquisitionPriority.Interactive)).Code);
        Assert.Empty(producer.Calls);
        producer.Run = call => Task.FromResult(Receipt(call.Need));
        var receipt = await service.AcquireAsync(Need(1), Source with { SourceReference = valid }, AcquisitionPriority.Interactive);
        Assert.Equal(Need(1).EncodingKey, receipt.EncodingKey);
    }

    [Fact]
    public async Task Payload_accounting_survives_throttled_progress_and_terminal_delivery()
    {
        var callbacks = 0;
        var producer = new Producer { Run = async call =>
        {
            for (var bytes = 1; bytes <= 4096; bytes++)
                await call.Context.ReportAsync(new("Transferring", "wireBytes", bytes));
            return Receipt(call.Need);
        } };
        await using var service = new AcquisitionCoordinator(producer, Small(), new Clock());
        await using var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Background,
            _ => { Interlocked.Increment(ref callbacks); return Task.CompletedTask; });
        await subscription.Completion.WaitAsync(Deadline);
        Assert.Equal(4096, subscription.ObservedPayloadBytes);
        Assert.InRange(Volatile.Read(ref callbacks), 0, 1);
        await subscription.DisposeAsync(); Assert.Equal(4096, subscription.ObservedPayloadBytes);
    }

    [Fact]
    public async Task Shared_payload_metrics_start_at_attachment_and_freeze_when_a_consumer_detaches()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        await using var first = service.Attach(Need(1), Source, AcquisitionPriority.Background);
        var call = await producer.Next(); await call.Context.ReportAsync(new("Transfer", "wireBytes", 100));
        await using var second = service.Attach(Need(1), Source, AcquisitionPriority.Interactive);
        Assert.Equal(0, second.ObservedPayloadBytes);
        await call.Context.ReportAsync(new("Transfer", "wireBytes", 160));
        Assert.Equal(160, first.ObservedPayloadBytes); Assert.Equal(60, second.ObservedPayloadBytes);
        await first.DisposeAsync();
        await call.Context.ReportAsync(new("Transfer", "wireBytes", 190));
        Assert.Equal(160, first.ObservedPayloadBytes); Assert.Equal(90, second.ObservedPayloadBytes);
        call.Release.SetResult(); await second.Completion.WaitAsync(Deadline);
        Assert.Equal(90, second.ObservedPayloadBytes);
    }

    [Fact]
    public async Task Final_consumer_metrics_include_actual_cancellation_cleanup_bytes()
    {
        var cleaning = Signal(); var release = Signal();
        var producer = new Producer { Run = async call =>
        {
            try { await call.Release.Task.WaitAsync(call.Token); }
            finally
            {
                await call.Context.ReportAsync(new("Transfer", "wireBytes", 50)); cleaning.TrySetResult();
                await release.Task; await call.Context.ReportAsync(new("Transfer", "wireBytes", 75));
            }
            return Receipt(call.Need);
        } };
        await using var service = new AcquisitionCoordinator(producer, Small());
        await using var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Background);
        var call = await producer.Next(); await call.Context.ReportAsync(new("Transfer", "wireBytes", 25));
        var disposal = subscription.DisposeAsync().AsTask();
        await cleaning.Task.WaitAsync(Deadline); Assert.False(disposal.IsCompleted);
        Assert.Equal(50, subscription.ObservedPayloadBytes);
        release.SetResult(); await disposal.WaitAsync(Deadline);
        Assert.Equal(75, subscription.ObservedPayloadBytes);
    }

    [Fact]
    public async Task Payload_counter_cannot_move_backwards_even_when_progress_would_be_throttled()
    {
        var producer = new Producer { Run = async call =>
        {
            await call.Context.ReportAsync(new("Transfer", "wireBytes", 20));
            await call.Context.ReportAsync(new("Transfer", "wireBytes", 19)); return Receipt(call.Need);
        } };
        await using var service = new AcquisitionCoordinator(producer, Small(), new Clock());
        await using var subscription = service.Attach(Need(1), Source, AcquisitionPriority.Background);
        Assert.Equal("ACQUISITION_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => subscription.Completion)).Code);
        Assert.Equal(20, subscription.ObservedPayloadBytes);
    }

    [Fact]
    public async Task Pre_canceled_acquisition_never_attaches_or_executes()
    {
        var producer = new Producer(); await using var service = new AcquisitionCoordinator(producer, Small());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AcquireAsync(Need(1), Source,
            AcquisitionPriority.Interactive, token: cancellation.Token)); Assert.Empty(producer.Calls);
    }
}
