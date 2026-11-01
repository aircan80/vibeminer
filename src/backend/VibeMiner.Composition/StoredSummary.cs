using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Composition;

/// <summary>The newest summary recorded in the history log.</summary>
public static class StoredSummary
{
    /// <summary>History entries read per page.</summary>
    public const int Page = 1000;

    /// <summary>The newest summary in the history log and what it says, read through layer 2; null when none was recorded.</summary>
    public static async Task<(HistoryEntry Entry, SummaryRefresh Read)?> NewestSummaryAsync(IObjectSource objects, CancellationToken ct)
    {
        HistoryEntry? newest = null, after = null;
        while (true)
        {
            var page = await objects.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Summary, After: after, Limit: Page), ct).ConfigureAwait(false);
            foreach (var e in page) if (newest is null || e.ObservedAt >= newest.ObservedAt) newest = e;
            if (page.Count < Page) break;
            after = page[^1];
        }
        if (newest is null) return null;
        var got = await objects.GetKeyAsync(new ObjectRequest(newest.Document), cdn: null, FetchPriority.Interactive, ct).ConfigureAwait(false);
        var observed = got.IsPresent && got.Bytes is { } bytes
            ? new ObservationResult(ObjectState.Present, new Observation(ObservationKind.Summary, "", newest.Region, new Uri(newest.Url), newest.ObservedAt, bytes), Recorded: true)
            : new ObservationResult(got.State, Code: got.Code, Message: got.Message);
        return (newest, Summaries.Read(observed));
    }
}
