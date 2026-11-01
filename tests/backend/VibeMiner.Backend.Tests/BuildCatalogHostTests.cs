using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using VibeMiner.Composition;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary><c>catalog.list</c> and <c>catalog.refresh</c> (build-catalog-v1, #205) over layer 3 fakes, and once over a real store.</summary>
public sealed class BuildCatalogHostTests
{
    private const string Header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly byte[] Cdns = Encoding.UTF8.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0\n## seqn = 1\nus|tpr/wow|cdn.example.invalid\n");
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), K = new('e', 32), P = new('f', 32);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static CancellationToken Ct => CancellationToken.None;

    private readonly Clock clock = new();
    private readonly InMemoryObjectSource source;
    public BuildCatalogHostTests() => source = new InMemoryObjectSource { Time = clock };

    private static byte[] Versions(int seqn, params (string Region, string Build, string Name)[] rows) =>
        Encoding.UTF8.GetBytes($"{Header}\n## seqn = {seqn}\n" + string.Concat(rows.Select(r =>
            $"{r.Region}|{r.Build}|{new string('d', 32)}|{K}|{r.Name.Split('.')[^1]}|{r.Name}|{P}\n")));

    /// <summary>Appends one us-host observation to the history log, then moves the clock a minute.</summary>
    private async Task ObserveAsync(string product, ObservationKind kind, byte[] bytes)
    {
        source.AddReply(product, "us", kind, bytes);
        Assert.True((await source.ObserveAsync(new(kind, product, "us"), Ct)).Recorded);
        clock.Now += TimeSpan.FromMinutes(1);
    }

    private static JsonElement ProductOf(JsonElement catalog, string id) =>
        catalog.GetProperty("products").EnumerateArray().Single(p => p.GetProperty("productId").GetString() == id);

    [Fact]
    public async Task HelloAdvertisesTheCatalogOnlyWhenComposed()
    {
        await using (var plain = await Session.Start(null))
        {
            await plain.Send("hello", "hello", new { });
            Assert.DoesNotContain("build-catalog-v1", (await plain.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
            await plain.Send("list", "catalog.list", new { });
            Assert.Equal("UNKNOWN_METHOD", (await plain.Receive()).GetProperty("error").GetProperty("code").GetString());
        }
        await using var service = new BuildCatalogService(new FakeStores(source));
        await using var session = await Session.Start(service);
        await session.Send("hello", "hello", new { });
        Assert.Contains("build-catalog-v1", (await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task ParametersMustBeEmpty()
    {
        await using var service = new BuildCatalogService(new FakeStores(source));
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { productId = "wow" });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        await session.Send("refresh", "catalog.refresh", new { force = true });
        Assert.Equal("INVALID_REQUEST", (await session.Receive()).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(source.Observed);
    }

    [Fact]
    public async Task ListProjectsEveryReviewedProductWithCurrentRegionsOnly()
    {
        // seqn 1: A in us and eu, C in kr and tw. seqn 2 (the current reply): B in us, A still in eu.
        await ObserveAsync("wow", ObservationKind.Versions, Versions(1, ("us", A, "11.0.0.1001"), ("eu", A, "11.0.0.1001"), ("kr", C, "10.9.0.999"), ("tw", C, "10.9.0.999")));
        var newest = clock.Now;
        await ObserveAsync("wow", ObservationKind.Versions, Versions(2, ("us", B, "11.0.0.1002"), ("eu", A, "11.0.0.1001")));
        var observed = source.Observed.Count;
        await using var service = new BuildCatalogService(new FakeStores(source));
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { });
        var catalog = (await session.Receive()).GetProperty("result");

        Assert.Equal("writer", catalog.GetProperty("access").GetString());
        Assert.Equal(JsonValueKind.Null, catalog.GetProperty("summary").ValueKind);
        var products = catalog.GetProperty("products").EnumerateArray().ToArray();
        Assert.Equal(Products.Reviewed.Select(p => p.Id), products.Select(p => p.GetProperty("productId").GetString()));
        Assert.Equal(Products.Reviewed.Select(p => p.Label), products.Select(p => p.GetProperty("label").GetString()));
        Assert.Equal("PTR 2", ProductOf(catalog, "wowxptr").GetProperty("label").GetString());

        var wow = ProductOf(catalog, "wow");
        Assert.Equal(3, wow.GetProperty("buildCount").GetInt32());
        Assert.Equal(newest, DateTimeOffset.Parse(wow.GetProperty("lastObservedAt").GetString()!));
        Assert.Equal(0, wow.GetProperty("problemCount").GetInt32());
        var builds = wow.GetProperty("builds").EnumerateArray().ToArray();
        Assert.Equal("11.0.0.1002", builds[0].GetProperty("versionsName").GetString());
        JsonElement Build(string name) => builds.Single(b => b.GetProperty("versionsName").GetString() == name);
        string[] Regions(string name) => Build(name).GetProperty("regions").EnumerateArray().Select(r => r.GetString()!).ToArray();
        Assert.True(Build("11.0.0.1002").GetProperty("current").GetBoolean());
        Assert.Equal(["us"], Regions("11.0.0.1002"));
        // A current build shows only its current listings: A is no longer listed in us.
        Assert.True(Build("11.0.0.1001").GetProperty("current").GetBoolean());
        Assert.Equal(["eu"], Regions("11.0.0.1001"));
        Assert.False(Build("10.9.0.999").GetProperty("current").GetBoolean());
        Assert.Equal(["kr", "tw"], Regions("10.9.0.999"));
        Assert.Equal(C, Build("10.9.0.999").GetProperty("buildConfig").GetString());
        Assert.Equal(64, Build("10.9.0.999").GetProperty("id").GetString()!.Length);
        var first = Build("11.0.0.1001").GetProperty("firstSeenAt").GetString()!;
        Assert.True(first.EndsWith("+00:00", StringComparison.Ordinal) || first.EndsWith('Z'), first);
        Assert.Equal(newest - TimeSpan.FromMinutes(1), DateTimeOffset.Parse(first));

        // No versions history at all.
        var ptr = ProductOf(catalog, "wowt");
        Assert.Equal(JsonValueKind.Null, ptr.GetProperty("lastObservedAt").ValueKind);
        Assert.Equal(0, ptr.GetProperty("buildCount").GetInt32());
        Assert.Empty(ptr.GetProperty("builds").EnumerateArray());
        // Local only: nothing asked of the version service.
        Assert.Equal(observed, source.Observed.Count);
    }

    [Fact]
    public async Task ListReportsTheNewestStoredSummary()
    {
        await ObserveAsync("", ObservationKind.Summary, Encoding.UTF8.GetBytes(
            "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 42\nwow|1|\nwowz|3|\nagent|4|\nwow_classic_era_beta|5|cdn\n"));
        await using var service = new BuildCatalogService(new FakeStores(source));
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { });
        var summary = (await session.Receive()).GetProperty("result").GetProperty("summary");
        Assert.True(summary.GetProperty("readable").GetBoolean());
        Assert.Equal(["wow_classic_era_beta", "wowz"], summary.GetProperty("newProducts").EnumerateArray().Select(p => p.GetString()));

        var unreadableAt = clock.Now;
        await ObserveAsync("", ObservationKind.Summary, "not a table\n"u8.ToArray());
        await session.Send("again", "catalog.list", new { });
        summary = (await session.Receive()).GetProperty("result").GetProperty("summary");
        Assert.False(summary.GetProperty("readable").GetBoolean());
        Assert.Empty(summary.GetProperty("newProducts").EnumerateArray());
        Assert.Equal(unreadableAt, DateTimeOffset.Parse(summary.GetProperty("observedAt").GetString()!));
    }

    [Fact]
    public async Task ListCapsBuildsAndProblemsPerProduct()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = new ListedCatalog(product => product == "wow"
            ? new BuildList(product, [.. Enumerable.Range(0, 150).Select(i => Synthetic(product, i, "11.0.0." + i, 1, at))],
                [.. Enumerable.Range(0, 25).Select(i => new CatalogProblem("VERSIONS_ROW_INVALID", $"Row {i} can't be read."))], at)
            : new BuildList(product, [], [], null));
        await using var service = new BuildCatalogService(new FakeStores(source, () => catalog));
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { });
        var wow = ProductOf((await session.Receive()).GetProperty("result"), "wow");
        Assert.Equal(100, wow.GetProperty("builds").GetArrayLength());
        Assert.Equal(150, wow.GetProperty("buildCount").GetInt32());
        Assert.Equal("11.0.0.0", wow.GetProperty("builds")[0].GetProperty("versionsName").GetString());
        Assert.Equal(20, wow.GetProperty("problems").GetArrayLength());
        Assert.Equal(25, wow.GetProperty("problemCount").GetInt32());
        Assert.Equal("Row 0 can't be read.", wow.GetProperty("problems")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ListStopsAddingBuildsAtTheBuildsBudget()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        // About 1 KB a build: a 128-char name and 64 regions. 13 products of 100 builds would be about 1.3 MB.
        var catalog = new ListedCatalog(product => new BuildList(product,
            [.. Enumerable.Range(0, 100).Select(i => Synthetic(product, i, new string('9', 120) + "." + i.ToString("D7"), 64, at))], [], at));
        await using var service = new BuildCatalogService(new FakeStores(source, () => catalog));
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { });
        var frame = await session.Receive();
        var products = frame.GetProperty("result").GetProperty("products").EnumerateArray().ToArray();
        Assert.Equal(13, products.Length);
        Assert.All(products, p => Assert.Equal(100, p.GetProperty("buildCount").GetInt32()));
        Assert.Equal(100, products[0].GetProperty("builds").GetArrayLength());
        Assert.Equal(0, products[^1].GetProperty("builds").GetArrayLength());
        var bytes = products.Sum(p => p.GetProperty("builds").EnumerateArray().Sum(b => Encoding.UTF8.GetByteCount(b.GetRawText()) + 1));
        Assert.InRange(bytes, BuildCatalogWire.BuildsBudgetBytes - 2000, BuildCatalogWire.BuildsBudgetBytes);
        Assert.Equal(64, products[0].GetProperty("builds")[0].GetProperty("regions").GetArrayLength());
    }

    /// <summary>Every product at its caps with escape-heavy text: 100 large builds, 25 problems of 512 apostrophes (6 bytes each as JSON).</summary>
    private static ListedCatalog WorstCase()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var message = new string('\'', 512);
        var problems = Enumerable.Range(0, 25).Select(_ => new CatalogProblem("VERSIONS_ROW_INVALID", message)).ToArray();
        var failed = new ObservationResult(ObjectState.Unavailable, Code: "CDN_OFFLINE", Message: message);
        return new ListedCatalog(product => new BuildList(product,
            [.. Enumerable.Range(0, 100).Select(i => Synthetic(product, i, new string('9', 120) + "." + i.ToString("D7"), 64, at))], problems, at), failed);
    }

    /// <summary>The schema's bounds on a BuildCatalog that the frame budget could break.</summary>
    private static void AssertCatalogShape(JsonElement catalog)
    {
        var products = catalog.GetProperty("products").EnumerateArray().ToArray();
        Assert.Equal(Products.Reviewed.Count, products.Length);
        foreach (var product in products)
        {
            Assert.InRange(product.GetProperty("builds").GetArrayLength(), 0, Math.Min(100, product.GetProperty("buildCount").GetInt32()));
            Assert.InRange(product.GetProperty("problems").GetArrayLength(), 0, Math.Min(20, product.GetProperty("problemCount").GetInt32()));
            foreach (var problem in product.GetProperty("problems").EnumerateArray())
            {
                Assert.Matches("^[A-Z0-9_]{1,64}$", problem.GetProperty("code").GetString());
                Assert.InRange(problem.GetProperty("message").GetString()!.Length, 1, 512);
            }
        }
    }

    [Fact]
    public async Task WorstCaseListFitsTheFrame()
    {
        await using var service = new BuildCatalogService(new FakeStores(source, WorstCase));
        await using var session = await Session.Start(service);
        var id = new string('\'', 128);
        await session.Send(id, "catalog.list", new { });
        var line = await session.ReceiveLine();
        Assert.InRange(Encoding.UTF8.GetByteCount(line) + 1, 0, ProtocolHost.MaxFrameBytes);
        using var document = JsonDocument.Parse(line);
        var frame = document.RootElement;
        Assert.Equal(id, frame.GetProperty("id").GetString());
        var catalog = frame.GetProperty("result");
        AssertCatalogShape(catalog);
        var products = catalog.GetProperty("products").EnumerateArray().ToArray();
        Assert.All(products, p => Assert.Equal(25, p.GetProperty("problemCount").GetInt32()));
        // Builds go first, from the last products; the problems alone (about 0.8 MB) then fit, so every product keeps 20.
        Assert.All(products, p => Assert.Equal(20, p.GetProperty("problems").GetArrayLength()));
        Assert.Equal(0, products[^1].GetProperty("builds").GetArrayLength());
        Assert.Equal(100, products[0].GetProperty("builds").GetArrayLength());
    }

    [Fact]
    public async Task FittingDropsBuildsThenProblemsFromTheLastProducts()
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var catalog = new ListedCatalog(product => new BuildList(product, [.. Enumerable.Range(0, 3).Select(i => Synthetic(product, i, "11.0.0." + i, 1, at))],
            [.. Enumerable.Range(0, 3).Select(i => new CatalogProblem("VERSIONS_ROW_INVALID", new string('\'', 100)))], at));
        await using var handle = await new FakeStores(source, () => catalog).OpenWriterAsync(Ct);
        var parts = await BuildCatalogWire.CatalogAsync(handle, writer: true, Ct);
        var empty = JsonSerializer.SerializeToUtf8Bytes(BuildCatalogWire.Fit(parts with { Products = [.. parts.Products.Select(p => p with { Builds = [], Problems = [] })] }, int.MaxValue), Json).Length;
        // Room for about five problems and nothing else.
        var limit = empty + 5 * 620;
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(BuildCatalogWire.Fit(parts, limit), Json));
        Assert.InRange(document.RootElement.GetRawText().Length, 0, limit);
        var products = document.RootElement.GetProperty("products").EnumerateArray().ToArray();
        Assert.All(products, p => Assert.Empty(p.GetProperty("builds").EnumerateArray()));
        Assert.All(products, p => Assert.Equal(3, p.GetProperty("buildCount").GetInt32()));
        Assert.All(products, p => Assert.Equal(3, p.GetProperty("problemCount").GetInt32()));
        Assert.Equal(3, products[0].GetProperty("problems").GetArrayLength());
        Assert.Equal(0, products[^1].GetProperty("problems").GetArrayLength());
    }

    [Fact]
    public async Task WorstCaseRefreshFitsTheFrame()
    {
        await using var service = new BuildCatalogService(new FakeStores(source, WorstCase));
        await using var session = await Session.Start(service);
        var id = new string('\'', 128);
        await session.Send(id, "catalog.refresh", new { });
        string line;
        while ((line = await session.ReceiveLine()).Contains("\"type\":\"progress\"", StringComparison.Ordinal)) { }
        Assert.InRange(Encoding.UTF8.GetByteCount(line) + 1, 0, ProtocolHost.MaxFrameBytes);
        using var document = JsonDocument.Parse(line);
        var result = document.RootElement.GetProperty("result");
        // Versions and cdns for every product, and the summary: all of them, each with a 512-char message.
        var failures = result.GetProperty("failures").EnumerateArray().ToArray();
        Assert.Equal(13 * 2 + 1, failures.Length);
        Assert.All(failures, f => Assert.Equal(512, f.GetProperty("message").GetString()!.Length));
        Assert.Empty(result.GetProperty("newProducts").EnumerateArray());
        AssertCatalogShape(result.GetProperty("catalog"));
    }

    [Fact]
    public async Task RefreshReportsOrderedProgressAndOnlyRealFailures()
    {
        await ObserveAsync("wowt", ObservationKind.Versions, Versions(1, ("us", B, "11.1.0.2000")));
        source.AddReply("wow", "us", ObservationKind.Versions, Versions(5, ("us", A, "11.0.0.1001"))).AddReply("wow", "us", ObservationKind.Cdns, Cdns)
            .FailObservation("wowt", "us", ObservationKind.Versions, ObjectState.Unavailable, "CDN_OFFLINE", "The version service can't be reached.")
            .AddReply("", "us", ObservationKind.Summary, "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 7\nwow|1|\nwowz|3|\n"u8.ToArray());
        await using var service = new BuildCatalogService(new FakeStores(source), clock);
        await using var session = await Session.Start(service);
        await session.Send("refresh", "catalog.refresh", new { });
        var completed = new List<int>(); var sequences = new List<long>();
        JsonElement terminal;
        while (true)
        {
            var frame = await session.Receive();
            if (!frame.TryGetProperty("type", out _)) { terminal = frame; break; }
            Assert.Equal("refresh", frame.GetProperty("requestId").GetString());
            Assert.Equal(BuildCatalogService.ProgressStage, frame.GetProperty("stage").GetString());
            Assert.Equal("products", frame.GetProperty("unit").GetString());
            Assert.Equal(JsonValueKind.Null, frame.GetProperty("region").ValueKind);
            Assert.Equal(Products.Reviewed.Count + 1, frame.GetProperty("total").GetInt32());
            completed.Add(frame.GetProperty("completed").GetInt32()); sequences.Add(frame.GetProperty("sequence").GetInt64());
        }
        Assert.Equal(Enumerable.Range(0, Products.Reviewed.Count + 2), completed);
        Assert.Equal(Enumerable.Range(1, Products.Reviewed.Count + 2).Select(i => (long)i), sequences);

        var result = terminal.GetProperty("result");
        Assert.Equal(13, result.GetProperty("checked").GetInt32());
        // wowt's versions are offline; every other unanswered document is NotFound, an answer.
        var failure = Assert.Single(result.GetProperty("failures").EnumerateArray());
        Assert.Equal("wowt", failure.GetProperty("productId").GetString());
        Assert.Equal("versions", failure.GetProperty("document").GetString());
        Assert.Equal("unavailable", failure.GetProperty("state").GetString());
        Assert.Equal("CDN_OFFLINE", failure.GetProperty("code").GetString());
        Assert.Equal("The version service can't be reached.", failure.GetProperty("message").GetString());
        Assert.Equal(["wowz"], result.GetProperty("newProducts").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(clock.Now, DateTimeOffset.Parse(result.GetProperty("refreshedAt").GetString()!));

        var catalog = result.GetProperty("catalog");
        Assert.Equal("writer", catalog.GetProperty("access").GetString());
        Assert.Equal("11.0.0.1001", ProductOf(catalog, "wow").GetProperty("builds")[0].GetProperty("versionsName").GetString());
        // The offline product still serves its stored list.
        Assert.Equal("11.1.0.2000", ProductOf(catalog, "wowt").GetProperty("builds")[0].GetProperty("versionsName").GetString());
        Assert.Equal(["wowz"], catalog.GetProperty("summary").GetProperty("newProducts").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(1 + 13 * 2 + 1, source.Observed.Count);
    }

    [Fact]
    public async Task RefreshCancelStopsStartingProductsAndAnswersAfterRunningOnesRetire()
    {
        var gated = new GatedCatalog(new InMemoryCatalog(source));
        await using var service = new BuildCatalogService(new FakeStores(source, () => gated));
        await using var session = await Session.Start(service);
        await session.Send("refresh", "catalog.refresh", new { });
        Assert.Equal(0, (await session.Receive()).GetProperty("completed").GetInt32());
        await gated.ThreeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.Equal(BuildCatalogService.MaxProductsInFlight, gated.Started);
        await session.Send("cancel", "request.cancel", new { requestId = "refresh" });
        Assert.True((await session.Receive()).GetProperty("result").GetProperty("requested").GetBoolean());
        var terminal = await session.Receive();
        Assert.Equal("refresh", terminal.GetProperty("id").GetString());
        var error = terminal.GetProperty("error");
        Assert.Equal("CANCELED", error.GetProperty("code").GetString());
        Assert.Equal("Refresh stopped. Products checked before stopping keep their new results.", error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(3, gated.Retired);
        Assert.Equal(3, gated.Started);
    }

    [Fact]
    public async Task BusyWriterListsReadOnlyAndRefusesRefreshThenTakesTheWriterOnceFree()
    {
        await ObserveAsync("wow", ObservationKind.Versions, Versions(1, ("us", A, "11.0.0.1001")));
        var stores = new FakeStores(source) { Busy = true };
        var observed = source.Observed.Count;
        await using var service = new BuildCatalogService(stores);
        await using var session = await Session.Start(service);
        await session.Send("list", "catalog.list", new { });
        var list = (await session.Receive()).GetProperty("result");
        Assert.Equal("readOnly", list.GetProperty("access").GetString());
        Assert.Equal(1, ProductOf(list, "wow").GetProperty("buildCount").GetInt32());
        Assert.Equal(1, stores.ReadOnlyOpens);
        Assert.Equal(0, stores.OpenReadOnly);

        await session.Send("refresh", "catalog.refresh", new { });
        var error = (await session.Receive()).GetProperty("error");
        Assert.Equal("OBJECT_STORE_BUSY", error.GetProperty("code").GetString());
        Assert.Equal(BuildCatalogService.BusyMessage, error.GetProperty("message").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(observed, source.Observed.Count);

        stores.Busy = false;
        await session.Send("again", "catalog.list", new { });
        Assert.Equal("writer", (await session.Receive()).GetProperty("result").GetProperty("access").GetString());
        await session.Send("third", "catalog.list", new { });
        Assert.Equal("writer", (await session.Receive()).GetProperty("result").GetProperty("access").GetString());
        // Two busy tries and one open, kept for the process.
        Assert.Equal(3, stores.WriterOpens);
        Assert.Equal(1, stores.ReadOnlyOpens);
    }

    [Fact]
    public async Task ConcurrentFirstRequestsOpenTheWriterOnce()
    {
        var stores = new FakeStores(source);
        await using var service = new BuildCatalogService(stores);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.ListAsync(Ct)));
        Assert.Equal(1, stores.WriterOpens);
        await service.DisposeAsync();
        Assert.Equal(1, stores.Disposed);
    }

    [Fact]
    public async Task RealStoreListCreatesTheStoresAndHoldsTheWriterLock()
    {
        using var directory = new TempDirectory();
        var service = new BuildCatalogService(new DataFolderBuildCatalogStores(directory.Path));
        await using (service)
        {
            await using (var session = await Session.Start(service))
            {
                await session.Send("list", "catalog.list", new { });
                var catalog = (await session.Receive()).GetProperty("result");
                Assert.Equal("writer", catalog.GetProperty("access").GetString());
                Assert.Equal(13, catalog.GetProperty("products").GetArrayLength());
                Assert.Equal(JsonValueKind.Null, catalog.GetProperty("summary").ValueKind);
            }
            Assert.True(Directory.Exists(Path.Combine(directory.Path, "cdn-store-v1")));
            Assert.True(File.Exists(Path.Combine(directory.Path, "catalog-v1", "catalog.sqlite3")));
            Assert.Equal(DataFolderPaths.ObjectStoreFolder(directory.Path), Path.Combine(directory.Path, "cdn-store-v1"));
            // vm's case: a second writer is refused while the Host holds the lock; a read-only open still works.
            var busy = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(DataFolderPaths.ObjectStoreFolder(directory.Path)));
            Assert.Equal(ObjectStoreCodes.Busy, busy.Code);
            await (await ObjectStore.OpenReadOnlyAsync(DataFolderPaths.ObjectStoreFolder(directory.Path))).DisposeAsync();
            await using (var reader = await new DataFolderBuildCatalogStores(directory.Path).OpenReadOnlyAsync(Ct))
                Assert.Null((await reader.Catalog.ListBuildsAsync("wow", Ct)).LastObservedAt);
        }
        // Shutdown lets the lock go.
        await (await ObjectStore.OpenAsync(DataFolderPaths.ObjectStoreFolder(directory.Path))).DisposeAsync();
    }

    private static CatalogBuild Synthetic(string product, int index, string name, int regions, DateTimeOffset at)
    {
        var config = index.ToString("x32");
        return new CatalogBuild(product, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(product + name))), name, config, null,
            new Dictionary<string, string> { ["BuildConfig"] = config, ["VersionsName"] = name }, at - TimeSpan.FromMinutes(index), at, true,
            [.. Enumerable.Range(0, regions).Select(r => new BuildRegion("region" + (char)('a' + r / 26) + (char)('a' + r % 26), B, at, at, true, default))]);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, 123, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Opens the fakes; <see cref="Busy"/> answers a writer open as a held lock does.</summary>
    private sealed class FakeStores(InMemoryObjectSource source, Func<ICatalog>? catalog = null) : IBuildCatalogStores
    {
        private int writerOpens, readOnlyOpens, disposed, openReadOnly;
        public volatile bool Busy;
        public int WriterOpens => Volatile.Read(ref writerOpens);
        public int ReadOnlyOpens => Volatile.Read(ref readOnlyOpens);
        /// <summary>Read-only handles not disposed yet.</summary>
        public int OpenReadOnly => Volatile.Read(ref openReadOnly);
        public int Disposed => Volatile.Read(ref disposed);

        public async Task<IBuildCatalogHandle> OpenWriterAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref writerOpens);
            await Task.Yield();
            if (Busy) throw new ObjectStoreException(ObjectStoreCodes.Busy, "Another writer holds the store.");
            return new Handle(source, catalog?.Invoke() ?? new InMemoryCatalog(source), () => Interlocked.Increment(ref disposed));
        }

        public Task<IBuildCatalogHandle> OpenReadOnlyAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref readOnlyOpens);
            Interlocked.Increment(ref openReadOnly);
            return Task.FromResult<IBuildCatalogHandle>(new Handle(source, new InMemoryCatalog(source), () => Interlocked.Decrement(ref openReadOnly)));
        }

        private sealed class Handle(IObjectSource objects, ICatalog catalog, Action disposed) : IBuildCatalogHandle
        {
            public IObjectSource Objects => objects;
            public ICatalog Catalog => catalog;
            public ValueTask DisposeAsync() { disposed(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>Serves fixed lists; with <paramref name="failed"/>, every refresh observation answers that.</summary>
    private sealed class ListedCatalog(Func<string, BuildList> list, ObservationResult? failed = null) : ICatalog
    {
        public Task<BuildList> ListBuildsAsync(string product, CancellationToken cancellationToken) => Task.FromResult(list(product));
        public Task<CatalogRefresh> RefreshAsync(string product, CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogRefresh(product, failed ?? throw new InvalidOperationException(), failed, list(product)));
        public Task<SummaryRefresh> RefreshSummaryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Summaries.Read(failed ?? throw new InvalidOperationException()));
    }

    /// <summary>Every product refresh waits until canceled, then takes a moment to retire.</summary>
    private sealed class GatedCatalog(ICatalog inner) : ICatalog
    {
        private int started, retired;
        public int Started => Volatile.Read(ref started);
        public int Retired => Volatile.Read(ref retired);
        public TaskCompletionSource ThreeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BuildList> ListBuildsAsync(string product, CancellationToken cancellationToken) => inner.ListBuildsAsync(product, cancellationToken);
        public async Task<CatalogRefresh> RefreshAsync(string product, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 3) ThreeStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
            finally
            {
                await Task.Delay(100, CancellationToken.None);
                Interlocked.Increment(ref retired);
            }
        }
        public Task<SummaryRefresh> RefreshSummaryAsync(CancellationToken cancellationToken) => inner.RefreshSummaryAsync(cancellationToken);
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient client, server; private readonly StreamWriter writer; private readonly StreamReader reader; private readonly Task completion;
        private Session(TcpClient client, TcpClient server, BuildCatalogService? catalog)
        {
            this.client = client; this.server = server; writer = new(client.GetStream(), new UTF8Encoding(false), leaveOpen: true); reader = new(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            completion = new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), buildCatalog: catalog).RunAsync(server.GetStream(), server.GetStream());
        }
        internal static async Task<Session> Start(BuildCatalogService? catalog)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var client = new TcpClient();
            await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await listener.AcceptTcpClientAsync(); return new(client, server, catalog);
        }
        internal async Task Send(string id, string method, object parameters) { await writer.WriteLineAsync(JsonSerializer.Serialize(Request(id, method, parameters), Json)); await writer.FlushAsync(); }
        internal async Task<string> ReceiveLine() => (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
        internal async Task<JsonElement> Receive() { using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!); return document.RootElement.Clone(); }
        public async ValueTask DisposeAsync() { client.Dispose(); try { await completion.WaitAsync(TimeSpan.FromSeconds(10)); } finally { server.Dispose(); reader.Dispose(); await writer.DisposeAsync(); } }
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
