using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>The priority lanes of <see cref="TableWorkScheduler"/> (D263). Ordering comes from the scheduler's own state, never from timing.</summary>
public sealed class TableWorkSchedulerTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const TablePriority Later = TablePriority.Background;
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task InteractiveWorkStartsWhileBackgroundSlotsAreHeldAndMoreIsQueued()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 1, maxBackground: 2);
        using var b1 = await scheduler.EnterAsync(Later, Ct);
        using var b2 = await scheduler.EnterAsync(Later, Ct);
        var b3 = scheduler.EnterAsync(Later, Ct).AsTask();

        var now = scheduler.EnterAsync(Now, Ct);
        Assert.True(now.IsCompletedSuccessfully);
        Assert.False(b3.IsCompleted);
        Assert.Equal(1, scheduler.Queued(Later));
        (await now).Dispose();
        Assert.False(b3.IsCompleted);

        b1.Dispose();
        (await b3).Dispose();
    }

    [Fact]
    public async Task QueuedBackgroundWorkStartsOnlyOnceNoInteractiveWorkIsQueued()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 1, maxBackground: 1);
        var i1 = await scheduler.EnterAsync(Now, Ct);
        var b1 = await scheduler.EnterAsync(Later, Ct);
        var b2 = scheduler.EnterAsync(Later, Ct).AsTask();
        var i2 = scheduler.EnterAsync(Now, Ct).AsTask();
        var i3 = scheduler.EnterAsync(Now, Ct).AsTask();

        // A free Background slot isn't taken while Interactive work waits.
        b1.Dispose();
        Assert.Equal((0, 1), (scheduler.Running(Later), scheduler.Queued(Later)));
        Assert.False(b2.IsCompleted);

        i1.Dispose();
        var second = await i2;
        Assert.False(b2.IsCompleted);
        Assert.False(i3.IsCompleted);

        second.Dispose();
        var third = await i3;
        // The last Interactive caller left the queue: Background goes now, beside the running Interactive work.
        var background = await b2;
        Assert.Equal((1, 1), (scheduler.Running(Now), scheduler.Running(Later)));
        third.Dispose();
        background.Dispose();
        Assert.Equal((0, 0), (scheduler.Running(Now), scheduler.Running(Later)));
    }

    [Fact]
    public async Task ACallerCanceledWhileQueuedLeavesTheQueue()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 1, maxBackground: 1);
        using var i1 = await scheduler.EnterAsync(Now, Ct);
        using var cancel = new CancellationTokenSource();
        var i2 = scheduler.EnterAsync(Now, cancel.Token).AsTask();
        var b1 = scheduler.EnterAsync(Later, Ct).AsTask();
        Assert.False(b1.IsCompleted);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => i2);
        Assert.Equal(0, scheduler.Queued(Now));
        // Nothing Interactive waits any more, so the queued Background caller starts.
        (await b1).Dispose();

        // Canceled before asking: nothing is queued.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scheduler.EnterAsync(Now, cancel.Token));
        Assert.Equal(0, scheduler.Queued(Now));
    }

    [Fact]
    public async Task LanesRunAtMostTheirSlotsAndQueueAtMostTheirBound()
    {
        var scheduler = new TableWorkScheduler(maxInteractive: 4, maxBackground: 2, maxQueued: 6);
        var waits = Enumerable.Range(0, 10).Select(_ => scheduler.EnterAsync(Now, Ct).AsTask()).ToList();
        Assert.Equal((4, 6), (scheduler.Running(Now), scheduler.Queued(Now)));
        await Assert.ThrowsAsync<TablesBusyException>(async () => await scheduler.EnterAsync(Now, Ct));

        var backgrounds = Enumerable.Range(0, 3).Select(_ => scheduler.EnterAsync(Later, Ct).AsTask()).ToList();
        Assert.Equal((0, 3), (scheduler.Running(Later), scheduler.Queued(Later)));

        // First come, first served; one freed slot starts one caller.
        for (var i = 0; i < waits.Count; i++)
        {
            var slot = await waits[i];
            Assert.True(scheduler.Running(Now) <= 4);
            Assert.Equal(Math.Min(4, waits.Count - i), scheduler.Running(Now));
            slot.Dispose();
            slot.Dispose();
        }
        Assert.Equal(0, scheduler.Running(Now));
        Assert.Equal((2, 1), (scheduler.Running(Later), scheduler.Queued(Later)));
        foreach (var wait in backgrounds) (await wait).Dispose();
        Assert.Equal(0, scheduler.Running(Later));
    }

    [Fact]
    public void BoundsBelowOneAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TableWorkScheduler(maxInteractive: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TableWorkScheduler(maxBackground: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TableWorkScheduler(maxQueued: -1));
    }
}
