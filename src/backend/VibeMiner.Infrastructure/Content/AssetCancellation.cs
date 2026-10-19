namespace VibeMiner.Infrastructure;

internal static class AssetCancellation
{
    internal static Deadline After(CancellationTokenSource source, TimeSpan timeout, TimeProvider? clock = null) => new(source, timeout, clock);

    internal static async Task RequestAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync(); }
        catch (AggregateException) { /* Notification failure never proves producer/lease retirement. */ }
        catch (ObjectDisposedException) { /* An already retired owner won the cancellation race. */ }
    }

    /// <summary>Owns both the one-shot timer and actual notification retirement. Timer disposal
    /// alone does not establish that CancelAsync callbacks finished.</summary>
    internal sealed class Deadline : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly ITimer timer;
        private readonly CancellationTokenSource source;
        private Task? notification;
        private bool closed;
        private Task? disposal;
        internal Deadline(CancellationTokenSource source, TimeSpan timeout, TimeProvider? clock)
        {
            this.source = source;
            timer = (clock ?? TimeProvider.System).CreateTimer(Fire, null, timeout, Timeout.InfiniteTimeSpan);
        }
        private void Fire(object? state)
        {
            lock (gate)
            {
                if (!closed) notification ??= RequestAsync(source);
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (gate) return new(disposal ??= DisposeCoreAsync());
        }
        private async Task DisposeCoreAsync()
        {
            Task? retiring;
            lock (gate) { closed = true; retiring = notification; }
            await timer.DisposeAsync();
            if (retiring is not null) await retiring;
        }
    }
}
