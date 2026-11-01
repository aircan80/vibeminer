using VibeMiner.Builds;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// One behaviour suite for <see cref="ITables"/>: the in-memory fake layers 6 and up test against, and later the real service.
/// Each side builds the same neutral <see cref="TableScenario"/> its own way. Synthetic data only.
/// </summary>
public abstract class TablesContract
{
    private static CancellationToken Ct => CancellationToken.None;
    private const TablePriority Now = TablePriority.Interactive;

    /// <summary>A tables service holding exactly this scenario's build and locale.</summary>
    protected abstract Task<ITables> CreateAsync(TableScenario scenario);

    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32));
    public const string Key = "0123456789ABCDEF";

    private static ScenarioRow Row(uint id, params string[] values) => new(id, [id.ToString(System.Globalization.CultureInfo.InvariantCulture), .. values]);

    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];

    /// <summary>
    /// 100 Alpha: 7 rows, a string, a 2-element array and a foreign key to BetaItem. 200 BetaItem: not stored. 300 Gamma: the
    /// integer extremes. 400: a DB2 the manifest doesn't know. 500 Delta: not in the build. 600 Epsilon: partial (rows 1 and 2, keyed
    /// 10 and 11). 700 Zeta: every section keyed. 800 Eta: already generated. 900 Empty: no rows. Given out of order.
    /// </summary>
    public static TableScenario Scenario() => new(Build, "enUS",
    [
        new(300, "Gamma", [ScenarioColumn.Id(), ScenarioColumn.Int("U32", 32, true), ScenarioColumn.Int("I64", 64), ScenarioColumn.Int("U64", 64, true)],
            [Row(1, "4294967295", "-9223372036854775808", "18446744073709551615"), Row(2, "0", "9223372036854775807", "0")]),
        new(100, "Alpha", [ScenarioColumn.Id(), ScenarioColumn.Text("Label"), ScenarioColumn.Int("Flags", arrayLength: 2),
                ScenarioColumn.Int("BetaID", foreignKey: new ForeignKey("BetaItem", "ID"))],
            [.. Enumerable.Range(1, 7).Select(i => Row((uint)(i * 10), $"row {i}", $"{i}", "-1", "0"))]),
        new(200, "BetaItem", Plain, [Row(1, "5"), Row(2, "6")], Stored: false),
        new(400, null, Plain, [Row(1, "1")]),
        new(500, "Delta", Plain, [], InBuild: false),
        new(600, "Epsilon", Plain, [Row(1, "7"), Row(2, "8")], new ScenarioKeyedSection(Key, [10, 11])),
        new(700, "Zeta", Plain, [], new ScenarioKeyedSection(Key, [20])),
        new(800, "Eta", Plain, [Row(1, "9")], Generated: true),
        new(900, "Empty", Plain, []),
    ]);

    private async Task<IBuildTables> OpenAsync()
    {
        var result = await (await CreateAsync(Scenario())).ForAsync(Build, "enUS", Ct);
        Assert.Equal(Files.FilesOpenState.Open, result.State);
        return result.Tables!;
    }

    private static async Task<List<TableEntry>> ListAsync(IBuildTables tables, TableListFilter filter, CancellationToken? ct = null)
    {
        var list = new List<TableEntry>();
        await foreach (var entry in tables.ListAsync(filter, Now, ct ?? Ct)) list.Add(entry);
        return list;
    }

    private static async Task<List<TableRow>> AllPagesAsync(IBuildTables tables, TableRef table, int limit)
    {
        var rows = new List<TableRow>();
        long after = -1;
        string? identity = null;
        while (true)
        {
            var page = await tables.PageAsync(new(table, after, limit, Identity: identity), Now, Ct);
            Assert.Equal(TablePageState.Ok, page.State);
            Assert.True(page.Rows.Count <= limit);
            if (identity is not null) Assert.Equal(identity, page.Identity);
            identity = page.Identity;
            rows.AddRange(page.Rows);
            if (page.NextAfter is not { } next) return rows;
            Assert.Equal(page.Rows[^1].Ordinal, next);
            after = next;
        }
    }

    // ForAsync

    [Fact]
    public async Task OpensWithBuildAndLocale()
    {
        await using var tables = await OpenAsync();
        Assert.Equal((Build, "enUS"), (tables.Build, tables.Locale));
    }

    [Fact]
    public async Task UnknownBuildDoesNotOpen()
    {
        var result = await (await CreateAsync(Scenario())).ForAsync(Other, "enUS", Ct);
        Assert.NotEqual(Files.FilesOpenState.Open, result.State);
        Assert.Null(result.Tables);
    }

    [Fact]
    public async Task UnregisteredLocaleThrows()
    {
        var service = await CreateAsync(Scenario());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ForAsync(Build, "enus", Ct));
    }

    // ListAsync

    [Fact]
    public async Task ListsAscendingInShallowStates()
    {
        await using var tables = await OpenAsync();
        var list = await ListAsync(tables, TableListFilter.All);
        Assert.Equal([100u, 200, 300, 400, 500, 600, 700, 800, 900], list.Select(e => e.FileDataId));
        Assert.Equal(
            [TableState.NotChecked, TableState.NotStored, TableState.NotChecked, TableState.NoDefinition, TableState.NotInBuild,
             TableState.NotChecked, TableState.NotChecked, TableState.Ready, TableState.NotChecked],
            list.Select(e => e.State));
        Assert.All(list.Where(e => e.State == TableState.Ready), e => Assert.True(e.Checked));
        Assert.All(list.Where(e => e.FileDataId != 800), e => Assert.False(e.Checked));
        Assert.Null(list.Single(e => e.FileDataId == 400).Name);
        Assert.Equal("Alpha", list[0].Name);
        Assert.All(list.Where(e => !e.Checked), e => Assert.Null(e.Counts));
    }

    [Fact]
    public async Task NameFilterIsLiteralAndIgnoresAsciiCase()
    {
        await using var tables = await OpenAsync();
        async Task<uint[]> Ids(string name) => [.. (await ListAsync(tables, new() { Name = name })).Select(e => e.FileDataId)];
        Assert.Equal(new uint[] { 100 }, await Ids("ALP"));
        Assert.Equal(new uint[] { 200 }, await Ids("taIT"));
        Assert.Empty(await Ids("A.p"));
        Assert.Empty(await Ids("*"));
        Assert.Empty(await Ids("Alpha "));
        Assert.Equal(new uint[] { 100, 200, 300, 500, 600, 700, 800, 900 }, await Ids(""));
    }

    [Fact]
    public async Task StateFilter()
    {
        await using var tables = await OpenAsync();
        var list = await ListAsync(tables, new() { States = new HashSet<TableState> { TableState.NotStored, TableState.NotInBuild } });
        Assert.Equal([200u, 500], list.Select(e => e.FileDataId));
    }

    [Fact]
    public async Task CheckedTableListsInItsCheckedState()
    {
        await using var tables = await OpenAsync();
        await tables.CheckAsync(100, Now, Ct);
        var alpha = (await ListAsync(tables, new() { Name = "Alpha" })).Single();
        Assert.Equal((TableState.Ready, true), (alpha.State, alpha.Checked));
        Assert.NotNull(alpha.Counts);
    }

    // CheckAsync

    [Fact]
    public async Task CheckReadyCounts()
    {
        await using var tables = await OpenAsync();
        var entry = await tables.CheckAsync(100, Now, Ct);
        Assert.Equal((100u, "Alpha", TableState.Ready, true), (entry.FileDataId, entry.Name, entry.State, entry.Checked));
        Assert.Equal((7L, 7L, 0, 0L), (entry.Counts!.DeclaredRows, entry.Counts.ReadableRows, entry.Counts.ExcludedSections, entry.Counts.ExcludedRecords));
        Assert.Matches("^[0-9a-f]{32}$", entry.ContentKey);
    }

    [Fact]
    public async Task CheckByNameIgnoresAsciiCase()
    {
        await using var tables = await OpenAsync();
        var entry = await tables.CheckAsync(TableRef.ForName("bETAiTEM"), Now, Ct);
        Assert.Equal((200u, "BetaItem"), (entry.FileDataId, entry.Name));
    }

    [Fact]
    public async Task CheckFetchesNotStored()
    {
        await using var tables = await OpenAsync();
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(200, Now, Ct)).State);
    }

    [Fact]
    public async Task CheckUnknownTables()
    {
        await using var tables = await OpenAsync();
        var byName = await tables.CheckAsync(TableRef.ForName("NoSuchTable"), Now, Ct);
        Assert.Equal((0u, TableState.NoDefinition), (byName.FileDataId, byName.State));
        var byId = await tables.CheckAsync(12345, Now, Ct);
        Assert.Equal((12345u, TableState.NotInBuild), (byId.FileDataId, byId.State));
        Assert.Equal(TableState.NotInBuild, (await tables.CheckAsync(500, Now, Ct)).State);
        Assert.Equal(TableState.NoDefinition, (await tables.CheckAsync(400, Now, Ct)).State);
    }

    [Fact]
    public async Task CheckPartialAndEncrypted()
    {
        await using var tables = await OpenAsync();
        var partial = await tables.CheckAsync(600, Now, Ct);
        Assert.Equal(TableState.Partial, partial.State);
        Assert.Equal([Key], partial.Reason!.KeyNames);
        Assert.Equal(2, partial.Counts!.ReadableRows);
        Assert.True(partial.Counts.DeclaredRows > partial.Counts.ReadableRows);
        Assert.Equal((1, 2L), (partial.Counts.ExcludedSections, partial.Counts.ExcludedRecords));
        var encrypted = await tables.CheckAsync(700, Now, Ct);
        Assert.Equal(TableState.Encrypted, encrypted.State);
        Assert.Equal([Key], encrypted.Reason!.KeyNames);
    }

    // SchemaAsync

    [Fact]
    public async Task SchemaGivesFlattenedColumnsAndForeignKeys()
    {
        await using var tables = await OpenAsync();
        var schema = await tables.SchemaAsync(TableRef.ForName("alpha"), Now, Ct);
        Assert.Equal(TableState.Ready, schema.Table.State);
        Assert.Equal(["ID", "Label", "Flags[0]", "Flags[1]", "BetaID"], schema.Columns!.Select(c => c.Name));
        Assert.True(schema.Columns![0].IsId);
        Assert.Equal(new ForeignKey("BetaItem", "ID"), schema.Schema!.Columns.Single(c => c.Name == "BetaID").ForeignKey);
    }

    [Fact]
    public async Task SchemaOfUndefinedAndAbsentTables()
    {
        await using var tables = await OpenAsync();
        var undefined = await tables.SchemaAsync(400, Now, Ct);
        Assert.Equal((TableState.NoDefinition, null, null), (undefined.Table.State, undefined.Columns, undefined.Schema));
        var absent = await tables.SchemaAsync(500, Now, Ct);
        Assert.Equal((TableState.NotInBuild, null), (absent.Table.State, absent.Columns));
    }

    /// <summary>A deDE build with 100 Names: a 3-element locstring array between two ints (D320).</summary>
    public static TableScenario LocStringScenario() => new(Build, "deDE",
    [
        new(100, "Names", [ScenarioColumn.Id(), ScenarioColumn.Int("Rank"), ScenarioColumn.LocText("Name_lang", 3), ScenarioColumn.Int("Flags")],
            [Row(1, "4", "Erste", "", "Dritte", "5"), Row(2, "-1", "a", "b", "c", "6")]),
    ]);

    [Fact]
    public async Task LocStringArrayReadsEachElementInTheHandleLocale()
    {
        await using var tables = (await (await CreateAsync(LocStringScenario())).ForAsync(Build, "deDE", Ct)).Tables!;
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(100, Now, Ct)).State);
        var schema = await tables.SchemaAsync(100, Now, Ct);
        Assert.Equal(["ID", "Rank", "Name_lang[0]", "Name_lang[1]", "Name_lang[2]", "Flags"], schema.Columns!.Select(c => c.Name));
        var name = schema.Schema!.Columns.Single(c => c.Name == "Name_lang");
        Assert.Equal((ColumnKind.LocString, 3, WowLocale.DeDE), (name.Kind, name.ArrayLength, name.Locale));
        Assert.Equal(WowLocale.DeDE, schema.Schema.Locale);
        var rows = await AllPagesAsync(tables, 100, 10);
        Assert.Equal([["1", "4", "Erste", "", "Dritte", "5"], ["2", "-1", "a", "b", "c", "6"]], rows.Select(r => r.Row.Values));
    }

    // PageAsync

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(TableQueries.MaxLimit)]
    public async Task PagesContinueExactly(int limit)
    {
        await using var tables = await OpenAsync();
        var rows = await AllPagesAsync(tables, 100, limit);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => (long)i), rows.Select(r => r.Ordinal));
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (uint)(i * 10)), rows.Select(r => r.Row.Id));
        Assert.Equal(["70", "row 7", "7", "-1", "0"], rows[^1].Row.Values);
    }

    [Fact]
    public async Task PageCursorBounds()
    {
        await using var tables = await OpenAsync();
        var first = await tables.PageAsync(new(100, Limit: 3), Now, Ct);
        Assert.Equal((TablePageState.Ok, 2L, 3L), (first.State, first.NextAfter, first.Scanned));
        Assert.NotNull(first.Identity);
        var last = await tables.PageAsync(new(100, After: 5, Limit: 3), Now, Ct);
        Assert.Equal((TablePageState.Ok, null), (last.State, last.NextAfter));
        Assert.Equal([6L], last.Rows.Select(r => r.Ordinal));
        var atEnd = await tables.PageAsync(new(100, After: 6), Now, Ct);
        Assert.Equal((TablePageState.Ok, 0, null), (atEnd.State, atEnd.Rows.Count, atEnd.NextAfter));
        var beyond = await tables.PageAsync(new(100, After: 7), Now, Ct);
        Assert.Equal((TablePageState.InvalidCursor, 0), (beyond.State, beyond.Rows.Count));
    }

    [Fact]
    public async Task EmptyTablePages()
    {
        await using var tables = await OpenAsync();
        var page = await tables.PageAsync(new(900), Now, Ct);
        Assert.Equal((TablePageState.Ok, 0, null), (page.State, page.Rows.Count, page.NextAfter));
        Assert.Equal(TablePageState.InvalidCursor, (await tables.PageAsync(new(900, After: 0), Now, Ct)).State);
    }

    [Fact]
    public async Task StaleIdentityIsNeverRebased()
    {
        await using var tables = await OpenAsync();
        var first = await tables.PageAsync(new(100, Limit: 3), Now, Ct);
        var stale = await tables.PageAsync(new(100, first.NextAfter!.Value, 3, Identity: first.Identity + "x"), Now, Ct);
        Assert.Equal((TablePageState.Stale, 0, null), (stale.State, stale.Rows.Count, stale.NextAfter));
        var other = await tables.PageAsync(new(800), Now, Ct);
        Assert.NotEqual(first.Identity, other.Identity);
        Assert.Equal(TablePageState.Stale, (await tables.PageAsync(new(100, Identity: other.Identity), Now, Ct)).State);
    }

    [Fact]
    public async Task PartialTablePagesReadableRows()
    {
        await using var tables = await OpenAsync();
        var page = await tables.PageAsync(new(600), Now, Ct);
        Assert.Equal((TablePageState.Ok, TableState.Partial), (page.State, page.Table.State));
        Assert.Equal([1u, 2], page.Rows.Select(r => r.Row.Id));
    }

    [Theory]
    [InlineData(400u, TableState.NoDefinition)]
    [InlineData(500u, TableState.NotInBuild)]
    [InlineData(700u, TableState.Encrypted)]
    public async Task UnreadableTablesDontPage(uint id, TableState state)
    {
        await using var tables = await OpenAsync();
        var page = await tables.PageAsync(new(id), Now, Ct);
        Assert.Equal((TablePageState.NotReadable, state, 0, null, null), (page.State, page.Table.State, page.Rows.Count, page.NextAfter, page.Identity));
    }

    [Fact]
    public async Task IntegersStayExact()
    {
        await using var tables = await OpenAsync();
        var page = await tables.PageAsync(new(TableRef.ForName("Gamma")), Now, Ct);
        Assert.Equal(["1", "4294967295", "-9223372036854775808", "18446744073709551615"], page.Rows[0].Row.Values);
        Assert.Equal(["2", "0", "9223372036854775807", "0"], page.Rows[1].Row.Values);
        var row = await tables.GetAsync(300, 1, Now, Ct);
        Assert.Equal(page.Rows[0].Row.Values, row.Row!.Row.Values);
    }

    // Filters

    /// <summary>
    /// For filters, scanning at most 10 rows a page. 300 Gamma: integer extremes and an 8-bit column. 100 Items: a string with a
    /// bad-text cell, a 2-element int array, a float, and an ID above int32. 200 Many: 25 rows, Value = ID % 5. No float is NaN:
    /// the DB2 reader refuses non-finite floats, so no real table has one (NaN never matching is TableQueriesTests').
    /// </summary>
    public static TableScenario FilterScenario() => new(Build, "enUS",
    [
        new(300, "Gamma", [ScenarioColumn.Id(), ScenarioColumn.Int("U8", 8, true), ScenarioColumn.Int("U32", 32, true), ScenarioColumn.Int("I64", 64),
                ScenarioColumn.Int("U64", 64, true)],
            [Row(1, "255", "4294967295", "-9223372036854775808", "18446744073709551615"),
             Row(2, "0", "0", "9223372036854775807", "0"),
             Row(3, "1", "4294967294", "-9223372036854775807", "18446744073709551614")]),
        new(100, "Items", [ScenarioColumn.Id(), ScenarioColumn.Text("Label"), ScenarioColumn.Int("Flags", arrayLength: 2), ScenarioColumn.Float("Weight")],
            [Row(1, "Sword", "1", "3", "1.5"),
             Row(2, "sword", "3", "0", "2.25"),
             Row(3, "Shield", "0", "0", "7"),
             Row(4, "", "2", "2", "-0.5") with { BadText = [1] },
             Row(5, "", "0", "3", "3.4028235E+38"),
             Row(3000000000, "Axe", "0", "0", "-7")]),
        new(200, "Many", Plain, [.. Enumerable.Range(1, 25).Select(i => Row((uint)i, $"{i % 5}"))]),
    ], MaxScanRows: 10);

    private async Task<IBuildTables> OpenFiltersAsync()
    {
        var result = await (await CreateAsync(FilterScenario())).ForAsync(Build, "enUS", Ct);
        Assert.Equal(Files.FilesOpenState.Open, result.State);
        return result.Tables!;
    }

    private static TableFilter Is(string column, string value) => new(column, TableFilterOp.Equals, value);
    private static TableFilter Range(string column, string low, string high) => new(column, TableFilterOp.Between, low, high);

    /// <summary>Every page of a filtered table: the matching IDs in order, and each page.</summary>
    private static async Task<(List<uint> Ids, List<TablePage> Pages)> FilteredAsync(IBuildTables tables, TableRef table, int limit,
        params TableFilter[] filters)
    {
        var ids = new List<uint>();
        var pages = new List<TablePage>();
        long after = -1;
        string? identity = null;
        while (true)
        {
            var page = await tables.PageAsync(new(table, after, limit, filters, identity), Now, Ct);
            Assert.Equal(TablePageState.Ok, page.State);
            Assert.InRange(page.Rows.Count, 0, limit);
            Assert.InRange(page.Scanned, page.Rows.Count, 10);
            Assert.All(page.Rows, r => Assert.True(r.Ordinal > after));
            pages.Add(page);
            identity = page.Identity;
            ids.AddRange(page.Rows.Select(r => r.Row.Id));
            if (page.NextAfter is not { } next) return (ids, pages);
            Assert.Equal(after + page.Scanned, next);
            after = next;
        }
    }

    private static async Task<uint[]> IdsAsync(IBuildTables tables, TableRef table, params TableFilter[] filters) =>
        [.. (await FilteredAsync(tables, table, TableQueries.MaxLimit, filters)).Ids];

    private static async Task AssertBadAsync(IBuildTables tables, TableRef table, string code, params TableFilter[] filters)
    {
        var page = await tables.PageAsync(new(table, Filters: filters), Now, Ct);
        Assert.Equal((TablePageState.BadFilter, code, 0, null), (page.State, page.Code, page.Rows.Count, page.NextAfter));
        Assert.False(string.IsNullOrEmpty(page.Message));
    }

    [Fact]
    public async Task IntegerEqualsIsExact()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 300, Is("U32", "4294967295")));
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 300, Is("I64", "-9223372036854775808")));
        // Both round to the same double; only exact comparison tells them apart.
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 300, Is("U64", "18446744073709551615")));
        Assert.Equal(new uint[] { 3 }, await IdsAsync(tables, 300, Is("U64", "18446744073709551614")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 300, Is("U64", "0")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 300, Is("ID", "2")));
    }

    [Fact]
    public async Task IdsAreUnsigned32Bit()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 3000000000 }, await IdsAsync(tables, 100, Is("ID", "3000000000")));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 3000000000 }, await IdsAsync(tables, 100, Range("ID", "0", "4294967295")));
        Assert.Empty(await IdsAsync(tables, 100, Is("ID", "-1294967296")));
        Assert.Empty(await IdsAsync(tables, 100, Is("ID", "4294967296")));
    }

    [Fact]
    public async Task IntegerRangeIsInclusive()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 1, 3 }, await IdsAsync(tables, 300, Range("I64", "-9223372036854775808", "-9223372036854775807")));
        Assert.Equal(new uint[] { 1, 3 }, await IdsAsync(tables, 300, Range("U32", "4294967294", "4294967295")));
        Assert.Equal(new uint[] { 2, 3 }, await IdsAsync(tables, 300, Range("U8", "-5", "1")));
        Assert.Equal(new uint[] { 1, 2, 3 }, await IdsAsync(tables, 300, Range("U64", "-1", "18446744073709551616")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 300, Range("U8", "0", "0")));
    }

    [Theory]
    [InlineData("U8", "256")]
    [InlineData("U8", "-1")]
    [InlineData("U8", "511")]
    [InlineData("U32", "4294967296")]
    [InlineData("U32", "-4294967295")]
    [InlineData("I64", "9223372036854775808")]
    [InlineData("I64", "-9223372036854775809")]
    [InlineData("U64", "18446744073709551616")]
    [InlineData("U64", "-1")]
    [InlineData("ID", "-4294967296")]
    public async Task IntegerOutOfRangeMatchesNothing(string column, string value)
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Empty(await IdsAsync(tables, 300, Is(column, value)));
    }

    [Theory]
    [InlineData("01")]
    [InlineData("00")]
    [InlineData("-0")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("0x1")]
    [InlineData("")]
    [InlineData("100000000000000000000")]
    [InlineData("-100000000000000000000")]
    public async Task IntegerValuesMustBeCanonical(string value)
    {
        await using var tables = await OpenFiltersAsync();
        await AssertBadAsync(tables, 300, TableQueries.FilterValueCode, Is("U32", value));
        await AssertBadAsync(tables, 300, TableQueries.FilterValueCode, Range("U32", "0", value));
    }

    [Fact]
    public async Task IntegerRangeMustBeOrdered()
    {
        await using var tables = await OpenFiltersAsync();
        await AssertBadAsync(tables, 300, TableQueries.FilterValueCode, Range("U32", "2", "1"));
    }

    [Fact]
    public async Task FloatRangeComparesAsFloat32()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 1, 2, 4 }, await IdsAsync(tables, 100, Range("Weight", "-1", "2.25")));
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 100, Range("Weight", "1.5", "1.5")));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 3000000000 }, await IdsAsync(tables, 100, Range("Weight", "-3.4028235E+38", "3.4028235E+38")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 100, Range("Weight", "2.2500001", "2.25000001")));
    }

    [Fact]
    public async Task FloatTakesFiniteBetweenOnly()
    {
        await using var tables = await OpenFiltersAsync();
        await AssertBadAsync(tables, 100, TableQueries.FilterOpCode, Is("Weight", "1.5"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", "NaN", "1"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", "0", "Infinity"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", "0", "1e40"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", "abc", "1"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", " 1", "2"));
        await AssertBadAsync(tables, 100, TableQueries.FilterValueCode, Range("Weight", "2", "1"));
    }

    [Fact]
    public async Task StringEqualsIsOrdinalAndSkipsBadText()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 100, Is("Label", "Sword")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 100, Is("Label", "sword")));
        Assert.Empty(await IdsAsync(tables, 100, Is("Label", "SWORD")));
        Assert.Empty(await IdsAsync(tables, 100, Is("Label", "Sword ")));
        // Row 4's cell is "" only because its text is invalid; it never matches.
        Assert.Equal(new uint[] { 5 }, await IdsAsync(tables, 100, Is("Label", "")));
        await AssertBadAsync(tables, 100, TableQueries.FilterOpCode, Range("Label", "A", "Z"));
    }

    [Fact]
    public async Task ArrayBaseNameMatchesAnyElement()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 1, 2, 5 }, await IdsAsync(tables, 100, Is("Flags", "3")));
        Assert.Equal(new uint[] { 1, 5 }, await IdsAsync(tables, 100, Is("Flags[1]", "3")));
        Assert.Equal(new uint[] { 2 }, await IdsAsync(tables, 100, Is("Flags[0]", "3")));
        Assert.Equal(new uint[] { 2, 4 }, await IdsAsync(tables, 100, Range("Flags[0]", "2", "3")));
    }

    [Theory]
    [InlineData("Nope")]
    [InlineData("label")]
    [InlineData("Flags[2]")]
    [InlineData("Flags[")]
    [InlineData("Weight[0]")]
    public async Task UnknownColumnIsBadFilter(string column)
    {
        await using var tables = await OpenFiltersAsync();
        await AssertBadAsync(tables, 100, TableQueries.FilterColumnCode, Is(column, "1"));
    }

    [Fact]
    public async Task FiltersAllHold()
    {
        await using var tables = await OpenFiltersAsync();
        Assert.Equal(new uint[] { 10, 15, 20 }, await IdsAsync(tables, 200, Is("Value", "0"), Range("ID", "6", "20")));
        Assert.Equal(new uint[] { 1 }, await IdsAsync(tables, 100, Is("Flags", "3"), Range("Weight", "0", "2")));
        Assert.Empty(await IdsAsync(tables, 100, Is("Label", "Sword"), Is("Label", "sword")));
        await AssertBadAsync(tables, 100, TableQueries.FilterColumnCode, Is("Label", "Sword"), Is("Nope", "1"));
    }

    [Fact]
    public async Task FilteredScanIsBoundedAndContinues()
    {
        await using var tables = await OpenFiltersAsync();
        var (ids, pages) = await FilteredAsync(tables, 200, 50, Is("Value", "0"));
        Assert.Equal([5u, 10, 15, 20, 25], ids);
        Assert.Equal([9L, 19L, null], pages.Select(p => p.NextAfter));
        Assert.Equal([10L, 10L, 5L], pages.Select(p => p.Scanned));

        var (none, empty) = await FilteredAsync(tables, 200, 50, Is("Value", "7"));
        Assert.Empty(none);
        Assert.Equal([9L, 19L, null], empty.Select(p => p.NextAfter));

        // A full page stops at its last match; the last match is the last row, so that page ends.
        var (one, single) = await FilteredAsync(tables, 200, 1, Is("Value", "0"));
        Assert.Equal([5u, 10, 15, 20, 25], one);
        Assert.Equal([4L, 9L, 14L, 19L, null], single.Select(p => p.NextAfter));
        Assert.All(single, p => Assert.Equal(5L, p.Scanned));
    }

    [Fact]
    public async Task FilteredPageCursorAndIdentity()
    {
        await using var tables = await OpenFiltersAsync();
        var first = await tables.PageAsync(new(200, Filters: [Is("Value", "1")]), Now, Ct);
        Assert.Equal([0L, 5L], first.Rows.Select(r => r.Ordinal));
        var stale = await tables.PageAsync(new(200, 9, Filters: [Is("Value", "1")], Identity: first.Identity + "x"), Now, Ct);
        Assert.Equal((TablePageState.Stale, 0), (stale.State, stale.Rows.Count));
        var atEnd = await tables.PageAsync(new(200, 24, Filters: [Is("Value", "1")]), Now, Ct);
        Assert.Equal((TablePageState.Ok, 0, null, 0L), (atEnd.State, atEnd.Rows.Count, atEnd.NextAfter, atEnd.Scanned));
        var beyond = await tables.PageAsync(new(200, 25, Filters: [Is("Value", "1")]), Now, Ct);
        Assert.Equal(TablePageState.InvalidCursor, beyond.State);
    }

    [Fact]
    public async Task AtMostEightFilters()
    {
        await using var tables = await OpenFiltersAsync();
        var eight = Enumerable.Repeat(Is("Value", "0"), TableQueries.MaxFilters).ToArray();
        Assert.Equal(new uint[] { 5, 10, 15, 20, 25 }, await IdsAsync(tables, 200, eight));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(200, Filters: [.. eight, Is("Value", "0")]), Now, Ct));
    }

    [Fact]
    public async Task FilterTextIsBounded()
    {
        await using var tables = await OpenFiltersAsync();
        var longest = new string('a', TableQueries.MaxFilterValueLength);
        Assert.Empty(await IdsAsync(tables, 100, Is("Label", longest)));
        await AssertBadAsync(tables, 100, TableQueries.FilterColumnCode, Is(new string('a', TableQueries.MaxFilterColumnLength), "1"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Filters: [Is("Label", longest + "a")]), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Filters: [Range("ID", "0", longest + "1")]), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            tables.PageAsync(new(100, Filters: [Is(new string('a', TableQueries.MaxFilterColumnLength + 1), "1")]), Now, Ct));
    }

    // GetAsync

    [Fact]
    public async Task RowOutcomesStayDistinct()
    {
        await using var tables = await OpenAsync();
        var found = await tables.GetAsync(100, 30, Now, Ct);
        Assert.Equal((TableRowOutcome.Found, 2L, 30u), (found.Outcome, found.Row!.Ordinal, found.Row.Row.Id));
        Assert.Equal(TableRowOutcome.Absent, (await tables.GetAsync(100, 31, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Found, (await tables.GetAsync(600, 2, Now, Ct)).Outcome);
        var keyed = await tables.GetAsync(600, 10, Now, Ct);
        Assert.Equal((TableRowOutcome.Protected, Key, null), (keyed.Outcome, keyed.ProtectedKey, keyed.Row));
        var unknown = await tables.GetAsync(600, 3, Now, Ct);
        Assert.Equal((TableRowOutcome.Unknown, TableState.Partial), (unknown.Outcome, unknown.Table.State));
        Assert.Equal(TableRowOutcome.Protected, (await tables.GetAsync(700, 20, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Unknown, (await tables.GetAsync(700, 21, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Unknown, (await tables.GetAsync(400, 1, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Unknown, (await tables.GetAsync(500, 1, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Absent, (await tables.GetAsync(900, 0, Now, Ct)).Outcome);
    }

    // Follow and referenced-by

    private static ForeignKey To(string table, string column = "ID") => new(table, column);

    /// <summary>
    /// For relations, scanning at most 20 rows. 100 Item: foreign keys to every kind of target. 200 ItemClass: a non-ID int8 target
    /// (ClassID 2 three times). 300 Spell: an ID target with row 0 (Rank only pads the record: a DB2 record has a field). 400 Aura: a partial ID target (keyed 10 and 11). 500 Delta: not in
    /// the build. 600 Big: 25 rows, over the scan bound. 700 Effect: partial (keyed 5). 800 Locked: every section keyed. 900 ItemExtra:
    /// its signed int32 ID is a foreign key to Item (an ID above 2^31), and a 2-element array references Spell.
    /// </summary>
    public static TableScenario RelationScenario() => new(Build, "enUS",
    [
        new(100, "Item", [ScenarioColumn.Id(), ScenarioColumn.Int("ClassID", 8, foreignKey: To("itemclass", "ClassID")),
                ScenarioColumn.Int("SpellID", foreignKey: To("Spell")), ScenarioColumn.Int("AuraID", foreignKey: To("Aura")),
                ScenarioColumn.Int("Wide", foreignKey: To("ItemClass", "ClassID")), ScenarioColumn.Int("LostID", foreignKey: To("Delta")),
                ScenarioColumn.Int("NoneID", foreignKey: To("Nowhere")), ScenarioColumn.Int("BigRef", foreignKey: To("Big", "Value")),
                ScenarioColumn.Int("EffectSpell", foreignKey: To("Effect", "SpellID")), ScenarioColumn.Int("Value")],
            [Row(1, "2", "10", "1", "300", "1", "1", "3", "10", "5"),
             Row(2, "0", "0", "10", "2", "1", "1", "3", "12", "5"),
             Row(3, "7", "99", "3", "7", "1", "1", "3", "10", "5"),
             Row(4, "2", "-1", "1", "2", "1", "1", "3", "10", "5"),
             Row(3000000000, "7", "99", "1", "7", "1", "1", "3", "10", "5")]),
        new(200, "ItemClass", [ScenarioColumn.Id(), ScenarioColumn.Int("ClassID", 8)],
            [Row(1, "2"), Row(2, "2"), Row(3, "0"), Row(4, "2"), Row(5, "4")]),
        new(300, "Spell", [ScenarioColumn.Id(), ScenarioColumn.Int("Rank")], [Row(0, "1"), Row(10, "1"), Row(11, "1")]),
        new(400, "Aura", [ScenarioColumn.Id(), ScenarioColumn.Int("Rank")], [Row(1, "1"), Row(2, "1")], new ScenarioKeyedSection(Key, [10, 11])),
        new(500, "Delta", [ScenarioColumn.Id(), ScenarioColumn.Int("SpellID", foreignKey: To("Spell"))], [], InBuild: false),
        new(600, "Big", [ScenarioColumn.Id(), ScenarioColumn.Int("Value"), ScenarioColumn.Int("SpellID", foreignKey: To("Spell"))],
            [.. Enumerable.Range(1, 25).Select(i => Row((uint)i, "3", "10"))]),
        new(700, "Effect", [ScenarioColumn.Id(), ScenarioColumn.Int("SpellID", foreignKey: To("Spell"))], [Row(1, "10"), Row(2, "11")],
            new ScenarioKeyedSection(Key, [5])),
        new(800, "Locked", [ScenarioColumn.Id(), ScenarioColumn.Int("SpellID", foreignKey: To("Spell"))], [], new ScenarioKeyedSection(Key, [1])),
        new(900, "ItemExtra", [new("ID", ColumnKind.Int, 32, false, IsId: true, ForeignKey: To("Item")),
                ScenarioColumn.Int("Spells", arrayLength: 2, foreignKey: To("Spell"))],
            [Row(3000000000, "11", "10"), Row(1, "0", "12")]),
    ], MaxScanRows: 20);

    private async Task<IBuildTables> OpenRelationsAsync()
    {
        var result = await (await CreateAsync(RelationScenario())).ForAsync(Build, "enUS", Ct);
        Assert.Equal(Files.FilesOpenState.Open, result.State);
        return result.Tables!;
    }

    private static Task<TableFollowResult> FollowAsync(IBuildTables tables, uint id, string column, long after = -1, int limit = 50) =>
        tables.FollowAsync(new(100, id, column, after, limit), Now, Ct);

    [Fact]
    public async Task FollowPagesEveryMatchOfANonIdColumn()
    {
        await using var tables = await OpenRelationsAsync();
        var first = await FollowAsync(tables, 1, "ClassID", limit: 2);
        Assert.Equal((TableFollowOutcome.Found, 200u, TableState.Ready), (first.Outcome, first.Target!.FileDataId, first.Target.State));
        Assert.Equal(new ForeignKey("itemclass", "ClassID"), first.Reference);
        Assert.Equal(new uint[] { 1, 2 }, first.Rows.Select(r => r.Row.Id));
        Assert.Equal(1L, first.NextAfter);
        var second = await FollowAsync(tables, 1, "ClassID", first.NextAfter!.Value, 2);
        Assert.Equal((TableFollowOutcome.Found, 4u, 3L, (long?)null),
            (second.Outcome, second.Rows.Single().Row.Id, second.Rows[0].Ordinal, second.NextAfter));
        var all = await FollowAsync(tables, 1, "ClassID");
        Assert.Equal(new uint[] { 1, 2, 4 }, all.Rows.Select(r => r.Row.Id));
        Assert.Null(all.NextAfter);
        Assert.Equal(TableRowOutcome.Found, all.Source.Outcome);
        var none = await FollowAsync(tables, 3, "ClassID");
        Assert.Equal((TableFollowOutcome.Absent, 0), (none.Outcome, none.Rows.Count));
    }

    [Fact]
    public async Task FollowZeroIsARealValue()
    {
        await using var tables = await OpenRelationsAsync();
        var spell = await FollowAsync(tables, 2, "SpellID");
        Assert.Equal((TableFollowOutcome.Found, 0u, 0L), (spell.Outcome, spell.Rows.Single().Row.Id, spell.Rows[0].Ordinal));
        var itemClass = await FollowAsync(tables, 2, "ClassID");
        Assert.Equal((TableFollowOutcome.Found, 3u), (itemClass.Outcome, itemClass.Rows.Single().Row.Id));
    }

    [Fact]
    public async Task FollowIdTargetOutcomes()
    {
        await using var tables = await OpenRelationsAsync();
        var found = await FollowAsync(tables, 1, "SpellID");
        Assert.Equal((TableFollowOutcome.Found, 10u, 300u), (found.Outcome, found.Rows.Single().Row.Id, found.Target!.FileDataId));
        Assert.Equal(TableFollowOutcome.Absent, (await FollowAsync(tables, 3, "SpellID")).Outcome);
        var inPartial = await FollowAsync(tables, 1, "AuraID");
        Assert.Equal((TableFollowOutcome.Found, 1u), (inPartial.Outcome, inPartial.Rows.Single().Row.Id));
        var unknown = await FollowAsync(tables, 3, "AuraID");
        Assert.Equal((TableFollowOutcome.Unknown, TableState.Partial, 0), (unknown.Outcome, unknown.Target!.State, unknown.Rows.Count));
        var keyed = await FollowAsync(tables, 2, "AuraID");
        Assert.Equal((TableFollowOutcome.Unknown, TableQueries.RelationProtectedCode, Key), (keyed.Outcome, keyed.Code, keyed.ProtectedKey));
    }

    [Fact]
    public async Task FollowPartialNonIdTargetIsALowerBound()
    {
        await using var tables = await OpenRelationsAsync();
        var found = await FollowAsync(tables, 1, "EffectSpell");
        Assert.Equal((TableFollowOutcome.Found, 1u, TableState.Partial), (found.Outcome, found.Rows.Single().Row.Id, found.Target!.State));
        var unknown = await FollowAsync(tables, 2, "EffectSpell");
        Assert.Equal((TableFollowOutcome.Unknown, TableState.Partial), (unknown.Outcome, unknown.Target!.State));
    }

    [Fact]
    public async Task FollowValueOutOfRangeNeverWraps()
    {
        await using var tables = await OpenRelationsAsync();
        // 300 doesn't fit ItemClass.ClassID (int8), and isn't wrapped to 44.
        var wide = await FollowAsync(tables, 1, "Wide");
        Assert.Equal((TableFollowOutcome.KeyOutOfRange, 0, 200u), (wide.Outcome, wide.Rows.Count, wide.Target!.FileDataId));
        var negative = await FollowAsync(tables, 4, "SpellID");
        Assert.Equal((TableFollowOutcome.KeyOutOfRange, 0), (negative.Outcome, negative.Rows.Count));
    }

    [Fact]
    public async Task FollowNeedsADeclaredReference()
    {
        await using var tables = await OpenRelationsAsync();
        var plain = await FollowAsync(tables, 1, "Value");
        Assert.Equal((TableFollowOutcome.NotAReference, null, null), (plain.Outcome, plain.Reference, plain.Target));
        Assert.Equal(TableFollowOutcome.NotAReference, (await FollowAsync(tables, 1, "Nope")).Outcome);
        Assert.Equal(TableFollowOutcome.NotAReference, (await FollowAsync(tables, 1, "ID")).Outcome);
    }

    [Fact]
    public async Task FollowFromAMissingRowGivesTheSourceRow()
    {
        await using var tables = await OpenRelationsAsync();
        var result = await FollowAsync(tables, 99, "SpellID");
        Assert.Equal((TableFollowOutcome.SourceRow, TableRowOutcome.Absent, null), (result.Outcome, result.Source.Outcome, result.Target));
        Assert.Equal(new ForeignKey("Spell", "ID"), result.Reference);
    }

    [Fact]
    public async Task FollowToATableTheBuildLacksIsUnknown()
    {
        await using var tables = await OpenRelationsAsync();
        var lost = await FollowAsync(tables, 1, "LostID");
        Assert.Equal((TableFollowOutcome.Unknown, TableState.NotInBuild), (lost.Outcome, lost.Target!.State));
        var none = await FollowAsync(tables, 1, "NoneID");
        Assert.Equal((TableFollowOutcome.Unknown, TableState.NoDefinition), (none.Outcome, none.Target!.State));
    }

    [Fact]
    public async Task FollowRefusesToScanAnOversizedTarget()
    {
        await using var tables = await OpenRelationsAsync();
        var big = await FollowAsync(tables, 1, "BigRef");
        Assert.Equal((TableFollowOutcome.Unindexed, TableQueries.RelationUnindexedCode, 0), (big.Outcome, big.Code, big.Rows.Count));
    }

    private static (uint, string, TableReferenceScope, string, bool, string?) Shape(TableReferenceSource s) =>
        (s.Table.FileDataId, s.Column, s.Scope, string.Join(",", s.RowIds), s.Truncated, s.Code);

    [Fact]
    public async Task ReferencedByListsEverySourceAndItsScope()
    {
        await using var tables = await OpenRelationsAsync();
        var result = await tables.ReferencedByAsync(new(300, 10), Now, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal(
            [(100u, "SpellID", TableReferenceScope.Complete, "1", false, null),
             (600u, "SpellID", TableReferenceScope.NotChecked, "", false, TableQueries.RelationUnindexedCode),
             (700u, "SpellID", TableReferenceScope.Partial, "1", false, null),
             (800u, "SpellID", TableReferenceScope.NotChecked, "", false, null),
             (900u, "Spells", TableReferenceScope.Complete, "3000000000", false, null)],
            result.Sources.Select(Shape));
        Assert.Equal(TableState.Encrypted, result.Sources[3].Table.State);
        var zero = await tables.ReferencedByAsync(new(300, 0), Now, Ct);
        Assert.Equal(new uint[] { 2 }, zero.Sources[0].RowIds);
    }

    [Fact]
    public async Task ReferencedByLooksUpTheTargetColumnAndTruncates()
    {
        await using var tables = await OpenRelationsAsync();
        // ItemClass row 1 is ClassID 2: Item rows 1 and 4 by ClassID, 2 and 4 by Wide.
        var result = await tables.ReferencedByAsync(new(200, 1, 1), Now, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal(
            [(100u, "ClassID", TableReferenceScope.Complete, "1", true, null),
             (100u, "Wide", TableReferenceScope.Complete, "2", true, null)],
            result.Sources.Select(Shape));
        var all = await tables.ReferencedByAsync(new(200, 1), Now, Ct);
        Assert.Equal(["1,4", "2,4"], all.Sources.Select(s => string.Join(",", s.RowIds)));
        Assert.All(all.Sources, s => Assert.False(s.Truncated));
    }

    [Fact]
    public async Task NoReferencesOnlyWhenEverySourceIsComplete()
    {
        await using var tables = await OpenRelationsAsync();
        var none = await tables.ReferencedByAsync(new(200, 5), Now, Ct);
        Assert.Equal((TableReferencesOutcome.NoReferences, 2), (none.Outcome, none.Sources.Count));
        Assert.All(none.Sources, s => Assert.Equal((TableReferenceScope.Complete, 0), (s.Scope, s.RowIds.Count)));
        var notChecked = await tables.ReferencedByAsync(new(300, 11), Now, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, notChecked.Outcome);
        Assert.Empty(notChecked.Sources.Where(s => s.Table.FileDataId == 100).Single().RowIds);
        var unreferenced = await tables.ReferencedByAsync(new(900, 1), Now, Ct);
        Assert.Equal((TableReferencesOutcome.NoReferences, 0), (unreferenced.Outcome, unreferenced.Sources.Count));
    }

    [Fact]
    public async Task FollowAnArrayElement()
    {
        await using var tables = await OpenRelationsAsync();
        var second = await tables.FollowAsync(new(900, 3000000000, "Spells[1]"), Now, Ct);
        Assert.Equal((TableFollowOutcome.Found, 10u), (second.Outcome, second.Rows.Single().Row.Id));
        var first = await tables.FollowAsync(new(900, 3000000000, "Spells[0]"), Now, Ct);
        Assert.Equal((TableFollowOutcome.Found, 11u), (first.Outcome, first.Rows.Single().Row.Id));
        Assert.Equal(TableFollowOutcome.NotAReference, (await tables.FollowAsync(new(900, 3000000000, "Spells[2]"), Now, Ct)).Outcome);
        Assert.Equal(TableFollowOutcome.NotAReference, (await tables.FollowAsync(new(900, 3000000000, "Spells"), Now, Ct)).Outcome);
    }

    [Fact]
    public async Task ReferencedByMatchesAnyArrayElement()
    {
        await using var tables = await OpenRelationsAsync();
        var eleven = await tables.ReferencedByAsync(new(300, 11), Now, Ct);
        Assert.Equal(new uint[] { 3000000000 }, eleven.Sources.Single(s => s.Table.FileDataId == 900).RowIds);
        var zero = await tables.ReferencedByAsync(new(300, 0), Now, Ct);
        Assert.Equal(new uint[] { 1 }, zero.Sources.Single(s => s.Table.FileDataId == 900).RowIds);
    }

    [Fact]
    public async Task IdColumnsSpanUint32BothWays()
    {
        await using var tables = await OpenRelationsAsync();
        // ItemExtra.ID is declared signed int32, but an ID is always 0 to uint32 max.
        var follow = await tables.FollowAsync(new(900, 3000000000, "ID"), Now, Ct);
        Assert.Equal((TableFollowOutcome.Found, 3000000000u, 100u), (follow.Outcome, follow.Rows.Single().Row.Id, follow.Target!.FileDataId));
        var references = await tables.ReferencedByAsync(new(100, 3000000000), Now, Ct);
        Assert.Equal((TableReferencesOutcome.Searched, "ID", TableReferenceScope.Complete), (references.Outcome, references.Sources.Single().Column,
            references.Sources[0].Scope));
        Assert.Equal(new uint[] { 3000000000 }, references.Sources[0].RowIds);
    }

    /// <summary>100 Source points at 200 Many's Value; 200 Many: 3000 rows, Value = ID % 1000, OneID 1 for ID % 1000 = 7; 300 One.</summary>
    public static TableScenario ChunkScenario() => new(Build, "enUS",
    [
        new(100, "Source", [ScenarioColumn.Id(), ScenarioColumn.Int("ManyRef", foreignKey: To("Many", "Value"))], [Row(1, "7")]),
        new(200, "Many", [ScenarioColumn.Id(), ScenarioColumn.Int("Value"), ScenarioColumn.Int("OneID", foreignKey: To("One"))],
            [.. Enumerable.Range(1, 3000).Select(i => Row((uint)i, $"{i % 1000}", i % 1000 == 7 ? "1" : "2"))]),
        new(300, "One", [ScenarioColumn.Id(), ScenarioColumn.Int("Rank")], [Row(1, "1"), Row(2, "1")]),
    ]);

    [Fact]
    public async Task ScansReachPastTheFirstChunk()
    {
        await using var tables = (await (await CreateAsync(ChunkScenario())).ForAsync(Build, "enUS", Ct)).Tables!;
        var follow = await tables.FollowAsync(new(100, 1, "ManyRef", Limit: 2), Now, Ct);
        Assert.Equal(new uint[] { 7, 1007 }, follow.Rows.Select(r => r.Row.Id));
        var rest = await tables.FollowAsync(new(100, 1, "ManyRef", follow.NextAfter!.Value), Now, Ct);
        Assert.Equal((2007u, (long?)null), (rest.Rows.Single().Row.Id, rest.NextAfter));
        var references = await tables.ReferencedByAsync(new(300, 1), Now, Ct);
        Assert.Equal(new uint[] { 7, 1007, 2007 }, references.Sources.Single().RowIds);
        Assert.Empty(references.Unresolved);
    }

    [Fact]
    public async Task ReferencedByAMissingRowGivesTheRow()
    {
        await using var tables = await OpenRelationsAsync();
        var absent = await tables.ReferencedByAsync(new(300, 12), Now, Ct);
        Assert.Equal((TableReferencesOutcome.SourceRow, TableRowOutcome.Absent, 0), (absent.Outcome, absent.Row.Outcome, absent.Sources.Count));
        var keyed = await tables.ReferencedByAsync(new(400, 10), Now, Ct);
        Assert.Equal((TableReferencesOutcome.SourceRow, TableRowOutcome.Protected), (keyed.Outcome, keyed.Row.Outcome));
    }

    [Fact]
    public async Task RelationArgumentsAreChecked()
    {
        await using var tables = await OpenRelationsAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.FollowAsync(null!, Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.FollowAsync(new(100, 1, ""), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.FollowAsync(new(100, 1, "SpellID", -2), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.FollowAsync(new(100, 1, "SpellID", Limit: 0), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.ReferencedByAsync(new(300, 10, 0), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.ReferencedByAsync(new(300, 10, TableQueries.MaxRowsPerSource + 1), Now, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.FollowAsync(new(100, 1, "SpellID"), Now, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.ReferencedByAsync(new(300, 10), Now, new CancellationToken(true)));
    }

    // Arguments, cancellation, dispose

    [Fact]
    public async Task BadArgumentsThrow()
    {
        await using var tables = await OpenAsync();
        Assert.Throws<ArgumentOutOfRangeException>(() => TableRef.Id(0));
        Assert.Throws<ArgumentException>(() => TableRef.ForName(" "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.CheckAsync(default, Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.GetAsync(default, 1, Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.SchemaAsync(default, Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(null!, Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(default), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Limit: 0), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Limit: TableQueries.MaxLimit + 1), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, After: -2), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Filters: [new("Value", TableFilterOp.Between, "1")]), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Filters: [new("Value", TableFilterOp.Equals, "1", "2")]), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => tables.PageAsync(new(100, Filters: [new("", TableFilterOp.Equals, "1")]), Now, Ct));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ListAsync(tables, null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ListAsync(tables, new() { States = new HashSet<TableState> { (TableState)99 } }));
    }

    [Fact]
    public async Task CanceledCallsThrow()
    {
        var service = await CreateAsync(Scenario());
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ForAsync(Build, "enUS", canceled));
        await using var tables = await OpenAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListAsync(tables, TableListFilter.All, canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.CheckAsync(100, Now, canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.SchemaAsync(100, Now, canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.PageAsync(new(100), Now, canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.GetAsync(100, 10, Now, canceled));
    }

    [Fact]
    public async Task DisposedHandleThrows()
    {
        var tables = await OpenAsync();
        await tables.DisposeAsync();
        await tables.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ListAsync(tables, TableListFilter.All));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.CheckAsync(100, Now, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.SchemaAsync(100, Now, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.PageAsync(new(100), Now, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.GetAsync(100, 10, Now, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.FollowAsync(new(100, 10, "BetaID"), Now, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.ReferencedByAsync(new(200, 1), Now, Ct));
    }
}
