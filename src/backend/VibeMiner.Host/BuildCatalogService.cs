using VibeMiner.Builds;
using VibeMiner.Domain;
using VibeMiner.Objects;

namespace VibeMiner.Host;

/// <summary>
/// The desktop's build list from layer 3 (#205). The object store's writer lock is taken on the first catalog request (creating a
/// missing store) and kept for the process lifetime; when another process holds it, the next request tries again.
/// <see cref="ListAsync"/> then reads the store read-only for that one request; <see cref="RefreshAsync"/> answers
/// <c>OBJECT_STORE_BUSY</c>. Dispose after the protocol host stops, so no request still uses the writer.
/// </summary>
public sealed class BuildCatalogService(IBuildCatalogStores stores, TimeProvider? time = null) : IAsyncDisposable
{
    /// <summary>Products refreshed at once.</summary>
    public const int MaxProductsInFlight = 3;
    public const string BusyMessage = "Another VibeMiner process, such as vm browse, is writing the build store. Close it, then refresh.";
    public const string ProgressStage = "Checking the version service";

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IBuildCatalogHandle? writer;
    private bool disposed;

    /// <summary><c>catalog.list</c>: local only. Never fails for the lock; answers <c>readOnly</c> instead.</summary>
    public async Task<object> ListAsync(CancellationToken cancellationToken)
    {
        if (await WriterAsync(cancellationToken).ConfigureAwait(false) is { } held)
            return await UseAsync(async () => BuildCatalogWire.List(await BuildCatalogWire.CatalogAsync(held, writer: true, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
        var reader = await OpenAsync(() => stores.OpenReadOnlyAsync(cancellationToken)).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
            return await UseAsync(async () => BuildCatalogWire.List(await BuildCatalogWire.CatalogAsync(reader, writer: false, cancellationToken).ConfigureAwait(false))).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>catalog.refresh</c>: every reviewed product, <see cref="MaxProductsInFlight"/> at once, then the summary, then the list.
    /// <paramref name="progress"/> gets (completed, total): once before any product starts, once per finished product and once
    /// after the summary; total counts the summary as one. Cancel stops starting products, cancels the running ones and throws
    /// once they retired; what they observed stays in the history log.
    /// </summary>
    public async Task<object> RefreshAsync(Func<int, int, Task> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var held = await WriterAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new DiscoveryException(ObjectStoreCodes.Busy, BusyMessage, true);
        return await UseAsync(async () =>
        {
            var products = Products.Reviewed;
            var total = products.Count + 1;
            var results = new CatalogRefresh?[products.Count];
            var completed = 0;
            using var reporting = new SemaphoreSlim(1, 1);
            await progress(0, total).ConfigureAwait(false);
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, products.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = MaxProductsInFlight, CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        results[index] = await held.Catalog.RefreshAsync(products[index].Id, token).ConfigureAwait(false);
                        await reporting.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try { await progress(++completed, total).ConfigureAwait(false); }
                        finally { reporting.Release(); }
                    }).ConfigureAwait(false);
            }
            // ForEachAsync reports cancellation even when the token was set after the last product finished.
            catch (OperationCanceledException) when (Volatile.Read(ref completed) == products.Count) { }
            var summary = await held.Catalog.RefreshSummaryAsync(cancellationToken).ConfigureAwait(false);
            await progress(total, total).ConfigureAwait(false);
            // Every observation is in the history log now; a late cancel doesn't hide it.
            var catalog = await BuildCatalogWire.CatalogAsync(held, writer: true, CancellationToken.None).ConfigureAwait(false);
            return BuildCatalogWire.Refresh(catalog, clock.GetUtcNow(), results.OfType<CatalogRefresh>().ToArray(), summary);
        }).ConfigureAwait(false);
    }

    /// <summary>The writer, opened on first use; null while another process holds the lock.</summary>
    private async Task<IBuildCatalogHandle?> WriterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (writer is not null) return writer;
            try { return writer = await OpenAsync(() => stores.OpenWriterAsync(cancellationToken)).ConfigureAwait(false); }
            catch (DiscoveryException e) when (e.Code == ObjectStoreCodes.Busy) { return null; }
        }
        finally { gate.Release(); }
    }

    private static async Task<IBuildCatalogHandle> OpenAsync(Func<Task<IBuildCatalogHandle>> open)
    {
        try { return await open().ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { throw new DiscoveryException(e.Code, BusyMessage, true); }
        catch (ObjectStoreException e) { throw Unavailable("opened", e.Code); }
        catch (CatalogStoreException e) { throw Unavailable("opened", e.Code); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Unavailable("opened", e.GetType().Name); }
    }

    private static async Task<T> UseAsync<T>(Func<Task<T>> use)
    {
        try { return await use().ConfigureAwait(false); }
        catch (ObjectStoreException e) { throw Unavailable("read", e.Code); }
        catch (CatalogStoreException e) { throw Unavailable("read", e.Code); }
    }

    private static DiscoveryException Unavailable(string verb, string code) =>
        new("BUILD_STORE_UNAVAILABLE", $"The build store can't be {verb} ({code}).", true);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
            writer = null;
        }
        finally { gate.Release(); }
    }
}
