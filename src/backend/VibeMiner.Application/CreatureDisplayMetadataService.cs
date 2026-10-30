using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Bounds subscriptions and observers; the store owns the sole shared prerequisite coordinator.
/// Production callers also retain C1's shared end-to-end publication/preparation admission through output flush.</summary>
public sealed class CreatureDisplayMetadataService(ICreatureDisplayMetadataStore store, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    public const string MetadataPolicy = "creature-display-metadata-v1";
    private readonly object gate = new();
    private readonly HashSet<Operation> operations = [];
    private bool closed;
    private int preparations, reads;

    private sealed class Operation(bool prepare, Observer? observer)
    {
        internal readonly bool Prepare = prepare;
        internal readonly Observer? Observer = observer;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly object Gate = new();
        internal TaskCompletionSource? Notification;
        internal bool Finished;
    }
    private sealed class Observer(Func<CreaturesPreparationProgress, Task> callback, TimeProvider clock)
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource stop = new();
        private CreaturesPreparationProgress? latest;
        private TaskCompletionSource? drain;
        private bool closed, running;
        private long? prior;
        internal void Post(CreaturesPreparationProgress value)
        {
            ValidateProgress(value);
            bool start;
            lock (gate)
            {
                if (closed) return;
                latest = value; start = !running;
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
                        var delay = TimeSpan.FromMilliseconds(100) - clock.GetElapsedTime(timestamp);
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stop.Token);
                    }
                    CreaturesPreparationProgress value;
                    lock (gate)
                    {
                        if (closed || latest is null) { running = false; drain!.TrySetResult(); return; }
                        value = latest; latest = null; prior = clock.GetTimestamp();
                    }
                    await callback(value);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { lock (gate) { closed = true; latest = null; running = false; drain!.TrySetResult(); } }
        }
        internal async Task CloseAsync()
        {
            Task retirement;
            lock (gate) { closed = true; latest = null; retirement = drain?.Task ?? Task.CompletedTask; }
            try { await stop.CancelAsync(); } catch (AggregateException) { }
            await retirement; stop.Dispose();
        }
    }

    public const string FactsRevision = "creature-display-facts-v1", LabelRevision = "creature-labels-v1";
    // Reviewed on #62 (WoW review): Creature.Classification is signed8; values outside 0–6 have no label.
    private static readonly CreatureLabelLists ReviewedLabels = new(LabelRevision, Array.AsReadOnly<CreatureLabel>(
    [
        new("0", "Normal"), new("1", "Elite"), new("2", "Rare elite"), new("3", "World boss"), new("4", "Rare"), new("5", "Trivial"), new("6", "Minus")
    ]));
    public static CreatureLabelLists Labels() => ReviewedLabels;
    public Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request,
        CancellationToken token = default) => ExecuteAsync(request, false, null, (r, t) => store.ReadDisplayAsync(r, t), token);
    public Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request,
        Func<CreaturesPreparationProgress, Task>? progress = null, CancellationToken token = default) =>
        ExecuteAsync<CreatureDisplayMetadataResult>(request, true, progress, null, token);
    /// <summary>Shares the two local read credits with ReadDisplayAsync; never prepares or downloads.</summary>
    public Task<CreatureDisplayFactsResult> ReadFactsAsync(CreatureDisplayMetadataRequest request, CancellationToken token = default) =>
        ExecuteAsync(request, false, null, (r, t) => store.ReadFactsAsync(r, t), token);

    private async Task<T> ExecuteAsync<T>(CreatureDisplayMetadataRequest request,
        bool prepare, Func<CreaturesPreparationProgress, Task>? progress, Func<CreatureDisplayMetadataRequest, CancellationToken, Task<T>>? read, CancellationToken token)
        where T : class
    {
        Validate(request); token.ThrowIfCancellationRequested();
        request = request with { Context = request.Context with { } };
        var operation = new Operation(prepare, progress is null ? null : new(progress, timeProvider ?? TimeProvider.System));
        lock (gate)
        {
            if (closed) { operation.Cancellation.Dispose(); throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true); }
            if (prepare ? preparations >= 2 : reads >= 2)
            {
                operation.Cancellation.Dispose();
                throw new DiscoveryException(prepare ? "CREATURES_BUSY" : "CREATURES_QUERY_BUSY",
                    "Creature display work is at its bounded capacity or retiring.", true);
            }
            if (prepare) preparations++; else reads++;
            operations.Add(operation);
        }
        using var cancellation = token.Register(() => RequestCancellation(operation));
        try
        {
            var result = prepare
                ? (T)(object)await store.PrepareDisplayAsync(request, value => { ValidateProgress(value); operation.Observer?.Post(value); return Task.CompletedTask; }, operation.Cancellation.Token)
                : await read!(request, operation.Cancellation.Token);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            Task notification;
            lock (operation.Gate) { operation.Finished = true; notification = operation.Notification?.Task ?? Task.CompletedTask; }
            await notification;
            try { if (operation.Observer is not null) await operation.Observer.CloseAsync(); }
            finally
            {
                operation.Cancellation.Dispose();
                lock (gate)
                {
                    operations.Remove(operation);
                    if (prepare) preparations--; else reads--;
                    operation.Retired.TrySetResult();
                }
            }
        }
    }
    private static void RequestCancellation(Operation operation)
    {
        TaskCompletionSource notification;
        lock (operation.Gate)
        {
            if (operation.Finished || operation.Notification is not null) return;
            operation.Notification = notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = NotifyAsync();
        async Task NotifyAsync()
        {
            try { await operation.Cancellation.CancelAsync(); }
            catch (AggregateException) { }
            finally { notification.TrySetResult(); }
        }
    }
    public static void Validate(CreatureDisplayMetadataRequest request)
    {
        if (request is null || request.Slot is < 0 or > 3)
            throw new DiscoveryException("INVALID_REQUEST", "Choose one exact Creature artifact, unsigned Creature ID and slot from zero through three.");
        CreaturesService.Validate(new CreatureInspectRequest(request.Context, request.ArtifactId, request.CreatureId));
    }
    private static void ValidateProgress(CreaturesPreparationProgress value)
    {
        static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
        if (value is null || !Text(value.Stage, 128) || !Text(value.Unit, 32) ||
            value.Completed is < 0 || value.Total is < 0 || value.Completed is { } done && value.Total is { } total && done > total ||
            value.TableId is not null && (value.TableId.Length != 8 || value.TableId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new DiscoveryException("CREATURES_PROGRESS_INVALID", "Creature display progress requires bounded stage text and honest current-stage counters.");
    }
    public async ValueTask DisposeAsync()
    {
        Operation[] retiring;
        lock (gate) { closed = true; retiring = operations.ToArray(); }
        foreach (var operation in retiring) RequestCancellation(operation);
        await Task.WhenAll(retiring.Select(operation => operation.Retired.Task));
    }
}
