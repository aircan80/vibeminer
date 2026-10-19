using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DownloadContentReuseTests
{
    [Fact]
    public async Task Downloaded_content_receipt_increases_new_DL_assessment_and_roundtrips_exactly_once()
    {
        await using var h = await Harness.Create();
        var before = await h.Open(); Assert.Equal("0", before.Download!.VerifiedEncodedBytes);
        var original = await h.RegisterContent(h.Data.PayloadA);
        var opened = await h.Open(); var summary = opened.Download!;
        Assert.Equal(N(h.Data.PayloadA.Encoded.Length), summary.VerifiedEncodedBytes);
        Assert.Equal(N(h.Data.PayloadB.Encoded.Length), summary.RemainingEncodedBytes);
        var candidates = await h.Catalog.ReadRetainedMetadataAsync(default);
        var item = Assert.Single(candidates, item => item.Key == original.EncodingKey);
        Assert.Equal(original.ContentKey, item.ContentKey); Assert.Equal(original.Sha256, item.Sha256);
        Assert.Equal(original, await h.Catalog.FindContentAsync(original.EncodingKey, default));
        var page = await new DownloadQuery(h.Catalog, h.Objects).ReadDownloadsAsync(new("wow", h.Data.BuildIdentity, "us", summary.LedgerId), default);
        Assert.True(Assert.Single(page.Entries, entry => entry.EncodingKey == original.EncodingKey).VerifiedAtAssessment);
        Assert.False(Assert.Single(page.Entries, entry => entry.EncodingKey == h.Data.PayloadB.EncodingKey).VerifiedAtAssessment);
        Assert.Equal(summary.AssessedAt, page.AssessedAt);
    }

    [Fact]
    public async Task Identical_metadata_and_content_aliases_share_one_physical_credit_and_keep_original_receipts()
    {
        await using var h = await Harness.Create(); var content = await h.RegisterContent(h.Data.PayloadA);
        var metadata = Metadata(content); await h.Catalog.RecordMetadataAsync(metadata, default);
        var opened = await h.Open(); Assert.Equal(N(h.Data.PayloadA.Encoded.Length), opened.Download!.VerifiedEncodedBytes);
        var candidates = await h.Catalog.ReadRetainedMetadataAsync(default);
        Assert.Single(candidates, item => item.Key == content.EncodingKey);
        Assert.Equal(content, await h.Catalog.FindContentAsync(content.EncodingKey, default));
        Assert.Equal(metadata, await h.Catalog.FindMetadataAsync(content.EncodingKey, default));
        var saved = await h.Catalog.ReadDownloadAdmissionAsync(new("wow", h.Data.BuildIdentity, "us", opened.Download.LedgerId), default);
        Assert.Equal([content.EncodingKey], saved.VerifiedKeys); Assert.Equal(content.EncodedBytes, N(saved.VerifiedLengths[content.EncodingKey]));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("sha")]
    [InlineData("encoded")]
    [InlineData("decoded")]
    public async Task Incompatible_alias_facts_fail_before_any_reuse_is_credited(string change)
    {
        await using var h = await Harness.Create(); var content = await h.RegisterContent(h.Data.PayloadA); var alias = Metadata(content);
        alias = change switch { "content" => alias with { ContentKey = new('f', 32) }, "sha" => alias with { Sha256 = new('f', 64) },
            "encoded" => alias with { EncodedBytes = N(h.Data.PayloadA.Encoded.Length + 1) }, _ => alias with { DecodedBytes = N(h.Data.PayloadA.Decoded.Length + 1) } };
        await h.Catalog.RecordMetadataAsync(alias, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadRetainedMetadataAsync(default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Open());
        Assert.Equal(0, await h.CountAdmissions());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_corrupt_physical_content_receives_no_reuse_credit(bool corrupt)
    {
        await using var h = await Harness.Create(); var receipt = await h.RegisterContent(h.Data.PayloadA, publish: corrupt);
        if (corrupt)
        {
            var bytes = h.Data.PayloadA.Encoded.ToArray(); bytes[^1] ^= 1; await File.WriteAllBytesAsync(h.ObjectPath(receipt.Sha256), bytes);
        }
        var opened = await h.Open(); Assert.Equal("0", opened.Download!.VerifiedEncodedBytes);
        Assert.Equal(N(h.Data.ExpectedEncodedBytes), opened.Download.RemainingEncodedBytes);
    }

    [Fact]
    public async Task CAS_digest_alone_does_not_replace_BLTE_content_and_encoding_proof()
    {
        await using var h = await Harness.Create(); var bytes = h.Data.PayloadA.Encoded.ToArray(); bytes[^1] ^= 1;
        var receipt = Content(h.Data.PayloadA) with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        await h.Objects.PublishAsync(bytes, receipt.Sha256, default); await h.Catalog.RecordContentAsync(receipt, default);
        var opened = await h.Open(); Assert.Equal("0", opened.Download!.VerifiedEncodedBytes);
    }

    [Fact]
    public async Task Derived_table_row_artifact_is_not_an_encoded_game_object_alias()
    {
        await using var h = await Harness.Create(); await using var rows = new TableRowArtifacts(h.Objects);
        var binding = new TableArtifactBinding(new('a', 64), new('b', 64), new('c', 64), "test-provider", "enUS", "test-decoder", new('d', 64));
        await using var writer = await rows.CreateWriterAsync(binding, default);
        await writer.HeaderAsync(new(1, "50238EC2", "9A2A4834", 1, 0, 4, [new("ID", "integer", true)]), default);
        await writer.RowAsync(new(1, false, null, ["1"]), default);
        var artifact = await writer.CompleteAsync(new(1, 1, 0, 0, 0, 0, "complete", 204), default);
        await h.Catalog.RecordTableArtifactAsync(artifact, default);
        var opened = await h.Open(); Assert.Equal("0", opened.Download!.VerifiedEncodedBytes);
        Assert.DoesNotContain(await h.Catalog.ReadRetainedMetadataAsync(default), item => item.Sha256 == artifact.Sha256);
    }

    [Fact]
    public async Task Saved_assessment_requires_durable_generic_receipt_when_read_again()
    {
        await using var h = await Harness.Create(); await h.RegisterContent(h.Data.PayloadA); var opened = await h.Open();
        await h.Execute("DELETE FROM content_objects");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadDownloadAdmissionAsync(new("wow", h.Data.BuildIdentity, "us", opened.Download!.LedgerId), default));
        Assert.Equal("0", (await h.Open()).Download!.VerifiedEncodedBytes);
    }

    [Fact]
    public async Task Finite_candidate_bound_applies_to_both_sources_together()
    {
        await using var h = await Harness.Create(); var template = Content(h.Data.PayloadA);
        await h.SeedContentAliases(template, 10000);
        await h.Catalog.RecordMetadataAsync(Metadata(Content(h.Data.PayloadB)), default);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadRetainedMetadataAsync(default));
        Assert.Equal("DOWNLOAD_ASSESSMENT_LIMIT", failure.Code);
    }

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static ContentReceipt Content(InventoryFixture.FixtureObject item) => new(item.ContentKey, item.EncodingKey,
        Convert.ToHexStringLower(SHA256.HashData(item.Encoded)), N(item.Encoded.Length), N(item.Decoded.Length), InventoryFixture.Url("data", item.EncodingKey),
        "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", "content-blte-v1", new("loose", "0", N(item.Encoded.Length), null));
    // Authored source-only BLTE receipt: this duplicate alias does not claim a completed ROOT grammar.
    private static MetadataReceipt Metadata(ContentReceipt item) => new("root", item.ContentKey, item.EncodingKey, item.Sha256,
        item.EncodedBytes, item.DecodedBytes, item.SourceUrl, item.RetrievedAt, item.VerifiedAt, MetadataAcquirer.SourceRevision);

    private sealed class Harness : IAsyncDisposable
    {
        private string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-content-reuse-" + Guid.NewGuid().ToString("N"));
        internal InventoryFixture Data { get; } = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal VerifiedObjectStore Objects { get; private set; } = null!;
        private HttpClient client = null!; private DownloadWorkspacePreparer preparer = null!;
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Objects = new(h.Directory);
            h.client = new(new Handler(h.Data)); h.preparer = new(h.Catalog, h.Objects, h.client);
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, h.Data.Discovery, "2026-10-19T00:00:00Z", default); return h;
        }
        internal Task<WorkspaceSnapshot> Open() => preparer.PrepareAsync(new("wow", Data.BuildIdentity, "us"), _ => Task.CompletedTask, default);
        internal async Task<ContentReceipt> RegisterContent(InventoryFixture.FixtureObject item, bool publish = true)
        { var receipt = Content(item); if (publish) await Objects.PublishAsync(item.Encoded, receipt.Sha256, default); await Catalog.RecordContentAsync(receipt, default); return receipt; }
        internal string ObjectPath(string sha) => Path.Combine(Directory, "objects-v1", sha[..2], sha[2..]);
        internal async Task SeedContentAliases(ContentReceipt template, int count)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False");
            await db.OpenAsync(); using var command = db.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<$n)
                INSERT INTO content_objects
                SELECT printf('%032x',i),$c,$h,$e,$d,
                    json_set($r,'$.encodingKey',printf('%032x',i),'$.sourceUrl',$u || printf('%032x',i)) FROM n
                """;
            command.Parameters.AddWithValue("$n", count); command.Parameters.AddWithValue("$c", template.ContentKey);
            command.Parameters.AddWithValue("$h", template.Sha256); command.Parameters.AddWithValue("$e", long.Parse(template.EncodedBytes, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$d", long.Parse(template.DecodedBytes, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$r", JsonSerializer.Serialize(template, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$u", "https://us.cdn.blizzard.com/tpr/wow/data/00/00/");
            await command.ExecuteNonQueryAsync();
        }
        internal async Task Execute(string sql)
        { using var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> CountAdmissions()
        { using var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM download_admissions"; return (long)(await command.ExecuteScalarAsync())!; }
        public async ValueTask DisposeAsync() { preparer.Dispose(); client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!data.Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected authored request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
}
