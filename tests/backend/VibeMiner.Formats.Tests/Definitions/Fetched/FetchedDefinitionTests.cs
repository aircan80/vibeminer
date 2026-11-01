using System.Security.Cryptography;
using System.Text;
using VibeMiner.Formats.Definitions;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

/// <summary>
/// #55 / D081 / D113: newer WoWDBDefs definitions over layer 2, ported from the legacy FetchedDefinitionTests against the
/// in-memory object source. Each check is one recorded observation, so "requests" are counted as observe calls (one per
/// check, where the old fake GitHub counted two: commit, then file).
/// </summary>
public sealed class FetchedDefinitionTests
{
    private const string Revision = "1111111111111111111111111111111111111111";
    private const string Newer = "2222222222222222222222222222222222222222";
    private const string ItemTable = "50238ec2";
    private static readonly TimeSpan Gap = FetchedDefinitions.DefaultMinimumGap;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A synthetic Item-like definition with one block listing <paramref name="build"/>.</summary>
    private static byte[] ItemWith(string layout, string build) =>
        Encoding.UTF8.GetBytes($"COLUMNS\nint ID\nint Value\n\nLAYOUT {layout}\nBUILD {build}\nID<32>\nValue<32>\n");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Func<string, Task> None => _ => Task.CompletedTask;
    private static bool Any(DbdDocument _) => true;
    private static Func<DbdDocument, bool> Lists(string build) => document => document.ListsBuild(build);

    private static (InMemoryObjectSource Source, Clock Clock) Create()
    {
        var clock = new Clock();
        return (new InMemoryObjectSource { Time = clock }, clock);
    }

    private static int Asked(InMemoryObjectSource source) => source.Observed.Count(o => o.Kind == ObservationKind.Definition);
    private static ObservationRequest Request(string table = "Item") => new(ObservationKind.Definition, ItemTable, ObservationRequest.DefinitionRegion, table);

    [Fact]
    public async Task A_fetched_definition_is_verified_cached_and_reused_offline_after_a_restart()
    {
        var (source, clock) = Create();
        var bytes = ItemWith("9A2A4834", "77.0.0.100"); source.AddDefinition(ItemTable, "Item", Revision, bytes);
        var stages = new List<string>();
        var outcome = await new FetchedDefinitions(source, clock)
            .EnsureAsync(ItemTable, "Item", Any, stage => { stages.Add(stage); return Task.CompletedTask; }, default);
        Assert.Null(outcome.Failure); Assert.True(outcome.Asked);
        var fetched = outcome.Definition!;
        Assert.Equal(Revision, fetched.Revision);
        Assert.Equal(Sha(bytes), fetched.Sha256);
        Assert.Equal(bytes.Length, fetched.ByteLength);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{Revision}/definitions/Item.dbd", fetched.SourceUrl);
        Assert.Equal(clock.Now, fetched.FetchedAt); Assert.Equal(clock.Now, fetched.CheckedAt);
        Assert.Equal(1, Asked(source));
        Assert.Contains("Checking WoWDBDefs for the newest Item definition", stages);
        // The fetch is in the history log with its exact URL and bytes.
        var entry = Assert.Single(await source.ReadHistoryAsync(new(Product: ItemTable, Kind: HistoryKind.Definition), default));
        Assert.Equal(fetched.SourceUrl, entry.Url);
        // A restart offline: the cache from history answers, read locally, and nothing is asked.
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var restarted = new FetchedDefinitions(source, clock);
        var again = await restarted.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(again.Failure); Assert.False(again.Asked); Assert.Equal(fetched.Sha256, again.Definition!.Sha256); Assert.Equal(1, Asked(source));
        Assert.Equal(fetched with { Document = again.Definition.Document }, again.Definition);
        Assert.Equal(fetched.Revision, (await restarted.CachedAsync(ItemTable, "Item", default))!.Revision);
        Assert.All(source.Asked, asked => Assert.Equal(FetchPriority.Interactive, asked.Priority));
    }

    // Changed from the legacy test: a check of an unchanged commit downloads the file again, since each check is one
    // recorded observation; the unchanged bytes still keep their first fetch and revision.
    [Fact]
    public async Task A_refresh_waits_for_the_interval_and_keeps_unchanged_bytes_and_their_first_fetch()
    {
        var (source, clock) = Create();
        var bytes = ItemWith("9A2A4834", "77.0.0.100"); source.AddDefinition(ItemTable, "Item", Revision, bytes);
        var definitions = new FetchedDefinitions(source, clock);
        var first = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromHours(23);
        Assert.Same(first, (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition);
        Assert.Equal(1, Asked(source));
        clock.Now += TimeSpan.FromHours(2);
        var same = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        Assert.Equal(2, Asked(source));
        Assert.Equal(first.FetchedAt, same.FetchedAt); Assert.Equal(clock.Now, same.CheckedAt); Assert.Equal(first.Revision, same.Revision);
        // After a restart the history says the same.
        Assert.Equal(same with { Document = null! }, (await new FetchedDefinitions(source, clock).CachedAsync(ItemTable, "Item", default))! with { Document = null! });
        // A newer commit with new bytes replaces the cache.
        clock.Now += TimeSpan.FromHours(25);
        var newer = ItemWith("9A2A4834", "77.0.0.200"); source.AddDefinition(ItemTable, "Item", Newer, newer);
        var replaced = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        Assert.Equal(Newer, replaced.Revision); Assert.Equal(Sha(newer), replaced.Sha256); Assert.Equal(clock.Now, replaced.FetchedAt);
        var reread = (await new FetchedDefinitions(source, clock).CachedAsync(ItemTable, "Item", default))!;
        Assert.Equal(Newer, reread.Revision); Assert.Equal(clock.Now, reread.FetchedAt);
    }

    [Fact]
    public async Task Bytes_that_come_back_after_other_bytes_are_a_new_fetch_and_history_pages_are_read_to_the_end()
    {
        var (source, clock) = Create();
        var a = ItemWith("9A2A4834", "77.0.0.100"); var b = ItemWith("9A2A4834", "77.0.0.200");
        var definitions = new FetchedDefinitions(source, clock);
        foreach (var (revision, bytes) in new[] { (Revision, a), (Revision, a), (Newer, b), ("3333333333333333333333333333333333333333", a), ("4444444444444444444444444444444444444444", a) })
        {
            source.AddDefinition(ItemTable, "Item", revision, bytes);
            await definitions.EnsureAsync(ItemTable, "Item", Any, None, default, deliberate: true);
            clock.Now += TimeSpan.FromDays(2);
        }
        var start = new Clock().Now;
        var expected = new { Revision = "3333333333333333333333333333333333333333", FetchedAt = start + TimeSpan.FromDays(6), CheckedAt = start + TimeSpan.FromDays(8) };
        foreach (var reader in new[] { definitions, new FetchedDefinitions(source, clock, null, null, historyPage: 2, historyPages: 3) })
        {
            var cached = (await reader.CachedAsync(ItemTable, "Item", default))!;
            Assert.Equal(expected, new { cached.Revision, cached.FetchedAt, cached.CheckedAt }); Assert.Equal(Sha(a), cached.Sha256);
        }
        // Past its page bound, history reads as no cache rather than a stale one.
        Assert.Null(await new FetchedDefinitions(source, clock, null, null, historyPage: 2, historyPages: 2).CachedAsync(ItemTable, "Item", default));
    }

    [Fact]
    public async Task Offline_is_its_own_state_and_a_cached_definition_keeps_working()
    {
        var (source, clock) = Create();
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var definitions = new FetchedDefinitions(source, clock);
        var offline = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(offline.Definition);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Failure!.Code); Assert.True(offline.Failure.Retryable);
        Assert.Equal("VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer Item definition.", offline.Failure.Message);
        // A failed attempt waits the minimum gap before the next one, then nothing sticks.
        var asked = Asked(source);
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!.Code);
        Assert.Equal(asked, Asked(source));
        clock.Now += Gap;
        var cached = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromDays(3);
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var stale = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", stale.Failure!.Code); Assert.Equal(cached.Sha256, stale.Definition!.Sha256);
    }

    // The legacy test drove a connect timeout through HTTP; the layer 1 client now reports it as CDN_OFFLINE, which is what
    // this layer sees.
    [Fact]
    public async Task A_connect_timeout_is_offline_and_a_cached_definition_keeps_working()
    {
        var (source, clock) = Create();
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE",
            "VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer Item definition.");
        var definitions = new FetchedDefinitions(source, clock);
        var offline = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(offline.Definition); Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Failure!.Code);
        clock.Now += Gap; source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var cached = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromDays(2);
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var stale = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", stale.Failure!.Code); Assert.Equal(cached.Sha256, stale.Definition!.Sha256);
    }

    // Changed from the legacy test: the 1 MiB limit is the layer 1 client's (CDN_TOO_LARGE, nothing recorded); bytes over it
    // that reach the history anyway are refused here too and never count.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_definition_over_one_MiB_is_too_large_and_never_saved(bool client)
    {
        var (source, clock) = Create();
        if (client) source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Refused, "CDN_TOO_LARGE");
        else source.AddDefinition(ItemTable, "Item", Revision, Encoding.UTF8.GetBytes(new string('\n', FetchedDefinitions.MaxDefinitionBytes + 1)));
        var outcome = await new FetchedDefinitions(source, clock).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Definition); Assert.Equal("DEFINITION_FETCH_TOO_LARGE", outcome.Failure!.Code); Assert.False(outcome.Failure.Retryable);
        Assert.Equal("The newest WoWDBDefs Item definition is larger than VibeMiner allows (1 MiB), so it wasn’t used.", outcome.Failure.Message);
        Assert.Null(await new FetchedDefinitions(source, clock).CachedAsync(ItemTable, "Item", default));
    }

    // Changed from the legacy test: the refused bytes stay in the history log as observed, but never count as the cache,
    // and an earlier good definition stays the cache, also after a restart.
    [Theory]
    [InlineData("not a definition\n")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT 1234ABCD\nBUILD 1.2.3.4\nMissing<32>\n")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT 1234ABCD, 1234abcd\nBUILD 1.2.3.4\nID<32>\n")]
    public async Task A_definition_the_reader_refuses_is_a_parse_failure_and_never_saved(string text)
    {
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, Encoding.UTF8.GetBytes(text));
        var definitions = new FetchedDefinitions(source, clock);
        var outcome = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Definition); Assert.Equal("DEFINITION_FETCH_INVALID", outcome.Failure!.Code); Assert.False(outcome.Failure.Retryable);
        Assert.Matches(@"^The newest WoWDBDefs Item definition couldn’t be read safely \(DBD_[A-Z_]+\), so it wasn’t used\.$", outcome.Failure.Message);
        Assert.Null(await new FetchedDefinitions(source, clock).CachedAsync(ItemTable, "Item", default));
        // A good definition, then a bad one: the good one stays the cache, and the bad check moves its check time.
        clock.Now += Gap; var good = ItemWith("9A2A4834", "77.0.0.100"); source.AddDefinition(ItemTable, "Item", Newer, good);
        Assert.Equal(Sha(good), (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default, deliberate: true)).Definition!.Sha256);
        var fetchedAt = clock.Now;
        clock.Now += TimeSpan.FromDays(2); source.AddDefinition(ItemTable, "Item", Revision, Encoding.UTF8.GetBytes(text));
        var refused = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_INVALID", refused.Failure!.Code); Assert.Equal(Sha(good), refused.Definition!.Sha256);
        foreach (var reader in new[] { definitions, new FetchedDefinitions(source, clock) })
        {
            var cached = (await reader.CachedAsync(ItemTable, "Item", default))!;
            Assert.Equal((Newer, fetchedAt, clock.Now), (cached.Revision, cached.FetchedAt, cached.CheckedAt));
        }
        Assert.Equal(3, (await source.ReadHistoryAsync(new(Product: ItemTable, Kind: HistoryKind.Definition), default)).Count);
    }

    [Fact]
    public async Task A_table_WoWDBDefs_does_not_have_is_not_found()
    {
        var (source, clock) = Create();
        var definitions = new FetchedDefinitions(source, clock);
        var missing = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!;
        Assert.Equal("DEFINITION_FETCH_NOT_FOUND", missing.Code); Assert.False(missing.Retryable);
        Assert.Equal("WoWDBDefs on GitHub has no Item.dbd file to fetch, so only the bundled Item definition can be used.", missing.Message);
        // The scripted 404 from the client, as an empty commits list or a missing file answers.
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.NotFound, "CDN_NOT_FOUND");
        Assert.Equal("DEFINITION_FETCH_NOT_FOUND", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default, deliberate: true)).Failure!.Code);
    }

    // Redirects, rate limits, server errors, timeouts, broken connections, a store that can't take the bytes and a refused
    // request are all a failed fetch that may be retried, whatever layer 2 says about retrying.
    [Theory]
    [InlineData(ObjectState.Refused, "CDN_PROTOCOL", "Fetching a newer Item definition from WoWDBDefs failed. GitHub redirected the request, and VibeMiner doesn’t follow redirects.",
        "Fetching a newer Item definition from WoWDBDefs failed. GitHub redirected the request, and VibeMiner doesn’t follow redirects.")]
    [InlineData(ObjectState.Unavailable, "CDN_HTTP_STATUS", "Fetching a newer Item definition from WoWDBDefs failed. GitHub is limiting requests right now.",
        "Fetching a newer Item definition from WoWDBDefs failed. GitHub is limiting requests right now.")]
    [InlineData(ObjectState.Unavailable, "CDN_TIMEOUT", "Fetching a newer Item definition from WoWDBDefs failed. WoWDBDefs didn’t answer within 30 seconds.",
        "Fetching a newer Item definition from WoWDBDefs failed. WoWDBDefs didn’t answer within 30 seconds.")]
    [InlineData(ObjectState.Unavailable, "CDN_NETWORK", null, "Fetching a newer Item definition from WoWDBDefs failed. The fetch ended with CDN_NETWORK.")]
    [InlineData(ObjectState.StoreFailed, "OBJECT_STORE_DISK_FULL", "The disk is full.", "Fetching a newer Item definition from WoWDBDefs failed. The disk is full.")]
    [InlineData(ObjectState.Refused, "OBSERVATION_SOURCE_MISSING", "No source serves Definition.", "Fetching a newer Item definition from WoWDBDefs failed. No source serves Definition.")]
    public async Task Redirects_rate_limits_and_server_errors_fail_without_following_anything(ObjectState state, string code, string? message, string expected)
    {
        var (source, clock) = Create();
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, state, code, message);
        var outcome = await new FetchedDefinitions(source, clock).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_FAILED", outcome.Failure!.Code); Assert.True(outcome.Failure.Retryable);
        Assert.Equal(expected, outcome.Failure.Message);
        Assert.True(outcome.Asked); Assert.Equal(1, Asked(source));
    }

    [Fact]
    public async Task A_cancelled_check_stops_and_records_no_failure()
    {
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var definitions = new FetchedDefinitions(source, clock);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => definitions.EnsureAsync(ItemTable, "Item", Any, None, cancel.Token));
        Assert.Null(await definitions.LastCheckAsync(ItemTable, "Item", Any, default));
        Assert.NotNull((await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition);
    }

    /// <summary>Cancels the caller's token while the observation is recorded and answers as the real source does then.</summary>
    private sealed class CancelDuringWrite(InMemoryObjectSource inner, CancellationTokenSource cancel) : IObjectSource
    {
        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken token) =>
            inner.GetKeyAsync(request, cdn, priority, token);
        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken token) => inner.GetKeysAsync(requests, cdn, priority, progress, token);
        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken token) => inner.GetRangeAsync(request, offset, destination, cdn, priority, token);
        public async Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken token)
        {
            var result = await inner.ObserveAsync(request, token);
            await cancel.CancelAsync();
            return new(ObjectState.StoreFailed, result.Observation, false, "OBJECT_ACCESS_CANCELED", "The history write was canceled.");
        }
        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken token) => inner.CountPresentAsync(keys, token);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken token) => inner.HasKeysAsync(keys, token);
        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken token) => inner.ReadHistoryAsync(query, token);
    }

    [Fact]
    public async Task A_cancel_during_the_history_write_propagates_and_records_no_failure()
    {
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        using var cancel = new CancellationTokenSource();
        var definitions = new FetchedDefinitions(new CancelDuringWrite(source, cancel), clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => definitions.EnsureAsync(ItemTable, "Item", Any, None, cancel.Token));
        Assert.Null(await definitions.LastCheckAsync(ItemTable, "Item", Any, default));
        Assert.True(await definitions.RefreshDueAsync(ItemTable, "Item", Any, default));
    }

    // Adapted from the legacy damaged-file cases: the cache is the history, so the damage is an entry that doesn't count.
    [Theory]
    [InlineData("bytes")]
    [InlineData("missing")]
    [InlineData("url")]
    [InlineData("name")]
    [InlineData("invalid")]
    public async Task A_cache_that_does_not_verify_is_never_used_and_is_fetched_again(string damage)
    {
        var (source, clock) = Create();
        var good = ItemWith("9A2A4834", "77.0.0.100");
        if (damage is "bytes" or "missing")
        {
            source.AddDefinition(ItemTable, "Item", Revision, good);
            Assert.Equal(ObjectState.Present, (await source.ObserveAsync(Request(), default)).State);
            var entry = Assert.Single(await source.ReadHistoryAsync(new(Product: ItemTable, Kind: HistoryKind.Definition), default));
            if (damage == "bytes") source.Add(entry.Document, Encoding.UTF8.GetBytes("damaged\n"));
            else source.Unavailable(entry.Document, "OBJECT_ACCESS_NO_CDN_CONTEXT");
        }
        else
        {
            // A short commit, another table's file, or bytes the reader refuses.
            source.AddDefinition(ItemTable, damage == "name" ? "ItemSparse" : "Item", damage == "url" ? "1111111" : Revision,
                damage == "invalid" ? Encoding.UTF8.GetBytes("not a definition\n") : good);
            Assert.Equal(ObjectState.Present, (await source.ObserveAsync(Request(damage == "name" ? "ItemSparse" : "Item"), default)).State);
        }
        var restarted = new FetchedDefinitions(source, clock);
        Assert.Null(await restarted.CachedAsync(ItemTable, "Item", default));
        Assert.Null(await restarted.LastCheckAsync(ItemTable, "Item", Any, default));
        // Other bytes, so a damaged or unreadable document of the first fetch can't stand in for them.
        var next = ItemWith("9A2A4834", "77.0.0.300"); source.AddDefinition(ItemTable, "Item", Newer, next);
        var outcome = await restarted.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Failure); Assert.Equal(Sha(next), outcome.Definition!.Sha256); Assert.Equal(Newer, outcome.Definition.Revision);
        Assert.Equal(2, Asked(source));
        Assert.Equal(Newer, (await new FetchedDefinitions(source, clock).CachedAsync(ItemTable, "Item", default))!.Revision);
    }

    [Fact]
    public async Task A_fresh_cache_that_does_not_cover_the_build_is_asked_again_but_at_most_once_per_gap()
    {
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var definitions = new FetchedDefinitions(source, clock);
        Assert.Equal(Revision, (await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.100"), None, default)).Definition!.Revision);
        Assert.Equal(1, Asked(source));
        // Release day: WoWDBDefs adds the new build within the hour. The cache is fresh but doesn't list it.
        source.AddDefinition(ItemTable, "Item", Newer, ItemWith("9A2A4834", "77.0.0.200"));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.True(await definitions.RefreshDueAsync(ItemTable, "Item", Lists("77.0.0.200"), default) is false);
        Assert.Equal(Revision, (await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(1, Asked(source));
        clock.Now += Gap;
        Assert.True(await definitions.RefreshDueAsync(ItemTable, "Item", Lists("77.0.0.200"), default));
        Assert.Equal(Newer, (await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(2, Asked(source));
        // Covered and fresh: no request at all.
        Assert.Equal(Newer, (await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(2, Asked(source));
    }

    [Fact]
    public async Task Retry_after_the_network_comes_back_fetches_at_once_inside_the_wait()
    {
        var (source, clock) = Create();
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var definitions = new FetchedDefinitions(source, clock);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.100"), None, default, deliberate: true)).Failure!.Code);
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        clock.Now += TimeSpan.FromMinutes(1);
        // An automatic re-check waits the gap; a deliberate Retry asks at once.
        var waiting = await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.100"), None, default);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", waiting.Failure!.Code); Assert.False(waiting.Asked); Assert.Equal(1, Asked(source));
        Assert.False(await definitions.RefreshDueAsync(ItemTable, "Item", Lists("77.0.0.100"), default));
        var retried = await definitions.EnsureAsync(ItemTable, "Item", Lists("77.0.0.100"), None, default, deliberate: true);
        Assert.Null(retried.Failure); Assert.True(retried.Asked); Assert.Equal(Revision, retried.Definition!.Revision);
        Assert.Equal(2, Asked(source));
        Assert.Null((await definitions.LastCheckAsync(ItemTable, "Item", Lists("77.0.0.100"), default))!.Failure);
    }

    [Fact]
    public async Task After_a_fetch_that_does_not_cover_the_build_the_definition_says_what_it_found_also_after_a_restart()
    {
        // #81: the fetched definition lacks the build's line.
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "1.60.1.70450"));
        var definitions = new FetchedDefinitions(source, clock);
        var lists = Lists("77.0.0.100");
        Assert.Null(await definitions.LastCheckAsync(ItemTable, "Item", lists, default));
        var checkedAt = clock.Now;
        Assert.False((await definitions.EnsureAsync(ItemTable, "Item", lists, None, default)).Definition!.Document.ListsBuild("77.0.0.100"));
        var asked = Asked(source);
        clock.Now += TimeSpan.FromMinutes(1);
        var expected = new DefinitionCheck(checkedAt, new(Revision, checkedAt), null, checkedAt + Gap);
        Assert.Equal(expected, await definitions.LastCheckAsync(ItemTable, "Item", lists, default));
        // A relaunch reads the same outcome from the history, and opening again inside the gap doesn't ask.
        var restarted = new FetchedDefinitions(source, clock);
        Assert.Equal(expected, await restarted.LastCheckAsync(ItemTable, "Item", lists, default));
        Assert.False((await restarted.EnsureAsync(ItemTable, "Item", lists, None, default)).Asked);
        Assert.Equal(asked, Asked(source));
        // Once nextCheckAfter has passed, opening the data asks again and the check moves on.
        clock.Now = expected.NextCheckAfter;
        Assert.True((await restarted.EnsureAsync(ItemTable, "Item", lists, None, default)).Asked);
        Assert.True(Asked(source) > asked);
        foreach (var reader in new[] { restarted, new FetchedDefinitions(source, clock) })
        {
            var moved = (await reader.LastCheckAsync(ItemTable, "Item", lists, default))!;
            Assert.Equal(clock.Now, moved.CheckedAt); Assert.Equal(clock.Now + Gap, moved.NextCheckAfter);
            Assert.Equal(new FetchedDefinitionInfo(Revision, checkedAt), moved.Fetched);
        }
        // A covering cache is asked again after the refresh interval.
        Assert.Equal(clock.Now + FetchedDefinitions.DefaultRefreshInterval, (await restarted.LastCheckAsync(ItemTable, "Item", Any, default))!.NextCheckAfter);
    }

    [Fact]
    public async Task A_failed_check_shows_until_the_engine_restarts_and_names_when_opening_asks_again()
    {
        var (source, clock) = Create();
        source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var definitions = new FetchedDefinitions(source, clock);
        var at = clock.Now;
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!.Code);
        // Re-checks wait the gap after a failure; Retry asks at once.
        Assert.Equal(new DefinitionCheck(at, null, "DEFINITION_FETCH_OFFLINE", at + Gap), await definitions.LastCheckAsync(ItemTable, "Item", Any, default));
        // Failures aren't kept across a restart; the first open there asks again anyway.
        Assert.Null(await new FetchedDefinitions(source, clock).LastCheckAsync(ItemTable, "Item", Any, default));
    }

    [Fact]
    public async Task Checks_of_one_table_run_one_at_a_time()
    {
        var (source, clock) = Create();
        source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var definitions = new FetchedDefinitions(source, clock);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => definitions.EnsureAsync(ItemTable, "Item", Any, None, default))));
        Assert.Equal(1, Asked(source)); Assert.Equal(1, outcomes.Count(o => o.Asked));
        Assert.All(outcomes, o => Assert.Equal(Revision, o.Definition!.Revision));
    }

    [Theory]
    [InlineData("50238EC2", "Item")]
    [InlineData("50238ec", "Item")]
    [InlineData(ItemTable, "")]
    [InlineData(ItemTable, "Item/../x")]
    public async Task A_table_identity_outside_the_rules_is_refused_before_anything_is_read(string tableId, string tableName)
    {
        var (source, clock) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => new FetchedDefinitions(source, clock).EnsureAsync(tableId, tableName, Any, None, default));
        Assert.Empty(source.Observed);
    }
}
