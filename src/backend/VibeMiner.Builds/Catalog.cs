using System.Text.RegularExpressions;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary><see cref="SourceRegion"/>: the version-service host region a refresh asks.</summary>
public sealed record CatalogOptions(string SourceRegion = "us");

/// <summary>
/// The <see cref="ICatalog"/> over layer 2 and the SQLite catalog store. Every list first reads history entries the store
/// doesn't have yet. The first sync in a process reads the whole log; later ones resume after a seen entry at least
/// <see cref="ResumeLag"/> older than the newest, so an entry appended late with an older time is still picked up. An entry
/// whose reply can't be read is reported and retried by key on every list (up to <see cref="MaxFailed"/>; past that the
/// next sync reads the whole log again).
/// </summary>
public sealed partial class Catalog : ICatalog, IAsyncDisposable
{
    internal static readonly TimeSpan ResumeLag = TimeSpan.FromMinutes(10);
    internal const int MaxFailed = 1000;
    internal const int MaxListProblems = 50;

    private readonly IObjectSource source;
    private readonly CatalogStore store;
    private readonly CatalogOptions options;
    private readonly SemaphoreSlim sync = new(1, 1);
    private readonly Dictionary<ObjectKey, HistoryEntry> failed = [];
    private bool rescan = true;
    private HistoryEntry? resumeAfter;
    private volatile bool disposed;

    /// <summary>History entries read per page.</summary>
    internal int PageSize { get; set; } = 1000;

    private Catalog(IObjectSource source, CatalogStore store, CatalogOptions options)
    { this.source = source; this.store = store; this.options = options; }

    [GeneratedRegex("^[a-z]{2,8}$")]
    private static partial Regex RegionPattern();

    /// <summary>Opens the catalog store at <paramref name="path"/>. Reads nothing from the history log yet.</summary>
    public static async Task<Catalog> OpenAsync(IObjectSource source, string path, CatalogOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new();
        if (options.SourceRegion is null || !RegionPattern().IsMatch(options.SourceRegion))
            throw new ArgumentException("The source region must be 2-8 lowercase letters.", nameof(options));
        var store = await CatalogStore.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        return new Catalog(source, store, options);
    }

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
        var list = await ListAsync(product, cancellationToken).ConfigureAwait(false);
        return new CatalogRefresh(product, versions, cdns, list);
    }

    public async Task<SummaryRefresh> RefreshSummaryAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Summaries.Read(await ObserveAsync(ObservationKind.Summary, "", cancellationToken).ConfigureAwait(false));
    }

    private async Task<ObservationResult> ObserveAsync(ObservationKind kind, string product, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ObserveAsync(new(kind, product, options.SourceRegion), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ObservationResult(ObjectState.Unavailable, Code: "CATALOG_OBSERVE_FAILED",
                Message: $"Observing {kind} failed: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Validate(string product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (!Products.IsWow(product))
            throw new ArgumentException($"'{product}' isn't a WoW product id.", nameof(product));
    }

    private async Task<BuildList> ListAsync(string product, CancellationToken cancellationToken)
    {
        await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var transient = await SyncAsync(cancellationToken).ConfigureAwait(false);
            var list = await store.ListAsync(product, cancellationToken).ConfigureAwait(false);
            var mine = transient.Where(t => t.Product == product).Select(t => t.Problem).ToList();
            if (mine.Count == 0) return list;
            if (mine.Count > MaxListProblems)
            {
                var more = mine.Count - MaxListProblems;
                mine = [.. mine.Take(MaxListProblems), new CatalogProblem("CATALOG_PROBLEMS_TRUNCATED",
                    $"{more} more history entries can't be read now.")];
            }
            return list with { Problems = [.. list.Problems, .. mine] };
        }
        finally { sync.Release(); }
    }

    /// <summary>
    /// Adds the versions history entries the store doesn't have. Returns the entries that couldn't be read now.
    /// Runs under <see cref="sync"/>; state changes only when it finishes, so a cancelled sync is simply redone.
    /// </summary>
    private async Task<List<(string Product, CatalogProblem Problem)>> SyncAsync(CancellationToken cancellationToken)
    {
        var problems = new List<(string, CatalogProblem)>();
        var nowFailed = new Dictionary<ObjectKey, HistoryEntry>();
        var overflow = false;

        async Task ReadAsync(HistoryEntry entry)
        {
            if (await TryAddAsync(entry, cancellationToken).ConfigureAwait(false) is not { } problem) return;
            problems.Add((entry.Product, problem));
            if (nowFailed.Count < MaxFailed) nowFailed[entry.Record] = entry;
            else overflow = true;
        }

        // Entries that failed before are retried by key; the scan below skips them.
        foreach (var entry in failed.Values.ToList()) await ReadAsync(entry).ConfigureAwait(false);

        var after = rescan ? null : resumeAfter;
        var anchor = after;
        var recent = new Queue<HistoryEntry>();
        DateTimeOffset? newest = null;
        while (true)
        {
            var page = await source.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Versions, After: after, Limit: PageSize),
                cancellationToken).ConfigureAwait(false);
            foreach (var entry in page)
            {
                if (newest is null || entry.ObservedAt > newest) newest = entry.ObservedAt;
                recent.Enqueue(entry);
                // The log is oldest first: resume after the newest entry read that is at least ResumeLag older than the newest.
                while (recent.TryPeek(out var head) && head.ObservedAt <= newest - ResumeLag) anchor = recent.Dequeue();
                if (failed.ContainsKey(entry.Record) || nowFailed.ContainsKey(entry.Record)) continue;
                if (await store.HasAsync(entry.Record, cancellationToken).ConfigureAwait(false)) continue;
                await ReadAsync(entry).ConfigureAwait(false);
            }
            if (page.Count < PageSize) break;
            after = page[^1];
        }

        failed.Clear();
        foreach (var (key, entry) in nowFailed) failed[key] = entry;
        rescan = overflow;
        resumeAfter = overflow ? null : anchor;
        return problems;
    }

    /// <summary>Reads one entry's reply into the store. Returns why it couldn't, or null.</summary>
    private async Task<CatalogProblem?> TryAddAsync(HistoryEntry entry, CancellationToken cancellationToken)
    {
        var got = await source.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, FetchPriority.Interactive,
            cancellationToken).ConfigureAwait(false);
        if (got.IsPresent && got.Bytes is { } bytes)
        {
            await store.AddAsync(new ProjectedEntry(entry, VersionsReply.Read(entry, bytes)), cancellationToken).ConfigureAwait(false);
            return null;
        }
        var detail = $"{got.State}{(got.Code is null ? "" : ", " + got.Code)}";
        var (code, message) = got.State switch
        {
            ObjectState.Present => ("CATALOG_DOCUMENT_TOO_LARGE", $"The versions reply is too large to read ({detail})."),
            ObjectState.Refused => ("CATALOG_DOCUMENT_REFUSED", $"The versions reply was refused ({detail})."),
            ObjectState.Unsupported => ("CATALOG_DOCUMENT_UNSUPPORTED", $"The versions reply can't be read this way ({detail})."),
            ObjectState.StoreFailed => ("CATALOG_DOCUMENT_STORE_FAILED", $"The versions reply couldn't be stored ({detail})."),
            _ => ("CATALOG_DOCUMENT_UNAVAILABLE", $"The versions reply can't be read now ({detail})."),
        };
        return new CatalogProblem(code, message, entry.Record);
    }

    /// <summary>Waits for a running sync, then closes the store. Safe to call twice.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await sync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await store.DisposeAsync().ConfigureAwait(false);
        }
        finally { sync.Release(); }
    }
}
