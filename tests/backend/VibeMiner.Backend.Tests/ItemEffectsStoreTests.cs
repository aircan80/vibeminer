using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using CoreHarness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsStoreTests
{
    [Fact]
    public async Task LocalReadVerifiesOnlyBridgeAndMissingItemNeverProbesPoisonedOptionalSource()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var read = await f.Store.ReadAsync(f.Selection, default);
        Assert.Equal(ItemEffectsReadinessState.NotPrepared, read.State); Assert.Equal(ItemEffectsSourceState.Ready, read.Source!.State);
        Assert.Null(read.Snapshot); Assert.Empty(f.Prepared); Assert.Equal(0, f.Store.Pressure.TargetSlots);
        Assert.Null(await f.H.Catalog.ReadItemEffectsBridgeAdmissionAsync(f.H.Opening, default));
        await f.Sql("UPDATE table_admissions SET source=$v WHERE table_id='00cb674f'", new string('x', 131073));
        Assert.Equal(ItemEffectsReadinessState.MissingItem, (await f.Store.ReadAsync(f.Selection with { ItemId = "2" }, default)).State);
        Assert.Equal("ITEM_EFFECTS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => f.Store.ReadAsync(f.Selection, default))).Code);
        Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task ImmutableLinksPreserveOccurrencesCopiesAndZeroThroughIndependentDetailsNamesAndColdReopen()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.Ready, ready.State); Assert.Equal("5", ready.ObservedAssociations);
        Assert.Equal(0, f.Store.Pressure.TargetSlots); Assert.Empty(f.Prepared);
        var edges = new List<ItemEffectsEdge>(); string? cursor = null;
        do
        {
            var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId, 2, cursor), default);
            ItemEffectsValidation.Validate(page, new(f.Selection, ready.Snapshot.ArtifactId, 2, cursor));
            edges.AddRange(page.Edges); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(5, edges.Count); Assert.Equal(2, edges.Count(edge => edge.Bridge.RowId == "500"));
        Assert.Equal(5, edges.Select(edge => edge.Bridge.SourceOrdinal).Distinct().Count());
        Assert.Equal("500", Assert.Single(edges, edge => edge.Bridge.IsCopy).Bridge.CopySourceId);
        var keys = edges.Select(edge => edge.Key).ToArray();
        var details = await f.Store.ReadDetailsAsync(new(f.Selection, ready.Snapshot.ArtifactId, keys), default);
        Assert.Equal(4, details.Outcomes.Count(outcome => outcome.State == ItemEffectState.ObservedReady));
        Assert.Single(details.Outcomes, outcome => outcome.State == ItemEffectState.EffectKeyOutOfRange);
        var zero = details.Outcomes.First(outcome => outcome.ItemEffectId == "0");
        Assert.Equal("4294967295", zero.Fields![2].Value); Assert.Equal(ItemEffectEvidenceKind.RawPhysicalInteger, zero.Fields[2].Evidence);
        Assert.Equal("0", zero.Fields[6].Value); Assert.Equal(new TableReference("Spell", "ID"), details.Source.Snapshot!.Columns[7].Reference);
        var names = await f.Store.ReadNamesAsync(new(f.Selection, ready.Snapshot.ArtifactId, details.Source.Snapshot.ArtifactId, keys), default);
        Assert.Equal(3, names.Outcomes.Count(outcome => outcome.State == ItemEffectNameState.Ready));
        Assert.All(names.Outcomes.Where(outcome => outcome.State == ItemEffectNameState.Ready), outcome => Assert.Equal(" ", outcome.Text));
        Assert.Single(names.Outcomes, outcome => outcome.State == ItemEffectNameState.SpellKeyOutOfRange);
        Assert.Single(names.Outcomes, outcome => outcome.State == ItemEffectNameState.EffectNotReady);
        Assert.Empty(f.Prepared); Assert.InRange(f.Store.Pressure.TargetSlots, 0, 2);
        await f.Reopen();
        var reopened = await f.Store.ReadAsync(f.Selection, default);
        Assert.Equal(JsonSerializer.Serialize(ready), JsonSerializer.Serialize(reopened));
        Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task LinksDetailsAndNamesAcquireOnlyTheirOwnMissingPrerequisites()
    {
        await using var f = await ItemEffectsStoreFixture.Create(effect: false, name: false);
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var keys = page.Edges.Select(edge => edge.Key).ToArray();
        var request = new ItemEffectDetailsRequest(f.Selection, ready.Snapshot.ArtifactId, keys);
        var local = await f.Store.ReadDetailsAsync(request, default);
        Assert.Equal(ItemEffectsSourceState.NotPrepared, local.Source.State); Assert.Empty(f.Prepared);
        var details = await f.Store.PrepareDetailsAsync(request, _ => Task.CompletedTask, default);
        Assert.Equal(new[] { "4002a5b1" }, f.Prepared.ToArray()); Assert.Equal(ItemEffectsSourceState.Ready, details.Source.State);
        var namesRequest = new ItemEffectNamesRequest(f.Selection, ready.Snapshot.ArtifactId, details.Source.Snapshot!.ArtifactId, keys);
        var names = await f.Store.ReadNamesAsync(namesRequest, default);
        Assert.Equal(ItemEffectsSourceState.NotPrepared, names.Sources[1].State); Assert.Single(f.Prepared);
        var prepared = await f.Store.PrepareNamesAsync(namesRequest, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.Ready, prepared.Sources[1].State);
        Assert.Equal(new[] { "4002a5b1", "46c66698" }, f.Prepared.ToArray());
    }
}

// Authored row artifacts exercise orchestration/authority, not native WDC5/BLTE qualification.
internal sealed class ItemEffectsStoreFixture : IAsyncDisposable
{
    internal CoreHarness H = null!;
    internal ItemEffectsStore Store = null!;
    private AcquisitionResources? limitedResources;
    internal AcquisitionResources Resources => limitedResources ?? H.Resources;
    internal ItemEffectsSelection Selection = null!;
    internal readonly ConcurrentQueue<string> Prepared = new();
    internal Func<TableDataRequest, CancellationToken, Task>? BeforePrepare { get; set; }
    internal Func<TableDataRequest, Func<string, Task>, CancellationToken, Task>? DuringPrepare { get; set; }
    internal readonly Dictionary<ItemEffectsSourceRole, StoredTableAdmission> Desired = [];
    internal readonly Dictionary<ItemEffectsSourceRole, Db2Row[]> Rows = new()
    {
        [ItemEffectsSourceRole.Bridge] = [new(500, false, null, ["500", "0", "1"]), new(501, false, null, ["501", "7", "1"]),
            new(502, false, null, ["502", "-1", "1"]), new(503, true, 500, ["503", "0", "1"]), new(500, false, null, ["500", "0", "1"])],
        [ItemEffectsSourceRole.Effect] = [new(0, false, null, ["0", "0", "0", "4294967295", "-1", "-2", "65536", "0", "70000", "-1"]),
            new(7, false, null, ["7", "1", "2", "3", "4", "5", "6", "-1", "8", "9"])],
        [ItemEffectsSourceRole.Name] = [new(0, false, null, ["0", " "])]
    };
    internal static async Task<ItemEffectsStoreFixture> Create(bool bridge = true, bool effect = true, bool name = true, bool twoItems = false, int coreColumns = 3, uint extraItem = 2, bool singleCpu = false)
    {
        var f = new ItemEffectsStoreFixture { H = await CoreHarness.Create() }; var h = f.H;
        if (singleCpu)
        {
            await h.Store.DisposeAsync(); f.limitedResources = new(maximumCpu: 1, maximumBackgroundCpu: 1);
            h.Store = new(h.Catalog, h.Objects, h.Tables, f.Resources);
        }
        if (twoItems || coreColumns > 3) await ExpandCore(h, twoItems ? 2 : 1, coreColumns, extraItem);
        var core = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        f.Selection = new(h.Request, core.ArtifactId, "1");
        foreach (var payload in new[] { h.Fixture.PayloadA, h.Fixture.PayloadB })
            await h.Objects.PublishAsync(payload.Encoded, Convert.ToHexStringLower(SHA256.HashData(payload.Encoded)), default);
        foreach (var role in Enum.GetValues<ItemEffectsSourceRole>())
        {
            f.Desired[role] = await f.Source(role, f.Rows[role]);
            if (role switch { ItemEffectsSourceRole.Bridge => bridge, ItemEffectsSourceRole.Effect => effect, _ => name }) await f.Publish(role, f.Desired[role]);
        }
        await h.Store.DisposeAsync(); await h.Tables.DisposeAsync();
        h.Tables = new(new ReadyTables(f)); h.Store = new(h.Catalog, h.Objects, h.Tables, f.Resources);
        f.Store = new(h.Catalog, h.Objects, h.Store, h.Tables, f.Resources); return f;
    }
    private static async Task ExpandCore(CoreHarness h, int count, int columnCount, uint extraItem)
    {
        foreach (var sparse in new[] { false, true })
        {
            var prior = sparse ? h.Sparse : h.Item;
            var binding = prior.Rows.Binding with { DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"core-{sparse}-{count}-{columnCount}"))) };
            var columns = prior.Rows.Header.Columns.Concat(Enumerable.Range(3, columnCount - 3).Select(index => new Db2Column($"Flags[{index}]", "integer", false))).ToArray();
            await using var reader = new TableRowArtifacts(h.Objects, h.Resources, null);
            await using var writer = await reader.CreateWriterAsync(binding, default);
            await writer.HeaderAsync(prior.Rows.Header with { DeclaredRecords = count, Columns = columns }, default);
            foreach (var id in count == 1 ? new uint[] { 1 } : new uint[] { 1, extraItem })
            {
                string[] first = sparse ? [N(id), "Item " + N(id), "4"] : [N(id), "2", "132759"];
                await writer.RowAsync(new(id, false, null, first.Concat(Enumerable.Repeat("0", columnCount - 3)).ToArray()), default);
            }
            var rows = await writer.CompleteAsync(prior.Rows.Completion with { DeclaredRecords = count, PhysicalRows = count }, default);
            var definition = (await TablesCatalog.Provider.Value).Definitions[prior.Snapshot.TableId];
            var snapshot = prior.Snapshot with { ArtifactId = rows.ContentKey, RowCount = N((uint)count),
                Columns = columns.Select(column => TableDataStore.ProjectColumn(column, definition.Document)).ToArray(),
                Coverage = prior.Snapshot.Coverage with { DeclaredRecords = N((uint)count), PhysicalRows = N((uint)count) } };
            var next = prior with { Rows = rows, Snapshot = snapshot };
            await h.Catalog.RecordTableArtifactAsync(rows, default);
            await h.Catalog.CommitTableAdmissionAsync(h.Opening, snapshot.TableId, "enUS", next.Source, next.Association, rows, CoreHarness.Time, default);
            if (sparse) h.Sparse = next; else h.Item = next;
        }
        static string N(uint value) => value.ToString(CultureInfo.InvariantCulture);
    }
    internal static string Table(ItemEffectsSourceRole role) => role switch
    { ItemEffectsSourceRole.Bridge => "00cb674f", ItemEffectsSourceRole.Effect => "4002a5b1", _ => "46c66698" };
    internal async Task<StoredTableAdmission> Source(ItemEffectsSourceRole role, Db2Row[] rows, string? decoder = null, bool partial = false, string variant = "", Action<Db2Column[]>? changeColumns = null, Func<TableArtifactBinding, TableArtifactBinding>? changeBinding = null, bool alternate = false)
    {
        var table = Table(role); var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
        var original = role == ItemEffectsSourceRole.Name || alternate ? H.Sparse : H.Item;
        var binding = original.Rows.Binding with { DefinitionSha256 = definition.Sha256, ProviderRevision = BundledTableDefinitions.Revision,
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { role, rows, partial, variant }))),
            DecoderRevision = decoder ?? TableDataStore.AdmissionRevision(table, definition.Sha256, BundledTableDefinitions.Revision, false),
            SchemaSelectionSha256 = TableRowArtifacts.SchemaSelectionKey(definition.Document, H.Opening.Build.Version) };
        if (changeBinding is not null) binding = changeBinding(binding);
        var layout = role switch { ItemEffectsSourceRole.Bridge => "96F083AD", ItemEffectsSourceRole.Effect => "4CA77678", _ => "782EE721" };
        var fields = definition.Document.SelectBuild(H.Opening.Build.Version, layout).Blocks.Single().Fields;
        var columns = fields.Select((field, i) => new Db2Column(field.Column.Name, field.Column.Family == "int" ? "integer" : "string", field.IsId,
            role == ItemEffectsSourceRole.Effect && i is 3 or 6 or 8 ? new("raw-physical-integer-v1", 32, false, 16, i != 3,
                "The physical integer width differs from the candidate definition.") : null)).ToArray();
        changeColumns?.Invoke(columns);
        var physical = rows.Count(row => !row.IsCopy); var copies = rows.Length - physical; var excluded = partial ? 5 : 0;
        await using var artifacts = new TableRowArtifacts(H.Objects, Resources, null);
        await using var writer = await artifacts.CreateWriterAsync(binding, default);
        await writer.HeaderAsync(new(5, table.ToUpperInvariant(), layout, physical + excluded, copies, 4, columns), default);
        foreach (var row in rows) await writer.RowAsync(row, default);
        var result = await writer.CompleteAsync(new(physical + excluded, physical, copies, partial ? 1 : 0, excluded, 0,
            partial ? "unencryptedSections" : "complete", long.Parse(original.Source.DecodedBytes, CultureInfo.InvariantCulture),
            rows.Sum(row => row.TextIssues?.Count ?? 0)), default);
        var association = original.Association with { FileDataId = provider.Entries.Single(entry => entry.TableId == table).Db2FileDataId! };
        return new(result, original.Source, association, CoreHarness.Time);
    }
    internal async Task Publish(ItemEffectsSourceRole role, StoredTableAdmission source)
    {
        await H.Catalog.RecordTableArtifactAsync(source.Artifact, default);
        await H.Catalog.CommitTableAdmissionAsync(H.Opening, Table(role), "enUS", source.Source, source.Association, source.Artifact, source.PreparedAt, default);
    }
    internal async Task Reopen()
    { await Store.DisposeAsync(); Store = new(H.Catalog, H.Objects, H.Store, H.Tables, Resources); }
    internal async Task Sql(string sql, string? value = null)
    {
        await using var connection = new SqliteConnection("Data Source=" + Path.Combine(H.DirectoryPath, "discovery-v1.sqlite3")); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        if (value is not null) command.Parameters.AddWithValue("$v", value); await command.ExecuteNonQueryAsync();
    }
    internal async Task<string> ObjectPath(string sha, long length)
    { await using var input = await H.Objects.OpenVerifiedAsync(sha, length, Resources, null, default); return input.Name; }
    public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await H.DisposeAsync(); }
    private sealed class ReadyTables(ItemEffectsStoreFixture f) : ITableDataStore
    {
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token) => throw new InvalidOperationException("Local effects never use generic eager readiness.");
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token) => throw new InvalidOperationException("Effects use held immutable row readers.");
        public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
        {
            f.Prepared.Enqueue(request.TableId);
            if (f.BeforePrepare is { } before) await before(request, token);
            if (f.DuringPrepare is { } during) await during(request, progress, token);
            token.ThrowIfCancellationRequested(); var role = Enum.GetValues<ItemEffectsSourceRole>().Single(role => Table(role) == request.TableId);
            var admission = f.Desired[role]; await f.Publish(role, admission);
            var a = admission.Artifact; var c = a.Completion; var definition = (await TablesCatalog.Provider.Value).Definitions[request.TableId];
            return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale,
                a.ContentKey, admission.PreparedAt, a.Header.LayoutHash, a.Binding.DefinitionSha256, a.Binding.ProviderRevision, a.Binding.DecoderRevision,
                admission.Source, admission.Association, a.Header.Columns.Select(column => TableDataStore.ProjectColumn(column, definition.Document)).ToArray(),
                new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections),
                    N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords), N(a.Header.DeclaredCopyRecords), N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(c.PhysicalRows + c.CopyRows), N(c.TextIssueCount));
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
