using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;
using static VibeMiner.Backend.Tests.CreatureGeometryTestFixture;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureGeometryServiceTests
{
    [Fact]
    public async Task Local_read_and_open_never_invoke_prepare_and_exact_request_is_owned()
    {
        var store = new Store(); await using var service = new CreatureGeometryService(store);
        var result = await service.ReadAsync(new(Selection)); Assert.Equal(CreatureGeometryState.NotPrepared, result.State);
        await using var lease = await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        Assert.Equal(1, store.Reads); Assert.Equal(1, store.Opens); Assert.Equal(0, store.Prepares);
    }
    [Fact]
    public async Task Two_consumers_cancel_independently_and_third_is_immediately_busy()
    {
        var release = Signal(); var entered = Signal(); var store = new Store();
        store.Prepare = async (r, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Result(r.Selection); };
        await using var service = new CreatureGeometryService(store); using var stop = new CancellationTokenSource();
        var first = service.PrepareAsync(new(Selection), token: stop.Token); await entered.Task.WaitAsync(Guard);
        var second = service.PrepareAsync(new(Selection with { CreatureId = "124" }));
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection)))).Code);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(second.IsCompleted);
        await service.ReadAsync(new(Selection)); release.TrySetResult(); await second.WaitAsync(Guard);
    }
    [Fact]
    public async Task Cancellation_and_shutdown_keep_both_source_cleanups_charged()
    {
        var entered = Signal(); var cleaning = Signal(); var cleanup = Signal(); var never = Signal(); var store = new Store();
        store.Prepare = async (r, _, token) =>
        {
            entered.TrySetResult(); try { await never.Task.WaitAsync(token); }
            finally { cleaning.TrySetResult(); await cleanup.Task; }
            return Result(r.Selection);
        };
        var service = new CreatureGeometryService(store); using var stop = new CancellationTokenSource();
        var a = service.PrepareAsync(new(Selection), token: stop.Token); var b = service.PrepareAsync(new(Selection));
        await entered.Task.WaitAsync(Guard); stop.Cancel(); await cleaning.Task.WaitAsync(Guard);
        Assert.False(a.IsCompleted); Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection)))).Code);
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); cleanup.TrySetResult();
        await closing.WaitAsync(Guard); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
    }
    [Fact]
    public async Task Fifteen_minute_deadlines_are_per_consumer_and_wait_actual_cleanup()
    {
        var clock = new ManualTimerClock(); var never = Signal(); var cleaning = Signal(); var cleanup = Signal(); var store = new Store();
        store.Prepare = async (r, _, token) => { try { await never.Task.WaitAsync(token); } finally { if (r.Selection.CreatureId == "123") { cleaning.TrySetResult(); await cleanup.Task; } } return Result(r.Selection); };
        var service = new CreatureGeometryService(store, clock);
        var a = service.PrepareAsync(new(Selection)); clock.Advance(TimeSpan.FromMinutes(5));
        var b = service.PrepareAsync(new(Selection with { CreatureId = "124" })); clock.Advance(TimeSpan.FromMinutes(10));
        await cleaning.Task.WaitAsync(Guard); Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); cleanup.TrySetResult();
        Assert.Equal("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", (await Assert.ThrowsAsync<DiscoveryException>(() => a)).Code);
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.Equal("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", (await Assert.ThrowsAsync<DiscoveryException>(() => b)).Code);
        await service.DisposeAsync();
    }
    [Fact]
    public async Task Completed_preparations_keep_admission_until_blocked_observers_retire()
    {
        var observed = Signal(); var observerRelease = Signal(); var sourceRelease = Signal(); var store = new Store();
        store.Prepare = async (r, report, token) => { await report(new("modelVerification", "Verifying model", "bytes", 1, 2)); await sourceRelease.Task.WaitAsync(token); return Result(r.Selection); };
        var service = new CreatureGeometryService(store); var count = 0;
        Task Observe(CreatureGeometryProgress _) { if (Interlocked.Increment(ref count) == 2) observed.TrySetResult(); return observerRelease.Task; }
        var a = service.PrepareAsync(new(Selection), Observe); var b = service.PrepareAsync(new(Selection), Observe);
        await observed.Task.WaitAsync(Guard); sourceRelease.TrySetResult();
        await Task.WhenAll(store.PreparationTasks).WaitAsync(Guard); await service.ReadAsync(new(Selection));
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection)))).Code);
        var closing = service.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted); observerRelease.TrySetResult();
        // Shutdown may win the post-producer cancellation race; it cannot bypass observer retirement.
        try { await Task.WhenAll(a, b).WaitAsync(Guard); } catch (OperationCanceledException) { }
        await closing.WaitAsync(Guard); Assert.True(a.IsCompleted); Assert.True(b.IsCompleted);
    }
    [Fact]
    public async Task Observer_coalesces_latest_without_blocking_source_or_peer()
    {
        var clock = new ManualTimerClock(); var first = Signal(); var latest = Signal(); var release = Signal(); var store = new Store();
        Func<CreatureGeometryProgress, Task>? report = null;
        store.Prepare = async (r, callback, token) => { report = callback; await release.Task.WaitAsync(token); return Result(r.Selection); };
        await using var service = new CreatureGeometryService(store, clock); var seen = new List<long?>();
        var call = service.PrepareAsync(new(Selection), p => { seen.Add(p.Completed); if (p.Completed == 0) first.TrySetResult(); if (p.Completed == 1000) latest.TrySetResult(); return Task.CompletedTask; });
        await report!(new("modelAcquisition", "Acquiring model", "bytes", 0, 1000)); await first.Task.WaitAsync(Guard);
        // The pump arms its throttle timer after the first callback returns. Advancing before that would arm it at 200 ms, which never fires.
        // The service's 15-minute preparation deadline is armed too, so skip it.
        Assert.Equal(TimeSpan.FromMilliseconds(100).Ticks, await clock.Armed(due => due < TimeSpan.FromMinutes(15).Ticks).WaitAsync(Guard));
        for (var i = 1; i <= 1000; i++) Assert.True(report(new("modelAcquisition", "Acquiring model", "bytes", i, 1000)).IsCompletedSuccessfully);
        clock.Advance(TimeSpan.FromMilliseconds(100)); await latest.Task.WaitAsync(Guard); Assert.Equal(new long?[] { 0, 1000 }, seen);
        release.TrySetResult(); await call.WaitAsync(Guard);
    }
    [Fact]
    public async Task Two_returned_leases_keep_slots_until_inner_disposal_and_close_is_idempotent()
    {
        var release = Signal(); var a = new Lease { Release = release.Task }; var b = new Lease(); var store = new Store(); var next = 0;
        store.Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(Interlocked.Increment(ref next) == 1 ? a : b);
        await using var service = new CreatureGeometryService(store);
        var x = await service.OpenAsync(new(Selection, Manifest().ArtifactId)); var y = await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        var close = x.DisposeAsync().AsTask(); await a.Closing.Task.WaitAsync(Guard); var duplicate = x.DisposeAsync().AsTask(); Assert.False(close.IsCompleted);
        Assert.False(x.Retirement.IsCompleted);
        Assert.Equal("CREATURE_GEOMETRY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.OpenAsync(new(Selection, Manifest().ArtifactId)))).Code);
        await service.ReadAsync(new(Selection)); release.TrySetResult(); await Task.WhenAll(close, duplicate).WaitAsync(Guard); Assert.Equal(1, a.Closes);
        await x.Retirement.WaitAsync(Guard);
        await y.DisposeAsync(); Assert.Equal(1, b.Closes);
    }
    [Fact]
    public async Task Canceling_late_open_does_not_retire_admission_until_late_lease_is_closed()
    {
        var entered = Signal(); var open = Signal(); var close = Signal(); var inner = new Lease { Release = close.Task }; var store = new Store();
        store.Open = async (_, _) => { entered.TrySetResult(); await open.Task; return inner; };
        var service = new CreatureGeometryService(store); using var stop = new CancellationTokenSource();
        var call = service.OpenAsync(new(Selection, Manifest().ArtifactId), stop.Token); await entered.Task.WaitAsync(Guard); stop.Cancel();
        var shutdown = service.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted); open.TrySetResult(); await inner.Closing.Task.WaitAsync(Guard);
        Assert.False(call.IsCompleted); Assert.False(shutdown.IsCompleted); close.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call); await shutdown.WaitAsync(Guard); Assert.Equal(1, inner.Closes);
    }
    [Fact]
    public async Task Absolute_lease_deadline_includes_open_and_never_restarts_on_return()
    {
        var clock = new ManualTimerClock(); var open = Signal(); var inner = new Lease(); var store = new Store();
        store.Open = async (_, _) => { await open.Task; return inner; };
        await using var service = new CreatureGeometryService(store, clock);
        var call = service.OpenAsync(new(Selection, Manifest().ArtifactId)); clock.Advance(TimeSpan.FromSeconds(29)); open.TrySetResult();
        var lease = await call.WaitAsync(Guard); clock.Advance(TimeSpan.FromSeconds(1)); await inner.Closing.Task.WaitAsync(Guard); await lease.DisposeAsync();
        Assert.Equal(1, inner.Closes); Assert.Throws<ObjectDisposedException>(() => lease.Content);
    }
    [Fact]
    public async Task Expired_open_still_closes_its_late_return_and_reports_deadline()
    {
        var clock = new ManualTimerClock(); var open = Signal(); var inner = new Lease(); var store = new Store();
        store.Open = async (_, _) => { await open.Task; return inner; };
        await using var service = new CreatureGeometryService(store, clock);
        var call = service.OpenAsync(new(Selection, Manifest().ArtifactId)); clock.Advance(TimeSpan.FromSeconds(30)); Assert.False(call.IsCompleted); open.TrySetResult();
        Assert.Equal("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", (await Assert.ThrowsAsync<DiscoveryException>(() => call)).Code); Assert.Equal(1, inner.Closes);
    }
    [Fact]
    public async Task Bad_manifest_retires_inner_lease_and_disposal_failure_fails_closed()
    {
        var store = new Store(); var bad = new Lease { Manifest = Manifest() with { LineIndexCount = 7 } }; store.Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(bad);
        var service = new CreatureGeometryService(store);
        Assert.Equal("CREATURE_GEOMETRY_RESULT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.OpenAsync(new(Selection, Manifest().ArtifactId)))).Code); Assert.Equal(1, bad.Closes);
        var failed = new Lease { Fail = true }; store.Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(failed);
        var lease = await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => lease.DisposeAsync().AsTask())).Code);
        Assert.True(lease.Retirement.IsFaulted); Assert.Equal(1, failed.RetirementReads);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(new(Selection)))).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.DisposeAsync().AsTask()); Assert.Equal(1, failed.Closes);
    }
    [Fact]
    public async Task Shutdown_starts_both_lease_closes_before_waiting_for_either()
    {
        var release = Signal(); var first = new Lease { Release = release.Task }; var second = new Lease { Release = release.Task }; var store = new Store(); var count = 0;
        store.Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(++count == 1 ? first : second);
        var service = new CreatureGeometryService(store);
        await service.OpenAsync(new(Selection, Manifest().ArtifactId)); await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        var closing = service.DisposeAsync().AsTask(); await Task.WhenAll(first.Closing.Task, second.Closing.Task).WaitAsync(Guard); Assert.False(closing.IsCompleted);
        release.TrySetResult(); await closing.WaitAsync(Guard); Assert.Equal(1, first.Closes); Assert.Equal(1, second.Closes);
    }
    [Fact]
    public async Task One_deadline_timer_covers_both_preparations_and_both_held_leases()
    {
        var clock = new ManualTimerClock(); var hold = Signal(); var store = new Store();
        store.Prepare = async (r, _, ct) => { await hold.Task.WaitAsync(ct); return Result(r.Selection); };
        var service = new CreatureGeometryService(store, clock);
        var a = service.PrepareAsync(new(Selection)); var b = service.PrepareAsync(new(Selection));
        var x = await service.OpenAsync(new(Selection, Manifest().ArtifactId)); var y = await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        Assert.Equal(1, clock.TimerCount); var shutdown = service.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        await shutdown.WaitAsync(Guard); await Task.WhenAll(x.Retirement, y.Retirement).WaitAsync(Guard);
    }
    [Fact]
    public async Task Retirement_worker_starts_new_peer_close_while_first_close_is_held()
    {
        var release = Signal(); var first = new Lease { Release = release.Task }; var second = new Lease(); var store = new Store(); var count = 0;
        store.Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(++count == 1 ? first : second);
        await using var service = new CreatureGeometryService(store);
        var x = await service.OpenAsync(new(Selection, Manifest().ArtifactId)); var y = await service.OpenAsync(new(Selection, Manifest().ArtifactId));
        var a = x.DisposeAsync().AsTask(); await first.Closing.Task.WaitAsync(Guard);
        await y.DisposeAsync().AsTask().WaitAsync(Guard); Assert.Equal(1, second.Closes); Assert.False(a.IsCompleted);
        release.TrySetResult(); await a.WaitAsync(Guard);
    }
    [Fact]
    public async Task Two_local_calls_include_canceled_cleanup_without_consuming_preparation_slots()
    {
        var never = Signal(); var cleaning = Signal(); var release = Signal(); var store = new Store();
        store.Read = async (r, ct) => { try { await never.Task.WaitAsync(ct); } finally { cleaning.TrySetResult(); await release.Task; } return Result(r.Selection); };
        var service = new CreatureGeometryService(store); using var stop = new CancellationTokenSource();
        var a = service.ReadAsync(new(Selection), stop.Token); var b = service.ReadAsync(new(Selection)); stop.Cancel(); await cleaning.Task.WaitAsync(Guard);
        Assert.Equal("CREATURES_QUERY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(new(Selection)))).Code);
        await service.PrepareAsync(new(Selection)); var shutdown = service.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted); release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b); await shutdown.WaitAsync(Guard);
    }
    [Fact]
    public async Task Throwing_observer_and_cancellation_callback_do_not_skip_cleanup()
    {
        var observed = Signal(); var release = Signal(); var callbackInvoked = Signal(); var cleaning = Signal(); var cleanup = Signal(); var store = new Store();
        store.Prepare = async (r, report, ct) =>
        {
            using var callback = ct.Register(() => { callbackInvoked.TrySetResult(); throw new InvalidOperationException("Authored cancellation callback."); });
            await report(new("modelParsing", "Parsing model", "entries", 0, 1));
            try { await release.Task; ct.ThrowIfCancellationRequested(); } finally { cleaning.TrySetResult(); await cleanup.Task; }
            return Result(r.Selection);
        };
        var service = new CreatureGeometryService(store); using var stop = new CancellationTokenSource();
        var a = service.PrepareAsync(new(Selection), _ => { observed.TrySetResult(); throw new InvalidOperationException("Authored observer failure."); }, stop.Token);
        await observed.Task.WaitAsync(Guard); stop.Cancel(); await callbackInvoked.Task.WaitAsync(Guard);
        Assert.False(cleaning.Task.IsCompleted); Assert.False(a.IsCompleted);
        release.TrySetResult(); await cleaning.Task.WaitAsync(Guard);
        var shutdown = service.DisposeAsync().AsTask(); Assert.False(shutdown.IsCompleted);
        cleanup.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await shutdown.WaitAsync(Guard);
    }
    [Fact]
    public async Task Dispose_return_alone_is_not_inner_retirement_confirmation()
    {
        var inner = new SplitRetirementLease(); var store = new Store { Open = (_, _) => Task.FromResult<ICreatureGeometryReadLease>(inner) };
        await using var service = new CreatureGeometryService(store);
        var lease = await service.OpenAsync(new(Selection, Manifest().ArtifactId)); var closing = lease.DisposeAsync().AsTask();
        await inner.Closing.Task.WaitAsync(Guard); Assert.False(closing.IsCompleted); Assert.False(lease.Retirement.IsCompleted);
        inner.Confirm(); await closing.WaitAsync(Guard); await lease.Retirement.WaitAsync(Guard);
    }
    [Fact]
    public async Task Source_cleanup_debt_rejects_future_admission_and_is_reported_at_shutdown()
    {
        var store = new Store { Read = (_, _) => throw new DiscoveryException("CREATURE_GEOMETRY_CLEANUP_FAILED", "Authored source debt.") };
        var service = new CreatureGeometryService(store);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(new(Selection)));
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection)))).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.DisposeAsync().AsTask());
    }
    private sealed class SplitRetirementLease : ICreatureGeometryReadLease
    {
        internal readonly TaskCompletionSource Closing = Signal(); private readonly TaskCompletionSource retired = Signal();
        public Stream Content { get; } = new MemoryStream(new byte[320], writable: false);
        public CreatureGeometryManifest Manifest => CreatureGeometryTestFixture.Manifest();
        public Task Retirement => retired.Task;
        public ValueTask DisposeAsync() { Closing.TrySetResult(); return ValueTask.CompletedTask; }
        internal void Confirm() { Content.Dispose(); retired.TrySetResult(); }
    }
}
