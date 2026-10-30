using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class EncodedReceiptCatalogTests
{
    [Fact]
    public async Task Fresh_catalog_stores_one_durable_alias_and_keeps_its_original_source_evidence()
    {
        await using var h = await Harness.Create(); var original = Encoded();
        Assert.Equal(22L, await h.Scalar("PRAGMA user_version"));
        Assert.Null(await h.Catalog.FindEncodedReceiptAsync(original.EncodingKey, original.EncodedBytes, default));
        await h.Catalog.RecordEncodedReceiptAsync(original, default);
        await h.Catalog.RecordEncodedReceiptAsync(original with { VerifiedAt = "2026-10-20T00:00:00Z", RetrievedAt = "2026-10-20T00:00:00Z", SourceUrl = Url(original.EncodingKey, "eu") }, default);
        Assert.Equal(original, await h.Catalog.FindEncodedReceiptAsync(original.EncodingKey, original.EncodedBytes, default));
        await h.Restart(); Assert.Equal(original, await h.Catalog.FindEncodedReceiptAsync(original.EncodingKey, original.EncodedBytes, default));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM encoded_objects"));
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1"))); // Catalog lookup never claims current physical verification.
    }

    [Theory]
    [InlineData("full")]
    [InlineData("partial")]
    [InlineData("metadata")]
    public async Task Stronger_original_receipts_project_encoded_proof_without_rewriting_their_source_kind(string kind)
    {
        await using var h = await Harness.Create(); await h.Record(kind);
        var found = await h.Catalog.FindEncodedReceiptAsync(Key, 20, default);
        Assert.NotNull(found); Assert.Equal("blte-encoded-v1", found.VerifierRevision);
        Assert.Equal(Key, found.EncodingKey); Assert.Equal(Hash, found.Sha256); Assert.Equal(20L, found.EncodedBytes);
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM encoded_objects"));
        if (kind == "metadata")
        {
            var original = Metadata(); Assert.Equal(original.SourceUrl, found.SourceUrl); Assert.Equal(original.RetrievedAt, found.RetrievedAt);
            Assert.Equal(new ContentLocation("loose", "0", "20", null), found.Location);
            Assert.Equal(original, await h.Catalog.FindMetadataAsync(Key, default));
        }
        else
        {
            var original = Content(kind == "partial"); Assert.Equal(original.SourceUrl, found.SourceUrl); Assert.Equal(original.RetrievedAt, found.RetrievedAt);
            Assert.Equal(original.Location, found.Location); Assert.Equal(original.VerifiedAt, found.VerifiedAt);
            Assert.Equal(original.Projection?.IdentitySha256, (await h.Catalog.FindContentAsync(Key, default))!.Projection?.IdentitySha256);
        }
    }

    [Fact]
    public async Task Native_original_receipt_wins_then_the_earliest_stronger_source_is_chosen_without_reassociation()
    {
        await using var h = await Harness.Create();
        var content = Content(false) with { RetrievedAt = "2026-10-18T00:00:00Z" };
        await h.Catalog.RecordContentAsync(content, default); await h.Catalog.RecordMetadataAsync(Metadata(), default);
        var inherited = await h.Catalog.FindEncodedReceiptAsync(Key, 20, default);
        Assert.Equal(content.SourceUrl, inherited!.SourceUrl); Assert.Equal(content.Location, inherited.Location);
        var native = Encoded() with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-20T00:00:00Z" };
        await h.Catalog.RecordEncodedReceiptAsync(native, default);
        Assert.Equal(native, await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("full")]
    [InlineData("partial")]
    [InlineData("metadata")]
    public async Task A_requested_length_conflicting_with_any_retained_kind_is_an_integrity_failure(string kind)
    {
        await using var h = await Harness.Create(); await h.Record(kind);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindEncodedReceiptAsync(Key, 21, default));
        Assert.Equal("ENCODED_RECEIPT_INTEGRITY", failure.Code);
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("length")]
    public async Task Cross_kind_conflicting_bytes_are_refused_even_when_each_individual_receipt_is_well_formed(string conflict)
    {
        await using var h = await Harness.Create(); await h.Catalog.RecordMetadataAsync(Metadata(), default);
        var content = Content(false);
        content = conflict == "sha" ? content with { Sha256 = new('e', 64) } : content with { EncodedBytes = "21" };
        await h.Catalog.RecordContentAsync(content, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedReceiptAsync(Encoded(), default));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM encoded_objects"));
    }

    [Fact]
    public async Task Native_encoded_alias_cannot_change_its_digest_or_length()
    {
        await using var h = await Harness.Create(); var receipt = Encoded(); await h.Catalog.RecordEncodedReceiptAsync(receipt, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedReceiptAsync(receipt with { Sha256 = new('e', 64) }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedReceiptAsync(receipt with { EncodedBytes = 21, Location = receipt.Location with { SourceBytes = "21" } }, default));
        Assert.Equal(receipt, await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Fact]
    public async Task Batches_are_bounded_exact_key_lookups_and_do_not_scan_unrequested_corrupt_receipts()
    {
        await using var h = await Harness.Create(); await h.Catalog.RecordEncodedReceiptAsync(Encoded(), default);
        await h.Execute("INSERT INTO encoded_objects VALUES($k,$h,20,'malformed-unrequested')", ("$k", new string('f', 32)), ("$h", Hash));
        var result = await h.Catalog.ReadEncodedReceiptsAsync([(Key, 20), (Key, 20), (new string('b', 32), 20)], default);
        Assert.Single(result); Assert.Equal(Encoded(), result[Key]);
        Assert.Empty(await h.Catalog.ReadEncodedReceiptsAsync([], default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadEncodedReceiptsAsync(Enumerable.Repeat((Key, 20L), 129).ToArray(), default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadEncodedReceiptsAsync([(Key, 20), (Key, 21)], default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindEncodedReceiptAsync(new string('f', 32), 20, default));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("revision")]
    [InlineData("timestamp")]
    [InlineData("utc")]
    [InlineData("chronology")]
    public async Task Native_receipts_require_official_exact_locators_and_canonical_UTC_evidence(string defect)
    {
        await using var h = await Harness.Create(); var receipt = Encoded();
        receipt = defect switch
        {
            "origin" => receipt with { SourceUrl = "https://example.org/object" },
            "offset" => receipt with { Location = new("archive", "81", "100", new('b', 32)), SourceUrl = Url(new('b', 32)) },
            "size" => receipt with { Location = receipt.Location with { SourceBytes = "020" } },
            "revision" => receipt with { VerifierRevision = "unknown" },
            "timestamp" => receipt with { RetrievedAt = " 2026-10-19T00:00:00Z " },
            "chronology" => receipt with { VerifiedAt = "2026-10-18T00:00:00Z" },
            _ => receipt with { VerifiedAt = "2026-10-19T02:00:00+02:00" }
        };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedReceiptAsync(receipt, default));
        Assert.Null(await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Fact]
    public async Task Archive_encoded_receipt_preserves_the_original_bounded_range_locator()
    {
        await using var h = await Harness.Create(); var receipt = Encoded() with { SourceUrl = Url(new('b', 32)), Location = new("archive", "50", "100", new('b', 32)) };
        await h.Catalog.RecordEncodedReceiptAsync(receipt, default); await h.Restart();
        Assert.Equal(receipt, await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Fact]
    public async Task Cancellation_and_failed_SQL_publication_never_establish_a_durable_receipt()
    {
        await using var h = await Harness.Create(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.RecordEncodedReceiptAsync(Encoded(), cancellation.Token));
        Assert.Null(await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
        await h.Execute("CREATE TRIGGER fail_encoded BEFORE INSERT ON encoded_objects BEGIN SELECT RAISE(ABORT,'authored publication failure'); END");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedReceiptAsync(Encoded(), default));
        await h.Restart(); Assert.Null(await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
        await h.Execute("DROP TRIGGER fail_encoded"); await h.Catalog.RecordEncodedReceiptAsync(Encoded(), default);
        Assert.Equal(Encoded(), await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Fact]
    public async Task V7_upgrade_preserves_discovery_and_stronger_receipts_and_creates_no_backfill()
    {
        await using var h = await Harness.Create(); var fixture = InventoryFixture.Create();
        var generation = await h.Catalog.BeginRefreshAsync("wow", default);
        await h.Catalog.CommitAsync("wow", generation, fixture.Discovery, Now, default); await h.Catalog.RecordContentAsync(Content(true), default);
        await h.Catalog.DisposeAsync();
        await h.Execute(CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; PRAGMA user_version=7"); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
        Assert.Equal(22L, await h.Scalar("PRAGMA user_version")); Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM builds"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM encoded_objects"));
        Assert.NotNull(await h.Catalog.FindContentAsync(Key, default)); Assert.NotNull(await h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    [Fact]
    public async Task Archive_metadata_keeps_actual_source_location_in_encoded_projection_and_immutable_bindings()
    {
        await using var h = await Harness.Create(); var source = Metadata() with
        {
            Role = "download", ParserRevision = "download-v1", SourceUrl = Url(new('b', 32)),
            Location = new("archive", "50", "100", new('b', 32))
        };
        await h.Catalog.RecordMetadataAsync(source, default);
        var projected = await h.Catalog.FindEncodedReceiptAsync(Key, 20, default);
        Assert.Equal(source.SourceUrl, projected!.SourceUrl); Assert.Equal(source.Location, projected.Location);
        var ledger = new DownloadLedgerArtifact(new('e', 64), 32, 0, 0);
        await h.Catalog.RecordDownloadArtifactAsync(source, ledger, default);
        Assert.Equal(ledger, await h.Catalog.FindDownloadArtifactAsync(source, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindDownloadArtifactAsync(source with { Location = source.Location! with { Offset = "51" } }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordMetadataAsync(source with { Location = source.Location! with { Offset = "81" } }, default));
    }

    [Fact]
    public async Task Legacy_null_metadata_location_is_equivalent_to_explicit_loose_provenance()
    {
        await using var h = await Harness.Create(); var source = Metadata() with { Role = "download", ParserRevision = "download-v1" };
        await h.Catalog.RecordMetadataAsync(source, default); var ledger = new DownloadLedgerArtifact(new('e', 64), 32, 0, 0);
        await h.Catalog.RecordDownloadArtifactAsync(source, ledger, default);
        Assert.Equal(ledger, await h.Catalog.FindDownloadArtifactAsync(source with { Location = new("loose", "0", "20", null) }, default));
        Assert.Null((await h.Catalog.FindMetadataAsync(Key, default))!.Location);
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 20)]
    [InlineData("00000000000000000000000000000000", 20)]
    [InlineData("", 20)]
    [InlineData(Key, 8)]
    [InlineData(Key, 536870913L)]
    public async Task Invalid_request_identities_and_lengths_are_refused_even_when_no_alias_exists(string key, long length)
    {
        await using var h = await Harness.Create();
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindEncodedReceiptAsync(key, length, default));
        Assert.Equal("INVALID_REQUEST", failure.Code);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("full")]
    [InlineData("metadata")]
    public async Task Stored_alias_columns_must_still_agree_with_the_original_receipt(string kind)
    {
        await using var h = await Harness.Create(); await h.Record(kind);
        await h.Execute(kind switch
        {
            "native" => "UPDATE encoded_objects SET encoded_bytes=21",
            "full" => "UPDATE content_objects SET sha256='eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'",
            _ => "UPDATE metadata_objects SET ckey='eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'"
        });
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindEncodedReceiptAsync(Key, 20, default));
    }

    private const string Key = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Hash = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string Now = "2026-10-19T00:00:00Z";
    private static string Url(string key, string region = "us") => $"https://{region}.cdn.blizzard.com/tpr/wow/data/{key[..2]}/{key[2..4]}/{key}";
    private static EncodedReceipt Encoded() => new(Key, Hash, 20, Url(Key), Now, Now, "blte-encoded-v1", new("loose", "0", "20", null));
    private static MetadataReceipt Metadata() => new("root", new('c', 32), Key, Hash, "20", "5", Url(Key), Now, Now, MetadataAcquirer.SourceRevision);
    private static ContentReceipt Content(bool partial)
    {
        ContentProjection? proof = null;
        if (partial)
        {
            proof = new(ContentProjectionIdentity.Policy, "", [new("0", "2", new('e', 64))], [new("2", "3", 1, "0807060504030201")]);
            proof = proof with { IdentitySha256 = ContentProjectionIdentity.Compute(new('c', 32), Key, "5", proof) };
        }
        return new(new('c', 32), Key, Hash, "20", "5", Url(new('b', 32), "eu"), Now, Now,
            partial ? ContentProjectionIdentity.VerifierRevision : "content-blte-v1", new("archive", "50", "100", new('b', 32)), proof);
    }
    private sealed class Harness : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-encoded-catalog-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; set; } = null!;
        internal static async Task<Harness> Create() { var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); return h; }
        internal async Task Record(string kind)
        { if (kind == "native") await Catalog.RecordEncodedReceiptAsync(Encoded(), default); else if (kind == "metadata") await Catalog.RecordMetadataAsync(Metadata(), default); else await Catalog.RecordContentAsync(Content(kind == "partial"), default); }
        internal async Task Restart() { await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); }
        internal async Task Execute(string sql, params (string Name, object Value)[] parameters)
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value); await command.ExecuteNonQueryAsync(); }
        internal async Task<long> Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
        private SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False"); db.Open(); return db; }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
