using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemIconLeasesTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly ItemIconReadRequest Request = new(new("wow", new('a', 64), "us", "enUS"), new('b', 64), "123", new('c', 64));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Busy(ItemIconLeases pool)
    {
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => pool.OpenAsync(Request, default));
        Assert.Equal("ICON_IMAGE_BUSY", error.Code); Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Four_opening_admissions_refuse_a_fifth_without_reaching_storage()
    {
        var entered = Signal(); var release = Signal(); var count = 0;
        var store = new Store { Open = async token => { if (Interlocked.Increment(ref count) == 4) entered.SetResult(); await release.Task.WaitAsync(token); return new Lease(); } };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        var opens = Enumerable.Range(0, 4).Select(_ => pool.OpenAsync(Request, default)).ToArray();
        await entered.Task.WaitAsync(Deadline); await Busy(pool); Assert.Equal(4, count);
        release.SetResult(); var results = await Task.WhenAll(opens).WaitAsync(Deadline);
        Assert.Equal(4, results.Select(result => result.LeaseId).Distinct().Count());
    }

    [Fact]
    public async Task Draining_close_holds_capacity_and_every_disposal_is_serial()
    {
        var leases = Enumerable.Range(0, 4).Select(_ => new Lease { ReleaseDisposal = Signal() }).ToArray(); var index = 0;
        var store = new Store { Open = _ => Task.FromResult<IItemIconReadLease>(leases[index++]) };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        var handles = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.OpenAsync(Request, default)));
        var first = pool.CloseAsync(handles[0].LeaseId); await leases[0].DisposalEntered.Task.WaitAsync(Deadline);
        var second = pool.CloseAsync(handles[1].LeaseId); await Busy(pool);
        Assert.False(leases[1].DisposalEntered.Task.IsCompleted); Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        leases[0].ReleaseDisposal!.SetResult(); await first.WaitAsync(Deadline);
        await leases[1].DisposalEntered.Task.WaitAsync(Deadline); leases[1].ReleaseDisposal!.SetResult(); await second.WaitAsync(Deadline);
        leases[2].ReleaseDisposal!.SetResult(); leases[3].ReleaseDisposal!.SetResult();
    }

    [Fact]
    public async Task Canceled_open_keeps_its_admission_until_the_source_really_retires()
    {
        using var cancellation = new CancellationTokenSource();
        var release = Signal(); var canceled = Signal(); var calls = 0;
        var store = new Store { Open = async token =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call != 1) return new Lease();
            using var registration = token.Register(() => canceled.TrySetResult());
            await release.Task; token.ThrowIfCancellationRequested(); return new Lease();
        } };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        var opening = pool.OpenAsync(Request, cancellation.Token);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => pool.OpenAsync(Request, default)));
        await cancellation.CancelAsync(); await canceled.Task.WaitAsync(Deadline);
        Assert.False(opening.IsCompleted); await Busy(pool); Assert.Equal(4, calls);
        release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        var next = await pool.OpenAsync(Request, default); Assert.NotNull(next); Assert.Equal(5, calls);
    }

    [Fact]
    public async Task Null_source_descriptor_releases_admission_without_publishing()
    {
        var calls = 0;
        var store = new Store { Open = _ => Task.FromResult<IItemIconReadLease>(++calls == 1 ? null! : new Lease()) };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        Assert.Equal("ICON_IMAGE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.OpenAsync(Request, default))).Code);
        Assert.NotNull(await pool.OpenAsync(Request, default));
    }

    [Fact]
    public async Task Cancel_after_store_commit_disposes_before_return_and_never_publishes_a_handle()
    {
        using var cancellation = new CancellationTokenSource(); var lease = new Lease { ReleaseDisposal = Signal() };
        var store = new Store { Open = _ => { cancellation.Cancel(); return Task.FromResult<IItemIconReadLease>(lease); } };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        var opening = pool.OpenAsync(Request, cancellation.Token);
        await lease.DisposalEntered.Task.WaitAsync(Deadline); Assert.False(opening.IsCompleted);
        lease.ReleaseDisposal.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task Shutdown_cancels_open_and_waits_for_late_commit_and_real_cleanup()
    {
        var entered = Signal(); var release = Signal(); var canceled = Signal();
        var lease = new Lease { ReleaseDisposal = Signal() };
        var store = new Store { Open = async token => { using var registration = token.Register(() => canceled.TrySetResult()); entered.SetResult(); await release.Task; return lease; } };
        await using var icons = new ItemIconsService(store); var pool = new ItemIconLeases(icons);
        var open = pool.OpenAsync(Request, default); await entered.Task.WaitAsync(Deadline);
        var shutdown = pool.DisposeAsync().AsTask(); var repeated = pool.DisposeAsync().AsTask();
        await canceled.Task.WaitAsync(Deadline); Assert.False(shutdown.IsCompleted); Assert.False(repeated.IsCompleted);
        release.SetResult(); await lease.DisposalEntered.Task.WaitAsync(Deadline); Assert.False(open.IsCompleted);
        lease.ReleaseDisposal.SetResult(); await Task.WhenAll(shutdown, repeated).WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open); Assert.Equal(1, lease.Disposals);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.OpenAsync(Request, default))).Code);
    }

    [Fact]
    public async Task Expiry_uses_monotonic_time_from_open_completion_and_close_race_is_idempotent()
    {
        var clock = new Clock(); var releaseOpen = Signal(); var lease = new Lease { ReleaseDisposal = Signal() };
        var store = new Store { Open = async _ => { await releaseOpen.Task; return lease; } };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons, clock);
        var opening = pool.OpenAsync(Request, default); clock.Advance(TimeSpan.FromHours(2)); releaseOpen.SetResult();
        var handle = await opening.WaitAsync(Deadline); clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(lease.DisposalEntered.Task.IsCompleted); clock.Advance(TimeSpan.FromSeconds(1));
        await lease.DisposalEntered.Task.WaitAsync(Deadline);
        var close = pool.CloseAsync(handle.LeaseId); Assert.False(close.IsCompleted); lease.ReleaseDisposal.SetResult();
        await close.WaitAsync(Deadline); await pool.CloseAsync(handle.LeaseId); Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task Expiry_close_and_shutdown_share_one_actual_disposal()
    {
        var clock = new Clock(); var lease = new Lease { ReleaseDisposal = Signal() }; var store = new Store { Open = _ => Task.FromResult<IItemIconReadLease>(lease) };
        await using var icons = new ItemIconsService(store); var pool = new ItemIconLeases(icons, clock);
        var handle = await pool.OpenAsync(Request, default); clock.Advance(ItemIconLeases.Lifetime);
        await lease.DisposalEntered.Task.WaitAsync(Deadline);
        var close = pool.CloseAsync(handle.LeaseId); var shutdown = pool.DisposeAsync().AsTask();
        Assert.False(close.IsCompleted); Assert.False(shutdown.IsCompleted); lease.ReleaseDisposal.SetResult();
        await Task.WhenAll(close, shutdown).WaitAsync(Deadline); Assert.Equal(1, lease.Disposals);
    }

    [Theory]
    [InlineData("artifact")][InlineData("sha")][InlineData("uppercase")][InlineData("length0")][InlineData("lengthlarge")]
    [InlineData("width0")][InlineData("widthlarge")][InlineData("height0")][InlineData("heightlarge")]
    public async Task Invalid_proof_descriptor_retires_before_failure(string invalid)
    {
        var lease = new Lease { ReleaseDisposal = Signal() };
        switch (invalid)
        {
            case "artifact": lease.ArtifactId = new('d', 64); break;
            case "sha": lease.Sha256 = "../anything"; break;
            case "uppercase": lease.Sha256 = new('A', 64); break;
            case "length0": lease.ByteLength = 0; break;
            case "lengthlarge": lease.ByteLength = 5 * 1024 * 1024 + 1; break;
            case "width0": lease.Width = 0; break;
            case "widthlarge": lease.Width = 1025; break;
            case "height0": lease.Height = 0; break;
            case "heightlarge": lease.Height = 1025; break;
        }
        await using var icons = new ItemIconsService(new Store { Open = _ => Task.FromResult<IItemIconReadLease>(lease) });
        await using var pool = new ItemIconLeases(icons);
        var opening = pool.OpenAsync(Request, default); await lease.DisposalEntered.Task.WaitAsync(Deadline);
        Assert.False(opening.IsCompleted); lease.ReleaseDisposal.SetResult();
        Assert.Equal("ICON_IMAGE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => opening)).Code);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task Handles_are_canonical_opaque_and_descriptor_contains_no_paths_or_stream()
    {
        await using var icons = new ItemIconsService(new Store()); await using var pool = new ItemIconLeases(icons);
        var result = await pool.OpenAsync(Request, default);
        Assert.Matches("^[a-f0-9]{32}$", result.LeaseId); Assert.Equal("4660", result.ByteLength);
        Assert.Equal(new[] { "LeaseId", "ArtifactId", "Sha256", "ByteLength", "Width", "Height" },
            typeof(ItemIconLeaseDescriptor).GetProperties().Select(property => property.Name));
        var json = JsonSerializer.Serialize(result); Assert.DoesNotContain("/", json); Assert.DoesNotContain("Content", json);
        await pool.CloseAsync(new('f', 32)); await pool.CloseAsync(result.LeaseId); await pool.CloseAsync(result.LeaseId);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.CloseAsync("../source.png"))).Code);
    }

    [Fact]
    public async Task Failed_open_releases_its_slot_and_does_not_affect_other_reads()
    {
        var calls = 0; var store = new Store { Open = _ => ++calls == 1 ? Task.FromException<IItemIconReadLease>(new IOException("authored")) : Task.FromResult<IItemIconReadLease>(new Lease()) };
        await using var icons = new ItemIconsService(store); await using var pool = new ItemIconLeases(icons);
        await Assert.ThrowsAsync<IOException>(() => pool.OpenAsync(Request, default));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pool.OpenAsync(Request, default))); Assert.Equal(4, results.Length);
    }

    [Fact]
    public async Task Disposal_failure_is_observed_and_fails_closed_instead_of_leaking_new_admissions()
    {
        var lease = new Lease { Failure = new IOException("authored disposer failure") };
        await using var icons = new ItemIconsService(new Store { Open = _ => Task.FromResult<IItemIconReadLease>(lease) });
        var pool = new ItemIconLeases(icons); var handle = await pool.OpenAsync(Request, default);
        Assert.Equal("ICON_IMAGE_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.CloseAsync(handle.LeaseId))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.OpenAsync(Request, default))).Code);
        Assert.Equal("ICON_IMAGE_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.DisposeAsync().AsTask())).Code);
        Assert.Equal("ICON_IMAGE_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => pool.DisposeAsync().AsTask())).Code);
    }

    private sealed class Store : IItemIconsStore
    {
        internal Func<CancellationToken, Task<IItemIconReadLease>> Open = _ => Task.FromResult<IItemIconReadLease>(new Lease());
        public Task<IItemIconReadLease> OpenAsync(ItemIconReadRequest request, CancellationToken token) => Open(token);
        public Task<ItemIconBatch> PrepareBatchAsync(ItemIconBatchRequest request, Func<ItemIconProgress, Task> progress, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Lease : IItemIconReadLease
    {
        public Stream Content => Stream.Null;
        public string ArtifactId { get; set; } = Request.ImageArtifactId;
        public string Sha256 { get; set; } = new('d', 64);
        public long ByteLength { get; set; } = 4660;
        public int Width { get; set; } = 64;
        public int Height { get; set; } = 64;
        internal TaskCompletionSource DisposalEntered = Signal();
        internal TaskCompletionSource? ReleaseDisposal;
        internal Exception? Failure;
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); DisposalEntered.TrySetResult();
            if (ReleaseDisposal is not null) await ReleaseDisposal.Task;
            if (Failure is not null) throw Failure;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private Timer? timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => timer = new(callback, state);
        internal void Advance(TimeSpan by) { Interlocked.Add(ref ticks, by.Ticks); timer!.Tick(); }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            internal void Tick() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !disposed;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
