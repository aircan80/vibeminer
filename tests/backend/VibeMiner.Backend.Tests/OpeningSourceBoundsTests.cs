using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class OpeningSourceBoundsTests
{
    private const string Cdns = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\nus|tpr/wow|us.cdn.blizzard.com|https://us.cdn.blizzard.com/\n";
    public static TheoryData<string, string> Malformed => new()
    {
        { "current", "oversize" }, { "pinned", "oversize" }, { "receipts", "oversize" },
        { "current", "text-body" }, { "pinned", "text-body" }, { "receipts", "text-body" },
        { "current", "declared-text" }, { "pinned", "declared-text" }, { "receipts", "declared-text" },
        { "current", "missing-raw" }, { "pinned", "missing-raw" }, { "receipts", "missing-raw" },
        { "current", "missing-role" }, { "pinned", "missing-role" }, { "receipts", "missing-role" },
        { "current", "foreign-role" }, { "pinned", "foreign-role" }, { "receipts", "foreign-role" },
        { "current", "duplicate" }, { "pinned", "duplicate" }, { "receipts", "duplicate" },
        { "current", "url-limit" }, { "pinned", "url-limit" }, { "receipts", "url-limit" },
        { "current", "declared-lie" }, { "pinned", "declared-lie" }, { "receipts", "declared-lie" }
    };
    [Theory, MemberData(nameof(Malformed))]
    public async Task Poisoned_source_pair_fails_typed_before_returning_current_pinned_or_projection(string path, string mutation)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await SeedAsync(catalog);
        using (var db = Open(directory.Path))
        using (var command = db.CreateCommand())
        {
            command.CommandText = mutation switch
            {
                "oversize" => "UPDATE raw_documents SET body=zeroblob(1048577),byte_length=1 WHERE sha256=$sha",
                "text-body" => "UPDATE raw_documents SET body='x',byte_length=1 WHERE sha256=$sha",
                "declared-text" => "UPDATE raw_documents SET byte_length='not-a-number' WHERE sha256=$sha",
                "missing-raw" => "DELETE FROM raw_documents WHERE sha256=$sha",
                "missing-role" => "DELETE FROM documents WHERE generation=$g AND role='cdns'",
                "foreign-role" => "INSERT INTO documents SELECT generation,'foreign',url,retrieved_at,sha256,parser_revision FROM documents WHERE generation=$g AND role='versions'",
                "duplicate" => "CREATE TABLE documents_copy AS SELECT * FROM documents; DROP TABLE documents; ALTER TABLE documents_copy RENAME TO documents; INSERT INTO documents SELECT * FROM documents WHERE generation=$g AND role='versions'",
                "url-limit" => "UPDATE documents SET url=$long WHERE generation=$g AND role='cdns'",
                _ => "UPDATE raw_documents SET byte_length=1048576 WHERE sha256=$sha"
            };
            command.Parameters.AddWithValue("$sha", original.Cdns.Sha256); command.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$long", new string('x', 2049)); command.ExecuteNonQuery();
        }
        var error = await Assert.ThrowsAsync<DiscoveryException>(async () =>
        {
            if (path == "pinned") await catalog.ResolveOpeningAsync(request, original.Reference, default);
            else if (path == "current") await catalog.ResolveOpeningAsync(request, default);
            else await catalog.ReadAsync("wow", "all", 10, null, default);
        });
        Assert.Equal("CATALOG_INTEGRITY", error.Code);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_one_MiB_source_documents_keep_normal_and_historical_opening_after_restart(bool maximum)
    {
        using var directory = new TempDirectory(); WorkspaceRequest request; CapturedOpeningContext original;
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            (request, original) = await SeedAsync(catalog, maximum);
            Assert.Equal(maximum ? 1 << 20 : Fixtures.Utf8(Cdns).Length, original.Cdns.Bytes.Length);
            var pinned = await catalog.ResolveOpeningAsync(request, original.Reference, default);
            Assert.Equal(original.Reference, pinned.Reference); Assert.Equal(original.Cdns.Bytes, pinned.Cdns.Bytes);
            Assert.Equal(2, Assert.Single((await catalog.ReadAsync("wow", "all", 10, null, default)).Builds).SourceReceipts.Count);
        }
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var pinned = await catalog.ResolveOpeningAsync(request, original.Reference, default);
            Assert.Equal(original.Cdns.Bytes, pinned.Cdns.Bytes); Assert.Equal(original.Versions.Bytes, pinned.Versions.Bytes);
        }
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    public async Task Versions_only_discovery_stays_visible_offline_after_restart_without_inventing_a_route(string cdnFailure)
    {
        using var directory = new TempDirectory(); WorkspaceRequest request;
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            using var source = new OfficialDiscoverySource(new HttpClient(new Handler((r, _) =>
            {
                var versions = r.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal);
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(versions || cdnFailure != "missing" ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent(versions ? Fixtures.Versions(Fixtures.Row()) : cdnFailure == "empty" ? "" : "not-a-header") });
            })));
            var attempt = await source.FetchAsync("wow", default); Assert.Equal(cdnFailure == "missing" ? 1 : 2, attempt.Documents.Count);
            var generation = await catalog.BeginRefreshAsync("wow", default);
            var page = await catalog.CommitAsync("wow", generation, attempt, "2026-10-19T00:00:00Z", default);
            var build = Assert.Single(page.Builds); request = new("wow", build.Id, "us");
            Assert.Equal("versions", Assert.Single(build.SourceReceipts).Role); Assert.Empty(build.RouteRegions);
            Assert.Equal("SOURCE_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ResolveOpeningAsync(request, default))).Code);
        }
        await using (var catalog = await DiscoveryCatalog.OpenAsync(directory.Path))
        {
            var build = Assert.Single((await catalog.ReadAsync("wow", "all", 10, null, default)).Builds);
            Assert.Equal(request.BuildIdentity, build.Id); Assert.Equal("versions", Assert.Single(build.SourceReceipts).Role);
        }
    }
    [Theory]
    [InlineData("oversize")]
    [InlineData("text-body")]
    [InlineData("url-limit")]
    public async Task Production_SQL_masks_invalid_payload_and_metadata_before_any_managed_body_getter(string mutation)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (_, original) = await SeedAsync(catalog); using var db = Open(directory.Path);
        using (var update = db.CreateCommand())
        {
            update.CommandText = mutation switch
            {
                "oversize" => "UPDATE raw_documents SET body=zeroblob(1048577),byte_length=1 WHERE sha256=$sha",
                "text-body" => "UPDATE raw_documents SET body='x',byte_length=1 WHERE sha256=$sha",
                _ => "UPDATE documents SET url=$long WHERE generation=$g AND role='cdns'"
            };
            update.Parameters.AddWithValue("$sha", original.Cdns.Sha256); update.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$long", new string('x', 2049)); update.ExecuteNonQuery();
        }
        using var command = db.CreateCommand(); command.CommandText = DiscoveryCatalog.OpeningSourceDocumentsSql;
        command.Parameters.AddWithValue("$generation", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader(); var masked = 0; var valid = 0;
        while (reader.Read())
        {
            if (reader.GetInt32(7) == 1) { valid++; continue; }
            masked++; for (var i = 0; i <= 6; i++) Assert.True(reader.IsDBNull(i));
        }
        Assert.Equal(1, masked); Assert.Equal(1, valid);
    }
    [Theory]
    [InlineData("oversize")]
    [InlineData("text-body")]
    [InlineData("url-limit")]
    public async Task Already_verified_SHA_cannot_skip_actual_row_type_length_or_text_guards(string mutation)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await SeedAsync(catalog); using var db = Open(directory.Path); using var update = db.CreateCommand();
        update.CommandText = mutation switch
        {
            "oversize" => "UPDATE raw_documents SET body=zeroblob(1048577),byte_length=1 WHERE sha256=$sha",
            "text-body" => "UPDATE raw_documents SET body='x',byte_length=1 WHERE sha256=$sha",
            _ => "UPDATE documents SET url=$long WHERE generation=$g AND role='cdns'"
        };
        update.Parameters.AddWithValue("$sha", original.Cdns.Sha256); update.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$long", new string('x', 2049)); update.ExecuteNonQuery();
        // Invoke the existing private projection on an idle owned catalog to seed its
        // exact hash-dedup argument deterministically; no runtime test hook is added.
        var method = typeof(DiscoveryCatalog).GetMethod("ReadReceipts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(catalog,
            [request.BuildIdentity, new HashSet<string> { original.Cdns.Sha256, original.Versions.Sha256 }, CancellationToken.None]));
        Assert.Equal("CATALOG_INTEGRITY", Assert.IsType<DiscoveryException>(error.InnerException).Code);
    }
    [Theory]
    [InlineData("original_fields")]
    [InlineData("route_fields")]
    public async Task Pinned_observation_existing_field_bound_is_checked_before_deserialization(string column)
    {
        using var directory = new TempDirectory(); await using var catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
        var (request, original) = await SeedAsync(catalog); using var db = Open(directory.Path); using var update = db.CreateCommand();
        update.CommandText = $"UPDATE observations SET {column}=$long WHERE generation=$g";
        update.Parameters.AddWithValue("$long", new string('x', 131073)); update.Parameters.AddWithValue("$g", long.Parse(original.Reference.Generation, CultureInfo.InvariantCulture)); update.ExecuteNonQuery();
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => catalog.ResolveOpeningAsync(request, original.Reference, default))).Code);
    }
    private static async Task<(WorkspaceRequest Request, CapturedOpeningContext Opening)> SeedAsync(DiscoveryCatalog catalog, bool maximum = false)
    {
        var versions = Fixtures.Versions(Fixtures.Row()); var cdns = Cdns;
        if (maximum) { versions = Pad(versions); cdns = Pad(cdns); }
        var attempt = await Fixtures.Attempt(versions, cdns); var generation = await catalog.BeginRefreshAsync("wow", default);
        var page = await catalog.CommitAsync("wow", generation, attempt, "2026-10-19T00:00:00.0000000Z", default);
        var request = new WorkspaceRequest("wow", Assert.Single(page.Builds).Id, "us");
        return (request, await catalog.ResolveOpeningAsync(request, default));
    }
    private static string Pad(string source)
    {
        var text = new System.Text.StringBuilder(source);
        while (text.Length < (1 << 20))
        { var remaining = (1 << 20) - text.Length; if (remaining == 1) text.Append('\n'); else text.Append('#').Append('x', Math.Min(16000, remaining - 2)).Append('\n'); }
        return text.ToString();
    }
    private static SqliteConnection Open(string directory)
    { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "discovery-v1.sqlite3"), Pooling = false, ForeignKeys = false }.ToString()); db.Open(); return db; }
}
