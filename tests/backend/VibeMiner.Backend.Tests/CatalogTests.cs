using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CatalogTests
{
    private const string Time1 = "2026-10-18T12:00:00.0000000+00:00";
    private const string Time2 = "2026-10-18T13:00:00.0000000+00:00";
    private static async Task<DiscoveryPage> Store(DiscoveryCatalog catalog, DiscoveryAttempt attempt, string time = Time1)
    {
        var generation = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
        return await catalog.CommitAsync("wow", generation, attempt, time, CancellationToken.None);
    }
    [Fact]
    public async Task RealSqliteRestartRetainsExactGroupedEvidenceWithoutBecomingLocal()
    {
        using var directory = new TempDirectory();
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu"), Fixtures.Row("us")));
        string id;
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var page = await Store(catalog, attempt);
            var build = Assert.Single(page.Builds); id = build.Id;
            Assert.Equal(new[] { "eu", "us" }, build.Regions); Assert.False(build.IsNew);
            Assert.False(build.IsLocal); Assert.Equal("unknown", build.Availability);
            Assert.Equal(4, build.SourceReceipts.Count);
            Assert.Equal("00123", build.BuildId); Assert.Equal(Fixtures.C, build.ProductConfig);
            Assert.Empty((await catalog.ReadAsync("wow", "local", 50, null, CancellationToken.None)).Builds);
        }
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var page = await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None);
            Assert.Equal(id, Assert.Single(page.Builds).Id); Assert.True(page.HasSavedHistory);
            Assert.Equal(Time1, page.LastCheckedAt);
        }
        using var db = OpenDb(directory.Path);
        Assert.Equal(3L, Scalar(db, "SELECT count(*) FROM observations"));
        Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM raw_documents"));
    }
    [Fact]
    public async Task EmptyOptionalProductConfigProjectsWireNullWhileIdentityPreservesEmptyAndAbsent()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var empty = await Store(catalog, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(productConfig: ""))));
        var original = Assert.Single(empty.Builds); Assert.Null(original.ProductConfig);
        Assert.Equal("", original.IdentityFields["ProductConfig"]);
        using var wire = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(empty, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, wire.RootElement.GetProperty("builds")[0].GetProperty("productConfig").ValueKind);
        var missing = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|Opaque!STRING:0\n" +
            $"us|{Fixtures.A}|{Fixtures.B}||00123|1.2.3.00123|opaque\n";
        var absent = await Store(catalog, await Fixtures.Attempt(missing));
        Assert.Equal(2, absent.Builds.Count);
        Assert.All(absent.Builds, build => Assert.Null(build.ProductConfig));
        Assert.Single(absent.Builds, build => build.IdentityFields.ContainsKey("ProductConfig"));
        Assert.Single(absent.Builds, build => !build.IdentityFields.ContainsKey("ProductConfig"));
    }
    [Fact]
    public async Task RepeatedBytesHaveDistinctEventsAndDeduplicatedRawDocuments()
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row()));
            await Store(catalog, attempt); var later = await Store(catalog, attempt, Time2);
            Assert.Equal(Time1, later.Builds[0].FirstSeenAt); Assert.Equal(Time2, later.Builds[0].LastSeenAt);
            Assert.False(later.Builds[0].IsNew);
        }
        using var db = OpenDb(directory.Path);
        Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM events"));
        Assert.Equal(2L, Scalar(db, "SELECT count(*) FROM raw_documents"));
        Assert.Equal(4L, Scalar(db, "SELECT count(*) FROM documents"));
    }
    [Fact]
    public async Task LaterNewIdentityStaysNewAndReturningIdentityKeepsFirstSeen()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var first = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row()));
        await Store(catalog, first);
        var next = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(config: Fixtures.C)));
        var second = await Store(catalog, next, Time2);
        var newBuild = Assert.Single(second.Builds, build => build.BuildConfig == Fixtures.C);
        Assert.True(newBuild.IsNew); Assert.True(newBuild.IsAdvertised);
        Assert.False(second.Builds.Single(build => build.BuildConfig == Fixtures.A).IsAdvertised);
        var third = await Store(catalog, first, "2020-01-01T00:00:00.0000000+00:00");
        Assert.Equal(2, third.Builds.Count);
        Assert.Equal(Time1, third.Builds.Single(build => build.BuildConfig == Fixtures.A).FirstSeenAt);
        Assert.False(third.Builds.Single(build => build.BuildConfig == Fixtures.A).IsNew);
        Assert.True(third.Builds.Single(build => build.BuildConfig == Fixtures.C).IsNew);
    }
    [Fact]
    public async Task PartialAndFailedChecksRetainPreviousAdvertisementAndBaselineNewRegionsNeutrally()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        await Store(catalog, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu"))));
        var partial = await Store(catalog, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(config: Fixtures.C), Fixtures.Row("eu", config: "invalid"))), Time2);
        Assert.Equal("partial", partial.Connection);
        Assert.Equal(new[] { "eu" }, partial.Builds.Single(build => build.BuildConfig == Fixtures.A).AdvertisedRegions);
        var failure = new DiscoveryAttempt([], [], [], [], [new(null, "NETWORK_UNAVAILABLE", "Official metadata could not be reached.")], "offline");
        var offline = await Store(catalog, failure);
        Assert.Equal("offline", offline.Connection); Assert.Equal(2, offline.Builds.Count);
        Assert.All(offline.Builds, build => Assert.True(build.IsAdvertised));
        var regionalFirst = await Store(catalog, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row("tw", config: Fixtures.B)), Fixtures.Cdns("tw")));
        Assert.False(regionalFirst.Builds.Single(build => build.BuildConfig == Fixtures.B).IsNew);
    }
    [Fact]
    public async Task OlderRefreshCannotOverwriteNewerSuccessfulGeneration()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var older = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
        var newer = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
        await catalog.CommitAsync("wow", newer, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(config: Fixtures.C))), Time2, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<DiscoveryException>(() => catalog.CommitAsync("wow", older,
            new([], [], [], [], [], "online"), Time1, CancellationToken.None));
        Assert.Equal("STALE_REQUEST", exception.Code);
        var page = await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None);
        Assert.Equal(Fixtures.C, Assert.Single(page.Builds).BuildConfig);
        Assert.False(page.Builds[0].IsNew);
    }
    [Fact]
    public async Task CursorsBindProductFilterAndGenerationAndNeverMixSnapshots()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row(config: Fixtures.C)));
        await Store(catalog, attempt);
        var first = await catalog.ReadAsync("wow", "all", 1, null, CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        var malformed = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(first.NextCursor!))!;
        malformed["after"] = null;
        var malformedCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(malformed.ToJsonString()));
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAsync("wow", "all", 1, malformedCursor, CancellationToken.None))).Code);
        var second = await catalog.ReadAsync("wow", "all", 1, first.NextCursor, CancellationToken.None);
        Assert.NotEqual(first.Builds[0].Id, second.Builds[0].Id); Assert.Null(second.NextCursor);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAsync("wow", "advertised", 1, first.NextCursor, CancellationToken.None))).Code);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAsync("wowt", "all", 1, first.NextCursor, CancellationToken.None))).Code);
        await catalog.BeginRefreshAsync("wow", CancellationToken.None);
        Assert.Equal("STALE_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ReadAsync("wow", "all", 1, first.NextCursor, CancellationToken.None))).Code);
    }
    [Fact]
    public async Task CanceledPublicationDoesNotCreateFirstSeenOrBaseline()
    {
        using var directory = new TempDirectory(); await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var generation = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row()));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.CommitAsync("wow", generation, attempt, Time1, cancel.Token));
            Assert.Empty((await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None)).Builds);
            Assert.False((await Store(catalog, attempt)).Builds[0].IsNew);
        }
    }
    [Fact]
    public async Task TamperedSavedBytesAreRefused()
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
            await Store(catalog, await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row())));
        using (var db = OpenDb(directory.Path))
        { using var command = db.CreateCommand(); command.CommandText = "UPDATE raw_documents SET body=X'00'"; command.ExecuteNonQuery(); }
        await using var reopened = await DiscoveryCatalog.OpenAsync(directory.Path);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => reopened.ReadAsync("wow", "all", 50, null, CancellationToken.None))).Code);
    }
    [Fact]
    public async Task VersionOneCatalogUpgradesLatestObservationIndexWithoutLosingHistory()
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row(), Fixtures.Row("eu")));
            await Store(catalog, attempt); await Store(catalog, attempt, Time2);
        }
        using (var db = OpenDb(directory.Path))
        { using var command = db.CreateCommand(); command.CommandText = CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; DROP TABLE content_locators; DROP TABLE table_admissions; DROP TABLE table_artifacts; DROP TABLE content_objects; DROP TABLE resolver_artifacts; DROP TABLE download_admissions; DROP TABLE download_artifact_sources; DROP TABLE download_artifacts; DROP TABLE inventory_checkpoints; DROP TABLE inventory_merge_results; DROP TABLE inventory_merge_state; DROP TABLE inventory_resolution_cursor; DROP TABLE inventory_available; DROP TABLE inventory_units; DROP TABLE inventory_logical; DROP TABLE inventory_tags; DROP TABLE inventory_origins; DROP TABLE en_objects; DROP TABLE en_candidates; DROP TABLE en_content; DROP TABLE inventory_generations; DROP TABLE metadata_objects; DROP TABLE open_acknowledgements; DROP TABLE workspace_configurations; DROP TABLE workspace_admissions; DROP TABLE configuration_objects; DROP TABLE build_regions; PRAGMA user_version=1;"; command.ExecuteNonQuery(); }
        await using (var upgraded = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var page = await upgraded.ReadAsync("wow", "all", 50, null, CancellationToken.None);
            var build = Assert.Single(page.Builds); Assert.Equal(4, build.SourceReceipts.Count);
            Assert.Equal(Time1, build.FirstSeenAt); Assert.Equal(Time2, build.LastSeenAt);
            Assert.Equal(0, build.SourceReceipts.Single(receipt => receipt.Region == "us" && receipt.Role == "versions").RowOrdinal);
        }
        using var verified = OpenDb(directory.Path);
        Assert.Equal(14L, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal(6L, Scalar(verified, "SELECT count(*) FROM observations"));
        Assert.Equal(2L, Scalar(verified, "SELECT count(*) FROM build_regions"));
    }
    [Fact]
    public async Task NewerCatalogSchemaIsRefused()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        using (var db = OpenDb(directory.Path)) { using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        Assert.Equal("CATALOG_VERSION", (await Assert.ThrowsAsync<DiscoveryException>(() => DiscoveryCatalog.OpenAsync(directory.Path))).Code);
    }
    [Fact]
    public async Task LocalReadAndRegistryDoNotCallSource()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var source = new NeverSource(); var service = new DiscoveryService(source, catalog);
        Assert.Empty((await service.ReadAsync("wow")).Builds);
        Assert.Equal(13, ProductRegistry.Products.Count);
        Assert.Equal(0, source.Calls);
    }
    [Fact]
    public async Task DurablePublicationWinsCancellationBeforeServiceReturns()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        using var cancellation = new CancellationTokenSource();
        using var source = Fixtures.Source(Fixtures.Versions(Fixtures.Row()));
        var service = new DiscoveryService(source, new CancelAfterCommit(catalog, cancellation));
        var page = await service.RefreshAsync("wow", cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested); Assert.Single(page.Builds);
        Assert.Single((await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None)).Builds);
    }
    private sealed class CancelAfterCommit(IDiscoveryCatalog inner, CancellationTokenSource cancellation) : IDiscoveryCatalog
    {
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => inner.BeginRefreshAsync(product, token);
        public async Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string time, CancellationToken token)
        {
            var page = await inner.CommitAsync(product, generation, attempt, time, token);
            cancellation.Cancel(); return page;
        }
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => inner.ReadAsync(product, filter, limit, cursor, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NeverSource : IDiscoverySource
    {
        public int Calls;
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Local reads must not use HTTP."); }
    }
    private static SqliteConnection OpenDb(string directory)
    { var db = new SqliteConnection($"Data Source={Path.Combine(directory, "discovery-v1.sqlite3")};Pooling=False"); db.Open(); return db; }
    private static long Scalar(SqliteConnection db, string sql)
    { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
}
