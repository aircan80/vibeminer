using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

// The store's public port remains safe for a direct caller too: one running callback and
// one latest pending value, never a callback awaited by the source/coordinator worker.
internal sealed class CreatureDisplayProgressObserver(Func<CreaturesPreparationProgress, Task> callback)
{
    private readonly object gate = new();
    private CreaturesPreparationProgress? latest;
    private bool closed, running;
    private TaskCompletionSource? drain;
    private readonly CancellationTokenSource stopping = new();
    private long? prior;
    internal void Post(CreaturesPreparationProgress progress)
    {
        bool start;
        lock (gate)
        {
            if (closed) return;
            latest = progress; start = !running;
            if (start) { running = true; drain = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        if (start) _ = PumpAsync();
    }
    private async Task PumpAsync()
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                if (prior is { } timestamp)
                {
                    var delay = TimeSpan.FromMilliseconds(100) - TimeProvider.System.GetElapsedTime(timestamp);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, stopping.Token);
                }
                CreaturesPreparationProgress value;
                lock (gate)
                {
                    if (closed || latest is null) { running = false; drain!.TrySetResult(); return; }
                    value = latest; latest = null; prior = TimeProvider.System.GetTimestamp();
                }
                await callback(value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (gate) { closed = true; latest = null; running = false; drain!.TrySetResult(); } }
    }
    internal async Task CloseAsync()
    {
        Task retired; lock (gate) { closed = true; latest = null; retired = drain?.Task ?? Task.CompletedTask; }
        try { await stopping.CancelAsync(); } catch (AggregateException) { }
        await retired; stopping.Dispose();
    }
}
