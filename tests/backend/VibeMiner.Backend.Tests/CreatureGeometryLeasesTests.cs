using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;
using static VibeMiner.Backend.Tests.CreatureGeometryTestFixture;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureGeometryLeasesTests
{
    private static readonly CreatureGeometryOpenRequest Request = new(Selection, new('4', 64));
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Opening_and_draining_share_two_admissions_and_duplicate_close_joins()
    {
        var first = new ControlledLease(); var second = new ControlledLease();
        var opened = new TaskCompletionSource<ICreatureGeometryReadLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var registry = new CreatureGeometryLeases((_, _) => ++calls == 1 ? opened.Task : Task.FromResult<ICreatureGeometryReadLease>(second));
        var pending = registry.OpenAsync(Request, default);
        var b = await registry.OpenAsync(Request, default);
        await Busy(registry); Assert.Equal(2, calls);
        opened.SetResult(first); var a = await pending;
        Assert.NotEqual(a.LeaseId, b.LeaseId); Assert.Matches("^[0-9a-f]{32}$", a.LeaseId);
        var close = registry.CloseAsync(a.LeaseId); var duplicate = registry.CloseAsync(a.LeaseId);
        await first.Disposing.Task.WaitAsync(Guard); await Busy(registry);
        first.DisposeRelease.SetResult(); Assert.False(close.IsCompleted);
        first.Retired.SetResult(); await Task.WhenAll(close, duplicate).WaitAsync(Guard);
        Assert.Equal(1, first.Disposals);
        second.Release(); await registry.CloseAsync(b.LeaseId);
        await registry.CloseAsync(a.LeaseId);
    }

    [Fact]
    public async Task Canceled_open_owns_late_lease_until_disposal_and_retirement()
    {
        var original = new TaskCompletionSource<ICreatureGeometryReadLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ControlledLease(); var other = new ControlledLease();
        var calls = 0;
        await using var registry = new CreatureGeometryLeases((_, _) => ++calls == 1 ? original.Task : Task.FromResult<ICreatureGeometryReadLease>(other));
        using var cancellation = new CancellationTokenSource();
        var pending = registry.OpenAsync(Request, cancellation.Token); cancellation.Cancel();
        var b = await registry.OpenAsync(Request, default); await Busy(registry);
        original.SetResult(lease); await lease.Disposing.Task.WaitAsync(Guard);
        Assert.False(pending.IsCompleted); await Busy(registry);
        lease.DisposeRelease.SetResult(); Assert.False(pending.IsCompleted);
        lease.Retired.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        other.Release(); await registry.CloseAsync(b.LeaseId);
    }

    [Fact]
    public async Task Shutdown_starts_both_closes_before_waiting_and_rejects_new_open()
    {
        var leases = new[] { new ControlledLease(), new ControlledLease() }; var calls = 0;
        var registry = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(leases[calls++]));
        await registry.OpenAsync(Request, default); await registry.OpenAsync(Request, default);
        var shutdown = registry.DisposeAsync().AsTask();
        await Task.WhenAll(leases.Select(l => l.Disposing.Task)).WaitAsync(Guard);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.OpenAsync(Request, default))).Code);
        leases[1].Release(); Assert.False(shutdown.IsCompleted);
        leases[0].Release(); await shutdown.WaitAsync(Guard); await registry.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_failure_still_waits_authoritative_retirement_and_retains_debt()
    {
        var lease = new ControlledLease { DisposeError = new IOException("Authored failure") };
        var registry = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease));
        var descriptor = await registry.OpenAsync(Request, default);
        var close = registry.CloseAsync(descriptor.LeaseId); await lease.Disposing.Task.WaitAsync(Guard);
        lease.DisposeRelease.SetResult(); Assert.False(close.IsCompleted);
        var shutdown = registry.DisposeAsync().AsTask(); // Shutdown is already waiting when the failed attempt retires.
        lease.Retired.SetResult(); Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => close)).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => shutdown);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.CloseAsync(descriptor.LeaseId))).Code);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.CloseAsync(descriptor.LeaseId))).Code);
    }

    [Fact]
    public async Task Authoritative_failure_is_not_successful_dispose_credit()
    {
        var lease = new ControlledLease(); var registry = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease));
        var descriptor = await registry.OpenAsync(Request, default);
        var close = registry.CloseAsync(descriptor.LeaseId); await lease.Disposing.Task.WaitAsync(Guard);
        lease.DisposeRelease.SetResult(); lease.Retired.SetException(new IOException("Authored retirement failure"));
        await Assert.ThrowsAsync<DiscoveryException>(() => close);
        await Assert.ThrowsAsync<DiscoveryException>(() => registry.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Cleanup_failure_before_transfer_is_charged_and_fail_closed()
    {
        var registry = new CreatureGeometryLeases((_, _) => Task.FromException<ICreatureGeometryReadLease>(new DiscoveryException("CREATURE_GEOMETRY_CLEANUP_FAILED", "Authored debt")));
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.OpenAsync(Request, default))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.OpenAsync(Request, default))).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => registry.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Invalid_descriptor_is_closed_before_error_and_manifest_is_owned()
    {
        var lease = new ControlledLease { Manifest = Manifest() with { ArtifactId = new('7', 64) } };
        await using var registry = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease));
        var pending = registry.OpenAsync(Request, default); await lease.Disposing.Task.WaitAsync(Guard);
        Assert.False(pending.IsCompleted); lease.Release(); await Assert.ThrowsAsync<DiscoveryException>(() => pending);
        var good = new ControlledLease(); var min = new float[] { -6, -4, 0 }; good.Manifest = Manifest() with { Bounds = new(min, new float[] { 3, 7, 8 }) };
        await using var second = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(good));
        var descriptor = await second.OpenAsync(Request, default); min[0] = 100;
        Assert.Equal(-6, descriptor.Manifest.Bounds.Min[0]); good.Release(); await second.CloseAsync(descriptor.LeaseId);
    }

    [Fact]
    public async Task Ordinary_open_failure_returns_capacity_and_close_requires_canonical_id()
    {
        var good = new ControlledLease(); var calls = 0;
        await using var registry = new CreatureGeometryLeases((_, _) => ++calls == 1 ? Task.FromException<ICreatureGeometryReadLease>(new IOException("Authored unavailable")) : Task.FromResult<ICreatureGeometryReadLease>(good));
        await Assert.ThrowsAsync<IOException>(() => registry.OpenAsync(Request, default));
        var descriptor = await registry.OpenAsync(Request, default); good.Release(); await registry.CloseAsync(descriptor.LeaseId);
        await registry.CloseAsync(new('a', 32));
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => registry.CloseAsync("../not-a-handle"))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_notification_and_original_open_both_retire_before_capacity(bool throwing)
    {
        var original = new TaskCompletionSource<ICreatureGeometryReadLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Signal(); using var release = new ManualResetEventSlim();
        CancellationTokenRegistration registration = default;
        var other = new ControlledLease(); var calls = 0;
        var registry = new CreatureGeometryLeases((_, token) =>
        {
            if (++calls != 1) return Task.FromResult<ICreatureGeometryReadLease>(other);
            // Test-owned registration survives original task completion, so invocation cannot be skipped.
            registration = token.Register(() =>
            {
                entered.TrySetResult();
                if (!release.Wait(Guard)) throw new TimeoutException("Authored callback guard");
                if (throwing) throw new IOException("Authored callback failure");
            });
            return original.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var pending = registry.OpenAsync(Request, cancellation.Token);
        var second = await registry.OpenAsync(Request, default);
        try
        {
            cancellation.Cancel(); await entered.Task.WaitAsync(Guard);
            await Busy(registry); Assert.False(pending.IsCompleted);
            if (throwing) { release.Set(); Assert.False(pending.IsCompleted); }
            original.SetException(new IOException("Authored original terminal failure"));
            if (!throwing) { Assert.False(pending.IsCompleted); await Busy(registry); release.Set(); }
            await Assert.ThrowsAsync<IOException>(() => pending);
            other.Release(); await registry.CloseAsync(second.LeaseId);
        }
        finally
        {
            release.Set(); original.TrySetException(new IOException("Authored cleanup")); other.Release();
            try { await registry.DisposeAsync().AsTask().WaitAsync(Guard); }
            finally { await registration.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task App_owns_one_absolute_timer_and_expiry_closes_without_a_host_sweep()
    {
        var clock = new ManualTimerClock(); var lease = new ControlledLease();
        var original = new TaskCompletionSource<ICreatureGeometryReadLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Store { Open = (_, _) => original.Task };
        await using var app = new CreatureGeometryService(store, clock);
        await using var registry = new CreatureGeometryLeases(app);
        var pending = registry.OpenAsync(Request, default);
        Assert.Equal(1, clock.TimerCount);
        clock.Advance(TimeSpan.FromSeconds(29)); original.SetResult(lease);
        var descriptor = await pending.WaitAsync(Guard);
        clock.Advance(TimeSpan.FromSeconds(1));
        await lease.Disposing.Task.WaitAsync(Guard);
        // Expiry at original admission+30s, not response+30s; registry created no timer.
        Assert.Equal(1, clock.TimerCount); lease.Release();
        await registry.CloseAsync(descriptor.LeaseId).WaitAsync(Guard);
    }

    [Fact]
    public async Task Completed_authoritative_retirement_requests_registry_cleanup_without_close()
    {
        var lease = new ControlledLease();
        await using var registry = new CreatureGeometryLeases((_, _) => Task.FromResult<ICreatureGeometryReadLease>(lease));
        var descriptor = await registry.OpenAsync(Request, default);
        lease.Retired.SetResult();
        await lease.Disposing.Task.WaitAsync(Guard);
        Assert.Equal(1, lease.Disposals); lease.DisposeRelease.SetResult();
        await registry.CloseAsync(descriptor.LeaseId).WaitAsync(Guard);
    }

    private static async Task Busy(CreatureGeometryLeases registry) => Assert.Equal("CREATURE_GEOMETRY_BUSY",
        (await Assert.ThrowsAsync<DiscoveryException>(() => registry.OpenAsync(Request, default))).Code);

    private sealed class ControlledLease : ICreatureGeometryReadLease
    {
        internal readonly TaskCompletionSource Disposing = Signal(), DisposeRelease = Signal(), Retired = Signal();
        internal Exception? DisposeError; internal int Disposals;
        public Stream Content { get; } = new MemoryStream(new byte[320], false);
        public CreatureGeometryManifest Manifest { get; set; } = CreatureGeometryTestFixture.Manifest();
        public Task Retirement => Retired.Task;
        public async ValueTask DisposeAsync()
        { Interlocked.Increment(ref Disposals); Disposing.TrySetResult(); await DisposeRelease.Task; if (DisposeError is { } error) throw error; await Content.DisposeAsync(); }
        internal void Release() { DisposeRelease.TrySetResult(); Retired.TrySetResult(); }
    }
}
