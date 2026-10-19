using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Selected-item effects over immutable bridge/target artifacts and the shared Tables pipeline.</summary>
public sealed partial class ItemEffectsStore : IItemEffectsStore, IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly ItemsStore items;
    private readonly TableDataService tables;
    private readonly AcquisitionResources resources;
    private readonly TimeProvider clock;
    private readonly ItemEffectsStoreStagingHooks? stagingHooks;
    private readonly Action<string>? observeResources;
    private readonly Func<StoredTableAdmission, CancellationToken, Task<FileStream>>? openSource;
    private readonly object gate = new();
    private readonly List<Operation> active = new(4);
    private readonly List<BridgeSlot> bridges = new(2);
    private readonly List<TargetSlot> targets = new(2);
    private int preparations, queries;
    private bool closed, cleanupDebt, builder;
    private Task? disposal;

    public ItemEffectsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, ItemsStore items,
        TableDataService tables, AcquisitionResources resources) : this(catalog, objects, items, tables, resources, TimeProvider.System) { }
    internal ItemEffectsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, ItemsStore items,
        TableDataService tables, AcquisitionResources resources, TimeProvider clock, ItemEffectsStoreStagingHooks? stagingHooks = null, Action<string>? observeResources = null, Func<StoredTableAdmission, CancellationToken, Task<FileStream>>? openSource = null)
    { this.catalog = catalog; this.objects = objects; this.items = items; this.tables = tables; this.resources = resources; this.clock = clock; this.stagingHooks = stagingHooks; this.observeResources = observeResources; this.openSource = openSource; }

    internal (int Operations, int Preparations, int Queries, int BridgeSlots, int TargetSlots, int BridgePins, int TargetPins, bool Builder) Pressure
    { get { lock (gate) return (active.Count, preparations, queries, bridges.Count, targets.Count, bridges.Sum(slot => slot.Pins), targets.Sum(slot => slot.Pins), builder); } }
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Busy() => new("ITEM_EFFECTS_BUSY", "Effects work is at its bounded capacity or retiring.", true);
    private static DiscoveryException Cleanup() => ItemEffectsBridgePolicy.Cleanup();
    private static DiscoveryException Stale() => new("ITEM_EFFECTS_BRIDGE_STALE", "The selected effects bridge source or index changed.", true);
    private void Debt() { lock (gate) cleanupDebt = true; }

    private sealed class Operation(ItemEffectsStore owner, bool prepare, Func<ItemEffectsProgress, Task>? callback)
    {
        private readonly object sync = new();
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Task> Worker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly bool Prepare = prepare;
        internal readonly StoreObserver? Observer = callback is null ? null : new(callback, owner.clock);
        private CancellationTokenRegistration registration;
        private TaskCompletionSource? notification;
        private bool retiring;
        internal bool CleanupFailed;
        internal CancellationToken Token => Cancellation.Token;
        internal void Attach(CancellationToken token) => registration = token.UnsafeRegister(static state =>
            { _ = ((Operation)state!).CancelAsync(); }, this);
        internal Task ReportAsync(ItemEffectsProgress progress) => Observer?.PostAsync(progress) ?? Task.CompletedTask;
        internal Task CancelAsync()
        {
            TaskCompletionSource completion;
            lock (sync)
            {
                if (notification is not null) return notification.Task;
                if (retiring) return Task.CompletedTask;
                completion = notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _ = NotifyAsync(); return completion.Task;
            async Task NotifyAsync()
            {
                await Task.Yield();
                try { await Cancellation.CancelAsync(); }
                catch (AggregateException) { /* Notification has retired; actual worker cleanup is still awaited. */ }
                finally { completion.TrySetResult(); }
            }
        }
        internal async Task RetireAsync()
        {
            await registration.DisposeAsync();
            Task? pending; lock (sync) { retiring = true; pending = notification?.Task; }
            Exception? failure = null;
            try { if (Observer is not null) await Observer.CloseAsync(); } catch (Exception error) { failure = error; }
            try { if (pending is not null) await pending; } catch (Exception error) { failure ??= error; }
            Cancellation.Dispose();
            if (failure is not null) throw Cleanup();
        }
    }
    private Task<T> RunAsync<T>(bool prepare, Func<ItemEffectsProgress, Task>? callback,
        CancellationToken token, Func<Operation, Task<T>> work)
    {
        token.ThrowIfCancellationRequested(); Operation operation;
        lock (gate)
        {
            if (cleanupDebt) throw Cleanup();
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (active.Count == 4 || prepare && preparations == 2) throw Busy();
            operation = new(this, prepare, callback); active.Add(operation); if (prepare) preparations++;
        }
        operation.Attach(token);
        var worker = ExecuteAsync(); operation.Worker.TrySetResult(worker);
        _ = RetireAccountingAsync(); return worker;
        async Task<T> ExecuteAsync()
        {
            // Install the actual returned Task before work/cleanup can become terminal.
            await Task.Yield();
            try { return await work(operation); }
            finally
            {
                try { await operation.RetireAsync(); }
                catch { operation.CleanupFailed = true; Debt(); throw Cleanup(); }
            }
        }
        async Task RetireAccountingAsync()
        {
            // Charge admission until the PUBLIC worker Task itself is terminal, not an earlier
            // source-finished signal or a finally block immediately before Task completion.
            try { await worker; } catch (Exception) { }
            if (!operation.CleanupFailed)
            {
                lock (gate) { active.Remove(operation); if (prepare) preparations--; }
                operation.Finished.TrySetResult();
            }
            else { operation.Finished.TrySetException(Cleanup()); _ = operation.Finished.Task.Exception; }
        }
    }
    private sealed class Query(ItemEffectsStore owner) : IDisposable
    {
        private bool released;
        public void Dispose() { lock (owner.gate) { if (released) return; released = true; owner.queries--; } }
    }
    private Query EnterQuery(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); lock (gate)
        {
            if (cleanupDebt) throw Cleanup();
            if (queries == 4) throw Busy();
            queries++; return new(this);
        }
    }
    private async Task<ItemEffectsItemsProof> CaptureAsync(ItemEffectsSelection selection, CancellationToken token)
    {
        ItemEffectsValidation.Validate(selection);
        using var query = EnterQuery(token);
        // The shared helper has finished all discovery/source/configuration validation.
        // Effects consumes scalar build identity and the original opening documents,
        // never discovery history. Do not retain that graph through later waits/copies.
        return CompactItemsProof(await items.AuthorizeItemEffectsAsync(selection, token));
    }
    internal static ItemEffectsItemsProof CompactItemsProof(ItemEffectsItemsProof proof) => proof with
    {
        Opening = proof.Opening with
        {
            Build = proof.Opening.Build with
            {
                IdentityFields = System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
                Regions = Array.Empty<string>(), AdvertisedRegions = Array.Empty<string>(),
                RouteRegions = Array.Empty<string>(), SourceReceipts = Array.Empty<SourceReceipt>(),
                CdnConfigHistory = Array.Empty<CdnConfigObservation>()
            }
        }
    };
    private static void RequireItem(ItemEffectsItemsProof proof)
    {
        if (!proof.Item.Admitted) throw new DiscoveryException("ITEM_EFFECTS_ITEM_NOT_ADMITTED", "Choose an Item in the current admitted Items scope.");
        if (ulong.Parse(proof.Item.ItemId, CultureInfo.InvariantCulture) > int.MaxValue)
            throw new DiscoveryException("ITEM_EFFECTS_ITEM_KEY_OUT_OF_RANGE", "The selected Item key is outside the signed bridge domain.");
    }
    private async Task CpuAsync(Action action, CancellationToken token)
    {
        try { using (await resources.EnterCpuAsync(null, token)) { token.ThrowIfCancellationRequested(); action(); } }
        finally { await Task.Yield(); }
    }
    private async Task<T> CatalogCopyAsync<T>(Func<Task<T>> invoke, CancellationToken token)
    {
        Task<T>? scheduled = null;
        // Catalog ports own one scheduled SQL worker, but their bounded input copies happen
        // synchronously before returning its Task. Admit that CPU separately, then release it
        // BEFORE waiting for SQL/queue retirement; the accepted Task is always observed.
        await CpuAsync(() => scheduled = invoke(), token);
        observeResources?.Invoke("catalog-input-queued");
        return await scheduled!;
    }
    private async Task RecordBridgeAsync(ItemEffectsBridgeArtifact artifact, CancellationToken token)
    {
        Task? scheduled = null;
        await CpuAsync(() => scheduled = catalog.RecordItemEffectsBridgeArtifactAsync(artifact, token), token);
        await scheduled!;
    }
    private static int Parent(ItemEffectsItemsProof proof) => int.Parse(proof.Item.ItemId, CultureInfo.InvariantCulture);
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion; Operation[] accepted;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true; accepted = active.ToArray(); completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task;
        }
        _ = StopAsync(); return new(completion.Task);
        async Task StopAsync()
        {
            Exception? failure = null;
            // Start every independent cancellation before awaiting any callback/worker.
            var notifications = accepted.Select(operation => operation.CancelAsync()).ToArray();
            foreach (var notification in notifications) try { await notification; } catch (Exception error) { failure ??= error; }
            foreach (var operation in accepted)
            {
                // Ordinary operation errors/cancellation belong to its caller; cleanup failure is latched.
                try { await (await operation.Worker.Task); } catch (Exception) { }
                try { await operation.Finished.Task; } catch (Exception error) { failure ??= error; }
            }
            try { await CloseSessionsAsync(); } catch (Exception error) { failure ??= error; }
            lock (gate) if (cleanupDebt) failure ??= Cleanup();
            if (failure is null) completion.TrySetResult(); else { Debt(); completion.TrySetException(Cleanup()); }
        }
    }
}
