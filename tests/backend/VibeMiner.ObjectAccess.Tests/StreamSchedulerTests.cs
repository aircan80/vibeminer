using VibeMiner.Cdn;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class StreamSchedulerTests
{
    private static StreamScheduler Make(int min = 2, int max = 4, int interactive = 1) => new(new() { MinBackground = min, MaxBackground = max, Interactive = interactive });

    [Fact]
    public async Task BackgroundStopsAtItsLimitAndInteractiveStillGetsItsReserve()
    {
        var s = Make();
        var held = new List<StreamScheduler.Lease>();
        for (var i = 0; i < 2; i++) held.Add(await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None));
        var third = s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None);
        Assert.False(third.IsCompleted);
        var screen = await s.AcquireAsync(s.NewTicket(FetchPriority.Interactive), CancellationToken.None);
        Assert.True(screen.InteractiveSlot);
        // The reserve is busy too: the next interactive request waits, and is served before the waiting background one.
        var screen2 = s.AcquireAsync(s.NewTicket(FetchPriority.Interactive), CancellationToken.None);
        Assert.False(screen2.IsCompleted);
        held[0].Dispose();
        Assert.False((await screen2).InteractiveSlot);
        Assert.False(third.IsCompleted);
        held[1].Dispose();
        await third;
    }

    [Fact]
    public async Task TheLimitGrowsAfterAFullLimitOfSuccessesAndShrinksOnTimeoutsButStaysInBounds()
    {
        var s = Make(min: 4, max: 6);
        for (var round = 0; round < 40; round++)
            (await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None)).Complete(null);
        Assert.Equal(6, s.BackgroundLimit);
        (await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None)).Complete(CdnFailure.Timeout);
        Assert.Equal(5, s.BackgroundLimit);
        for (var round = 0; round < 10; round++)
            (await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None)).Complete(CdnFailure.HttpStatus);
        Assert.Equal(4, s.BackgroundLimit);
        // A 404 or a bad object says nothing about the CDN's load.
        (await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None)).Complete(CdnFailure.NotFound);
        Assert.Equal(4, s.BackgroundLimit);
    }

    [Fact]
    public async Task AWaitingBackgroundTicketCanBePromoted()
    {
        var s = Make(min: 1, max: 1);
        var busy = await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None);
        var ticket = s.NewTicket(FetchPriority.Background);
        var waiting = s.AcquireAsync(ticket, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        s.Promote(ticket);
        var lease = await waiting;
        Assert.True(lease.InteractiveSlot);
        Assert.Equal(FetchPriority.Interactive, ticket.CurrentPriority);
        lease.Dispose(); busy.Dispose();
        Assert.Equal(0, s.BackgroundActive + s.InteractiveActive);
    }

    [Fact]
    public async Task AFollowUpTicketIsPromotedWithItsTransferAndNothingElseIs()
    {
        var s = Make(min: 1, max: 1);
        var busy = await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None);
        var parent = s.NewTicket(FetchPriority.Background);
        var retry = s.NewTicket(parent);
        var deeper = s.NewTicket(retry);
        var unrelated = s.NewTicket(FetchPriority.Background);
        var waiting = s.AcquireAsync(retry, CancellationToken.None);
        var other = s.AcquireAsync(unrelated, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        s.Promote(parent);
        var lease = await waiting;
        Assert.True(lease.InteractiveSlot);
        Assert.Equal(FetchPriority.Interactive, deeper.CurrentPriority);
        Assert.Equal(FetchPriority.Background, unrelated.CurrentPriority);
        Assert.Equal(FetchPriority.Interactive, s.NewTicket(parent).CurrentPriority);   // made after the promotion
        Assert.False(other.IsCompleted);
        lease.Dispose(); busy.Dispose();
        (await other).Dispose();
    }

    [Fact]
    public async Task ACanceledWaitLeavesNoTraceAndALeaseIsReleasedOnce()
    {
        var s = Make(min: 1, max: 1);
        var busy = await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = s.AcquireAsync(s.NewTicket(FetchPriority.Background), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        busy.Complete(null); busy.Dispose(); busy.Complete(CdnFailure.Timeout);
        Assert.Equal(0, s.BackgroundActive);
        Assert.Equal(1, s.BackgroundLimit);
        var next = await s.AcquireAsync(s.NewTicket(FetchPriority.Background), CancellationToken.None);
        Assert.Equal(1, s.BackgroundActive);
        next.Dispose();
        var ticket = s.NewTicket(FetchPriority.Background);
        (await s.AcquireAsync(ticket, CancellationToken.None)).Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.AcquireAsync(ticket, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new StreamScheduler(new() { MinBackground = 5, MaxBackground = 4 }));
    }
}
