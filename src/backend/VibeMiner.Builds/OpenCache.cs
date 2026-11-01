namespace VibeMiner.Builds;

/// <summary>
/// The reuse rules every <see cref="IBuilds"/> shares: opened builds kept by <see cref="BuildRef"/> (bounded, least recently
/// opened dropped), and one in-flight open per build under its own token. Each caller waits with its own token; when the
/// last waiter cancels, the shared open is cancelled and forgotten. Only an open that finished Open, while still wanted, is kept.
/// At most <c>maxConcurrent</c> opens run at once; the rest wait under their own token, so a cancelled waiting open leaves nothing.
/// </summary>
internal sealed class OpenCache
{
    private sealed class Flight
    {
        public required CancellationTokenSource Cancel { get; init; }
        public Task<BuildOpenResult> Task { get; set; } = null!;
        public int Waiters { get; set; }
        /// <summary>The open has finished with its token. Set under the lock.</summary>
        public bool Done { get; set; }
        /// <summary>A caller is cancelling the token outside the lock; it disposes the source if the open is done by then.</summary>
        public bool Cancelling { get; set; }
    }

    private readonly Lock gate = new();
    private readonly int capacity;
    private readonly LinkedList<(BuildRef Build, IBuild Opened)> recent = [];
    private readonly Dictionary<BuildRef, LinkedListNode<(BuildRef Build, IBuild Opened)>> opened = [];
    private readonly Dictionary<BuildRef, Flight> flights = [];
    private readonly SemaphoreSlim running;

    public OpenCache(int capacity, int maxConcurrent = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        this.capacity = capacity;
        running = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async Task<BuildOpenResult> OpenAsync(BuildRef build, Func<CancellationToken, Task<BuildOpenResult>> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        cancellationToken.ThrowIfCancellationRequested();
        Flight flight;
        lock (gate)
        {
            if (opened.TryGetValue(build, out var node))
            {
                recent.Remove(node); recent.AddFirst(node);
                return new(build, BuildOpenState.Open, node.Value.Opened, null, null, null, null);
            }
            if (!flights.TryGetValue(build, out flight!))
            {
                flight = new Flight { Cancel = new CancellationTokenSource() };
                flights[build] = flight;
                flight.Task = RunAsync(build, flight, open);
            }
            flight.Waiters++;
        }
        try { return await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var abandoned = false;
            lock (gate)
            {
                if (--flight.Waiters == 0 && flights.TryGetValue(build, out var current) && current == flight)
                {
                    flights.Remove(build);
                    flight.Cancelling = abandoned = true;
                }
            }
            if (abandoned)
            {
                flight.Cancel.Cancel();
                lock (gate)
                {
                    flight.Cancelling = false;
                    if (flight.Done) flight.Cancel.Dispose();
                }
            }
            throw;
        }
    }

    private async Task<BuildOpenResult> RunAsync(BuildRef build, Flight flight, Func<CancellationToken, Task<BuildOpenResult>> open)
    {
        var token = flight.Cancel.Token;
        await Task.Yield();
        try
        {
            await running.WaitAsync(token).ConfigureAwait(false);
            BuildOpenResult result;
            try { result = await open(token).ConfigureAwait(false); }
            finally { running.Release(); }
            lock (gate)
            {
                if (!flights.TryGetValue(build, out var current) || current != flight) throw new OperationCanceledException(token);
                flights.Remove(build);
                if (result is { State: BuildOpenState.Open, Opened: { } built }) Keep(build, built);
            }
            return result;
        }
        catch
        {
            lock (gate) if (flights.TryGetValue(build, out var current) && current == flight) flights.Remove(build);
            throw;
        }
        finally
        {
            lock (gate)
            {
                flight.Done = true;
                if (!flight.Cancelling) flight.Cancel.Dispose();
            }
        }
    }

    private void Keep(BuildRef build, IBuild built)
    {
        opened[build] = recent.AddFirst((build, built));
        while (recent.Count > capacity)
        {
            opened.Remove(recent.Last!.Value.Build);
            recent.RemoveLast();
        }
    }
}
