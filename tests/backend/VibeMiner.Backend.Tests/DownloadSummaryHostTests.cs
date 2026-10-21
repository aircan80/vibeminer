using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DownloadSummaryHostTests
{
    private static readonly string Opened = new('a', 64), Never = new('b', 64);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };

    [Fact]
    public async Task Summaries_keep_request_order_exact_decimal_strings_and_state_names()
    {
        var store = new Store();
        var replies = await Run(new(store), Request("hello", "hello", new { }),
            Request("read", "download.summaries", new { builds = new[] { new { productId = "wow", buildIdentity = Opened }, new { productId = "wowt", buildIdentity = Never } } }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), c => c.GetString() == "download-summary-v1");
        var summaries = replies["read"].GetProperty("result").GetProperty("summaries").EnumerateArray().ToArray();
        Assert.Equal(["measured", "notOpened"], summaries.Select(s => s.GetProperty("state").GetString()));
        var download = summaries[0].GetProperty("download");
        Assert.Equal("188202631691", download.GetProperty("totalEncodedBytes").GetString());
        Assert.Equal("9007199254740993", download.GetProperty("storedBytes").GetString());
        Assert.Equal("current", download.GetProperty("countState").GetString());
        Assert.Equal(JsonValueKind.Null, download.GetProperty("countProblem").ValueKind);
        Assert.Equal("assessed", download.GetProperty("presence").GetProperty("state").GetString());
        Assert.Equal("2", download.GetProperty("presence").GetProperty("missingObjects").GetString());
        Assert.True(download.GetProperty("downloadAll").GetBoolean()); Assert.False(download.GetProperty("running").GetBoolean());
        Assert.Equal(["eu", "us"], download.GetProperty("sourceRegions").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(JsonValueKind.Null, summaries[1].GetProperty("download").ValueKind);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Malformed_oversized_and_duplicate_requests_are_refused_before_any_read()
    {
        var store = new Store();
        var build = new { productId = "wow", buildIdentity = Opened };
        var replies = await Run(new(store),
            Request("empty", "download.summaries", new { builds = Array.Empty<object>() }),
            Request("many", "download.summaries", new { builds = Enumerable.Range(0, 101).Select(i => new { productId = "wow", buildIdentity = i.ToString("x64") }).ToArray() }),
            Request("duplicate", "download.summaries", new { builds = new[] { build, build } }),
            Request("extra", "download.summaries", new { builds = new[] { build }, force = true }),
            Request("region", "download.summaries", new { builds = new[] { new { productId = "wow", buildIdentity = Opened, sourceRegion = "us" } } }),
            Request("product", "download.summaries", new { builds = new[] { new { productId = "diablo", buildIdentity = Opened } } }),
            Request("shape", "download.summaries", new { builds = "wow" }));
        Assert.All(replies, frame => Assert.Equal(frame.Key == "product" ? "INVALID_PRODUCT" : "INVALID_REQUEST", frame.Value.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Missing_service_does_not_advertise_or_fake_summaries()
    {
        var replies = await Run(null, Request("hello", "hello", new { }),
            Request("read", "download.summaries", new { builds = new[] { new { productId = "wow", buildIdentity = Opened } } }));
        Assert.DoesNotContain(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), c => c.GetString() == "download-summary-v1");
        Assert.Equal("DOWNLOAD_SUMMARY_UNAVAILABLE", replies["read"].GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<Dictionary<string, JsonElement>> Run(DownloadSummaryService? summaries, params object[] requests)
    {
        var protocol = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), downloadSummaries: summaries);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, Json))) + "\n"));
        using var output = new MemoryStream();
        await protocol.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(frame => frame.GetProperty("id").GetString()!);
    }
    private sealed class Store : IDownloadSummaryStore
    {
        internal int Reads;
        public Task<IReadOnlyList<BuildDownloadSummary>> ReadDownloadSummariesAsync(IReadOnlyList<BuildKey> builds, CancellationToken token)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<BuildDownloadSummary>>(builds.Select(build => build.BuildIdentity == Opened
                ? new BuildDownloadSummary(build.ProductId, build.BuildIdentity, BuildDownloadState.Measured, new(new('c', 64), "downloadManifestAllVariants",
                    2881628, 188202631691, 2881620, 9007199254740993, StoredCountState.Current, null, "2026-10-20T00:00:00Z",
                    new("assessed", "2026-10-19T00:00:00Z", 2881628, 2, 6), true, false, ["eu", "us"]))
                : new BuildDownloadSummary(build.ProductId, build.BuildIdentity, BuildDownloadState.NotOpened, null)).ToArray());
        }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
