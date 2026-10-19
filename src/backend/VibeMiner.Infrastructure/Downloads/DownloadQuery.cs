using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed class DownloadQuery(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    AcquisitionResources? resources = null) : IDownloadCatalog
{
    private readonly DownloadLedger ledger = resources is null ? new(objects) : new(objects, resources, null);
    public async Task<DownloadPage> ReadDownloadsAsync(DownloadReadRequest request, CancellationToken token)
    {
        DownloadService.Validate(request);
        var saved = await catalog.ReadDownloadAdmissionAsync(request, token);
        var matched = await ledger.AssessAsync(saved.Artifact, saved.VerifiedLengths, token);
        if (matched.Count != saved.VerifiedKeys.Count)
            throw new DiscoveryException("DOWNLOAD_CATALOG_INTEGRITY", "The saved reuse assessment does not match this download manifest.");
        var rows = await ledger.ReadPageAsync(saved.Artifact,
            request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture), request.Limit + 1, token);
        var retained = new HashSet<string>(saved.VerifiedKeys, StringComparer.Ordinal);
        var entries = rows.Take(request.Limit).Select(row => new DownloadObjectEntry(row.Id.ToString(CultureInfo.InvariantCulture),
            row.EncodingKey, row.EncodedBytes.ToString(CultureInfo.InvariantCulture), retained.Contains(row.EncodingKey))).ToArray();
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.LedgerId, saved.AssessedAt, entries,
            saved.Artifact.ObjectCount.ToString(CultureInfo.InvariantCulture), rows.Count > request.Limit ? entries[^1].Id : null);
    }
}
