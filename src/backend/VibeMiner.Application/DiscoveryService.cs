using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record SourceDocument(string Role, string Url, string RetrievedAt, string Sha256, byte[] Bytes,
    string ParserRevision = "text-metadata-v1");
public sealed record BuildObservation(BuildIdentity Identity, string Region, int VersionsOrdinal,
    int? CdnsOrdinal, IReadOnlyDictionary<string, string> OriginalFields,
    IReadOnlyDictionary<string, string>? RouteFields)
{
    /// <summary>The CDN configuration this exact row advertised. It is not part of the build identity.</summary>
    public string CdnConfig => BuildIdentity.ObservedCdnConfig(OriginalFields);
}
public sealed record DiscoveryAttempt(IReadOnlyList<SourceDocument> Documents,
    IReadOnlyList<BuildObservation> Observations, IReadOnlyList<string> SuccessfulRegions,
    IReadOnlyList<string> CheckedRegions, IReadOnlyList<DiscoveryProblem> Errors, string Connection);
public interface IDiscoverySource
{
    Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken cancellationToken);
}
public interface IDiscoveryCatalog : IAsyncDisposable
{
    Task<long> BeginRefreshAsync(string product, CancellationToken cancellationToken);
    Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt,
        CancellationToken cancellationToken);
    Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor,
        CancellationToken cancellationToken);
    /// <summary>One bounded page across every registered product, optionally narrowed to one product.</summary>
    Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor,
        CancellationToken cancellationToken);
}

public sealed class DiscoveryService(IDiscoverySource source, IDiscoveryCatalog catalog, TimeProvider? timeProvider = null)
{
    /// <summary>At most this many products are checked at once by a cross-product refresh.</summary>
    public const int MaxProductsInFlight = 3;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    /// <summary>
    /// Check every registered product with the existing per-product refresh, at most <see cref="MaxProductsInFlight"/>
    /// at a time. Each product commits on its own, so a product that finished before cancellation keeps its new
    /// evidence. A product whose attempt fails with a typed error is reported in RefreshProblems and keeps its earlier
    /// evidence; the others continue. Cancellation stops starting products, cancels the running ones and returns only
    /// after they retired. Progress reports finished products in order, one report per product plus the initial one.
    /// </summary>
    public async Task<DiscoveryCatalogPage> RefreshAllAsync(Func<DiscoveryRefreshProgress, Task>? progress,
        CancellationToken cancellationToken)
    {
        var products = ProductRegistry.Products;
        var problems = new ProductRefreshProblem?[products.Count];
        var completed = 0;
        using var reporting = new SemaphoreSlim(1, 1);
        if (progress is not null) await progress(new(0, products.Count));
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, products.Count),
                new ParallelOptions { MaxDegreeOfParallelism = MaxProductsInFlight, CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    try { await RefreshAsync(products[index].Id, token); }
                    catch (DiscoveryException ex) { problems[index] = new(products[index].Id, ex.Code, ex.Message); }
                    await reporting.WaitAsync(CancellationToken.None);
                    try
                    {
                        var done = ++completed;
                        if (progress is not null) await progress(new(done, products.Count));
                    }
                    finally { reporting.Release(); }
                });
        }
        // ForEachAsync reports cancellation even when the token was set after the last product finished.
        // Every body has retired here, so completed is final: if every product finished, the durable result wins.
        catch (OperationCanceledException) when (Volatile.Read(ref completed) == products.Count) { }
        // Every product has committed or failed, so a late cancellation does not hide durable results.
        // The first page reports what was saved, not an earlier window.
        var page = await catalog.ReadAllAsync(null, "all", 50, null, CancellationToken.None);
        return page with { RefreshProblems = problems.OfType<ProductRefreshProblem>().ToArray() };
    }
    public Task<DiscoveryCatalogPage> ReadAllAsync(string? product = null, string filter = "all", int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (product is not null) ProductRegistry.Validate(product);
        if (filter is not ("all" or "local" or "advertised") || limit is < 1 or > 100)
            throw new DiscoveryException("INVALID_REQUEST", "The discovery filter or page size is invalid.");
        return catalog.ReadAllAsync(product, filter, limit, cursor, cancellationToken);
    }
    public async Task<DiscoveryPage> RefreshAsync(string product, CancellationToken cancellationToken)
    {
        ProductRegistry.Validate(product);
        var generation = await catalog.BeginRefreshAsync(product, cancellationToken);
        var attempt = await source.FetchAsync(product, cancellationToken);
        return await catalog.CommitAsync(product, generation, attempt, clock.GetUtcNow().ToString("O"), cancellationToken);
    }
    public Task<DiscoveryPage> ReadAsync(string product, string filter = "all", int limit = 50, string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ProductRegistry.Validate(product);
        if (filter is not ("all" or "local" or "advertised") || limit is < 1 or > 100)
            throw new DiscoveryException("INVALID_REQUEST", "The discovery filter or page size is invalid.");
        return catalog.ReadAsync(product, filter, limit, cursor, cancellationToken);
    }
}
