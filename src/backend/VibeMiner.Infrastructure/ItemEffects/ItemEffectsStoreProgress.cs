using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    // Direct adapter callers have the same bounded, nonblocking observer retirement contract as App callers.
    private sealed class StoreObserver(Func<ItemEffectsProgress, Task> callback, TimeProvider clock)
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource stopping = new();
        private ItemEffectsProgress? latest;
        private TaskCompletionSource? drained, closed;
        private bool running, closing;
        private long? previous;
        internal Task PostAsync(ItemEffectsProgress value)
        {
            if (value is null || !Text(value.Stage, 128) || !Text(value.Unit, 32) ||
                value.Completed is < 0 or > 9_007_199_254_740_991 || value.Total is < 0 or > 9_007_199_254_740_991 ||
                value.Completed is { } done && value.Total is { } total && done > total ||
                value.TableId is not null && value.TableId is not ("00cb674f" or "4002a5b1" or "46c66698"))
                throw new DiscoveryException("ITEM_EFFECTS_PROGRESS_INVALID", "Effects progress requires bounded stage text and honest current-stage counters.");
            bool start;
            lock (sync)
            {
                if (closing) return Task.CompletedTask;
                latest = value; start = !running;
                if (start) { running = true; drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            }
            if (start) _ = PumpAsync(); return Task.CompletedTask;
        }
        private static bool Text(string? value, int maximum) => value is not null && value.Length is > 0 &&
            value.Length <= maximum && !value.Any(char.IsControl);
        private async Task PumpAsync()
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    if (previous is { } at)
                    {
                        var delay = TimeSpan.FromMilliseconds(100) - clock.GetElapsedTime(at);
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stopping.Token);
                    }
                    ItemEffectsProgress value;
                    lock (sync)
                    {
                        if (closing || latest is null) { running = false; drained!.TrySetResult(); return; }
                        value = latest; latest = null; previous = clock.GetTimestamp();
                    }
                    await callback(value);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { lock (sync) { closing = true; latest = null; running = false; drained!.TrySetResult(); } }
        }
        internal Task CloseAsync()
        {
            TaskCompletionSource completion; Task retirement;
            lock (sync)
            {
                if (closed is not null) return closed.Task;
                completion = closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                closing = true; latest = null; retirement = drained?.Task ?? Task.CompletedTask;
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
}
