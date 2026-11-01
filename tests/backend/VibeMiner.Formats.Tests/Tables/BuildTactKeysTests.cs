using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary><see cref="BuildTactKeys"/> over <see cref="InMemoryTables"/>. All key bytes are made up.</summary>
public sealed class BuildTactKeysTests
{
    private const string Locale = "enUS";
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly byte[] NameA = [0x0c, 0x2d, 0x17, 0x76, 0x29, 0xcc, 0x43, 0x1c];
    private static readonly byte[] NameB = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
    private static readonly byte[] NameC = [0xf0, 0xe0, 0xd0, 0xc0, 0xb0, 0xa0, 0x90, 0x80];

    private static byte[] Key(byte seed) => [.. Enumerable.Range(0, 16).Select(i => (byte)(seed + i))];

    private static string[] Cells(byte[] bytes) => [.. bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))];

    private static Db2Column[] Columns(string name, int width) =>
        [new("ID", "integer", true), .. Enumerable.Range(0, width).Select(i => new Db2Column($"{name}[{i}]", "integer", false))];

    private static Db2Row Row(uint id, string[] cells) => new(id, false, null, [id.ToString(CultureInfo.InvariantCulture), .. cells]);

    private static FakeTable KeyTable(TableState state, params (uint Id, string[] Cells)[] rows) =>
        new(BuildTactKeys.TactKeyFileDataId, BuildTactKeys.TactKeyName, state, Columns("Key", 16),
            TableQueries.IsReadable(state) ? [.. rows.Select(r => Row(r.Id, r.Cells))] : null);

    private static FakeTable LookupTable(TableState state, params (uint Id, string[] Cells)[] rows) =>
        new(BuildTactKeys.TactKeyLookupFileDataId, BuildTactKeys.TactKeyLookupName, state, Columns("TACTID", 8),
            TableQueries.IsReadable(state) ? [.. rows.Select(r => Row(r.Id, r.Cells))] : null);

    private static async Task<BuildTactKeySet> LoadAsync(params FakeTable[] tables)
    {
        var fake = new InMemoryTables().Add(TablesContract.Build, Locale, tables);
        var open = await fake.ForAsync(TablesContract.Build, Locale, Ct);
        await using var handle = open.Tables!;
        return await BuildTactKeys.LoadAsync(handle, TablePriority.Background, Ct);
    }

    private static TableSchema Schema(string name, int width, bool unsigned) => TableSchema.FromBlock(new DbdBlock(["00000000"], [],
        [
            new DbdField(new DbdColumn("ID", "int", true, null, null), 32, true, 1, true, false, false, null),
            new DbdField(new DbdColumn(name, "int", true, null, null), 8, unsigned, width, false, false, false, null),
        ], null), SchemaMatch.Build, WowLocale.Parse(Locale));

    private static string Fingerprint(byte[] key) => Convert.ToHexStringLower(SHA256.HashData(key));

    [Fact]
    public async Task JoinsByIdAndNamesLittleEndian()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(0x10))), (2, Cells(Key(0x40)))),
            LookupTable(TableState.Ready, (2, Cells(NameB)), (1, Cells(NameA))));

        Assert.Equal(2, set.Count);
        Assert.Equal(["0807060504030201", "1C43CC2976172D0C"], set.Names);
        Assert.Equal(Key(0x10), set.Find(0x1C43CC2976172D0CUL)!.Value.ToArray());
        Assert.Equal(Key(0x40), set.Find(0x0807060504030201UL)!.Value.ToArray());
        Assert.Equal(Fingerprint(Key(0x10)), set.FingerprintOf("1C43CC2976172D0C"));
        Assert.Equal(BuildTactKeyTableOutcome.Read, set.KeyTable.Outcome);
        Assert.Equal(BuildTactKeyTableOutcome.Read, set.LookupTable.Outcome);
        Assert.Equal(TableState.Ready, set.KeyTable.Table.State);
        Assert.Equal((0, 0, 0), (set.SkippedZero, set.Unmatched, set.Malformed));
        Assert.Empty(set.Conflicts);
        Assert.Null(set.Find(0x1122334455667788UL));
        Assert.Null(set.FingerprintOf("1122334455667788"));
    }

    [Fact]
    public async Task FindAndFingerprintAgree()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(1))), (2, Cells(Key(2))), (3, Cells(Key(3)))),
            LookupTable(TableState.Ready, (1, Cells(NameA)), (2, Cells(NameB)), (3, Cells(NameC))));

        Assert.Same(set, set.Keys);
        Assert.Same(set, set.Status);
        foreach (var name in set.Names)
        {
            var key = set.Keys.Find(ulong.Parse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            Assert.NotNull(key);
            Assert.Equal(Fingerprint(key.Value.ToArray()), set.Status.FingerprintOf(name));
            Assert.Matches("^[0-9a-f]{64}$", set.Status.FingerprintOf(name));
        }
        // A fingerprint the generated store accepts.
        _ = new VibeMiner.Formats.Generated.TactKeyStatus(set.Names.ToDictionary(n => n, n => set.FingerprintOf(n)!));
    }

    [Fact]
    public async Task SkipsAllZeroPlaceholders()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(new byte[16])), (2, Cells(Key(9)))),
            LookupTable(TableState.Ready, (1, Cells(NameA)), (2, Cells(NameB))));

        Assert.Equal(["0807060504030201"], set.Names);
        Assert.Null(set.Find(0x1C43CC2976172D0CUL));
        Assert.Equal(1, set.SkippedZero);
        Assert.Equal(0, set.Unmatched);
    }

    [Fact]
    public async Task DropsANameJoinedToTwoDifferentKeys()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(1))), (2, Cells(Key(2))), (3, Cells(Key(3))), (4, Cells(Key(4))), (5, Cells(Key(4)))),
            LookupTable(TableState.Ready, (1, Cells(NameA)), (2, Cells(NameA)), (3, Cells(NameA)), (4, Cells(NameB)), (5, Cells(NameB))));

        // A third row for the conflicted name doesn't bring it back; the same key twice isn't a conflict.
        Assert.Equal(["1C43CC2976172D0C"], set.Conflicts);
        Assert.Null(set.Find(0x1C43CC2976172D0CUL));
        Assert.Null(set.FingerprintOf("1C43CC2976172D0C"));
        Assert.Equal(["0807060504030201"], set.Names);
    }

    [Fact]
    public async Task SkipsUnmatchedRowsOnEitherSide()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(1))), (7, Cells(Key(7)))),
            LookupTable(TableState.Ready, (1, Cells(NameA)), (8, Cells(NameB)), (9, Cells(NameC))));

        Assert.Equal(["1C43CC2976172D0C"], set.Names);
        Assert.Equal(3, set.Unmatched);
    }

    [Theory]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("007")]
    [InlineData(" 7")]
    [InlineData("")]
    [InlineData("1.5")]
    public async Task SkipsAndCountsMalformedByteCells(string cell)
    {
        var bad = Cells(Key(1));
        bad[3] = cell;
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, bad), (2, Cells(Key(2)))),
            LookupTable(TableState.Ready, (1, Cells(NameA)), (2, Cells(NameB))));

        Assert.Equal(["0807060504030201"], set.Names);
        Assert.Equal(1, set.Malformed);
        // The lookup row of the malformed key row counts only as malformed.
        Assert.Equal(0, set.Unmatched);
    }

    [Fact]
    public async Task AMalformedLookupRowLeavesItsKeyRowOutOfUnmatched()
    {
        var bad = Cells(NameA);
        bad[0] = "x";
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(1))), (2, Cells(Key(2)))),
            LookupTable(TableState.Ready, (1, bad), (2, Cells(NameB))));

        Assert.Equal(["0807060504030201"], set.Names);
        Assert.Equal((1, 0, 0), (set.Malformed, set.Unmatched, set.Unknown));
    }

    [Fact]
    public async Task ReadsNegativeCellsOfASignedInt8ColumnAsTwosComplement()
    {
        var key = Key(0xf0); // f0 .. ff
        var cells = Cells(key);
        cells[15] = "-1";
        cells[0] = "-16";
        var keys = KeyTable(TableState.Ready, (1, cells)) with { Schema = Schema("Key", 16, unsigned: false) };
        var set = await LoadAsync(keys, LookupTable(TableState.Ready, (1, Cells(NameA))));

        Assert.Equal(key, set.Find(0x1C43CC2976172D0CUL)!.Value.ToArray());
        Assert.Equal(0, set.Malformed);
    }

    [Theory]
    [InlineData("-129")]
    [InlineData("-0")]
    [InlineData("-01")]
    public async Task RefusesOutOfRangeSignedCells(string cell)
    {
        var cells = Cells(Key(1));
        cells[2] = cell;
        var keys = KeyTable(TableState.Ready, (1, cells)) with { Schema = Schema("Key", 16, unsigned: false) };
        var set = await LoadAsync(keys, LookupTable(TableState.Ready, (1, Cells(NameA))));

        Assert.Equal(0, set.Count);
        Assert.Equal(1, set.Malformed);
    }

    [Fact]
    public async Task NegativeCellsOfAnUnsignedColumnAreMalformed()
    {
        var cells = Cells(Key(1));
        cells[2] = "-1";
        var keys = KeyTable(TableState.Ready, (1, cells)) with { Schema = Schema("Key", 16, unsigned: true) };
        var set = await LoadAsync(keys, LookupTable(TableState.Ready, (1, Cells(NameA))));

        Assert.Equal(0, set.Count);
        Assert.Equal(1, set.Malformed);
    }

    [Fact]
    public async Task APartialTableStillGivesItsReadableKeys()
    {
        var keys = KeyTable(TableState.Partial, (1, Cells(Key(1)))) with { ProtectedIds = new Dictionary<uint, string> { [2] = "AABBCCDDEEFF0011" } };
        var set = await LoadAsync(keys, LookupTable(TableState.Ready, (1, Cells(NameA)), (2, Cells(NameB))));

        Assert.Equal(["1C43CC2976172D0C"], set.Names);
        Assert.Equal(TableState.Partial, set.KeyTable.Table.State);
        Assert.Equal(BuildTactKeyTableOutcome.Read, set.KeyTable.Outcome);
        // Lookup row 2's key may sit in the protected section: unknown, not unmatched.
        Assert.Equal((1, 0), (set.Unknown, set.Unmatched));
    }

    [Fact]
    public async Task AKeyRowWithoutItsLookupRowInAPartialLookupTableIsUnknown()
    {
        var lookup = LookupTable(TableState.Partial, (1, Cells(NameA))) with { ProtectedIds = new Dictionary<uint, string> { [2] = "AABBCCDDEEFF0011" } };
        var set = await LoadAsync(KeyTable(TableState.Ready, (1, Cells(Key(1))), (2, Cells(Key(2)))), lookup);

        Assert.Equal(["1C43CC2976172D0C"], set.Names);
        Assert.Equal((1, 0), (set.Unknown, set.Unmatched));
    }

    [Fact]
    public async Task ABuildWithoutTheTablesGivesAnEmptySetWithTheirStates()
    {
        // A build without the tables.
        var set = await LoadAsync(new FakeTable(100, "Spell", TableState.Ready, Columns("Name", 1), []));

        Assert.Equal(0, set.Count);
        Assert.Empty(set.Names);
        Assert.Equal(TableState.NotInBuild, set.KeyTable.Table.State);
        Assert.Equal(TableState.NotInBuild, set.LookupTable.Table.State);
        Assert.Equal(BuildTactKeyTableOutcome.NotReadable, set.KeyTable.Outcome);
        Assert.Equal(BuildTactKeyTableOutcome.NotReadable, set.LookupTable.Outcome);
        Assert.Null(set.FingerprintOf("1C43CC2976172D0C"));
    }

    [Fact]
    public async Task AnUnreadableTableGivesNoKeysAndTheOtherIsntPaged()
    {
        var fake = new InMemoryTables().Add(TablesContract.Build, Locale,
        [
            KeyTable(TableState.Missing),
            LookupTable(TableState.Ready, (1, Cells(NameA))),
        ]);
        var open = await fake.ForAsync(TablesContract.Build, Locale, Ct);
        await using var handle = open.Tables!;
        var set = await BuildTactKeys.LoadAsync(handle, TablePriority.Interactive, Ct);

        Assert.Equal(0, set.Count);
        Assert.Equal(TableState.Missing, set.KeyTable.Table.State);
        Assert.Equal(BuildTactKeyTableOutcome.NotReadable, set.KeyTable.Outcome);
        Assert.Equal(BuildTactKeyTableOutcome.NotNeeded, set.LookupTable.Outcome);
        Assert.Equal(0, fake.Calls(nameof(IBuildTables.PageAsync)));
    }

    [Fact]
    public async Task FindsTheTablesByNameUnderAnotherFileDataId()
    {
        var set = await LoadAsync(
            KeyTable(TableState.Ready, (1, Cells(Key(1)))) with { FileDataId = 5000 },
            LookupTable(TableState.Ready, (1, Cells(NameA))) with { FileDataId = 5001 });

        Assert.Equal(["1C43CC2976172D0C"], set.Names);
        Assert.Equal(5000u, set.KeyTable.Table.FileDataId);
    }

    [Fact]
    public async Task MissingByteColumnsGiveNoKeys()
    {
        var lookup = LookupTable(TableState.Ready, (1, Cells(NameA))) with { Columns = Columns("Other", 8) };
        var set = await LoadAsync(KeyTable(TableState.Ready, (1, Cells(Key(1)))), lookup);

        Assert.Equal(0, set.Count);
        Assert.Equal(BuildTactKeyTableOutcome.MissingColumns, set.LookupTable.Outcome);
    }

    [Fact]
    public async Task PagesPastOnePage()
    {
        var count = 2 * BuildTactKeys.PageRows + 5;
        var keyRows = Enumerable.Range(1, count).Select(i => ((uint)i, Cells(Key((byte)i)))).ToArray();
        var lookupRows = Enumerable.Range(1, count).Select(i => ((uint)i, Cells(BitConverter.GetBytes((ulong)i)))).ToArray();
        var set = await LoadAsync(KeyTable(TableState.Ready, keyRows), LookupTable(TableState.Ready, lookupRows));

        Assert.Equal(count, set.Count);
        Assert.Equal(Key((byte)count), set.Find((ulong)count)!.Value.ToArray());
    }

    [Fact]
    public async Task RefusesMoreThanMaxRows()
    {
        var keyRows = Enumerable.Range(1, BuildTactKeys.MaxRows + 1).Select(i => ((uint)i, Cells(Key((byte)i)))).ToArray();
        var set = await LoadAsync(KeyTable(TableState.Ready, keyRows), LookupTable(TableState.Ready, (1, Cells(NameA))));

        Assert.Equal(0, set.Count);
        Assert.Equal(BuildTactKeyTableOutcome.TooManyRows, set.KeyTable.Outcome);
    }

    [Theory]
    [InlineData(1, BuildTactKeyTableOutcome.Read)]
    [InlineData(2, BuildTactKeyTableOutcome.Stale)]
    public async Task RereadsATableOnceWhenItsIdentityChanges(int changes, BuildTactKeyTableOutcome outcome)
    {
        var count = BuildTactKeys.PageRows + 5;
        var keyRows = Enumerable.Range(1, count).Select(i => ((uint)i, Cells(Key((byte)i)))).ToArray();
        var lookupRows = Enumerable.Range(1, count).Select(i => ((uint)i, Cells(BitConverter.GetBytes((ulong)i)))).ToArray();
        var fake = new InMemoryTables().Add(TablesContract.Build, Locale, [KeyTable(TableState.Ready, keyRows), LookupTable(TableState.Ready, lookupRows)]);
        var open = await fake.ForAsync(TablesContract.Build, Locale, Ct);
        await using var handle = new ChangingTables(open.Tables!, BuildTactKeys.TactKeyFileDataId, changes);
        var set = await BuildTactKeys.LoadAsync(handle, TablePriority.Background, Ct);

        Assert.Equal(outcome, set.KeyTable.Outcome);
        Assert.Equal(BuildTactKeyTableOutcome.Read, set.LookupTable.Outcome);
        Assert.Equal(outcome == BuildTactKeyTableOutcome.Read ? count : 0, set.Count);
        Assert.Equal(0, handle.ChangesLeft);
    }

    /// <summary>Answers Stale to the next <paramref name="changes"/> continuation pages of one table, as if its identity changed.</summary>
    private sealed class ChangingTables(IBuildTables inner, uint fileDataId, int changes) : IBuildTables
    {
        public int ChangesLeft { get; private set; } = changes;
        public VibeMiner.Builds.BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken ct) => inner.ListAsync(filter, priority, ct);
        public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken ct) => inner.CheckAsync(table, priority, ct);
        public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken ct) => inner.SchemaAsync(table, priority, ct);
        public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken ct) => inner.GetAsync(table, id, priority, ct);
        public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken ct) => inner.FollowAsync(request, priority, ct);
        public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken ct) => inner.ReferencedByAsync(request, priority, ct);
        public Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken ct) => inner.PreloadAsync(progress, ct);

        public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken ct)
        {
            var page = await inner.PageAsync(request, priority, ct);
            if (request.Table.FileDataId != fileDataId || request.Identity is null || ChangesLeft == 0) return page;
            ChangesLeft--;
            return page with { State = TablePageState.Stale, Rows = [], NextAfter = null, Identity = "changed" };
        }
    }

    [Fact]
    public async Task Cancels()
    {
        var fake = new InMemoryTables().Add(TablesContract.Build, Locale,
            [KeyTable(TableState.Ready, (1, Cells(Key(1)))), LookupTable(TableState.Ready, (1, Cells(NameA)))]);
        var open = await fake.ForAsync(TablesContract.Build, Locale, Ct);
        await using var handle = open.Tables!;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuildTactKeys.LoadAsync(handle, TablePriority.Background, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => BuildTactKeys.LoadAsync(null!, TablePriority.Background, Ct));
    }
}
