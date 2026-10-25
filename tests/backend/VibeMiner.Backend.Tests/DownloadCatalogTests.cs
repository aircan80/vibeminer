using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DownloadCatalogTests
{
    [Fact]
    public async Task Small_source_artifact_and_admission_rows_replace_no_legacy_inventory_or_source_data()
    {
        await using var h = await Harness.Create();
        await h.Record();
        var workspace = await h.Commit([h.Fixture.PayloadA.EncodingKey]);
        Assert.Equal(DiscoveryCatalog.DownloadProfile, workspace.PreparationProfile);
        Assert.Equal("partial", workspace.Readiness); Assert.Equal("verified", workspace.MetadataState);
        Assert.Equal("notPrepared", workspace.InventoryState); Assert.Equal("measured", workspace.RetentionState);
        Assert.Null(workspace.Inventory); Assert.Null(workspace.DownloadProblem);
        Assert.True(workspace.Build.IsLocal); Assert.False(workspace.Build.IsNew);
        var summary = workspace.Download!;
        Assert.Equal(DiscoveryCatalog.DownloadScope, summary.Scope); Assert.Equal("2", summary.ObjectCount);
        Assert.Equal(h.Fixture.ExpectedEncodedBytes.ToString(CultureInfo.InvariantCulture), summary.TotalEncodedBytes);
        Assert.Equal(h.Fixture.PayloadA.Encoded.Length.ToString(CultureInfo.InvariantCulture), summary.VerifiedEncodedBytes);
        Assert.Equal(h.Fixture.PayloadB.Encoded.Length.ToString(CultureInfo.InvariantCulture), summary.RemainingEncodedBytes);
        Assert.Equal(h.Overhead.ToString(CultureInfo.InvariantCulture), summary.MetadataEncodedBytes);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_origins"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM en_content"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM download_artifacts"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM download_artifact_sources"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM download_admissions"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM workspace_configurations"));
        Assert.Single((await h.Catalog.ReadAsync("wow", "local", 50, null, default)).Builds);
        var admission = await h.Catalog.ReadDownloadAdmissionAsync(h.Request, default);
        Assert.Equal(h.Artifact, admission.Artifact); Assert.Equal([h.Fixture.PayloadA.EncodingKey], admission.VerifiedKeys);
        Assert.Equal(h.Fixture.PayloadA.Encoded.Length, admission.VerifiedLengths[h.Fixture.PayloadA.EncodingKey]);
        Assert.Equal(summary.AssessedAt, admission.AssessedAt);
    }

    [Fact]
    public async Task Artifact_and_exact_assessed_admission_reopen_without_source_or_inventory_reconstruction()
    {
        await using var h = await Harness.Create(); await h.Record();
        var first = await h.Commit([]);
        await h.Restart();
        var saved = await h.Catalog.FindDownloadArtifactAsync(h.Source with { ParserRevision = MetadataAcquirer.SourceRevision, VerifiedAt = "2026-10-20T00:00:00Z" }, default);
        Assert.Equal(h.Artifact, saved);
        var admission = await h.Catalog.ReadDownloadAdmissionAsync(h.Request, default);
        Assert.Empty(admission.VerifiedKeys); Assert.Equal(first.Download!.AssessedAt, admission.AssessedAt);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM events"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_generations"));
    }

    [Fact]
    public async Task Ledger_source_bindings_are_immutable_and_preserve_original_receipts()
    {
        await using var h = await Harness.Create(); await h.Record();
        await h.Catalog.RecordDownloadArtifactAsync(h.Source with { VerifiedAt = "2026-10-20T00:00:00Z" }, h.Artifact, default);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM download_artifact_sources"));
        Assert.Equal(h.Source.RetrievedAt, await h.Text("SELECT json_extract(receipt,'$.retrievedAt') FROM download_artifact_sources"));
        Assert.Equal(h.Source.VerifiedAt, await h.Text("SELECT json_extract(receipt,'$.verifiedAt') FROM download_artifact_sources"));
        foreach (var source in new[] { h.Source with { ContentKey = new string('a', 32) }, h.Source with { EncodingKey = new string('a', 32) },
            h.Source with { EncodedBytes = "100" }, h.Source with { SourceUrl = "https://eu.cdn.blizzard.com/changed" },
            h.Source with { RetrievedAt = "2026-10-20T00:00:00Z" } })
            await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindDownloadArtifactAsync(source, default));
        Assert.Null(await h.Catalog.FindDownloadArtifactAsync(h.Source with { Sha256 = new string('a', 64) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordDownloadArtifactAsync(h.Source,
            h.Artifact with { Sha256 = new string('b', 64) }, default));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM download_artifacts"));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordDownloadArtifactAsync(
            h.Source with { ParserRevision = MetadataAcquirer.SourceRevision }, h.Artifact, default));
    }

    [Fact]
    public async Task Missing_or_corrupt_artifact_binding_does_not_become_a_usable_admission()
    {
        await using var h = await Harness.Create();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Commit([]));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM workspace_admissions"));
        await h.Record();
        await h.Execute("UPDATE download_artifacts SET byte_length=byte_length+1");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindDownloadArtifactAsync(h.Source, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Commit([]));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM open_acknowledgements"));
    }

    [Fact]
    public async Task Unavailable_size_is_explicit_acknowledged_and_not_a_fake_local_catalog()
    {
        await using var h = await Harness.Create();
        var problem = new DownloadProblem("DOWNLOAD_NOT_PUBLISHED", "This build does not publish a supported download manifest.");
        var workspace = await h.Catalog.CommitUnavailableDownloadWorkspaceAsync(h.Context, h.Configurations, problem, default);
        Assert.Equal(problem, workspace.DownloadProblem); Assert.Null(workspace.Download); Assert.Null(workspace.Inventory);
        Assert.Equal("partial", workspace.Readiness); Assert.Equal("configurationsVerified", workspace.MetadataState);
        Assert.Equal("unknown", workspace.RetentionState); Assert.Equal("notPrepared", workspace.InventoryState);
        Assert.False(workspace.Build.IsLocal); Assert.False(workspace.Build.IsNew);
        Assert.Empty((await h.Catalog.ReadAsync("wow", "local", 50, null, default)).Builds);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM open_acknowledgements"));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadDownloadAdmissionAsync(h.Request, default));
    }

    [Fact]
    public async Task Exact_identity_region_ledger_and_page_bounds_are_checked_without_fallback()
    {
        await using var h = await Harness.Create(); await h.Record(); await h.Commit([]);
        foreach (var request in new[] { h.Request with { ProductId = "wowt" }, h.Request with { SourceRegion = "eu" },
            h.Request with { BuildIdentity = new string('a', 64) }, h.Request with { LedgerId = new string('b', 64) } })
            Assert.Equal("DOWNLOAD_NOT_READY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                h.Catalog.ReadDownloadAdmissionAsync(request, default))).Code);
        foreach (var request in new[] { h.Request with { Limit = 101 }, h.Request with { Limit = 0 }, h.Request with { After = "01" },
            h.Request with { After = "-1" }, h.Request with { After = "9223372036854775808" }, h.Request with { LedgerId = "short" } })
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                h.Catalog.ReadDownloadAdmissionAsync(request, default))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.ReadDownloadAdmissionAsync(h.Request with { After = "3" }, default))).Code);
        await h.Catalog.ReadDownloadAdmissionAsync(h.Request with { After = "2" }, default);
    }

    [Fact]
    public async Task Cancellation_bad_configuration_and_invalid_assessment_never_acknowledge_success()
    {
        await using var h = await Harness.Create(); await h.Record();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.CommitDownloadWorkspaceAsync(h.Context,
            h.Configurations, [h.Source], h.Artifact, [], h.Overhead, cancel.Token));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitDownloadWorkspaceAsync(h.Context,
            h.Configurations.Take(1).ToArray(), [h.Source], h.Artifact, [], h.Overhead, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Commit([h.Fixture.PayloadA.EncodingKey, h.Fixture.PayloadA.EncodingKey]));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Commit([new string('a', 32)]));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitDownloadWorkspaceAsync(h.Context,
            h.Configurations, [h.Source], h.Artifact, [], h.Overhead + 1, default));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM workspace_admissions"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM open_acknowledgements"));
    }

    [Theory]
    [InlineData("UPDATE download_admissions SET summary=json_set(summary,'$.verifiedEncodedBytes',CAST(CAST(json_extract(summary,'$.verifiedEncodedBytes') AS INTEGER)+1 AS TEXT),'$.remainingEncodedBytes',CAST(CAST(json_extract(summary,'$.remainingEncodedBytes') AS INTEGER)-1 AS TEXT))")]
    [InlineData("UPDATE download_admissions SET assessed_at='2026-10-19T01:00:00+01:00',summary=json_set(summary,'$.assessedAt','2026-10-19T01:00:00+01:00')")]
    [InlineData("UPDATE metadata_objects SET encoded=encoded+1 WHERE ekey=(SELECT value FROM download_admissions,json_each(verified_keys) LIMIT 1)")]
    [InlineData("DELETE FROM metadata_objects WHERE ekey=(SELECT value FROM download_admissions,json_each(verified_keys) LIMIT 1)")]
    public async Task Paging_rejects_changed_dated_assessment_or_immutable_receipt_without_rereading_payloads(string corruption)
    {
        await using var h = await Harness.Create(); await h.Record(); await h.Commit([h.Fixture.PayloadA.EncodingKey]);
        await h.Execute(corruption);
        Assert.Equal("DOWNLOAD_CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.ReadDownloadAdmissionAsync(h.Request, default))).Code);
    }

    [Fact]
    public async Task Empty_download_ledger_has_a_known_zero_scope_instead_of_unknown_or_missing()
    {
        await using var h = await Harness.Create();
        var empty = new DownloadLedgerArtifact(new string('c', 64), 32, 0, 0);
        await h.Catalog.RecordDownloadArtifactAsync(h.Source, empty, default);
        var workspace = await h.Catalog.CommitDownloadWorkspaceAsync(h.Context, h.Configurations, [h.Source], empty, [], h.Overhead, default);
        Assert.Equal("0", workspace.Download!.TotalEncodedBytes); Assert.Equal("0", workspace.Download.RemainingEncodedBytes);
        Assert.Equal("0", workspace.Download.ObjectCount); Assert.Null(workspace.DownloadProblem);
        Assert.True(workspace.Build.IsLocal);
    }

    [Fact]
    public async Task Retained_candidates_have_exact_aliases_sorted_and_bounded_without_inventory_membership()
    {
        await using var h = await Harness.Create();
        var candidates = await h.Catalog.ReadRetainedMetadataAsync(default);
        Assert.Equal(candidates.Select(value => value.Key).Order(StringComparer.Ordinal), candidates.Select(value => value.Key));
        Assert.Contains(candidates, value => value.Key == h.Fixture.PayloadA.EncodingKey);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_units"));
        await h.Execute("UPDATE metadata_objects SET ckey='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' WHERE ekey='" + h.Fixture.PayloadA.EncodingKey + "'");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadRetainedMetadataAsync(default));
    }

    [Fact]
    public async Task Resolver_artifacts_bind_original_encoded_source_to_verified_decoded_bytes_and_revision()
    {
        await using var h = await Harness.Create();
        var source = h.Receipt(h.Fixture.Metadata["encoding"]);
        await h.Catalog.RecordMetadataAsync(source with { ParserRevision = MetadataAcquirer.SourceRevision }, default);
        var decodedSha = Convert.ToHexStringLower(SHA256.HashData(h.Fixture.Metadata["encoding"].Decoded));
        var artifact = new ResolverArtifact(source, "encoding-native-file-v1", decodedSha,
            h.Fixture.Metadata["encoding"].Decoded.Length);
        await h.Catalog.RecordResolverArtifactAsync(artifact, default);
        await h.Catalog.RecordResolverArtifactAsync(artifact with { Source = source with { VerifiedAt = "2026-10-20T00:00:00Z" } }, default);
        await h.Restart();
        Assert.Equal(artifact, await h.Catalog.FindResolverArtifactAsync(source.Sha256, "encoding", artifact.ParserRevision, default));
        Assert.Null(await h.Catalog.FindResolverArtifactAsync(source.Sha256, "encoding", "encoding-native-file-v2", default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordResolverArtifactAsync(
            artifact with { DecodedSha256 = new string('a', 64) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordResolverArtifactAsync(
            artifact with { DecodedBytes = artifact.DecodedBytes + 1 }, default));
    }

    [Fact]
    public async Task V5_upgrade_preserves_objects_history_completed_and_interrupted_catalog_rows()
    {
        await using var h = await Harness.Create();
        var g = await h.Catalog.StartInventoryAsync(h.Context, default);
        var payload = h.Fixture.PayloadA;
        await h.Catalog.AddEncodingAsync(g, [new(payload.ContentKey, payload.Decoded.Length, [payload.EncodingKey])],
            [new(payload.EncodingKey, payload.Encoded.Length)], default);
        await h.Catalog.DisposeAsync();
        await h.Execute(CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; DROP TABLE content_locators; DROP TABLE table_admissions; DROP TABLE table_artifacts; DROP TABLE content_objects; DROP TABLE resolver_artifacts; DROP TABLE download_admissions; DROP TABLE download_artifact_sources; DROP TABLE download_artifacts; PRAGMA user_version=5");
        h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
        Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='staging'"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_content"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM events"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM configuration_objects"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM download_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='inventory_reference'"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal InventoryFixture Fixture { get; } = InventoryFixture.Create();
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-download-catalog-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; set; } = null!;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal IReadOnlyList<ConfigurationReceipt> Configurations { get; private set; } = [];
        internal MetadataReceipt Source => Receipt(Fixture.Metadata["download"]);
        internal DownloadLedgerArtifact Artifact => new(new string('d', 64), 80, 2, Fixture.ExpectedEncodedBytes);
        internal long Overhead => Configurations.Sum(value => (long)value.ByteLength) + Fixture.Metadata["download"].Encoded.Length;
        internal DownloadReadRequest Request => new("wow", Context.Build.Id, "us", Artifact.Sha256);
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, h.Fixture.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            h.Context = await h.Catalog.ResolveOpeningAsync(new("wow", h.Fixture.BuildIdentity, "us"), default);
            var configurations = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); var url = InventoryFixture.Url("config", key);
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, url, "2026-10-19T00:00:00Z"), default);
                configurations.Add(new(role, key, sha, bytes.Length, url, "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", 1));
            }
            h.Configurations = configurations;
            foreach (var value in new[] { h.Fixture.Metadata["download"], h.Fixture.PayloadA, h.Fixture.PayloadB })
                await h.Catalog.RecordMetadataAsync(h.Receipt(value, value.Role.StartsWith("payload", StringComparison.Ordinal) ? "root" : null)
                    with { ParserRevision = MetadataAcquirer.SourceRevision }, default);
            return h;
        }
        internal MetadataReceipt Receipt(InventoryFixture.FixtureObject value, string? role = null) => new(role ?? value.Role,
            value.ContentKey, value.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(value.Encoded)),
            value.Encoded.Length.ToString(CultureInfo.InvariantCulture), value.Decoded.Length.ToString(CultureInfo.InvariantCulture),
            InventoryFixture.Url("data", value.EncodingKey), "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", (role ?? value.Role) + "-v1");
        internal Task Record() => Catalog.RecordDownloadArtifactAsync(Source, Artifact, default);
        internal Task<WorkspaceSnapshot> Commit(IReadOnlyList<string> keys) => Catalog.CommitDownloadWorkspaceAsync(Context,
            Configurations, [Source], Artifact, keys, Overhead, default);
        internal async Task Restart() { await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); }
        internal async Task Execute(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False");
            await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
        }
        internal async Task<long> Scalar(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False");
            await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        internal async Task<string?> Text(string sql)
        {
            await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False");
            await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql;
            return await command.ExecuteScalarAsync() as string;
        }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
