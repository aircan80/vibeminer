using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

public sealed class TableQueriesTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("7", "7")]
    [InlineData("-1", "-1")]
    [InlineData("4294967295", "4294967295")]
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    [InlineData("99999999999999999999", "99999999999999999999")]
    [InlineData("-99999999999999999999", "-99999999999999999999")]
    public void ParsesCanonicalIntegersExactly(string text, string expected)
    {
        Assert.True(TableQueries.TryParseInteger(text, out var value));
        Assert.Equal(Int128.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("00")]
    [InlineData("-0")]
    [InlineData("-01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("١")]
    [InlineData("100000000000000000000")]
    [InlineData("--1")]
    public void RefusesAnythingElse(string? text) => Assert.False(TableQueries.TryParseInteger(text, out _));

    [Fact]
    public void NameMatchFoldsAsciiOnly()
    {
        var entry = new TableEntry(1, "Straße", TableState.NotChecked, false, null, null, null);
        Assert.True(TableQueries.Matches(new() { Name = "STRA" }, entry));
        Assert.False(TableQueries.Matches(new() { Name = "STRASSE" }, entry));
        Assert.False(TableQueries.Matches(new() { Name = "STRAẞE" }, entry));
        Assert.False(TableQueries.Matches(new() { Name = "" }, entry with { Name = null }));
        Assert.True(TableQueries.Matches(new() { Name = "" }, entry));
        Assert.True(TableQueries.NamesEqual("ItemSparse", "ITEMSPARSE"));
        Assert.False(TableQueries.NamesEqual("Item", "ItemSparse"));
    }

    [Fact]
    public async Task PageRefusesFiltersAndShortSources()
    {
        var entry = new TableEntry(1, "A", TableState.Ready, true, null, null, null);
        TableRowReader none = (_, _, _) => Task.FromResult<IReadOnlyList<Db2Row>>([]);
        await Assert.ThrowsAsync<ArgumentException>(() => TableQueries.PageAsync(entry, "id",
            new(1, Filters: [new("A", TableFilterOp.Equals, "1")]), 3, none, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TableQueries.PageAsync(entry, "id", new(1), 3, none, CancellationToken.None));
    }

    private static readonly Db2ValueInterpretation RawEvidence = new("raw", 32, false, 16, true, "values exceed the definition width");

    [Fact]
    public void RawColumnsArentFilterKeys()
    {
        Db2Column[] columns = [new("ID", "integer", true), new("Raw", "integer", false, RawEvidence),
            new("Mixed[0]", "integer", false), new("Mixed[1]", "integer", false, RawEvidence)];
        foreach (var name in new[] { "Raw", "Mixed", "Mixed[1]" })
        {
            var result = TableQueries.CompileFilters(columns, null, [new(name, TableFilterOp.Equals, "1")]);
            Assert.Equal((true, TableQueries.FilterRawCode), (result.IsBad, result.Code));
        }
        var plain = TableQueries.CompileFilters(columns, null, [new("Mixed[0]", TableFilterOp.Equals, "1")]);
        Assert.True(plain.Matches!(new Db2Row(1, false, null, ["1", "5", "1", "1"])));
    }

    [Fact]
    public void WithoutSchemaAnyInt64OrUInt64Value()
    {
        Db2Column[] columns = [new("ID", "integer", true), new("V", "integer", false)];
        bool Match(string value, string cell) =>
            TableQueries.CompileFilters(columns, null, [new("V", TableFilterOp.Equals, value)]).Matches!(new Db2Row(1, false, null, ["1", cell]));
        Assert.True(Match("18446744073709551615", "18446744073709551615"));
        Assert.True(Match("-9223372036854775808", "-9223372036854775808"));
        Assert.False(Match("18446744073709551616", "18446744073709551616"));
        Assert.False(Match("1", "01"));
        Assert.True(TableQueries.CompileFilters(columns, null, []).Matches!(new Db2Row(1, false, null, ["1", "x"])));
    }

    [Fact]
    public void ShortRowsDontMatch()
    {
        Db2Column[] columns = [new("ID", "integer", true), new("V", "integer", false)];
        var filter = TableQueries.CompileFilters(columns, null, [new("V", TableFilterOp.Equals, "1")]);
        Assert.False(filter.Matches!(new Db2Row(1, false, null, ["1"])));
    }

    [Fact]
    public async Task FilteredPageArguments()
    {
        var entry = new TableEntry(1, "A", TableState.Ready, true, null, null, null);
        TableRowReader none = (_, _, _) => Task.FromResult<IReadOnlyList<Db2Row>>([]);
        Db2Column[] columns = [new("ID", "integer", true)];
        TableFilter[] filters = [new("ID", TableFilterOp.Equals, "1")];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TableQueries.PageAsync(entry, "id", new(1, Filters: filters), 3, none,
            columns, null, 0, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => TableQueries.PageAsync(entry, "id", new(1, Filters: filters), 3, none,
            columns, null, 10, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TableQueries.PageAsync(entry, "id", new(1, Filters: filters), 3, none,
            columns, null, 10, new CancellationToken(true)));
    }

    [Fact]
    public void SignedIdDefinitionStillFiltersAsUInt32()
    {
        // WoWDBDefs declares $id$ID<32> signed; the reader gives the ID as uint32.
        var block = new VibeMiner.Formats.DbdBlock(["00000000"], [],
            [new(new("ID", "int", true, null, null), 32, false, 1, true, false, false, null)], null);
        var schema = VibeMiner.Formats.Definitions.TableSchema.FromBlock(block, VibeMiner.Formats.Definitions.SchemaMatch.Build,
            VibeMiner.Formats.Definitions.WowLocale.Parse("enUS"));
        Db2Column[] columns = [new("ID", "integer", true)];
        var row = new Db2Row(3000000000, false, null, ["3000000000"]);
        Assert.True(TableQueries.CompileFilters(columns, schema, [new("ID", TableFilterOp.Equals, "3000000000")]).Matches!(row));
        Assert.True(TableQueries.CompileFilters(columns, schema, [new("ID", TableFilterOp.Between, "0", "4294967295")]).Matches!(row));
        Assert.False(TableQueries.CompileFilters(columns, schema, [new("ID", TableFilterOp.Equals, "-1294967296")]).Matches!(row));
    }

    // 3000 rows of [ID, Hit]; Hit is "1" at ordinals 1023, 1024, 2047 and 2048, either side of the 1024-row read chunks.
    private static readonly Db2Row[] ChunkRows = [.. Enumerable.Range(0, 3000).Select(i =>
        new Db2Row((uint)i + 1, false, null, [$"{i + 1}", i is 1023 or 1024 or 2047 or 2048 ? "1" : "0"]))];
    private static readonly Db2Column[] ChunkColumns = [new("ID", "integer", true), new("Hit", "integer", false)];
    private static readonly TableEntry ChunkEntry = new(1, "A", TableState.Ready, true, null, null, null);

    private static Task<TablePage> ChunkPageAsync(long after, int limit, List<(long First, int Count)> reads, CancellationToken ct = default) =>
        TableQueries.PageAsync(ChunkEntry, "id", new(1, after, limit, [new("Hit", TableFilterOp.Equals, "1")]), ChunkRows.Length,
            (first, count, _) =>
            {
                reads.Add((first, count));
                return Task.FromResult<IReadOnlyList<Db2Row>>(ChunkRows.Skip((int)first).Take(count).ToArray());
            }, ChunkColumns, null, 2500, ct);

    [Fact]
    public async Task FilteredScanCrossesChunksExactly()
    {
        var reads = new List<(long First, int Count)>();
        var first = await ChunkPageAsync(-1, 50, reads);
        Assert.Equal([1023L, 1024, 2047, 2048], first.Rows.Select(r => r.Ordinal));
        Assert.Equal((2499L, 2500L), (first.NextAfter, first.Scanned));
        Assert.Equal([(0L, 1024), (1024L, 1024), (2048L, 452)], reads);
        reads.Clear();
        var rest = await ChunkPageAsync(2499, 50, reads);
        Assert.Equal((0, null, 500L), (rest.Rows.Count, rest.NextAfter, rest.Scanned));
        Assert.Equal([(2500L, 500)], reads);
    }

    [Fact]
    public async Task FilteredScanStopsAtLimitMidChunk()
    {
        var reads = new List<(long First, int Count)>();
        var first = await ChunkPageAsync(-1, 3, reads);
        Assert.Equal([1023L, 1024, 2047], first.Rows.Select(r => r.Ordinal));
        Assert.Equal((2047L, 2048L), (first.NextAfter, first.Scanned));
        var second = await ChunkPageAsync(2047, 3, reads);
        Assert.Equal([2048L], second.Rows.Select(r => r.Ordinal));
        Assert.Equal((null, 952L), (second.NextAfter, second.Scanned));
    }

    [Fact]
    public async Task FilteredScanStopsWhenCanceledBetweenChunks()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;
        TableRowReader read = (first, count, _) =>
        {
            reads++;
            cts.Cancel();
            return Task.FromResult<IReadOnlyList<Db2Row>>(ChunkRows.Skip((int)first).Take(count).ToArray());
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TableQueries.PageAsync(ChunkEntry, "id",
            new(1, Filters: [new("Hit", TableFilterOp.Equals, "1")]), ChunkRows.Length, read, ChunkColumns, null, 2500, cts.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void FloatNaNNeverMatches()
    {
        IReadOnlyList<VibeMiner.Formats.Db2Column> columns = [new("ID", "integer", true), new("Weight", "float", false)];
        var filter = TableQueries.CompileFilters(columns, null, [new("Weight", TableFilterOp.Between, "-3.4028235E+38", "3.4028235E+38")]);
        Assert.True(filter.Matches!(new(1, false, null, ["1", "1.5"])));
        Assert.False(filter.Matches!(new(2, false, null, ["2", "NaN"])));
    }
}

/// <summary>The shared relation rules against a bare source, for what a fake can't stage.</summary>
public sealed class TableRelationQueriesTests
{
    // Target(1) and Source(2) have 5000 rows each, every Value and Ref 0; Source row 1's Ref is 7 when looked up by ID. Source.Ref
    // points at Target.Value, Source.Back at Target.ID. Reading the second chunk cancels.
    private sealed class CancellingSource(CancellationTokenSource cancel) : ITableRelationSource
    {
        public int Reads;

        private static TableSchema Schema(params DbdField[] fields) =>
            TableSchema.FromBlock(new(["00000000"], [], fields, null), SchemaMatch.Build, WowLocale.Parse("enUS"));

        private static DbdField Field(string name, bool id = false, DbdReference? reference = null) =>
            new(new DbdColumn(name, "int", true, null, reference), 32, false, 1, id, false, false, null);

        private static TableEntry Entry(uint id, string name) => new(id, name, TableState.Ready, true, null, null, null);

        private Task<IReadOnlyList<Db2Row>> Read(long first, int count, CancellationToken _)
        {
            if (++Reads == 2) cancel.Cancel();
            return Task.FromResult<IReadOnlyList<Db2Row>>([.. Enumerable.Range((int)first, count).Select(i => new Db2Row((uint)i + 1, false, null, [$"{i + 1}", "0", "0"]))]);
        }

        private RelationTable Target => new(Entry(1, "Target"), [new("ID", "integer", true), new("Value", "integer", false), new("Spare", "integer", false)],
            Schema(Field("ID", true), Field("Value"), Field("Spare")), 5000, Read);

        private RelationTable Source => new(Entry(2, "Source"), [new("ID", "integer", true), new("Ref", "integer", false), new("Back", "integer", false)],
            Schema(Field("ID", true), Field("Ref", reference: new("Target", "Value")), Field("Back", reference: new("Target", "ID"))), 5000, Read);

        public Task<RelationTable> OpenAsync(TableRef table, CancellationToken cancellationToken) =>
            Task.FromResult(table.FileDataId == 2 || table.Name == "Source" ? Source : Target);

        public Task<TableRowResult> GetAsync(TableRef table, uint id, CancellationToken cancellationToken) =>
            Task.FromResult(table.FileDataId == 2
                ? new TableRowResult(Source.Entry, TableRowOutcome.Found, new(0, new Db2Row(1, false, null, ["1", "7"])), null)
                : new TableRowResult(Target.Entry, TableRowOutcome.Found, new(0, new Db2Row(1, false, null, ["1", "0"])), null));

        public async IAsyncEnumerable<TableEntry> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return Target.Entry;
            yield return Source.Entry;
        }

        public Task<RelationSchema> SchemaAsync(TableEntry table, CancellationToken cancellationToken) =>
            Task.FromResult(new RelationSchema(RelationSchemaState.Resolved, table.FileDataId == 2 ? Source.Schema : Target.Schema));
    }

    [Fact]
    public async Task FollowStopsScanningWhenCanceled()
    {
        using var cancel = new CancellationTokenSource();
        var source = new CancellingSource(cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TableQueries.FollowAsync(source, new(2, 1, "Ref"), 10_000, cancel.Token));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task ReferencedByStopsScanningWhenCanceled()
    {
        using var cancel = new CancellationTokenSource();
        var source = new CancellingSource(cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TableQueries.ReferencedByAsync(source, new(1, 1), 10_000, 256, 1_000_000, cancel.Token));
        Assert.Equal(2, source.Reads);
    }
}
