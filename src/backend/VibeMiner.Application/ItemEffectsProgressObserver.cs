using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>One running callback and one replaceable latest progress value; producer never awaits callbacks.</summary>
internal sealed class ItemEffectsProgressObserver(Func<ItemEffectsProgress, Task> callback, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private ItemEffectsProgress? pending;
    private TaskCompletionSource? drained, closed;
    private bool running, stoppingAdmission;
    private long? previous;

    public Task ReportAsync(ItemEffectsProgress value)
    {
        ItemEffectsValidation.ValidateProgress(value);
        bool start;
        lock (gate)
        {
            if (stoppingAdmission) return Task.CompletedTask;
            pending = value; start = !running;
            if (start) { running = true; drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        if (start) _ = PumpAsync();
        return Task.CompletedTask;
    }
    private async Task PumpAsync()
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                if (previous is { } timestamp)
                {
                    var delay = TimeSpan.FromMilliseconds(100) - clock.GetElapsedTime(timestamp);
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stopping.Token);
                }
                ItemEffectsProgress value;
                lock (gate)
                {
                    if (stoppingAdmission || pending is null) { running = false; drained!.TrySetResult(); return; }
                    value = pending; pending = null; previous = clock.GetTimestamp();
                }
                await callback(value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Callback failure removes only this observer. The request still receives its terminal result.
            lock (gate) { stoppingAdmission = true; pending = null; running = false; drained!.TrySetResult(); }
        }
    }
    public Task CloseAsync()
    {
        TaskCompletionSource completion; Task retirement;
        lock (gate)
        {
            if (closed is not null) return closed.Task;
            completion = closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stoppingAdmission = true; pending = null; retirement = drained?.Task ?? Task.CompletedTask;
        }
        _ = CloseCoreAsync(); return completion.Task;
        async Task CloseCoreAsync()
        {
            try
            {
                try { await stopping.CancelAsync(); } catch (AggregateException) { }
                await retirement; stopping.Dispose(); completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
