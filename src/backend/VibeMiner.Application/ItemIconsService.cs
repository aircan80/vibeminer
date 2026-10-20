using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Four bounded shared visible demands, one producer, independent consumer cancellation.</summary>
public sealed class ItemIconsService : IAsyncDisposable
{
    public const int MaximumBatches = 4, MaximumConsumers = 8;
    private readonly IItemIconsStore store;
    private readonly object gate = new();
    private readonly Dictionary<string, Demand> demands = new(StringComparer.Ordinal);
    private readonly Queue<Demand> queue = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private bool closed;
    private int consumers;
    private TaskCompletionSource? consumersRetired;
    private sealed class Demand(ItemIconBatchRequest request, string key)
    {
        internal readonly ItemIconBatchRequest Request = request;
        internal readonly string Key = key;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<ItemIconBatch> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Dictionary<long, Observer> Observers = [];
        internal long NextObserver;
        internal int Consumers;
        internal bool Stopping, ProgressClosed;
        internal readonly SemaphoreSlim Dispatch = new(1, 1);
    }
    private sealed class Observer(Func<ItemIconProgress, Task> callback)
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed;
        internal async Task ReportAsync(ItemIconProgress progress)
        {
            await serial.WaitAsync();
            try { if (!closed) try { await callback(progress); } catch (Exception e) when (e is not OutOfMemoryException) { closed = true; } }
            finally { serial.Release(); }
        }
        internal async Task CloseAsync()
        { await serial.WaitAsync(); try { closed = true; } finally { serial.Release(); } }
    }
    public ItemIconsService(IItemIconsStore store) { this.store = store; worker = RunAsync(); }

    public async Task<ItemIconBatch> PrepareBatchAsync(ItemIconBatchRequest request,
        Func<ItemIconProgress, Task>? progress = null, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        // Caller mutation cannot change either the producer authority or the shared-demand key.
        request = request with { ItemIds = Array.AsReadOnly(request.ItemIds.ToArray()) };
        Validate(request);
        var key = request.Context.ProductId + ":" + request.Context.BuildIdentity + ":" + request.Context.SourceRegion + ":" +
            request.Context.ResearchLocale + ":" + request.ArtifactId + ":" + string.Join(',', request.ItemIds);
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
            if (consumers++ == 0) consumersRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            demand.Consumers++; id = ++demand.NextObserver;
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
            finally
            {
                // Logical demand withdrawal and actual subscription retirement are separate.
                // A closing observer or last-consumer producer cleanup still owns capacity.
                lock (gate)
                {
                    if (--consumers == 0) consumersRetired!.TrySetResult();
                }
            }
        }
    }
    public Task<IItemIconReadLease> OpenAsync(ItemIconReadRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested(); lock (gate) RequireOpen();
        return store.OpenAsync(request, token);
    }
    private async Task RunAsync()
    {
        while (true)
        {
            await wake.WaitAsync();
            while (true)
            {
                Demand? demand; bool stopping;
                lock (gate)
                {
                    demand = queue.TryDequeue(out var next) ? next : null;
                    if (demand is null && closed) return;
                    // This dequeue is admission. Shutdown and a last canceling consumer set Stopping under
                    // the gate before they cancel, and may not have reached this token yet (#22).
                    stopping = demand?.Stopping == true;
                }
                if (demand is null) break;
                ItemIconBatch? result = null; Exception? failure = null;
                try
                {
                    if (stopping) throw new OperationCanceledException(demand.Cancellation.Token);
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
    private async Task ReportAsync(Demand demand, ItemIconProgress value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.Stage) || value.Stage.Length > 128 || value.Stage.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(value.Unit) || value.Unit.Length > 32 || value.Unit.Any(char.IsControl) ||
            value.Completed is < 0 || value.Total is < 0 || value.Completed is { } done && value.Total is { } total && done > total)
            throw new DiscoveryException("ITEM_ICONS_PROGRESS_INVALID", "Icon progress must contain bounded text and measured nonnegative stage counters.");
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
    private static DiscoveryException Busy() => new("ITEM_ICONS_BUSY", "Visible icon demands are at their bounded capacity or stopping.", true);
    private static async Task CancelAsync(Demand demand)
    {
        try { await demand.Cancellation.CancelAsync(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* A faulty callback cannot bypass actual producer retirement. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Demand[] pending; Task retirement;
        lock (gate)
        {
            closed = true; pending = demands.Values.ToArray();
            foreach (var demand in pending) demand.Stopping = true;
            retirement = consumersRetired?.Task ?? Task.CompletedTask; Wake();
        }
        foreach (var demand in pending) await CancelAsync(demand);
        await worker;
        await retirement;
    }
    public static void Validate(ItemIconBatchRequest request)
    {
        if (request is null) throw Invalid(); ItemsService.Validate(request.Context);
        if (!Hash(request.ArtifactId) || request.ItemIds is null || request.ItemIds.Count > 50) throw Invalid();
        foreach (var id in request.ItemIds) if (!ItemId(id)) throw Invalid();
    }
    public static void Validate(ItemIconReadRequest request)
    {
        if (request is null) throw Invalid(); ItemsService.Validate(request.Context);
        if (!Hash(request.ArtifactId) || !Hash(request.ImageArtifactId) || !ItemId(request.ItemId)) throw Invalid();
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ItemId(string? value) => value is { Length: >= 1 and <= 10 } && (value.Length == 1 || value[0] != '0') &&
        value.All(ch => ch is >= '0' and <= '9') && uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact Items feature, at most 50 unsigned Item IDs and an approved image artifact.");
}
