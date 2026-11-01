using System.Collections.ObjectModel;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds.Testing;

/// <summary>
/// The standard <see cref="ICatalog"/> test double for layer 4 and up: no SQLite, no files. Every list reads the versions
/// history through layer 2 and computes the list with the catalog store's rules. Like <see cref="Catalog"/>, a reply it has
/// read once is kept; one it can't read now is reported and tried again by the next list.
/// </summary>
public sealed class InMemoryCatalog(IObjectSource source, string sourceRegion = "us") : ICatalog, IAsyncDisposable
{
    private const int PageSize = 1000;
    private readonly Lock gate = new();
    private readonly Dictionary<ObjectKey, (HistoryEntry Entry, VersionsRead Read)> read = [];
    private volatile bool disposed;

    public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }

    public async Task<BuildList> ListBuildsAsync(string product, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(product);
        return await ListAsync(product, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CatalogRefresh> RefreshAsync(string product, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(product);
        var versions = await ObserveAsync(ObservationKind.Versions, product, cancellationToken).ConfigureAwait(false);
        var cdns = await ObserveAsync(ObservationKind.Cdns, product, cancellationToken).ConfigureAwait(false);
        return new(product, versions, cdns, await ListAsync(product, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SummaryRefresh> RefreshSummaryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Summaries.Read(await ObserveAsync(ObservationKind.Summary, "", cancellationToken).ConfigureAwait(false));
    }

    private async Task<ObservationResult> ObserveAsync(ObservationKind kind, string product, CancellationToken cancellationToken)
    {
        try { return await source.ObserveAsync(new(kind, product, sourceRegion), cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(ObjectState.Unavailable, Code: "CATALOG_OBSERVE_FAILED", Message: $"Observing {kind} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Why a reply can't be read, with the same codes as <see cref="Catalog"/>.</summary>
    private static CatalogProblem Unreadable(HistoryEntry entry, ObjectResult got)
    {
        var detail = $"{got.State}{(got.Code is null ? "" : ", " + got.Code)}";
        var (code, message) = got.State switch
        {
            ObjectState.Present => ("CATALOG_DOCUMENT_TOO_LARGE", $"The versions reply is too large to read ({detail})."),
            ObjectState.Refused => ("CATALOG_DOCUMENT_REFUSED", $"The versions reply was refused ({detail})."),
            ObjectState.StoreFailed => ("CATALOG_DOCUMENT_STORE_FAILED", $"The versions reply couldn't be stored ({detail})."),
            _ => ("CATALOG_DOCUMENT_UNAVAILABLE", $"The versions reply can't be read now ({detail})."),
        };
        return new(code, message, entry.Record);
    }

    private static void Validate(string product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!Products.IsKnown(product)) throw new ArgumentException($"'{product}' isn't a registered product.", nameof(product));
    }

    private async Task<BuildList> ListAsync(string product, CancellationToken cancellationToken)
    {
        var transient = new List<CatalogProblem>();
        HistoryEntry? after = null;
        while (true)
        {
            var page = await source.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Versions, After: after, Limit: PageSize),
                cancellationToken).ConfigureAwait(false);
            foreach (var entry in page)
            {
                lock (gate) if (read.ContainsKey(entry.Record)) continue;
                var got = await source.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, FetchPriority.Interactive,
                    cancellationToken).ConfigureAwait(false);
                if (got.IsPresent && got.Bytes is { } bytes) { lock (gate) read.TryAdd(entry.Record, (entry, VersionsReply.Read(entry, bytes))); }
                else if (entry.Product == product) transient.Add(Unreadable(entry, got));
            }
            if (page.Count < PageSize) break;
            after = page[^1];
        }
        if (transient.Count > Catalog.MaxListProblems)
            transient = [.. transient.Take(Catalog.MaxListProblems), new("CATALOG_PROBLEMS_TRUNCATED",
                $"{transient.Count - Catalog.MaxListProblems} more history entries can't be read now.")];

        List<(HistoryEntry Entry, VersionsRead Read)> mine;
        lock (gate) mine = [.. read.Values.Where(x => x.Entry.Product == product)];
        // Times as the store keeps them: whole milliseconds, UTC.
        static DateTimeOffset Ms(DateTimeOffset t) => DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());

        var current = mine.Where(x => x.Read.Readable).GroupBy(x => x.Entry.Region, StringComparer.Ordinal)
            .Select(g => g.OrderBy(x => x.Read.SequenceNumber is null).ThenByDescending(x => x.Read.SequenceNumber)
                .ThenByDescending(x => Ms(x.Entry.ObservedAt)).ThenByDescending(x => x.Entry.Record).First().Entry.Record)
            .ToHashSet();

        var builds = mine.SelectMany(x => x.Read.Sightings.Select(s => (x.Entry, Sighting: s)))
            .GroupBy(x => x.Sighting.Identity.Id, StringComparer.Ordinal)
            .Select(b =>
            {
                var regions = b.GroupBy(x => (x.Sighting.Region, x.Sighting.CdnConfig)).Select(g =>
                {
                    var last = g.OrderByDescending(x => Ms(x.Entry.ObservedAt)).ThenByDescending(x => x.Entry.Record).First().Entry;
                    return new BuildRegion(g.Key.Region, g.Key.CdnConfig, g.Min(x => Ms(x.Entry.ObservedAt)), Ms(last.ObservedAt),
                        g.Any(x => current.Contains(x.Entry.Record)), last.Record);
                }).OrderBy(r => r.Region, StringComparer.Ordinal).ThenBy(r => r.FirstSeen).ThenBy(r => r.CdnConfig, StringComparer.Ordinal).ToList();
                var f = new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(
                    b.First().Sighting.Identity.Fields.ToDictionary(), StringComparer.Ordinal));
                var productConfig = f.TryGetValue("ProductConfig", out var pc) && pc.Length != 0 ? pc : null;
                return new CatalogBuild(product, b.Key, f["VersionsName"], f["BuildConfig"], productConfig, f,
                    regions.Min(r => r.FirstSeen), regions.Max(r => r.LastSeen), regions.Any(r => r.Current), regions);
            }).OrderByDescending(b => b.FirstSeen).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();

        var problems = mine.OrderByDescending(x => Ms(x.Entry.ObservedAt)).ThenByDescending(x => x.Entry.Record)
            .SelectMany(x => x.Read.Problems.Select((p, i) => (Problem: p with { Record = x.Entry.Record }, Ordinal: i))
                .OrderBy(p => p.Problem.Row ?? -1).ThenBy(p => p.Ordinal).Select(p => p.Problem))
            .Take(CatalogStore.MaxProblems).Concat(transient).ToList();

        DateTimeOffset? lastObserved = mine.Count == 0 ? null : mine.Max(x => Ms(x.Entry.ObservedAt));
        return new BuildList(product, builds, problems, lastObserved);
    }
}
