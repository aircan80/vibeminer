namespace VibeMiner.Infrastructure;

/// <summary>One running callback and one replaceable latest update. No caller callback executes
/// on the producer's traversal path; actual callback retirement still owns its admission.</summary>
internal sealed class AssetProgressObserver(Func<string, Task> callback) : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource failed = new();
    private string? pending;
    private Task? worker;
    private bool closed;
    internal Exception? Failure { get; private set; }
    internal CancellationToken FailureToken => failed.Token;

    internal Task PostAsync(string stage)
    {
        lock (gate)
        {
            if (closed) return Task.CompletedTask;
            pending = stage;
            worker ??= RunAsync();
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        // A synchronous observer must not run under a producer's CPU/ROOT permit either.
        await Task.Yield();
        while (true)
        {
            string stage;
            lock (gate)
            {
                if (pending is null) { worker = null; return; }
                stage = pending; pending = null;
            }
            try { await callback(stage); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                lock (gate) { Failure = error; closed = true; pending = null; }
                await AssetCancellation.RequestAsync(failed);
                return;
            }
        }
    }

    internal async Task CloseAsync(bool discard)
    {
        Task? draining;
        lock (gate) { closed = true; if (discard) pending = null; draining = worker; }
        if (draining is not null) await draining;
    }
    public void Dispose() => failed.Dispose();
}
