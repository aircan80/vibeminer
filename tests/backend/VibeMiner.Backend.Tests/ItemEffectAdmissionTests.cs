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

public sealed class ItemEffectAdmissionTests
{
    private const string Effect = "4002a5b1", Version = "12.1.5.70448", Now = "2026-10-19T00:00:00Z";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_bundled_profile_preserves_three_whole_raw_columns_and_reopens_offline(bool projected)
    {
        await using var h = await Harness.Create(projected);
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal(projected ? TableDataStore.ItemEffectProjectionDecoderRevision : TableDataStore.ItemEffectDecoderRevision, snapshot.DecoderRevision);
        Assert.Equal("4CA77678", snapshot.LayoutHash);
        var stored = (await h.ReadAdmission())!;
        Assert.Equal("4002A5B1", stored.Artifact.Header.TableHash); // Native uppercase versus canonical catalog lowercase.
        Assert.Equal(TableRowArtifacts.ContentKey(stored.Artifact.Binding), snapshot.ArtifactId);
        foreach (var ordinal in new[] { 3, 6, 8 })
        {
            var column = snapshot.Columns[ordinal]; var interpretation = column.Interpretation!;
            Assert.Equal("raw-physical-integer-v1", interpretation.Policy); Assert.Equal(32, interpretation.PhysicalBits);
            Assert.False(interpretation.PhysicalSigned); Assert.Equal(16, interpretation.CandidateBits);
            Assert.Equal(ordinal != 3, interpretation.CandidateUnsigned); Assert.Null(column.Reference);
            Assert.NotNull(stored.Artifact.Header.Columns[ordinal].Interpretation);
        }
        Assert.Equal(new[] { "Charges", "SpellCategoryID", "ChrSpecializationID" }, snapshot.Columns.Where(c => c.Interpretation is not null).Select(c => c.Name));
        Assert.Equal("Spell", snapshot.Columns[7].Reference!.Table); Assert.Null(snapshot.Columns[7].Interpretation);
        var selected = h.Definition.Document.SelectBuild(Version, "4CA77678").Blocks.Single();
        Assert.False(selected.Fields[9].Column.VerifiedName); Assert.False(selected.Fields[3].Unsigned);
        Assert.Equal("SpellCategory", selected.Fields[6].Column.Reference!.Table);
        Assert.Equal("ChrSpecialization", selected.Fields[8].Column.Reference!.Table);
        var page = await h.ReadRows(snapshot.ArtifactId);
        Assert.Equal(new[] { "4294967295", "65535", "65536", "1", "2147483648", "4294967295" }, page.Rows.Select(r => r.Values[3]));
        var copy = Assert.Single(page.Rows, r => r.IsCopy); Assert.Equal("99", copy.Id); Assert.Equal("10", copy.CopySourceId); Assert.Equal("99", copy.Values[0]);
        Assert.Equal("6", snapshot.RowCount);
        Assert.Equal(projected ? "unencryptedSections" : "complete", snapshot.Coverage.Scope);
        Assert.Equal(projected ? "1" : "0", snapshot.Coverage.ExcludedKeyedSections);
        Assert.Equal(projected ? "1" : "0", snapshot.Coverage.ExcludedPhysicalRecords);
        Assert.Equal(projected ? "1" : "0", snapshot.Coverage.ExcludedCopyRecords);
        Assert.Equal(projected, snapshot.Source.Projection is not null);
        var oldBinding = stored.Artifact.Binding with { DecoderRevision = projected ? TableDataStore.ProjectionDecoderRevision : TableDataStore.DecoderRevision };
        Assert.NotEqual(snapshot.ArtifactId, TableRowArtifacts.ContentKey(oldBinding));
        var requests = h.Handler.Requests; h.Handler.Offline = true; await h.Restart();
        var reopened = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal(snapshot.ArtifactId, reopened.ArtifactId); Assert.Equal(snapshot.PreparedAt, reopened.PreparedAt);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Source, Json), JsonSerializer.Serialize(reopened.Source, Json));
        Assert.Equal(requests, h.Handler.Requests); Assert.Equal("6", (await h.ReadRows(reopened.ArtifactId)).TotalCount);
    }

    [Fact]
    public async Task Full_plaintext_receipt_can_still_have_excluded_native_sections()
    {
        await using var h = await Harness.Create(false, nativePartial: true);
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal(TableDataStore.ItemEffectDecoderRevision, snapshot.DecoderRevision);
        Assert.Null(snapshot.Source.Projection); Assert.Equal("content-blte-v1", snapshot.Source.VerifierRevision);
        Assert.Equal("unencryptedSections", snapshot.Coverage.Scope); Assert.Equal("1", snapshot.Coverage.ExcludedKeyedSections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_effect_v3_is_source_readable_but_stale_and_refreshes_offline_without_rewriting_old_proofs(bool projected)
    {
        await using var h = await Harness.Create(projected, legacy: true);
        var old = (await h.ReadAdmission())!; var oldDescriptor = JsonSerializer.Serialize(old.Artifact, Json);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        var stale = await Assert.ThrowsAsync<DiscoveryException>(() => h.ReadRows(old.Artifact.ContentKey));
        Assert.Equal("TABLE_INTERPRETATION_STALE", stale.Code);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context, Effect, "enUS",
            old.Source, old.Association, old.Artifact, Now, default));
        Assert.Equal("TABLE_STORAGE_INTEGRITY", error.Code);
        var requests = h.Handler.Requests; h.Handler.Offline = true; var progress = new List<string>();
        var fresh = await h.Store.PrepareAsync(h.Request, stage => { progress.Add(stage); return Task.CompletedTask; }, default);
        Assert.Contains("Updating retained table interpretation", progress); Assert.NotEqual(old.Artifact.ContentKey, fresh.ArtifactId);
        Assert.Equal(projected ? TableDataStore.ItemEffectProjectionDecoderRevision : TableDataStore.ItemEffectDecoderRevision, fresh.DecoderRevision);
        // Revalidation returns a fresh verification observation; the original persisted receipt stays byte-identical below.
        Assert.Equal(JsonSerializer.Serialize(old.Source, Json), JsonSerializer.Serialize(fresh.Source with { VerifiedAt = old.Source.VerifiedAt }, Json));
        Assert.Equal(oldDescriptor, JsonSerializer.Serialize(await h.Catalog.FindTableArtifactAsync(old.Artifact.ContentKey, default), Json));
        Assert.Equal(JsonSerializer.Serialize(old.Source, Json), JsonSerializer.Serialize(await h.Catalog.FindContentAsync(old.Source.EncodingKey, default), Json));
        Assert.Equal(requests, h.Handler.Requests); Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        var replaced = await Assert.ThrowsAsync<DiscoveryException>(() => h.ReadRows(old.Artifact.ContentKey));
        Assert.Equal("TABLE_ARTIFACT_STALE", replaced.Code);
        await h.Restart(); Assert.Equal(fresh.ArtifactId, (await h.Store.ReadAsync(h.Request, default)).Snapshot!.ArtifactId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_current_v3_retained_rows_keep_identical_key_bytes_and_eligibility(bool projected)
    {
        await using var h = await Harness.Create(projected, legacy: true, tableId: "50238ec2");
        var old = (await h.ReadAdmission())!; var descriptor = JsonSerializer.Serialize(old.Artifact, Json);
        Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        // New unrelated v3 commits remain valid, independently of historical read compatibility.
        await h.Catalog.CommitTableAdmissionAsync(h.Context, h.Request.TableId, "enUS", old.Source, old.Association, old.Artifact, Now, default);
        var requests = h.Handler.Requests; h.Handler.Offline = true; await h.Restart(); var progress = new List<string>();
        var snapshot = await h.Store.PrepareAsync(h.Request, stage => { progress.Add(stage); return Task.CompletedTask; }, default);
        Assert.Equal(old.Artifact.ContentKey, snapshot.ArtifactId); Assert.Equal(Now, snapshot.PreparedAt);
        Assert.Equal(descriptor, JsonSerializer.Serialize((await h.ReadAdmission())!.Artifact, Json));
        Assert.DoesNotContain("Updating retained table interpretation", progress); Assert.Equal(requests, h.Handler.Requests);
    }

    [Theory]
    [InlineData("50238ec2", "Item")]
    [InlineData("919be54e", "ItemSparse")]
    [InlineData("95d8638f", "ItemSubClass")]
    [InlineData("bfdaf9f1", "CreatureDisplayInfo")]
    [InlineData("7f695fa5", "CreatureModelData")]
    public async Task Representative_unaffected_bundled_bindings_keep_exact_v3_identity(string tableId, string name)
    {
        var provider = await TablesCatalog.Provider.Value;
        // Assert names as well as digests so a mistaken test table cannot silently lose scope.
        var definition = provider.Definitions[tableId]; Assert.Equal(name, provider.Entries.Single(e => e.TableId == tableId).Name);
        foreach (var projected in new[] { false, true })
        {
            var expected = projected ? "wdc5-values-v7+table-admission-projection-v3" : "wdc5-values-v7+table-admission-v3";
            var before = new TableArtifactBinding(new('a', 64), new('b', 64), definition.Sha256, provider.Info.Revision, "enUS", expected, TableRowArtifacts.SchemaSelectionKey(definition.Document, Version));
            var after = before with { DecoderRevision = TableDataStore.AdmissionRevision(tableId, definition.Sha256, provider.Info.Revision, projected) };
            Assert.Equal(JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json));
            Assert.Equal(TableRowArtifacts.ContentKey(before), TableRowArtifacts.ContentKey(after));
        }
    }

    [Theory]
    [InlineData("table", false)] [InlineData("table", true)]
    [InlineData("definition", false)] [InlineData("definition", true)]
    [InlineData("provider", false)] [InlineData("provider", true)]
    public async Task Forged_v4_is_rejected_at_registration_lookup_and_admission_read(string defect, bool projected)
    {
        await using var h = await Harness.Create(projected);
        await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default); var saved = (await h.ReadAdmission())!;
        var artifact = defect switch
        {
            "table" => saved.Artifact with { Header = saved.Artifact.Header with { TableHash = "50238EC2" } },
            "definition" => saved.Artifact with { Binding = saved.Artifact.Binding with { DefinitionSha256 = new('f', 64) } },
            _ => saved.Artifact with { Binding = saved.Artifact.Binding with { ProviderRevision = "wrong-provider" } }
        };
        artifact = Rekey(artifact);
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordTableArtifactAsync(artifact, default))).Code);
        await h.ReplaceArtifact(artifact);
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindTableArtifactAsync(artifact.ContentKey, default))).Code);
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.ReadAdmission())).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V4_full_and_projection_admission_revisions_cannot_be_swapped(bool projected)
    {
        await using var h = await Harness.Create(projected);
        await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default); var saved = (await h.ReadAdmission())!;
        var wrong = Rekey(saved.Artifact with { Binding = saved.Artifact.Binding with
            { DecoderRevision = projected ? TableDataStore.ItemEffectDecoderRevision : TableDataStore.ItemEffectProjectionDecoderRevision } });
        await h.Catalog.RecordTableArtifactAsync(wrong, default);
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context,
            Effect, "enUS", saved.Source, saved.Association, wrong, Now, default))).Code);
        await h.ReplaceArtifact(wrong);
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.ReadAdmission())).Code);
    }

    [Theory]
    [InlineData("identity")] [InlineData("scope")] [InlineData("exclusions")] [InlineData("length")] [InlineData("association")]
    public async Task V4_preserves_projection_and_source_guards_without_replacing_valid_admission(string defect)
    {
        await using var h = await Harness.Create(true);
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default); var saved = (await h.ReadAdmission())!;
        var artifact = defect switch
        {
            "identity" => saved.Artifact with { Binding = saved.Artifact.Binding with { DecodedSha256 = new('f', 64) } },
            "scope" => saved.Artifact with { Completion = saved.Artifact.Completion with { Scope = "complete" } },
            "exclusions" => saved.Artifact with { Completion = saved.Artifact.Completion with { ExcludedEncryptedSections = 0 } },
            "length" => saved.Artifact with { Completion = saved.Artifact.Completion with { ConsumedBytes = 1 } },
            _ => saved.Artifact
        };
        // Independent immutable descriptor avoids testing a registration collision instead of admission validation.
        artifact = Rekey(artifact with { Binding = artifact.Binding with { SchemaSelectionSha256 = new('e', 64) } });
        await h.Catalog.RecordTableArtifactAsync(artifact, default);
        var association = defect == "association" ? saved.Association with { EncodingSource = saved.Association.EncodingSource with { Sha256 = new('f', 64) } } : saved.Association;
        Assert.Equal(defect == "association" ? "DOWNLOAD_CATALOG_INTEGRITY" : "TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitTableAdmissionAsync(h.Context,
            Effect, "enUS", saved.Source, association, artifact, Now, default))).Code);
        Assert.Equal(snapshot.ArtifactId, (await h.ReadAdmission())!.Artifact.ContentKey);
    }

    [Theory]
    [InlineData("layout", "DB2_SCHEMA_MISSING")]
    [InlineData("table", "DB2_SCHEMA_MISMATCH")] // Without the rule Charges' upper bits vary (65536, 0x80000000), so it can't narrow (D087).
    [InlineData("kind", "DB2_SCHEMA_MISMATCH")]
    [InlineData("palette", "DB2_INVALID")]
    public async Task Actual_production_profile_does_not_relax_unmatched_or_malformed_metadata(string defect, string code)
    {
        await using var h = await Harness.Create(false, defect: defect);
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default))).Code);
        Assert.Null(await h.ReadAdmission()); Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
    }

    [Fact]
    public async Task Original_pure_reader_refuses_varying_upper_bits_without_the_adapter_rule_and_keeps_canonical_signed_cases()
    {
        await using var h = await Harness.Create(false);
        var sink = new Sink(); var strictBytes = h.Payload.Decoded.ToArray();
        var start = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(strictBytes.AsSpan(212)));
        Put(strictBytes, start + 8, 2); // First row chooses the authored noncanonical Charges palette entry 65536.
        using var input = new MemoryStream(strictBytes);
        // Without the exact retail rule, Charges' misfit pallet values carry two upper-bit patterns, so D087 refuses it before Header.
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => Wdc5TableReader.ReadAsync(input, h.Definition.Document, Version, new(), sink));
        Assert.Equal("DB2_SCHEMA_MISMATCH", failure.Code); Assert.Contains("'Charges'", failure.Message); Assert.Contains("(0x10000, 0x80000000)", failure.Message);
        Assert.Null(sink.Header); Assert.Empty(sink.Rows);
        // The adapter opt-in preserves even canonical-negative bits as raw uint32, tested above.
        // A separate strict all-fit source proves the unchanged candidate signed16 decoding path.
        var payload = Table(h.Definition, Effect, "4CA77678", false, true, null);
        input.SetLength(0); await input.WriteAsync(payload.Decoded); input.Position = 0; sink = new();
        await Wdc5TableReader.ReadAsync(input, h.Definition.Document, Version, new(), sink);
        Assert.Equal(new[] { "-1", "-1", "1", "2", "3", "-1" }, sink.Rows.Select(r => r.Values[3]));
        Assert.Null(sink.Header!.Columns[3].Interpretation); Assert.Null(sink.Header.Columns[3].Narrowing);
    }

    [Fact]
    public async Task Cancellation_before_refresh_publication_preserves_historical_admission_and_source()
    {
        await using var h = await Harness.Create(true, legacy: true); var old = (await h.ReadAdmission())!;
        h.Handler.Offline = true; using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.PrepareAsync(h.Request, stage =>
        { if (stage == "Saving exact table admission") cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(old.Artifact.ContentKey, (await h.ReadAdmission())!.Artifact.ContentKey);
        Assert.Equal(JsonSerializer.Serialize(old.Source, Json), JsonSerializer.Serialize(await h.Catalog.FindContentAsync(old.Source.EncodingKey, default), Json));
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.NotEqual(old.Artifact.ContentKey, (await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default)).ArtifactId);
    }

    private static TableRowsArtifact Rekey(TableRowsArtifact artifact) => artifact with { ContentKey = TableRowArtifacts.ContentKey(artifact.Binding) };
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Put(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Half(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    private sealed record SourceObject(string ContentKey, string EncodingKey, byte[] Decoded, byte[] Encoded);
    private static SourceObject Raw(byte[] decoded)
    { byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded]; return new(Md5(decoded), Md5(encoded), decoded, encoded); }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vm-effect-policy-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!; private HttpClient client = null!;
        internal Handler Handler { get; private set; } = null!;
        internal TableDataStore Store { get; private set; } = null!;
        internal TableDataRequest Request { get; private set; } = null!;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal BundledTableDefinition Definition { get; private set; } = null!;
        internal SourceObject Payload { get; private set; } = null!;
        internal static async Task<Harness> Create(bool projected, bool legacy = false, string tableId = Effect, bool nativePartial = false, string? defect = null)
        {
            var h = new Harness(); var fixture = InventoryFixture.Create(); var provider = await TablesCatalog.Provider.Value;
            h.Definition = provider.Definitions[tableId]; var entry = provider.Entries.Single(e => e.TableId == tableId);
            h.Payload = Table(h.Definition, tableId, tableId == Effect ? "4CA77678" : "996192AA", projected || nativePartial, legacy || tableId != Effect, defect, projected);
            var root = Root(h.Payload.ContentKey, uint.Parse(entry.Db2FileDataId!, CultureInfo.InvariantCulture)); var encoding = EncodingObject([h.Payload, root]);
            var config = Encoding.UTF8.GetBytes($"root = {root.ContentKey}\nencoding = {encoding.ContentKey} {encoding.EncodingKey}\nencoding-size = {encoding.Decoded.Length} {encoding.Encoded.Length}\n");
            var configKey = Md5(config); var responses = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            { [InventoryFixture.Url("config", configKey)] = config, [InventoryFixture.Url("config", fixture.CdnKey)] = fixture.CdnConfig };
            foreach (var obj in new[] { h.Payload, root, encoding }) responses.Add(InventoryFixture.Url("data", obj.EncodingKey), obj.Encoded);
            var prior = fixture.Discovery.Documents.Single(d => d.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(prior.Bytes).Replace(fixture.BuildKey, configKey, StringComparison.Ordinal).Replace("1.2.3.12345", Version, StringComparison.Ordinal));
            var row = MetadataParser.Parse(bytes).Rows.Single(); var identity = BuildIdentity.Create("wow", row.Fields);
            var discovery = fixture.Discovery with { Documents = [prior with { Bytes = bytes, Sha256 = Sha(bytes) }, fixture.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [fixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields }] };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.objects = new(h.directory); h.Handler = new(responses); h.client = new(h.Handler);
            h.Request = new("wow", identity.Id, "us", tableId, "enUS");
            var generation = await h.Catalog.BeginRefreshAsync("wow", default); await h.Catalog.CommitAsync("wow", generation, discovery, Now, default);
            using (var configurations = new ConfigurationWorkspacePreparer(h.Catalog, h.objects, h.client))
                await configurations.PrepareAsync(new("wow", identity.Id, "us"), _ => Task.CompletedTask, default);
            h.Context = await h.Catalog.ResolveOpeningAsync(new("wow", identity.Id, "us"), default);
            h.Store = new(h.Catalog, h.objects, h.client);
            if (legacy)
            {
                using var acquisition = new SelectedFileAcquirer(h.Catalog, h.objects, h.client);
                await using var selected = await acquisition.AcquireAsync(new("wow", identity.Id, "us"), uint.Parse(entry.Db2FileDataId!, CultureInfo.InvariantCulture), "enUS", _ => Task.CompletedTask, default);
                var decodedHash = selected.Receipt.Projection?.IdentitySha256 ?? Convert.ToHexStringLower(await SHA256.HashDataAsync(selected.Decoded)); selected.Decoded.Position = 0;
                var binding = new TableArtifactBinding(selected.Receipt.Sha256, decodedHash, h.Definition.Sha256, provider.Info.Revision, "enUS",
                    projected ? TableDataStore.ProjectionDecoderRevision : TableDataStore.DecoderRevision, TableRowArtifacts.SchemaSelectionKey(h.Definition.Document, Version));
                await using var rows = new TableRowArtifacts(h.objects);
                var old = await rows.BuildAsync(selected.Decoded, h.Definition.Document, Version, binding, _ => Task.CompletedTask, default, new(AllowPartialEncryptedSections: true));
                await h.Catalog.RecordTableArtifactAsync(old, default);
                // Historical database state intentionally bypasses the current-commit API whose refusal is under test.
                await h.Execute("INSERT INTO table_admissions VALUES($b,'us',$ref,$t,'enUS',$k,$s,$a,$at)", ("$b", identity.Id),
                    ("$ref", JsonSerializer.Serialize(h.Context.Reference, Json)), ("$t", tableId), ("$k", old.ContentKey),
                    ("$s", JsonSerializer.Serialize(selected.Receipt, Json)), ("$a", JsonSerializer.Serialize(selected.Association, Json)), ("$at", Now));
            }
            return h;
        }
        internal Task<StoredTableAdmission?> ReadAdmission() => Catalog.ReadTableAdmissionAsync(Context, Request.TableId, "enUS", default);
        internal Task<TableDataPage> ReadRows(string artifact) => Store.ReadRowsAsync(new("wow", Request.BuildIdentity, "us", Request.TableId, "enUS", artifact), default);
        internal async Task Restart()
        { await Store.DisposeAsync(); await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(directory); Store = new(Catalog, objects, client); }
        internal async Task ReplaceArtifact(TableRowsArtifact artifact)
        {
            await Execute("INSERT OR REPLACE INTO table_artifacts VALUES($k,$d)", ("$k", artifact.ContentKey), ("$d", JsonSerializer.Serialize(artifact, Json)));
            await Execute("UPDATE table_admissions SET content_key=$k", ("$k", artifact.ContentKey));
        }
        private async Task Execute(string sql, params (string Name, object Value)[] values)
        { using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "discovery-v1.sqlite3") + ";Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value); await command.ExecuteNonQueryAsync(); }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); client.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(directory, true); }
    }

    // Original authored fixed WDC5 data using public schema metadata. No native rows or palettes are copied.
    private static SourceObject Table(BundledTableDefinition definition, string tableId, string layout, bool nativePartial, bool fits, string? defect, bool projected = false)
    {
        var fields = definition.Document.SelectBuild(Version, layout).Blocks.Single().Fields.Where(f => !f.NonInline).ToArray();
        Assert.All(fields, f => Assert.Equal(1, f.ArrayLength));
        var count = fields.Length; const int physical = 5; var sections = nativePartial ? 2 : 1; var recordSize = count * 4;
        uint[] charges = fits ? [uint.MaxValue, 65535, 1, 2, 3] : [uint.MaxValue, 65535, 65536, 1, 0x80000000];
        var palettes = new Dictionary<int, uint[]>();
        if (tableId == Effect) foreach (var index in new[] { 2, 5, 7 }) palettes[index] = index == 2 ? charges : Enumerable.Range(0, physical).Select(i => (uint)((fits ? 1 : index == 5 ? 65536 : 1048576) + i)).ToArray();
        if (defect == "kind") palettes.Remove(2);
        var paletteBytes = palettes.Values.Sum(p => p.Length * 4); var descriptor = 204 + sections * 40; var storage = descriptor + count * 4;
        var aux = storage + count * 24; var start = aux + paletteBytes + (nativePartial ? 8 : 0);
        var clearEnd = start + physical * recordSize + physical * 4 + 8;
        var body = new byte[clearEnd + (nativePartial ? recordSize + 4 + 8 : 0)]; "WDC5"u8.CopyTo(body); Put(body, 4, 5); "authored-effect-policy"u8.CopyTo(body.AsSpan(8));
        Put(body, 136, (uint)(physical + (nativePartial ? 1 : 0))); Put(body, 140, (uint)count); Put(body, 144, (uint)recordSize);
        Put(body, 152, defect == "table" ? 0x11223344 : uint.Parse(tableId, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Put(body, 156, defect == "layout" ? 0xdeadbeef : uint.Parse(layout, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Half(body, 172, 4); Put(body, 176, (uint)count); Put(body, 188, (uint)count * 24); Put(body, 196, (uint)paletteBytes); Put(body, 200, (uint)sections);
        Put(body, 212, (uint)start); Put(body, 216, physical); Put(body, 228, physical * 4); Put(body, 240, 1);
        if (nativePartial)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(244), 1); Put(body, 252, (uint)clearEnd); Put(body, 256, 1); Put(body, 268, 4); Put(body, 280, 1);
            Put(body, start - 8, 1); Put(body, start - 4, 20);
            Put(body, clearEnd + recordSize, 20); Put(body, clearEnd + recordSize + 4, 100); Put(body, clearEnd + recordSize + 8, 20);
        }
        var paletteAt = aux;
        for (var index = 0; index < count; index++)
        {
            Half(body, descriptor + index * 4 + 2, index * 4);
            var at = storage + index * 24; Half(body, at, index * 32); Half(body, at + 2, palettes.ContainsKey(index) ? 3 : 32);
            if (palettes.TryGetValue(index, out var values))
            {
                Put(body, at + 4, (uint)values.Length * 4); Put(body, at + 8, 3); Put(body, at + 12, (uint)index * 32); Put(body, at + 16, 3);
                foreach (var value in values) { Put(body, paletteAt, value); paletteAt += 4; }
            }
            for (var row = 0; row < physical; row++)
            {
                var value = palettes.ContainsKey(index) ? (uint)row : tableId == Effect && index == 6 ? 7u : 0u;
                if (tableId == Effect && index == 2 && defect == "kind") value = 65536;
                if (defect == "palette" && index == 2) value = 7;
                Put(body, start + row * recordSize + index * 4, value);
            }
        }
        for (var row = 0; row < physical; row++) Put(body, start + physical * recordSize + row * 4, (uint)(10 + row));
        Put(body, clearEnd - 8, 99); Put(body, clearEnd - 4, 10);
        if (!projected) return Raw(body);
        byte[] clear = [(byte)'N', .. body[..clearEnd]];
        byte[] encrypted = [(byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S', 0x99];
        var header = new byte[60]; "BLTE"u8.CopyTo(header); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 60); header[8] = 15; header[11] = 2;
        foreach (var (chunk, length, at) in new[] { (clear, clearEnd, 12), (encrypted, body.Length - clearEnd, 36) })
        { BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at), (uint)chunk.Length); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(at + 4), (uint)length); MD5.HashData(chunk).CopyTo(header, at + 8); }
        return new(Md5(body), Md5(header), body, [.. header, .. clear, .. encrypted]);
    }
    private static SourceObject Root(string key, uint fileId)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write("TSFM"u8); writer.Write(24u); writer.Write(2u); writer.Write(1u); writer.Write(0u); writer.Write(0u);
        writer.Write(1u); writer.Write(2u); writer.Write(0x10000000u); writer.Write(0u); writer.Write((byte)0); writer.Write(fileId); writer.Write(Convert.FromHexString(key));
        return Raw(stream.ToArray());
    }
    private static SourceObject EncodingObject(SourceObject[] entries)
    {
        var contents = entries.OrderBy(e => e.ContentKey, StringComparer.Ordinal).ToArray(); var encodings = entries.OrderBy(e => e.EncodingKey, StringComparer.Ordinal).ToArray();
        var cp = new byte[1024]; var ep = new byte[1024];
        for (var index = 0; index < contents.Length; index++) { var at = index * 38; cp[at] = 1; Put40(cp.AsSpan(at + 1), contents[index].Decoded.Length); Convert.FromHexString(contents[index].ContentKey).CopyTo(cp, at + 6); Convert.FromHexString(contents[index].EncodingKey).CopyTo(cp, at + 22); }
        for (var index = 0; index < encodings.Length; index++) { var at = index * 25; Convert.FromHexString(encodings[index].EncodingKey).CopyTo(ep, at); Put40(ep.AsSpan(at + 20), encodings[index].Encoded.Length); }
        var header = new byte[23]; "EN"u8.CopyTo(header); header[2] = 1; header[3] = header[4] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), 1); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(13), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), 1);
        return Raw([.. header, .. Convert.FromHexString(contents[0].ContentKey), .. MD5.HashData(cp), .. cp, .. Convert.FromHexString(encodings[0].EncodingKey), .. MD5.HashData(ep), .. ep]);
    }
    private static void Put40(Span<byte> target, int value) { target[0] = 0; BinaryPrimitives.WriteUInt32BigEndian(target[1..], (uint)value); }
    private sealed class Handler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        internal bool Offline; internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); if (Offline) throw new InvalidOperationException("Offline regression forbids HTTP"); Requests++; if (!responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new InvalidOperationException("Unexpected authored URL"); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) }); }
    }
    private sealed class Sink : IWdc5Consumer
    {
        internal Wdc5TableHeader? Header; internal List<Db2Row> Rows = [];
        public ValueTask HeaderAsync(Wdc5TableHeader header, CancellationToken token) { Header = header; return ValueTask.CompletedTask; }
        public ValueTask RowAsync(Db2Row row, CancellationToken token) { Rows.Add(row); return ValueTask.CompletedTask; }
    }
}
