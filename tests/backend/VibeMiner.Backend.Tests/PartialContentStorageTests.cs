using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class PartialContentStorageTests
{
    [Fact]
    public async Task Partial_receipt_retains_original_proof_without_claiming_full_CKey_verification()
    {
        await using var h = await Harness.Create(); var receipt = h.Receipt;
        await h.Catalog.RecordContentAsync(receipt, default);
        await h.Catalog.RecordContentAsync(receipt with { VerifiedAt = "2026-10-20T00:00:00Z" }, default);
        var saved = await h.Catalog.FindContentAsync(receipt.EncodingKey, default);
        Assert.Equal(receipt.SourceUrl, saved!.SourceUrl); Assert.Equal(receipt.RetrievedAt, saved.RetrievedAt);
        Assert.Equal(receipt.VerifiedAt, saved.VerifiedAt); Assert.Equal(receipt.Projection!.IdentitySha256, saved.Projection!.IdentitySha256);
        Assert.Equal(ContentProjectionIdentity.VerifierRevision, saved.VerifierRevision);
        using var encoded = new MemoryStream(h.Encoded);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(encoded, Stream.Null,
            receipt.EncodingKey, receipt.ContentKey, h.Encoded.Length, h.DecodedLength, default));
        Assert.Equal("BLTE_ENCRYPTED", failure.Code);
        await h.Restart(); Assert.Equal(saved.Projection.IdentitySha256, (await h.Catalog.FindContentAsync(receipt.EncodingKey, default))!.Projection!.IdentitySha256);
    }

    [Fact]
    public async Task Full_and_partial_receipt_discriminants_cannot_be_substituted_or_upgraded()
    {
        await using var h = await Harness.Create();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(h.Receipt with { VerifierRevision = "content-blte-v1" }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(h.Receipt with { Projection = null }, default));
        await h.Catalog.RecordContentAsync(h.Receipt, default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(h.Receipt with { VerifierRevision = "content-blte-v1", Projection = null }, default));
    }

    [Fact]
    public async Task Same_encoded_alias_rejects_different_canonical_projection_identity()
    {
        await using var h = await Harness.Create(); await h.Catalog.RecordContentAsync(h.Receipt, default);
        var changed = h.Projection with { ReadableRanges = [h.Projection.ReadableRanges[0] with { Sha256 = new('f', 64) }] };
        changed = changed with { IdentitySha256 = ContentProjectionIdentity.Compute(h.Receipt.ContentKey, h.Receipt.EncodingKey, h.Receipt.DecodedBytes, changed) };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(h.Receipt with { Projection = changed }, default));
        Assert.Equal(h.Projection.IdentitySha256, (await h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default))!.Projection!.IdentitySha256);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("hash")]
    [InlineData("gap")]
    [InlineData("policy")]
    [InlineData("ordinal")]
    public async Task Invalid_partial_proof_is_refused_before_any_alias_publication(string change)
    {
        await using var h = await Harness.Create(); var proof = h.Projection;
        proof = change switch
        {
            "identity" => proof with { IdentitySha256 = new('f', 64) },
            "hash" => proof with { ReadableRanges = [proof.ReadableRanges[0] with { Sha256 = new('f', 64) }] },
            "gap" => proof with { UnavailableRanges = [proof.UnavailableRanges[0] with { Offset = "6" }] },
            "policy" => proof with { Policy = "unknown" },
            _ => proof with { UnavailableRanges = [proof.UnavailableRanges[0] with { ChunkOrdinal = 0 }] }
        };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordContentAsync(h.Receipt with { Projection = proof }, default));
        Assert.Null(await h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default));
    }

    [Fact]
    public async Task Partial_table_admission_binds_projection_identity_revision_and_explicit_excluded_scope()
    {
        await using var h = await Harness.Create(); await h.Open(); await h.Catalog.RecordContentAsync(h.Receipt, default);
        var association = await h.Association(); var context = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        var artifact = h.Artifact(); await h.Catalog.RecordTableArtifactAsync(artifact, default);
        await h.Catalog.CommitTableAdmissionAsync(context, "50238ec2", "enUS", h.Receipt, association, artifact, Now, default);
        var saved = await h.Catalog.ReadTableAdmissionAsync(context, "50238ec2", "enUS", default);
        Assert.Equal(h.Projection.IdentitySha256, saved!.Artifact.Binding.DecodedSha256);
        Assert.Equal("unencryptedSections", saved.Artifact.Completion.Scope);
        Assert.Equal(h.Projection.IdentitySha256, saved.Source.Projection!.IdentitySha256);
        var invalidOrdinal = 0;
        foreach (var candidate in new[] { artifact with { Completion = artifact.Completion with { Scope = "complete" } },
            artifact with { Completion = artifact.Completion with { ExcludedEncryptedSections = 0 } },
            artifact with { Binding = artifact.Binding with { DecodedSha256 = new('9', 64) } },
            artifact with { Binding = artifact.Binding with { DecoderRevision = TableDataStore.DecoderRevision } } })
        {
            // Register an internally consistent independent descriptor first, so these assertions
            // exercise the partial-admission guards rather than an unrelated missing-artifact check.
            var binding = candidate.Binding with { SchemaSelectionSha256 = Sha(Encoding.UTF8.GetBytes($"invalid-{invalidOrdinal++}")) };
            var invalid = candidate with { Binding = binding, ContentKey = TableRowArtifacts.ContentKey(binding) };
            await h.Catalog.RecordTableArtifactAsync(invalid, default);
            await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(context, "50238ec2", "enUS", h.Receipt, association, invalid, Now, default));
        }
    }

    [Fact]
    public async Task Tampered_persisted_proof_is_rejected_on_content_and_table_admission_reads()
    {
        await using var h = await Harness.Create(); await h.Open(); await h.Catalog.RecordContentAsync(h.Receipt, default);
        var association = await h.Association(); var context = await h.Catalog.ResolveOpeningAsync(h.Request, default); var artifact = h.Artifact();
        await h.Catalog.RecordTableArtifactAsync(artifact, default);
        await h.Catalog.CommitTableAdmissionAsync(context, "50238ec2", "enUS", h.Receipt, association, artifact, Now, default);
        await h.Execute("UPDATE content_objects SET receipt=json_set(receipt,'$.projection.readableRanges[0].sha256',$hash)", ("$hash", new string('f', 64)));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadTableAdmissionAsync(context, "50238ec2", "enUS", default));
    }

    [Fact]
    public async Task Retained_partial_encoded_object_gets_exact_DL_credit_after_actual_projection_replay()
    {
        await using var h = await Harness.Create(); await h.Publish(h.Receipt, h.Encoded);
        var opened = await h.Open(); Assert.Equal(N(h.Encoded.Length), opened.Download!.VerifiedEncodedBytes);
        Assert.Equal("0", opened.Download.RemainingEncodedBytes);
        var item = Assert.Single(await h.Catalog.ReadRetainedMetadataAsync(default), item => item.Key == h.Receipt.EncodingKey);
        Assert.Equal(h.Projection.IdentitySha256, item.Projection!.IdentitySha256);
        var admission = await h.Catalog.ReadDownloadAdmissionAsync(new("wow", h.Request.BuildIdentity, "us", opened.Download.LedgerId), default);
        Assert.Equal([h.Receipt.EncodingKey], admission.VerifiedKeys);
        Assert.Equal(h.Encoded.Length, admission.VerifiedLengths[h.Receipt.EncodingKey]);
        await h.Restart(); Assert.Equal(N(h.Encoded.Length), (await h.Open()).Download!.VerifiedEncodedBytes);
    }

    [Fact]
    public async Task A_canonical_but_substituted_clear_hash_never_earns_partial_encoded_credit()
    {
        await using var h = await Harness.Create(); var proof = h.Projection with { ReadableRanges = [h.Projection.ReadableRanges[0] with { Sha256 = new('f', 64) }] };
        proof = proof with { IdentitySha256 = ContentProjectionIdentity.Compute(h.Receipt.ContentKey, h.Receipt.EncodingKey, h.Receipt.DecodedBytes, proof) };
        await h.Publish(h.Receipt with { Projection = proof }, h.Encoded);
        Assert.Equal("0", (await h.Open()).Download!.VerifiedEncodedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_encoded_partial_object_receives_no_credit_even_when_its_local_SHA_is_updated(bool updateSha)
    {
        await using var h = await Harness.Create(); var bytes = h.Encoded.ToArray(); bytes[^1] ^= 1;
        if (updateSha) await h.Publish(h.Receipt with { Sha256 = Sha(bytes) }, bytes);
        else
        {
            await h.Publish(h.Receipt, h.Encoded);
            await File.WriteAllBytesAsync(h.ObjectPath(h.Receipt.Sha256), bytes);
        }
        Assert.Equal("0", (await h.Open()).Download!.VerifiedEncodedBytes);
    }

    [Fact]
    public async Task Partial_and_full_metadata_aliases_cannot_masquerade_as_the_same_verification_scope()
    {
        await using var h = await Harness.Create(); await h.Publish(h.Receipt, h.Encoded);
        var r = h.Receipt;
        await h.Catalog.RecordMetadataAsync(new("root", r.ContentKey, r.EncodingKey, r.Sha256, r.EncodedBytes, r.DecodedBytes,
            r.SourceUrl, r.RetrievedAt, r.VerifiedAt, MetadataAcquirer.SourceRevision), default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadRetainedMetadataAsync(default));
    }

    private const string Now = "2026-10-19T00:00:00Z";
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static void Big(Stream stream, long value, int width) { for (var i = width - 1; i >= 0; i--) stream.WriteByte((byte)(value >> (i * 8))); }
    private sealed class Harness : IAsyncDisposable
    {
        private string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-partial-receipt-" + Guid.NewGuid().ToString("N"));
        internal byte[] Encoded { get; }
        internal long DecodedLength => 8;
        internal ContentProjection Projection { get; }
        internal ContentReceipt Receipt { get; }
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!; private DownloadWorkspacePreparer preparer = null!; private HttpClient client = null!;
        internal WorkspaceRequest Request { get; private set; } = null!;
        private readonly InventoryFixture original = InventoryFixture.Create();
        private readonly Dictionary<string, byte[]> responses = new(StringComparer.Ordinal);
        private Harness()
        {
            byte[] clear = "clear"u8.ToArray(); byte[][] chunks = [[(byte)'N', ..clear], [(byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0x11, 0x22, 0x33, 0x44, (byte)'S', 0x99]];
            using var header = new MemoryStream(); header.Write("BLTE"u8); Big(header, 60, 4); header.Write([15, 0, 0, 2]);
            for (var i = 0; i < chunks.Length; i++) { Big(header, chunks[i].Length, 4); Big(header, i == 0 ? 5 : 3, 4); header.Write(MD5.HashData(chunks[i])); }
            var headerBytes = header.ToArray(); Encoded = [..headerBytes, ..chunks[0], ..chunks[1]];
            var ck = new string('1', 32); var ek = Md5(headerBytes);
            var proof = new ContentProjection(ContentProjectionIdentity.Policy, "", [new("0", "5", Sha(clear))], [new("5", "3", 1, "0807060504030201")]);
            Projection = proof with { IdentitySha256 = ContentProjectionIdentity.Compute(ck, ek, "8", proof) };
            Receipt = new(ck, ek, Sha(Encoded), N(Encoded.Length), "8", InventoryFixture.Url("data", ek), Now, Now,
                ContentProjectionIdentity.VerifierRevision, new("loose", "0", N(Encoded.Length), null), Projection);
        }
        internal static async Task<Harness> Create()
        {
            var h = new Harness();
            using var dl = new MemoryStream(); dl.Write("DL"u8); dl.Write([1, 16, 0]); Big(dl, 1, 4); Big(dl, 0, 2);
            dl.Write(Convert.FromHexString(h.Receipt.EncodingKey)); Big(dl, h.Encoded.Length, 5); dl.WriteByte(0);
            var decoded = dl.ToArray(); byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', ..decoded];
            var old = h.original.Metadata["download"]; var dk = Md5(decoded); var ek = Md5(encoded);
            var text = Encoding.UTF8.GetString(h.original.BuildConfig).Replace(old.ContentKey, dk, StringComparison.Ordinal).Replace(old.EncodingKey, ek, StringComparison.Ordinal)
                .Replace($"download-size = {old.Decoded.Length} {old.Encoded.Length}", $"download-size = {decoded.Length} {encoded.Length}", StringComparison.Ordinal);
            var config = Encoding.UTF8.GetBytes(text); var configKey = Md5(config);
            foreach (var pair in h.original.Responses) h.responses.Add(pair.Key, pair.Value);
            h.responses[InventoryFixture.Url("config", configKey)] = config; h.responses[InventoryFixture.Url("data", ek)] = encoded;
            var versions = h.original.Discovery.Documents.Single(d => d.Role == "versions");
            var versionBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(versions.Bytes).Replace(h.original.BuildKey, configKey, StringComparison.Ordinal));
            var fields = MetadataParser.Parse(versionBytes).Rows.Single().Fields; var id = BuildIdentity.Create("wow", fields);
            var discovery = h.original.Discovery with { Documents = [versions with { Bytes = versionBytes, Sha256 = Sha(versionBytes) }, h.original.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [h.original.Discovery.Observations.Single() with { Identity = id, OriginalFields = fields }] };
            h.Request = new("wow", id.Id, "us"); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.objects = new(h.Directory);
            h.client = new(new Handler(h.responses)); h.preparer = new(h.Catalog, h.objects, h.client);
            var generation = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", generation, discovery, Now, default); return h;
        }
        internal Task<WorkspaceSnapshot> Open() => preparer.PrepareAsync(Request, _ => Task.CompletedTask, default);
        internal async Task Publish(ContentReceipt receipt, byte[] bytes) { await objects.PublishAsync(bytes, receipt.Sha256, default); await Catalog.RecordContentAsync(receipt, default); }
        internal string ObjectPath(string sha) => Path.Combine(Directory, "objects-v1", sha[..2], sha[2..]);
        internal async Task Restart() { preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); objects = new(Directory); preparer = new(Catalog, objects, client); }
        internal async Task<SelectedFileAssociation> Association()
        {
            var receipts = new List<MetadataReceipt>(); foreach (var role in new[] { "encoding", "root" })
            {
                var o = original.Metadata[role]; var receipt = new MetadataReceipt(role, o.ContentKey, o.EncodingKey, Sha(o.Encoded), N(o.Encoded.Length), N(o.Decoded.Length), InventoryFixture.Url("data", o.EncodingKey), Now, Now, MetadataAcquirer.SourceRevision);
                await Catalog.RecordMetadataAsync(receipt, default); receipts.Add(receipt);
            }
            return new("841626", "enUS", "enus-consistent-root-variants-v1", receipts[0], receipts[1], [new("0", "0", Receipt.ContentKey, "2", "0", "0", "0", "0", null)]);
        }
        internal TableRowsArtifact Artifact()
        {
            var b = new TableArtifactBinding(Receipt.Sha256, Projection.IdentitySha256, new('3', 64), "provider", "enUS", TableDataStore.ProjectionDecoderRevision, new('4', 64));
            return new(TableRowArtifacts.ContentKey(b), new('5', 64), 1024, b, new(5, "50238EC2", "996192AA", 2, 0, 4, [new("ID", "integer", true)]), new(2, 1, 0, 1, 1, 0, "unencryptedSections", DecodedLength));
        }
        internal async Task Execute(string sql, params (string Name, object Value)[] parameters)
        { using var db = new SqliteConnection("Data Source=" + Path.Combine(Directory, "discovery-v1.sqlite3") + ";Pooling=False"); await db.OpenAsync(); using var c = db.CreateCommand(); c.CommandText = sql; foreach (var p in parameters) c.Parameters.AddWithValue(p.Name, p.Value); await c.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync() { preparer.Dispose(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Handler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected authored request"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) }); }
    }
}
