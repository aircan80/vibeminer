using VibeMiner.Domain;

namespace VibeMiner.Application;

public interface IDownloadSummaryStore
{
    // Local projection only: one bounded catalog operation per call. It never starts a transfer,
    // a presence check or the stored-coverage count, and never writes.
    Task<IReadOnlyList<BuildDownloadSummary>> ReadDownloadSummariesAsync(IReadOnlyList<BuildKey> builds, CancellationToken token);
}

/// <summary>Stored figures for the builds a list shows, in request order (D065).</summary>
public sealed class DownloadSummaryService(IDownloadSummaryStore store)
{
    public const int MaxBuilds = 100;

    public async Task<IReadOnlyList<BuildDownloadSummary>> ReadAsync(IReadOnlyList<BuildKey> builds, CancellationToken token = default)
    {
        Validate(builds);
        token.ThrowIfCancellationRequested();
        var summaries = await store.ReadDownloadSummariesAsync(builds, token);
        if (summaries.Count != builds.Count || summaries.Where((summary, i) => summary.ProductId != builds[i].ProductId || summary.BuildIdentity != builds[i].BuildIdentity).Any())
            throw new DiscoveryException("DOWNLOAD_SUMMARY_INTEGRITY", "Stored download figures did not match the requested builds.");
        return summaries;
    }

    public static void Validate(IReadOnlyList<BuildKey> builds)
    {
        if (builds is null || builds.Count is < 1 or > MaxBuilds || builds.Distinct().Count() != builds.Count)
            throw Invalid();
        foreach (var build in builds)
        {
            if (build?.ProductId is null || build.BuildIdentity is not { Length: 64 } identity ||
                identity.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw Invalid();
            ProductRegistry.Validate(build.ProductId);
        }
    }

    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose one to 100 distinct exact builds.");
}
