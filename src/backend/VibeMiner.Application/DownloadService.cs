using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// One bounded page of a download ledger: the admitted manifest, or the measured ledger that stands for it (D066, D093).
/// <c>After</c> is an ordinal cursor; <c>From</c> starts the page at the first member whose encoding key is at least it.
/// </summary>
public sealed record DownloadReadRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string LedgerId, int Limit = 50, string? After = null, string? From = null);
/// <summary>Check the files a measured ledger adds to its admitted manifest, which Open's assessment never covered (D093).</summary>
public sealed record DownloadCheckRequest(string ProductId, string BuildIdentity, string SourceRegion, string LedgerId);
public interface IDownloadCatalog
{
    Task<DownloadPage> ReadDownloadsAsync(DownloadReadRequest request, CancellationToken token);
    Task<DownloadCheckResult> CheckDownloadsAsync(DownloadCheckRequest request, Func<DownloadCheckProgress, Task> progress, CancellationToken token);
}
public sealed class DownloadService(IDownloadCatalog catalog)
{
    public Task<DownloadPage> ReadAsync(DownloadReadRequest request, CancellationToken token = default)
    { Validate(request); return catalog.ReadDownloadsAsync(request, token); }
    public Task<DownloadCheckResult> CheckAsync(DownloadCheckRequest request, Func<DownloadCheckProgress, Task> progress, CancellationToken token = default)
    { ArgumentNullException.ThrowIfNull(progress); Validate(request); return catalog.CheckDownloadsAsync(request, progress, token); }
    public static void Validate(DownloadReadRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (!Hash(request.LedgerId, 64) ||
            request.Limit is < 1 or > 100 || request.After is not null &&
            (request.After.Length is < 1 or > 19 || request.After.Any(c => c is < '0' or > '9') ||
             request.After.Length > 1 && request.After[0] == '0' ||
             !long.TryParse(request.After, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
            request.From is not null && (request.After is not null || !Hash(request.From, 32)))
            throw new DiscoveryException("INVALID_REQUEST", "Choose an exact download ledger and a bounded object page.");
    }
    public static void Validate(DownloadCheckRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (!Hash(request.LedgerId, 64)) throw new DiscoveryException("INVALID_REQUEST", "Choose an exact measured download ledger.");
    }
    private static bool Hash(string? value, int length) =>
        value is not null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
