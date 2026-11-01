using VibeMiner.Cdn;

namespace VibeMiner.ObjectAccess;

public enum FetchPriority
{
    /// <summary>Something a screen is waiting for. Has its own streams and goes ahead of every background request.</summary>
    Interactive,
    /// <summary>Preload and Download all.</summary>
    Background,
}

public sealed record StreamOptions
{
    /// <summary>Background streams start here and never drop below it.</summary>
    public int MinBackground { get; init; } = 16;
    /// <summary>Background streams grow to at most this many.</summary>
    public int MaxBackground { get; init; } = 32;
    /// <summary>Streams only interactive requests may use, so one is always free for a screen.</summary>
    public int Interactive { get; init; } = 4;

    internal void Validate()
    {
        if (MinBackground < 1 || MaxBackground < MinBackground || Interactive < 1)
            throw new ArgumentException("Streams need 1 <= min <= max background and at least one interactive stream.");
    }
}

/// <summary>
/// Hands out HTTP streams. Background work gets an adaptive limit between <see cref="StreamOptions.MinBackground"/> and
/// <see cref="StreamOptions.MaxBackground"/>: it grows by one after a full limit's worth of successes in a row and drops a quarter
/// on a timeout, connection failure or 5xx. Interactive work has <see cref="StreamOptions.Interactive"/> streams of its own,
/// borrows a free background stream when those are busy, and is always granted before any waiting background request.
/// A waiting background request can be promoted when a screen starts waiting on the same object.
/// </summary>
public sealed class StreamScheduler
{
    private readonly StreamOptions options;
    private readonly Lock gate = new();
    private readonly LinkedList<Ticket> interactiveQueue = new(), backgroundQueue = new();
    private int backgroundActive, interactiveActive, streak;

    public StreamScheduler(StreamOptions? options = null)
    {
        this.options = options ?? new(); this.options.Validate();
        BackgroundLimit = this.options.MinBackground;
    }

    public int BackgroundLimit { get; private set; }
    public int BackgroundActive { get { lock (gate) return backgroundActive; } }
    public int InteractiveActive { get { lock (gate) return interactiveActive; } }

    /// <summary>A place in the queue. Keep it to promote the request while it waits.</summary>
    public sealed class Ticket(FetchPriority priority)
    {
        internal FetchPriority Priority = priority;
        internal readonly TaskCompletionSource<Lease> Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Ticket>? Node;
        /// <summary>Completes when a background ticket is promoted, so a wait that isn't in the stream queue (bytes) can move too.</summary>
        internal readonly TaskCompletionSource Promoted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FetchPriority CurrentPriority => Priority;
    }

    /// <summary>One held stream. Dispose it (or <see cref="Lease.Complete"/> it with the outcome) exactly once.</summary>
    public sealed class Lease : IDisposable
    {
        private readonly StreamScheduler owner;
        private int done;
        internal Lease(StreamScheduler owner, bool interactiveSlot) { this.owner = owner; InteractiveSlot = interactiveSlot; }
        /// <summary>True when the stream came from the interactive reserve, false when it is a background stream.</summary>
        public bool InteractiveSlot { get; }
        /// <summary>Releases the stream and feeds the outcome to the adaptive limit: null for success, else the failure.</summary>
        public void Complete(CdnFailure? failure) { if (Interlocked.Exchange(ref done, 1) == 0) owner.Release(this, failure, true); }
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) owner.Release(this, null, false); }
    }

    public Ticket NewTicket(FetchPriority priority) => new(priority);

    public async Task<Lease> AcquireAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        lock (gate)
        {
            if (ticket.Node is not null || ticket.Granted.Task.IsCompleted) throw new InvalidOperationException("A ticket is used once.");
            ticket.Node = (ticket.Priority == FetchPriority.Interactive ? interactiveQueue : backgroundQueue).AddLast(ticket);
            Grant();
        }
        using (cancellationToken.Register(() => Cancel(ticket, cancellationToken)))
            return await ticket.Granted.Task.ConfigureAwait(false);
    }

    /// <summary>Moves a waiting background request ahead of all background work. A granted or interactive ticket is unchanged.</summary>
    public void Promote(Ticket ticket)
    {
        lock (gate)
        {
            if (ticket.Priority == FetchPriority.Interactive) return;
            ticket.Priority = FetchPriority.Interactive;
            ticket.Promoted.TrySetResult();
            if (ticket.Node is null) return;
            backgroundQueue.Remove(ticket.Node);
            ticket.Node = interactiveQueue.AddLast(ticket);
            Grant();
        }
    }

    private void Cancel(Ticket ticket, CancellationToken token)
    {
        lock (gate)
        {
            if (ticket.Node is null) return;
            ticket.Node.List!.Remove(ticket.Node); ticket.Node = null;
        }
        ticket.Granted.TrySetCanceled(token);
    }

    private void Release(Lease lease, CdnFailure? failure, bool report)
    {
        lock (gate)
        {
            if (lease.InteractiveSlot) interactiveActive--; else backgroundActive--;
            if (report)
            {
                if (failure is CdnFailure.Timeout or CdnFailure.Network or CdnFailure.HttpStatus)
                { streak = 0; BackgroundLimit = Math.Max(options.MinBackground, BackgroundLimit - Math.Max(1, BackgroundLimit / 4)); }
                else if (failure is null && ++streak >= BackgroundLimit)
                { streak = 0; BackgroundLimit = Math.Min(options.MaxBackground, BackgroundLimit + 1); }
            }
            Grant();
        }
    }

    /// <summary>Under the lock. Interactive first: its reserve, then any free background stream. Then background in FIFO order.</summary>
    private void Grant()
    {
        while (interactiveQueue.First is { } next)
        {
            bool reserve;
            if (interactiveActive < options.Interactive) reserve = true;
            else if (backgroundActive < BackgroundLimit) reserve = false;
            else break;
            Take(next.Value, reserve);
        }
        while (interactiveQueue.First is null && backgroundQueue.First is { } next && backgroundActive < BackgroundLimit) Take(next.Value, false);
    }

    private void Take(Ticket ticket, bool reserve)
    {
        ticket.Node!.List!.Remove(ticket.Node); ticket.Node = null;
        if (reserve) interactiveActive++; else backgroundActive++;
        if (!ticket.Granted.TrySetResult(new Lease(this, reserve)))
        { if (reserve) interactiveActive--; else backgroundActive--; }
    }
}
