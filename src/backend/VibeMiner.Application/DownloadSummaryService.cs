using VibeMiner.Domain;

namespace VibeMiner.Application;

public interface IDownloadSummaryStore
{
    // Local projection only: one bounded catalog operation per call. It never starts a transfer,
    // a presence check or the stored-coverage count, and never writes.
    Task<IReadOnlyList<BuildDownloadSummary>> ReadDownloadSummariesAsync(IReadOnlyList<BuildKey> builds, CancellationToken token);

    /// <summary>Opaque revision of everything that can change a figure that is not already moving (D077).</summary>
    string SummaryRevision { get; }

    /// <summary>The revision once it differs from <paramref name="after"/>, or the unchanged one after <paramref name="limit"/>. Never reads the catalog.</summary>
    Task<string> WaitForSummaryChangeAsync(string after, TimeSpan limit, CancellationToken token);
}

/// <summary>Stored figures for the builds a list shows, in request order (D065).</summary>
public sealed class DownloadSummaryService(IDownloadSummaryStore store, TimeSpan? waitLimit = null)
{
    public const int MaxBuilds = 100;
    /// <summary>A change wait answers within this long even when nothing changed, well inside the desktop's request timeout.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);
    private readonly TimeSpan wait = waitLimit ?? MaxWait;

    /// <summary>Taken before a summary read, so any change during or after the read makes a later wait return at once.</summary>
    public string Revision => store.SummaryRevision;

    /// <summary>Waits for a stored-figure change after <paramref name="after"/> (D077). Bounded and cancellable; never starts work.</summary>
    public Task<string> WaitAsync(string after, CancellationToken token = default)
    {
        if (!ValidRevision(after)) throw new DiscoveryException("INVALID_REQUEST", "Name the summary revision of the last read.");
        token.ThrowIfCancellationRequested();
        return store.WaitForSummaryChangeAsync(after, wait, token);
    }

    /// <summary>Sixteen lowercase hex digits, a hyphen and a canonical nonnegative decimal of at most 19 digits.</summary>
    public static bool ValidRevision(string? value) =>
        value is { Length: >= 18 and <= 36 } && value[16] == '-' && value.AsSpan(0, 16).IndexOfAnyExcept("0123456789abcdef") < 0 &&
        value.AsSpan(17) is var digits && digits.IndexOfAnyExcept("0123456789") < 0 && (digits.Length == 1 || digits[0] != '0') &&
        long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

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
