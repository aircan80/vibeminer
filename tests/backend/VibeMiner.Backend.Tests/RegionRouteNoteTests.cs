using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>A region advertised without any CDN route is a regional note, not a check problem (D076, #47).</summary>
public sealed class RegionRouteNoteTests
{
    private const string Time = "2026-10-21T09:00:00.0000000+00:00";
    // Shaped like the real retail documents: sg and xx are advertised, but cdns has no row for them.
    private static readonly string RetailVersions = Fixtures.Versions(Fixtures.Row("eu"), Fixtures.Row("sg"), Fixtures.Row("us"), Fixtures.Row("xx"));
    private static readonly string RetailCdns = Fixtures.Cdns("eu", "us", "cn");

    [Fact]
    public async Task RegionsAdvertisedWithoutARouteAreNotesAndTheCheckIsOnline()
    {
        var attempt = await Fixtures.Attempt(RetailVersions, RetailCdns);
        Assert.Equal("online", attempt.Connection);
        Assert.Equal(new[] { "sg", "xx" }, attempt.Errors.Select(error => error.Region));
        Assert.All(attempt.Errors, error =>
        {
            Assert.Equal("ROUTE_MISSING", error.Code);
            Assert.Equal(DiscoveryProblem.NoteSeverity, error.Severity);
            Assert.Equal(OfficialDiscoverySource.MissingRouteMessage, error.Message);
        });
        // The advertisement is kept and every region still counts as checked; only routed regions carry a route.
        Assert.Equal(new[] { "eu", "sg", "us", "xx" }, attempt.SuccessfulRegions);
        Assert.Equal(4, attempt.Observations.Count);
        Assert.All(attempt.Observations.Where(row => row.Region is "sg" or "xx"), row => Assert.Null(row.CdnsOrdinal));
        Assert.All(attempt.Observations.Where(row => row.Region is "eu" or "us"), row => Assert.NotNull(row.CdnsOrdinal));
    }

    [Theory]
    [InlineData("us|tpr/wow|cdn.invalid|x\nus|tpr/wow|cdn.invalid|x\n", "ROUTE_AMBIGUOUS")]
    [InlineData("us||cdn.invalid|x\n", "ROUTE_INVALID")]
    [InlineData("us|tpr/wow| |x\n", "ROUTE_INVALID")]
    public async Task AmbiguousAndUnusableRoutesStayProblemsNextToANote(string usRoute, string code)
    {
        var cdns = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\n" + usRoute;
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row("sg"), Fixtures.Row("us")), cdns);
        Assert.Equal("partial", attempt.Connection);
        var us = Assert.Single(attempt.Errors, error => error.Region == "us");
        Assert.Equal(code, us.Code); Assert.Equal(DiscoveryProblem.ProblemSeverity, us.Severity);
        var sg = Assert.Single(attempt.Errors, error => error.Region == "sg");
        Assert.Equal(DiscoveryProblem.NoteSeverity, sg.Severity);
    }

    [Fact]
    public async Task AnUnreadableCdnsDocumentIsAProblemForEveryRegionNotANote()
    {
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent(RetailVersions) }));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var attempt = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Equal("partial", attempt.Connection);
        Assert.All(attempt.Errors, error => Assert.Equal(DiscoveryProblem.ProblemSeverity, error.Severity));
        Assert.Equal(new[] { "eu", "sg", "us", "xx" }, attempt.Errors.Where(error => error.Code == "ROUTE_UNAVAILABLE").Select(error => error.Region));
    }

    [Fact]
    public async Task ACommittedCheckWithOnlyNotesReadsOnlineWithItsNotesOnTheWire()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        using var source = Fixtures.Source(RetailVersions, RetailCdns);
        var page = await new DiscoveryService(source, catalog).RefreshAllAsync(null, CancellationToken.None);
        Assert.Empty(page.RefreshProblems);
        Assert.All(page.Products, status =>
        {
            Assert.Equal("online", status.Connection);
            Assert.Equal(new[] { "sg", "xx" }, status.Errors.Select(error => error.Region));
            Assert.All(status.Errors, error => Assert.Equal(DiscoveryProblem.NoteSeverity, error.Severity));
        });
        var build = page.Builds.First(build => build.ProductId == "wow");
        Assert.Equal(new[] { "eu", "sg", "us", "xx" }, build.AdvertisedRegions);
        Assert.Equal(new[] { "eu", "us" }, build.RouteRegions);
        var single = await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None);
        Assert.Equal("online", single.Connection);
        var wire = JsonSerializer.SerializeToElement(single.Errors[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "region", "code", "message", "severity" }, wire.EnumerateObject().Select(property => property.Name));
        Assert.Equal("note", wire.GetProperty("severity").GetString());
    }

    [Theory]
    // Saved before D076: a missing route alone was saved as partial and reads as the complete check it was.
    [InlineData("""[{"region":"sg","code":"ROUTE_MISSING","message":"old"},{"region":"xx","code":"ROUTE_MISSING","message":"old"}]""", "partial", "online", "note,note")]
    // A real problem keeps the check partial.
    [InlineData("""[{"region":"sg","code":"ROUTE_MISSING","message":"old"},{"region":"eu","code":"ROUTE_AMBIGUOUS","message":"old"}]""", "partial", "partial", "note,problem")]
    // A product-level ROUTE_MISSING is not a regional note.
    [InlineData("""[{"region":null,"code":"ROUTE_MISSING","message":"old"}]""", "partial", "partial", "problem")]
    // Saved with a severity: read as saved.
    [InlineData("""[{"region":"sg","code":"ROUTE_MISSING","message":"new","severity":"note"}]""", "online", "online", "note")]
    public async Task ChecksSavedBeforeSeverityReadWithTheNewClassification(string savedErrors, string savedConnection, string connection, string severities)
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var generation = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
            await catalog.CommitAsync("wow", generation, await Fixtures.Attempt(RetailVersions, RetailCdns), Time, CancellationToken.None);
        }
        Overwrite(directory.Path, savedErrors, savedConnection);
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var page = await catalog.ReadAsync("wow", "all", 50, null, CancellationToken.None);
            Assert.Equal(connection, page.Connection);
            Assert.Equal(severities, string.Join(',', page.Errors.Select(error => error.Severity)));
            var status = (await catalog.ReadAllAsync("wow", "all", 50, null, CancellationToken.None)).Products.Single(s => s.ProductId == "wow");
            Assert.Equal(connection, status.Connection);
            Assert.Equal(page.Errors, status.Errors);
        }
    }

    [Fact]
    public async Task ASavedCheckWithNoCheckedRegionsStaysFailedEvenWithOnlyLegacyFindings()
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var generation = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
            await catalog.CommitAsync("wow", generation, new DiscoveryAttempt([], [], [], [], [new(null, "HTTP_STATUS", "old")], "partial"), Time, CancellationToken.None);
        }
        Overwrite(directory.Path, """[{"region":"sg","code":"ROUTE_MISSING","message":"old"}]""", "partial");
        await using var reopened = await DiscoveryCatalog.OpenAsync(directory.Path);
        Assert.Equal("partial", (await reopened.ReadAsync("wow", "all", 50, null, CancellationToken.None)).Connection);
    }

    [Fact]
    public async Task AnUnknownSavedSeverityIsRefusedAsCatalogIntegrity()
    {
        using var directory = new TempDirectory();
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var generation = await catalog.BeginRefreshAsync("wow", CancellationToken.None);
            await catalog.CommitAsync("wow", generation, await Fixtures.Attempt(RetailVersions, RetailCdns), Time, CancellationToken.None);
        }
        Overwrite(directory.Path, """[{"region":"sg","code":"ROUTE_MISSING","message":"x","severity":"info"}]""", "online");
        await using var reopened = await DiscoveryCatalog.OpenAsync(directory.Path);
        var refused = await Assert.ThrowsAsync<DiscoveryException>(() => reopened.ReadAsync("wow", "all", 50, null, CancellationToken.None));
        Assert.Equal("CATALOG_INTEGRITY", refused.Code);
    }

    private static void Overwrite(string directory, string errors, string connection)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "discovery-v1.sqlite3")};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE products SET errors=$errors, connection=$connection WHERE product='wow'";
        command.Parameters.AddWithValue("$errors", errors); command.Parameters.AddWithValue("$connection", connection);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}
