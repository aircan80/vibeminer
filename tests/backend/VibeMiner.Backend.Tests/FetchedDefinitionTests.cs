using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;
using R = VibeMiner.Backend.Tests.TableAdmissionRevisionTests;

namespace VibeMiner.Backend.Tests;

/// <summary>#55 / D081: newer WoWDBDefs definitions fetched at runtime, against a local fake GitHub.</summary>
public sealed class FetchedDefinitionTests
{
    internal const string Revision = "1111111111111111111111111111111111111111";
    internal const string Newer = "2222222222222222222222222222222222222222";
    private const string ItemTable = "50238ec2";
    private static readonly string ItemDbd = File.ReadAllText(RepositoryPaths.File(
        "src/backend/VibeMiner.Infrastructure/Tables/Resources/WoWDBDefs/b9a63096cdd99b26967f17f9515377ae62cadfd7/definitions/Item.dbd"));

    /// <summary>The bundled Item definition plus one block: the 9A2A4834 fields under another layout and build.</summary>
    internal static byte[] ItemWith(string layout, string build)
    {
        var lines = ItemDbd.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "LAYOUT 9A2A4834");
        var fields = lines.Skip(start).TakeWhile(line => line.Length > 0)
            .Where(line => !line.StartsWith("LAYOUT ", StringComparison.Ordinal) && !line.StartsWith("BUILD ", StringComparison.Ordinal) && !line.StartsWith("COMMENT ", StringComparison.Ordinal));
        return Encoding.UTF8.GetBytes(ItemDbd.TrimEnd('\n') + "\n\nLAYOUT " + layout + "\nBUILD " + build + "\n" + string.Join("\n", fields) + "\n");
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A local fake of the two GitHub hosts. Unrouted addresses answer 404.</summary>
    internal sealed class GitHub : HttpMessageHandler
    {
        internal readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new(StringComparer.Ordinal);
        internal readonly List<Uri> Requests = [];
        internal Exception? Failure;
        internal bool Hang;
        internal void Serve(string table, string revision, byte[] bytes)
        {
            Routes[WoWDBDefsDefinitions.CommitsUrl(table).AbsoluteUri] = () => Text($"[{{\"sha\":\"{revision}\",\"commit\":{{\"message\":\"authored\"}}}}]");
            Routes[WoWDBDefsDefinitions.DefinitionUrl(revision, table)] = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        internal static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme);
            Assert.Contains(uri.Host, new[] { WoWDBDefsDefinitions.ApiHost, WoWDBDefsDefinitions.RawHost });
            lock (Requests) Requests.Add(uri);
            if (Hang) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            return Routes.TryGetValue(uri.AbsoluteUri, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    internal sealed class Policy(bool enabled) : ITableDefinitionFetchPolicy
    {
        public bool Enabled = enabled;
        public Task<bool> FetchEnabledAsync(CancellationToken token) => Task.FromResult(Enabled);
    }

    private static Func<string, Task> None => _ => Task.CompletedTask;
    private static bool Any(DbdDocument _) => true;

    [Fact]
    public async Task A_fetched_definition_is_verified_cached_and_reused_offline_after_a_restart()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        var bytes = ItemWith("9A2A4834", "77.0.0.100"); github.Serve("Item", Revision, bytes);
        var stages = new List<string>();
        var outcome = await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock)
            .EnsureAsync(ItemTable, "Item", Any, stage => { stages.Add(stage); return Task.CompletedTask; }, default);
        Assert.Null(outcome.Failure);
        var fetched = outcome.Definition!;
        Assert.Equal(Revision, fetched.Revision);
        Assert.Equal(R.Sha(bytes), fetched.Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{Revision}/definitions/Item.dbd", fetched.SourceUrl);
        Assert.Equal(clock.Now, fetched.FetchedAt);
        Assert.Equal(2, github.Requests.Count);
        Assert.Contains("Checking WoWDBDefs for the newest Item definition", stages);
        var folder = Path.Combine(directory.Path, WoWDBDefsDefinitions.FolderName, "wowdbdefs");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, $"{ItemTable}-{fetched.Sha256}.dbd")));
        using (var record = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(folder, ItemTable + ".json"))))
        {
            Assert.Equal(fetched.SourceUrl, record.RootElement.GetProperty("sourceUrl").GetString());
            Assert.Equal(fetched.Sha256, record.RootElement.GetProperty("sha256").GetString());
            Assert.Equal("2026-10-23T12:00:00.0000000+00:00", record.RootElement.GetProperty("fetchedAt").GetString());
        }
        // A restart offline: the verified cache answers and nothing reaches the network.
        github.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline");
        var restarted = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock);
        var again = await restarted.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(again.Failure); Assert.Equal(fetched.Sha256, again.Definition!.Sha256); Assert.Equal(2, github.Requests.Count);
        Assert.Equal(fetched.Revision, (await restarted.CachedAsync(ItemTable, "Item", default))!.Revision);
    }

    [Fact]
    public async Task A_refresh_waits_for_the_interval_and_keeps_unchanged_bytes_and_their_first_fetch()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        var bytes = ItemWith("9A2A4834", "77.0.0.100"); github.Serve("Item", Revision, bytes);
        var definitions = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock);
        var first = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromHours(23);
        Assert.Same(first, (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition);
        Assert.Equal(2, github.Requests.Count);
        clock.Now += TimeSpan.FromHours(2);
        var same = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        Assert.Equal(3, github.Requests.Count);
        Assert.Equal(first.FetchedAt, same.FetchedAt); Assert.Equal(clock.Now, same.CheckedAt); Assert.Equal(first.Revision, same.Revision);
        // A newer commit with new bytes replaces the cache and its old file.
        clock.Now += TimeSpan.FromHours(25);
        var newer = ItemWith("9A2A4834", "77.0.0.200"); github.Serve("Item", Newer, newer);
        var replaced = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        Assert.Equal(Newer, replaced.Revision); Assert.Equal(R.Sha(newer), replaced.Sha256); Assert.Equal(clock.Now, replaced.FetchedAt);
        var folder = Path.Combine(directory.Path, WoWDBDefsDefinitions.FolderName, "wowdbdefs");
        Assert.Equal([$"{ItemTable}-{replaced.Sha256}.dbd"], Directory.GetFiles(folder, "*.dbd").Select(Path.GetFileName));
    }

    [Fact]
    public async Task Offline_is_its_own_state_and_a_cached_definition_keeps_working()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        github.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        var definitions = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock);
        var offline = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(offline.Definition);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Failure!.Code); Assert.True(offline.Failure.Retryable);
        // A failed attempt waits the minimum gap before the next one, then nothing sticks.
        var requests = github.Requests.Count;
        github.Failure = null; github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!.Code);
        Assert.Equal(requests, github.Requests.Count);
        clock.Now += WoWDBDefsDefinitions.DefaultMinimumGap; github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var cached = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromDays(3);
        github.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline");
        var stale = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", stale.Failure!.Code); Assert.Equal(cached.Sha256, stale.Definition!.Sha256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_definition_over_one_MiB_is_too_large_and_never_saved(bool declared)
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Serve("Item", Revision, []);
        var huge = new byte[WoWDBDefsDefinitions.MaxDefinitionBytes + 1];
        github.Routes[WoWDBDefsDefinitions.DefinitionUrl(Revision, "Item")] = () => new(HttpStatusCode.OK)
            { Content = declared ? new ByteArrayContent(huge) : new StreamContent(new MemoryStream(huge)) };
        var outcome = await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github)).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Definition); Assert.Equal("DEFINITION_FETCH_TOO_LARGE", outcome.Failure!.Code); Assert.False(outcome.Failure.Retryable);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, WoWDBDefsDefinitions.FolderName, "wowdbdefs")));
    }

    [Theory]
    [InlineData("not a definition\n")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT 1234ABCD\nBUILD 1.2.3.4\nMissing<32>\n")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT 1234ABCD, 1234abcd\nBUILD 1.2.3.4\nID<32>\n")]
    public async Task A_definition_the_reader_refuses_is_a_parse_failure_and_never_saved(string text)
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Serve("Item", Revision, Encoding.UTF8.GetBytes(text));
        var outcome = await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github)).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Definition); Assert.Equal("DEFINITION_FETCH_INVALID", outcome.Failure!.Code);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, WoWDBDefsDefinitions.FolderName, "wowdbdefs")));
    }

    [Fact]
    public async Task A_table_WoWDBDefs_does_not_have_is_not_found()
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Routes[WoWDBDefsDefinitions.CommitsUrl("Item").AbsoluteUri] = () => GitHub.Text("[]");
        var definitions = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github));
        Assert.Equal("DEFINITION_FETCH_NOT_FOUND", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!.Code);
        // The commit exists but the file doesn't.
        github.Routes[WoWDBDefsDefinitions.CommitsUrl("Item").AbsoluteUri] = () => GitHub.Text($"[{{\"sha\":\"{Revision}\"}}]");
        Assert.Equal("DEFINITION_FETCH_NOT_FOUND", (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Failure!.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Redirects_rate_limits_and_server_errors_fail_without_following_anything(HttpStatusCode status)
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        github.Routes[WoWDBDefsDefinitions.DefinitionUrl(Revision, "Item")] = () =>
        { var response = new HttpResponseMessage(status); response.Headers.Location = new Uri("https://example.invalid/Item.dbd"); return response; };
        var outcome = await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github)).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_FAILED", outcome.Failure!.Code); Assert.True(outcome.Failure.Retryable);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_fetch_stops_at_its_time_budget_and_a_cancellation_stops_it_at_once()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Hang = true };
        var slow = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), fetchBudget: TimeSpan.FromMilliseconds(100));
        var timedOut = await slow.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_FAILED", timedOut.Failure!.Code);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WoWDBDefsDefinitions(directory.Path, new HttpClient(github))
            .EnsureAsync(ItemTable, "Item", Any, None, cancel.Token));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("record")]
    [InlineData("url")]
    public async Task A_cache_that_does_not_verify_is_never_used_and_is_fetched_again(string damage)
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var fetched = (await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github)).EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        var folder = Path.Combine(directory.Path, WoWDBDefsDefinitions.FolderName, "wowdbdefs");
        var record = Path.Combine(folder, ItemTable + ".json");
        if (damage == "bytes") await File.AppendAllTextAsync(Path.Combine(folder, $"{ItemTable}-{fetched.Sha256}.dbd"), "\n");
        else if (damage == "record") await File.WriteAllTextAsync(record, "{");
        else await File.WriteAllTextAsync(record, (await File.ReadAllTextAsync(record)).Replace("raw.githubusercontent.com", "example.invalid"));
        var restarted = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github));
        Assert.Null(await restarted.CachedAsync(ItemTable, "Item", default));
        Assert.Equal(fetched.Sha256, (await restarted.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!.Sha256);
        Assert.Equal(4, github.Requests.Count);
    }

    [Fact]
    public async Task A_long_layout_line_keeps_every_layout_and_still_refuses_a_repeat()
    {
        // One LAYOUT line near the line bound: 1,600 distinct layouts. A pairwise repeat check would still pass,
        // but the reader must not compare every pair; a repeat later in the line is still refused.
        var layouts = Enumerable.Range(0, 16_000 / 10).Select(i => i.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var text = "COLUMNS\nint ID\n\nLAYOUT " + string.Join(", ", layouts) + "\nBUILD 1.2.3.4\nID<32>\n";
        Assert.Equal(layouts.Length, DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(text)).Blocks.Single().LayoutHashes.Count);
        var repeated = "COLUMNS\nint ID\n\nLAYOUT " + string.Join(", ", layouts.Append(layouts[0].ToLowerInvariant())) + "\nBUILD 1.2.3.4\nID<32>\n";
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(repeated))).Code);
    }

    [Fact]
    public async Task A_fresh_cache_that_does_not_cover_the_build_is_asked_again_but_at_most_once_per_gap()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var definitions = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock);
        var lists = (string build) => (Func<DbdDocument, bool>)(document => document.ListsBuild(build));
        Assert.Equal(Revision, (await definitions.EnsureAsync(ItemTable, "Item", lists("77.0.0.100"), None, default)).Definition!.Revision);
        Assert.Equal(2, github.Requests.Count);
        // Release day: WoWDBDefs adds the new build within the hour. The cache is fresh but doesn't list it.
        github.Serve("Item", Newer, ItemWith("9A2A4834", "77.0.0.200"));
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(Revision, (await definitions.EnsureAsync(ItemTable, "Item", lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(2, github.Requests.Count);
        clock.Now += WoWDBDefsDefinitions.DefaultMinimumGap;
        Assert.Equal(Newer, (await definitions.EnsureAsync(ItemTable, "Item", lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(4, github.Requests.Count);
        // Covered and fresh: no request at all.
        Assert.Equal(Newer, (await definitions.EnsureAsync(ItemTable, "Item", lists("77.0.0.200"), None, default)).Definition!.Revision);
        Assert.Equal(4, github.Requests.Count);
    }

    [Fact]
    public async Task A_connect_timeout_is_offline_and_a_cached_definition_keeps_working()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        github.Failure = new TaskCanceledException("connect", new TimeoutException("connect timeout"));
        var definitions = new WoWDBDefsDefinitions(directory.Path, new HttpClient(github), clock);
        var offline = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(offline.Definition); Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Failure!.Code);
        clock.Now += WoWDBDefsDefinitions.DefaultMinimumGap; github.Failure = null; github.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var cached = (await definitions.EnsureAsync(ItemTable, "Item", Any, None, default)).Definition!;
        clock.Now += TimeSpan.FromDays(2);
        github.Failure = new TaskCanceledException("connect", new TimeoutException("connect timeout"));
        var stale = await definitions.EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", stale.Failure!.Code); Assert.Equal(cached.Sha256, stale.Definition!.Sha256);
    }

    /// <summary>A definition inside every reader limit whose layout selection would serialize gigabytes.</summary>
    internal static byte[] Hostile(int blocks, int layoutsPerBlock)
    {
        var text = new StringBuilder("COLUMNS\n");
        for (var i = 0; i < 256; i++) text.Append("int F").Append(i).Append(" // ").Append('c', 1000).Append('\n');
        var next = 0;
        for (var b = 0; b < blocks; b++)
        {
            text.Append("\nLAYOUT ").Append(string.Join(", ", Enumerable.Range(0, layoutsPerBlock).Select(_ => (next++).ToString("X8", System.Globalization.CultureInfo.InvariantCulture))));
            text.Append("\nBUILD 77.0.0.").Append(b + 1).Append('\n');
            for (var i = 0; i < 256; i++) text.Append('F').Append(i).Append("<32>\n");
        }
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    [Theory]
    [InlineData(30, 1600)]
    [InlineData(5, 1600)]
    public async Task A_definition_whose_layout_selection_would_explode_is_refused_as_invalid(int blocks, int layouts)
    {
        var bytes = Hostile(blocks, layouts);
        Assert.True(bytes.Length <= WoWDBDefsDefinitions.MaxDefinitionBytes);
        Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() => DbdDefinitionReader.Parse(bytes)).Code);
        using var directory = new TempDirectory(); var github = new GitHub();
        github.Serve("Item", Revision, bytes);
        var outcome = await new WoWDBDefsDefinitions(directory.Path, new HttpClient(github)).EnsureAsync(ItemTable, "Item", Any, None, default);
        Assert.Null(outcome.Definition); Assert.Equal("DEFINITION_FETCH_INVALID", outcome.Failure!.Code);
    }

    [Fact]
    public async Task Every_bundled_definition_fits_the_layout_and_selection_bounds()
    {
        var provider = await TablesCatalog.Provider.Value;
        Assert.Equal(12, provider.Definitions.Count);
        Assert.NotNull(DbdDefinitionReader.Parse(ItemWith("9A2A4834", "77.0.0.100")));
    }

    // ---- Table data with fetched definitions, end to end over the authored CDN and fake GitHub ----

    [Fact]
    public async Task Exact_build_first_a_fetched_definition_that_lists_the_build_beats_a_bundled_layout_match()
    {
        // D080 would open 1.60.1.70500 by layout with bundled; WoWDBDefs lists it exactly, so that wins.
        await using var h = await Tables.Create("1.60.1.70500");
        var fake = ItemWith("9A2A4834", "1.60.1.70500"); h.GitHub.Serve("Item", Revision, fake);
        var snapshot = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Null(snapshot.SchemaMatch); Assert.Equal(R.Sha(fake), snapshot.DefinitionSha256); Assert.Equal(Revision, snapshot.FetchedDefinition!.Revision);
        Assert.Null((await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).Problem);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("noBuild")]
    public async Task Without_an_exact_fetched_definition_the_bundled_layout_match_still_opens(string kind)
    {
        await using var h = await Tables.Create("1.60.1.70500");
        if (kind == "offline") h.GitHub.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline");
        else h.GitHub.Serve("Item", Revision, Encoding.UTF8.GetBytes(ItemDbd));
        var snapshot = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(TableSchemaMatch.LayoutOnly, snapshot.SchemaMatch); Assert.Null(snapshot.FetchedDefinition);
        Assert.Equal(BundledTableDefinitions.Revision, snapshot.ProviderRevision);
    }

    [Fact]
    public async Task A_table_opened_by_layout_upgrades_to_a_fetched_exact_definition_once_upstream_lists_the_build()
    {
        await using var h = await Tables.Create("1.60.1.70500");
        h.GitHub.Serve("Item", Revision, Encoding.UTF8.GetBytes(ItemDbd));
        var first = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(TableSchemaMatch.LayoutOnly, first.SchemaMatch); Assert.Null(first.FetchedDefinition);
        var github = h.GitHub.Requests.Count; var cdn = h.Cdn.Requests;
        // Within the gap the rows stay current and nothing is asked.
        h.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(first.ArtifactId, (await h.Store.ReadAsync(h.Request, default)).Snapshot!.ArtifactId);
        Assert.Equal(first.ArtifactId, (await h.Store.PrepareAsync(h.Request, None, default)).ArtifactId);
        Assert.Equal(github, h.GitHub.Requests.Count);
        // WoWDBDefs now lists the build. After the gap Data prepares again and the stored file reads exactly.
        var exact = ItemWith("9A2A4834", "1.60.1.70500"); h.GitHub.Serve("Item", Newer, exact);
        h.Clock.Now += WoWDBDefsDefinitions.DefaultMinimumGap;
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        var upgraded = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Null(upgraded.SchemaMatch); Assert.Equal(Newer, upgraded.FetchedDefinition!.Revision); Assert.Equal(R.Sha(exact), upgraded.DefinitionSha256);
        Assert.Equal(cdn, h.Cdn.Requests);
        Assert.Equal(upgraded.ArtifactId, (await h.Store.ReadAsync(h.Request, default)).Snapshot!.ArtifactId);
    }

    [Fact]
    public async Task While_GitHub_is_unreachable_a_table_asks_once_per_gap_and_still_opens_by_layout()
    {
        await using var h = await Tables.Create("1.60.1.70500");
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        Assert.Equal(TableSchemaMatch.LayoutOnly, (await h.Store.PrepareAsync(h.Request, None, default)).SchemaMatch);
        var attempts = h.GitHub.Requests.Count;
        Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.NotNull(await h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal(attempts, h.GitHub.Requests.Count);
        h.Clock.Now += WoWDBDefsDefinitions.DefaultMinimumGap;
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Equal(TableSchemaMatch.LayoutOnly, (await h.Store.PrepareAsync(h.Request, None, default)).SchemaMatch);
        Assert.True(h.GitHub.Requests.Count > attempts);
    }

    [Fact]
    public async Task Retry_after_the_network_comes_back_fetches_at_once_inside_the_wait()
    {
        // No bundled layout opens 77.x, so only a fetched definition can. The failed first open must not lock Retry out.
        await using var h = await Tables.Create("77.0.0.100");
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        var offline = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Code); Assert.Contains("use Retry table data", offline.Message);
        h.GitHub.Failure = null; h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        h.Clock.Now += TimeSpan.FromMinutes(1);
        var retried = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(Revision, retried.FetchedDefinition!.Revision);
        Assert.Equal("1", Assert.Single((await h.Store.ReadRowsAsync(h.Rows(retried.ArtifactId), default)).Rows).Id);
    }

    [Fact]
    public async Task With_the_switch_off_an_unknown_layout_on_an_unlisted_build_says_fetching_is_off()
    {
        await using var h = await Tables.Create("1.60.1.70500", 0x1234abcd, enabled: false);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal("DB2_SCHEMA_MISSING", failure.Code); Assert.False(failure.Retryable);
        Assert.Contains("layout 1234ABCD", failure.Message);
        Assert.Contains("Fetching newer table definitions from WoWDBDefs is off in Settings", failure.Message);
        Assert.Empty(h.GitHub.Requests);
    }

    [Theory]
    [InlineData("unlisted")]
    [InlineData("listed")]
    public void Escape_heavy_comments_cannot_make_layout_selection_expensive(string shape)
    {
        // Every '<' costs 6 bytes once escaped. Column comments are serialized with each field's own comment too.
        var text = new StringBuilder("COLUMNS\n");
        for (var i = 0; i < 256; i++) text.Append("int C").Append(i).Append(" // ").Append('<', 2000).Append('\n');
        if (shape == "unlisted")
        {
            text.Append("\nLAYOUT ").Append(string.Join(", ", Enumerable.Range(0, 28).Select(i => i.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)))).Append("\nBUILD 12.0.0.1000\n");
            for (var i = 0; i < 256; i++) text.Append('C').Append(i).Append("<32>\n");
        }
        else for (var b = 0; b < 250; b++)
        {
            text.Append("\nBUILD 12.0.0.5000\n");
            for (var i = 0; i < 30; i++) text.Append('C').Append(i).Append("<32>\n");
        }
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        Assert.True(bytes.Length <= WoWDBDefsDefinitions.MaxDefinitionBytes);
        Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() => DbdDefinitionReader.Parse(bytes)).Code);
    }

    [Fact]
    public async Task Each_selection_key_stays_within_the_parsed_bound()
    {
        // The bound holds for the real definitions too: the key's JSON never exceeds what the reader allowed.
        foreach (var (_, definition) in (await TablesCatalog.Provider.Value).Definitions)
        {
            Assert.InRange(definition.Document.SelectionCost, 1, new DbdLimits().MaxSelectionBytes / 3);
            foreach (var block in definition.Document.Blocks)
                foreach (var field in block.Fields)
                    Assert.True(JsonSerializer.SerializeToUtf8Bytes(field, Json).Length <= DbdDefinitionReader.SelectionBytes(field));
        }
    }

    [Fact]
    public async Task Feature_prerequisites_use_bundled_definitions_only_and_never_contact_GitHub()
    {
        await using var h = await Tables.Create("77.0.0.100");
        h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Feature, None, default))).Code);
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Feature, default))).Code);
        Assert.Empty(h.GitHub.Requests);
        // Rows Tables read with a fetched definition are never handed to a feature.
        Assert.NotNull((await h.Store.PrepareAsync(h.Request, None, default)).FetchedDefinition);
        var github = h.GitHub.Requests.Count;
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Feature, default));
        Assert.Equal(github, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task A_feature_prerequisite_on_a_listed_build_with_an_unknown_layout_fails_without_fetching()
    {
        await using var h = await Tables.Create("1.60.1.70450", 0x1234abcd);
        h.GitHub.Serve("Item", Revision, ItemWith("1234ABCD", "1.60.1.70450"));
        Assert.Equal("DB2_SCHEMA_MISSING", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Feature, None, default))).Code);
        Assert.Empty(h.GitHub.Requests);
        Assert.NotNull((await h.Store.PrepareAsync(h.Request, None, default)).FetchedDefinition);
        Assert.Null((await h.Store.ReadAsync(h.Feature, default)).Snapshot);
        Assert.Equal(2, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task A_build_line_the_bundled_definition_lacks_opens_with_a_fetched_definition_and_says_so()
    {
        await using var h = await Tables.Create("77.0.0.100");
        var fake = ItemWith("9A2A4834", "77.0.0.100"); h.GitHub.Serve("Item", Revision, fake);
        var before = await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default);
        Assert.Null(before.Definition!.Fetched); Assert.Equal("on", before.DefinitionFetch);
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", before.Problem!.Code); Assert.Contains("fetches the newest WoWDBDefs definition", before.Problem.Message);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Empty(h.GitHub.Requests);
        var stages = new List<string>();
        var snapshot = await h.Store.PrepareAsync(h.Request, stage => { stages.Add(stage); return Task.CompletedTask; }, default);
        Assert.Equal(new TableDefinitionFetched(Revision, h.Clock.Now), snapshot.FetchedDefinition);
        Assert.Equal(Revision, snapshot.ProviderRevision); Assert.Equal(R.Sha(fake), snapshot.DefinitionSha256);
        Assert.Null(snapshot.SchemaMatch); Assert.Equal(TableDataStore.ProjectionDecoderRevision, snapshot.DecoderRevision);
        Assert.Contains("\"fetchedDefinition\":{\"revision\":\"" + Revision + "\"", JsonSerializer.Serialize(snapshot, Json));
        Assert.True(stages.IndexOf("Checking WoWDBDefs for the newest Item definition") < stages.IndexOf("Resolving selected table dependencies"));
        Assert.Equal("1", Assert.Single((await h.Store.ReadRowsAsync(h.Rows(snapshot.ArtifactId), default)).Rows).Id);
        // Structure now shows the fetched definition the rows were read with.
        var after = await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default);
        Assert.Equal(new TableDefinitionFetched(Revision, h.Clock.Now), after.Definition!.Fetched);
        Assert.Equal(R.Sha(fake), after.Definition.Sha256); Assert.Null(after.Problem);
        Assert.Equal("9A2A4834", Assert.Single(after.Definition.Candidates).LayoutHash);
        // A restart offline reads the same rows from the cache, with no GitHub or CDN traffic.
        var github = h.GitHub.Requests.Count; var cdn = h.Cdn.Requests;
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline"); h.Cdn.Offline = true;
        await using var restarted = h.Restart();
        Assert.Equal(snapshot.ArtifactId, (await restarted.ReadAsync(h.Request, default)).Snapshot!.ArtifactId);
        Assert.Equal(snapshot.ArtifactId, (await restarted.PrepareAsync(h.Request, None, default)).ArtifactId);
        Assert.Equal(github, h.GitHub.Requests.Count); Assert.Equal(cdn, h.Cdn.Requests);
    }

    [Fact]
    public async Task Bundled_wins_when_it_covers_the_file_and_GitHub_is_never_asked()
    {
        await using var h = await Tables.Create("1.60.1.70450");
        h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "1.60.1.70450"));
        var snapshot = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Null(snapshot.FetchedDefinition); Assert.Equal(BundledTableDefinitions.Revision, snapshot.ProviderRevision);
        Assert.Null((await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).Definition!.Fetched);
        Assert.Empty(h.GitHub.Requests);
    }

    [Fact]
    public async Task A_layout_the_bundled_definition_lacks_reads_with_a_fetched_block_for_that_layout()
    {
        await using var h = await Tables.Create("1.60.1.70450", 0x1234abcd);
        var fake = ItemWith("1234ABCD", "1.60.1.70450"); h.GitHub.Serve("Item", Revision, fake);
        var before = await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default);
        Assert.Null(before.Definition!.Fetched); Assert.Null(before.Problem);
        var snapshot = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal("1234ABCD", snapshot.LayoutHash); Assert.Equal(Revision, snapshot.FetchedDefinition!.Revision);
        Assert.Equal(R.Sha(fake), (await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).Definition!.Sha256);
        // Turned off, the rows read with a fetched definition are no longer current, and the table can't open by bundled.
        h.Policy.Enabled = false;
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        var definition = await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default);
        Assert.Null(definition.Definition!.Fetched); Assert.Equal("off", definition.DefinitionFetch);
        var requests = h.GitHub.Requests.Count;
        Assert.Equal("DB2_SCHEMA_MISSING", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default))).Code);
        Assert.Equal(requests, h.GitHub.Requests.Count);
    }

    [Fact]
    public async Task With_the_switch_off_an_uncovered_line_fails_before_any_download_or_fetch()
    {
        await using var h = await Tables.Create("77.0.0.100", enabled: false);
        h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default))).Code);
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Request, default))).Code);
        Assert.Equal("off", (await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).DefinitionFetch);
        Assert.Empty(h.GitHub.Requests); Assert.Equal(h.OpenedRequests, h.Cdn.Requests);
    }

    [Theory]
    [InlineData("offline", "DEFINITION_FETCH_OFFLINE")]
    [InlineData("missing", "DEFINITION_FETCH_NOT_FOUND")]
    [InlineData("large", "DEFINITION_FETCH_TOO_LARGE")]
    [InlineData("malformed", "DEFINITION_FETCH_INVALID")]
    [InlineData("noLine", "SCHEMA_BUILD_NOT_LISTED")]
    public async Task Each_fetch_failure_is_its_own_state_and_nothing_is_downloaded(string kind, string code)
    {
        await using var h = await Tables.Create("77.0.0.100");
        if (kind == "offline") h.GitHub.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline");
        if (kind == "large") h.GitHub.Serve("Item", Revision, new byte[WoWDBDefsDefinitions.MaxDefinitionBytes + 1]);
        if (kind == "malformed") h.GitHub.Serve("Item", Revision, "COLUMNS\n"u8.ToArray());
        if (kind == "noLine") h.GitHub.Serve("Item", Revision, Encoding.UTF8.GetBytes(ItemDbd));
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal(code, failure.Code);
        if (kind == "noLine") Assert.Contains("Neither the pinned definition nor the WoWDBDefs definition fetched 2026-10-23 (1111111)", failure.Message);
        Assert.Contains("Tables the bundled definitions cover still open.", failure.Message);
        if (kind == "offline") Assert.Contains("turn off Fetch newer table definitions in Settings", failure.Message);
        if (kind == "missing") Assert.Contains("only the bundled Item definition can be used", failure.Message);
        Assert.Equal(h.OpenedRequests, h.Cdn.Requests);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
    }

    [Fact]
    public async Task A_failed_fetch_for_an_uncovered_layout_names_the_layout_and_keeps_its_state()
    {
        await using var h = await Tables.Create("1.60.1.70450", 0x1234abcd);
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal("DEFINITION_FETCH_OFFLINE", failure.Code); Assert.True(failure.Retryable);
        Assert.Contains("layout 1234ABCD", failure.Message);
        // Back online a minute later, Retry asks at once; the fetched definition has no block for this layout either.
        h.Clock.Now += TimeSpan.FromMinutes(1); h.GitHub.Failure = null; h.GitHub.Serve("Item", Revision, ItemWith("ABCD1234", "1.60.1.70450"));
        var missing = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default));
        Assert.Equal("DB2_SCHEMA_MISSING", missing.Code); Assert.Contains("layout 1234ABCD", missing.Message);
        Assert.Contains("WoWDBDefs definition fetched", missing.Message);
    }

    [Fact]
    public async Task Switching_fetching_off_and_on_reaches_a_table_that_was_already_open()
    {
        await using var h = await Tables.Create("1.60.1.70500");
        h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "1.60.1.70500"));
        var fetched = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(TableFetchedBecause.ExactBuild, fetched.FetchedBecause);
        // Off: the stored fetched rows are no longer current, and the next open reads with bundled by layout.
        h.Policy.Enabled = false;
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Null((await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).Definition!.Fetched);
        var requests = h.GitHub.Requests.Count;
        var bundled = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Null(bundled.FetchedDefinition); Assert.Null(bundled.FetchedBecause); Assert.Equal(TableSchemaMatch.LayoutOnly, bundled.SchemaMatch);
        Assert.Equal(requests, h.GitHub.Requests.Count);
        // On again: the exact fetched definition in the cache wins at the next open, without asking GitHub.
        h.Policy.Enabled = true;
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.NotNull((await h.Catalog.ReadDefinitionAsync(h.DefinitionRequest, default)).Definition!.Fetched);
        var again = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(fetched.ArtifactId, again.ArtifactId); Assert.Equal(TableFetchedBecause.ExactBuild, again.FetchedBecause);
        Assert.Equal(requests, h.GitHub.Requests.Count);
    }

    [Theory]
    [InlineData("77.0.0.100", 0x9a2a4834u, "9A2A4834", "77.0.0.100", TableFetchedBecause.ExactBuild, false, false)]
    [InlineData("1.60.1.70500", 0x9a2a4834u, "9A2A4834", "1.60.1.70500", TableFetchedBecause.ExactBuild, false, true)]
    [InlineData("1.60.1.70450", 0x1234abcdu, "1234ABCD", "1.60.1.70450", TableFetchedBecause.LayoutNotBundled, false, false)]
    // The bundled definition lists the build; the fetched one has the layout only on another build of that line.
    [InlineData("1.60.1.70450", 0x1234abcdu, "1234ABCD", "1.60.1.69000", TableFetchedBecause.LayoutNotBundled, true, false)]
    [InlineData("77.0.0.200", 0x9a2a4834u, "9A2A4834", "77.0.0.100", TableFetchedBecause.LayoutMatch, true, false)]
    public async Task Rows_say_which_rule_chose_the_fetched_definition(string build, uint layout, string hex, string listed, string because,
        bool byLayout, bool fallback)
    {
        await using var h = await Tables.Create(build, layout);
        // A fetched definition that lists another build of the line doesn't list this one.
        var text = Encoding.UTF8.GetString(ItemWith(hex, listed));
        if (listed != build) text = text.Replace(", " + build, "", StringComparison.Ordinal);
        h.GitHub.Serve("Item", Revision, Encoding.UTF8.GetBytes(text));
        var snapshot = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(because, snapshot.FetchedBecause);
        Assert.Equal(byLayout ? TableSchemaMatch.LayoutOnly : null, snapshot.SchemaMatch); Assert.Equal(fallback, snapshot.BundledFallback);
        Assert.Contains($"\"fetchedBecause\":\"{because}\"", JsonSerializer.Serialize(snapshot, Json));
    }

    [Fact]
    public async Task Stored_rows_whose_re_check_failed_cost_one_attempt_per_prepare()
    {
        await using var h = await Tables.Create("77.0.0.100");
        h.GitHub.Serve("Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.NotNull((await h.Store.PrepareAsync(h.Request, None, default)).FetchedDefinition);
        // The cache is gone and GitHub can't be reached: the stored rows' re-check asks and fails, the rows go stale,
        // and the load after that reuses the outcome instead of asking a second time.
        Directory.Delete(Path.Combine(h.DataPath, WoWDBDefsDefinitions.FolderName), true);
        await using var restarted = h.Restart();
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        var before = h.GitHub.Requests.Count;
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await Assert.ThrowsAsync<DiscoveryException>(() => restarted.PrepareAsync(h.Request, None, default))).Code);
        Assert.Equal(before + 1, h.GitHub.Requests.Count);
    }

    [Theory]
    [InlineData("unlisted build", "77.0.0.100", 0x9a2a4834u, "9A2A4834")]
    [InlineData("listed build, uncovered layout", "1.60.1.70450", 0x1234abcdu, "1234ABCD")]
    public async Task Retry_a_minute_after_an_offline_failure_asks_once_and_opens(string path, string version, uint layout, string hex)
    {
        await using var h = await Tables.Create(version, layout);
        h.GitHub.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline");
        Assert.Equal("DEFINITION_FETCH_OFFLINE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, None, default))).Code);
        Assert.True(h.GitHub.Requests.Count == 1, $"{path}: one attempt per prepare");
        h.Clock.Now += TimeSpan.FromMinutes(1); h.GitHub.Failure = null; h.GitHub.Serve("Item", Revision, ItemWith(hex, version));
        var opened = await h.Store.PrepareAsync(h.Request, None, default);
        Assert.Equal(Revision, opened.FetchedDefinition!.Revision); Assert.Equal(hex, opened.LayoutHash);
        Assert.True(h.GitHub.Requests.Count == 3, $"{path}: the retry made one attempt (commit, then file)");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One opened build whose Item file has the given layout, a fake GitHub and a switchable policy.</summary>
    private sealed class Tables : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        internal string DataPath => directory.Path;
        private VerifiedObjectStore objects = null!; private HttpClient cdnClient = null!;
        internal DiscoveryCatalog DiscoveryCatalog { get; private set; } = null!;
        internal R.Handler Cdn { get; private set; } = null!;
        internal GitHub GitHub { get; } = new();
        internal Clock Clock { get; } = new();
        internal Policy Policy { get; private set; } = null!;
        internal TableDataStore Store { get; private set; } = null!;
        internal TablesCatalog Catalog { get; private set; } = null!;
        internal TableDataRequest Request { get; private set; } = null!;
        internal TableDataRequest Feature => Request with { FetchedDefinitions = false };
        internal TableDefinitionRequest DefinitionRequest => new("wow", Request.BuildIdentity, "us", ItemTable);
        internal TableRowsRequest Rows(string artifact) => new("wow", Request.BuildIdentity, "us", ItemTable, "enUS", artifact);
        internal int OpenedRequests { get; private set; }
        private readonly List<IAsyncDisposable> stores = [];

        internal static async Task<Tables> Create(string version, uint layout = 0x9a2a4834, bool enabled = true)
        {
            var h = new Tables(); h.Policy = new(enabled); var fixture = InventoryFixture.Create();
            var provider = await TablesCatalog.Provider.Value;
            var count = provider.Definitions[ItemTable].Document.SelectFile("1.60.1.70450", "9A2A4834").Blocks.Single().Fields.Count(f => !f.NonInline);
            var payload = R.Table(count, layout); var root = R.Root(payload.ContentKey); var en = R.Encoding([payload, root]);
            var config = System.Text.Encoding.UTF8.GetBytes($"root = {root.ContentKey}\nencoding = {en.ContentKey} {en.EncodingKey}\nencoding-size = {en.Decoded.Length} {en.Encoded.Length}\n");
            var configKey = R.Md5(config); var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
                { [InventoryFixture.Url("config", configKey)] = config, [InventoryFixture.Url("config", fixture.CdnKey)] = fixture.CdnConfig };
            foreach (var obj in new[] { payload, root, en }) responses.Add(InventoryFixture.Url("data", obj.EncodingKey), obj.Encoded);
            var prior = fixture.Discovery.Documents.Single(d => d.Role == "versions");
            var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(prior.Bytes).Replace(fixture.BuildKey, configKey, StringComparison.Ordinal).Replace("1.2.3.12345", version, StringComparison.Ordinal));
            var row = MetadataParser.Parse(bytes).Rows.Single(); var identity = BuildIdentity.Create("wow", row.Fields);
            var discovery = fixture.Discovery with { Documents = [prior with { Bytes = bytes, Sha256 = R.Sha(bytes) }, fixture.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [fixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields }] };
            h.DiscoveryCatalog = await DiscoveryCatalog.OpenAsync(h.directory.Path); h.objects = new(h.directory.Path);
            h.Cdn = new(responses); h.cdnClient = new(h.Cdn);
            h.Request = new("wow", identity.Id, "us", ItemTable, "enUS", FetchedDefinitions: true);
            var generation = await h.DiscoveryCatalog.BeginRefreshAsync("wow", default);
            await h.DiscoveryCatalog.CommitAsync("wow", generation, discovery, "2026-10-19T00:00:00Z", default);
            using (var configurations = new ConfigurationWorkspacePreparer(h.DiscoveryCatalog, h.objects, h.cdnClient))
                await configurations.PrepareAsync(new("wow", identity.Id, "us"), _ => Task.CompletedTask, default);
            h.OpenedRequests = h.Cdn.Requests;
            h.Store = h.Restart(); h.Catalog = new(h.DiscoveryCatalog, h.Sources());
            return h;
        }
        private TableDefinitionSources Sources() => new(new WoWDBDefsDefinitions(directory.Path, new HttpClient(GitHub), Clock), Policy);
        /// <summary>A new store and definition cache over the same storage, as after an engine restart.</summary>
        internal TableDataStore Restart() { var store = new TableDataStore(DiscoveryCatalog, objects, cdnClient, definitions: Sources()); stores.Add(store); return store; }
        public async ValueTask DisposeAsync()
        {
            foreach (var store in stores) await store.DisposeAsync();
            cdnClient.Dispose(); await objects.DisposeAsync(); await DiscoveryCatalog.DisposeAsync(); directory.Dispose();
        }
    }
}
