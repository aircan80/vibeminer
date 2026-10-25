using VibeMiner.Application;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableDataStorageTests
{
    [Fact]
    public async Task Content_alias_preserves_original_source_after_reverification_and_rejects_conflicting_bytes()
    {
        await using var h = await Harness.Create(); var source = Source();
        await h.Catalog.RecordContentAsync(source, default);
        await h.Catalog.RecordContentAsync(source with { VerifiedAt = "2026-10-20T00:00:00Z", RetrievedAt = "2026-10-20T00:00:00Z" }, default);
        Assert.Equal(source, await h.Catalog.FindContentAsync(source.EncodingKey, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(source with { Sha256 = new('d', 64) }, default));
        Assert.Null(await h.Catalog.FindContentAsync(new('e', 32), default));
        await h.Restart(); Assert.Equal(source, await h.Catalog.FindContentAsync(source.EncodingKey, default));
    }

    [Theory]
    [InlineData("https://example.org/tpr/wow/data/aa/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://us.cdn.blizzard.com/tpr/wow/data/aa/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?token=x")]
    [InlineData("https://us.cdn.blizzard.com:9443/tpr/wow/data/aa/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Source_receipts_reject_untrusted_or_modified_origins(string url)
    {
        await using var h = await Harness.Create();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(Source() with { SourceUrl = url }, default));
    }

    [Fact]
    public async Task Archive_locator_bounds_and_source_revision_are_required()
    {
        await using var h = await Harness.Create(); var source = Source();
        var archive = source with { SourceUrl = InventoryFixture.Url("data", new('b', 32)), Location = new("archive", "50", "100", new('b', 32)) };
        await h.Catalog.RecordContentAsync(archive, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(source with { Location = new("archive", "99", "100", new('b', 32)) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(source with { VerifierRevision = "future" }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(source with { EncodedBytes = "010" }, default));
    }

    [Fact]
    public async Task Table_admission_is_exact_context_locale_source_and_interpretation_across_restart()
    {
        await using var h = await Harness.Create(); var source = Source(); await h.Catalog.RecordContentAsync(source, default);
        var encoding = Metadata("encoding", 'e'); var root = Metadata("root", 'f');
        await h.Catalog.RecordMetadataAsync(encoding, default); await h.Catalog.RecordMetadataAsync(root, default);
        var binding = new TableArtifactBinding(source.Sha256, new('9', 64), new('8', 64), "provider-revision", "enUS", Wdc5TableReader.Revision, new('4', 64));
        var artifact = new TableRowsArtifact(TableRowArtifacts.ContentKey(binding), new('7', 64), 1000, binding,
            new(1, "50238EC2", "9A2A4834", 1, 0, 4, [new("ID", "integer", true)]), new(1, 1, 0, 0, 0, 0, "complete", 10));
        var association = new SelectedFileAssociation("841626", "enUS", "enus-consistent-root-variants-v1", encoding, root,
            [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
        const string at = "2026-10-19T00:00:00Z";
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context, "50238ec2", "enUS", source, association, artifact, at, default));
        await h.Catalog.RecordTableArtifactAsync(artifact, default);
        await h.Catalog.CommitTableAdmissionAsync(h.Context, "50238ec2", "enUS", source, association, artifact, at, default);
        var saved = await h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default);
        Assert.Equal(artifact.ContentKey, saved!.Artifact.ContentKey); Assert.Equal(source, saved.Source);
        Assert.Equal(association.Memberships, saved.Association.Memberships); Assert.Equal(at, saved.PreparedAt);
        Assert.Null(await h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "deDE", default));
        Assert.Null(await h.Catalog.ReadTableAdmissionAsync(h.Context with { Reference = h.Context.Reference with { Generation = "999" } }, "50238ec2", "enUS", default));
        await h.Restart(); Assert.Equal(artifact.Sha256, (await h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default))!.Artifact.Sha256);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordTableArtifactAsync(artifact with { Sha256 = new('6', 64) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context, "50238ec2", "deDE", source, association, artifact, at, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", cancellation.Token));
    }

    [Fact]
    public async Task V6_upgrade_preserves_existing_discovery_and_configuration_admission()
    {
        await using var h = await Harness.Create(); await h.Catalog.DisposeAsync();
        using (var db = h.OpenDb()) { using var c = db.CreateCommand(); c.CommandText = CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; DROP TABLE content_locators; DROP TABLE table_admissions; DROP TABLE table_artifacts; DROP TABLE content_objects; PRAGMA user_version=6"; c.ExecuteNonQuery(); }
        h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
        Assert.True(await h.Catalog.HasAdmissionAsync(h.Context, default)); Assert.Null(await h.Catalog.FindContentAsync(Source().EncodingKey, default));
        using var check = h.OpenDb(); using var command = check.CreateCommand(); command.CommandText = "PRAGMA user_version"; Assert.Equal(19L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Durable_locator_can_precede_content_and_binds_exact_cdn_configuration()
    {
        await using var h = await Harness.Create(); var proof = new ContentLocatorArtifact(new('a', 32), new('b', 64), new('c', 32), new('d', 64), 1024);
        await h.Catalog.RecordContentLocatorAsync(proof, default); await h.Restart();
        Assert.Equal(proof, await h.Catalog.FindContentLocatorAsync(proof.EncodingKey, proof.CdnConfigurationSha256, default));
        Assert.Null(await h.Catalog.FindContentLocatorAsync(proof.EncodingKey, new('e', 64), default));
        Assert.Null(await h.Catalog.FindContentAsync(proof.EncodingKey, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentLocatorAsync(proof with { Sha256 = new('f', 64) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentLocatorAsync(proof with { ByteLength = 9L << 20 }, default));
    }

    private static ContentReceipt Source() => new(new('c', 32), new('a', 32), new('b', 64), "20", "10", InventoryFixture.Url("data", new('a', 32)),
        "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", "content-blte-v1", new("loose", "0", "20", null));
    private static MetadataReceipt Metadata(string role, char value) => new(role, new(value, 32), new(value, 32), new(value, 64), "20", "10",
        InventoryFixture.Url("data", new(value, 32)), "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", MetadataAcquirer.SourceRevision);

    private sealed class Harness : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-table-storage-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; set; } = null!;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); var f = InventoryFixture.Create();
            var g = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", g, f.Discovery, "2026-10-19T00:00:00Z", default);
            h.Context = await h.Catalog.ResolveOpeningAsync(new("wow", f.BuildIdentity, "us"), default);
            var receipts = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", f.BuildKey, f.BuildConfig), ("cdnConfig", f.CdnKey, f.CdnConfig) })
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); const string time = "2026-10-19T00:00:00Z";
                await h.Catalog.RecordConfigurationAsync(new(key, hash, bytes.Length, InventoryFixture.Url("config", key), time), default);
                receipts.Add(new(role, key, hash, bytes.Length, InventoryFixture.Url("config", key), time, time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Context, receipts, "2026-10-19T00:00:00Z", default); return h;
        }
        internal async Task Restart() { await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); }
        internal SqliteConnection OpenDb() { var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False"); db.Open(); return db; }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
