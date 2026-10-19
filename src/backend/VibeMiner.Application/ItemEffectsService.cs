using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Shared Host/non-Host end-to-end publication ownership; adapters own source sharing.</summary>
public sealed class ItemEffectsService(IItemEffectsStore store, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HashSet<ItemEffectsOperationLease> publications = [];
    private int longOperations;
    private bool closed;
    private TaskCompletionSource? disposed;

    public ItemEffectsOperationLease Admit(ItemEffectsOperation operation)
    {
        if (!Enum.IsDefined(operation)) throw new DiscoveryException("INVALID_REQUEST", "Choose a defined effects operation.");
        var isLong = operation is ItemEffectsOperation.PrepareLinks or ItemEffectsOperation.PrepareDetails or ItemEffectsOperation.PrepareNames;
        lock (gate)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (publications.Count == 4 || isLong && longOperations == 2)
                throw new DiscoveryException("ITEM_EFFECTS_BUSY", "Effects publishers are at their bounded capacity or retiring.", true);
            var lease = new ItemEffectsOperationLease(this, operation);
            publications.Add(lease); if (isLong) longOperations++;
            return lease;
        }
    }
    public Task<ItemEffectsReadiness> ReadAsync(ItemEffectsOperationLease lease, ItemEffectsSelection selection, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.Read, () => { ItemEffectsValidation.Validate(selection); return selection; },
            (owned, _, token) => store.ReadAsync(owned, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), null, ct);
    public Task<ItemEffectsReadiness> PrepareLinksAsync(ItemEffectsOperationLease lease, ItemEffectsSelection selection,
        Func<ItemEffectsProgress, Task>? progress = null, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.PrepareLinks, () => { ItemEffectsValidation.Validate(selection); return selection; },
            (owned, report, token) => store.PrepareLinksAsync(owned, report, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), progress, ct);
    public Task<ItemEffectsPage> ReadPageAsync(ItemEffectsOperationLease lease, ItemEffectsPageRequest request, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.ReadPage, () => ItemEffectsValidation.Snapshot(request),
            (owned, _, token) => store.ReadPageAsync(owned, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), null, ct);
    public Task<ItemEffectDetailsResult> ReadDetailsAsync(ItemEffectsOperationLease lease, ItemEffectDetailsRequest request, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.ReadDetails, () => ItemEffectsValidation.Snapshot(request),
            (owned, _, token) => store.ReadDetailsAsync(owned, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), null, ct);
    public Task<ItemEffectDetailsResult> PrepareDetailsAsync(ItemEffectsOperationLease lease, ItemEffectDetailsRequest request,
        Func<ItemEffectsProgress, Task>? progress = null, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.PrepareDetails, () => ItemEffectsValidation.Snapshot(request),
            (owned, report, token) => store.PrepareDetailsAsync(owned, report, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), progress, ct);
    public Task<ItemEffectNamesResult> ReadNamesAsync(ItemEffectsOperationLease lease, ItemEffectNamesRequest request, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.ReadNames, () => ItemEffectsValidation.Snapshot(request),
            (owned, _, token) => store.ReadNamesAsync(owned, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), null, ct);
    public Task<ItemEffectNamesResult> PrepareNamesAsync(ItemEffectsOperationLease lease, ItemEffectNamesRequest request,
        Func<ItemEffectsProgress, Task>? progress = null, CancellationToken ct = default) =>
        ExecuteAsync(lease, ItemEffectsOperation.PrepareNames, () => ItemEffectsValidation.Snapshot(request),
            (owned, report, token) => store.PrepareNamesAsync(owned, report, token), (result, owned) => ItemEffectsValidation.Validate(result, owned), progress, ct);

    private async Task<TResult> ExecuteAsync<TRequest, TResult>(ItemEffectsOperationLease lease, ItemEffectsOperation operation,
        Func<TRequest> snapshot, Func<TRequest, Func<ItemEffectsProgress, Task>, CancellationToken, Task<TResult>> work,
        Action<TResult, TRequest> validate, Func<ItemEffectsProgress, Task>? progress, CancellationToken ct)
    {
        lock (gate)
        {
            if (lease is null || lease.Owner != this || lease.Operation != operation || lease.Claimed || lease.Disposing || !publications.Contains(lease))
                throw new DiscoveryException("ITEM_EFFECTS_LEASE_INVALID", "Use one matching effects operation on its owning service lease.");
            // Retirement TCS was installed when admitted, before any competing Dispose can see Claimed.
            lease.Claimed = true;
        }
        ItemEffectsProgressObserver? observer = null;
        CancellationTokenRegistration registration = default;
        try
        {
            var owned = snapshot();
            ct.ThrowIfCancellationRequested();
            lock (gate) { if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true); }
            registration = ct.UnsafeRegister(_ => { _ = RequestCancellation(lease); }, null);
            lease.Cancellation.Token.ThrowIfCancellationRequested();
            observer = progress is null ? null : new(progress, timeProvider ?? TimeProvider.System);
            Task Report(ItemEffectsProgress value)
            {
                ItemEffectsValidation.ValidateProgress(value);
                return observer?.ReportAsync(value) ?? Task.CompletedTask;
            }
            var result = await work(owned, Report, lease.Cancellation.Token);
            validate(result, owned);
            return result;
        }
        catch (DiscoveryException error) when (error.Code == "ITEM_EFFECTS_CLEANUP_FAILED")
        { lock (gate) lease.CleanupFailure = error; throw; }
        finally
        {
            try
            {
                await registration.DisposeAsync();
                if (observer is not null) await observer.CloseAsync();
            }
            catch (Exception error) { lock (gate) lease.CleanupFailure = error; throw Cleanup(); }
            finally
            {
                Task notification;
                lock (gate) { lease.ProducerFinished = true; notification = lease.CancellationRetired?.Task ?? Task.CompletedTask; }
                try { await notification; }
                finally { lease.OperationRetired.TrySetResult(); }
            }
        }
    }
    private Task RequestCancellation(ItemEffectsOperationLease lease)
    {
        TaskCompletionSource completion; bool start;
        lock (gate)
        {
            if (lease.ProducerFinished) return Task.CompletedTask;
            start = lease.CancellationRetired is null;
            completion = lease.CancellationRetired ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = CancelAsync();
        return completion.Task;
        async Task CancelAsync()
        {
            try { await lease.Cancellation.CancelAsync(); }
            catch (AggregateException) { /* Observed callback failure cannot skip actual worker retirement. */ }
            finally { completion.TrySetResult(); }
        }
    }
    internal Task RetirePublicationAsync(ItemEffectsOperationLease lease)
    {
        lock (gate)
        {
            if (lease.Disposing) return lease.PublicationRetired.Task;
            lease.Disposing = true;
            if (!lease.Claimed) { lease.ProducerFinished = true; lease.OperationRetired.TrySetResult(); }
        }
        _ = RetireAsync(); return lease.PublicationRetired.Task;
        async Task RetireAsync()
        {
            try
            {
                await RequestCancellation(lease);
                await lease.OperationRetired.Task;
                lock (gate)
                {
                    if (lease.CleanupFailure is not null) throw Cleanup();
                    lease.Cancellation.Dispose();
                    publications.Remove(lease); if (lease.IsLong) longOperations--;
                }
                lease.PublicationRetired.TrySetResult();
            }
            catch (Exception) { lease.PublicationRetired.TrySetException(Cleanup()); }
        }
    }
    public ValueTask DisposeAsync()
    {
        ItemEffectsOperationLease[] accepted; TaskCompletionSource completion;
        lock (gate)
        {
            if (disposed is not null) return new(disposed.Task);
            closed = true; completion = disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            accepted = publications.ToArray();
        }
        _ = CloseAsync(); return new(completion.Task);
        async Task CloseAsync()
        {
            try
            {
                // Publishers dispose their own leases after their terminal writer retires.
                await Task.WhenAll(accepted.Select(RequestCancellation));
                await Task.WhenAll(accepted.Select(l => l.PublicationRetired.Task));
                completion.TrySetResult();
            }
            catch (Exception) { completion.TrySetException(Cleanup()); }
        }
    }
    private static DiscoveryException Cleanup() => new("ITEM_EFFECTS_CLEANUP_FAILED", "Effects resources did not retire safely.");
}
