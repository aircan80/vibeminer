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
}

public sealed class DiscoveryService(IDiscoverySource source, IDiscoveryCatalog catalog, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
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
