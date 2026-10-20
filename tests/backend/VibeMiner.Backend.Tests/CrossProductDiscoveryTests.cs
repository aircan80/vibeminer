using System.Collections.Concurrent;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>Discovery read and refresh across every registered product (D062).</summary>
public sealed class CrossProductDiscoveryTests
{
    private const string Time = "2026-10-20T09:00:00.0000000+00:00";
    private static string Hash(char c) => new(c, 32);
    /// <summary>One versions row with an explicit raw BuildId, so ordering and identity can be told apart.</summary>
    private static string Row(string buildId, char config, string region = "us") =>
        $"{region}|{Hash(config)}|{Fixtures.B}||{buildId}|1.2.3.{buildId}|{Fixtures.C}|opaque";
    private static async Task<DiscoveryAttempt> Attempt(string product, params string[] rows)
    { using var source = Fixtures.Source(Fixtures.Versions(rows)); return await source.FetchAsync(product, CancellationToken.None); }
    private static async Task Store(DiscoveryCatalog catalog, string product, params string[] rows)
    {
        var generation = await catalog.BeginRefreshAsync(product, CancellationToken.None);
        await catalog.CommitAsync(product, generation, await Attempt(product, rows), Time, CancellationToken.None);
    }
    private static async Task<List<Build>> ReadAll(DiscoveryCatalog catalog, string? product = null, string filter = "all", int limit = 1)
    {
        var builds = new List<Build>();
        string? cursor = null;
        do
        {
            var page = await catalog.ReadAllAsync(product, filter, limit, cursor, CancellationToken.None);
            Assert.True(page.Builds.Count <= limit);
            builds.AddRange(page.Builds); cursor = page.NextCursor;
        } while (cursor is not null);
        return builds;
    }

    [Fact]
    public async Task PagesEveryProductInRegistryOrderThenNewestBuildNumberWithAStableCursor()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        // wow_classic is stored first and wow last: storage order must not decide display order.
        await Store(catalog, "wow_classic", Row("00500", 'd'));
        await Store(catalog, "wowt", Row("00900", 'e'), Row("not-a-number", 'f'));
        await Store(catalog, "wow", Row("00123", 'a'), Row("00456", '1'), Row("10000", '2'));
        var all = await ReadAll(catalog);
        Assert.Equal(new[] { "wow:10000", "wow:00456", "wow:00123", "wowt:00900", "wowt:not-a-number", "wow_classic:00500" },
            all.Select(build => $"{build.ProductId}:{build.BuildId}"));
        Assert.Equal(all.Count, all.Select(build => build.Id).Distinct().Count());
        // A full page agrees with the paged walk, and the product filter keeps the same order.
        var page = await catalog.ReadAllAsync(null, "all", 100, null, CancellationToken.None);
        Assert.Equal(all.Select(build => build.Id), page.Builds.Select(build => build.Id));
        Assert.Null(page.NextCursor); Assert.Null(page.ProductId); Assert.Equal("all", page.Filter); Assert.Empty(page.RefreshProblems);
        Assert.Equal(new[] { "00900", "not-a-number" }, (await ReadAll(catalog, "wowt")).Select(build => build.BuildId));
        // Every registered product reports its saved status in registry order, checked or not.
        Assert.Equal(ProductRegistry.Products.Select(product => product.Id), page.Products.Select(status => status.ProductId));
        var wow = page.Products[0];
        Assert.Equal("online", wow.Connection); Assert.Equal(Time, wow.LastCheckedAt); Assert.True(wow.HasSavedHistory);
        Assert.Equal(new[] { "us" }, wow.CheckedRegions);
        var neverChecked = page.Products.Single(status => status.ProductId == "wow_beta");
        Assert.Equal("notChecked", neverChecked.Connection); Assert.Null(neverChecked.LastCheckedAt); Assert.False(neverChecked.HasSavedHistory);
        // The cross-product projection is the same exact build the single-product read returns.
        var single = await catalog.ReadAsync("wowt", "all", 50, null, CancellationToken.None);
        Assert.Equal(single.Builds.Select(build => build.Id).Order(), all.Where(build => build.ProductId == "wowt").Select(build => build.Id).Order());
        var exact = single.Builds.Single(build => build.BuildId == "00900");
        var crossed = all.Single(build => build.Id == exact.Id);
        Assert.Equal(exact.IdentityFields, crossed.IdentityFields); Assert.Equal(exact.CdnConfig, crossed.CdnConfig);
        Assert.Equal(exact.SourceReceipts, crossed.SourceReceipts);
    }

    [Fact]
    public async Task CursorIsRefusedAfterAnotherProductCommitsButSurvivesARefreshThatOnlyStarted()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, "wow", Row("00123", 'a'), Row("00456", '1'));
        var first = await catalog.ReadAllAsync(null, "all", 1, null, CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "advertised", 1, first.NextCursor, CancellationToken.None))).Code);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync("wow", "all", 1, first.NextCursor, CancellationToken.None))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "all", 1, "%%%", CancellationToken.None))).Code);
        // A single-product cursor is not a cross-product cursor.
        var perProduct = await catalog.ReadAsync("wow", "all", 1, null, CancellationToken.None);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "all", 1, perProduct.NextCursor, CancellationToken.None))).Code);
        // Starting another product's refresh changes nothing that is visible yet.
        var generation = await catalog.BeginRefreshAsync("wowt", CancellationToken.None);
        var second = await catalog.ReadAllAsync(null, "all", 1, first.NextCursor, CancellationToken.None);
        Assert.NotEqual(first.Builds[0].Id, Assert.Single(second.Builds).Id);
        await catalog.CommitAsync("wowt", generation, await Attempt("wowt", Row("00900", 'e')), Time, CancellationToken.None);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "all", 1, first.NextCursor, CancellationToken.None))).Code);
        Assert.Equal("INVALID_PRODUCT", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync("../wow", "all", 1, null, CancellationToken.None))).Code);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAllAsync(null, "all", 101, null, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task CurrentFilterKeepsOnlyBuildsAdvertisedAtTheLastCheckAcrossProducts()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, "wow", Row("00123", 'a'));
        await Store(catalog, "wow", Row("00456", '1')); // 00123 is no longer advertised in us
        await Store(catalog, "wowt", Row("00900", 'e'));
        var current = await ReadAll(catalog, filter: "advertised");
        Assert.Equal(new[] { "wow:00456", "wowt:00900" }, current.Select(build => $"{build.ProductId}:{build.BuildId}"));
        Assert.All(current, build => Assert.True(build.IsAdvertised));
        Assert.False((await ReadAll(catalog)).Single(build => build.BuildId == "00123").IsAdvertised);
        Assert.Empty(await ReadAll(catalog, filter: "local"));
    }

    [Fact]
    public async Task RefreshAllChecksEveryProductAndReportsATypedFailureWithoutLosingEarlierEvidence()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, "wowt", Row("00800", 'e'));
        var source = new ScriptedSource(product => product switch
        {
            "wowt" => throw new DiscoveryException("CATALOG_BUSY", "Authored busy catalog.", true),
            "wow_beta" => Task.FromResult(new DiscoveryAttempt([], [], [], [], [new(null, "NETWORK_UNAVAILABLE", "Official metadata could not be reached.")], "offline")),
            _ => Attempt(product, Row(product == "wow" ? "00123" : "00042", 'a'))
        });
        var reports = new ConcurrentQueue<DiscoveryRefreshProgress>();
        var page = await new DiscoveryService(source, catalog).RefreshAllAsync(progress => { reports.Enqueue(progress); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(ProductRegistry.Products.Select(product => product.Id).Order(), source.Started.Order());
        var problem = Assert.Single(page.RefreshProblems);
        Assert.Equal(("wowt", "CATALOG_BUSY"), (problem.ProductId, problem.Code));
        // The failed product keeps its earlier build and status; the offline product is a committed offline check.
        Assert.Contains(page.Builds, build => build.ProductId == "wowt" && build.BuildId == "00800");
        Assert.Equal("online", page.Products.Single(status => status.ProductId == "wowt").Connection);
        var beta = page.Products.Single(status => status.ProductId == "wow_beta");
        Assert.Equal("offline", beta.Connection); Assert.Equal("NETWORK_UNAVAILABLE", Assert.Single(beta.Errors).Code);
        Assert.All(page.Products.Where(status => status.ProductId is not ("wowt" or "wow_beta")), status => Assert.Equal("online", status.Connection));
        Assert.Equal("wow", page.Builds[0].ProductId);
        // One initial report, then one per finished product in order.
        Assert.Equal(Enumerable.Range(0, ProductRegistry.Products.Count + 1), reports.Select(report => report.Completed));
        Assert.All(reports, report => Assert.Equal(ProductRegistry.Products.Count, report.Total));
    }

    [Fact]
    public async Task RefreshAllChecksAtMostThreeProductsAtOnce()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var running = 0; var peak = 0;
        var source = new ScriptedSource(async product =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return await Attempt(product, Row("00123", 'a'));
        });
        var page = await new DiscoveryService(source, catalog).RefreshAllAsync(null, CancellationToken.None);
        Assert.Equal(ProductRegistry.Products.Count, source.Started.Count);
        Assert.InRange(peak, 1, DiscoveryService.MaxProductsInFlight);
        Assert.Equal(ProductRegistry.Products.Select(product => product.Id), page.Builds.Select(build => build.ProductId));
        Assert.Null(page.NextCursor); Assert.Empty(page.RefreshProblems);
    }

    [Fact]
    public async Task CancelStopsStartingProductsAndReturnsOnlyAfterRunningChecksRetired()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        using var cancellation = new CancellationTokenSource();
        var gated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0; var retired = 0;
        var source = new ScriptedSource(async (product, token) =>
        {
            // The first product completes; the next ones hold until canceled.
            if (product == "wow") return await Attempt(product, Row("00123", 'a'));
            if (Interlocked.Increment(ref waiting) == DiscoveryService.MaxProductsInFlight) gated.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await retiring.Task; Interlocked.Increment(ref retired); }
            throw new InvalidOperationException("Only cancellation retires this authored source.");
        });
        var refresh = new DiscoveryService(source, catalog).RefreshAllAsync(null, cancellation.Token);
        await gated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Task.Delay(50);
        Assert.False(refresh.IsCompleted); // cleanup of the running checks has not finished
        retiring.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Equal(DiscoveryService.MaxProductsInFlight, Volatile.Read(ref retired));
        Assert.Equal(1 + DiscoveryService.MaxProductsInFlight, source.Started.Count);
        // The product that finished before cancellation keeps its committed result.
        var saved = await catalog.ReadAllAsync(null, "all", 50, null, CancellationToken.None);
        Assert.Equal("wow", Assert.Single(saved.Builds).ProductId);
        Assert.Equal("online", saved.Products[0].Connection);
        Assert.All(saved.Products.Skip(1), status => Assert.Equal("notChecked", status.Connection));
    }

    [Fact]
    public async Task CancelAfterTheLastProductCommittedStillReturnsTheDurableResult()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        using var cancellation = new CancellationTokenSource();
        var source = new ScriptedSource(product => Attempt(product, Row("00123", 'a')));
        // The cancel lands right after the thirteenth commit, before ForEachAsync completes.
        var page = await new DiscoveryService(source, new CancelAfterCommits(catalog, ProductRegistry.Products.Count, cancellation))
            .RefreshAllAsync(null, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ProductRegistry.Products.Select(product => product.Id), page.Builds.Select(build => build.ProductId));
        Assert.All(page.Products, status => Assert.Equal("online", status.Connection));
        Assert.Empty(page.RefreshProblems);
    }

    [Fact]
    public async Task SavedProductErrorsShrinkTheBuildBudgetAndThePageStopsEarlyWithAValidCursor()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        // 100 wow builds over two regions (64 rows per region at most).
        var rows = Enumerable.Range(0, 100).Select(i => $"{(i < 50 ? "us" : "eu")}|{i:x32}|{Fixtures.B}||{10000 + i}|1.2.3.{10000 + i}|{Fixtures.C}|opaque").ToArray();
        await Store(catalog, "wow", rows);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var unconstrained = await catalog.ReadAllAsync(null, "all", 100, null, CancellationToken.None);
        Assert.Equal(100, unconstrained.Builds.Count);
        var allBuildBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unconstrained.Builds, json).Length;
        Assert.True(allBuildBytes < 750_000, "Without large statuses all 100 builds fit the build budget.");
        // Every other product saves a failed check with many long problems, sized so statuses leave room for about half the builds.
        var problem = new DiscoveryProblem(null, "NETWORK_UNAVAILABLE", new string('m', 240));
        var problemBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(problem, json).Length + 1;
        var perProduct = (DiscoveryCatalog.MaxCatalogPageBytes - allBuildBytes / 2) / (ProductRegistry.Products.Count - 1) / problemBytes;
        foreach (var product in ProductRegistry.Products.Skip(1))
        {
            var generation = await catalog.BeginRefreshAsync(product.Id, CancellationToken.None);
            await catalog.CommitAsync(product.Id, generation, new DiscoveryAttempt([], [], [], [], Enumerable.Repeat(problem, perProduct).ToArray(), "offline"), Time, CancellationToken.None);
        }
        var first = await catalog.ReadAllAsync(null, "all", 100, null, CancellationToken.None);
        var statusBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(first.Products, json).Length;
        var buildBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(first.Builds, json).Length;
        Assert.All(first.Products.Skip(1), status => Assert.Equal(perProduct, status.Errors.Count));
        Assert.InRange(first.Builds.Count, 20, 80);
        // The engine counts each build's own JSON; the array adds a bracket pair and the separators.
        var counted = buildBytes - (first.Builds.Count + 1);
        Assert.True(statusBytes + counted <= DiscoveryCatalog.MaxCatalogPageBytes, $"{statusBytes} + {counted}");
        var nextBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(unconstrained.Builds[first.Builds.Count], json).Length;
        Assert.True(statusBytes + counted + nextBytes > DiscoveryCatalog.MaxCatalogPageBytes, "The page stops only when the next build would not fit.");
        Assert.Equal(unconstrained.Builds.Take(first.Builds.Count).Select(build => build.Id), first.Builds.Select(build => build.Id));
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(first, json).Length < 1024 * 1024);
        Assert.NotNull(first.NextCursor);
        // The cursor continues the same order; every build appears once.
        var all = first.Builds.ToList(); var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var next = await catalog.ReadAllAsync(null, "all", 100, cursor, CancellationToken.None);
            Assert.NotEmpty(next.Builds);
            all.AddRange(next.Builds); cursor = next.NextCursor;
        }
        Assert.Equal(unconstrained.Builds.Select(build => build.Id), all.Select(build => build.Id));
    }

    [Fact]
    public async Task HttpErrorsAndOfflineProductsSaveFailedChecksWithNoCheckedRegions()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var versions = Fixtures.Versions(Row("00123", 'a'));
        // wow answers, wow_classic returns HTTP 500 on both documents, every other product is unreachable.
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v2/products/wow/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("/versions", StringComparison.Ordinal) ? versions : Fixtures.Cdns("us")) });
            if (path.StartsWith("/v2/products/wow_classic/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
            throw new HttpRequestException("Authored unreachable source.");
        }));
        using var source = new OfficialDiscoverySource(client);
        var page = await new DiscoveryService(source, catalog).RefreshAllAsync(null, CancellationToken.None);
        Assert.Empty(page.RefreshProblems);
        var wow = page.Products.Single(status => status.ProductId == "wow");
        Assert.Equal("online", wow.Connection); Assert.Equal(new[] { "us" }, wow.CheckedRegions);
        // The desktop reads a partial status with no checked regions as a check that received nothing.
        var http = page.Products.Single(status => status.ProductId == "wow_classic");
        Assert.Equal("partial", http.Connection); Assert.Empty(http.CheckedRegions); Assert.False(http.HasSavedHistory);
        Assert.Equal(new[] { "HTTP_STATUS", "HTTP_STATUS" }, http.Errors.Select(error => error.Code));
        Assert.All(page.Products.Where(status => status.ProductId is not ("wow" or "wow_classic")), status =>
        {
            Assert.Equal("offline", status.Connection); Assert.Empty(status.CheckedRegions);
            Assert.All(status.Errors, error => Assert.Equal("NETWORK_UNAVAILABLE", error.Code));
        });
        Assert.Equal("wow", Assert.Single(page.Builds).ProductId);
    }

    private sealed class CancelAfterCommits(IDiscoveryCatalog inner, int commits, CancellationTokenSource cancellation) : IDiscoveryCatalog
    {
        private int committed;
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => inner.BeginRefreshAsync(product, token);
        public async Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string time, CancellationToken token)
        {
            var page = await inner.CommitAsync(product, generation, attempt, time, token);
            if (Interlocked.Increment(ref committed) == commits) cancellation.Cancel();
            return page;
        }
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => inner.ReadAsync(product, filter, limit, cursor, token);
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => inner.ReadAllAsync(product, filter, limit, cursor, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
    private sealed class ScriptedSource : IDiscoverySource
    {
        private readonly Func<string, CancellationToken, Task<DiscoveryAttempt>> fetch;
        public ConcurrentQueue<string> Started { get; } = new();
        public ScriptedSource(Func<string, Task<DiscoveryAttempt>> fetch) => this.fetch = (product, _) => fetch(product);
        public ScriptedSource(Func<string, CancellationToken, Task<DiscoveryAttempt>> fetch) => this.fetch = fetch;
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) { Started.Enqueue(product); return fetch(product, token); }
    }
}
