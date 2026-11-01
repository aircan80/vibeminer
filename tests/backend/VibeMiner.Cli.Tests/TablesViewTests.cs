using System.Globalization;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The <c>:tables</c> views against <see cref="InMemoryTables"/> (synthetic tables), through the navigator.</summary>
public sealed class TablesViewTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private static readonly BuildRef Build = new("wow", A, B, "eu");
    private const string Key = "ABCDEF0123456789";

    private readonly FakeStores stores = new();
    private readonly ManualTime time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly BrowseSession session;
    private readonly Navigator navigator;
    private readonly InMemoryTables fake = new();
    private readonly RecordingTables tables;
    private int leases, closed;

    public TablesViewTests()
    {
        stores.Source.Time = time;
        session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder);
        navigator = new Navigator(ViewRegistry.Default(), session, a => a());
        tables = new RecordingTables(fake);
        fake.Add(Build, "enUS", Seed());
    }

    public async ValueTask DisposeAsync() => await navigator.DisposeAsync().AsTask().WaitAsync(Guard);

    private void Wire() => session.OpenTables = (_, _) =>
    {
        Interlocked.Increment(ref leases);
        return Task.FromResult(new TablesLease(tables, () => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; }));
    };

    private static readonly Db2Column[] SpellColumns =
        [new("ID", "integer", true), new("Name", "string", false), new("OtherID", "integer", false), new("Flags[0]", "integer", false), new("Flags[1]", "integer", false)];

    private static TableSchema SpellSchema() => TableSchema.FromBlock(new DbdBlock(["00000000"], [],
    [
        Field("ID", "int", 32, false, isId: true),
        Field("Name", "string", null, null),
        Field("OtherID", "int", 32, true, reference: new("Other", "ID")),
        Field("Flags", "int", 8, true, array: 2),
    ], null), SchemaMatch.Build, WowLocale.EnUS);

    private static TableSchema OtherSchema() => TableSchema.FromBlock(new DbdBlock(["00000000"], [],
        [Field("ID", "int", 32, false, isId: true), Field("Label", "string", null, null)], null), SchemaMatch.Build, WowLocale.EnUS);

    private static DbdField Field(string name, string family, int? bits, bool? unsigned, bool isId = false, DbdReference? reference = null, int array = 1) =>
        new(new DbdColumn(name, family, true, null, reference), bits, unsigned, array, isId, false, false, null);

    /// <summary>
    /// Spell (100): 120 Ready rows, the last a copy and row 7 with a text issue. Other (200): 2 rows. 300: a DB2 with no
    /// definition. Locked (400): Encrypted, ID 5 under a key. Gone (500): not stored, a check says Missing.
    /// </summary>
    private static FakeTable[] Seed()
    {
        var spell = Enumerable.Range(1, 120).Select(i => i == 120
            ? new Db2Row(120, true, 1, ["120", "copy", "4294967295", "0", "1"])
            : new Db2Row((uint)i, false, null, [I(i), $"Spell {i}", i == 1 ? "4294967295" : "2", "3", "255"],
                i == 7 ? [new Db2TextIssue(1, "TEXT_INVALID_UTF8", "ff")] : null)).ToArray();
        return
        [
            new(100, "Spell", TableState.Ready, SpellColumns, spell, SpellSchema()),
            new(200, "Other", TableState.Ready, [new("ID", "integer", true), new("Label", "string", false)],
                [new(2, false, null, ["2", "two"]), new(3, false, null, ["3", "three"])], OtherSchema()),
            new(300, null, TableState.NoDefinition),
            new(400, "Locked", TableState.Encrypted, SpellColumns, ProtectedIds: new Dictionary<uint, string> { [5] = Key }),
            new(500, "Gone", TableState.Missing, Reason: new TableStateReason("OBJECT_MISSING", "The CDN answered 404.", null, null, null, [])),
        ];
    }

    private static string I(long value) => value.ToString(CultureInfo.InvariantCulture);

    private const string CdnsHeader = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0";
    private const string Netease = "cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\n";
    private const string EuCdn = "eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n";

    /// <summary>
    /// The catalog lists one current wow build in <paramref name="regions"/> (us and eu by default), and the cdns reply routes
    /// <paramref name="cdns"/> (eu only by default), so the build opens in eu.
    /// </summary>
    private async Task ServeBuildAsync(string[]? regions = null, string cdns = EuCdn)
    {
        const string header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
        var rows = string.Concat((regions ?? ["us", "eu"]).Select(r => $"{r}|{A}|{B}|{C}|58123|11.0.7.58123|{C}\n"));
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, Encoding.UTF8.GetBytes($"{header}\n## seqn = 1\n{rows}"));
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        stores.Source.AddReply("wow", "us", ObservationKind.Cdns, Encoding.UTF8.GetBytes($"{CdnsHeader}\n## seqn = 1\n{cdns}"));
        await stores.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), default);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TheBuildOpensInTheRegionTheRoutePickerChooses()
    {
        // The QA case on #180: cn sorts first and its cdns row lists only a host CdnRoute doesn't admit, so a table read there
        // had no CDN route (OBJECT_ACCESS_NO_CDN_CONTEXT). :tables takes the region f, d and a take (D280).
        await ServeBuildAsync(["cn", "eu", "us"], Netease + EuCdn);
        Wire();
        var top = await GoAsync(":tables");
        Assert.Equal([["wow", "11.0.7.58123", "eu", A]], Cells(top));
        var list = await DrillAsync(0);
        Assert.Equal(["100", "200", "300", "400", "500"], list.Visible.Select(r => r.Cells[0]));
    }

    private async Task<BrowseFrame> GoAsync(string name)
    {
        Assert.True(navigator.Go(name));
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private async Task<BrowseFrame> DrillAsync(int row)
    {
        Assert.True(navigator.Drill(row));
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private async Task BackAsync()
    {
        Assert.True(navigator.Back());
        await navigator.Loading.WaitAsync(Guard);
    }

    private static List<string[]> Cells(BrowseFrame frame) => frame.Visible.Select(r => r.Cells.ToArray()).ToList();

    private static int RowOf(BrowseFrame frame, string first) => frame.Visible.ToList().FindIndex(r => r.Cells[0] == first);

    private async Task<BrowseFrame> OpenListAsync()
    {
        await ServeBuildAsync();
        Wire();
        var top = await GoAsync(":tables");
        Assert.Equal([["wow", "11.0.7.58123", "eu", A]], Cells(top));
        return await DrillAsync(0);
    }

    [Fact]
    public async Task WithoutTheSeamTheViewsSayTablesArentAvailable()
    {
        await ServeBuildAsync();
        var top = await GoAsync(":tab");
        Assert.Equal(["Product", "Version", "Region", "Build config"], top.Content!.Columns);
        Assert.Equal("1 current builds. Enter lists a build's tables (enUS). " + TablesAccess.NotWired, top.Content.Note);
        var list = await DrillAsync(0);
        Assert.Equal("tables > wow 11.0.7.58123 enUS", navigator.Path);
        Assert.Empty(list.Content!.Rows);
        Assert.Equal(TablesAccess.NotWired, list.Content.Note);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task NoStoreAndNoBuildsSaySo()
    {
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        Assert.StartsWith("No object store at", (await GoAsync(":tables")).Content!.Note);
        stores.ReadOnlyFailure = null;
        Assert.Equal("No current builds in the history log. r in :builds fetches them. " + TablesAccess.NotWired, (await GoAsync(":tables")).Content!.Note);
    }

    [Fact]
    public async Task TheListHasOneRowPerTableWithStateCountsAndClosesWhatItOpened()
    {
        var list = await OpenListAsync();
        Assert.Equal(["FDID", "Name", "State", "Checked", "Rows", "Reason"], list.Content!.Columns);
        Assert.Equal(
        [
            ["100", "Spell", "NotChecked", "no", "-", ""],
            ["200", "Other", "NotChecked", "no", "-", ""],
            ["300", "-", "NoDefinition", "no", "-", ""],
            ["400", "Locked", "NotChecked", "no", "-", ""],
            ["500", "Gone", "NotStored", "no", "-", ""],
        ], Cells(list));
        Assert.Equal("5 tables: 3 NotChecked, 1 NotStored, 1 NoDefinition. Enter opens a table.", list.Content.Note);
        Assert.Equal((1, 1), (leases, closed));
        Assert.Equal(1, fake.Calls(nameof(ITables.ForAsync)));
        Assert.All(tables.Priorities, p => Assert.Equal(TablePriority.Interactive, p.Priority));

        navigator.SetFilter("nodefinition");
        Assert.Equal(["300"], list.Visible.Select(r => r.Cells[0]));
        navigator.SetFilter("SPELL");
        Assert.Equal(["100"], list.Visible.Select(r => r.Cells[0]));
        navigator.SetFilter("notstored");
        Assert.Equal(["500"], list.Visible.Select(r => r.Cells[0]));
    }

    [Fact]
    public async Task ACheckedTableListsItsCountsAndAnOpenFailureIsANote()
    {
        await using (var handle = (await fake.ForAsync(Build, "enUS", default)).Tables!) await handle.CheckAsync(100, TablePriority.Background, default);
        var list = await OpenListAsync();
        Assert.Equal(["100", "Spell", "Ready", "yes", "120/120", ""], Cells(list)[0]);
        Assert.StartsWith("5 tables: 2 NotChecked, 1 Ready, ", list.Content!.Note);

        fake.AddFailing(Build, FilesOpenState.Encrypted, "BUILD_ENCRYPTED");
        navigator.Reload();
        await navigator.Loading.WaitAsync(Guard);
        Assert.Empty(list.Content!.Rows);
        Assert.Equal("Can't open the build's files for enUS: Encrypted (BUILD_ENCRYPTED): Scripted open failure.", list.Content.Note);
        Assert.Equal(leases, closed);
    }

    [Fact]
    public async Task ATableShowsItsSchemaRowThenPagesOfFiftyRows()
    {
        await OpenListAsync();
        var table = await DrillAsync(0);
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell", navigator.Path);
        Assert.Equal(["ID", "Name", "OtherID", "Flags[0]", "Flags[1]"], table.Content!.Columns);
        var rows = Cells(table);
        Assert.Equal(52, rows.Count);
        Assert.Equal(["(schema)", "4 columns; Enter shows them", "", "", ""], rows[0]);
        Assert.Equal(["1", "Spell 1", "4294967295", "3", "255"], rows[1]);
        Assert.Equal(["50", "Spell 50", "2", "3", "255"], rows[50]);
        Assert.Equal(["(more)", "Enter shows rows 51-100", "", "", ""], rows[51]);
        Assert.Equal("Spell (100): Ready, 120/120 rows readable/declared; rows 1-50. Enter on (more) shows the next page.", table.Content.Note);

        var schema = await DrillAsync(0);
        Assert.Equal(["Column", "Kind", "Bits", "Array", "Key", "References"], schema.Content!.Columns);
        Assert.Equal(
        [
            ["ID", "Int", "32 signed", "", "id", ""],
            ["Name", "String", "", "", "", ""],
            ["OtherID", "Int", "32 unsigned", "", "", "Other.ID"],
            ["Flags", "Int", "8 unsigned", "[2]", "", ""],
        ], Cells(schema));
        await BackAsync();

        var second = await DrillAsync(51);
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell > rows 51-100", navigator.Path);
        Assert.Equal(["51", "Spell 51", "2", "3", "255"], Cells(second)[1]);
        Assert.Equal(["(more)", "Enter shows rows 101-120", "", "", ""], Cells(second)[^1]);
        var last = await DrillAsync(51);
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell > rows 51-100 > rows 101-120", navigator.Path);
        var lastRows = Cells(last);
        Assert.Equal(21, lastRows.Count);
        Assert.Equal(["120", "copy", "4294967295", "0", "1"], lastRows[^1]);
        Assert.Equal("Spell (100): Ready, 120/120 rows readable/declared; rows 101-120.", last.Content!.Note);
        Assert.All(tables.Priorities, p => Assert.Equal(TablePriority.Interactive, p.Priority));
        Assert.Equal(leases, closed);
    }

    [Fact]
    public async Task ATableThatCantBeReadShowsItsStateAndReason()
    {
        var list = await OpenListAsync();
        var gone = await DrillAsync(RowOf(list, "500"));
        Assert.Equal("Gone can't be read: Missing (OBJECT_MISSING).", gone.Content!.Note);
        Assert.Contains(["State", "Missing"], Cells(gone));
        Assert.Contains(["Checked", "yes"], Cells(gone));
        Assert.Contains(["Reason code", "OBJECT_MISSING"], Cells(gone));
        Assert.Contains(["Message", "The CDN answered 404."], Cells(gone));
        await BackAsync();

        var locked = await DrillAsync(RowOf(list, "400"));
        Assert.Equal("Locked can't be read: Encrypted.", locked.Content!.Note);
        Assert.Contains(["Keys needed", Key], Cells(locked));
        Assert.Contains(["Rows", "0/1"], Cells(locked));
        await BackAsync();

        var none = await DrillAsync(RowOf(list, "300"));
        Assert.Equal("300 can't be read: NoDefinition.", none.Content!.Note);
    }

    [Fact]
    public async Task ARowShowsItsValuesCopyAndTextIssuesAndItsForeignKeysDrill()
    {
        await OpenListAsync();
        await DrillAsync(0);
        var row = await DrillAsync(7);
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell > ID 7", navigator.Path);
        Assert.Equal(["Column", "Value", "References"], row.Content!.Columns);
        Assert.Equal(
        [
            ["(ID)", "7", ""], ["(ordinal)", "6", ""], ["(copy)", "no", ""],
            ["ID", "7", ""], ["Name", "Spell 7", ""], ["OtherID", "2", "Other.ID; Enter follows"], ["Flags[0]", "3", ""], ["Flags[1]", "255", ""],
            ["(text issue)", "Name: TEXT_INVALID_UTF8", "raw ff"],
            ["(referenced by)", "rows of other tables that point here", "Enter searches"],
        ], Cells(row));
        Assert.Equal("Spell row 7. 1 text issue(s).", row.Content.Note);
        Assert.Null(row.Visible[4].Drill);
        Assert.NotNull(row.Visible[5].Drill);
        Assert.Contains((nameof(IBuildTables.GetAsync), TablePriority.Interactive), tables.Priorities);

        await BackAsync();
        await DrillAsync(51);
        await DrillAsync(51);
        var copy = await DrillAsync(20);
        Assert.Contains(["(copy)", "copy of 1", ""], Cells(copy));
    }

    [Fact]
    public async Task ARowThatIsntFoundSaysHow()
    {
        Wire();
        var spell = new TableEntry(100, "Spell", TableState.Ready, true, null, null, null);
        var locked = new TableEntry(400, "Locked", TableState.Encrypted, true, null, null, null);
        var absent = await new TableRowView(Build, "enUS", spell, 999).LoadAsync(session, default);
        Assert.Equal("No row 999 in Spell; the table is complete, so it is absent.", absent.Note);
        Assert.Equal(["Outcome", "Absent", ""], absent.Rows[0].Cells);
        var protectedRow = await new TableRowView(Build, "enUS", locked, 5).LoadAsync(session, default);
        Assert.Equal($"Row 5 is in an encrypted section (key {Key}).", protectedRow.Note);
        Assert.Equal(["Key", Key, ""], protectedRow.Rows[1].Cells);
        var unknown = await new TableRowView(Build, "enUS", locked, 6).LoadAsync(session, default);
        Assert.StartsWith("Row 6 is unknown: not in the readable rows of Locked (Encrypted).", unknown.Note);
    }

    [Fact]
    public async Task FollowingAForeignKeyOpensTheTargetRows()
    {
        var other = new TableEntry(200, "Other", TableState.Ready, true, null, null, null);
        tables.Follow = (request, priority) =>
        {
            Assert.Equal((100u, 7u, "OtherID", -1L, TablePriority.Interactive), (request.Table.FileDataId, request.Id, request.Column, request.After, priority));
            return new(TableFollowOutcome.Found, Source(7), new("Other", "ID"), other, [new(0, new Db2Row(2, false, null, ["2", "two"]))], null);
        };
        await OpenListAsync();
        await DrillAsync(0);
        var row = await DrillAsync(7);
        var follow = await DrillAsync(RowOf(row, "OtherID"));
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell > ID 7 > OtherID -> Other.ID", navigator.Path);
        Assert.Equal(["ID", "Label"], follow.Content!.Columns);
        Assert.Equal([["2", "two"]], Cells(follow));
        Assert.Equal("1 row(s) of Other.ID match OtherID of row 7.", follow.Content.Note);
        var target = await DrillAsync(0);
        Assert.Equal("tables > wow 11.0.7.58123 enUS > Spell > ID 7 > OtherID -> Other.ID > ID 2", navigator.Path);
        Assert.Contains(["Label", "two", ""], Cells(target));
        Assert.Equal(leases, closed);
    }

    private static TableRowResult Source(uint id) =>
        new(new TableEntry(100, "Spell", TableState.Ready, true, null, null, null), TableRowOutcome.Found, new(id - 1, new Db2Row(id, false, null, [])), null);

    public static TheoryData<TableFollowOutcome, string> Outcomes => new()
    {
        { TableFollowOutcome.Absent, "No row of Other.ID matches; the target is complete, so the reference points nowhere." },
        { TableFollowOutcome.Unknown, "No match in the readable rows of Other.ID (Partial); that isn't proof it's absent." },
        { TableFollowOutcome.KeyOutOfRange, "The value doesn't fit Other.ID's declared width and sign, so nothing can match." },
        { TableFollowOutcome.NotAReference, "OtherID declares no foreign key in the resolved schema." },
        { TableFollowOutcome.SourceRow, "Row 7 itself wasn't found (Unknown)." },
        { TableFollowOutcome.Unindexed, "Other.ID isn't keyed by that column and has too many rows to scan." },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task AFollowWithoutMatchesShowsItsOutcome(TableFollowOutcome outcome, string note)
    {
        Wire();
        var target = new TableEntry(200, "Other", TableState.Partial, true, new TableStateReason("DB2_KEYED", null, null, null, null, [Key]), null, null);
        var source = outcome == TableFollowOutcome.SourceRow
            ? new TableRowResult(new TableEntry(100, "Spell", TableState.Partial, true, null, null, null), TableRowOutcome.Unknown, null, null) : Source(7);
        tables.Follow = (_, _) => new(outcome, source, outcome == TableFollowOutcome.NotAReference ? null : new("Other", "ID"),
            outcome == TableFollowOutcome.Unknown ? target : null, [], null);
        var view = new TableFollowView(Build, "enUS", new TableEntry(100, "Spell", TableState.Ready, true, null, null, null), 7, "OtherID", new("Other", "ID"));
        var content = await view.LoadAsync(session, default);
        Assert.Equal(note, content.Note);
        Assert.Equal(["Outcome", outcome.ToString()], content.Rows[0].Cells);
        if (outcome == TableFollowOutcome.SourceRow) Assert.Contains(content.Rows, r => r.Cells.SequenceEqual(["Source row", "Unknown"]));
        if (outcome == TableFollowOutcome.Unknown)
        {
            Assert.Contains(content.Rows, r => r.Cells.SequenceEqual(["Target state", "Partial"]));
            Assert.Contains(content.Rows, r => r.Cells.SequenceEqual(["Target reason", "DB2_KEYED"]));
        }
        Assert.Equal(leases, closed);
    }

    [Fact]
    public async Task ReferencedByListsMatchingRowsAtBackgroundPriority()
    {
        var other = new TableEntry(200, "Other", TableState.Ready, true, null, null, null);
        var locked = new TableEntry(400, "Locked", TableState.Encrypted, true, null, null, null);
        tables.ReferencedBy = (request, priority) =>
        {
            Assert.Equal((100u, 7u, TablePriority.Background), (request.Table.FileDataId, request.Id, priority));
            return new(TableReferencesOutcome.Searched, Source(7),
            [
                new(other, "SpellID", new("Spell", "ID"), TableReferenceScope.Complete, [2, 3], false),
                new(locked, "SpellID", new("Spell", "ID"), TableReferenceScope.NotChecked, [], false),
            ], []);
        };
        await OpenListAsync();
        await DrillAsync(0);
        var row = await DrillAsync(7);
        var refs = await DrillAsync(RowOf(row, "(referenced by)"));
        Assert.Equal(
        [
            ["Other (200)", "SpellID", "Complete", "2"],
            ["Other (200)", "SpellID", "Complete", "3"],
            ["Locked (400)", "SpellID", "NotChecked (Encrypted)", "-"],
        ], Cells(refs));
        Assert.Equal("2 row(s) from 2 source column(s). Partial or unchecked sources: the list is a lower bound.", refs.Content!.Note);
        Assert.Null(refs.Visible[2].Drill);
        var target = await DrillAsync(1);
        Assert.Contains(["Label", "three", ""], Cells(target));
    }

    private static readonly TableEntry SpellEntry = new(100, "Spell", TableState.Ready, true, null, null, null);
    private static readonly BuildRef Other = new("wow", C, B, "eu");

    [Fact]
    public async Task APageThatIsntOkSaysWhyAndKeepsTheSchemaRow()
    {
        Wire();
        var stale = await new TableRowsView(Build, "enUS", SpellEntry, 49, "not the identity").LoadAsync(session, default);
        Assert.Equal("The table changed since the first page (Stale). Esc back to the first page and reopen it.", stale.Note);
        Assert.Equal("(schema)", Assert.Single(stale.Rows).Cells[0]);
        var past = await new TableRowsView(Build, "enUS", SpellEntry, 120).LoadAsync(session, default);
        Assert.Equal("No rows past this point (InvalidCursor).", past.Note);
        Assert.Single(past.Rows);
        tables.Page = request => TableQueries.NotReadable(new TableEntry(100, "Spell", TableState.Corrupt, true, null, null, null));
        var gone = await new TableRowsView(Build, "enUS", SpellEntry).LoadAsync(session, default);
        Assert.Equal("No rows: NotReadable.", gone.Note);
        Assert.Single(gone.Rows);
        Assert.Equal(leases, closed);
    }

    [Fact]
    public async Task TheListStopsAtItsCap()
    {
        Wire();
        fake.Add(Other, "enUS", Enumerable.Range(1, TableListView.MaxListed + 1).Select(i => new FakeTable((uint)i, null, TableState.NoDefinition)));
        var list = await new TableListView(Other, "wow big", "enUS").LoadAsync(session, default);
        Assert.Equal(TableListView.MaxListed, list.Rows.Count);
        Assert.Equal("20,000 tables: 20,000 NoDefinition. Only the first 20,000 are listed. Enter opens a table.", list.Note);
        Assert.Equal(leases, closed);
    }

    // D291: an absent external parent is "" with DB2_RELATION_ABSENT; the grids and the row view show it as (no parent), and 0 stays 0.
    [Fact]
    public async Task AnAbsentParentShowsAsNoParentInThePageFollowAndRowViews()
    {
        Wire();
        var schema = TableSchema.FromBlock(new DbdBlock(["00000000"], [],
            [Field("ID", "int", 32, false, isId: true), Field("ParentID", "int", 32, true, reference: new("Tree", "ID"))], null), SchemaMatch.Build, WowLocale.EnUS);
        var absent = new Db2TextIssue(1, Db2Reader.RelationAbsent, "");
        var tree = new FakeTable(700, "Tree", TableState.Ready, [new("ID", "integer", true), new("ParentID", "integer", false)],
            [new(1, false, null, ["1", ""], [absent]), new(2, false, null, ["2", "1"]), new(3, false, null, ["3", "0"])], schema);
        fake.Add(Other, "enUS", [tree]);
        var entry = new TableEntry(700, "Tree", TableState.Ready, true, null, null, null);
        var page = await new TableRowsView(Other, "enUS", entry).LoadAsync(session, default);
        Assert.Equal([["1", TablesAccess.NoParent], ["2", "1"], ["3", "0"]], page.Rows.Skip(1).Select(r => r.Cells.ToArray()));
        var follow = await new TableFollowView(Other, "enUS", entry, 2, "ParentID", new("Tree", "ID")).LoadAsync(session, default);
        Assert.Equal([["1", TablesAccess.NoParent]], follow.Rows.Select(r => r.Cells.ToArray()));
        var row = await new TableRowView(Other, "enUS", entry, 1).LoadAsync(session, default);
        Assert.Contains(row.Rows, r => r.Cells.SequenceEqual(["ParentID", TablesAccess.NoParent, "Tree.ID; Enter follows"]));
        Assert.Contains(row.Rows, r => r.Cells.SequenceEqual(["(text issue)", "ParentID: DB2_RELATION_ABSENT", "no parent (not 0)"]));
        Assert.Equal(leases, closed);
    }

    [Fact]
    public async Task APartialTableSaysSoAndItsMissingRowsAreUnknownNotAbsent()
    {
        Wire();
        var half = new FakeTable(600, "Half", TableState.Partial, [new("ID", "integer", true), new("Label", "string", false)],
            [new(1, false, null, ["1", "one"])], OtherSchema(), ProtectedIds: new Dictionary<uint, string> { [9] = Key });
        fake.Add(Other, "enUS", [half]);
        var entry = new TableEntry(600, "Half", TableState.Partial, true, null, null, null);
        var page = await new TableRowsView(Other, "enUS", entry).LoadAsync(session, default);
        Assert.Equal("Half (600): Partial, 1/2 rows readable/declared; rows 1-1. Partial: keyed sections are left out.", page.Note);
        var unknown = await new TableRowView(Other, "enUS", entry, 99).LoadAsync(session, default);
        Assert.Equal(["Outcome", "Unknown", ""], unknown.Rows[0].Cells);
        Assert.Equal("Row 99 is unknown: not in the readable rows of Half (Partial). That isn't proof it's absent.", unknown.Note);
        var keyed = await new TableRowView(Other, "enUS", entry, 9).LoadAsync(session, default);
        Assert.Equal(["Outcome", "Protected", ""], keyed.Rows[0].Cells);
    }

    [Fact]
    public async Task ReferencedBySaysWhenTheRowIsMissingNothingPointsOrSourcesWereCut()
    {
        Wire();
        var view = new TableReferencedByView(Build, "enUS", SpellEntry, 7);
        tables.ReferencedBy = (_, _) => new(TableReferencesOutcome.SourceRow, new TableRowResult(SpellEntry, TableRowOutcome.Absent, null, null), [], []);
        var missing = await view.LoadAsync(session, default);
        Assert.Empty(missing.Rows);
        Assert.Equal("Row 7 itself wasn't found (Absent), so nothing was searched.", missing.Note);

        var other = new TableEntry(200, "Other", TableState.Ready, true, null, null, null);
        tables.ReferencedBy = (_, _) => new(TableReferencesOutcome.NoReferences, Source(7),
            [new(other, "SpellID", new("Spell", "ID"), TableReferenceScope.Complete, [], false)], []);
        var none = await view.LoadAsync(session, default);
        Assert.Equal([["Other (200)", "SpellID", "Complete", "-"]], none.Rows.Select(r => r.Cells.ToArray()));
        Assert.Equal("Nothing points at row 7: every source was searched completely.", none.Note);

        tables.ReferencedBy = (_, _) => new(TableReferencesOutcome.Searched, Source(7),
            [new(other, "SpellID", new("Spell", "ID"), TableReferenceScope.Complete, [.. Enumerable.Range(1, TableQueries.DefaultLimit).Select(i => (uint)i)], true)], []);
        var cut = await view.LoadAsync(session, default);
        Assert.Equal(TableQueries.DefaultLimit, cut.Rows.Count);
        Assert.Equal("50 row(s) from 1 source column(s). 1 source(s) had more than 50 matches; only those are shown.", cut.Note);

        var unread = new TableEntry(300, "Unread", TableState.NotStored, true, null, null, null);
        tables.ReferencedBy = (_, _) => new(TableReferencesOutcome.Searched, Source(7),
            [new(other, "SpellID", new("Spell", "ID"), TableReferenceScope.Complete, [], false)], [unread]);
        var unresolved = await view.LoadAsync(session, default);
        Assert.Equal([["Other (200)", "SpellID", "Complete", "-"], ["Unread (300)", "-", "NotResolved (NotStored)", "-"]],
            unresolved.Rows.Select(r => r.Cells.ToArray()));
        Assert.Null(unresolved.Rows[1].Drill);
        Assert.Equal("0 row(s) from 1 source column(s). 1 table(s) couldn't be searched without opening them,"
            + " so they may point here too.", unresolved.Note);
        Assert.Equal(leases, closed);
    }

    /// <summary>
    /// Passes calls to the fake and records each handle call's priority; follow and referenced-by, which the fake doesn't model
    /// yet, answer from scripts.
    /// </summary>
    private sealed class RecordingTables(ITables inner) : ITables
    {
        private readonly Lock gate = new();
        private readonly List<(string, TablePriority)> priorities = [];

        public Func<TableFollowRequest, TablePriority, TableFollowResult>? Follow { get; set; }
        public Func<TablePageRequest, TablePage>? Page { get; set; }
        public Func<TableReferencesRequest, TablePriority, TableReferencesResult>? ReferencedBy { get; set; }

        public IReadOnlyList<(string Method, TablePriority Priority)> Priorities { get { lock (gate) return [.. priorities]; } }

        private void Record(string method, TablePriority priority) { lock (gate) priorities.Add((method, priority)); }

        public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var opened = await inner.ForAsync(build, locale, cancellationToken);
            return opened.Tables is { } handle ? opened with { Tables = new Handle(this, handle) } : opened;
        }

        private sealed class Handle(RecordingTables owner, IBuildTables inner) : IBuildTables
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();

            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(ListAsync), priority);
                return inner.ListAsync(filter, priority, cancellationToken);
            }

            public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(CheckAsync), priority);
                return inner.CheckAsync(table, priority, cancellationToken);
            }

            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(SchemaAsync), priority);
                return inner.SchemaAsync(table, priority, cancellationToken);
            }

            public Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(PageAsync), priority);
                return owner.Page is { } page ? Task.FromResult(page(request)) : inner.PageAsync(request, priority, cancellationToken);
            }

            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(GetAsync), priority);
                return inner.GetAsync(table, id, priority, cancellationToken);
            }

            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(FollowAsync), priority);
                return owner.Follow is { } follow ? Task.FromResult(follow(request, priority)) : inner.FollowAsync(request, priority, cancellationToken);
            }

            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken)
            {
                owner.Record(nameof(ReferencedByAsync), priority);
                return owner.ReferencedBy is { } refs ? Task.FromResult(refs(request, priority)) : inner.ReferencedByAsync(request, priority, cancellationToken);
            }

            public Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken)
            {
                owner.Record(nameof(PreloadAsync), TablePriority.Background);
                return inner.PreloadAsync(progress, cancellationToken);
            }
        }
    }
}
