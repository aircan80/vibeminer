using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Four bounded shared visible demands, one producer, independent consumer cancellation.</summary>
public sealed class ItemClassificationsService : IAsyncDisposable
{
    public bool SupportsSubclassLabels => store.SupportsSubclassLabels;
    public const int MaximumBatches = 4, MaximumConsumers = 8;
    private readonly IItemClassificationsStore store;
    private readonly object gate = new();
    private readonly Dictionary<string, Demand> demands = new(StringComparer.Ordinal);
    private readonly Queue<Demand> queue = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private bool closed;
    private int consumers;
    private TaskCompletionSource drained = CompletedDrain();
    private static TaskCompletionSource CompletedDrain()
    { var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); completion.SetResult(); return completion; }
    private sealed class Demand(ItemClassificationBatchRequest request, string key)
    {
        internal readonly ItemClassificationBatchRequest Request = request;
        internal readonly string Key = key;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<ItemClassificationBatch> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<long, Observer> Observers = [];
        internal long NextObserver;
        internal int Consumers;
        internal bool Stopping, ProgressClosed;
        internal readonly SemaphoreSlim Dispatch = new(1, 1);
    }
    private sealed class Observer(Func<ItemClassificationProgress, Task> callback)
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed;
        internal async Task ReportAsync(ItemClassificationProgress progress)
        {
            await serial.WaitAsync();
            try { if (!closed) try { await callback(progress); } catch (Exception e) when (e is not OutOfMemoryException) { closed = true; } }
            finally { serial.Release(); }
        }
        internal async Task CloseAsync()
        { await serial.WaitAsync(); try { closed = true; } finally { serial.Release(); } }
    }
    public ItemClassificationsService(IItemClassificationsStore store) { this.store = store; worker = RunAsync(); }

    public async Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request,
        Func<ItemClassificationProgress, Task>? progress = null, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        if (request.PolicyRevision == "classification-label-index-v2" && !SupportsSubclassLabels)
            throw new DiscoveryException("ITEM_SUBCLASS_UNSUPPORTED", "This engine does not support the requested classification policy.");
        // Caller mutation cannot change either the producer authority or the shared-demand key.
        request = request with { ItemIds = Array.AsReadOnly(request.ItemIds.ToArray()) };
        Validate(request);
        var key = request.Context.ProductId + ":" + request.Context.BuildIdentity + ":" + request.Context.SourceRegion + ":" +
            request.Context.ResearchLocale + ":" + request.ArtifactId + ":" + string.Join(',', request.ItemIds) + ":" + request.IncludeSubclass + ":" + request.PolicyRevision;
        var observer = progress is null ? null : new Observer(progress);
        Demand demand; long id;
        lock (gate)
        {
            RequireOpen();
            if (consumers >= MaximumConsumers) throw Busy();
            if (!demands.TryGetValue(key, out demand!))
            {
                if (demands.Count >= MaximumBatches) throw Busy();
                demand = new(request, key); demands.Add(key, demand); queue.Enqueue(demand); Wake();
            }
            if (demand.Stopping || demand.Consumers >= MaximumConsumers) throw Busy();
            if (consumers == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            demand.Consumers++; consumers++; id = ++demand.NextObserver;
            if (observer is not null) demand.Observers.Add(id, observer);
        }
        var detached = false;
        try { return await demand.Completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            bool last;
            lock (gate)
            {
                detached = true; demand.Observers.Remove(id); demand.Consumers--;
                last = !demand.Completion.Task.IsCompleted && demand.Consumers == 0;
                if (last) demand.Stopping = true;
            }
            // Retain the global subscription charge through callback drain and producer retirement.
            if (last) await CancelAsync(demand);
            if (observer is not null) await observer.CloseAsync();
            if (demand.Completion.Task.IsCompleted || last)
            {
                try { return await demand.Completion.Task; }
                catch (OperationCanceledException) { throw new OperationCanceledException(token); }
            }
            throw;
        }
        finally
        {
            try
            {
                if (!detached)
                {
                    lock (gate) { demand.Observers.Remove(id); demand.Consumers--; }
                    if (observer is not null) await observer.CloseAsync();
                }
            }
            finally { lock (gate) { consumers--; if (consumers == 0) drained.TrySetResult(); } }
        }
    }
    private async Task RunAsync()
    {
        while (true)
        {
            await wake.WaitAsync();
            while (true)
            {
                Demand? demand;
                lock (gate)
                {
                    demand = queue.TryDequeue(out var next) ? next : null;
                    if (demand is null && closed) return;
                }
                if (demand is null) break;
                ItemClassificationBatch? result = null; Exception? failure = null;
                try
                {
                    demand.Cancellation.Token.ThrowIfCancellationRequested();
                    result = await store.PrepareBatchAsync(demand.Request, value => ReportAsync(demand, value), demand.Cancellation.Token);
                }
                catch (Exception error) { failure = error; }
                await demand.Dispatch.WaitAsync();
                try { lock (gate) demand.ProgressClosed = true; }
                finally { demand.Dispatch.Release(); }
                lock (gate)
                {
                    demands.Remove(demand.Key);
                    if (failure is OperationCanceledException canceled) demand.Completion.TrySetCanceled(canceled.CancellationToken);
                    else if (failure is not null) demand.Completion.TrySetException(failure);
                    else demand.Completion.TrySetResult(result!);
                    demand.Cancellation.Dispose();
                }
            }
        }
    }
    private async Task ReportAsync(Demand demand, ItemClassificationProgress value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Stage) || value.Stage.Length > 128 || value.Stage.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.Unit) || value.Unit.Length > 32 || value.Unit.Any(char.IsControl) ||
            value.Completed is < 0 || value.Total is < 0 || value.Completed is { } done && value.Total is { } total && done > total)
            throw new DiscoveryException("ITEM_CLASSIFICATIONS_PROGRESS_INVALID", "Classification progress must contain bounded text and measured nonnegative stage counters.");
        if (!await demand.Dispatch.WaitAsync(0)) return;
        try
        {
            Observer[] observers;
            lock (gate) observers = demand.Stopping || demand.ProgressClosed ? [] : demand.Observers.Values.ToArray();
            foreach (var observer in observers) await observer.ReportAsync(value);
        }
        finally { demand.Dispatch.Release(); }
    }
    private void Wake() { if (wake.CurrentCount == 0) wake.Release(); }
    private void RequireOpen() { if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true); }
    private static DiscoveryException Busy() => new("ITEM_CLASSIFICATIONS_BUSY", "Classification demands are at their bounded capacity or stopping.", true);
    private static async Task CancelAsync(Demand demand)
    {
        try { await demand.Cancellation.CancelAsync(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A faulty callback cannot bypass actual producer retirement. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Demand[] pending;
        lock (gate) { closed = true; pending = demands.Values.ToArray(); foreach (var demand in pending) demand.Stopping = true; Wake(); }
        foreach (var demand in pending) await CancelAsync(demand);
        await worker;
        Task retirement; lock (gate) retirement = drained.Task;
        await retirement;
    }
    public static void Validate(ItemClassificationBatchRequest request)
    {
        if (request is null) throw Invalid(); ItemsService.Validate(request.Context);
        if (request.PolicyRevision is not ("classification-label-index-v1" or "classification-label-index-v2") || !Hash(request.ArtifactId) || request.ItemIds is null || request.ItemIds.Count is < 1 or > 50) throw Invalid();
        foreach (var id in request.ItemIds) if (!ItemId(id)) throw Invalid();
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ItemId(string? value) => value is { Length: >= 1 and <= 10 } && (value.Length == 1 || value[0] != '0') &&
        value.All(ch => ch is >= '0' and <= '9') && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact Items feature, one to 50 canonical unsigned Item IDs.");
}
