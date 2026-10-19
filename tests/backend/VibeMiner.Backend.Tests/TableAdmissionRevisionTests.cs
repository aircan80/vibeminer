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

public sealed class TableAdmissionRevisionTests
{
    private const string V3 = "wdc5-fixed-values-v3+table-admission-projection-v1";
    private const string V4 = "wdc5-values-v4+table-admission-projection-v1";
    private const string V5 = "wdc5-values-v5+table-admission-projection-v1";
    private const string V6 = "wdc5-values-v6+table-admission-projection-v1";
    private const string V7 = "wdc5-values-v7+table-admission-projection-v2";

    [Theory]
    [InlineData(V3)]
    [InlineData(V4)]
    [InlineData(V5)]
    [InlineData(V6)]
    [InlineData(V7)]
    public async Task Historical_partial_admission_is_stale_and_rebuilds_offline_without_deleting_source_or_old_artifact(string revision)
    {
        await using var h = await Harness.Create(revision); h.Handler.Offline = true;
        var historical = await h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default);
        Assert.Equal(revision, historical!.Artifact.Binding.DecoderRevision);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        var oldRequests = h.Handler.Requests; var progress = new List<string>();
        var rebuilt = await h.Store.PrepareAsync(h.Request, stage => { progress.Add(stage); return Task.CompletedTask; }, default);
        Assert.Contains("Updating retained table interpretation", progress);
        Assert.Equal(TableDataStore.ProjectionDecoderRevision, rebuilt.DecoderRevision);
        Assert.Equal(oldRequests, h.Handler.Requests); Assert.NotEqual(h.OldArtifact.ContentKey, rebuilt.ArtifactId);
        Assert.Equal(h.Source.SourceUrl, rebuilt.Source.SourceUrl); Assert.Equal(h.Source.RetrievedAt, rebuilt.Source.RetrievedAt);
        Assert.Equal(h.Source.Projection!.IdentitySha256, rebuilt.Source.Projection!.IdentitySha256);
        Assert.Equal(h.OldArtifact.Sha256, (await h.Catalog.FindTableArtifactAsync(h.OldArtifact.ContentKey, default))!.Sha256);
        Assert.Equal(h.Source.Sha256, (await h.Catalog.FindContentAsync(h.Source.EncodingKey, default))!.Sha256);
        var rows = await h.Store.ReadRowsAsync(new("wow", h.Request.BuildIdentity, "us", "50238ec2", "enUS", rebuilt.ArtifactId), default);
        Assert.Equal("1", Assert.Single(rows.Rows).Id);
        Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot);
    }

    [Theory]
    [InlineData(V3)]
    [InlineData(V4)]
    [InlineData(V5)]
    [InlineData(V6)]
    [InlineData(V7)]
    public async Task New_commits_cannot_publish_a_historical_partial_reader_revision(string revision)
    {
        await using var h = await Harness.Create(revision);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context, "50238ec2", "enUS",
            h.Source, h.Association, h.OldArtifact, Now, default));
        Assert.Equal("TABLE_STORAGE_INTEGRITY", error.Code);
        Assert.Equal(revision, (await h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default))!.Artifact.Binding.DecoderRevision);
    }

    [Theory]
    [InlineData("wdc5-fixed-values-v2+table-admission-projection-v1")]
    [InlineData("wdc5-values-v999+table-admission-projection-v1")]
    [InlineData("unrelated-reader")]
    public async Task Unknown_or_undelivered_partial_revisions_are_integrity_failures(string revision)
    {
        await using var h = await Harness.Create(V3); await h.ReplaceArtifact(h.OldArtifact with { Binding = h.OldArtifact.Binding with { DecoderRevision = revision } });
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default));
        Assert.Equal("TABLE_STORAGE_INTEGRITY", error.Code);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("scope")]
    [InlineData("excluded")]
    [InlineData("negative-text")]
    [InlineData("excess-text")]
    public async Task Historical_revision_never_weakens_projection_scope_or_artifact_integrity(string defect)
    {
        await using var h = await Harness.Create(V3); var old = h.OldArtifact;
        var invalid = defect switch
        {
            "proof" => old with { Binding = old.Binding with { DecodedSha256 = new('f', 64) } },
            "scope" => old with { Completion = old.Completion with { Scope = "complete" } },
            "excluded" => old with { Completion = old.Completion with { ExcludedEncryptedSections = 0 } },
            "negative-text" => old with { Completion = old.Completion with { TextIssueCount = -1 } },
            _ => old with { Completion = old.Completion with { TextIssueCount = 1000 } }
        };
        await h.ReplaceArtifact(invalid);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default));
    }

    [Theory]
    [InlineData(V3)]
    [InlineData(V4)]
    [InlineData(V5)]
    [InlineData(V6)]
    [InlineData(V7)]
    [InlineData("wdc5-values-v999+table-admission-projection-v1")]
    public async Task A_full_source_cannot_masquerade_as_any_partial_admission_revision(string revision)
    {
        await using var h = await Harness.Create(V3);
        var full = h.Source with { VerifierRevision = "content-blte-v1", Projection = null };
        await h.Execute("UPDATE content_objects SET receipt=$r", ("$r", JsonSerializer.Serialize(full, Json)));
        await h.Execute("UPDATE table_admissions SET source=$r", ("$r", JsonSerializer.Serialize(full, Json)));
        await h.ReplaceArtifact(h.OldArtifact with { Binding = h.OldArtifact.Binding with { DecoderRevision = revision } });
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadTableAdmissionAsync(h.Context, "50238ec2", "enUS", default));
    }

    private const string Now = "2026-10-19T00:00:00Z", Version = "1.60.1.70450";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Put(byte[] b, int at, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), n);
    private sealed record SourceObject(string Role, string ContentKey, string EncodingKey, byte[] Decoded, byte[] Encoded);
    private static SourceObject Raw(string role, byte[] decoded)
    { byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded]; return new(role, Md5(decoded), Md5(encoded), decoded, encoded); }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vm-admission-revision-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!; private HttpClient client = null!;
        internal Handler Handler { get; private set; } = null!;
        internal TableDataStore Store { get; private set; } = null!;
        internal TableDataRequest Request { get; private set; } = null!;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal ContentReceipt Source { get; private set; } = null!;
        internal SelectedFileAssociation Association { get; private set; } = null!;
        internal TableRowsArtifact OldArtifact { get; private set; } = null!;
        internal static async Task<Harness> Create(string revision)
        {
            var h = new Harness(); var baseFixture = InventoryFixture.Create();
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions["50238ec2"];
            var fields = definition.Document.SelectBuild(Version, "9A2A4834").Blocks.Single().Fields.Where(f => !f.NonInline).ToArray();
            var payload = Table(fields.Length); var root = Root(payload.ContentKey); var en = Encoding([payload, root]);
            var config = System.Text.Encoding.UTF8.GetBytes($"root = {root.ContentKey}\nencoding = {en.ContentKey} {en.EncodingKey}\nencoding-size = {en.Decoded.Length} {en.Encoded.Length}\n");
            var configKey = Md5(config); var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { [InventoryFixture.Url("config", configKey)] = config, [InventoryFixture.Url("config", baseFixture.CdnKey)] = baseFixture.CdnConfig };
            foreach (var obj in new[] { payload, root, en }) responses.Add(InventoryFixture.Url("data", obj.EncodingKey), obj.Encoded);
            var prior = baseFixture.Discovery.Documents.Single(d => d.Role == "versions");
            var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(prior.Bytes).Replace(baseFixture.BuildKey, configKey, StringComparison.Ordinal).Replace("1.2.3.12345", Version, StringComparison.Ordinal));
            var row = MetadataParser.Parse(bytes).Rows.Single(); var identity = BuildIdentity.Create("wow", row.Fields);
            var discovery = baseFixture.Discovery with { Documents = [prior with { Bytes = bytes, Sha256 = Sha(bytes) }, baseFixture.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [baseFixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields }] };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.objects = new(h.directory); h.Handler = new(responses); h.client = new(h.Handler);
            h.Request = new("wow", identity.Id, "us", "50238ec2", "enUS");
            var generation = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", generation, discovery, Now, default);
            using (var configurations = new ConfigurationWorkspacePreparer(h.Catalog, h.objects, h.client))
                await configurations.PrepareAsync(new("wow", identity.Id, "us"), _ => Task.CompletedTask, default);
            h.Context = await h.Catalog.ResolveOpeningAsync(new("wow", identity.Id, "us"), default);
            using (var acquirer = new SelectedFileAcquirer(h.Catalog, h.objects, h.client))
            await using (var selected = await acquirer.AcquireAsync(new("wow", identity.Id, "us"), 841626, "enUS", _ => Task.CompletedTask, default))
            {
                h.Source = selected.Receipt; h.Association = selected.Association;
                var binding = new TableArtifactBinding(h.Source.Sha256, h.Source.Projection!.IdentitySha256, definition.Sha256,
                    provider.Info.Revision, "enUS", revision, TableRowArtifacts.SchemaSelectionKey(definition.Document, Version));
                await using var rows = new TableRowArtifacts(h.objects);
                h.OldArtifact = await rows.BuildAsync(selected.Decoded, definition.Document, Version, binding, _ => Task.CompletedTask, default,
                    new Wdc5Limits(AllowPartialEncryptedSections: true));
                await h.Catalog.RecordTableArtifactAsync(h.OldArtifact, default);
            }
            await h.Execute("""
                INSERT INTO table_admissions VALUES($b,'us',$ref,'50238ec2','enUS',$k,$s,$a,$at)
                """, ("$b", identity.Id), ("$ref", JsonSerializer.Serialize(h.Context.Reference, Json)), ("$k", h.OldArtifact.ContentKey),
                ("$s", JsonSerializer.Serialize(h.Source, Json)), ("$a", JsonSerializer.Serialize(h.Association, Json)), ("$at", Now));
            h.Store = new(h.Catalog, h.objects, h.client); return h;
        }
        internal async Task ReplaceArtifact(TableRowsArtifact artifact)
        {
            artifact = artifact with { ContentKey = TableRowArtifacts.ContentKey(artifact.Binding) };
            await Execute("INSERT OR REPLACE INTO table_artifacts VALUES($k,$d)", ("$k", artifact.ContentKey), ("$d", JsonSerializer.Serialize(artifact, Json)));
            await Execute("UPDATE table_admissions SET content_key=$k", ("$k", artifact.ContentKey));
        }
        internal async Task Execute(string sql, params (string Name, object Value)[] values)
        { using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3") + ";Pooling=False"); await db.OpenAsync(); using var c = db.CreateCommand(); c.CommandText = sql; foreach (var value in values) c.Parameters.AddWithValue(value.Name, value.Value); await c.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync() { if (Store is not null) await Store.DisposeAsync(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(directory, true); }
    }
    private static SourceObject Table(int count)
    {
        var recordSize = count * 4; var start = 204 + 80 + count * 28 + 8; var clearEnd = start + recordSize + 4;
        var body = new byte[clearEnd + recordSize + 4]; "WDC5"u8.CopyTo(body); Put(body, 4, 5); "authored"u8.CopyTo(body.AsSpan(8));
        Put(body, 136, 2); Put(body, 140, (uint)count); Put(body, 144, (uint)recordSize); Put(body, 152, 0x50238ec2); Put(body, 156, 0x9a2a4834);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(172), 4); Put(body, 176, (uint)count); Put(body, 188, (uint)count * 24); Put(body, 200, 2);
        Put(body, 212, (uint)start); Put(body, 216, 1); Put(body, 228, 4);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(244), 1); Put(body, 252, (uint)clearEnd); Put(body, 256, 1); Put(body, 268, 4);
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(284 + i * 4 + 2), (ushort)(i * 4));
            var at = 284 + count * 4 + i * 24; BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), (ushort)(i * 32)); BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 2), 32);
        }
        Put(body, start - 8, 1); Put(body, start - 4, 2); Put(body, start + recordSize, 1); Put(body, clearEnd + recordSize, 2);
        byte[] clear = [(byte)'N', .. body[..clearEnd]];
        byte[] encrypted = [(byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S', 0x99];
        var header = new byte[60]; "BLTE"u8.CopyTo(header); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 60); header[8] = 15; header[11] = 2;
        foreach (var (chunk, length, at) in new[] { (clear, clearEnd, 12), (encrypted, body.Length - clearEnd, 36) })
        { BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), (uint)chunk.Length); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at + 4), (uint)length); MD5.HashData(chunk).CopyTo(header, at + 8); }
        return new("table", Md5(body), Md5(header), body, [.. header, .. clear, .. encrypted]);
    }
    private static SourceObject Root(string key)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write("TSFM"u8); w.Write(24u); w.Write(2u); w.Write(1u); w.Write(0u); w.Write(0u);
        w.Write(1u); w.Write(2u); w.Write(0x10000000u); w.Write(0u); w.Write((byte)0); w.Write(841626u); w.Write(Convert.FromHexString(key));
        return Raw("root", stream.ToArray());
    }
    private static SourceObject Encoding(SourceObject[] entries)
    {
        var contents = entries.OrderBy(e => e.ContentKey, StringComparer.Ordinal).ToArray(); var encodings = entries.OrderBy(e => e.EncodingKey, StringComparer.Ordinal).ToArray();
        var cp = new byte[1024]; var ep = new byte[1024];
        for (var i = 0; i < contents.Length; i++) { var at = i * 38; cp[at] = 1; Put40(cp.AsSpan(at + 1), contents[i].Decoded.Length); Convert.FromHexString(contents[i].ContentKey).CopyTo(cp, at + 6); Convert.FromHexString(contents[i].EncodingKey).CopyTo(cp, at + 22); }
        for (var i = 0; i < encodings.Length; i++) { var at = i * 25; Convert.FromHexString(encodings[i].EncodingKey).CopyTo(ep, at); Put40(ep.AsSpan(at + 20), encodings[i].Encoded.Length); }
        var header = new byte[23]; "EN"u8.CopyTo(header); header[2] = 1; header[3] = header[4] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), 1); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(13), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), 1);
        return Raw("encoding", [.. header, .. Convert.FromHexString(contents[0].ContentKey), .. MD5.HashData(cp), .. cp, .. Convert.FromHexString(encodings[0].EncodingKey), .. MD5.HashData(ep), .. ep]);
    }
    private static void Put40(Span<byte> target, int n) { target[0] = 0; BinaryPrimitives.WriteUInt32BigEndian(target[1..], (uint)n); }
    private sealed class Handler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        internal bool Offline; internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Offline) throw new InvalidOperationException("Offline regression forbids HTTP"); Requests++; if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected authored URL"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) }); }
    }
}
