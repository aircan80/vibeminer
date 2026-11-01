using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The one writer handle a browse session holds, shared by everyone in the session who asks for it: <c>r</c>, <c>a</c> and the
/// tables composition. The lock is taken by the first acquire and let go when the last lease is disposed, so the session never
/// blocks itself with OBJECT_STORE_BUSY; another process holding the lock still gives it.
/// </summary>
internal sealed class SharedWriter(IStoreOpener stores, string storeFolder)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IStoreHandle? inner;
    private int leases;

    /// <summary>Leases currently out; the handle is open while this is above 0.</summary>
    public int Leases => Volatile.Read(ref leases);

    /// <summary>
    /// A lease on the writer handle, opening it when none is out. <paramref name="create"/> only matters for that open: it
    /// creates a missing store. Dispose the lease once; the handle closes with the last one.
    /// </summary>
    public async Task<IStoreHandle> AcquireAsync(bool create, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            inner ??= await stores.OpenWriterAsync(storeFolder, create, ct).ConfigureAwait(false);
            leases++;
            return new Lease(this, inner);
        }
        finally { gate.Release(); }
    }

    private async ValueTask ReleaseAsync()
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (--leases > 0 || inner is null) return;
            var closing = inner;
            inner = null;
            await closing.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed class Lease(SharedWriter owner, IStoreHandle handle) : IStoreHandle
    {
        private int disposed;
        public bool Writer => handle.Writer;
        public IObjectSource Objects => handle.Objects;
        public Task<ICatalog> CatalogAsync(CancellationToken ct) => handle.CatalogAsync(ct);
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => handle.StatsAsync(ct);
        public Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => handle.ScrubAsync(progress, ct);
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) == 0 ? owner.ReleaseAsync() : ValueTask.CompletedTask;
    }
}
