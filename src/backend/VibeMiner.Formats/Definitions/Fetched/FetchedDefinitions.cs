using System.Collections.Concurrent;
using System.Security.Cryptography;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Formats.Definitions;

/// <summary>A fetched definition's commit and when these exact bytes were first fetched (D081).</summary>
public sealed record FetchedDefinitionInfo(string Revision, DateTimeOffset FetchedAt);

/// <summary>A table definition fetched from WoWDBDefs and kept in the history log (D081). FetchedAt and Revision are those
/// of the first fetch of these exact bytes; CheckedAt is when WoWDBDefs was last asked about the table.</summary>
public sealed record FetchedDefinition(string TableId, string TableName, string Sha256, long ByteLength,
    string SourceUrl, string Revision, DateTimeOffset FetchedAt, DateTimeOffset CheckedAt, DbdDocument Document)
{
    public FetchedDefinitionInfo Info => new(Revision, FetchedAt);
}

/// <summary>The newest verified definition this engine has for a table, and why it isn't newer when a refresh failed.
/// Both are null only when nothing was ever fetched and the fetch failed: then Failure says why. Asked is true when this
/// call asked WoWDBDefs (successfully or not).</summary>
public sealed record FetchedDefinitionOutcome(FetchedDefinition? Definition, DefinitionException? Failure, bool Asked = false);

/// <summary>The last time WoWDBDefs was asked about a table (D113): the cached definition, which may not cover the build,
/// the failure code when that last attempt failed (kept only while the engine runs), and when opening the table's data
/// asks again by itself.</summary>
public sealed record DefinitionCheck(DateTimeOffset CheckedAt, FetchedDefinitionInfo? Fetched, string? Failure, DateTimeOffset NextCheckAfter);

/// <summary>
/// Newer WoWDBDefs table definitions (D081) over layer 2. A fetch is one <see cref="ObservationKind.Definition"/> observation,
/// which layer 2 records in the history log; the layer 1 client owns the transport rules (two HTTPS hosts, no redirects,
/// 1 MiB, the time budget, two fetches at once). The cache is that history: the newest entry for the table whose URL is the
/// raw file at a 40-hex commit and whose bytes (read from the store only, never the network) are 1 byte to 1 MiB and parse
/// with the bounded DBD reader. Anything else is skipped, so the next fetch replaces it. A table is checked again after the
/// refresh interval when its cache covers what's needed, otherwise after the minimum gap; failures are kept in memory only.
/// </summary>
public sealed class FetchedDefinitions
{
    public const int MaxDefinitionBytes = 1 << 20;
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(24);
    /// <summary>The shortest time between two upstream checks of one table when its cache doesn't cover a build.</summary>
    public static readonly TimeSpan DefaultMinimumGap = TimeSpan.FromMinutes(15);
    /// <summary>History entries read per page.</summary>
    internal const int HistoryPage = 1000;
    /// <summary>
    /// Pages read per table: 1,000,000 entries, about 28 years of checks one minimum gap apart. A history longer than that
    /// reads as no cache (its newest entries weren't seen), so the table is fetched again rather than served a stale one.
    /// </summary>
    internal const int MaxHistoryPages = 1000;
    /// <summary>Runs of one document kept while reading history; older ones are dropped (they only date the first fetch).</summary>
    internal const int MaxRuns = 4096;

    private readonly IObjectSource source;
    private readonly TimeProvider time;
    private readonly TimeSpan refresh, gap;
    private readonly int page, pages;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tables = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Known> loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, DefinitionException Failure)> failures = new(StringComparer.Ordinal);

    private sealed record Known(string TableName, FetchedDefinition? Definition);

    public FetchedDefinitions(IObjectSource source, TimeProvider? clock = null, TimeSpan? refreshInterval = null, TimeSpan? minimumGap = null)
        : this(source, clock, refreshInterval, minimumGap, HistoryPage, MaxHistoryPages) { }

    internal FetchedDefinitions(IObjectSource source, TimeProvider? clock, TimeSpan? refreshInterval, TimeSpan? minimumGap, int historyPage, int historyPages)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyPage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(historyPages, 1);
        this.source = source; time = clock ?? TimeProvider.System;
        refresh = refreshInterval ?? DefaultRefreshInterval; gap = minimumGap ?? DefaultMinimumGap;
        page = historyPage; pages = historyPages;
    }

    /// <summary>The verified cached definition, or null. Local only: never asks WoWDBDefs. History is read on first use;
    /// after that this instance's own checks keep it current.</summary>
    public async Task<FetchedDefinition?> CachedAsync(string tableId, string tableName, CancellationToken token)
    {
        Validate(tableId, tableName);
        if (loaded.TryGetValue(tableId, out var known) && known.TableName == tableName) return known.Definition;
        var definition = await LoadAsync(tableId, tableName, token).ConfigureAwait(false);
        // A check that finished while this read history is newer than what it read.
        var kept = loaded.AddOrUpdate(tableId, new Known(tableName, definition), (_, current) => current.TableName == tableName ? current : new(tableName, definition));
        return kept.Definition;
    }

    /// <summary>The cached definition while it covers what the caller needs and is younger than the refresh interval.
    /// A cached definition that doesn't cover it is checked again upstream, but at most once per minimum gap, so a new
    /// build opens as soon as WoWDBDefs lists it without asking GitHub on every attempt. A failed refresh keeps the
    /// cached definition and says why. Cancellation stops the fetch. A deliberate request (a first open or Retry)
    /// skips the wait after a failure, so a reconnected network works at once.</summary>
    public async Task<FetchedDefinitionOutcome> EnsureAsync(string tableId, string tableName, Func<DbdDocument, bool> covers,
        Func<string, Task> progress, CancellationToken token, bool deliberate = false)
    {
        Validate(tableId, tableName);
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(progress);
        var gate = tables.GetOrAdd(tableId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = await CachedAsync(tableId, tableName, token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            // After a failed attempt, re-checks wait the same minimum gap, so an unreachable GitHub costs one attempt per
            // gap rather than one per table load. A deliberate request asks again at once.
            if (!deliberate && failures.TryGetValue(tableId, out var last) && last.At <= now && now - last.At < gap) return new(cached, last.Failure);
            if (!Due(cached, covers, now)) return new(cached, null);
            await progress($"Checking WoWDBDefs for the newest {tableName} definition").ConfigureAwait(false);
            var result = await source.ObserveAsync(new(ObservationKind.Definition, tableId, ObservationRequest.DefinitionRegion, tableName), token)
                .ConfigureAwait(false);
            // A cancel during the fetch or the history write comes back as a result; it is the caller's, not a failed fetch.
            token.ThrowIfCancellationRequested();
            try
            {
                var fresh = Accept(tableId, tableName, cached, result, token);
                failures.TryRemove(tableId, out _);
                return new(fresh, null, true);
            }
            catch (DefinitionException failure)
            {
                failures[tableId] = (time.GetUtcNow(), failure);
                return new(loaded.TryGetValue(tableId, out var known) && known.TableName == tableName ? known.Definition : cached, failure, true);
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>True when <see cref="EnsureAsync"/> would ask WoWDBDefs now. Local only.</summary>
    public async Task<bool> RefreshDueAsync(string tableId, string tableName, Func<DbdDocument, bool> covers, CancellationToken token)
    {
        Validate(tableId, tableName);
        ArgumentNullException.ThrowIfNull(covers);
        var cached = await CachedAsync(tableId, tableName, token).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (failures.TryGetValue(tableId, out var last) && last.At <= now && now - last.At < gap) return false;
        return Due(cached, covers, now);
    }

    /// <summary>The outcome of the last time WoWDBDefs was asked about this table (#81), or null when it never was. Local
    /// only. The cached definition and its check time survive a restart; a failure is kept only while the engine runs,
    /// and a first open after a restart asks again anyway. NextCheckAfter is when opening the table's data asks again by
    /// itself: the gap after a failure (as re-checks wait; Retry table data asks at once), else the gap or, for a covering
    /// cache, the refresh interval after the cache's last check.</summary>
    public async Task<DefinitionCheck?> LastCheckAsync(string tableId, string tableName, Func<DbdDocument, bool> covers, CancellationToken token)
    {
        Validate(tableId, tableName);
        ArgumentNullException.ThrowIfNull(covers);
        var cached = await CachedAsync(tableId, tableName, token).ConfigureAwait(false);
        var failed = failures.TryGetValue(tableId, out var last);
        if (cached is null && !failed) return null;
        var now = time.GetUtcNow();
        // A success clears the failure, so a kept failure is always the newest attempt.
        var checkedAt = failed ? last.At : cached!.CheckedAt;
        var next = failed ? checkedAt + gap : cached!.CheckedAt > now ? now
            : cached.CheckedAt + (covers(cached.Document) && refresh > gap ? refresh : gap);
        return new(checkedAt, cached?.Info, failed ? last.Failure.Code : null, next);
    }

    // A cache that covers what's needed is asked about again after the refresh interval; one that doesn't, after the
    // minimum gap. A check time in the future (a clock moved back) is due.
    private bool Due(FetchedDefinition? cached, Func<DbdDocument, bool> covers, DateTimeOffset now)
    {
        if (cached is null || cached.CheckedAt > now) return true;
        var age = now - cached.CheckedAt;
        return !(age < refresh && covers(cached.Document) || age < gap);
    }

    // This instance's own observation: the same rules as an entry read from history, so a restart reads the same cache.
    private FetchedDefinition Accept(string tableId, string tableName, FetchedDefinition? cached, ObservationResult result, CancellationToken token)
    {
        if (result.State != ObjectState.Present || result.Observation is not { } observation) throw Failure(tableName, result);
        // A recorded entry is a check, counting or not: the cache's check time moves with it, as it does after a restart.
        void Checked()
        {
            if (result.Recorded && cached is not null && observation.ObservedAt > cached.CheckedAt)
                loaded[tableId] = new(tableName, cached with { CheckedAt = observation.ObservedAt });
        }
        var bytes = observation.Bytes;
        if (Revision(observation.Url.AbsoluteUri, tableName) is not { } revision)
        { Checked(); throw Failed(tableName, "The definition came from an address VibeMiner doesn’t allow."); }
        if (bytes.Length > MaxDefinitionBytes) { Checked(); throw TooLarge(tableName); }
        DbdDocument document;
        try { document = DbdDefinitionReader.Parse(bytes, token: token); }
        catch (Db2FormatException failure)
        { Checked(); throw Invalid(tableName, failure.Code); }
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var at = observation.ObservedAt;
        // Unchanged bytes keep their first revision and fetch time, so rows read with them stay current.
        var definition = cached is not null && cached.Sha256 == sha
            ? cached with { CheckedAt = at > cached.CheckedAt ? at : cached.CheckedAt }
            : new FetchedDefinition(tableId, tableName, sha, bytes.Length, observation.Url.AbsoluteUri, revision, at, at, document);
        loaded[tableId] = new(tableName, definition);
        return definition;
    }

    // One run is consecutive entries (oldest first) of one document whose URL names a commit, skipping entries that don't.
    private sealed class Run(ObjectKey document, HistoryEntry first, string revision)
    {
        public ObjectKey Document { get; } = document;
        public HistoryEntry First { get; } = first;
        public string Revision { get; } = revision;
    }

    // The newest counting entry is the cached definition; the oldest entry of its unbroken run of the same bytes dates it.
    // Entries that don't count (a wrong URL, bytes missing, too large or unreadable) neither count nor break a run.
    private async Task<FetchedDefinition?> LoadAsync(string tableId, string tableName, CancellationToken token)
    {
        var runs = new LinkedList<Run>();
        DateTimeOffset? newest = null;
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == pages) return null;
            var batch = await source.ReadHistoryAsync(new(Product: tableId, Region: ObservationRequest.DefinitionRegion, Kind: HistoryKind.Definition,
                After: after, Limit: page), token).ConfigureAwait(false);
            foreach (var entry in batch)
            {
                if (entry.Kind != HistoryKind.Definition || entry.Product != tableId || entry.Region != ObservationRequest.DefinitionRegion) continue;
                if (newest is null || entry.ObservedAt > newest) newest = entry.ObservedAt;
                if (Revision(entry.Url, tableName) is not { } revision) continue;
                if (runs.Last?.Value.Document == entry.Document) continue;
                runs.AddLast(new Run(entry.Document, entry, revision));
                if (runs.Count > MaxRuns) runs.RemoveFirst();
            }
            if (batch.Count < page) break;
            after = batch[^1];
        }
        var documents = new Dictionary<ObjectKey, (byte[] Bytes, DbdDocument Document)?>();
        async Task<(byte[] Bytes, DbdDocument Document)?> ReadAsync(ObjectKey key)
        {
            if (documents.TryGetValue(key, out var known)) return known;
            var result = await source.GetKeyAsync(new ObjectRequest(key), cdn: null, FetchPriority.Interactive, token).ConfigureAwait(false);
            (byte[], DbdDocument)? value = null;
            if (result.IsPresent && result.Bytes is { Length: >= 1 and <= MaxDefinitionBytes } bytes)
                try { value = (bytes, DbdDefinitionReader.Parse(bytes, token: token)); }
                catch (Db2FormatException) { }
            documents[key] = value;
            return value;
        }
        for (var node = runs.Last; node is not null; node = node.Previous)
        {
            if (await ReadAsync(node.Value.Document).ConfigureAwait(false) is not { } chosen) continue;
            var start = node.Value;
            for (var older = node.Previous; older is not null; older = older.Previous)
            {
                if (older.Value.Document == start.Document) { start = older.Value; continue; }
                if (await ReadAsync(older.Value.Document).ConfigureAwait(false) is not null) break;
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(chosen.Bytes));
            return new(tableId, tableName, sha, chosen.Bytes.Length, WoWDBDefsObservations.DefinitionUrl(start.Revision, tableName), start.Revision,
                start.First.ObservedAt, newest!.Value, chosen.Document);
        }
        return null;
    }

    // The commit an exact raw definition URL names, or null (layer 2 owns the URL rule).
    private static string? Revision(string url, string tableName) => WoWDBDefsObservations.DefinitionRevision(url, tableName);

    private static DefinitionException Failure(string table, ObservationResult result) => result.Code switch
    {
        "CDN_OFFLINE" => Offline(table),
        "CDN_NOT_FOUND" => NotFound(table),
        "CDN_TOO_LARGE" => TooLarge(table),
        // A transport failure (CDN_*) can only come from the layer 1 WoWDBDefs client, whose message is already the whole
        // sentence ("Fetching a newer … failed." and the detail). Anything else (the store, a refused request) is a detail.
        { } code when code.StartsWith("CDN_", StringComparison.Ordinal) && result.Message is { Length: > 0 } message =>
            new("DEFINITION_FETCH_FAILED", message, true),
        _ => Failed(table, result.Message ?? $"The fetch ended with {result.Code ?? result.State.ToString()}."),
    };

    private static void Validate(string tableId, string tableName)
    {
        if (tableId is not { Length: 8 } || !tableId.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            tableName is not { Length: > 0 and <= 128 } || tableName.Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("A pinned provider table identity and name are required.");
    }

    internal static DefinitionException Offline(string table) => new("DEFINITION_FETCH_OFFLINE",
        $"VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer {table} definition.", true);
    internal static DefinitionException Failed(string table, string detail) => new("DEFINITION_FETCH_FAILED",
        $"Fetching a newer {table} definition from WoWDBDefs failed. {detail}", true);
    internal static DefinitionException TooLarge(string table) => new("DEFINITION_FETCH_TOO_LARGE",
        $"The newest WoWDBDefs {table} definition is larger than VibeMiner allows (1 MiB), so it wasn’t used.");
    internal static DefinitionException Invalid(string table, string code) => new("DEFINITION_FETCH_INVALID",
        $"The newest WoWDBDefs {table} definition couldn’t be read safely ({code}), so it wasn’t used.");
    internal static DefinitionException NotFound(string table) => new("DEFINITION_FETCH_NOT_FOUND",
        $"WoWDBDefs on GitHub has no {table}.dbd file to fetch, so only the bundled {table} definition can be used.");
}
