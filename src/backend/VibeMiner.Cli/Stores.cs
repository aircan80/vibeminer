using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>One open object store, as the commands use it. The tests give a fake; <see cref="RealStores"/> opens the real one.</summary>
internal interface IStoreHandle : IAsyncDisposable
{
    /// <summary>Whether this handle holds the writer lock.</summary>
    bool Writer { get; }
    /// <summary>
    /// History and objects through layer 2. Read-only: local only, nothing touches the network. Writer: the store, then the
    /// official version service and CDN, recording what they serve.
    /// </summary>
    IObjectSource Objects { get; }
    /// <summary>The build catalog over <see cref="Objects"/>, opened on first use and closed with the handle.</summary>
    Task<ICatalog> CatalogAsync(CancellationToken ct);
    ValueTask<StoreStats> StatsAsync(CancellationToken ct);
    Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Opens the store in a folder. Both throw <see cref="ObjectStoreException"/> with a named code; only a writer open asked to
/// creates a store. The catalog store lives beside it, at <c>catalog-v1/catalog.sqlite3</c> in the data folder (a cache of the
/// history log, created on first use).
/// </summary>
internal interface IStoreOpener
{
    Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct);
    /// <summary>
    /// Takes the writer lock (OBJECT_STORE_BUSY when the app or another writer holds it). With <paramref name="create"/> a
    /// missing store is created; without, it is OBJECT_STORE_MISSING.
    /// </summary>
    Task<IStoreHandle> OpenWriterAsync(string folder, bool create, CancellationToken ct);
}

/// <summary>The CLI's composition root for the store: layer 1 opened here, everything read above it goes through layer 2.</summary>
internal sealed class RealStores : IStoreOpener
{
    public const string CatalogFolder = "catalog-v1", CatalogFile = "catalog.sqlite3";

    public async Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct)
    {
        var store = await ObjectStore.OpenReadOnlyAsync(folder, ct: ct).ConfigureAwait(false);
        return new ReadOnlyHandle(store, CatalogPath(folder));
    }

    public async Task<IStoreHandle> OpenWriterAsync(string folder, bool create, CancellationToken ct)
    {
        // A writer open creates a missing store; unless asked to, a read-only open first makes "missing" an answer, not a new
        // empty store. Only OBJECT_STORE_MISSING stops here: an unreadable index is what the writer's scrub repairs.
        if (!create)
        {
            try { await (await ObjectStore.OpenReadOnlyAsync(folder, ct: ct).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (ObjectStoreException e) when (e.Code != ObjectStoreCodes.Missing) { }
        }
        var store = await ObjectStore.OpenAsync(folder, ct: ct).ConfigureAwait(false);
        return new WriterHandle(store, CatalogPath(folder));
    }

    /// <summary>The catalog store in the data folder that holds the object store folder.</summary>
    private static string CatalogPath(string storeFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storeFolder))!, CatalogFolder, CatalogFile);

    private abstract class Handle(string catalogPath) : IStoreHandle
    {
        private Catalog? catalog;
        public abstract bool Writer { get; }
        public abstract IObjectSource Objects { get; }
        public abstract ValueTask<StoreStats> StatsAsync(CancellationToken ct);
        public abstract Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct);
        public async Task<ICatalog> CatalogAsync(CancellationToken ct) =>
            catalog ??= await Catalog.OpenAsync(Objects, catalogPath, null, ct).ConfigureAwait(false);
        protected abstract ValueTask CloseStoreAsync();
        public async ValueTask DisposeAsync()
        {
            try { if (catalog is not null) await catalog.DisposeAsync().ConfigureAwait(false); }
            finally { await CloseStoreAsync().ConfigureAwait(false); }
        }
    }

    private sealed class ReadOnlyHandle(ReadOnlyObjectStore store, string catalogPath) : Handle(catalogPath)
    {
        public override bool Writer => false;
        public override IObjectSource Objects { get; } = new LocalObjectSource(store, store);
        public override ValueTask<StoreStats> StatsAsync(CancellationToken ct) => store.StatsAsync(ct);
        public override Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => store.ScrubAsync(false, progress, ct);
        protected override ValueTask CloseStoreAsync() => store.DisposeAsync();
    }

    /// <summary>
    /// The writer's layer 2 is the real <see cref="ObjectSource"/>: the store, then the official CDN and version service, and the
    /// wow-listfile's GitHub releases (D105) for <c>N</c> in <c>vm browse</c>. Layer 4 asks for the listfile only while the
    /// names switch is on; the listfile client has its own handler and connection budget.
    /// </summary>
    private sealed class WriterHandle : Handle
    {
        private readonly ObjectStore store;
        // Layer 1's handler: no redirects or decompression, bounded connections. The clients set their own timeouts.
        private readonly HttpClient http = new(CdnClient.CreateHandler(4)) { Timeout = Timeout.InfiniteTimeSpan };
        private readonly WowListfileConnections listfileConnections = new();
        private readonly HttpClient listfileHttp;

        public WriterHandle(ObjectStore store, string catalogPath) : base(catalogPath)
        {
            this.store = store;
            // The listfile client sets its own budgets; HttpClient's timeout would cut a part short.
            listfileHttp = new(WowListfileClient.CreateHandler(listfileConnections)) { Timeout = Timeout.InfiniteTimeSpan };
            Objects = new ObjectSource(store, store, store, null, new CdnClient(http),
                [new VersionServiceObservations(new VersionClient(http)),
                 new WowListfileObservations(new WowListfileClient(listfileHttp, connections: listfileConnections))], ObjectVerification.Default);
        }

        public override bool Writer => true;
        public override IObjectSource Objects { get; }
        public override ValueTask<StoreStats> StatsAsync(CancellationToken ct) => store.StatsAsync(ct);
        public override Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => ((IObjectStoreMaintenance)store).ScrubAsync(false, progress, ct);
        protected override async ValueTask CloseStoreAsync()
        {
            http.Dispose();
            listfileHttp.Dispose();
            await store.DisposeAsync().ConfigureAwait(false);
        }
    }
}
