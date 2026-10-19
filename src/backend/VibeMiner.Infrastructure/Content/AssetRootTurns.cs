namespace VibeMiner.Infrastructure;

/// <summary>A finite FIFO shared by ready ROOT scans and cold grammar validation. The registry's
/// context/subscription admissions bound all entries. No network or observer work runs here.</summary>
internal sealed class AssetRootTurns(object gate)
{
    private readonly LinkedList<Turn> pending = [];
    private bool running;
    private Turn? active;
    internal object? TailOwner { get { lock (gate) return pending.Last?.Value.Owner; } }
    internal object? ActiveOwner { get { lock (gate) return active?.Owner; } }

    internal Task<T> Enqueue<T>(object owner, Func<CancellationToken, Task<T>> operation,
        CancellationToken token, Action? freeze = null)
    {
        var turn = new Turn<T>(owner, operation, token, freeze);
        lock (gate)
        {
            turn.Node = pending.AddLast(turn);
            turn.Cancellation = token.Register(() => CancelQueued(turn));
            if (!running) { running = true; _ = RunAsync(); }
        }
        return turn.Completion.Task;
    }

    private void CancelQueued(Turn turn)
    {
        lock (gate)
        {
            if (turn.Node is null) return;
            pending.Remove(turn.Node); turn.Node = null;
            turn.CancelQueued();
        }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        while (true)
        {
            Turn turn;
            lock (gate)
            {
                if (pending.First is null) { active = null; running = false; return; }
                turn = pending.First.Value; pending.RemoveFirst(); turn.Node = null; active = turn;
                turn.Cancellation.Unregister();
                turn.Freeze?.Invoke();
            }
            await turn.ExecuteAsync();
            lock (gate) active = null;
            // Complete only after retiring this turn, before consumers drain their observers.
            turn.Complete();
        }
    }

    private abstract class Turn(object owner, CancellationToken token, Action? freeze)
    {
        internal object Owner { get; } = owner;
        internal CancellationToken Token { get; } = token;
        internal Action? Freeze { get; } = freeze;
        internal LinkedListNode<Turn>? Node;
        internal CancellationTokenRegistration Cancellation;
        internal abstract Task ExecuteAsync();
        internal abstract void Complete();
        internal abstract void CancelQueued();
    }
    private sealed class Turn<T>(object owner, Func<CancellationToken, Task<T>> operation,
        CancellationToken token, Action? freeze) : Turn(owner, token, freeze)
    {
        internal readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T? result;
        private Exception? failure;
        internal override async Task ExecuteAsync()
        {
            try { Token.ThrowIfCancellationRequested(); result = await operation(Token); }
            catch (Exception error) { failure = error; }
        }
        internal override void CancelQueued() => Completion.TrySetCanceled(Token);
        internal override void Complete()
        {
            if (failure is OperationCanceledException canceled) Completion.TrySetCanceled(canceled.CancellationToken);
            else if (failure is not null) Completion.TrySetException(failure);
            else Completion.TrySetResult(result!);
        }
    }
}
