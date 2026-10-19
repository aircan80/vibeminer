using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TablesServiceTests
{
    [Theory]
    [InlineData(0, null, "")]
    [InlineData(101, null, "")]
    [InlineData(50, "01", "")]
    [InlineData(50, "-1", "")]
    [InlineData(50, "2147483648", "")]
    [InlineData(50, null, "Item\nClass")]
    public async Task Invalid_queries_fail_before_any_provider_work(int limit, string? cursor, string query)
    {
        var provider = new GuardedProvider(); var service = new TablesService(provider);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadCatalogAsync(
            new("wow", new string('a', 64), "us", query, limit, cursor)));
        Assert.Equal("INVALID_REQUEST", error.Code); Assert.Equal(0, provider.Calls);
    }

    [Theory]
    [InlineData("../Item.dbd")]
    [InlineData("50238EC2")]
    [InlineData("50238ec20")]
    [InlineData("")]
    public async Task Definition_identity_is_a_canonical_provider_key_not_a_path_or_name(string id)
    {
        var provider = new GuardedProvider(); var service = new TablesService(provider);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadDefinitionAsync(
            new("wow", new string('a', 64), "us", id)));
        Assert.Equal("INVALID_REQUEST", error.Code); Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Cancellation_and_literal_search_reach_the_provider_without_becoming_a_query_language()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var provider = new GuardedProvider { ExpectedToken = cancel.Token };
        var service = new TablesService(provider);
        var request = new TableCatalogRequest("wow", new string('a', 64), "us", "Item%' OR 1=1", 7, "0");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadCatalogAsync(request, cancel.Token));
        Assert.Equal(request, provider.Seen); Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Oversized_search_and_forged_context_are_rejected_before_provider_work()
    {
        var provider = new GuardedProvider(); var service = new TablesService(provider);
        foreach (var request in new[] { new TableCatalogRequest("wow", new string('a',64), "us", new string('x',81)),
            new TableCatalogRequest("../wow", new string('a',64), "us"),
            new TableCatalogRequest("wow", "123", "us"), new TableCatalogRequest("wow", new string('a',64), "../us") })
            await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadCatalogAsync(request));
        Assert.Equal(0, provider.Calls);
    }

    private sealed class GuardedProvider : ITablesCatalog
    {
        public int Calls;
        public CancellationToken ExpectedToken;
        public TableCatalogRequest? Seen;
        public Task<TableCatalogPage> ReadCatalogAsync(TableCatalogRequest request, CancellationToken token)
        {
            Calls++; Seen = request; Assert.Equal(ExpectedToken, token); token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unexpected provider admission.");
        }
        public Task<TableDefinitionSnapshot> ReadDefinitionAsync(TableDefinitionRequest request, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Invalid definition must not reach the provider."); }
    }
}
