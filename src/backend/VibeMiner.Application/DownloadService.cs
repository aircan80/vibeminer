using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record DownloadReadRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string LedgerId, int Limit = 50, string? After = null);
public interface IDownloadCatalog
{
    Task<DownloadPage> ReadDownloadsAsync(DownloadReadRequest request, CancellationToken token);
}
public sealed class DownloadService(IDownloadCatalog catalog)
{
    public Task<DownloadPage> ReadAsync(DownloadReadRequest request, CancellationToken token = default)
    { Validate(request); return catalog.ReadDownloadsAsync(request, token); }
    public static void Validate(DownloadReadRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.LedgerId.Length != 64 || request.LedgerId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            request.Limit is < 1 or > 100 || request.After is not null &&
            (request.After.Length is < 1 or > 19 || request.After.Any(c => c is < '0' or > '9') ||
             request.After.Length > 1 && request.After[0] == '0' ||
             !long.TryParse(request.After, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new DiscoveryException("INVALID_REQUEST", "Choose an exact download ledger and a bounded object page.");
    }
}
