using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureDisplaySourcesTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Progress(CreaturesPreparationProgress _) => Task.CompletedTask;
    [Fact]
    public async Task RealCatalogCasProfilesAndExactCopyLookupResolveWithoutTablesOrNetwork()
    {
        await using var fixture = await Fixture.Create(); await using var source = fixture.Sources();
        await using var store = new CreatureDisplayMetadataStore(source);
        var result = await store.ReadDisplayAsync(fixture.Request, default);
        Assert.Equal(CreatureDisplayMetadataState.MetadataResolved, result.State); Assert.Equal("33", result.ModelId); Assert.Equal("777", result.ModelFileDataId);
        Assert.True(result.Sources[0].Row!.IsCopy); Assert.Equal("122", result.Sources[0].Row!.CopySourceId); Assert.Equal("123", result.Sources[0].Row!.Source.RowId);
        Assert.Equal("33", result.Sources[1].Row!.Source.RowId); Assert.False(result.Sources[1].Row!.IsCopy);
        Assert.Equal(2, source.Pressure.Readers); Assert.Equal(0, source.Pressure.Borrowed); Assert.Empty(fixture.Prepared);
        var original = JsonSerializer.Serialize(result, CreaturesPolicy.Json);
        Assert.Equal(original, JsonSerializer.Serialize(await store.ReadDisplayAsync(fixture.Request, default), CreaturesPolicy.Json));
        await store.DisposeAsync(); Assert.Equal(0, source.Pressure.Readers);
        await using var fresh = new CreatureDisplayMetadataStore(fixture.Sources());
        Assert.Equal(original, JsonSerializer.Serialize(await fresh.ReadDisplayAsync(fixture.Request, default), CreaturesPolicy.Json));
    }
    [Theory]
    [InlineData(CreatureDisplayTargetRole.Display)]
    [InlineData(CreatureDisplayTargetRole.Model)]
    public async Task HistoricalAdmissionIsUnpreparedAndAutomaticSharedPreparationRefreshesOffline(CreatureDisplayTargetRole role)
    {
        await using var fixture = await Fixture.Create(); await fixture.PublishTarget(role, historical: true);
        await using var store = new CreatureDisplayMetadataStore(fixture.Sources());
        var before = await store.ReadDisplayAsync(fixture.Request, default);
        Assert.Equal(CreatureDisplayMetadataState.NotPrepared, before.State); Assert.Equal(CreatureDisplayPolicy.Table(role), before.RequiredTableId);
        Assert.Empty(fixture.Prepared);
        var ready = await store.PrepareDisplayAsync(fixture.Request, Progress, default).WaitAsync(Deadline);
        Assert.Equal(CreatureDisplayMetadataState.MetadataResolved, ready.State); Assert.Equal([CreatureDisplayPolicy.Table(role)], fixture.Prepared);
        Assert.Equal("777", ready.ModelFileDataId); Assert.Equal(2, ready.Sources.Count);
    }
    [Fact]
    public async Task TwoOpeningPhysicalReadersAreChargedAndThirdContextCannotTransientlyOpen()
    {
        await using var fixture = await Fixture.Create(); var both = Signal(); var release = Signal(); var opens = 0;
        await using var source = fixture.Sources(async (hash, size, token) =>
        { if (Interlocked.Increment(ref opens) == 2) both.SetResult(); await release.Task; return await fixture.Catalog.Projection.Objects.OpenVerifiedAsync(hash, size, token); });
        try
        {
            var display = source.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", default);
            var model = source.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Model, "33", default);
            await both.Task.WaitAsync(Deadline); Assert.Equal((2, 2, 2), source.Pressure);
            var other = fixture.Core with { Snapshot = fixture.Core.Snapshot with { Context = fixture.Core.Snapshot.Context with { BuildIdentity = new('f', 64) } } };
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => source.ProbeAsync(other, CreatureDisplayTargetRole.Display, "123", default).WaitAsync(Deadline))).Code);
            Assert.Equal(2, opens); Assert.Equal(2, source.Pressure.Readers);
            release.SetResult(); await Task.WhenAll(display, model).WaitAsync(Deadline);
            Assert.Equal((2, 0, 0), source.Pressure);
            await source.ProbeAsync(other, CreatureDisplayTargetRole.Display, "123", default);
            Assert.Equal(3, opens); Assert.Equal(1, source.Pressure.Readers);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task CanceledLastBorrowAwaitsLateOpenCleanupBeforeCapacityRetires()
    {
        await using var fixture = await Fixture.Create(); var entered = Signal(); var release = Signal();
        await using var source = fixture.Sources(async (hash, size, _) =>
        { entered.SetResult(); await release.Task; return await fixture.Catalog.Projection.Objects.OpenVerifiedAsync(hash, size, default); });
        using var stop = new CancellationTokenSource();
        try
        {
            var probe = source.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", stop.Token);
            await entered.Task.WaitAsync(Deadline); stop.Cancel(); Assert.False(probe.IsCompleted); Assert.Equal(1, source.Pressure.Readers);
            var other = fixture.Core with { Snapshot = fixture.Core.Snapshot with { Context = fixture.Core.Snapshot.Context with { SourceRegion = "eu" } } };
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => source.ProbeAsync(other, CreatureDisplayTargetRole.Display, "123", default).WaitAsync(Deadline))).Code);
            release.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe); Assert.Equal(0, source.Pressure.Readers);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task EncodedVerificationCloseFailureRetainsChargedDebtAndShutdownReportsIt()
    {
        await using var fixture = await Fixture.Create(); FaultyFile? file = null;
        var source = fixture.Sources(async (hash, size, token) =>
        {
            await using var verified = await fixture.Catalog.Projection.Objects.OpenVerifiedAsync(hash, size, token);
            var path = verified.Name; await verified.DisposeAsync(); file = new(path); return file;
        });
        try
        {
            Assert.Equal("CREATURE_DISPLAY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => source.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", default))).Code);
            Assert.Equal((1, 0, 0), source.Pressure);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => source.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", default))).Code);
            Assert.Equal("CREATURE_DISPLAY_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => source.DisposeAsync().AsTask())).Code);
            Assert.Equal(1, source.Pressure.Readers);
        }
        finally { if (file is not null) await file.CloseForTestAsync(); }
    }
    private sealed class FaultyFile(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("authored uncertain close"));
        internal ValueTask CloseForTestAsync() => base.DisposeAsync();
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal CreatureDisplayCatalogTests.Harness Catalog = null!;
        internal CreaturesStore Creatures = null!;
        internal TableDataService Tables = null!;
        internal CreaturesDisplayAuthorization Core = null!;
        internal readonly AcquisitionResources Resources = new();
        internal readonly List<string> Prepared = [];
        internal CreatureDisplayMetadataRequest Request => new(Core.Snapshot.Context, Core.Artifact.ContentKey, "1", 0);
        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture { Catalog = await CreatureDisplayCatalogTests.Harness.Create() };
            var authored = InventoryFixture.Create();
            foreach (var bytes in new[] { authored.BuildConfig, authored.CdnConfig, authored.PayloadA.Encoded })
            {
                string path; await using (var staging = await fixture.Catalog.Projection.Objects.CreateStagingFileAsync(default)) { path = staging.Name; await staging.WriteAsync(bytes); }
                await fixture.Catalog.Projection.Objects.PublishFileAsync(path, bytes.Length, default);
            }
            fixture.Tables = new(new ReadyTables(fixture));
            fixture.Creatures = new(fixture.Catalog.Catalog, fixture.Catalog.Projection.Objects, fixture.Tables, fixture.Resources);
            fixture.Core = await fixture.Creatures.AuthorizeDisplayAsync(fixture.Catalog.Core.Binding.Context, fixture.Catalog.Core.ContentKey, "1", default);
            await fixture.PublishTarget(CreatureDisplayTargetRole.Display); await fixture.PublishTarget(CreatureDisplayTargetRole.Model);
            return fixture;
        }
        internal CreatureDisplaySources Sources(Func<string, long, CancellationToken, Task<FileStream>>? open = null) => open is null
            ? new(Catalog.Catalog, Catalog.Projection.Objects, Creatures, Tables, Resources)
            : new(Catalog.Catalog, Catalog.Projection.Objects, Creatures, Tables, Resources, open);
        internal async Task<TableDataSnapshot> PublishTarget(CreatureDisplayTargetRole role, bool historical = false)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[CreatureDisplayPolicy.Table(role)];
            var template = CreatureDisplayPolicyTests.Rows(definition.Document, role); var edge = CreatureDisplayPolicy.SelectField(definition.Document, Catalog.Opening.Build.Version, template, role);
            var original = Catalog.Targets.Single(t => t.TableId == CreatureDisplayPolicy.Table(role)).Admission;
            var binding = Core.Artifact.Binding.Rows.Binding with { DefinitionSha256 = definition.Sha256, ProviderRevision = BundledTableDefinitions.Revision,
                DecoderRevision = historical ? "wdc5-values-v7+table-admission-v2" : TableDataStore.DecoderRevision,
                SchemaSelectionSha256 = TableRowArtifacts.SchemaSelectionKey(definition.Document, Catalog.Opening.Build.Version) };
            await using var writerOwner = new TableRowArtifacts(Catalog.Projection.Objects); await using var writer = await writerOwner.CreateWriterAsync(binding, default);
            var copy = role == CreatureDisplayTargetRole.Display;
            await writer.HeaderAsync(template.Header with { DeclaredCopyRecords = copy ? 1 : 0 }, default);
            string[] Values(uint id)
            {
                var values = template.Header.Columns.Select(c => c.Kind == "string" ? "" : "0").ToArray();
                for (var i = 0; i < values.Length; i++) if (template.Header.Columns[i].IsId) values[i] = id.ToString(CultureInfo.InvariantCulture);
                values[edge] = copy ? "33" : "777"; return values;
            }
            await writer.RowAsync(new(copy ? 122u : 33u, false, null, Values(copy ? 122u : 33u)), default);
            if (copy) await writer.RowAsync(new(123, true, 122, Values(123)), default);
            var rows = await writer.CompleteAsync(template.Completion with { CopyRows = copy ? 1 : 0, ConsumedBytes = long.Parse(original.Source.DecodedBytes, CultureInfo.InvariantCulture) }, default);
            await Catalog.Catalog.RecordTableArtifactAsync(rows, default);
            await Catalog.Catalog.CommitTableAdmissionAsync(Catalog.Opening, CreatureDisplayPolicy.Table(role), "enUS", original.Source, original.Association, rows, original.PreparedAt, default);
            return Core.Snapshot.CreatureSource with { TableId = CreatureDisplayPolicy.Table(role), ArtifactId = rows.ContentKey };
        }
        private sealed class ReadyTables(Fixture fixture) : ITableDataStore
        {
            public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => Task.FromResult(new TableDataReadiness(null));
            public Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
            { fixture.Prepared.Add(request.TableId); return fixture.PublishTarget(request.TableId == CreatureDisplayPolicy.DisplayTable ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model); }
            public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("No C2 row query through Tables.");
        }
        public async ValueTask DisposeAsync() { await Creatures.DisposeAsync(); await Tables.DisposeAsync(); await Catalog.DisposeAsync(); }
    }
}
