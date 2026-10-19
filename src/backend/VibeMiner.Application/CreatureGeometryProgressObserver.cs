using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>One callback and one replaceable latest value; producers never await user observers.</summary>
internal sealed class CreatureGeometryProgressObserver(Func<CreatureGeometryProgress, Task> callback, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stopping = new();
    private CreatureGeometryProgress? latest;
    private TaskCompletionSource? drained, retirement;
    private bool running, closed;
    private long? previous;
    internal Task ReportAsync(CreatureGeometryProgress value)
    {
        CreatureGeometryValidation.ValidateProgress(value); bool start;
        lock (gate)
        {
            if (closed) return Task.CompletedTask;
            latest = value; start = !running;
            if (start) { running = true; drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        }
        if (start) _ = PumpAsync(); return Task.CompletedTask;
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
                CreatureGeometryProgress value;
                lock (gate)
                {
                    if (closed || latest is null) { running = false; drained!.TrySetResult(); return; }
                    value = latest; latest = null; previous = clock.GetTimestamp();
                }
                await callback(value);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { lock (gate) { closed = true; latest = null; running = false; drained!.TrySetResult(); } }
    }
    internal Task CloseAsync()
    {
        TaskCompletionSource completion; Task pending;
        lock (gate)
        {
            if (retirement is not null) return retirement.Task;
            completion = retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closed = true; latest = null; pending = drained?.Task ?? Task.CompletedTask;
        }
        _ = CloseCoreAsync(); return completion.Task;
        async Task CloseCoreAsync()
        {
            try
            {
                try { await stopping.CancelAsync(); } catch (AggregateException) { }
                await pending; stopping.Dispose(); completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
