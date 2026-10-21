using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using S = VibeMiner.Domain.TableReferenceTextStates;

namespace VibeMiner.Backend.Tests;

public sealed class TableReferenceTextTests
{
    private const string Item = "50238ec2", ItemClass = "b977271e", ItemSparse = "919be54e", Creature = "c9d6b6b3",
        SpellName = "46c66698", ItemEffect = "4002a5b1";

    private static Db2Row R(uint id, params string[] values) => new(id, false, null, values);

    [Fact]
    public async Task Item_class_text_matches_ClassID_not_record_ID_and_keeps_every_value_state_apart()
    {
        await using var h = await Harness.Create();
        // Record IDs deliberately differ from ClassID, so a join on ID would find the wrong text.
        await h.Admit(ItemClass, "35680EB8", [new("ID", "integer", true), new("ClassName_lang", "string", false), new("ClassID", "integer", false)],
        [
            R(301, "301", "Recipe", "9"), R(302, "302", "", "4"), R(303, "303", "First", "7"), R(304, "304", "Second", "7"),
            new(305, false, null, ["305", "", "6"], [new(1, TableTextEvidence.InvalidUtf8, "ff")]),
            R(306, "306", new string('x', TableReferenceTextRules.MaximumTextUnits + 1), "5"), R(9, "9", "Wrong if joined on ID", "-128"),
        ]);
        var item = await h.Admit(Item, "996192AA", [new("ID", "integer", true), new("ClassID", "integer", false),
            new("Material", "integer", false), new("ContentTuningID", "integer", false)],
            [R(1, "1", "9", "1", "0"), R(2, "2", "4", "1", "0"), R(3, "3", "7", "1", "0"), R(4, "4", "6", "1", "0"), R(5, "5", "5", "1", "0"),
             R(6, "6", "12", "1", "0"), R(7, "7", "300", "1", "0"), R(8, "8", "-129", "1", "0"), R(10, "10", "9", "1", "0"), R(11, "11", "-128", "1", "0")]);
        await using var store = h.Store();
        var page = await store.ReadReferenceTextAsync(h.Rows(Item, item), default);

        Assert.Equal(TableReferenceTextRules.PolicyRevision, page.PolicyRevision);
        Assert.Equal(("453", "automatic"), (page.Columns[0].TextSource!.MeasuredEncodedBytes, page.Columns[0].TextSource!.LoadPolicy));
        Assert.Equal(["1", "2", "3", "4", "5", "6", "7", "8", "10", "11"], page.RowIds);
        var classColumn = Assert.Single(page.Columns, c => c.Name == "ClassID");
        Assert.Equal((S.Ready, 1), (classColumn.State, classColumn.ColumnIndex));
        Assert.Equal(new TableReference("ItemClass", "ClassID"), classColumn.Reference);
        Assert.Equal(("ItemClass", "ClassName_lang", "ClassID", "complete"), (classColumn.TextSource!.Table, classColumn.TextSource.TextColumn, classColumn.TextSource.MatchColumn, classColumn.TextSource.Scope));
        Assert.Equal(S.NoTextRule, Assert.Single(page.Columns, c => c.Name == "Material").State);
        Assert.Equal(S.NoDefinition, Assert.Single(page.Columns, c => c.Name == "ContentTuningID").State);
        Assert.DoesNotContain(page.Values, v => v.ColumnIndex != 1);

        var values = page.Values.ToDictionary(v => v.Value);
        Assert.Equal(10 - 1, values.Count); // "9" appears twice on the page and is looked up once.
        Assert.Equal((S.Resolved, "Recipe", "1", "301"), (values["9"].State, values["9"].Text, values["9"].MatchCount, values["9"].Row!.Id));
        Assert.Equal((S.Empty, "", "302"), (values["4"].State, values["4"].Text, values["4"].Row!.Id));
        Assert.Equal((S.Multiple, (string?)null, "2"), (values["7"].State, values["7"].Text, values["7"].MatchCount));
        Assert.Null(values["7"].Row);
        Assert.Equal((S.TextUnreadable, (string?)null, "305"), (values["6"].State, values["6"].Text, values["6"].Row!.Id));
        Assert.Equal((S.TextTooLong, (string?)null, "306"), (values["5"].State, values["5"].Text, values["5"].Row!.Id));
        Assert.Equal((S.NotFound, "0"), (values["12"].State, values["12"].MatchCount));
        Assert.Equal(S.KeyOutOfRange, values["300"].State);
        Assert.Equal(S.KeyOutOfRange, values["-129"].State);
        Assert.Equal((S.Resolved, "Wrong if joined on ID"), (values["-128"].State, values["-128"].Text));
    }

    [Fact]
    public async Task Unprepared_text_table_is_pending_and_a_partial_target_reports_its_scope()
    {
        await using var h = await Harness.Create();
        var item = await h.Admit(Item, "996192AA", [new("ID", "integer", true), new("ClassID", "integer", false)], [R(1, "1", "9"), R(2, "2", "3")]);
        await using (var store = h.Store())
        {
            var pending = await store.ReadReferenceTextAsync(h.Rows(Item, item), default);
            var column = Assert.Single(pending.Columns);
            Assert.Equal((S.NotPrepared, ItemClass, (string?)null), (column.State, column.TextSource!.TableId, column.TextSource.ArtifactId));
            Assert.Empty(pending.Values);
        }
        await h.Admit(ItemClass, "35680EB8", [new("ID", "integer", true), new("ClassName_lang", "string", false), new("ClassID", "integer", false)],
            [R(1, "1", "Recipe", "9")], partial: true);
        await using (var store = h.Store())
        {
            var ready = await store.ReadReferenceTextAsync(h.Rows(Item, item), default);
            var column = Assert.Single(ready.Columns);
            Assert.Equal((S.Ready, "unencryptedSections"), (column.State, column.TextSource!.Scope));
            Assert.Equal(S.Resolved, Assert.Single(ready.Values, v => v.Value == "9").State);
            // A miss in a partial table is scoped to its readable rows; the wire keeps the scope beside the miss.
            Assert.Equal(S.NotFound, Assert.Single(ready.Values, v => v.Value == "3").State);
        }
    }

    [Fact]
    public async Task Record_ID_targets_use_the_ID_directory_and_name_the_redirected_text_table()
    {
        await using var h = await Harness.Create();
        await h.Admit(SpellName, "782EE721", [new("ID", "integer", true), new("Name_lang", "string", false)], [R(0, "0", "Zero spell"), R(17, "17", "Authored spell")]);
        var effect = await h.Admit(ItemEffect, "4CA77678", [new("ID", "integer", true), new("SpellID", "integer", false)],
            [R(1, "1", "17"), R(2, "2", "0"), R(3, "3", "-1"), R(4, "4", "18")]);
        await using var store = h.Store();
        var page = await store.ReadReferenceTextAsync(h.Rows(ItemEffect, effect), default);
        var column = Assert.Single(page.Columns);
        Assert.Equal(new TableReference("Spell", "ID"), column.Reference);
        Assert.Equal(("SpellName", SpellName, "Name_lang"), (column.TextSource!.Table, column.TextSource.TableId, column.TextSource.TextColumn));
        Assert.Equal(("2754982", "onRequest"), (column.TextSource.MeasuredEncodedBytes, column.TextSource.LoadPolicy));
        var values = page.Values.ToDictionary(v => v.Value);
        Assert.Equal(("Authored spell", "17"), (values["17"].Text, values["17"].Row!.Id));
        Assert.Equal("Zero spell", values["0"].Text); // Zero is a real lookup.
        Assert.Equal(S.KeyOutOfRange, values["-1"].State); // Never wrapped to 4294967295.
        Assert.Equal(S.NotFound, values["18"].State);
    }

    [Fact]
    public async Task Item_ID_redirects_to_ItemSparse_and_a_copy_row_with_the_same_ID_counts_as_several_matches()
    {
        await using var h = await Harness.Create();
        await h.Admit(ItemSparse, "1C17D17F", [new("ID", "integer", true), new("Display_lang", "string", false), new("OppositeFactionItemID", "integer", false)],
            [R(5, "5", "Authored sword", "6"), R(6, "6", "Authored shield", "5"), R(7, "7", "Original", "0"), new(7, true, 6, ["7", "Copy", "0"]), R(8, "8", "Twin", "7")]);
        var sparse = await h.ReadSnapshot(ItemSparse);
        await using var store = h.Store();
        var page = await store.ReadReferenceTextAsync(h.Rows(ItemSparse, sparse), default);
        var column = Assert.Single(page.Columns);
        Assert.Equal((new TableReference("Item", "ID"), "ItemSparse", S.Ready), (column.Reference, column.TextSource!.Table, column.State));
        var values = page.Values.ToDictionary(v => v.Value);
        Assert.Equal("Authored shield", values["6"].Text);
        Assert.Equal((S.Multiple, "2"), (values["7"].State, values["7"].MatchCount));
        Assert.Equal(S.NotFound, values["0"].State);
    }

    [Fact]
    public async Task Non_ID_target_without_a_value_index_is_unindexed_and_never_scanned()
    {
        await using var h = await Harness.Create();
        var rows = Enumerable.Range(1, TableReferenceTextRules.MaximumValueIndexRows + 1)
            .Select(i => R((uint)i, i.ToString(CultureInfo.InvariantCulture), "Class", (i % 100).ToString(CultureInfo.InvariantCulture))).ToArray();
        await h.Admit(ItemClass, "35680EB8", [new("ID", "integer", true), new("ClassName_lang", "string", false), new("ClassID", "integer", false)], rows);
        var item = await h.Admit(Item, "996192AA", [new("ID", "integer", true), new("ClassID", "integer", false)], [R(1, "1", "9")]);
        await using var store = h.Store();
        var column = Assert.Single((await store.ReadReferenceTextAsync(h.Rows(Item, item), default)).Columns);
        Assert.Equal((S.Unindexed, "TABLE_REFERENCE_TEXT_UNINDEXED"), (column.State, column.Problem!.Code));
    }

    [Fact]
    public async Task Unusable_text_layout_is_unsupported_and_a_corrupt_text_artifact_is_unreadable()
    {
        await using var h = await Harness.Create();
        await h.Admit(Creature, "6E14C900", [new("ID", "integer", true), new("NameAlt_lang", "string", false), new("MountCreatureID", "integer", false)], [R(1, "1", "Alt", "1")]);
        var creature = await h.ReadSnapshot(Creature);
        await using (var store = h.Store())
        {
            var column = Assert.Single((await store.ReadReferenceTextAsync(h.Rows(Creature, creature), default)).Columns);
            Assert.Equal((S.Unsupported, "TABLE_REFERENCE_TEXT_LAYOUT"), (column.State, column.Problem!.Code));
        }
        var target = await h.Admit(ItemClass, "35680EB8", [new("ID", "integer", true), new("ClassName_lang", "string", false), new("ClassID", "integer", false)], [R(1, "1", "Recipe", "9")]);
        var item = await h.Admit(Item, "996192AA", [new("ID", "integer", true), new("ClassID", "integer", false)], [R(1, "1", "9")]);
        var path = h.PathFor(target.Artifact); var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await using (var store = h.Store())
        {
            var page = await store.ReadReferenceTextAsync(h.Rows(Item, item), default);
            var column = Assert.Single(page.Columns);
            Assert.Equal(S.Unreadable, column.State); Assert.NotNull(column.Problem); Assert.Empty(page.Values);
        }
    }

    [Fact]
    public async Task Creature_self_reference_resolves_Name_lang_and_page_binding_follows_the_row_query()
    {
        await using var h = await Harness.Create();
        await h.Admit(Creature, "6E14C900", [new("ID", "integer", true), new("Name_lang", "string", false), new("MountCreatureID", "integer", false)],
            Enumerable.Range(0, 60).Select(i => R((uint)i, i.ToString(CultureInfo.InvariantCulture), $"Authored creature {i}", ((i + 1) % 60).ToString(CultureInfo.InvariantCulture))).ToArray());
        var creature = await h.ReadSnapshot(Creature);
        await using var store = h.Store();
        var second = await store.ReadReferenceTextAsync(h.Rows(Creature, creature) with { After = "50" }, default);
        Assert.Equal(Enumerable.Range(50, 10).Select(i => i.ToString(CultureInfo.InvariantCulture)), second.RowIds);
        Assert.Equal("50", second.After);
        Assert.Equal("Authored creature 0", Assert.Single(second.Values, v => v.Value == "0").Text);
        var filtered = await store.ReadReferenceTextAsync(h.Rows(Creature, creature) with { RecordId = "7" }, default);
        Assert.Equal(["7"], filtered.RowIds); Assert.Equal("7", filtered.RecordId);
        Assert.Equal("Authored creature 8", Assert.Single(filtered.Values).Text);
        // Rows and reference text read the same page.
        var rows = await h.TableStore.ReadRowsAsync(h.Rows(Creature, creature) with { After = "50" }, default);
        Assert.Equal(rows.Rows.Select(r => r.Id), second.RowIds);
    }

    [Fact]
    public async Task Stale_artifact_unprepared_source_and_cancellation_are_refused_before_any_text()
    {
        await using var h = await Harness.Create();
        var item = await h.Admit(Item, "996192AA", [new("ID", "integer", true), new("ClassID", "integer", false)], [R(1, "1", "9")]);
        await using var store = h.Store();
        Assert.Equal("TABLE_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.ReadReferenceTextAsync(h.Rows(Item, item) with { ArtifactId = new string('0', 64) }, default))).Code);
        Assert.Equal("TABLE_NOT_PREPARED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.ReadReferenceTextAsync(h.Rows(Item, item) with { TableId = ItemClass }, default))).Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadReferenceTextAsync(h.Rows(Item, item), stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TableReferenceTextService(store).ReadAsync(h.Rows(Item, item), stop.Token));
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new TableReferenceTextService(store).ReadAsync(h.Rows(Item, item) with { Limit = 51 }))).Code);
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ReadReferenceTextAsync(h.Rows(Item, item), default));
    }

    [Fact]
    public void Allowlist_is_exactly_the_reviewed_four_rules()
    {
        Assert.Equal(["ItemClass::ClassID>ItemClass.ClassName_lang@ClassID", "Creature::ID>Creature.Name_lang@ID", "Spell::ID>SpellName.Name_lang@ID", "Item::ID>ItemSparse.Display_lang@ID"],
            TableReferenceTextRules.Allowlist.Select(r => $"{r.Table}::{r.Column}>{r.TextTable}.{r.TextColumn}@{r.MatchColumn}"));
        Assert.Equal((-128L, 127L, false), (TableReferenceTextRules.Allowlist[0].Minimum, TableReferenceTextRules.Allowlist[0].Maximum, TableReferenceTextRules.Allowlist[0].MatchesRecordId));
        // Small text tables load automatically; SpellName and ItemSparse wait for Load text.
        Assert.Equal(["ItemClass:automatic", "Creature:automatic", "SpellName:onRequest", "ItemSparse:onRequest"],
            TableReferenceTextRules.Allowlist.Select(r => $"{r.TextTable}:{r.LoadPolicy}"));
        Assert.Equal(1_048_576, TableReferenceTextRules.AutomaticLoadMaximumEncodedBytes);
        Assert.Equal("automatic", (TableReferenceTextRules.Allowlist[0] with { MeasuredEncodedBytes = 1_048_576 }).LoadPolicy);
        Assert.Equal("onRequest", (TableReferenceTextRules.Allowlist[0] with { MeasuredEncodedBytes = 1_048_577 }).LoadPolicy);
        Assert.Null(TableReferenceTextRules.For(new("ItemSubClass", "ID")));
        Assert.Null(TableReferenceTextRules.For(new("itemclass", "ClassID")));
    }

    internal sealed record Admitted(TableRowsArtifact Artifact, string TableId);

    internal sealed class Harness : IAsyncDisposable
    {
        private const string Time = "2026-10-19T00:00:00Z";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-reftext-" + Guid.NewGuid().ToString("N"));
        private readonly InventoryFixture fixture = InventoryFixture.Create();
        private readonly List<TableDataStore> stores = [];
        private readonly Dictionary<string, Admitted> admitted = [];
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal CapturedOpeningContext Opening = null!;
        internal ItemsContext Context = null!;
        internal TableDataStore TableStore = null!;
        private readonly HttpClient offline = new(new Offline());

        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.directory); h.Objects = new(h.directory);
            var versions = Fixtures.Versions($"us|{h.fixture.BuildKey}|{h.fixture.CdnKey}||70448|12.1.5.70448||reftext");
            var cdns = Encoding.UTF8.GetString(h.fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            var page = await h.Catalog.CommitAsync("wow", generation, discovery, Time, default);
            h.Context = new("wow", page.Builds.Single().Id, "us", "enUS");
            h.Opening = await h.Catalog.ResolveOpeningAsync(new("wow", h.Context.BuildIdentity, "us"), default);
            var configurations = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.fixture.BuildKey, h.fixture.BuildConfig), ("cdnConfig", h.fixture.CdnKey, h.fixture.CdnConfig) })
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); await h.Objects.PublishAsync(bytes, sha, default);
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, InventoryFixture.Url("config", key), Time), default);
                configurations.Add(new(role, key, sha, bytes.Length, InventoryFixture.Url("config", key), Time, Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configurations, Time, default);
            foreach (var role in new[] { "encoding", "root" }) await h.Catalog.RecordMetadataAsync(h.Metadata(role), default);
            h.TableStore = h.Store(); h.stores.Add(h.TableStore); return h;
        }
        internal TableDataStore Store() => new(Catalog, Objects, offline, resources: new AcquisitionResources());
        internal TableRowsRequest Rows(string table, Admitted value) =>
            new(Context.ProductId, Context.BuildIdentity, Context.SourceRegion, table, "enUS", value.Artifact.ContentKey);
        internal Task<Admitted> ReadSnapshot(string table) => Task.FromResult(admitted[table]);
        internal string PathFor(TableRowsArtifact artifact) => Path.Combine(directory, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]);
        private MetadataReceipt Metadata(string role)
        {
            var source = fixture.Metadata[role]; return new(role, source.ContentKey, source.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(source.Encoded)), N(source.Encoded.Length), N(source.Decoded.Length),
                InventoryFixture.Url("data", source.EncodingKey), Time, Time, MetadataAcquirer.SourceRevision);
        }
        // Authored rows under an exact pinned table/layout; no game content.
        internal async Task<Admitted> Admit(string table, string layout, Db2Column[] columns, Db2Row[] rows, bool partial = false)
        {
            var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[table];
            var bytes = fixture.PayloadA;
            var source = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)),
                N(bytes.Encoded.Length), N(bytes.Decoded.Length), InventoryFixture.Url("data", bytes.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
            await Catalog.RecordContentAsync(source, default);
            var entry = provider.Entries.Single(e => e.TableId == table);
            var association = new SelectedFileAssociation(entry.Db2FileDataId!, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), Metadata("root"),
                [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var binding = new TableArtifactBinding(source.Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(table + rows.Length + partial))),
                definition.Sha256, BundledTableDefinitions.Revision, "enUS",
                TableDataStore.AdmissionRevision(table, definition.Sha256, BundledTableDefinitions.Revision, false),
                TableRowArtifacts.SchemaSelectionKey(definition.Document, Opening.Build.Version));
            await using var artifacts = new TableRowArtifacts(Objects); await using var writer = await artifacts.CreateWriterAsync(binding, default);
            var copies = rows.Count(r => r.IsCopy); var physical = rows.Length - copies;
            var header = new Wdc5TableHeader(1, table.ToUpperInvariant(), layout, physical + (partial ? 3 : 0), copies, 4, columns);
            await writer.HeaderAsync(header, default);
            foreach (var row in rows) await writer.RowAsync(row, default);
            var issues = rows.Sum(r => r.TextIssues?.Count ?? 0);
            var artifact = await writer.CompleteAsync(partial ? new(physical + 3, physical, copies, 1, 3, 0, "unencryptedSections", bytes.Decoded.Length, issues)
                : new(physical, physical, copies, 0, 0, 0, "complete", bytes.Decoded.Length, issues), default);
            await Catalog.RecordTableArtifactAsync(artifact, default);
            await Catalog.CommitTableAdmissionAsync(Opening, table, "enUS", source, association, artifact, Time, default);
            return admitted[table] = new(artifact, table);
        }
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync()
        {
            foreach (var store in stores) await store.DisposeAsync();
            offline.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        private sealed class Offline : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("Reference text is local only.", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
