using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemIconsStoreTests
{
    [Fact]
    public async Task Exact_visible_batch_deduplicates_FDID_and_CKey_and_keeps_optional_outcomes_independent()
    {
        await using var h = await Harness.Create();
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var result = await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["3", "2", "1", "4", "5", "6", "7", "8", "99", "1"]), _ => Task.CompletedTask, default);
        Assert.Equal(new[] { "3", "2", "1", "4", "5", "6", "7", "8", "99", "1" }, result.Outcomes.Select(value => value.ItemId));
        Assert.All(result.Outcomes.Take(3), value => Assert.Equal(ItemIconStatus.Ready, value.Status));
        Assert.Equal(result.Outcomes[0].Image, result.Outcomes[1].Image); Assert.Equal(result.Outcomes[0].Image, result.Outcomes[2].Image);
        Assert.Equal(ItemIconStatus.Unsupported, result.Outcomes[3].Status);
        Assert.Equal(ItemIconStatus.MissingMembership, result.Outcomes[4].Status);
        Assert.Equal(ItemIconStatus.AmbiguousVariants, result.Outcomes[5].Status);
        Assert.Equal(ItemIconStatus.EncodingUnresolved, result.Outcomes[6].Status);
        Assert.Equal(ItemIconStatus.NoIcon, result.Outcomes[7].Status); Assert.Equal(ItemIconStatus.MissingItem, result.Outcomes[8].Status);
        Assert.Equal(1, h.HttpRequests.Count(url => url.EndsWith(h.Fixture.PayloadA.EncodingKey, StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(h.directory, "staging-v1")));
        var cached = await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default); Assert.NotNull(cached);
        Assert.Equal(2, cached.Admission.Association.Memberships.Count); Assert.Null(cached.Admission.Source.Projection);
    }
    // D096 (#73): only the decoded level is held to its length and bounds. A 2x1 paletted icon whose undecoded 1x1 tail
    // claims 9 bytes past the end of the file is Ready at level 0; the same fault on level 0 is still corrupt.
    [Theory]
    [InlineData(1, ItemIconStatus.Ready)][InlineData(0, ItemIconStatus.Corrupt)]
    public async Task An_icon_whose_only_fault_is_an_undecoded_tail_descriptor_is_ready(int broken, ItemIconStatus expected)
    {
        var icon = new byte[1172 + 3]; "BLP2"u8.CopyTo(icon); Put(4, 1); icon[8] = 1; icon[11] = 1; Put(12, 2); Put(16, 1);
        Put(20, 1172); Put(84, 2); Put(24, 1174); Put(88, 1); icon[148] = 20; icon[149] = 40; icon[150] = 200;
        Put(84 + broken * 4, 9);
        await using var h = await Harness.Create(icon: icon); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var outcome = Assert.Single((await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1"]), _ => Task.CompletedTask, default)).Outcomes);
        Assert.Equal(expected, outcome.Status);
        if (expected == ItemIconStatus.Ready) Assert.Equal((2, 1), (outcome.Image!.Width, outcome.Image.Height));
        else Assert.Equal("BLP_INVALID", outcome.Problem!.Code);
        void Put(int at, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(icon.AsSpan(at), value);
    }

    [Fact]
    public async Task Ready_icons_reopen_offline_without_source_session_and_read_authority_is_exact_and_bounded()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var prepared = await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1", "2"]), _ => Task.CompletedTask, default);
        var image = prepared.Outcomes[0].Image!; var request = new ItemIconReadRequest(h.Request, snapshot.ArtifactId, "1", image.ArtifactId);
        await h.Icons.DisposeAsync(); await h.Sessions.DisposeAsync();
        h.Sessions = new(h.Catalog, h.Objects, h.Client, h.Shared, h.Resources);
        h.Icons = new(h.Catalog, h.Objects, h.Store, h.Sessions, h.Shared, h.Resources, new(h.Objects));
        var before = h.HttpRequests.Count; h.Offline = true;
        Assert.Equal(ItemIconStatus.Ready, Assert.Single((await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1"]), _ => Task.CompletedTask, default)).Outcomes).Status);
        var reads = new List<IItemIconReadLease>();
        try
        {
            for (var i = 0; i < 4; i++) reads.Add(await h.Icons.OpenAsync(request, default));
            Assert.Equal("ITEM_ICONS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.OpenAsync(request, default))).Code);
            Assert.Equal("89504E470D0A1A0A", Convert.ToHexString(await Read(reads[0].Content, 8)));
            await reads[0].DisposeAsync(); reads.RemoveAt(0); reads.Add(await h.Icons.OpenAsync(request, default));
        }
        finally { foreach (var read in reads) await read.DisposeAsync(); }
        Assert.Equal(before, h.HttpRequests.Count);
        Assert.Equal("ITEM_ICON_UNKNOWN", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.OpenAsync(request with { ItemId = "8" }, default))).Code);
        Assert.Equal("ITEM_ICON_UNKNOWN", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.OpenAsync(request with { ImageArtifactId = new('f', 64) }, default))).Code);
        await h.ReplaceItem();
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.OpenAsync(request, default))).Code);
    }
    [Fact]
    public async Task Saved_configuration_poison_is_rejected_and_missing_PNG_regenerates_from_retained_association()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var request = new ItemIconBatchRequest(h.Request, snapshot.ArtifactId, ["1"]);
        var prepared = await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        var saved = (await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default))!;
        var pngPath = System.IO.Path.Combine(h.directory, "objects-v1", saved.Artifact.Sha256[..2], saved.Artifact.Sha256[2..]);
        File.Delete(pngPath); h.Offline = true; var before = h.HttpRequests.Count;
        var restored = await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(prepared.Outcomes[0].Image, restored.Outcomes[0].Image); Assert.Equal(before, h.HttpRequests.Count);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(h.directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await db.OpenAsync(); await using var command = db.CreateCommand();
        command.CommandText = "UPDATE item_icon_admissions SET descriptor=json_set(descriptor,'$.association.configurations[0].sha256',$sha)";
        command.Parameters.AddWithValue("$sha", new string('f', 64)); Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var failed = await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(ItemIconStatus.Corrupt, failed.Outcomes[0].Status); Assert.Null(failed.Outcomes[0].Image);
        Assert.Equal("ITEM_ICON_ASSOCIATION_INVALID", failed.Outcomes[0].Problem!.Code);
    }
    [Theory]
    [InlineData("feature")][InlineData("locale")][InlineData("opening")][InlineData("fdid")][InlineData("ckey")]
    [InlineData("ekey")][InlineData("projection")][InlineData("membership-id")][InlineData("membership-key")]
    [InlineData("variant-policy")][InlineData("encoding-policy")][InlineData("root-revision")][InlineData("root-source")]
    [InlineData("config-sha")][InlineData("config-length")][InlineData("config-url")][InlineData("receipt-url")][InlineData("png-source")]
    public async Task Durable_admission_rejects_source_feature_membership_and_PNG_substitution(string kind)
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1"]), _ => Task.CompletedTask, default);
        var saved = (await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default))!;
        var a = saved.Admission; var association = a.Association;
        var members = association.Memberships.ToArray(); var configs = association.Configurations.ToArray();
        switch (kind)
        {
            case "feature": a = a with { FeatureArtifact = new('f', 64) }; break;
            case "locale": a = a with { Context = a.Context with { ResearchLocale = "deDE" } }; break;
            case "opening": a = a with { Association = association with { SourceReference = association.SourceReference with { Generation = "999" } } }; break;
            case "fdid": a = a with { FileDataId = 1002 }; break;
            case "ckey": a = a with { Source = a.Source with { ContentKey = new('f', 32) } }; break;
            case "ekey": a = a with { Source = a.Source with { EncodingKey = new('f', 32) } }; break;
            case "projection": a = a with { Source = a.Source with { Projection = new("bad", new('f', 64), [], []) } }; break;
            case "membership-id": members[0] = members[0] with { Entry = members[0].Entry with { FileDataId = 1002 } }; a = a with { Association = association with { Memberships = members } }; break;
            case "membership-key": members[0] = members[0] with { Entry = members[0].Entry with { ContentKey = new('f', 32) } }; a = a with { Association = association with { Memberships = members } }; break;
            case "variant-policy": a = a with { Association = association with { VariantPolicy = "first-variant" } }; break;
            case "encoding-policy": a = a with { Association = association with { EncodingSelectionPolicy = "first-encoding" } }; break;
            case "root-revision": a = a with { Association = association with { RootLookupRevision = "old" } }; break;
            case "root-source": a = a with { Association = association with { RootSource = association.RootSource with { Sha256 = new('f', 64) } } }; break;
            case "config-sha": configs[0] = configs[0] with { Sha256 = new('f', 64) }; a = a with { Association = association with { Configurations = configs } }; break;
            case "config-length": configs[0] = configs[0] with { ByteLength = configs[0].ByteLength + 1 }; a = a with { Association = association with { Configurations = configs } }; break;
            case "config-url": configs[0] = configs[0] with { SourceUrl = "https://us.cdn.blizzard.com/changed" }; a = a with { Association = association with { Configurations = configs } }; break;
            case "receipt-url": a = a with { Source = a.Source with { SourceUrl = "https://us.cdn.blizzard.com/changed" } }; break;
            case "png-source": saved = saved with { Artifact = saved.Artifact with { Proof = saved.Artifact.Proof with { Source = saved.Artifact.Proof.Source with { ContentKey = new('f', 32) } } } }; break;
        }
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemIconAsync(h.Opening, snapshot, saved with { Admission = a }, default));
        Assert.Equal("ITEM_ICON_ASSOCIATION_INVALID", error.Code);
        Assert.NotNull(await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default));
    }
    [Fact]
    public async Task Cancellation_before_PNG_publication_cleans_staging_preserves_Items_and_retry_reuses_sources()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        using var cancellation = new CancellationTokenSource();
        var request = new ItemIconBatchRequest(h.Request, snapshot.ArtifactId, ["1"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Icons.PrepareBatchAsync(request, value =>
        { if (value.Stage == "Encoding verified icon PNG") cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Null(await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default));
        Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(h.directory, "staging-v1")));
        h.Offline = true; var before = h.HttpRequests.Count;
        Assert.Equal(ItemIconStatus.Ready, Assert.Single((await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default)).Outcomes).Status);
        Assert.Equal(before, h.HttpRequests.Count);
    }
    [Fact]
    public async Task Cross_feature_authority_fails_before_source_work_and_store_shutdown_closes_held_reads()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.PrepareBatchAsync(
            new(h.Request, new('f', 64), ["1"]), _ => Task.CompletedTask, default))).Code);
        Assert.Empty(h.HttpRequests);
        var prepared = await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1"]), _ => Task.CompletedTask, default);
        var request = new ItemIconReadRequest(h.Request, snapshot.ArtifactId, "1", prepared.Outcomes[0].Image!.ArtifactId);
        await using var read = await h.Icons.OpenAsync(request, default); Assert.True(read.Content.CanRead);
        await h.Icons.DisposeAsync(); Assert.False(read.Content.CanRead);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.OpenAsync(request, default))).Code);
    }
    [Theory]
    [InlineData("{")][InlineData("null")][InlineData("{\"pngRequestKey\":null}")]
    public async Task Malformed_durable_descriptor_has_typed_optional_corrupt_outcome(string descriptor)
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var request = new ItemIconBatchRequest(h.Request, snapshot.ArtifactId, ["1"]);
        await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(h.directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE item_icon_admissions SET descriptor=$d";
        command.Parameters.AddWithValue("$d", descriptor); Assert.Equal(1, await command.ExecuteNonQueryAsync());
        var result = await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(ItemIconStatus.Corrupt, Assert.Single(result.Outcomes).Status); Assert.Null(result.Outcomes[0].Image);
    }
    [Fact]
    public async Task JSON_null_original_content_receipt_refuses_icon_and_table_authority()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var request = new ItemIconBatchRequest(h.Request, snapshot.ArtifactId, ["1"]);
        await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(h.directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = "UPDATE content_objects SET receipt='null' WHERE ekey=$k";
        command.Parameters.AddWithValue("$k", h.Fixture.PayloadA.EncodingKey); Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal("ITEM_ICON_ASSOCIATION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default))).Code);
        // This fixture also uses that source for an authored table admission. Its corruption must
        // fail prerequisite authorization before any optional icon source work can proceed.
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default));
    }
    [Fact]
    public async Task Newly_authorized_FDID_reuses_verified_shared_PNG_offline_even_when_original_raw_bytes_are_absent()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var first = await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["1"]), _ => Task.CompletedTask, default);
        var saved = (await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default))!;
        Assert.Null(await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1003, default));
        File.Delete(System.IO.Path.Combine(h.directory, "objects-v1", saved.Admission.Source.Sha256[..2], saved.Admission.Source.Sha256[2..]));
        h.Offline = true; var before = h.HttpRequests.Count;
        var next = await h.Icons.PrepareBatchAsync(new(h.Request, snapshot.ArtifactId, ["2"]), _ => Task.CompletedTask, default);
        Assert.Equal(ItemIconStatus.Ready, next.Outcomes[0].Status); Assert.Equal("1003", next.Outcomes[0].FileDataId);
        Assert.Equal(first.Outcomes[0].Image, next.Outcomes[0].Image); Assert.Equal(before, h.HttpRequests.Count);
        Assert.NotNull(await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1003, default));
    }
    [Fact]
    public async Task Historical_tables_with_explicit_loose_metadata_match_canonical_legacy_null_without_rewriting_receipts()
    {
        await using var h = await Harness.Create(legacyMetadata: true);
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.NotNull(snapshot.ItemSource.Association.EncodingSource.Location); Assert.NotNull(snapshot.ItemSource.Association.RootSource.Location);
        var request = new ItemIconBatchRequest(h.Request, snapshot.ArtifactId, ["1", "1"]);
        var result = await h.Icons.PrepareBatchAsync(request, _ => Task.CompletedTask, default);
        Assert.All(result.Outcomes, value => Assert.Equal(ItemIconStatus.Ready, value.Status));
        var saved = (await h.Catalog.FindItemIconAsync(h.Opening, snapshot, 1001, default))!;
        Assert.Null(saved.Admission.Association.EncodingSource.Location); Assert.Null(saved.Admission.Association.RootSource.Location);
        Assert.NotNull((await h.Store.ReadAsync(h.Request, default)).Snapshot!.ItemSource.Association.EncodingSource.Location);
    }
    private static async Task<byte[]> Read(Stream stream, int count) { var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes); return bytes; }
    // Authored row/admission metadata tests association/lifecycle, not actual game parser qualification.
    private sealed record SourceFixture(TableRowsArtifact Rows, ContentReceipt Source, SelectedFileAssociation Association, TableDataSnapshot Snapshot);
    private sealed class Harness : IAsyncDisposable
    {
        internal const string Time = "2026-10-19T00:00:00Z";
        internal readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-item-icons-" + Guid.NewGuid().ToString("N"));
        internal readonly ItemIconData Fixture;
        private Harness(ItemIconData fixture) => Fixture = fixture;
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal CapturedOpeningContext Opening = null!;
        internal ItemsContext Request = null!;
        internal SourceFixture Item = null!, Sparse = null!;
        internal readonly AcquisitionResources Resources = new();
        internal TableDataService Tables = null!;
        internal ItemsStore Store = null!;
        internal ItemIconsStore Icons = null!;
        internal AssetResolutionSessions Sessions = null!;
        internal EncodedSourcePlans Plans = null!;
        internal AcquisitionCoordinator Shared = null!;
        internal HttpClient Client = null!;
        internal bool Offline;
        internal readonly List<string> HttpRequests = [];
        internal static async Task<Harness> Create(bool legacyMetadata = false, byte[]? icon = null)
        {
            var h = new Harness(ItemIconData.Create(icon)); h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.Objects = new(h.directory);
            var versions = Fixtures.Versions($"us|{h.Fixture.BuildKey}|{h.Fixture.CdnKey}||70448|12.1.5.70448||items");
            var cdns = Encoding.UTF8.GetString(h.Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            var page = await h.Catalog.CommitAsync("wow", generation, discovery, Time, default);
            h.Request = new("wow", page.Builds.Single().Id, "us", "enUS");
            h.Opening = await h.Catalog.ResolveOpeningAsync(new("wow", h.Request.BuildIdentity, "us"), default);
            var configurations = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); await h.Objects.PublishAsync(bytes, sha, default);
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, ItemIconData.Url("config", key), Time), default);
                configurations.Add(new(role, key, sha, bytes.Length, ItemIconData.Url("config", key), Time, Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configurations, Time, default);
            foreach (var role in new[] { "encoding", "root" })
            {
                var metadata = h.Metadata(role);
                if (legacyMetadata) metadata = metadata with { Location = null };
                await h.Catalog.RecordMetadataAsync(metadata, default);
                await h.Objects.PublishAsync(h.Fixture.Metadata[role].Encoded, metadata.Sha256, default);
                await h.Catalog.RecordEncodedReceiptAsync(new(metadata.EncodingKey, metadata.Sha256, h.Fixture.Metadata[role].Encoded.Length,
                    metadata.SourceUrl, Time, Time, "blte-encoded-v1", new("loose", "0", metadata.EncodedBytes, null)), default);
            }
            h.Item = await h.Source(ItemsProjection.ItemTable, h.Fixture.PayloadA);
            h.Sparse = await h.Source(ItemsProjection.SparseTable, h.Fixture.PayloadB);
            await h.Publish(h.Item); await h.Publish(h.Sparse);
            h.Tables = new(new ReadyTables(h)); h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources);
            h.Client = new(new Handler((request, token) =>
            {
                token.ThrowIfCancellationRequested(); if (h.Offline) throw new InvalidOperationException("Authored offline fixture forbids HTTP.");
                var url = request.RequestUri!.AbsoluteUri; h.HttpRequests.Add(url);
                if (!h.Fixture.Responses.TryGetValue(url, out var body) && !h.Fixture.Payloads.TryGetValue(request.RequestUri.Segments[^1], out body))
                    throw new InvalidOperationException("Unexpected authored icon request.");
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
            }));
            h.Plans = new(h.Catalog, h.Objects, h.Resources);
            h.Shared = new(new OfficialEncodedObjectProducer(h.Catalog, h.Objects, h.Client, h.Resources, h.Plans));
            h.Sessions = new(h.Catalog, h.Objects, h.Client, h.Shared, h.Resources);
            h.Icons = new(h.Catalog, h.Objects, h.Store, h.Sessions, h.Shared, h.Resources, new(h.Objects)); return h;
        }
        private MetadataReceipt Metadata(string role)
        {
            var source = Fixture.Metadata[role]; return new(role, source.ContentKey, source.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(source.Encoded)), N(source.Encoded.Length), N(source.Decoded.Length),
                ItemIconData.Url("data", source.EncodingKey), Time, Time, role == "root" ? "root-v1" : MetadataAcquirer.SourceRevision, new("loose", "0", N(source.Encoded.Length), null));
        }
        internal async Task<SourceFixture> Source(string table, ItemIconData.FixtureObject bytes, string? decoder = null)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
            var source = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)),
                N(bytes.Encoded.Length), N(bytes.Decoded.Length), ItemIconData.Url("data", bytes.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
            await Catalog.RecordContentAsync(source, default);
            var entry = provider.Entries.Single(e => e.TableId == table);
            var association = new SelectedFileAssociation(entry.Db2FileDataId!, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), Metadata("root"),
                [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var binding = new TableArtifactBinding(source.Sha256, new('d', 64), definition.Sha256, BundledTableDefinitions.Revision, "enUS",
                decoder ?? TableDataStore.DecoderRevision, TableRowArtifacts.SchemaSelectionKey(definition.Document, Opening.Build.Version));
            var sparse = table == ItemsProjection.SparseTable;
            Db2Column[] columns = sparse ? [new("ID", "integer", true), new("Display_lang", "string", false), new("OverallQualityID", "integer", false)]
                : [new("ID", "integer", true), new("ClassID", "integer", false), new("IconFileDataID", "integer", false)];
            await using var rows = new TableRowArtifacts(Objects); await using var writer = await rows.CreateWriterAsync(binding, default);
            var header = new Wdc5TableHeader(8, table.ToUpperInvariant(), sparse ? "1C17D17F" : "996192AA", 1, 0, 4, columns);
            await writer.HeaderAsync(header, default);
            string[] icons = ["1001", "1003", "1001", "1002", "999", "1004", "1005", "0"];
            for (var i = 0; i < icons.Length; i++) await writer.RowAsync(new((uint)(i + 1), false, null,
                sparse ? [N(i + 1), "Original", "4"] : [N(i + 1), "2", icons[i]]), default);
            var completion = new Wdc5Completion(8, 8, 0, 0, 0, 0, "complete", bytes.Decoded.Length);
            var artifact = await writer.CompleteAsync(completion, default);
            var snapshot = new TableDataSnapshot(Request.ProductId, Request.BuildIdentity, Request.SourceRegion, table, "enUS", artifact.ContentKey,
                Time, header.LayoutHash, definition.Sha256, binding.ProviderRevision, binding.DecoderRevision, source, association,
                columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null)).ToArray(),
                new("complete", "8", "8", "0", "0", "0", "0", "0", "0"), "8");
            return new(artifact, source, association, snapshot);
        }
        private async Task Publish(SourceFixture source)
        {
            await Catalog.RecordTableArtifactAsync(source.Rows, default);
            await Catalog.CommitTableAdmissionAsync(Opening, source.Snapshot.TableId, "enUS", source.Source, source.Association, source.Rows, Time, default);
        }
        internal async Task ReplaceItem()
        { Item = await Source(ItemsProjection.ItemTable, Fixture.PayloadB); await Publish(Item); }
        private sealed class ReadyTables(Harness h) : ITableDataStore
        {
            private TableDataSnapshot Snapshot(string table) => table == ItemsProjection.ItemTable ? h.Item.Snapshot : h.Sparse.Snapshot;
            public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token)
            {
                var snapshot = Snapshot(request.TableId);
                return Task.FromResult(new TableDataReadiness(snapshot.DecoderRevision != TableDataStore.DecoderRevision ? null : snapshot));
            }
            public Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token) =>
                throw new InvalidOperationException("Already prepared authored table sources must be reused.");
            public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("Items must use held immutable row readers, never per-page table catalog reads.");
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync()
        { if (Icons is not null) await Icons.DisposeAsync(); if (Sessions is not null) await Sessions.DisposeAsync(); if (Shared is not null) await Shared.DisposeAsync(); if (Plans is not null) await Plans.DisposeAsync(); Client?.Dispose(); if (Tables is not null) await Tables.DisposeAsync(); if (Store is not null) await Store.DisposeAsync(); if (Objects is not null) await Objects.DisposeAsync(); if (Catalog is not null) await Catalog.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
