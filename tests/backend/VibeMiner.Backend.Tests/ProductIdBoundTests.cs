using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

// Issue #42: a saved product was bounded to 16 bytes on the pinned open path, so the 17- and 19-byte
// listed products failed with CATALOG_INTEGRITY right after config verification.
public sealed class ProductIdBoundTests
{
    public static TheoryData<string> ListedProducts() => new(ProductRegistry.Products.Select(product => product.Id));

    [Fact]
    public void TheBoundIsTheLongestListedProductAndEveryIdFitsTheWireContract()
    {
        Assert.Equal(ProductRegistry.Products.Max(p => Encoding.UTF8.GetByteCount(p.Id)), ProductRegistry.MaxIdBytes);
        Assert.True(ProductRegistry.MaxIdBytes >= Encoding.UTF8.GetByteCount("wow_classic_era_ptr"));
        var schema = JsonNode.Parse(File.ReadAllText(RepositoryPaths.File("contracts/discovery.schema.json")))!;
        var patterns = ProductPatterns(schema).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(patterns);
        foreach (var product in ProductRegistry.Products)
        {
            Assert.True(product.Id.All(char.IsAscii), product.Id);
            foreach (var pattern in patterns)
                Assert.True(Regex.IsMatch(product.Id, pattern, RegexOptions.CultureInvariant), $"{product.Id} fails {pattern}");
        }
    }

    [Theory]
    [InlineData("wow_classic_era_ptr")]
    [InlineData("wow_classic_titan")]
    public async Task ALongListedProductOpensAndResolvesItsPinnedObservation(string product)
    {
        Assert.True(Encoding.UTF8.GetByteCount(product) > 16, "this regression needs a product longer than the old bound");
        await using var h = await Harness.CreateAsync(product);
        var opened = await h.OpenAsync();
        Assert.Equal(product, opened.Build.ProductId);
        Assert.Equal(h.Request.BuildIdentity, opened.Build.Id);
        var current = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        var pinned = await h.Catalog.ResolveOpeningAsync(h.Request, current.Reference, default);
        Assert.Equal(product, pinned.Build.ProductId);
        Assert.Equal(current.Reference, pinned.Reference);
        Assert.Equal(current.Build.CdnConfig, pinned.Build.CdnConfig);
    }

    [Theory]
    [MemberData(nameof(ListedProducts))]
    public async Task EveryListedProductPassesTheOpenPathChecks(string product)
    {
        WorkspaceService.Validate(new(product, new string('a', 64), "us"));
        await using var h = await Harness.CreateAsync(product);
        var opened = await h.OpenAsync();
        Assert.Equal(product, opened.Build.ProductId);
        var current = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        var pinned = await h.Catalog.ResolveOpeningAsync(h.Request, current.Reference, default);
        Assert.Equal(product, pinned.Build.ProductId);
    }

    [Fact]
    public async Task APinnedObservationWhoseSavedProductExceedsTheBoundIsStillRefused()
    {
        await using var h = await Harness.CreateAsync("wow");
        await h.OpenAsync();
        var current = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        await h.ExecuteAsync($"UPDATE events SET product='{new string('w', ProductRegistry.MaxIdBytes + 1)}' WHERE generation={current.Reference.Generation}");
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ResolveOpeningAsync(h.Request, current.Reference, default));
        Assert.Equal("CATALOG_INTEGRITY", error.Code);
    }

    private static IEnumerable<string> ProductPatterns(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (key == "productId" && value?["pattern"] is JsonValue pattern) yield return pattern.GetValue<string>();
                foreach (var nested in ProductPatterns(value)) yield return nested;
            }
        else if (node is JsonArray array)
            foreach (var item in array)
                foreach (var nested in ProductPatterns(item)) yield return nested;
    }

    private sealed class Responder(InventoryFixture data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            if (!data.Responses.TryGetValue(url, out var bytes))
                throw new InvalidOperationException("Unexpected authored source request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes.ToArray()) });
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-product-" + Guid.NewGuid().ToString("N"));
        private HttpClient client = null!;
        private VerifiedObjectStore objects = null!;
        private DownloadWorkspacePreparer preparer = null!;
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        public WorkspaceRequest Request { get; private set; } = null!;

        // The authored fixture is "wow"; only the product, its discovery receipt URLs and its identity change.
        public static async Task<Harness> CreateAsync(string product)
        {
            var data = InventoryFixture.Create(); var original = data.Discovery;
            var row = original.Observations.Single();
            var identity = BuildIdentity.Create(product, row.OriginalFields);
            var discovery = original with
            {
                Documents = original.Documents.Select(d => d with { Url = $"https://us.version.battle.net/v2/products/{product}/{d.Role}" }).ToArray(),
                Observations = [row with { Identity = identity }]
            };
            var h = new Harness { Request = new(product, identity.Id, "us") };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.objects = new(h.directory);
            h.client = new(new Responder(data)); h.preparer = new(h.Catalog, h.objects, h.client);
            var generation = await h.Catalog.BeginRefreshAsync(product, default);
            await h.Catalog.CommitAsync(product, generation, discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }

        public Task<WorkspaceSnapshot> OpenAsync() => preparer.PrepareWithProgressAsync(Request, _ => Task.CompletedTask, default);

        // Writes outside the catalog stand in for corrupt storage, so foreign keys are off here.
        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory, "discovery-v1.sqlite3"), Pooling = false, ForeignKeys = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            preparer.Dispose(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
