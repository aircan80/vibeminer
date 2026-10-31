using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>#9 / D106: the wago.tools build list and Check availability, against local fakes of wago.tools and Blizzard's CDN.</summary>
public sealed class CommunityBuildsTests
{
    private const string Time = "2026-10-20T09:00:00.0000000+00:00";
    private static string Hash(char c) => new(c, 32);
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    // Authored configs, not real ones.
    private static readonly byte[] BuildBytes = Encoding.UTF8.GetBytes("# Build Configuration\n\nroot = " + Hash('1') + "\nbuild-name = authored\n");
    private static readonly string[] Archives = [Hash('a'), Hash('b'), Hash('c')];
    private static readonly byte[] CdnBytes = Encoding.UTF8.GetBytes($"# CDN Configuration\n\narchives = {string.Join(' ', Archives)}\narchive-group = {Hash('d')}\npatch-archives = {Hash('e')}\n");
    private static readonly string BuildKey = Md5(BuildBytes), CdnKey = Md5(CdnBytes);
    private const string CdnsWithHosts = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\n" +
        "eu|tpr/wow|eu.cdn.blizzard.com|https://eu.cdn.blizzard.com/?maxhosts=4\n" +
        "us|tpr/wow|us.cdn.blizzard.com level3.blizzard.com|http://us.cdn.blizzard.com/?maxhosts=4 https://level3.ssl.blizzard.com/?maxhosts=4&fallback=1 https://us.cdn.blizzard.com/?maxhosts=4&fallback=1\n";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Policy(bool enabled) : ICommunityBuildPolicy
    {
        public bool Enabled = enabled;
        public Task<bool> CommunityBuildHistoryEnabledAsync(CancellationToken token) => Task.FromResult(Enabled);
    }

    private static string Wago(params (string Product, string Version, string Build, string Cdn, string? ProductConfig)[] rows) =>
        JsonSerializer.Serialize(rows.GroupBy(row => row.Product).ToDictionary(group => group.Key, group => group.Select(row => new Dictionary<string, object?>
        {
            ["product"] = row.Product, ["version"] = row.Version, ["created_at"] = "2025-03-04 05:06:07", ["build_config"] = row.Build,
            ["cdn_config"] = row.Cdn, ["product_config"] = row.ProductConfig, ["is_bgdl"] = false,
        }).ToArray()));

    /// <summary>A local fake of wago.tools' build list.</summary>
    private sealed class WagoTools(string body) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public string Body = body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Exception? Failure;
        public TaskCompletionSource? Hold;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Requests) Requests.Add(request);
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            if (Failure is not null) throw Failure;
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json"), RequestMessage = request };
            if (Status == HttpStatusCode.Found) response.Headers.Location = new Uri("https://elsewhere.invalid/");
            return response;
        }
    }

    private static async Task Store(DiscoveryCatalog catalog, string product, params string[] rows)
    {
        using var source = Fixtures.Source(Fixtures.Versions(rows), CdnsWithHosts);
        var generation = await catalog.BeginRefreshAsync(product, CancellationToken.None);
        await catalog.CommitAsync(product, generation, await source.FetchAsync(product, CancellationToken.None), Time, CancellationToken.None);
    }
    private static string Row(string version, string build, string cdn = Fixtures.B) =>
        $"us|{build}|{cdn}||{version.Split('.')[^1]}|{version}|{Fixtures.C}|opaque";

    private static async Task<List<Build>> ReadAll(DiscoveryCatalog catalog, bool community, string filter = "all", int limit = 2)
    {
        var builds = new List<Build>(); string? cursor = null;
        do
        {
            var page = await catalog.ReadAllAsync(null, filter, limit, cursor, community, CancellationToken.None);
            builds.AddRange(page.Builds); cursor = page.NextCursor;
        } while (cursor is not null);
        return builds;
    }

    [Fact]
    public void ParseKeepsOnlyUsableRowsOfRegisteredProducts()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"wow":[
              {"product":"wow","version":"11.2.7.67542","created_at":"2025-03-04 05:06:07","build_config":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","product_config":null,"is_bgdl":false},
              {"product":"wow","version":"11.2.7.67542","created_at":"2025-03-04 05:06:07","build_config":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","product_config":null},
              {"product":"wow","version":"11.2","build_config":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
              {"product":"wow","version":"11.2.5.66192","build_config":"not-a-hash","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
              {"product":"wowt","version":"11.2.5.66192","build_config":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"},
              {"product":"wow","version":"11.2.5.66192","build_config":"cccccccccccccccccccccccccccccccc","cdn_config":"dddddddddddddddddddddddddddddddd","product_config":"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee","created_at":"garbage"},
              "not an object"],
             "wowdev":[{"product":"wowdev","version":"1.0.0.1","build_config":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","cdn_config":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}]}
            """);
        var (rows, skipped) = WagoBuildList.Parse(json);
        Assert.Equal(6, skipped);
        Assert.Collection(rows,
            row =>
            {
                Assert.Equal(("wow", "11.2.7.67542", Hash('a'), Hash('b'), (string?)null), (row.ProductId, row.Version, row.BuildConfig, row.CdnConfig, row.ProductConfig));
                Assert.Equal("2025-03-04T05:06:07.0000000+00:00", row.CreatedAt);
                Assert.Equal(67542, row.BuildNumber);
            },
            row => { Assert.Equal(Hash('e'), row.ProductConfig); Assert.Null(row.CreatedAt); });
        // The identity is its own namespace: it never equals a Blizzard identity of the same fields.
        Assert.NotEqual(rows[0].Id, rows[1].Id);
        Assert.Matches("^[0-9a-f]{64}$", rows[0].Id);
        Assert.Equal("COMMUNITY_BUILDS_INVALID", Assert.Throws<DiscoveryException>(() => WagoBuildList.Parse("[]"u8.ToArray())).Code);
        Assert.Equal("COMMUNITY_BUILDS_INVALID", Assert.Throws<DiscoveryException>(() => WagoBuildList.Parse("{\"wow\":{}}"u8.ToArray())).Code);
        Assert.Equal("COMMUNITY_BUILDS_INVALID", Assert.Throws<DiscoveryException>(() => WagoBuildList.Parse("{"u8.ToArray())).Code);
    }

    [Theory]
    [InlineData("reset", "COMMUNITY_BUILDS_OFFLINE")]
    [InlineData("ended", "COMMUNITY_BUILDS_OFFLINE")]
    [InlineData("tls-reset", "COMMUNITY_BUILDS_OFFLINE")]
    [InlineData("certificate", "COMMUNITY_BUILDS_FAILED")]
    [InlineData("garbled", "COMMUNITY_BUILDS_FAILED")]
    public async Task A_connection_closed_before_any_answer_is_offline(string shape, string code)
    {
        // #127: a proxy's reset surfaced as an unknown error over a socket error and read as a failed fetch.
        var wago = new WagoTools("{}") { Failure = ConnectionFailures.Of(shape) };
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => new WagoBuildList(new HttpClient(wago)).FetchAsync(CancellationToken.None));
        Assert.Equal(code, failure.Code); Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task FetchNamesVibeMinerAndRefusesRedirectsErrorsAndOversizedAnswers()
    {
        var wago = new WagoTools(Wago(("wow", "11.2.7.67542", Hash('a'), Hash('b'), null)));
        var list = new WagoBuildList(new HttpClient(wago));
        var fetch = await list.FetchAsync(CancellationToken.None);
        var request = Assert.Single(wago.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://wago.tools/api/builds", request.RequestUri!.AbsoluteUri);
        Assert.Contains("VibeMiner", request.Headers.UserAgent.ToString());
        Assert.Contains("github.com/aircan80/vibeminer", request.Headers.UserAgent.ToString());
        Assert.Single(fetch.Rows);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(wago.Body))), fetch.Sha256);

        wago.Status = HttpStatusCode.Found;
        Assert.Equal("COMMUNITY_BUILDS_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => list.FetchAsync(CancellationToken.None))).Code);
        wago.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal("COMMUNITY_BUILDS_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => list.FetchAsync(CancellationToken.None))).Code);
        wago.Status = HttpStatusCode.OK; wago.Body = new string(' ', WagoBuildList.MaxResponseBytes + 1);
        Assert.Equal("COMMUNITY_BUILDS_TOO_LARGE", (await Assert.ThrowsAsync<DiscoveryException>(() => list.FetchAsync(CancellationToken.None))).Code);
        wago.Failure = new HttpRequestException(HttpRequestError.ConnectionError, "down");
        Assert.Equal("COMMUNITY_BUILDS_OFFLINE", (await Assert.ThrowsAsync<DiscoveryException>(() => list.FetchAsync(CancellationToken.None))).Code);
        wago.Failure = null; wago.Body = "{}"; wago.Hold = new();
        var slow = new WagoBuildList(new HttpClient(wago), readBudget: TimeSpan.FromMilliseconds(50));
        Assert.Equal("COMMUNITY_BUILDS_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => slow.FetchAsync(CancellationToken.None))).Code);
        Assert.Throws<ArgumentException>(() => new WagoBuildList(new HttpClient(wago), new Uri("https://wago.tools/")));
    }

    [Fact]
    public async Task TestOriginIsALocalServerThatGetsTheSameRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var body = Encoding.UTF8.GetBytes(Wago(("wowt", "12.0.1.70000", Hash('a'), Hash('b'), Hash('c'))));
        var served = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var head = new StringBuilder(); var buffer = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1) head.Append((char)buffer[0]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
            return head.ToString();
        });
        using var client = new HttpClient(WagoBuildList.CreateHandler());
        var fetch = await new WagoBuildList(client, new Uri($"http://127.0.0.1:{port}/")).FetchAsync(CancellationToken.None);
        var request = await served;
        Assert.StartsWith("GET /api/builds HTTP/1.1", request);
        Assert.Contains("User-Agent: VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)", request);
        // The receipt names the real list, not the test origin.
        Assert.Equal("https://wago.tools/api/builds", fetch.Url);
        Assert.Equal("wowt", Assert.Single(fetch.Rows).ProductId);
    }

    [Fact]
    public async Task ListIsAskedAtMostOnceADayOnlyWhileTheSwitchIsOn()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var wago = new WagoTools(Wago(("wow", "11.2.7.67542", Hash('a'), Hash('b'), null)));
        var clock = new Clock(); var policy = new Policy(false);
        var service = new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, policy, clock);
        await service.RefreshIfDueAsync(CancellationToken.None);
        Assert.Empty(wago.Requests);
        Assert.Null((await catalog.ReadCommunityBuildsStateAsync(CancellationToken.None)).LastAttemptAt);

        policy.Enabled = true;
        await service.RefreshIfDueAsync(CancellationToken.None);
        await service.RefreshIfDueAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(23);
        await service.RefreshIfDueAsync(CancellationToken.None);
        Assert.Single(wago.Requests);
        var state = await catalog.ReadCommunityBuildsStateAsync(CancellationToken.None);
        Assert.Equal(1, state.BuildCount); Assert.Null(state.Problem); Assert.NotNull(state.FetchedAt);
        var status = CommunityBuildsService.Status(true, state);
        Assert.Equal("ready", status.State);
        Assert.Equal("2026-10-24T12:00:00.0000000+00:00", status.NextCheckAfter);

        // A failed attempt is saved as the list's problem, keeps the earlier builds, and still waits a day.
        clock.Now += TimeSpan.FromHours(1);
        wago.Status = HttpStatusCode.InternalServerError;
        await service.RefreshIfDueAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromHours(2);
        await service.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(2, wago.Requests.Count);
        state = await catalog.ReadCommunityBuildsStateAsync(CancellationToken.None);
        Assert.Equal("COMMUNITY_BUILDS_FAILED", state.Problem!.Code);
        Assert.Equal(1, state.BuildCount);
        Assert.Equal("failed", CommunityBuildsService.Status(true, state).State);
        Assert.Equal("off", CommunityBuildsService.Status(false, state).State);

        // Cancelling stops the request; the attempt counts and says so.
        clock.Now += TimeSpan.FromDays(2);
        wago.Status = HttpStatusCode.OK; wago.Hold = new();
        using var cancel = new CancellationTokenSource();
        var running = service.RefreshIfDueAsync(cancel.Token);
        while (wago.Requests.Count < 3) await Task.Delay(5);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        state = await catalog.ReadCommunityBuildsStateAsync(CancellationToken.None);
        Assert.Equal("COMMUNITY_BUILDS_STOPPED", state.Problem!.Code);
        Assert.Equal(clock.Now.ToString("O"), state.LastAttemptAt);
    }

    [Fact]
    public async Task CommunityBuildsAreListedAmongBlizzardBuildsOnlyInAllAndNeverTwice()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, "wow", Row("11.2.7.67542", Hash('a')));
        var wago = new WagoTools(Wago(
            ("wow", "11.2.7.67542", Hash('a'), Hash('9'), null),            // Blizzard advertised it: listed once, as Blizzard's
            ("wow", "11.2.5.66192", Hash('2'), Hash('3'), Hash('4')),
            ("wow", "12.0.0.69000", Hash('5'), Hash('6'), null),
            ("wowt", "12.0.1.70000", Hash('7'), Hash('8'), null),
            ("wow_classic_era", "1.15.7.61000", Hash('c'), Hash('d'), null)));
        var service = new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock());
        await service.RefreshIfDueAsync(CancellationToken.None);

        var official = await ReadAll(catalog, community: false);
        Assert.Equal(new[] { "wow:67542" }, official.Select(build => $"{build.ProductId}:{build.BuildId}"));
        var all = await ReadAll(catalog, community: true);
        Assert.Equal(new[] { "wow:69000", "wow:67542", "wow:66192", "wowt:70000", "wow_classic_era:61000" }, all.Select(build => $"{build.ProductId}:{build.BuildId}"));
        Assert.Null(all[1].Community);
        var community = all[2];
        Assert.Equal(("wago.tools", "https://wago.tools/api/builds", "2025-03-04T05:06:07.0000000+00:00"),
            (community.Community!.Source, community.Community.SourceUrl, community.Community.CreatedAt));
        Assert.Equal((Hash('2'), Hash('3'), Hash('4'), "11.2.5.66192"), (community.BuildConfig, community.CdnConfig, community.ProductConfig, community.Version));
        Assert.Empty(community.Regions); Assert.Empty(community.RouteRegions); Assert.Empty(community.SourceReceipts);
        Assert.False(community.IsAdvertised || community.IsLocal || community.IsNew);
        Assert.False(community.IdentityFields.ContainsKey("KeyRing"));
        Assert.All(await ReadAll(catalog, community: true, filter: "advertised"), build => Assert.Null(build.Community));
        Assert.All(await ReadAll(catalog, community: true, filter: "local"), build => Assert.Null(build.Community));

        // A community build can't be opened: it has no Blizzard observation to pin.
        Assert.Equal("BUILD_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveLatestOpeningAsync(new("wow", community.Id, "us"), CancellationToken.None))).Code);

        // Blizzard advertising a listed build later takes it over.
        await Store(catalog, "wowt", Row("12.0.1.70000", Hash('7')));
        var after = await ReadAll(catalog, community: true);
        Assert.Single(after, build => build.ProductId == "wowt");
        Assert.Null(after.Single(build => build.ProductId == "wowt").Community);

        // A changed import or switch makes earlier cursors stale.
        var first = await catalog.ReadAllAsync(null, "all", 1, null, true, CancellationToken.None);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "all", 1, first.NextCursor, false, CancellationToken.None))).Code);
        var page = await catalog.ReadAllAsync(null, "all", 100, null, true, CancellationToken.None);
        Assert.Equal("ready", page.CommunityBuilds.State);
        // Builds Blizzard also advertised aren't counted as community builds.
        Assert.Equal(3, page.CommunityBuilds.BuildCount);
    }

    [Fact]
    public async Task DiscoveryServiceAsksTheListWithRefreshAndShowsItsStatusOnPages()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var wago = new WagoTools(Wago(("wow", "11.2.5.66192", Hash('2'), Hash('3'), null)));
        var policy = new Policy(true);
        using var source = Fixtures.Source(Fixtures.Versions(Row("11.2.7.67542", Hash('a'))), CdnsWithHosts);
        var service = new DiscoveryService(source, catalog, community: new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, policy, new Clock()));
        var before = await service.ReadAllAsync();
        Assert.Equal("notFetched", before.CommunityBuilds.State);
        Assert.Empty(wago.Requests);
        var refreshed = await service.RefreshAllAsync(null, CancellationToken.None);
        Assert.Single(wago.Requests);
        Assert.Equal("ready", refreshed.CommunityBuilds.State);
        Assert.Contains(refreshed.Builds, build => build.Community is not null);
        // A failing list never fails the product checks.
        wago.Status = HttpStatusCode.InternalServerError;
        await catalog.RecordCommunityAttemptAsync("2020-01-01T00:00:00.0000000+00:00", CancellationToken.None);
        refreshed = await service.RefreshAllAsync(null, CancellationToken.None);
        Assert.Empty(refreshed.RefreshProblems);
        Assert.Equal("failed", refreshed.CommunityBuilds.State);
        Assert.Contains(refreshed.Builds, build => build.Community is not null);
        policy.Enabled = false;
        var off = await service.ReadAllAsync();
        Assert.Equal("off", off.CommunityBuilds.State);
        Assert.DoesNotContain(off.Builds, build => build.Community is not null);
    }

    /// <summary>A local fake of Blizzard's CDN: which objects each host has, and how it answers the others.</summary>
    private sealed class Cdn : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Objects = new(StringComparer.Ordinal);
        public readonly HashSet<string> Missing = new(StringComparer.Ordinal);
        public Func<Uri, HttpStatusCode?> Override = _ => null;
        public readonly List<(HttpMethod Method, Uri Uri)> Requests = [];
        public TaskCompletionSource? Hold;
        public Cdn()
        {
            foreach (var host in new[] { "level3.ssl.blizzard.com", "us.cdn.blizzard.com" })
            {
                Objects[$"{host}/tpr/wow/config/{BuildKey[..2]}/{BuildKey[2..4]}/{BuildKey}"] = BuildBytes;
                Objects[$"{host}/tpr/wow/config/{CdnKey[..2]}/{CdnKey[2..4]}/{CdnKey}"] = CdnBytes;
                foreach (var archive in Archives)
                {
                    Objects[$"{host}/tpr/wow/data/{archive[..2]}/{archive[2..4]}/{archive}"] = [1];
                    Objects[$"{host}/tpr/wow/data/{archive[..2]}/{archive[2..4]}/{archive}.index"] = [2];
                }
            }
        }
        public void Remove(string pathSuffix) { foreach (var key in Objects.Keys.Where(key => key.EndsWith(pathSuffix, StringComparison.Ordinal)).ToArray()) Objects.Remove(key); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            lock (Requests) Requests.Add((request.Method, uri));
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            var status = Override(uri);
            var key = uri.Host + uri.AbsolutePath;
            if (status is null && Objects.TryGetValue(key, out var bytes))
                return new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : bytes) };
            // level3 answers a missing object with 403, the others with 404.
            return new(status ?? (uri.Host.StartsWith("level3", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound)) { RequestMessage = request };
        }
    }

    private static async Task<(DiscoveryCatalog Catalog, TempDirectory Directory, Build Build)> Saved(string? cdnKey = null)
    {
        var directory = new TempDirectory(); var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, "wow", Row("11.2.7.67542", BuildKey, cdnKey ?? CdnKey));
        return (catalog, directory, (await catalog.ReadAllAsync(null, "all", 10, null, CancellationToken.None)).Builds.Single());
    }

    private static async Task<(BuildAvailability Result, List<AvailabilityProgress> Progress)> Check(DiscoveryCatalog catalog, Build build, Cdn cdn, bool archives = false)
    {
        var progress = new List<AvailabilityProgress>();
        var service = new BuildAvailabilityService(catalog, new CdnAvailabilityProbe(new HttpClient(cdn)));
        var result = await service.CheckAsync(build.ProductId, build.Id, archives, value => { progress.Add(value); return Task.CompletedTask; }, CancellationToken.None);
        return (result, progress);
    }

    [Fact]
    public async Task AvailableBuildChecksConfigsThenIndexesThenOptionallyArchivesAndKeepsTheResult()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            var cdn = new Cdn();
            var (result, progress) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.Available, "indexes", "verified", "verified"), (result.State, result.Scope, result.BuildConfigState, result.CdnConfigState));
            Assert.Equal(new[] { "us.cdn.blizzard.com", "level3.ssl.blizzard.com" }, result.Hosts);
            Assert.Equal(new AvailabilityCounts(3, 3, 0, 0), result.Indexes);
            Assert.Null(result.Archives);
            // HEADs and two config GETs only; never an archive body, a patch archive or the archive group.
            Assert.All(cdn.Requests, request => Assert.True(request.Method == HttpMethod.Head || request.Uri.AbsolutePath.Contains("/config/")));
            Assert.DoesNotContain(cdn.Requests, request => request.Uri.AbsolutePath.Contains("/patch/") || request.Uri.AbsolutePath.Contains(Hash('e')) || request.Uri.AbsolutePath.Contains(Hash('d')));
            Assert.Equal(2, cdn.Requests.Count(request => request.Method == HttpMethod.Get));
            Assert.Contains(progress, value => value.Stage == "Checking archive indexes" && value.Completed == 3 && value.Total == 3);
            // The result is kept with the build and the time it ran.
            var page = await catalog.ReadAllAsync(null, "all", 10, null, CancellationToken.None);
            Assert.Equal(result, page.Builds.Single().AvailabilityCheck! with { Hosts = result.Hosts, Indexes = result.Indexes });
            Assert.Equal(result.CheckedAt, page.Builds.Single().AvailabilityCheck!.CheckedAt);

            cdn.Requests.Clear();
            (result, _) = await Check(catalog, build, cdn, archives: true);
            Assert.Equal((BuildAvailability.Available, "archives"), (result.State, result.Scope));
            Assert.Equal(new AvailabilityCounts(3, 3, 0, 0), result.Archives);
            Assert.All(cdn.Requests.Where(request => request.Uri.AbsolutePath.Contains("/data/")), request => Assert.Equal(HttpMethod.Head, request.Method));
        }
    }

    [Fact]
    public async Task MissingOnEveryHostIsGoneAndLevel3ForbiddenCountsAsMissing()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            var cdn = new Cdn(); cdn.Remove(BuildKey);
            var (result, _) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.Gone, "configs", "missing"), (result.State, result.Scope, result.BuildConfigState));
            Assert.Null(result.Indexes);
            // One host that can't answer leaves it unknown, not gone.
            cdn.Override = uri => uri.Host == "us.cdn.blizzard.com" ? HttpStatusCode.ServiceUnavailable : null;
            (result, _) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.Unknown, "unknown", "AVAILABILITY_UNANSWERED"), (result.State, result.BuildConfigState, result.Problem!.Code));
        }
    }

    [Fact]
    public async Task MissingCdnConfigOrIndexIsPartlyAvailable()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            var cdn = new Cdn(); cdn.Remove(CdnKey);
            var (result, _) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.PartlyAvailable, "configs", "verified", "missing"), (result.State, result.Scope, result.BuildConfigState, result.CdnConfigState));

            cdn = new Cdn(); cdn.Remove(Archives[1] + ".index");
            (result, _) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.PartlyAvailable, "indexes"), (result.State, result.Scope));
            Assert.Equal(new AvailabilityCounts(3, 2, 1, 0), result.Indexes);

            // An index the first host can't answer for but the second has is present.
            cdn = new Cdn();
            cdn.Override = uri => uri.Host == "us.cdn.blizzard.com" && uri.AbsolutePath.EndsWith(".index", StringComparison.Ordinal) ? HttpStatusCode.BadGateway : null;
            (result, _) = await Check(catalog, build, cdn);
            Assert.Equal(BuildAvailability.Available, result.State);

            // An archive whose index is there but whose data is gone.
            cdn = new Cdn(); cdn.Remove(Archives[2]);
            (result, _) = await Check(catalog, build, cdn, archives: true);
            Assert.Equal((BuildAvailability.PartlyAvailable, "archives"), (result.State, result.Scope));
            Assert.Equal(new AvailabilityCounts(3, 2, 1, 0), result.Archives);
        }
    }

    [Fact]
    public async Task ConfigBytesThatDontMatchTheirHashAreNeverTrusted()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            var cdn = new Cdn();
            foreach (var key in cdn.Objects.Keys.Where(key => key.EndsWith(BuildKey, StringComparison.Ordinal)).ToArray()) cdn.Objects[key] = Encoding.UTF8.GetBytes("# Build Configuration\n\nroot = tampered\n");
            var (result, _) = await Check(catalog, build, cdn);
            Assert.Equal((BuildAvailability.Unknown, "invalid", "AVAILABILITY_CONFIG_INVALID"), (result.State, result.BuildConfigState, result.Problem!.Code));
            Assert.Null(result.Indexes);
        }
    }

    [Fact]
    public async Task CommunityBuildIsCheckedWithItsOwnConfigsAndAStoppedCheckSavesNothing()
    {
        var (catalog, directory, official) = await Saved();
        await using (catalog) using (directory)
        {
            var wago = new WagoTools(Wago(("wow", "11.2.5.66192", BuildKey, CdnKey, null)));
            await new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock()).RefreshIfDueAsync(CancellationToken.None);
            var community = (await catalog.ReadAllAsync(null, "all", 10, null, true, CancellationToken.None)).Builds.Single(build => build.Community is not null);
            var cdn = new Cdn();
            var (result, _) = await Check(catalog, community, cdn);
            Assert.Equal(BuildAvailability.Available, result.State);
            Assert.Equal(result.CheckedAt, (await catalog.ReadAllAsync(null, "all", 10, null, true, CancellationToken.None)).Builds.Single(build => build.Id == community.Id).AvailabilityCheck!.CheckedAt);

            // A second check at once is refused; stopping the first saves nothing and keeps the earlier result.
            cdn.Hold = new();
            var service = new BuildAvailabilityService(catalog, new CdnAvailabilityProbe(new HttpClient(cdn)));
            using var stop = new CancellationTokenSource();
            var running = service.CheckAsync("wow", community.Id, true, _ => Task.CompletedTask, stop.Token);
            while (cdn.Requests.Count == 0) await Task.Delay(5);
            Assert.Equal("AVAILABILITY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.CheckAsync("wow", official.Id, false, _ => Task.CompletedTask, CancellationToken.None))).Code);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.Equal(result.CheckedAt, (await catalog.ReadAllAsync(null, "all", 10, null, true, CancellationToken.None)).Builds.Single(build => build.Id == community.Id).AvailabilityCheck!.CheckedAt);

            Assert.Equal("BUILD_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => service.CheckAsync("wow", new string('f', 64), false, _ => Task.CompletedTask, CancellationToken.None))).Code);
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.CheckAsync("wow", "nope", false, _ => Task.CompletedTask, CancellationToken.None))).Code);
        }
    }

    [Fact]
    public async Task NoSavedBlizzardCdnListMeansNoRoute()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var wago = new WagoTools(Wago(("wow", "11.2.5.66192", BuildKey, CdnKey, null)));
        await new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock()).RefreshIfDueAsync(CancellationToken.None);
        var community = (await catalog.ReadAllAsync(null, "all", 10, null, true, CancellationToken.None)).Builds.Single();
        var cdn = new Cdn();
        var service = new BuildAvailabilityService(catalog, new CdnAvailabilityProbe(new HttpClient(cdn)));
        Assert.Equal("AVAILABILITY_NO_ROUTE", (await Assert.ThrowsAsync<DiscoveryException>(() => service.CheckAsync("wow", community.Id, false, _ => Task.CompletedTask, CancellationToken.None))).Code);
        Assert.Empty(cdn.Requests);
        // Another product's saved list is enough: every WoW product uses the same hosts.
        await Store(catalog, "wowt", Row("12.0.1.70000", Hash('7')));
        Assert.Equal(BuildAvailability.Available, (await service.CheckAsync("wow", community.Id, false, _ => Task.CompletedTask, CancellationToken.None)).State);
    }

    [Fact]
    public async Task HostAdvertisesTheFeaturesAndStreamsCheckProgressThenTheResult()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            var wago = new WagoTools("{}");
            using var source = Fixtures.Source(Fixtures.Versions(Row("11.2.7.67542", BuildKey)), CdnsWithHosts);
            var discovery = new DiscoveryService(source, catalog, community: new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock()));
            var host = new ProtocolHost(discovery, availability: new BuildAvailabilityService(catalog, new CdnAvailabilityProbe(new HttpClient(new Cdn()))));
            var frames = await RunHost(host, 3,
                new { protocolVersion = 1, id = "hello", method = "hello", @params = new { } },
                new { protocolVersion = 1, id = "check", method = "builds.availability.check", @params = new { productId = "wow", buildIdentity = build.Id, archives = false } },
                new { protocolVersion = 1, id = "bad", method = "builds.availability.check", @params = new { productId = "wow", buildIdentity = build.Id } });
            var capabilities = frames.Single(frame => frame.TryGetProperty("id", out var id) && id.GetString() == "hello").GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("community-builds-v1", capabilities); Assert.Contains("build-availability-v1", capabilities);
            var progress = frames.Where(frame => frame.TryGetProperty("type", out var type) && type.GetString() == "progress" && frame.GetProperty("requestId").GetString() == "check").ToArray();
            Assert.NotEmpty(progress);
            Assert.All(progress, frame => Assert.Equal("files", frame.GetProperty("unit").GetString()));
            Assert.Equal(Enumerable.Range(1, progress.Length), progress.Select(frame => frame.GetProperty("sequence").GetInt32()));
            var result = frames.Single(frame => frame.TryGetProperty("id", out var id) && id.GetString() == "check").GetProperty("result");
            Assert.Equal("available", result.GetProperty("state").GetString());
            Assert.Equal(3, result.GetProperty("indexes").GetProperty("present").GetInt32());
            Assert.Equal("INVALID_REQUEST", frames.Single(frame => frame.TryGetProperty("id", out var id) && id.GetString() == "bad").GetProperty("error").GetProperty("code").GetString());
            Assert.Empty(wago.Requests);
        }
    }

    [Fact]
    public async Task TextTheReaderCantTurnIntoStringsRefusesTheListAndRefreshStillSucceeds()
    {
        // A lone surrogate escape is valid JSON to the reader, but GetString and Name throw on it (#9 review).
        foreach (var json in new[] { "{\"wow\":[{\"product\":\"wow\",\"version\":\"\\uD800\",\"build_config\":\"" + Hash('a') + "\",\"cdn_config\":\"" + Hash('b') + "\"}]}", "{\"\\uD800\":[{\"version\":\"1.0.0.1\"}]}" })
            Assert.Equal("COMMUNITY_BUILDS_INVALID", Assert.Throws<DiscoveryException>(() => WagoBuildList.Parse(Encoding.UTF8.GetBytes(json))).Code);
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var wago = new WagoTools("{\"\\uD800\":[{\"version\":\"1.0.0.1\"}]}");
        using var source = Fixtures.Source(Fixtures.Versions(Row("11.2.7.67542", Hash('a'))), CdnsWithHosts);
        var service = new DiscoveryService(source, catalog, community: new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock()));
        var page = await service.RefreshAllAsync(null, CancellationToken.None);
        Assert.Empty(page.RefreshProblems);
        Assert.Contains(page.Builds, build => build.Community is null && build.BuildId == "67542");
        Assert.Equal(("failed", "COMMUNITY_BUILDS_INVALID"), (page.CommunityBuilds.State, page.CommunityBuilds.Problem!.Code));
    }

    /// <summary>A fetcher that fails with something nobody planned for.</summary>
    private sealed class Throwing : ICommunityBuildFetcher
    {
        public Task<CommunityBuildFetch> FetchAsync(CancellationToken token) => throw new InvalidOperationException("unexpected");
    }

    [Fact]
    public async Task AnUnexpectedListFaultIsSavedAsTheListsProblem()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await new CommunityBuildsService(new Throwing(), catalog, new Policy(true), new Clock()).RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal("COMMUNITY_BUILDS_INVALID", (await catalog.ReadCommunityBuildsStateAsync(CancellationToken.None)).Problem!.Code);
    }

    [Fact]
    public async Task WaitingForAnHttpPermitDoesntCountAgainstAHead()
    {
        var (catalog, directory, build) = await Saved();
        await using (catalog) using (directory)
        {
            // A download holds the only permit for longer than one HEAD may take; the check still finds every file.
            var resources = new AcquisitionResources(maximumHttp: 1, maximumBackgroundHttp: 1);
            var held = await resources.EnterHttpAsync(null, CancellationToken.None);
            var service = new BuildAvailabilityService(catalog, new CdnAvailabilityProbe(new HttpClient(new Cdn()), resources, headTimeout: TimeSpan.FromMilliseconds(100)));
            var running = service.CheckAsync(build.ProductId, build.Id, false, _ => Task.CompletedTask, CancellationToken.None);
            await Task.Delay(400);
            Assert.False(running.IsCompleted);
            held.Dispose();
            var result = await running;
            Assert.Equal(BuildAvailability.Available, result.State);
            Assert.Equal(new AvailabilityCounts(3, 3, 0, 0), result.Indexes);
        }
    }

    [Fact]
    public async Task AConfigThatMatchesItsHashButIsntAConfigSaysSo()
    {
        var unreadable = Encoding.UTF8.GetBytes("this line has no equals sign\n");
        var key = Md5(unreadable);
        var (catalog, directory, _) = await Saved();
        await using (catalog) using (directory)
        {
            var wago = new WagoTools(Wago(("wow", "11.2.5.66192", key, CdnKey, null)));
            await new CommunityBuildsService(new WagoBuildList(new HttpClient(wago)), catalog, new Policy(true), new Clock()).RefreshIfDueAsync(CancellationToken.None);
            var community = (await catalog.ReadAllAsync(null, "all", 10, null, true, CancellationToken.None)).Builds.Single(build => build.Community is not null);
            var cdn = new Cdn();
            foreach (var host in new[] { "level3.ssl.blizzard.com", "us.cdn.blizzard.com" }) cdn.Objects[$"{host}/tpr/wow/config/{key[..2]}/{key[2..4]}/{key}"] = unreadable;
            var (result, _) = await Check(catalog, community, cdn);
            Assert.Equal((BuildAvailability.Unknown, "invalid"), (result.State, result.BuildConfigState));
            Assert.Contains("matches its hash but isn’t a config VibeMiner can read", result.Problem!.Message);
        }
    }

    private static async Task<List<JsonElement>> RunHost(ProtocolHost host, int replies, params object[] requests)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        var completion = host.RunAsync(server.GetStream(), server.GetStream());
        var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var request in requests) await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        await writer.FlushAsync();
        var frames = new List<JsonElement>();
        while (frames.Count(frame => frame.TryGetProperty("id", out _)) < replies)
        {
            using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!);
            frames.Add(document.RootElement.Clone());
        }
        client.Client.Shutdown(SocketShutdown.Send);
        await completion.WaitAsync(TimeSpan.FromSeconds(20));
        return frames;
    }
}
