namespace VibeMiner.Infrastructure;

/// <summary>
/// One whole-build metadata pass at a time (D071): the ROOT scope measurement and the file catalog preparation each hold
/// build-sized key arrays, so they never run together. A waiting ROOT scope measurement goes first, because Download all
/// waits for it; otherwise waiters are served in arrival order. Cancellation removes a waiter; a grant is released by
/// disposing it.
/// </summary>
public sealed class BackgroundMetadataGate
{
    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource<IDisposable>> first = new(), rest = new();
    private bool held;

    /// <summary>True while a pass holds the gate. Tests and diagnostics only.</summary>
    internal bool Held { get { lock (sync) return held; } }
    /// <summary>Passes waiting for the gate. Tests and diagnostics only.</summary>
    internal int Waiting { get { lock (sync) return first.Count + rest.Count; } }

    public Task<IDisposable> EnterAsync(bool priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiter;
        LinkedListNode<TaskCompletionSource<IDisposable>> node;
        lock (sync)
        {
            if (!held) { held = true; return Task.FromResult<IDisposable>(new Grant(this)); }
            waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (priority ? first : rest).AddLast(waiter);
        }
        if (token.CanBeCanceled)
        {
            var registration = token.Register(() =>
            {
                lock (sync) { if (node.List is null) return; node.List.Remove(node); }
                waiter.TrySetCanceled(token);
            });
            _ = waiter.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return waiter.Task;
    }

    private void Release()
    {
        while (true)
        {
            TaskCompletionSource<IDisposable> next;
            lock (sync)
            {
                var queue = first.Count > 0 ? first : rest.Count > 0 ? rest : null;
                if (queue is null) { held = false; return; }
                next = queue.First!.Value; queue.RemoveFirst();
            }
            // Removed from its queue under the lock, so cancellation can no longer complete it.
            if (next.TrySetResult(new Grant(this))) return;
        }
    }

    private sealed class Grant(BackgroundMetadataGate gate) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) gate.Release(); }
    }
}
