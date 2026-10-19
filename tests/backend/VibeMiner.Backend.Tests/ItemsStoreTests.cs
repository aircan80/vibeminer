using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemsStoreTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedFeatureReopensOfflineAndCachedQueriesRefuseReplacedSourceAdmission(bool readFirst)
    {
        await using var h = await Harness.Create();
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal("Original", Assert.Single((await h.Store.ReadPageAsync(new(h.Request, snapshot.ArtifactId), default)).Entries).Name.Value);
        await h.Store.DisposeAsync(); h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources);
        Assert.Equal(snapshot.ArtifactId, (await h.Store.ReadAsync(h.Request, default)).Snapshot!.ArtifactId);
        Assert.True(Assert.Single((await h.Store.AuthorizeIconsAsync(h.Request, snapshot.ArtifactId, ["1"], default)).Items).Admitted);
        var authorized = await h.Store.AuthorizeSummariesAsync(h.Request, snapshot.ArtifactId, ["1", "2", "1"], default);
        Assert.Equal([true, false, true], authorized.Items.Select(i => i.Admitted));
        Assert.Equal(snapshot.ArtifactId, authorized.Snapshot.ArtifactId);
        await h.ReplaceItem();
        if (readFirst) Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadPageAsync(new(h.Request, snapshot.ArtifactId), default))).Code);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.AuthorizeIconsAsync(h.Request, snapshot.ArtifactId, ["1"], default))).Code);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.AuthorizeSummariesAsync(
            h.Request, snapshot.ArtifactId, ["1"], default))).Code);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
    }

    [Fact]
    public async Task RetainedFeatureWithMatchingOldSqlKeysCannotBypassCurrentDecoderEligibility()
    {
        await using var h = await Harness.Create();
        var stale = await h.Source(ItemsProjection.ItemTable, h.Fixture.PayloadB, "historical-decoder-v1");
        h.Item = stale;
        await h.Catalog.RecordTableArtifactAsync(stale.Rows, default);
        await h.Catalog.CommitTableAdmissionAsync(h.Opening, ItemsProjection.ItemTable, "enUS", stale.Source, stale.Association, stale.Rows, Harness.Time, default);
        var binding = new ItemsProjectionBinding(h.Request, stale.Snapshot, h.Sparse.Snapshot, stale.Rows, h.Sparse.Rows);
        var artifact = await new ItemsProjection(h.Objects, h.Resources).BuildAsync(binding, _ => Task.CompletedTask, default);
        await h.Catalog.RecordItemsArtifactAsync(artifact, default);
        await h.Catalog.CommitItemsAdmissionAsync(h.Opening, "enUS", artifact, Harness.Time, default);
        var readiness = await h.Store.ReadAsync(h.Request, default);
        Assert.Null(readiness.Snapshot); Assert.Equal("notPrepared", readiness.Dependencies.Single(d => d.TableId == ItemsProjection.ItemTable).State);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadPageAsync(new(h.Request, artifact.ContentKey), default))).Code);
    }

    [Fact]
    public async Task SourceAndFeatureCatalogAssociationsStayExactAndCancellationPreservesReadyAdmission()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var saved = (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.CommitItemsAdmissionAsync(h.Opening, "enUS", saved.Artifact,
            "2026-10-20T00:00:00Z", canceled.Token));
        Assert.Equal(saved.PreparedAt, (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!.PreparedAt);
        Assert.Null(await h.Catalog.ReadItemsAdmissionAsync(h.Opening with { Reference = h.Opening.Reference with { Generation = "999" } }, "enUS", default));
        Assert.Equal("TABLE_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemsAdmissionAsync(h.Opening,
            "deDE", saved.Artifact, Harness.Time, default))).Code);
        Assert.Equal(snapshot.ArtifactId, saved.Artifact.ContentKey);
    }

    [Fact]
    public async Task LocalReadinessRejectsCorruptOriginalSourceEvenWhenJoinedProjectionIsValid()
    {
        await using var h = await Harness.Create(); var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await h.Store.DisposeAsync();
        string path;
        await using (var input = await h.Objects.OpenVerifiedAsync(h.Sparse.Rows.Sha256, h.Sparse.Rows.ByteLength, default)) path = input.Name;
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Request, default))).Code);
        Assert.Equal(snapshot.ArtifactId, (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!.Artifact.ContentKey);
    }

    [Fact]
    public async Task SourceReplacementBetweenCasPublicationAndCommitCannotAdmitAStaleFeature()
    {
        await using var h = await Harness.Create();
        var binding = new ItemsProjectionBinding(h.Request, h.Item.Snapshot, h.Sparse.Snapshot, h.Item.Rows, h.Sparse.Rows);
        var artifact = await new ItemsProjection(h.Objects, h.Resources).BuildAsync(binding, _ => Task.CompletedTask, default);
        await h.Catalog.RecordItemsArtifactAsync(artifact, default); await h.ReplaceItem();
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitItemsAdmissionAsync(h.Opening,
            "enUS", artifact, Harness.Time, default))).Code);
        Assert.Null(await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default));
        await using var orphan = await h.Objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, default);
        Assert.Equal(artifact.ByteLength, orphan.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalV1RemainsValidButRefreshesOfflineToV2WithoutReplacingOriginalBytes(bool cancelBeforeAdmission)
    {
        await using var h = await Harness.Create();
        var binding = new ItemsProjectionBinding(h.Request, h.Item.Snapshot, h.Sparse.Snapshot, h.Item.Rows, h.Sparse.Rows);
        var current = await new ItemsProjection(h.Objects, h.Resources).BuildAsync(binding, _ => Task.CompletedTask, default);
        var legacy = await ItemsProjectionTests.HistoricalV1(h.Objects, current);
        await h.Catalog.RecordItemsArtifactAsync(legacy, default);
        await h.Catalog.CommitItemsAdmissionAsync(h.Opening, "enUS", legacy, Harness.Time, default);
        Assert.Null((await h.Store.ReadAsync(h.Request, default)).Snapshot);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.ReadPageAsync(new(h.Request, legacy.ContentKey), default))).Code);
        if (cancelBeforeAdmission)
        {
            using var stop = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.PrepareAsync(h.Request, p =>
            { if (p.Stage == "Saving exact Items admission") stop.Cancel(); return Task.CompletedTask; }, stop.Token));
            Assert.Equal(legacy.ContentKey, (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!.Artifact.ContentKey);
        }
        var refreshed = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal(ItemsProjection.Revision, refreshed.FeatureRevision); Assert.NotEqual(legacy.ContentKey, refreshed.ArtifactId);
        Assert.Empty(h.PreparedTables); // Both exact source prerequisites were already local.
        Assert.Equal("Original", Assert.Single((await h.Store.ReadPageAsync(new(h.Request, refreshed.ArtifactId), default)).Entries).Name.Value);
        var historical = (await h.Catalog.FindItemsArtifactAsync(legacy.ContentKey, default))!;
        Assert.Equal(legacy.ContentKey, historical.ContentKey); Assert.Equal(legacy.Sha256, historical.Sha256);
        Assert.Equal(legacy.ByteLength, historical.ByteLength); Assert.Equal(ItemsProjection.LegacyRevision, historical.FeatureRevision);
        await using var retained = await new ItemsProjection(h.Objects, h.Resources).OpenAsync(historical, Harness.Time, default);
        Assert.Equal(ItemsProjection.LegacyRevision, retained.Snapshot.FeatureRevision);
        Assert.Equal("Original", Assert.Single((await retained.ReadPageAsync(new(h.Request, legacy.ContentKey), default)).Entries).Name.Value);
    }

    [Fact]
    public async Task MissingPrerequisitesPrepareSequentiallyThroughTheSharedTableService()
    {
        await using var h = await Harness.Create(); h.PretendMissing = true;
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal([ItemsProjection.ItemTable, ItemsProjection.SparseTable], h.PreparedTables);
        Assert.Equal("1", snapshot.Counts.MatchedRows);
    }

    [Fact]
    public async Task WarmQueriesMeasureFreshAdmissionAndSchemaChecksWithoutReopeningSourceArtifacts()
    {
        await using var h = await Harness.Create();
        var snapshot = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var allocated = GC.GetTotalAllocatedBytes(true); var clock = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
            Assert.Single((await h.Store.ReadPageAsync(new(h.Request, snapshot.ArtifactId, "original"), default)).Entries);
        clock.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        output.WriteLine($"Twenty warm end-to-end ItemsStore queries with fresh SQL admission/provider/definition/schema/source binding checks: {clock.Elapsed.TotalMilliseconds:F3}ms; cumulative allocated bytes={allocated}. Authored one-row sources; no real-data timing claim.");
    }

    [Fact]
    public async Task BusyPrerequisiteWaitObservesActualRetirementAndDoesNotCancelIndependentConsumer()
    {
        await using var h = await Harness.Create(); h.PretendMissing = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken independentToken = default;
        h.BeforePrepare = async (request, token) =>
        { if (request.TableId == ItemsProjection.SparseTable) { independentToken = token; entered.TrySetResult(); await release.Task.WaitAsync(token); } };
        var independent = h.Tables.PrepareAsync(new(h.Request.ProductId, h.Request.BuildIdentity, "us", ItemsProjection.SparseTable, "enUS"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var preparation = h.Store.PrepareAsync(h.Request, stage =>
        { if (stage.Stage == "Waiting for shared table preparation") waiting.TrySetResult(); return Task.CompletedTask; }, stop.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(preparation.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.False(independent.IsCompleted); Assert.False(independentToken.IsCancellationRequested);
        release.TrySetResult(); await independent;
        await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.Equal([ItemsProjection.SparseTable, ItemsProjection.ItemTable], h.PreparedTables);
    }

    // Authored row/admission metadata tests association/lifecycle, not actual game parser qualification.
    internal sealed record SourceFixture(TableRowsArtifact Rows, ContentReceipt Source, SelectedFileAssociation Association, TableDataSnapshot Snapshot);
    internal sealed class Harness : IAsyncDisposable
    {
        internal const string Time = "2026-10-19T00:00:00Z";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-items-store-" + Guid.NewGuid().ToString("N"));
        internal string DirectoryPath => directory;
        internal readonly InventoryFixture Fixture = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal CapturedOpeningContext Opening = null!;
        internal ItemsContext Request = null!;
        internal SourceFixture Item = null!, Sparse = null!;
        internal readonly AcquisitionResources Resources = new();
        internal TableDataService Tables = null!;
        internal ItemsStore Store = null!;
        internal bool PretendMissing;
        internal readonly List<string> PreparedTables = [];
        internal Func<TableDataRequest, CancellationToken, Task>? BeforePrepare;
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.Objects = new(h.directory);
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
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, InventoryFixture.Url("config", key), Time), default);
                configurations.Add(new(role, key, sha, bytes.Length, InventoryFixture.Url("config", key), Time, Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configurations, Time, default);
            foreach (var role in new[] { "encoding", "root" }) await h.Catalog.RecordMetadataAsync(h.Metadata(role), default);
            h.Item = await h.Source(ItemsProjection.ItemTable, h.Fixture.PayloadA);
            h.Sparse = await h.Source(ItemsProjection.SparseTable, h.Fixture.PayloadB);
            await h.Publish(h.Item); await h.Publish(h.Sparse);
            h.Tables = new(new ReadyTables(h)); h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources); return h;
        }
        private MetadataReceipt Metadata(string role)
        {
            var source = Fixture.Metadata[role]; return new(role, source.ContentKey, source.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(source.Encoded)), N(source.Encoded.Length), N(source.Decoded.Length),
                InventoryFixture.Url("data", source.EncodingKey), Time, Time, MetadataAcquirer.SourceRevision);
        }
        internal async Task<SourceFixture> Source(string table, InventoryFixture.FixtureObject bytes, string? decoder = null)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
            var source = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)),
                N(bytes.Encoded.Length), N(bytes.Decoded.Length), InventoryFixture.Url("data", bytes.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
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
            var header = new Wdc5TableHeader(1, table.ToUpperInvariant(), sparse ? "1C17D17F" : "996192AA", 1, 0, 4, columns);
            await writer.HeaderAsync(header, default); await writer.RowAsync(new(1, false, null, sparse ? ["1", "Original", "4"] : ["1", "2", "132759"]), default);
            var completion = new Wdc5Completion(1, 1, 0, 0, 0, 0, "complete", bytes.Decoded.Length);
            var artifact = await writer.CompleteAsync(completion, default);
            var snapshot = new TableDataSnapshot(Request.ProductId, Request.BuildIdentity, Request.SourceRegion, table, "enUS", artifact.ContentKey,
                Time, header.LayoutHash, definition.Sha256, binding.ProviderRevision, binding.DecoderRevision, source, association,
                columns.Select(c => new TableDataColumn(c.Name, c.Kind, c.IsId, null)).ToArray(),
                new("complete", "1", "1", "0", "0", "0", "0", "0", "0"), "1");
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
                return Task.FromResult(new TableDataReadiness(snapshot.DecoderRevision != TableDataStore.DecoderRevision || h.PretendMissing && !h.PreparedTables.Contains(request.TableId) ? null : snapshot));
            }
            public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
            {
                if (!h.PretendMissing) throw new InvalidOperationException("Already prepared fixture prerequisites must be reused.");
                if (h.BeforePrepare is { } before) await before(request, token);
                h.PreparedTables.Add(request.TableId); return Snapshot(request.TableId);
            }
            public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("Items must use held immutable row readers, never per-page table catalog reads.");
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync()
        { if (Tables is not null) await Tables.DisposeAsync(); if (Store is not null) await Store.DisposeAsync(); if (Objects is not null) await Objects.DisposeAsync(); if (Catalog is not null) await Catalog.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
