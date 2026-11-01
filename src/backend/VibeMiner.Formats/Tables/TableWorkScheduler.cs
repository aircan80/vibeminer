namespace VibeMiner.Formats.Tables;

/// <summary>
/// The slots layer 5's expensive work runs in (D263): a table open that has to read the DB2 file and build its generated rows, a
/// protected-ID read of the file's index, and each chunk a filtered page, a follow or a referenced-by scans. Interactive and
/// Background are separate lanes, at most <see cref="MaxInteractive"/> and <see cref="MaxBackground"/> running. Interactive work
/// never waits behind queued Background work (a caller joining a running build, or waiting on a handle's one WoWDBDefs ask, waits
/// for that); queued Background work starts only once no Interactive work is queued. Running work is
/// never preempted, but a scan takes a slot per chunk, so a Background scan yields between chunks. Each lane queues first come,
/// first served, at most <see cref="MaxQueued"/> callers; one more throws <see cref="TablesBusyException"/>. A queued caller
/// that cancels leaves the queue. Cheap work takes no slot: a table already in the generated store, an unfiltered page of it, a
/// row by ID, the shallow list. Work in a slot never asks for another, so slots can't deadlock. Layer 4 below has no priority.
/// </summary>
public sealed class TableWorkScheduler
{
    public const int DefaultMaxInteractive = 4;
    public const int DefaultMaxBackground = 2;
    public const int DefaultMaxQueued = 256;

    private readonly Lock gate = new();
    private readonly Lane interactive, background;
    private readonly List<(Func<bool> Condition, TaskCompletionSource Done)> watchers = [];

    /// <summary>For tests: awaited as a caller asks for a slot, before it looks.</summary>
    internal Func<TablePriority, Task>? Entering { get; set; }

    public TableWorkScheduler(int maxInteractive = DefaultMaxInteractive, int maxBackground = DefaultMaxBackground, int maxQueued = DefaultMaxQueued)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInteractive, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBackground, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxQueued, 0);
        interactive = new(maxInteractive);
        background = new(maxBackground);
        MaxQueued = maxQueued;
    }

    public int MaxInteractive => interactive.Max;
    public int MaxBackground => background.Max;
    public int MaxQueued { get; }

    /// <summary>The slots of a lane in use now.</summary>
    public int Running(TablePriority priority)
    {
        lock (gate) return LaneOf(priority).Running;
    }

    /// <summary>The callers waiting in a lane now.</summary>
    public int Queued(TablePriority priority)
    {
        lock (gate) return LaneOf(priority).Queue.Count;
    }

    /// <summary>
    /// A slot in <paramref name="priority"/>'s lane, now or once one frees; dispose it when the work ends. Throws
    /// <see cref="TablesBusyException"/> when the lane's queue is full, and <see cref="OperationCanceledException"/> when the caller
    /// cancels while queued (it is gone from the queue then).
    /// </summary>
    public async ValueTask<TableWorkSlot> EnterAsync(TablePriority priority, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        if (Entering is { } entering) await entering(priority).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Waiter? waiter = null;
        lock (gate)
        {
            var lane = LaneOf(priority);
            if (lane.Queue.Count == 0 && lane.Running < lane.Max && (priority == TablePriority.Interactive || interactive.Queue.Count == 0))
                lane.Running++;
            else
            {
                if (lane.Queue.Count >= MaxQueued) throw new TablesBusyException(priority);
                waiter = new Waiter(priority);
                waiter.Node = lane.Queue.AddLast(waiter);
            }
        }
        Changed();
        if (waiter is null) return new TableWorkSlot(this, priority);
        using (cancellationToken.UnsafeRegister(static (state, token) => { var (owner, w) = ((TableWorkScheduler, Waiter))state!; owner.Cancel(w, token); },
            (this, waiter)))
            return await waiter.Granted.Task.ConfigureAwait(false);
    }

    internal void Release(TablePriority priority)
    {
        List<Waiter> grants;
        lock (gate)
        {
            LaneOf(priority).Running--;
            grants = Pump();
        }
        Grant(grants);
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        List<Waiter> grants;
        lock (gate)
        {
            if (waiter.Node.List is null) return;
            LaneOf(waiter.Priority).Queue.Remove(waiter.Node);
            // An Interactive caller leaving may let Background work start.
            grants = Pump();
        }
        waiter.Granted.TrySetCanceled(token);
        Grant(grants);
    }

    /// <summary>Takes the slots that free ones and the rules allow, under the gate; the callers are told outside it.</summary>
    private List<Waiter> Pump()
    {
        var grants = new List<Waiter>();
        Take(interactive, grants);
        if (interactive.Queue.Count == 0) Take(background, grants);
        return grants;
    }

    private static void Take(Lane lane, List<Waiter> grants)
    {
        while (lane.Running < lane.Max && lane.Queue.First is { } first)
        {
            lane.Queue.RemoveFirst();
            lane.Running++;
            grants.Add(first.Value);
        }
    }

    private void Grant(List<Waiter> grants)
    {
        foreach (var waiter in grants) waiter.Granted.TrySetResult(new TableWorkSlot(this, waiter.Priority));
        Changed();
    }

    private Lane LaneOf(TablePriority priority) => priority == TablePriority.Interactive ? interactive : background;

    /// <summary>For tests: completes once <paramref name="condition"/> holds after a change (or now).</summary>
    internal Task WhenAsync(Func<bool> condition)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (watchers) watchers.Add((condition, done));
        Changed();
        return done.Task;
    }

    private void Changed()
    {
        lock (watchers)
            for (var i = watchers.Count - 1; i >= 0; i--)
                if (watchers[i].Condition())
                {
                    watchers[i].Done.TrySetResult();
                    watchers.RemoveAt(i);
                }
    }

    private sealed class Lane(int max)
    {
        public int Max { get; } = max;
        public int Running { get; set; }
        public LinkedList<Waiter> Queue { get; } = new();
    }

    private sealed class Waiter(TablePriority priority)
    {
        public TablePriority Priority { get; } = priority;
        public LinkedListNode<Waiter> Node { get; set; } = null!;
        public TaskCompletionSource<TableWorkSlot> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>One slot of a <see cref="TableWorkScheduler"/> lane. Disposing it frees the slot; a second dispose does nothing.</summary>
public sealed class TableWorkSlot : IDisposable
{
    private readonly TableWorkScheduler owner;
    private int disposed;

    internal TableWorkSlot(TableWorkScheduler owner, TablePriority priority) { this.owner = owner; Priority = priority; }

    public TablePriority Priority { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release(Priority);
    }
}

/// <summary>A lane of <see cref="TableWorkScheduler"/> already has its most callers queued. Code TABLES_BUSY.</summary>
public sealed class TablesBusyException(TablePriority priority)
    : InvalidOperationException($"Too much {priority.ToString().ToLowerInvariant()} table work is waiting already.")
{
    public const string BusyCode = "TABLES_BUSY";
    public string Code => BusyCode;
    public TablePriority Priority { get; } = priority;
}
