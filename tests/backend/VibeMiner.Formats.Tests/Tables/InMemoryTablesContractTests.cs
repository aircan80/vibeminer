using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>The <see cref="TablesContract"/> against <see cref="InMemoryTables"/>, plus what only the fake does.</summary>
public sealed class InMemoryTablesContractTests : TablesContract
{
    protected override Task<ITables> CreateAsync(TableScenario scenario) => Task.FromResult<ITables>(Seed(scenario));

    private static InMemoryTables Seed(TableScenario scenario) =>
        new InMemoryTables { MaxScanRows = scenario.MaxScanRows }.Add(scenario.Build, scenario.Locale, scenario.Tables.Select(table => Fake(table, scenario.Locale)));

    /// <summary>The state each side derives from the scenario, and the fake seeds directly.</summary>
    private static FakeTable Fake(ScenarioTable table, string locale)
    {
        var state = !table.InBuild ? TableState.NotInBuild
            : table.Name is null ? TableState.NoDefinition
            : table.Keyed is not null ? table.Rows.Count > 0 ? TableState.Partial : TableState.Encrypted
            : TableState.Ready;
        TableState? listed = table.Generated ? state : !table.Stored ? TableState.NotStored : null;
        var columns = table.Columns.SelectMany(c => c.Flattened.Select(name => new Db2Column(name, Kind(c.Kind), c.IsId))).ToArray();
        var schema = table.Name is null ? null : TableSchema.FromBlock(Block(table.Columns), SchemaMatch.Build, WowLocale.Parse(locale));
        return new FakeTable(table.FileDataId, table.Name, state, columns,
            TableQueries.IsReadable(state) ? [.. table.Rows.Select(Row)] : null,
            schema, ListedState: listed, Generated: table.Generated,
            ProtectedIds: table.Keyed is { } keyed ? keyed.Ids.ToDictionary(id => id, _ => keyed.KeyName) : null);
    }

    private static Db2Row Row(ScenarioRow row) => new(row.Id, false, null, row.Values,
        row.BadText is { Count: > 0 } bad ? [.. bad.Select(i => new Db2TextIssue(i, "DB2_TEXT_ENCODING_INVALID", "ff"))] : null);

    private static string Kind(ColumnKind kind) => kind switch { ColumnKind.Int => "integer", ColumnKind.Float => "float", _ => "string" };

    private static DbdBlock Block(IReadOnlyList<ScenarioColumn> columns) => new(["00000000"], [],
        [.. columns.Select(c => new DbdField(
            new DbdColumn(c.Name, c.Kind.ToString().ToLowerInvariant(), true, null, c.ForeignKey is { } fk ? new DbdReference(fk.Table, fk.Column) : null),
            c.Bits, c.Unsigned, c.ArrayLength, c.IsId, false, false, null))], null);

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task CountsCallsPerMethod()
    {
        var fake = Seed(Scenario());
        await using var tables = (await fake.ForAsync(Build, "enUS", Ct)).Tables!;
        await tables.CheckAsync(100, TablePriority.Interactive, Ct);
        await tables.PageAsync(new(100), TablePriority.Background, Ct);
        await tables.PageAsync(new(100), TablePriority.Background, Ct);
        Assert.Equal((1, 1, 2, 0), (fake.Calls(nameof(ITables.ForAsync)), fake.Calls(nameof(IBuildTables.CheckAsync)),
            fake.Calls(nameof(IBuildTables.PageAsync)), fake.Calls(nameof(IBuildTables.GetAsync))));
    }

    [Fact]
    public async Task CheckedStateOutlivesTheHandle()
    {
        var fake = Seed(Scenario());
        await using (var first = (await fake.ForAsync(Build, "enUS", Ct)).Tables!) await first.CheckAsync(600, TablePriority.Interactive, Ct);
        await using var second = (await fake.ForAsync(Build, "enUS", Ct)).Tables!;
        await foreach (var entry in second.ListAsync(new() { Name = "Epsilon" }, TablePriority.Interactive, Ct))
            Assert.Equal((TableState.Partial, true), (entry.State, entry.Checked));
    }

    private static TableSchema Schema(params DbdField[] fields) => Schema(SchemaMatch.Build, fields);

    private static TableSchema Schema(SchemaMatch match, params DbdField[] fields) =>
        TableSchema.FromBlock(new(["00000000"], [], fields, null), match, WowLocale.Parse("enUS"));

    private static readonly IReadOnlyList<Db2Column> RefColumns = [new("ID", "integer", true), new("TargetID", "integer", false)];

    private static FakeTable Target(uint id = 1) =>
        new(id, "Target", TableState.Ready, [new("ID", "integer", true)], [new Db2Row(1, false, null, ["1"])], Schema(Field("ID", true)));

    private static FakeTable Referrer(uint id, TableState state, SchemaMatch match = SchemaMatch.Build, string? name = null, int rows = 1) =>
        new(id, name ?? $"Source{id}", state, RefColumns,
            TableQueries.IsReadable(state) ? [.. Enumerable.Range(1, rows).Select(i => new Db2Row((uint)i, false, null, [$"{i}", "2"]))] : null,
            Schema(match, Field("ID", true), Field("TargetID", reference: new("Target", "ID"))));

    private static async Task<IBuildTables> OpenAsync(InMemoryTables fake) => (await fake.ForAsync(Build, "enUS", Ct)).Tables!;

    private static DbdField Field(string name, bool id = false, DbdReference? reference = null) =>
        new(new DbdColumn(name, "int", true, null, reference), 32, false, 1, id, false, false, null);

    private static readonly Db2ValueInterpretation RawEvidence = new("raw", 32, false, 16, true, "values exceed the definition width");

    [Fact]
    public async Task RawEvidenceIsNeverAKey()
    {
        // The scenario can't express raw evidence, so the fake is seeded directly.
        var target = new FakeTable(1, "Target", TableState.Ready, [new("ID", "integer", true), new("Code", "integer", false, RawEvidence)],
            [new Db2Row(1, false, null, ["1", "7"])], Schema(Field("ID", true), Field("Code")));
        var source = new FakeTable(2, "Source", TableState.Ready,
            [new("ID", "integer", true), new("CodeRef", "integer", false), new("RawRef", "integer", false, RawEvidence)],
            [new Db2Row(1, false, null, ["1", "7", "1"])],
            Schema(Field("ID", true), Field("CodeRef", reference: new("Target", "Code")), Field("RawRef", reference: new("Target", "ID"))));
        var fake = new InMemoryTables().Add(Build, "enUS", [target, source]);
        await using var tables = (await fake.ForAsync(Build, "enUS", Ct)).Tables!;
        var toRaw = await tables.FollowAsync(new(2, 1, "CodeRef"), TablePriority.Interactive, Ct);
        var fromRaw = await tables.FollowAsync(new(2, 1, "RawRef"), TablePriority.Interactive, Ct);
        Assert.Equal((TableFollowOutcome.NotAReference, TableQueries.RelationRawCode), (toRaw.Outcome, toRaw.Code));
        Assert.Equal((TableFollowOutcome.NotAReference, TableQueries.RelationRawCode), (fromRaw.Outcome, fromRaw.Code));
        var references = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, references.Outcome);
        Assert.Equal([(TableReferenceScope.NotChecked, TableQueries.RelationRawCode), (TableReferenceScope.NotChecked, TableQueries.RelationRawCode)],
            references.Sources.Select(s => (s.Scope, s.Code)));
    }

    [Fact]
    public async Task ReferencedBySearchesAtMostTheSourceCap()
    {
        var target = new FakeTable(1, "Target", TableState.Ready, [new("ID", "integer", true)], [new Db2Row(1, false, null, ["1"])], Schema(Field("ID", true)));
        var sources = Enumerable.Range(2, 4).Select(i => new FakeTable((uint)i, $"Source{i}", TableState.Ready,
            [new("ID", "integer", true), new("TargetID", "integer", false)], [new Db2Row(1, false, null, ["1", "2"])],
            Schema(Field("ID", true), Field("TargetID", reference: new("Target", "ID")))));
        var fake = new InMemoryTables { MaxReferenceSources = 3 }.Add(Build, "enUS", [target, .. sources]);
        await using var tables = (await fake.ForAsync(Build, "enUS", Ct)).Tables!;
        var result = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal([(2u, TableReferenceScope.Complete, (string?)null), (3u, TableReferenceScope.Complete, null), (4u, TableReferenceScope.Complete, null),
                (5u, TableReferenceScope.NotChecked, TableQueries.RelationSourceLimitCode)],
            result.Sources.Select(s => (s.Table.FileDataId, s.Scope, s.Code)));
        Assert.Equal(TableQueries.DefaultMaxReferenceSources, new InMemoryTables().MaxReferenceSources);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTables { MaxReferenceSources = -1 });
    }

    [Fact]
    public async Task ACellThatIsntAnIntegerIsUnknown()
    {
        var bad = new FakeTable(2, "Source", TableState.Ready, RefColumns,
            [new Db2Row(1, false, null, ["1", ""], [new Db2TextIssue(1, "DB2_TEXT_ENCODING_INVALID", "ff")]), new Db2Row(2, false, null, ["2", "x"])],
            Schema(Field("ID", true), Field("TargetID", reference: new("Target", "ID"))));
        var other = new FakeTable(1, "Target", TableState.Ready, [new("ID", "integer", true), new("Code", "integer", false)],
            [new Db2Row(1, false, null, ["1", "1.5"])], Schema(Field("ID", true), Field("Code")));
        var back = new FakeTable(3, "Back", TableState.Ready, RefColumns, [new Db2Row(1, false, null, ["1", "1"])],
            Schema(Field("ID", true), Field("TargetID", reference: new("Target", "Code"))));
        await using var tables = await OpenAsync(new InMemoryTables().Add(Build, "enUS", [other, bad, back]));
        foreach (var id in new uint[] { 1, 2 })
        {
            var result = await tables.FollowAsync(new(2, id, "TargetID"), TablePriority.Interactive, Ct);
            Assert.Equal((TableFollowOutcome.Unknown, TableQueries.RelationValueCode), (result.Outcome, result.Code));
        }
        var references = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, references.Outcome);
        var code = references.Sources.Single(s => s.Table.FileDataId == 3);
        Assert.Equal((TableReferenceScope.NotChecked, TableQueries.RelationValueCode), (code.Scope, code.Code));
    }

    // D291: an absent external parent is "" with DB2_RELATION_ABSENT. It is no value: follow says no parent, it never
    // matches parent 0 in referenced-by or a filter.
    [Fact]
    public async Task AnAbsentParentIsNoValueAndNeverZero()
    {
        var absent = new Db2TextIssue(1, Db2Reader.RelationAbsent, "");
        var source = new FakeTable(2, "Source", TableState.Ready, RefColumns,
            [new Db2Row(1, false, null, ["1", ""], [absent]), new Db2Row(2, false, null, ["2", "0"]), new Db2Row(3, true, 1, ["3", ""], [absent])],
            Schema(Field("ID", true), Field("TargetID", reference: new("Target", "ID"))));
        var target = new FakeTable(1, "Target", TableState.Ready, RefColumns, [new Db2Row(0, false, null, ["0", "0"])], Schema(Field("ID", true), Field("TargetID")));
        await using var tables = await OpenAsync(new InMemoryTables().Add(Build, "enUS", [target, source]));
        foreach (var id in new uint[] { 1, 3 })
        {
            var result = await tables.FollowAsync(new(2, id, "TargetID"), TablePriority.Interactive, Ct);
            Assert.Equal((TableFollowOutcome.Unknown, TableQueries.RelationValueCode, "Column TargetID has no parent for this row."), (result.Outcome, result.Code, result.Message));
        }
        Assert.Equal(TableFollowOutcome.Found, (await tables.FollowAsync(new(2, 2, "TargetID"), TablePriority.Interactive, Ct)).Outcome);
        var references = await tables.ReferencedByAsync(new(1, 0), TablePriority.Interactive, Ct);
        Assert.Equal([2u], references.Sources.Single(s => s.Table.FileDataId == 2).RowIds);
        var zero = TableQueries.CompileFilters(RefColumns, null, [new("TargetID", TableFilterOp.Equals, "0")]).Matches!;
        Assert.Equal([false, true, false], source.Rows!.Select(zero));
    }

    [Fact]
    public async Task OnlyTablesThatCantDeclareOrHoldRowsAreLeftOut()
    {
        var fake = new InMemoryTables().Add(Build, "enUS",
            [Target(), Referrer(2, TableState.NotInLocale), Referrer(3, TableState.NotInBuild), Referrer(4, TableState.NoDefinition), Referrer(5, TableState.Ready)]);
        await using var tables = await OpenAsync(fake);
        var result = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal((TableReferencesOutcome.NoReferences, 5u), (result.Outcome, result.Sources.Single().Table.FileDataId));
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public async Task UnresolvedSchemasRuleOutNoReferences()
    {
        var fake = new InMemoryTables().Add(Build, "enUS",
            [Target(), Referrer(2, TableState.DefinitionUnusable), Referrer(3, TableState.Ready, SchemaMatch.Layout), Referrer(5, TableState.Ready)]);
        await using var tables = await OpenAsync(fake);
        var result = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal([2u, 3], result.Unresolved.Select(e => e.FileDataId));
        Assert.Equal(5u, result.Sources.Single().Table.FileDataId);
        // Looking up schemas checks nothing: the layout-matched table is still unchecked; once checked, its schema resolves.
        Assert.Equal(0, fake.Calls(nameof(IBuildTables.CheckAsync)));
        await tables.CheckAsync(3, TablePriority.Interactive, Ct);
        var again = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal([2u], again.Unresolved.Select(e => e.FileDataId));
        Assert.Equal([3u, 5], again.Sources.Select(s => s.Table.FileDataId));
    }

    [Fact]
    public async Task ARowOfANamelessTableIsSearchedNotUnreferenced()
    {
        var nameless = new FakeTable(1, null, TableState.Ready, [new("ID", "integer", true)], [new Db2Row(1, false, null, ["1"])]);
        await using var tables = await OpenAsync(new InMemoryTables().Add(Build, "enUS", [nameless]));
        var result = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal((TableReferencesOutcome.Searched, TableRowOutcome.Found, 0), (result.Outcome, result.Row.Outcome, result.Sources.Count));
    }

    [Fact]
    public async Task ReferencedByStaysWithinItsRowBudget()
    {
        var fake = new InMemoryTables { MaxReferenceScanRows = 5 }.Add(Build, "enUS",
            [Target(), Referrer(2, TableState.Ready, rows: 3), Referrer(3, TableState.Ready, rows: 3), Referrer(4, TableState.Ready, rows: 2)]);
        await using var tables = await OpenAsync(fake);
        var result = await tables.ReferencedByAsync(new(1, 1), TablePriority.Interactive, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal([(2u, TableReferenceScope.Complete, (string?)null), (3u, TableReferenceScope.NotChecked, TableQueries.RelationBudgetCode),
                (4u, TableReferenceScope.Complete, null)],
            result.Sources.Select(s => (s.Table.FileDataId, s.Scope, s.Code)));
        Assert.Equal(TableQueries.DefaultMaxReferenceScanRows, new InMemoryTables().MaxReferenceScanRows);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTables { MaxReferenceScanRows = -1 });
    }

    [Fact]
    public void ScanBoundIsPositive() => Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryTables { MaxScanRows = 0 });

    [Fact]
    public void SeedRejectsImpossibleTables()
    {
        var fake = new InMemoryTables();
        Assert.Throws<ArgumentException>(() => fake.Add(Build, "enUS", [new FakeTable(1, "A", TableState.NotChecked)]));
        Assert.Throws<ArgumentException>(() => fake.Add(Build, "enUS", [new FakeTable(1, "A", TableState.Encrypted, Rows: [new Db2Row(1, false, null, ["1"])])]));
        Assert.Throws<ArgumentException>(() => fake.Add(Build, "enUS", [new FakeTable(1, "A", TableState.Ready), new FakeTable(2, "a", TableState.Ready)]));
        Assert.Throws<ArgumentException>(() => fake.Add(Build, "xxXX", []));
    }
}
