using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>
/// CDN routes from the <c>cdns</c> replies in layer 1's history log (D140): the <see cref="MaxReplies"/> most recently observed
/// distinct replies, newest first by highest numeric <c>## seqn</c>, then latest observed, a reply without a numeric seqn after
/// every one with. A reply that can't be read is skipped.
/// </summary>
internal static class CdnsRoutes
{
    public const int MaxReplies = 32;
    private const int HistoryPage = 1000;

    /// <summary>The readable replies, newest first. Empty when the history log can't be read.</summary>
    public static async Task<IReadOnlyList<VersionsTable>> NewestFirstAsync(IObjectSource source, string product, CancellationToken ct)
    {
        var latest = new Dictionary<ObjectKey, HistoryEntry>();
        try
        {
            HistoryEntry? after = null;
            while (true)
            {
                var page = await source.ReadHistoryAsync(new(product, null, HistoryKind.Cdns, after, HistoryPage), ct).ConfigureAwait(false);
                foreach (var e in page)
                    if (!latest.TryGetValue(e.Document, out var seen) || seen.ObservedAt < e.ObservedAt) latest[e.Document] = e;
                if (latest.Count > 2 * MaxReplies) Trim(latest);
                if (page.Count < HistoryPage) break;
                after = page[^1];
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { return []; }
        Trim(latest);

        var replies = new List<(long? Seqn, DateTimeOffset ObservedAt, VersionsTable Table)>();
        foreach (var entry in latest.Values)
        {
            try
            {
                var reply = await source.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, ct).ConfigureAwait(false);
                if (reply is not { IsPresent: true, Bytes: { } bytes }) continue;
                var table = VersionsTable.Parse(bytes);
                replies.Add((long.TryParse(table.Sequence, out var seqn) ? seqn : null, entry.ObservedAt, table));
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return [.. replies.OrderBy(r => r.Seqn is null).ThenByDescending(r => r.Seqn).ThenByDescending(r => r.ObservedAt).Select(r => r.Table)];
    }

    /// <summary>
    /// The route of the newest reply whose row for <paramref name="region"/> is usable (has a path and at least one host
    /// <see cref="CdnRoute"/> admits). A null region takes the <c>us</c> row, then the first usable row, per reply. Null when
    /// no reply has one.
    /// </summary>
    public static CdnRoute? Route(IReadOnlyList<VersionsTable> newestFirst, string? region)
    {
        foreach (var table in newestFirst)
        {
            if (region is not null) { if (RouteOf(table, region) is { } route) return route; continue; }
            if (RouteOf(table, "us") is { } us) return us;
            foreach (var row in table.Rows)
                if (RouteOf(row) is { } any) return any;
        }
        return null;
    }

    private static CdnRoute? RouteOf(VersionsTable table, string region) =>
        table.Rows.FirstOrDefault(r => r.Fields.TryGetValue("Name", out var name) && name == region) is { } row ? RouteOf(row) : null;

    private static CdnRoute? RouteOf(TableRow row)
    {
        if (!row.Fields.TryGetValue("Path", out var path) || !row.Fields.TryGetValue("Hosts", out var hosts)) return null;
        try { return new CdnRoute(hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries), path); }
        catch (ArgumentException) { return null; }
    }

    private static void Trim(Dictionary<ObjectKey, HistoryEntry> latest)
    {
        foreach (var old in latest.Values.OrderByDescending(e => e.ObservedAt).Skip(MaxReplies).Select(e => e.Document).ToArray())
            latest.Remove(old);
    }
}

/// <summary>
/// The region listing a build opens in: <see cref="Region"/> is the listing, <see cref="Routed"/> whether its region has a usable
/// CDN route. Region is null only when the build lists no region; Routed false means none of its regions has a usable route,
/// so the build opens from stored metadata only.
/// </summary>
public sealed record BuildRegionChoice(BuildRegion? Region, bool Routed);

/// <summary>Picks the region a build opens in (D280).</summary>
public static class BuildRegions
{
    /// <summary>
    /// The first of the build's listings (current ones first, then <paramref name="preferred"/>, then by region name, then the
    /// latest seen) whose region has a usable route in the <c>cdns</c> replies of the history log, read as an open reads them
    /// (D140). With none usable, the first listing in that order, not routed. Reads through layer 2 only; never fetches.
    /// </summary>
    public static async Task<BuildRegionChoice> ChooseAsync(IObjectSource source, CatalogBuild build, string preferred = "us",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(build);
        var ordered = build.Regions.OrderBy(g => !g.Current).ThenBy(g => g.Region != preferred)
            .ThenBy(g => g.Region, StringComparer.Ordinal).ThenByDescending(g => g.LastSeen).ToList();
        if (ordered.Count == 0) return new(null, false);
        var replies = await CdnsRoutes.NewestFirstAsync(source, build.Product, cancellationToken).ConfigureAwait(false);
        foreach (var g in ordered)
            if (CdnsRoutes.Route(replies, g.Region) is not null) return new(g, true);
        return new(ordered[0], false);
    }
}
