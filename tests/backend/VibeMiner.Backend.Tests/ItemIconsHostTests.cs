using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemIconsHostTests
{
    private static readonly ItemsContext Context = new("wow", new string('a', 64), "us", "enUS");
    private static readonly string Feature = new('b', 64), Image = new('c', 64);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };

    [Fact]
    public async Task Batch_outcomes_preserve_order_duplicates_and_independent_statuses_with_correlated_progress()
    {
        var store = new Store(); await using var icons = new ItemIconsService(store); await using var leases = new ItemIconLeases(icons);
        var replies = await Run(icons, leases, Request("hello", "hello", new { }),
            Request("batch", "items.icons", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Feature, itemIds = new[] { "7", "0", "7" } }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "item-icons-v1");
        var result = replies["batch"].GetProperty("result");
        Assert.Equal(Feature, result.GetProperty("artifactId").GetString());
        var entries = result.GetProperty("outcomes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "7", "0", "7" }, entries.Select(value => value.GetProperty("itemId").GetString()));
        Assert.Equal(new[] { "ready", "noIcon", "ready" }, entries.Select(value => value.GetProperty("status").GetString()));
        Assert.Equal(Image, entries[0].GetProperty("image").GetProperty("artifactId").GetString());
        Assert.Equal(JsonValueKind.Null, entries[1].GetProperty("image").ValueKind);
        Assert.Equal(1, store.Prepares);
    }

    [Fact]
    public async Task Open_returns_only_verified_identity_and_close_retires_it_idempotently()
    {
        var store = new Store(); await using var icons = new ItemIconsService(store); await using var leases = new ItemIconLeases(icons);
        var opened = (await Run(icons, leases, Request("open", "items.icon.open", new
        {
            Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
            artifactId = Feature, itemId = "7", imageArtifactId = Image
        })))["open"].GetProperty("result");
        Assert.Equal(new[] { "artifactId", "byteLength", "height", "leaseId", "sha256", "width" }, opened.EnumerateObject().Select(value => value.Name).Order());
        Assert.Equal(Image, opened.GetProperty("artifactId").GetString()); Assert.Equal("16", opened.GetProperty("byteLength").GetString());
        var leaseId = opened.GetProperty("leaseId").GetString()!; Assert.Matches("^[0-9a-f]{32}$", leaseId);
        Assert.Equal(new ItemIconReadRequest(Context, Feature, "7", Image), store.LastRead);
        Assert.False(store.Lease.Disposed);
        var replies = await Run(icons, leases, Request("close", "items.icon.close", new { leaseId }), Request("again", "items.icon.close", new { leaseId }));
        Assert.All(replies.Values, reply => Assert.True(reply.GetProperty("result").GetProperty("closed").GetBoolean()));
        Assert.True(store.Lease.Disposed); Assert.Equal(1, store.Lease.Disposals);
    }

    [Fact]
    public async Task Invalid_or_arbitrary_asset_authority_is_refused_before_storage()
    {
        var store = new Store(); await using var icons = new ItemIconsService(store); await using var leases = new ItemIconLeases(icons);
        var replies = await Run(icons, leases,
            Request("url", "items.icons", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Feature, itemIds = new[] { "7" }, url = "https://example.org/icon" }),
            Request("numeric", "items.icons", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Feature, itemIds = new[] { 7 } }),
            Request("limit", "items.icons", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Feature, itemIds = Enumerable.Repeat("7", 51).ToArray() }),
            Request("hash", "items.icon.open", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Feature, itemId = "7", imageArtifactId = Image, sha256 = new string('d', 64) }),
            Request("bad-close", "items.icon.close", new { leaseId = "../image" }));
        Assert.All(replies.Values, reply => Assert.Equal("INVALID_REQUEST", reply.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(0, store.Prepares + store.Opens);
    }

    [Fact]
    public async Task Missing_image_service_is_not_advertised_or_simulated()
    {
        var replies = await Run(null, null, Request("hello", "hello", new { }), Request("icons", "items.icons", new { }));
        Assert.DoesNotContain(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), value => value.GetString() == "item-icons-v1");
        Assert.Equal("ITEM_ICONS_UNAVAILABLE", replies["icons"].GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<Dictionary<string, JsonElement>> Run(ItemIconsService? icons, ItemIconLeases? leases, params object[] requests)
    {
        var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), itemIcons: icons, iconLeases: leases);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var output = new TerminalOutput(requests.Length);
        using var input = new TerminalInput(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, json))) + "\n"), output.Completed);
        await host.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        var frames = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToArray();
        foreach (var progress in frames.Where(frame => frame.TryGetProperty("type", out _)))
        {
            Assert.Equal("progress", progress.GetProperty("type").GetString()); Assert.Equal("batch", progress.GetProperty("requestId").GetString());
            Assert.Equal(3, progress.GetProperty("total").GetInt32()); Assert.Equal(1, progress.GetProperty("sequence").GetInt32());
        }
        return frames.Where(frame => frame.TryGetProperty("id", out _)).ToDictionary(reply => reply.GetProperty("id").GetString()!);
    }
    // EOF means application shutdown. Keep the connection open until every request
    // has a terminal reply, rather than racing asynchronous production against EOF.
    private sealed class TerminalInput(byte[] bytes, Task terminal) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position != Length) return await base.ReadAsync(buffer, token);
            await terminal.WaitAsync(token); return 0;
        }
    }
    private sealed class TerminalOutput(int expected) : MemoryStream
    {
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => completed.Task;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            await base.WriteAsync(bytes, token);
            var text = Encoding.UTF8.GetString(ToArray()); var end = text.LastIndexOf('\n');
            if (end < 0) return;
            var replies = 0;
            foreach (var line in text[..(end + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var frame = JsonDocument.Parse(line);
                if (frame.RootElement.TryGetProperty("id", out _)) replies++;
            }
            if (replies == expected) completed.TrySetResult();
        }
    }
    private sealed class Store : IItemIconsStore
    {
        internal int Prepares, Opens; internal ItemIconReadRequest? LastRead; internal readonly Lease Lease = new();
        public async Task<ItemIconBatch> PrepareBatchAsync(ItemIconBatchRequest request, Func<ItemIconProgress, Task> progress, CancellationToken token)
        {
            Prepares++; await Task.Yield(); token.ThrowIfCancellationRequested();
            await progress(new("Resolving visible icons", "objects", 3, 3));
            return new(request.Context, request.ArtifactId, request.ItemIds.Select(id => id == "0"
                ? new ItemIconOutcome(id, "0", ItemIconStatus.NoIcon)
                : new ItemIconOutcome(id, "132759", ItemIconStatus.Ready, new(Image, 2, 2, "16"))).ToArray());
        }
        public Task<IItemIconReadLease> OpenAsync(ItemIconReadRequest request, CancellationToken token)
        { Opens++; LastRead = request; return Task.FromResult<IItemIconReadLease>(Lease); }
    }
    private sealed class Lease : IItemIconReadLease
    {
        internal bool Disposed; internal int Disposals;
        public Stream Content { get; } = new MemoryStream(new byte[16]);
        public string ArtifactId => Image; public string Sha256 => new('d', 64); public long ByteLength => 16;
        public int Width => 2; public int Height => 2;
        public async ValueTask DisposeAsync() { Disposed = true; Disposals++; await Content.DisposeAsync(); }
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
