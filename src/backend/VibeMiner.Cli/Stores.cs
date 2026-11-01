using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>One open object store, as the commands use it. The tests give a fake; <see cref="RealStores"/> opens the real one.</summary>
internal interface IStoreHandle : IAsyncDisposable
{
    /// <summary>Whether this handle holds the writer lock.</summary>
    bool Writer { get; }
    /// <summary>History and objects through layer 2, local only: nothing here touches the network.</summary>
    IObjectSource Objects { get; }
    ValueTask<StoreStats> StatsAsync(CancellationToken ct);
    Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
}

/// <summary>Opens the store in a folder. Both throw <see cref="ObjectStoreException"/> with a named code; neither creates a store.</summary>
internal interface IStoreOpener
{
    Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct);
    /// <summary>Takes the writer lock (OBJECT_STORE_BUSY when the app or another writer holds it).</summary>
    Task<IStoreHandle> OpenWriterAsync(string folder, CancellationToken ct);
}

/// <summary>The CLI's composition root for the store: layer 1 opened here, everything read above it goes through layer 2.</summary>
internal sealed class RealStores : IStoreOpener
{
    public async Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct)
    {
        var store = await ObjectStore.OpenReadOnlyAsync(folder, ct: ct).ConfigureAwait(false);
        return new ReadOnlyHandle(store);
    }

    public async Task<IStoreHandle> OpenWriterAsync(string folder, CancellationToken ct)
    {
        // A writer open creates a missing store; a read-only open first makes "missing" an answer, not a new empty store.
        // Only OBJECT_STORE_MISSING stops here: an unreadable index is what the writer's scrub repairs.
        try { await (await ObjectStore.OpenReadOnlyAsync(folder, ct: ct).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code != ObjectStoreCodes.Missing) { }
        var store = await ObjectStore.OpenAsync(folder, ct: ct).ConfigureAwait(false);
        return new WriterHandle(store);
    }

    private sealed class ReadOnlyHandle(ReadOnlyObjectStore store) : IStoreHandle
    {
        public bool Writer => false;
        public IObjectSource Objects { get; } = new LocalObjectSource(store, store);
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => store.StatsAsync(ct);
        public Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => store.ScrubAsync(false, progress, ct);
        public ValueTask DisposeAsync() => store.DisposeAsync();
    }

    private sealed class WriterHandle(ObjectStore store) : IStoreHandle
    {
        public bool Writer => true;
        public IObjectSource Objects { get; } = new LocalObjectSource(store, store);
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => store.StatsAsync(ct);
        public Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => ((IObjectStoreMaintenance)store).ScrubAsync(false, progress, ct);
        public ValueTask DisposeAsync() => store.DisposeAsync();
    }
}
