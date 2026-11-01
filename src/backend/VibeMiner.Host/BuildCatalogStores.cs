using VibeMiner.Builds;
using VibeMiner.Composition;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Host;

/// <summary>One open object store with the layer 3 catalog over it. Disposing closes the catalog, then layer 2, then the store.</summary>
public interface IBuildCatalogHandle : IAsyncDisposable
{
    /// <summary>History and objects through layer 2. Read-only handles are local only.</summary>
    IObjectSource Objects { get; }
    ICatalog Catalog { get; }
}

/// <summary>
/// Opens the store and catalog for <see cref="BuildCatalogService"/>. Both throw <see cref="ObjectStoreException"/> with a named code
/// (<c>OBJECT_STORE_BUSY</c> when another process holds the writer lock) or <see cref="CatalogStoreException"/>. The tests give a fake.
/// </summary>
public interface IBuildCatalogStores
{
    /// <summary>Takes the writer lock, creating a missing store.</summary>
    Task<IBuildCatalogHandle> OpenWriterAsync(CancellationToken cancellationToken);
    /// <summary>Opens the existing store read-only; nothing touches the network.</summary>
    Task<IBuildCatalogHandle> OpenReadOnlyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The real stores in a data folder, at the paths <c>vm</c> uses: <c>cdn-store-v1/</c> and <c>catalog-v1/catalog.sqlite3</c>. The writer's
/// layer 2 is <see cref="WriterObjectSource"/> (the store, then the official CDN and version service).
/// </summary>
public sealed class DataFolderBuildCatalogStores(string dataFolder) : IBuildCatalogStores
{
    public async Task<IBuildCatalogHandle> OpenWriterAsync(CancellationToken cancellationToken)
    {
        var store = await ObjectStore.OpenAsync(DataFolderPaths.ObjectStoreFolder(dataFolder), ct: cancellationToken).ConfigureAwait(false);
        WriterObjectSource? source = null;
        try
        {
            source = new WriterObjectSource(store);
            var catalog = await Catalog.OpenAsync(source.Objects, DataFolderPaths.CatalogPath(dataFolder), null, cancellationToken).ConfigureAwait(false);
            return new Handle(source.Objects, catalog, source, store);
        }
        catch
        {
            source?.Dispose();
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IBuildCatalogHandle> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var store = await ObjectStore.OpenReadOnlyAsync(DataFolderPaths.ObjectStoreFolder(dataFolder), ct: cancellationToken).ConfigureAwait(false);
        try
        {
            var objects = new LocalObjectSource(store, store);
            var catalog = await Catalog.OpenAsync(objects, DataFolderPaths.CatalogPath(dataFolder), null, cancellationToken).ConfigureAwait(false);
            return new Handle(objects, catalog, null, store);
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Handle(IObjectSource objects, Catalog catalog, IDisposable? source, IAsyncDisposable store) : IBuildCatalogHandle
    {
        public IObjectSource Objects => objects;
        public ICatalog Catalog => catalog;
        public async ValueTask DisposeAsync()
        {
            try { await catalog.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                source?.Dispose();
                await store.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
