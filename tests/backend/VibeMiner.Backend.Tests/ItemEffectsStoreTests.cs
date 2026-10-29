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

    // #85: a table the exact build's ROOT doesn't list (Classic Era 1.15 has no ItemXItemEffect) is a settled fact of
    // that build, never a retryable unavailable source. A transient miss keeps its retryable state.
    [Theory]
    [InlineData("CONTENT_VARIANT_MISSING", false, ItemEffectsReadinessState.NotInBuild, "ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", false)]
    [InlineData("CONTENT_UNAVAILABLE", true, ItemEffectsReadinessState.Unavailable, "ITEM_EFFECTS_SOURCE_UNAVAILABLE", true)]
    [InlineData("HTTP_TIMEOUT", true, ItemEffectsReadinessState.Unavailable, "ITEM_EFFECTS_SOURCE_UNAVAILABLE", true)]
    public async Task ABridgeTableTheBuildDoesNotShipIsNotInBuildAndNotRetryable(string failure, bool transient,
        ItemEffectsReadinessState expected, string code, bool retryable)
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false);
        f.BeforePrepare = (_, _) => throw new DiscoveryException(failure, "Authored acquisition outcome.", transient);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(expected, links.State); Assert.Null(links.Snapshot); Assert.Null(links.ObservedAssociations);
        Assert.Equal(Enum.Parse<ItemEffectsSourceState>(expected.ToString()), links.Source!.State);
        Assert.Equal(code, links.Problem!.Code); Assert.Equal(retryable, links.Problem.Retryable);
        Assert.Equal(links.Problem, links.Source.Problem);
        Assert.Equal(new[] { "00cb674f" }, f.Prepared.ToArray());
        ItemEffectsValidation.Validate(links, f.Selection);
    }

    [Fact]
    public async Task AnEffectTableTheBuildDoesNotShipLeavesLinksAndNeverConsultsNames()
    {
        await using var f = await ItemEffectsStoreFixture.Create(effect: false, name: false);
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var keys = page.Edges.Select(edge => edge.Key).ToArray();
        f.BeforePrepare = (_, _) => throw new DiscoveryException("CONTENT_VARIANT_MISSING", "Authored acquisition outcome.");
        var details = await f.Store.PrepareDetailsAsync(new(f.Selection, ready.Snapshot.ArtifactId, keys), _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.NotInBuild, details.Source.State);
        Assert.False(details.Source.Problem!.Retryable);
        Assert.Contains(details.Outcomes, outcome => outcome.State == ItemEffectState.NotInBuild && outcome.Problem == details.Source.Problem);
        Assert.Equal(new[] { "4002a5b1" }, f.Prepared.ToArray());
    }

    // #86: Classic Era 1.15.9 has no ItemXItemEffect. Links come from ItemEffect.ParentItemID, the ItemEffect row is its own
    // effect, and one ItemEffect preparation serves both the links and the inputs.
    [Fact]
    public async Task ClassicLinksThroughItemEffectParentItemIdWithOneItemEffectSource()
    {
        await using var f = await ItemEffectsStoreFixture.Create(bridge: false, effect: false, name: false, classic: true);
        var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.Ready, links.State); Assert.Equal("3", links.ObservedAssociations);
        Assert.Equal("4002a5b1", links.Snapshot!.BridgeSource.TableId); Assert.Equal("1BF9CF3A", links.Snapshot.BridgeSource.LayoutHash);
        Assert.Equal(new ItemEffectsBridgeFields(new("4002a5b1", 0), new("4002a5b1", 0), new("4002a5b1", 10)), links.Snapshot.Fields);
        var page = await f.Store.ReadPageAsync(new(f.Selection, links.Snapshot.ArtifactId), default);
        Assert.Equal(new[] { "0", "7", "9" }, page.Edges.Select(edge => edge.Bridge.RowId).ToArray());
        Assert.All(page.Edges, edge =>
        {
            Assert.Equal("4002a5b1", edge.Bridge.TableId); Assert.Equal(edge.Bridge.RowId, edge.ItemEffectId.Value);
            Assert.Equal(new ItemEffectsSourceValue("4002a5b1", 10, "1"), edge.ParentItemId); Assert.Equal(0, edge.ItemEffectId.ColumnIndex);
        });
        Assert.True(page.Edges[2].Bridge.IsCopy); Assert.Equal("7", page.Edges[2].Bridge.CopySourceId);
        // The key must be the link row's own ID, and the parent must be column 10, not a bridge column.
        var request = new ItemEffectsPageRequest(f.Selection, links.Snapshot.ArtifactId);
        foreach (var changed in new[] { page.Edges[0] with { ItemEffectId = page.Edges[0].ItemEffectId with { Value = "7" } },
            page.Edges[0] with { ParentItemId = page.Edges[0].ParentItemId with { ColumnIndex = 2 } } })
            Assert.Equal("ITEM_EFFECTS_RESULT_INVALID", Assert.Throws<DiscoveryException>(() =>
                ItemEffectsValidation.Validate(page with { Edges = [changed, .. page.Edges.Skip(1)] }, request)).Code);
        var keys = page.Edges.Select(edge => edge.Key).ToArray();
        var details = await f.Store.PrepareDetailsAsync(new(f.Selection, links.Snapshot.ArtifactId, keys), _ => Task.CompletedTask, default);
        Assert.Equal(new[] { "4002a5b1" }, f.Prepared.ToArray());
        Assert.Equal(ItemEffectsSourceState.Ready, details.Source.State);
        Assert.Equal(links.Snapshot.BridgeSource.ArtifactId, details.Source.Snapshot!.ArtifactId);
        Assert.All(details.Outcomes, outcome => Assert.Equal(ItemEffectState.ObservedReady, outcome.State));
        var seven = details.Outcomes[1].Fields!;
        Assert.Equal(new[] { "1", "2", "3", "4", "5", "6", "-1", "65535", "9" }, seven.Select(field => field.Value).ToArray());
        Assert.All(seven.Take(8), field => Assert.Equal(ItemEffectEvidenceKind.StrictNumeric, field.Evidence));
        Assert.All(seven, field => Assert.Null(field.Interpretation));
        Assert.Equal(ItemEffectEvidenceKind.UnverifiedCandidate, seven[8].Evidence);
        Assert.Equal(new TableReference("SpellCategory", "ID"), seven[5].CandidateReference);
        Assert.Equal(new TableReference("ChrSpecialization", "ID"), seven[7].CandidateReference);
        Assert.Equal("9", details.Outcomes[2].Target!.RowId); Assert.True(details.Outcomes[2].Target!.IsCopy);
        var names = await f.Store.PrepareNamesAsync(new(f.Selection, links.Snapshot.ArtifactId, details.Source.Snapshot.ArtifactId, keys), _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsSourceState.Ready, names.Sources[1].State);
        Assert.Equal(new[] { "4002a5b1", "46c66698" }, f.Prepared.ToArray());
        Assert.Equal(ItemEffectNameState.Ready, names.Outcomes[0].State);
        Assert.Equal(ItemEffectNameState.SpellKeyOutOfRange, names.Outcomes[1].State);
    }

    // Retail keeps the ItemXItemEffect bridge; Classic builds link through ItemEffect.ParentItemID (#86). An unlisted build
    // whose version line holds both kinds of layout (1.x: Classic layouts and Classic Beta 1.60's 4CA77678) is undecided
    // until its ItemEffect file's layout chooses; "-" means undecided.
    [Theory]
    [InlineData("12.1.5.70448", null, "itemXItemEffect")]
    [InlineData("12.2.0.80000", null, "itemXItemEffect")]
    [InlineData("11.2.7.64978", null, "itemXItemEffect")]
    [InlineData("1.60.1.70450", null, "itemXItemEffect")]
    [InlineData("1.15.9.70003", null, "parentItemId")]
    [InlineData("5.5.4.70449", null, "parentItemId")]
    [InlineData("5.5.3.66382", null, "parentItemId")]
    [InlineData("2.5.6.70006", null, "parentItemId")]
    [InlineData("5.5.4.70631", null, "parentItemId")]
    [InlineData("5.5.5.71000", null, "parentItemId")]
    [InlineData("2.5.6.70900", null, "parentItemId")]
    [InlineData("1.15.9.70576", null, "-")]
    [InlineData("1.15.9.70576", "1BF9CF3A", "parentItemId")]
    [InlineData("1.15.10.71000", "1BF9CF3A", "parentItemId")]
    [InlineData("1.15.10.71000", "4CA77678", "itemXItemEffect")]
    [InlineData("1.60.2.71000", null, "-")]
    [InlineData("1.60.2.71000", "4CA77678", "itemXItemEffect")]
    // An unlisted 1.x build shipping an ItemEffect layout no definition knows links through ItemEffect, so that layout reads as
    // unsupported instead of ItemXItemEffect as missing.
    [InlineData("1.15.10.71000", "12345678", "parentItemId")]
    public async Task TheBundledItemEffectDefinitionChoosesEachBuildsLink(string version, string? layout, string link)
    {
        var provider = await TablesCatalog.Provider.Value;
        Assert.Equal(link, ItemEffectsLinkChoice.For(provider, version, layout, default)?.Name ?? "-");
    }

    // An undecided build prepares ItemEffect first; its layout then picks the link, and a Retail layout goes on to ItemXItemEffect.
    [Theory]
    [InlineData(false, new[] { "4002a5b1", "00cb674f" }, "00cb674f")]
    [InlineData(true, new[] { "4002a5b1" }, "4002a5b1")]
    public async Task AnUndecidedBuildPreparesItemEffectFirstAndFollowsItsLayout(bool classic, string[] prepared, string bridge)
    {
        // Builds no other test opens, so the test-only undecided mark can't reach a test running in parallel.
        var version = classic ? "1.15.9.69722" : "12.1.5.70391";
        ItemEffectsLinkChoice.UndecidedForTests[version] = true;
        try
        {
            await using var f = await ItemEffectsStoreFixture.Create(bridge: false, effect: false, name: false, classic: classic, version: version);
            var undecided = await f.Store.ReadAsync(f.Selection, default);
            Assert.Equal(ItemEffectsReadinessState.NotPrepared, undecided.State); Assert.Null(undecided.Link);
            var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            Assert.Equal(ItemEffectsReadinessState.Ready, links.State); Assert.Equal(bridge, links.Snapshot!.BridgeSource.TableId);
            Assert.Equal(classic ? "parentItemId" : "itemXItemEffect", links.Link);
            Assert.Equal(prepared, f.Prepared.ToArray());
        }
        finally { ItemEffectsLinkChoice.UndecidedForTests.TryRemove(version, out _); }
    }

    // An undecided build needs ItemEffect whichever way it decides, so a failure preparing it names ItemEffect's link, never
    // a Retail ItemXItemEffect one.
    [Theory]
    [InlineData("CONTENT_UNAVAILABLE", true, ItemEffectsReadinessState.Unavailable)]
    [InlineData("DB2_INTEGRITY", false, ItemEffectsReadinessState.Corrupt)]
    public async Task AnUndecidedBuildsFailedItemEffectNamesTheParentItemIdLink(string failure, bool transient, ItemEffectsReadinessState expected)
    {
        const string version = "1.15.9.69109";
        ItemEffectsLinkChoice.UndecidedForTests[version] = true;
        try
        {
            await using var f = await ItemEffectsStoreFixture.Create(bridge: false, effect: false, name: false, classic: true, version: version);
            f.BeforePrepare = (_, _) => throw new DiscoveryException(failure, "Authored acquisition outcome.", transient);
            var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            Assert.Equal(expected, links.State); Assert.Equal("parentItemId", links.Link);
            Assert.Equal(new[] { "4002a5b1" }, f.Prepared.ToArray());
            ItemEffectsValidation.Validate(links, f.Selection);
        }
        finally { ItemEffectsLinkChoice.UndecidedForTests.TryRemove(version, out _); }
    }

    // An undecided build whose ItemEffect file has a layout no definition knows says that ItemEffect layout is unsupported;
    // it never goes on to ask for ItemXItemEffect and call it missing.
    [Fact]
    public async Task AnUndecidedBuildsUnknownItemEffectLayoutReadsAsUnsupportedNotAsAMissingBridge()
    {
        const string version = "1.15.9.69547";
        ItemEffectsLinkChoice.UndecidedForTests[version] = true;
        try
        {
            await using var f = await ItemEffectsStoreFixture.Create(bridge: false, effect: false, name: false, classic: true, version: version);
            var unknown = await f.Source(ItemEffectsSourceRole.Effect, f.Rows[ItemEffectsSourceRole.Effect], fileLayout: "12345678");
            f.Desired[ItemEffectsSourceRole.Bridge] = unknown; f.Desired[ItemEffectsSourceRole.Effect] = unknown;
            var links = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
            Assert.Equal(ItemEffectsReadinessState.Unsupported, links.State);
            Assert.Equal("parentItemId", links.Link); Assert.Null(links.Source!.Snapshot);
            Assert.Equal("ITEM_EFFECTS_SOURCE_UNSUPPORTED", links.Problem!.Code);
            Assert.Equal(new[] { "4002a5b1" }, f.Prepared.ToArray());
        }
        finally { ItemEffectsLinkChoice.UndecidedForTests.TryRemove(version, out _); }
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
    // Classic Era 1.15.9 links through ItemEffect.ParentItemID (#86): one ItemEffect admission is both the bridge and
    // the effect source. Row 9 copies 7; row 8 belongs to another item.
    internal bool Classic;
    internal static readonly Db2Row[] ClassicEffects =
    [
        new(0, false, null, ["0", "0", "0", "0", "-1", "-2", "5", "0", "8", "-1", "1"]),
        new(7, false, null, ["7", "1", "2", "3", "4", "5", "6", "-1", "65535", "9", "1"]),
        new(8, false, null, ["8", "0", "1", "0", "0", "0", "0", "0", "0", "0", "2"]),
        new(9, true, 7, ["9", "1", "2", "3", "4", "5", "6", "-1", "65535", "9", "1"])
    ];
    internal string TableFor(ItemEffectsSourceRole role) => Classic && role == ItemEffectsSourceRole.Bridge ? "4002a5b1" : Table(role);
    internal static async Task<ItemEffectsStoreFixture> Create(bool bridge = true, bool effect = true, bool name = true, bool twoItems = false, int coreColumns = 3, uint extraItem = 2, bool singleCpu = false, bool classic = false, string? version = null)
    {
        var f = new ItemEffectsStoreFixture { H = await CoreHarness.Create(version: version ?? (classic ? "1.15.9.70003" : "12.1.5.70448")), Classic = classic }; var h = f.H;
        if (classic) { f.Rows[ItemEffectsSourceRole.Bridge] = ClassicEffects; f.Rows[ItemEffectsSourceRole.Effect] = ClassicEffects; }
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
            f.Desired[role] = classic && role == ItemEffectsSourceRole.Effect ? f.Desired[ItemEffectsSourceRole.Bridge] : await f.Source(role, f.Rows[role]);
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
    internal async Task<StoredTableAdmission> Source(ItemEffectsSourceRole role, Db2Row[] rows, string? decoder = null, bool partial = false, string variant = "", Action<Db2Column[]>? changeColumns = null, Func<TableArtifactBinding, TableArtifactBinding>? changeBinding = null, bool alternate = false, string? fileLayout = null)
    {
        var table = TableFor(role); var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
        var original = role == ItemEffectsSourceRole.Name || alternate ? H.Sparse : H.Item;
        var binding = original.Rows.Binding with { DefinitionSha256 = definition.Sha256, ProviderRevision = BundledTableDefinitions.Revision,
            DecodedSha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { role, rows, partial, variant }))),
            DecoderRevision = decoder ?? TableDataStore.AdmissionRevision(table, definition.Sha256, BundledTableDefinitions.Revision, false),
            SchemaSelectionSha256 = TableRowArtifacts.SchemaSelectionKey(definition.Document, H.Opening.Build.Version) };
        if (changeBinding is not null) binding = changeBinding(binding);
        var layout = Classic && role != ItemEffectsSourceRole.Name ? "1BF9CF3A" : role switch { ItemEffectsSourceRole.Bridge => "96F083AD", ItemEffectsSourceRole.Effect => "4CA77678", _ => "782EE721" };
        var fields = definition.Document.SelectBuild(H.Opening.Build.Version, layout).Blocks.Single().Fields;
        var columns = fields.Select((field, i) => new Db2Column(field.Column.Name, field.Column.Family == "int" ? "integer" : "string", field.IsId,
            !Classic && role == ItemEffectsSourceRole.Effect && i is 3 or 6 or 8 ? new("raw-physical-integer-v1", 32, false, 16, i != 3,
                "The physical integer width differs from the candidate definition.") : null)).ToArray();
        changeColumns?.Invoke(columns);
        var physical = rows.Count(row => !row.IsCopy); var copies = rows.Length - physical; var excluded = partial ? 5 : 0;
        await using var artifacts = new TableRowArtifacts(H.Objects, Resources, null);
        await using var writer = await artifacts.CreateWriterAsync(binding, default);
        // fileLayout: a header layout the definition doesn't know, with the reviewed columns.
        await writer.HeaderAsync(new(5, table.ToUpperInvariant(), fileLayout ?? layout, physical + excluded, copies, 4, columns), default);
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
        await H.Catalog.CommitTableAdmissionAsync(H.Opening, TableFor(role), "enUS", source.Source, source.Association, source.Artifact, source.PreparedAt, default);
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
            token.ThrowIfCancellationRequested(); var role = Enum.GetValues<ItemEffectsSourceRole>().First(role => f.TableFor(role) == request.TableId);
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
