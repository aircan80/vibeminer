using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class PinnedOpeningCatalogTests
{
    private const string OriginalCdns = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\n" +
        "us|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/\n" +
        "eu|tpr/wow|eu.cdn.blizzard.com|https://eu.cdn.blizzard.com/\n";

    private static async Task<(WorkspaceRequest Request, CapturedOpeningContext Opening)> Seed(DiscoveryCatalog catalog, string? cdns = null)
    {
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu")), cdns ?? OriginalCdns);
        var generation = await catalog.BeginRefreshAsync("wow", default);
        var page = await catalog.CommitAsync("wow", generation, attempt, "2026-10-19T00:00:00.0000000Z", default);
        var request = new WorkspaceRequest("wow", Assert.Single(page.Builds).Id, "us");
        return (request, await catalog.ResolveOpeningAsync(request, default));
    }

    [Fact]
    public async Task Historical_pin_survives_route_refresh_and_catalog_restart_with_original_source_evidence()
    {
        using var directory = new TempDirectory(); WorkspaceRequest request; CapturedOpeningContext original;
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            (request, original) = await Seed(catalog);
            await Seed(catalog, OriginalCdns.Replace("https://us.cdn.blizzard.com/", "https://level3.ssl.blizzard.com/", StringComparison.Ordinal));
            var pinned = await catalog.ResolveOpeningAsync(request, original.Reference, default);
            var latest = await catalog.ResolveOpeningAsync(request, default);
            Assert.Equal(original.Reference, pinned.Reference);
            Assert.Equal(original.Cdns.Sha256, pinned.Cdns.Sha256); Assert.Equal(original.Cdns.Bytes, pinned.Cdns.Bytes);
            Assert.Equal(original.Cdns.RetrievedAt, pinned.Cdns.RetrievedAt); Assert.Equal(original.Cdns.Url, pinned.Cdns.Url);
            Assert.Equal("us.cdn.blizzard.com", ConfigurationWorkspacePreparer.ResolveRoute(pinned).Host);
            Assert.Equal("level3.ssl.blizzard.com", ConfigurationWorkspacePreparer.ResolveRoute(latest).Host);
            Assert.All(pinned.Build.SourceReceipts, receipt => Assert.Equal("us", receipt.Region));
            Assert.Equal(original.Cdns.Sha256, Assert.Single(pinned.Build.SourceReceipts, receipt => receipt.Role == "cdns").Sha256);
        }
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var pinned = await catalog.ResolveOpeningAsync(request, original.Reference, default);
            Assert.Equal(original.Reference, pinned.Reference); Assert.Equal(original.Versions.Bytes, pinned.Versions.Bytes);
            Assert.Equal("us.cdn.blizzard.com", ConfigurationWorkspacePreparer.ResolveRoute(pinned).Host);
        }
    }

    [Fact]
    public async Task Corrupt_latest_document_does_not_block_an_unchanged_valid_historical_pin()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog);
        var (_, latest) = await Seed(catalog, OriginalCdns.Replace("https://us.cdn.blizzard.com/", "https://level3.ssl.blizzard.com/", StringComparison.Ordinal));
        using (var db = Open(directory.Path))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE raw_documents SET body=$body WHERE sha256=$sha";
            var corrupt = latest.Cdns.Bytes.ToArray(); corrupt[^2] ^= 1;
            command.Parameters.AddWithValue("$body", corrupt); command.Parameters.AddWithValue("$sha", latest.Cdns.Sha256); command.ExecuteNonQuery();
        }
        var pinned = await catalog.ResolveOpeningAsync(request, original.Reference, default);
        Assert.Equal(original.Cdns.Bytes, pinned.Cdns.Bytes);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ResolveOpeningAsync(request, default))).Code);
    }

    [Theory]
    [InlineData("generation-zero")]
    [InlineData("generation-leading-zero")]
    [InlineData("generation-overflow")]
    [InlineData("region")]
    [InlineData("versions-hash")]
    [InlineData("cdns-hash")]
    [InlineData("negative-row")]
    [InlineData("excess-row")]
    public async Task Invalid_pins_fail_before_any_latest_source_fallback(string scenario)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog); var pin = scenario switch
        {
            "generation-zero" => original.Reference with { Generation = "0" },
            "generation-leading-zero" => original.Reference with { Generation = "01" },
            "generation-overflow" => original.Reference with { Generation = "999999999999999999999999" },
            "region" => original.Reference with { Region = "eu" },
            "versions-hash" => original.Reference with { VersionsSha256 = new string('A', 64) },
            "cdns-hash" => original.Reference with { CdnsSha256 = null! },
            "negative-row" => original.Reference with { VersionsRowOrdinal = -1 },
            _ => original.Reference with { CdnsRowOrdinal = 10000 }
        };
        Assert.Equal("SOURCE_REFERENCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveOpeningAsync(request, pin, default))).Code);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("versions-row")]
    [InlineData("cdn-row")]
    [InlineData("build")]
    public async Task Different_historical_association_is_unavailable_without_substituting_latest(string scenario)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog);
        var pin = scenario switch
        {
            "generation" => original.Reference with { Generation = "999" },
            "versions-row" => original.Reference with { VersionsRowOrdinal = 1 },
            "cdn-row" => original.Reference with { CdnsRowOrdinal = 1 },
            _ => original.Reference
        };
        if (scenario == "build") request = request with { BuildIdentity = new string('f', 64) };
        Assert.Equal("SOURCE_REFERENCE_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveOpeningAsync(request, pin, default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_pinned_document_digest_and_actual_bytes_are_checked(bool corruptBytes)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog); var pin = original.Reference;
        if (corruptBytes)
        {
            using var db = Open(directory.Path); using var command = db.CreateCommand();
            var corrupt = original.Cdns.Bytes.ToArray(); corrupt[^2] ^= 1;
            command.CommandText = "UPDATE raw_documents SET body=$body WHERE sha256=$sha";
            command.Parameters.AddWithValue("$body", corrupt); command.Parameters.AddWithValue("$sha", original.Cdns.Sha256); command.ExecuteNonQuery();
        }
        else pin = pin with { CdnsSha256 = new string('e', 64) };
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveOpeningAsync(request, pin, default))).Code);
    }

    [Theory]
    [InlineData("original_fields")]
    [InlineData("route_fields")]
    public async Task Saved_observation_fields_must_match_the_pinned_raw_rows(string column)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog);
        using (var db = Open(directory.Path))
        using (var command = db.CreateCommand())
        {
            command.CommandText = $"UPDATE observations SET {column}='{{}}' WHERE generation=$g AND ordinal=$row";
            command.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$row", original.Reference.VersionsRowOrdinal); command.ExecuteNonQuery();
        }
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveOpeningAsync(request, original.Reference, default))).Code);
    }

    [Fact]
    public async Task Missing_pinned_document_cannot_be_repaired_by_reading_a_newer_pair()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog); await Seed(catalog);
        using (var db = Open(directory.Path))
        using (var command = db.CreateCommand())
        {
            command.CommandText = "DELETE FROM documents WHERE generation=$g AND role='cdns'";
            command.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
        }
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            catalog.ResolveOpeningAsync(request, original.Reference, default))).Code);
    }

    [Fact]
    public async Task Pre_canceled_pin_lookup_never_returns_a_context()
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await Seed(catalog); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ResolveOpeningAsync(request, original.Reference, stop.Token));
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(path, "discovery-v1.sqlite3"), Pooling = false }.ToString()); db.Open(); return db;
    }
}
