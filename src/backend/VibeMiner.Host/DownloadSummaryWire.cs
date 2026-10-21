using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Host;

/// <summary>Exact decimal strings and explicit state names for stored download figures (D065).</summary>
internal static class DownloadSummaryWire
{
    internal static object Summary(BuildDownloadSummary value) => new
    {
        value.ProductId, value.BuildIdentity,
        state = value.State switch
        {
            BuildDownloadState.NotOpened => "notOpened", BuildDownloadState.NotMeasured => "notMeasured",
            BuildDownloadState.Measured => "measured", _ => throw Invalid()
        },
        download = value.Download is { } d ? new
        {
            d.LedgerId, d.Scope, objectCount = N(d.ObjectCount), totalEncodedBytes = N(d.TotalEncodedBytes),
            storedObjects = N(d.StoredObjects), storedBytes = N(d.StoredBytes),
            countState = d.CountState switch
            {
                StoredCountState.Counting => "counting", StoredCountState.Current => "current",
                StoredCountState.Unavailable => "unavailable", _ => throw Invalid()
            },
            countProblem = d.CountProblem, d.UpdatedAt,
            presence = new
            {
                d.Presence.State, d.Presence.AssessedAt, observedObjects = N(d.Presence.ObservedObjects),
                missingObjects = N(d.Presence.MissingObjects), corruptObjects = N(d.Presence.CorruptObjects)
            },
            d.DownloadAll, d.Running, d.SourceRegions, unresolvableRootContent = N(d.UnresolvableRootContent),
            scopeState = d.ScopeState is "measuring" or "measured" or "manifestOnly" ? d.ScopeState : throw Invalid(), scopeProblem = d.ScopeProblem,
            d.ChangesPending
        } : null
    };

    private static string N(long value) => value < 0 ? throw Invalid() : value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Invalid() => new("DOWNLOAD_SUMMARY_INTEGRITY", "A stored download figure is inconsistent.");
}
