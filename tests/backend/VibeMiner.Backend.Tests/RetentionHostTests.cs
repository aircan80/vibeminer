using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class RetentionHostTests
{
    private static readonly RetentionTarget Target = new("wow", new string('a', 64), "us", new string('b', 64));
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static object Query => new { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Local_read_list_and_durable_ack_preserve_exact_wire_numbers_and_state_names()
    {
        var store = new Store(); var driver = new Driver(); var service = new RetentionService(store, driver);
        await service.RestoreAsync();
        var replies = await Run(service,
            Request("hello", "hello", new { }), Request("read", "retention.read", Query),
            Request("list", "retention.list", new { }),
            Request("set", "retention.set", new { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId,
                action = "downloadAll", expectedRevision = "9007199254740993" }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "retention-v1");
        var read = replies["read"].GetProperty("result");
        Assert.Equal("9007199254740993", read.GetProperty("revision").GetString());
        Assert.Equal("onDemand", read.GetProperty("mode").GetString());
        Assert.Equal("3", read.GetProperty("coverage").GetProperty("receiptObjects").GetString());
        Assert.Equal("notAssessed", read.GetProperty("coverage").GetProperty("presenceState").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("coverage").GetProperty("presenceAssessedAt").ValueKind);
        Assert.Equal("7", read.GetProperty("progress").GetProperty("completed").GetString());
        Assert.Equal(JsonValueKind.Null, read.GetProperty("progress").GetProperty("total").ValueKind);
        Assert.Single(replies["list"].GetProperty("result").GetProperty("jobs").EnumerateArray());
        var committed = replies["set"].GetProperty("result");
        Assert.Equal("downloadAll", committed.GetProperty("mode").GetString());
        Assert.Equal("queued", committed.GetProperty("state").GetString());
        Assert.Equal(1, driver.Notifications); Assert.Equal(1, store.Mutations);
        Assert.Equal(9007199254740993L, store.Last!.ExpectedRevision);
        await service.ShutdownAsync();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("9223372036854775808")]
    [InlineData(" 1")]
    [InlineData("\u0661")]
    public async Task Invalid_revision_never_mutates_or_signals(string revision)
    {
        var store = new Store(); var driver = new Driver();
        var replies = await Run(new(store, driver), Request("bad", "retention.set", new
        { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId, action = "downloadAll", expectedRevision = revision }));
        Assert.Equal("INVALID_REQUEST", replies["bad"].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, store.Mutations); Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Wrong_parameter_types_extra_fields_and_unknown_actions_never_mutate()
    {
        var store = new Store(); var driver = new Driver();
        var replies = await Run(new(store, driver),
            Request("number", "retention.set", new { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId, action = "pause", expectedRevision = 1 }),
            Request("extra", "retention.read", new { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId, force = true }),
            Request("list", "retention.list", new { force = true }),
            Request("unknown", "retention.set", new { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId, action = "cancel" }));
        Assert.All(replies.Values, frame => Assert.Equal("INVALID_REQUEST", frame.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(0, store.Mutations); Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Missing_service_does_not_advertise_or_fake_retention()
    {
        var replies = await Run(null, Request("hello", "hello", new { }), Request("read", "retention.read", Query));
        Assert.DoesNotContain(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "retention-v1");
        Assert.Equal("RETENTION_UNAVAILABLE", replies["read"].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Stale_intent_remains_typed_and_does_not_notify_driver()
    {
        var store = new Store { FailStale = true }; var driver = new Driver();
        var replies = await Run(new(store, driver), Request("stale", "retention.set", new
        { Target.ProductId, Target.BuildIdentity, Target.SourceRegion, Target.LedgerId, action = "resume", expectedRevision = "1" }));
        Assert.Equal("RETENTION_STALE", replies["stale"].GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, driver.Notifications);
    }

    private static async Task<Dictionary<string, JsonElement>> Run(RetentionService? retention, params object[] requests)
    {
        var protocol = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), retention: retention);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, Json))) + "\n"));
        using var output = new MemoryStream();
        await protocol.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(frame => frame.GetProperty("id").GetString()!);
    }
    private sealed class Store : IRetentionStore
    {
        internal int Mutations; internal RetentionSetRequest? Last; internal bool FailStale;
        private static RetentionSnapshot Snapshot(RetentionMode mode = RetentionMode.OnDemand, RetentionState state = RetentionState.OnDemand) =>
            new(Target, null, 9007199254740993, mode, false, state, "downloadManifestAllVariants", 3, 90,
                new(3, 90, "2026-10-19T00:00:00Z", "notAssessed", null, 0, 0, 0, 0, 0),
                new("Waiting for temporary storage", "bytes", 7), 0, 0, 0, 0, null, null);
        public Task<RetentionSnapshot> ReadAsync(RetentionTarget target, CancellationToken token) => Task.FromResult(Snapshot());
        public Task<IReadOnlyList<RetentionSnapshot>> ListAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<RetentionSnapshot>>([Snapshot()]);
        public Task<RetentionSnapshot> SetAsync(RetentionSetRequest request, CancellationToken token)
        {
            if (FailStale) throw new DiscoveryException("RETENTION_STALE", "The saved intent changed.", true);
            Last = request; Mutations++; return Task.FromResult(Snapshot(RetentionMode.DownloadAll, RetentionState.Queued));
        }
    }
    private sealed class Driver : IRetentionDriver
    {
        internal int Notifications;
        public Task RestoreAsync(CancellationToken token) => Task.CompletedTask;
        public void NotifyIntentChanged() => Notifications++;
        public Task ShutdownAsync(CancellationToken token) => Task.CompletedTask;
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
