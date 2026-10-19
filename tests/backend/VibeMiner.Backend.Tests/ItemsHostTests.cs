using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemsHostTests
{
    private static readonly ItemsContext Context = new("wow", new string('a', 64), "us", "enUS");
    private static readonly string Artifact = new('b', 64);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static object Scope => new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale };

    [Fact]
    public async Task Explicit_name_states_exact_IDs_literal_query_and_raw_text_evidence_cross_the_host()
    {
        var store = new Store(); await using var items = new ItemsService(store);
        const string literal = "%_\\ Épée";
        var replies = await Run(items,
            Request("hello", "hello", new { }), Request("local", "items.data", Scope),
            Request("page", "items.page", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Artifact, query = literal, limit = 50 }),
            Request("detail", "items.inspect", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Artifact, itemId = "4294967295" }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "items-v1");
        Assert.Equal(JsonValueKind.Null, replies["local"].GetProperty("result").GetProperty("snapshot").ValueKind);
        var page = replies["page"].GetProperty("result"); Assert.Equal(literal, page.GetProperty("query").GetString());
        Assert.Equal(literal, store.LastPage!.Query);
        var entries = page.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(new[] { "valid", "empty", "invalid", "missingSparse" }, entries.Select(item => item.GetProperty("name").GetProperty("state").GetString()));
        Assert.Equal("4294967295", entries[^1].GetProperty("id").GetString());
        Assert.Equal("", entries[1].GetProperty("name").GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("name").GetProperty("value").ValueKind);
        var detail = replies["detail"].GetProperty("result").GetProperty("detail");
        Assert.Equal("valid", detail.GetProperty("summary").GetProperty("name").GetProperty("state").GetString());
        Assert.Equal("c328", detail.GetProperty("sparseRow").GetProperty("textIssues")[0].GetProperty("rawBytesHex").GetString());
        Assert.Equal("4294967295", detail.GetProperty("itemRow").GetProperty("id").GetString());
        Assert.Equal(0, store.Prepares); Assert.Equal(1, store.Pages); Assert.Equal(1, store.Inspects);
    }

    [Fact]
    public async Task Invalid_search_modes_parameter_types_and_limits_do_not_reach_storage()
    {
        var store = new Store(); await using var items = new ItemsService(store);
        var replies = await Run(items,
            Request("mixed", "items.page", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Artifact, query = "123", exactItemId = "123" }),
            Request("limit", "items.page", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Artifact, query = "", limit = 51 }),
            Request("numeric", "items.inspect", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale,
                artifactId = Artifact, itemId = 7 }),
            Request("extra", "items.data", new { Context.ProductId, Context.BuildIdentity, Context.SourceRegion, Context.ResearchLocale, force = true }));
        Assert.All(replies.Values, reply => Assert.Equal("INVALID_REQUEST", reply.GetProperty("error").GetProperty("code").GetString()));
        Assert.Equal(0, store.Pages + store.Inspects + store.Prepares);
    }

    [Fact]
    public async Task Missing_feature_is_not_advertised_and_cannot_fabricate_a_page()
    {
        var replies = await Run(null, Request("hello", "hello", new { }), Request("items", "items.data", Scope));
        Assert.DoesNotContain(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "items-v1");
        Assert.Equal("ITEMS_UNAVAILABLE", replies["items"].GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Escaped_unpaired_surrogates_are_invalid_requests_before_storage()
    {
        var store = new Store(); await using var items = new ItemsService(store);
        var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), items: items);
        var scope = $"\"productId\":\"wow\",\"buildIdentity\":\"{Context.BuildIdentity}\",\"sourceRegion\":\"us\",\"researchLocale\":\"enUS\",\"artifactId\":\"{Artifact}\"";
        var frames = new[]
        {
            "{\"protocolVersion\":1,\"id\":\"query\",\"method\":\"items.page\",\"params\":{" + scope + ",\"query\":\"\\uD800\"}}",
            "{\"protocolVersion\":1,\"id\":\"item\",\"method\":\"items.inspect\",\"params\":{" + scope + ",\"itemId\":\"\\uDC00\"}}"
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', frames) + "\n"));
        using var output = new MemoryStream(); await host.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        var replies = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, replies.Length);
        foreach (var line in replies)
        { using var reply = JsonDocument.Parse(line); Assert.Equal("INVALID_REQUEST", reply.RootElement.GetProperty("error").GetProperty("code").GetString()); }
        Assert.Equal(0, store.Pages + store.Inspects + store.Prepares);
    }

    private static async Task<Dictionary<string, JsonElement>> Run(ItemsService? items, params object[] requests)
    {
        var host = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), items: items);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', requests.Select(request => JsonSerializer.Serialize(request, json))) + "\n"));
        using var output = new MemoryStream(); await host.RunAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .ToDictionary(reply => reply.GetProperty("id").GetString()!);
    }
    private sealed class Store : IItemsStore
    {
        internal int Pages, Inspects, Prepares; internal ItemsPageRequest? LastPage;
        private static ItemSummary Summary(string id, ItemNameState state, string? name) => new(id, new(state, name), [], null);
        public Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token) =>
            Task.FromResult(new ItemsReadiness(null, [new("50238ec2", "notPrepared", null), new("919be54e", "notPrepared", null)]));
        public Task<ItemsSnapshot> PrepareAsync(ItemsContext context, Func<ItemsPreparationProgress, Task> progress, CancellationToken token)
        { Prepares++; throw new InvalidOperationException("Local reads must not prepare data."); }
        public Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token)
        {
            Pages++; LastPage = request;
            return Task.FromResult(new ItemsPage(request.Context, request.ArtifactId, request.Query, request.ExactItemId,
                [Summary("0", ItemNameState.Valid, "Authored name"), Summary("7", ItemNameState.Empty, ""),
                    Summary("8", ItemNameState.Invalid, null), Summary("4294967295", ItemNameState.MissingSparse, null)], null));
        }
        public Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token)
        {
            Inspects++;
            return Task.FromResult(new ItemInspectResult(request.Context, request.ArtifactId, request.ItemId,
                new(Summary(request.ItemId, ItemNameState.Valid, "Authored usable name"),
                    new(request.ItemId, false, null, [request.ItemId, "17"]),
                    new(request.ItemId, false, null, [request.ItemId, ""], [new(1, "DB2_TEXT_ENCODING_INVALID", "c328")]))));
        }
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
