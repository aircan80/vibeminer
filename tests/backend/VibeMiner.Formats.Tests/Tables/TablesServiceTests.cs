using System.Buffers.Binary;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tests.Generated;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>What only the real <see cref="TablesService"/> does: every state mapping, and reading each table once. Synthetic data only.</summary>
public sealed class TablesServiceTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const TablePriority Now = TablePriority.Interactive;
    private static readonly DateTimeOffset Seen = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];
    private static readonly ScenarioRow[] Rows = [new(1, ["1", "5"]), new(2, ["2", "256"])];

    private static byte[] Bytes(IReadOnlyList<ScenarioRow>? rows = null) => SyntheticTables.Wdc5(Plain, rows ?? Rows, null).Bytes;

    private static SyntheticTable Table(uint id, FakeFile? file, string? dbd = null) =>
        new(id, $"T{id}", dbd ?? SyntheticTables.Dbd(Plain), file);

    private static FakeFile Stored(uint id, byte[]? bytes = null) => new(id, Content: bytes ?? Bytes(), Storage: FileStorage.Stored);

    private static byte[] Patched(Action<byte[]> patch) { var bytes = Bytes(); patch(bytes); return bytes; }

    private static IReadOnlyList<SyntheticTable> StateTables() =>
    [
        Table(10, Stored(10) with { Locales = ["deDE"] }),
        Table(11, new FakeFile(11, Size: 100, NotInEncoding: true)),
        Table(12, new FakeFile(12, Size: 100)),
        Table(13, new FakeFile(13, Content: Bytes(), ReadFailure: new(FileReadState.Missing, "OBJECT_ACCESS_MISSING", ObservedAt: Seen))),
        Table(14, new FakeFile(14, Content: Bytes(), ReadFailure: new(FileReadState.Corrupt, "FILES_CORRUPT"))),
        Table(15, new FakeFile(15, Content: Bytes(), ReadFailure: new(FileReadState.WrongKey, "FILES_WRONG_KEY"))),
        Table(16, new FakeFile(16, Content: Bytes(), ReadFailure: new(FileReadState.Unsupported, "BLTE_MODE_UNSUPPORTED"))),
        Table(17, Stored(17) with { Holes = [new FileHole(0, Bytes().Length, Key)] }),
        Table(18, Stored(18, Patched(b => b[4] = 4))),
        Table(19, Stored(19, Patched(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(148), 9)))),
        Table(20, Stored(20), SyntheticTables.Dbd(Plain, layout: "0A0B0C0D")),
        Table(21, Stored(21), SyntheticTables.Dbd(Plain, build: "9.9.9.9", layout: "0A0B0C0D")),
        new(22, "T22", null, Stored(22)),
        Table(23, Stored(23), SyntheticTables.Dbd([ScenarioColumn.Id(), ScenarioColumn.Int("Value", 8)])),
        new(24, null, null, Stored(24)),
        Table(25, null),
    ];

    private static async Task<IBuildTables> OpenAsync(SyntheticWorld world) => (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;

    private static async Task<List<TableEntry>> ListAsync(IBuildTables tables, TableListFilter? filter = null)
    {
        var list = new List<TableEntry>();
        await foreach (var entry in tables.ListAsync(filter ?? TableListFilter.All, Now, Ct)) list.Add(entry);
        return list;
    }

    [Theory]
    [InlineData(10u, TableState.NotInLocale, TableState.NotInLocale, null)]
    [InlineData(11u, TableState.NotInEncoding, TableState.NotInEncoding, null)]
    [InlineData(12u, TableState.NotStored, TableState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK")]
    [InlineData(13u, TableState.NotStored, TableState.Missing, "OBJECT_ACCESS_MISSING")]
    [InlineData(14u, TableState.NotStored, TableState.Corrupt, "FILES_CORRUPT")]
    [InlineData(15u, TableState.NotStored, TableState.WrongKey, "FILES_WRONG_KEY")]
    [InlineData(16u, TableState.NotStored, TableState.Unsupported, "BLTE_MODE_UNSUPPORTED")]
    [InlineData(17u, TableState.NotChecked, TableState.Encrypted, "DB2_SOURCE_PROTECTED")]
    [InlineData(18u, TableState.NotChecked, TableState.Unsupported, "DB2_UNSUPPORTED")]
    [InlineData(19u, TableState.NotChecked, TableState.Rejected, "DB2_INVALID")]
    // Checks resolve with the file's layout, so a definition without a block for it is no definition, as the reader would find.
    [InlineData(20u, TableState.NotChecked, TableState.NoDefinition, "DB2_SCHEMA_MISSING")]
    [InlineData(21u, TableState.NoDefinition, TableState.NoDefinition, "DB2_SCHEMA_MISSING")]
    [InlineData(22u, TableState.DefinitionUnusable, TableState.DefinitionUnusable, "DEFINITION_NOT_BUNDLED")]
    [InlineData(23u, TableState.NotChecked, TableState.DefinitionUnusable, "DB2_SCHEMA_MISMATCH")]
    [InlineData(24u, TableState.NoDefinition, TableState.NoDefinition, "TABLE_NOT_IN_MANIFEST")]
    [InlineData(25u, TableState.NotInBuild, TableState.NotInBuild, null)]
    public async Task EachCauseHasItsShallowAndCheckedState(uint id, TableState listed, TableState checkedState, string? code)
    {
        var world = await SyntheticTables.CreateAsync(Build, StateTables());
        await using var tables = await OpenAsync(world);
        var shallow = (await ListAsync(tables)).Single(e => e.FileDataId == id);
        Assert.Equal((listed, false, null), (shallow.State, shallow.Checked, shallow.Counts));
        var entry = await tables.CheckAsync(id, Now, Ct);
        Assert.Equal((checkedState, true, null), (entry.State, entry.Checked, entry.Counts));
        Assert.Equal(code, entry.Reason?.Code);
        // A check that didn't open leaves nothing behind: the table still lists shallow (a fetch may have stored its bytes).
        var after = (await ListAsync(tables)).Single(e => e.FileDataId == id);
        Assert.False(after.Checked);
        Assert.True(after.State == listed || after.State == TableState.NotChecked);
    }

    [Theory]
    [InlineData(12u, TableState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK")]
    [InlineData(17u, TableState.Encrypted, "DB2_SOURCE_PROTECTED")]
    public async Task AnUnreadableHeadKeepsItsStateWhenOnlyTheLayoutCouldResolve(uint id, TableState checkedState, string code)
    {
        // The definition covers this build only by layout, and the head that holds the layout can't be read.
        var layoutOnly = SyntheticTables.Dbd(Plain, build: "9.9.9.9");
        var table = StateTables().Single(t => t.FileDataId == id) with { Dbd = layoutOnly };
        var world = await SyntheticTables.CreateAsync(Build, [table]);
        await using var tables = await OpenAsync(world);
        var entry = await tables.CheckAsync(id, Now, Ct);
        Assert.Equal((checkedState, code), (entry.State, entry.Reason?.Code));
        if (checkedState == TableState.Encrypted) Assert.Equal([Key], entry.Reason!.KeyNames);
    }

    [Fact]
    public async Task ReasonsKeepTheLayersDetail()
    {
        var world = await SyntheticTables.CreateAsync(Build, StateTables());
        await using var tables = await OpenAsync(world);
        var missing = await tables.CheckAsync(13, Now, Ct);
        Assert.Equal((Seen, Db2OpenState.Missing), (missing.Reason!.ObservedAt, missing.Reason.Db2OpenState));
        Assert.Matches("^[0-9a-f]{32}$", missing.ContentKey);
        var wholly = await tables.CheckAsync(17, Now, Ct);
        Assert.Equal((Db2OpenState.Protected, Key), (wholly.Reason!.Db2OpenState, wholly.Reason.KeyNames.Single()));
        Assert.Equal(DefinitionState.FetchingOff, (await tables.CheckAsync(21, Now, Ct)).Reason!.DefinitionState);
        Assert.Equal(DefinitionState.Unreadable, (await tables.CheckAsync(22, Now, Ct)).Reason!.DefinitionState);
        var rejected = await tables.CheckAsync(19, Now, Ct);
        Assert.Equal(Db2OpenState.Rejected, rejected.Reason!.Db2OpenState);
        Assert.NotNull(rejected.Reason.Message);
        Assert.NotNull((await tables.CheckAsync(11, Now, Ct)).ContentKey);
    }

    [Fact]
    public async Task ReaderLimitIsTooLarge()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(30, Stored(30))], new Db2Limits(MaxRows: 1));
        await using var tables = await OpenAsync(world);
        var entry = await tables.CheckAsync(30, Now, Ct);
        Assert.Equal((TableState.TooLarge, "DB2_LIMIT"), (entry.State, entry.Reason!.Code));
    }

    [Fact]
    public async Task KeyedSectionsArePartialOrEncryptedWithCountsAndKeyNames()
    {
        var world = await SyntheticTables.CreateAsync(Build, [.. Scenario().Tables.Select(SyntheticTables.From)]);
        await using var tables = await OpenAsync(world);
        var partial = await tables.CheckAsync(600, Now, Ct);
        Assert.Equal(new TableCounts(4, 2, 1, 2, "WDC5", "01020304"), partial.Counts);
        Assert.Equal((TableState.Partial, Db2OpenState.Open), (partial.State, partial.Reason!.Db2OpenState));
        var keyed = await tables.CheckAsync(700, Now, Ct);
        Assert.Equal((TableState.Encrypted, "TABLE_ALL_SECTIONS_KEYED", 0L), (keyed.State, keyed.Reason!.Code, keyed.Counts!.ReadableRows));
        Assert.Equal([Key], keyed.Reason.KeyNames);
        var page = await tables.PageAsync(new(700), Now, Ct);
        Assert.Equal(TablePageState.NotReadable, page.State);
    }

    [Fact]
    public async Task StoredButUnopenedIsNotCheckedNeverReady()
    {
        var world = await SyntheticTables.CreateAsync(Build, [.. Scenario().Tables.Select(SyntheticTables.From)]);
        await using var tables = await OpenAsync(world);
        var list = await ListAsync(tables);
        Assert.DoesNotContain(list, e => e.State is TableState.Ready or TableState.Partial or TableState.Encrypted);
        Assert.All(list.Where(e => e.FileDataId is 100 or 300 or 600 or 700 or 800 or 900), e => Assert.Equal((TableState.NotChecked, false), (e.State, e.Checked)));
        Assert.Equal(0, world.Counted.Reads);
        Assert.Equal(0, world.Generated.Decodes);
    }

    [Fact]
    public async Task ASecondOpenReadsAndDecodesNothing()
    {
        var world = await SyntheticTables.CreateAsync(Build, [.. Scenario().Tables.Select(SyntheticTables.From)]);
        await using (var first = await OpenAsync(world))
        {
            Assert.Equal(TableState.Ready, (await first.CheckAsync(100, Now, Ct)).State);
            Assert.Equal(TableState.Partial, (await first.CheckAsync(600, Now, Ct)).State);
        }
        Assert.True(world.Counted.Reads > 0);
        Assert.Equal(2, world.Generated.Decodes);
        var reads = world.Counted.Reads;

        await using var second = await OpenAsync(world);
        Assert.Equal(TableState.Ready, (await second.CheckAsync(TableRef.ForName("ALPHA"), Now, Ct)).State);
        Assert.Equal(TableState.Partial, (await second.CheckAsync(600, Now, Ct)).State);
        Assert.NotNull((await second.SchemaAsync(100, Now, Ct)).Columns);
        Assert.Equal(7, (await second.PageAsync(new(100), Now, Ct)).Rows.Count);
        Assert.Equal(TableRowOutcome.Found, (await second.GetAsync(100, 70, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Absent, (await second.GetAsync(100, 71, Now, Ct)).Outcome);
        Assert.Equal(TableState.Ready, (await ListAsync(second, new() { Name = "Alpha" })).Single().State);
        Assert.Equal((reads, 2), (world.Counted.Reads, world.Generated.Decodes));
    }

    [Fact]
    public async Task ABuildWithoutAVersionDoesNotOpen()
    {
        var world = await SyntheticTables.CreateAsync(Build, StateTables());
        world.Files.Add(Other, ["enUS"], [Stored(10)]);
        var result = await world.Service.ForAsync(Other, "enUS", Ct);
        Assert.Equal((Files.FilesOpenState.Unsupported, "TABLES_BUILD_VERSION_UNKNOWN", null), (result.State, result.Code, result.Tables));
        var noLocale = await world.Service.ForAsync(Build, "frFR", Ct);
        Assert.Equal(Files.FilesOpenState.LocaleNotInBuild, noLocale.State);
    }

    [Fact]
    public async Task AListBeingReadRunsOnAfterDispose()
    {
        var world = await SyntheticTables.CreateAsync(Build, [.. Scenario().Tables.Select(SyntheticTables.From)]);
        var tables = await OpenAsync(world);
        var seen = new List<uint>();
        await foreach (var entry in tables.ListAsync(TableListFilter.All, Now, Ct))
        {
            if (seen.Count == 0) await tables.DisposeAsync();
            seen.Add(entry.FileDataId);
        }
        Assert.Equal([100u, 200, 300, 400, 500, 600, 700, 800, 900], seen);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => tables.CheckAsync(100, Now, Ct));
    }

    // The table list

    [Fact]
    public async Task UnmanifestedDb2sAreFoundByListfileNameOnly()
    {
        var tables = new List<SyntheticTable> { Table(10, Stored(10)), new(24, null, null, Stored(24)), new(26, null, null, Stored(26)) };
        var world = await SyntheticTables.CreateAsync(Build, tables);
        // 26 is a DB2 by its bytes but named outside dbfilesclient/: its head is never read for the list.
        world.Files.Names.Use(new Dictionary<uint, string> { [10] = "dbfilesclient/t10.db2", [24] = "dbfilesclient/other.db2", [26] = "interface/t26.db2" });
        await using (var named = await OpenAsync(world))
            Assert.Equal([10u, 24], (await ListAsync(named)).Select(e => e.FileDataId));
        var filter = Assert.Single(world.Counted.Lists);
        Assert.Equal("dbfilesclient/", filter.PathPrefix);
        Assert.Equal([Files.FileFormat.Db2], filter.Formats!);

        // With the names off no unmanifested DB2 is found; the manifest's tables still list.
        var off = await SyntheticTables.CreateAsync(Build, tables, names: false);
        await using var tablesOff = await OpenAsync(off);
        Assert.Equal([10u], (await ListAsync(tablesOff)).Select(e => e.FileDataId));
    }

    [Fact]
    public async Task AListedFileThatIsntADb2IsNotInBuild()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10)), new(27, null, null, new FakeFile(27, Content: "BLP2...."u8.ToArray(), Storage: FileStorage.Stored))],
            names: false);
        await using var tables = await OpenAsync(world);
        var entry = await tables.CheckAsync(27, Now, Ct);
        Assert.Equal((TableState.NotInBuild, true), (entry.State, entry.Checked));
    }

    // Layer 2 failures

    [Fact]
    public async Task ALayer2FailureIsStoreFailedNotAThrow()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10)), new(24, null, null, Stored(24))]);
        await using var tables = await OpenAsync(world);
        world.Counted.StatFailure = new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The index can't be read.");
        world.Counted.ListFailure = new ObjectStoreException(ObjectStoreCodes.Closed, "The store was closed.");
        // The unmanifested DB2s can't be looked for; the manifest's tables list with the failure.
        var listed = Assert.Single(await ListAsync(tables));
        Assert.Equal((10u, TableState.StoreFailed, ObjectStoreCodes.IndexUnreadable), (listed.FileDataId, listed.State, listed.Reason!.Code));
        var entry = await tables.CheckAsync(10, Now, Ct);
        Assert.Equal((TableState.StoreFailed, true, ObjectStoreCodes.IndexUnreadable), (entry.State, entry.Checked, entry.Reason!.Code));
        Assert.Equal(TablePageState.NotReadable, (await tables.PageAsync(new(10), Now, Ct)).State);

        // Cancellation is not a state.
        world.Counted.StatFailure = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.CheckAsync(10, Now, Ct));
        world.Counted.StatFailure = null;
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(10, Now, Ct)).State);
    }

    // Protected or unknown

    [Fact]
    public async Task ProtectedIdsAreReadOncePerTable()
    {
        var world = await SyntheticTables.CreateAsync(Build, [.. Scenario().Tables.Select(SyntheticTables.From)]);
        await using (var first = await OpenAsync(world))
        {
            Assert.Equal(TableState.Partial, (await first.CheckAsync(600, Now, Ct)).State);
            var reads = world.Counted.Reads;
            var hit = await first.GetAsync(600, 10, Now, Ct);
            Assert.Equal((TableRowOutcome.Protected, Key), (hit.Outcome, hit.ProtectedKey));
            Assert.True(world.Counted.Reads > reads);
        }
        // Another handle on the same service shares them: no misses read the file again.
        var after = world.Counted.Reads;
        await using var second = await OpenAsync(world);
        Assert.Equal((TableRowOutcome.Protected, Key), ((await second.GetAsync(600, 11, Now, Ct)).Outcome, (await second.GetAsync(600, 10, Now, Ct)).ProtectedKey));
        Assert.Equal(TableRowOutcome.Unknown, (await second.GetAsync(600, 99, Now, Ct)).Outcome);
        Assert.Equal(TableRowOutcome.Found, (await second.GetAsync(600, 1, Now, Ct)).Outcome);
        Assert.Equal(after, world.Counted.Reads);
        Assert.Equal(0, world.Counted.OpenStreams);
    }

    [Fact]
    public void TheProtectedIdCacheIsBounded()
    {
        var cache = new ProtectedIds();
        for (var i = 0; i <= ProtectedIds.MaxTables; i++) cache.Put($"t{i}", new Dictionary<uint, string> { [1] = Key });
        Assert.Null(cache.Get("t0"));
        Assert.NotNull(cache.Get($"t{ProtectedIds.MaxTables}"));
        var big = Enumerable.Range(0, ProtectedIds.MaxIds).ToDictionary(i => (uint)i, _ => Key);
        cache.Put("big", big);
        Assert.Same(big, cache.Get("big"));
        Assert.Null(cache.Get("t1"));
        cache.Put("over", Enumerable.Range(0, ProtectedIds.MaxIds + 1).ToDictionary(i => (uint)i, _ => Key));
        Assert.Null(cache.Get("over"));
    }

    // A decode outliving its caller

    [Fact]
    public async Task ACanceledCheckStillClosesTheFileWhenTheDecodeEnds()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10))]);
        await using var tables = await OpenAsync(world);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Generated.AfterDecode = async () => { reached.TrySetResult(); await release.Task; };
        using var cancel = new CancellationTokenSource();
        var check = tables.CheckAsync(10, Now, cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, world.Counted.OpenStreams);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (world.Counted.OpenStreams != 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(0, world.Counted.OpenStreams);

        // A shared decode one caller leaves runs on for the other and closes the file too.
        world.Store.Clear();
        reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var leaving = new CancellationTokenSource();
        var left = tables.CheckAsync(10, Now, leaving.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var staying = tables.CheckAsync(10, Now, Ct);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        release.SetResult();
        Assert.Equal(TableState.Ready, (await staying).State);
        Assert.Equal(0, world.Counted.OpenStreams);
    }

    // Definitions

    [Fact]
    public async Task AHandleAsksWoWDBDefsAtMostOnce()
    {
        var source = new InMemoryObjectSource();
        // Neither definition covers the build's version line, so each check would ask for a newer one.
        var other = SyntheticTables.Dbd(Plain, build: "9.9.9.9");
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10), other), Table(11, Stored(11), other)], fetched: source);
        await using var tables = await OpenAsync(world);
        // With fetching on and nothing fetched yet, a local miss lists as not checked: a fetch may still find one.
        Assert.All(await ListAsync(tables), e => Assert.Equal(TableState.NotChecked, e.State));
        Assert.Empty(source.Observed);
        await tables.CheckAsync(10, Now, Ct);
        await tables.CheckAsync(11, Now, Ct);
        await tables.CheckAsync(10, Now, Ct);
        Assert.Equal(1, source.Observed.Count(o => o.Kind == ObservationKind.Definition));
    }

    [Fact]
    public async Task ABuildListedOnlyByLayoutReads()
    {
        // The definition lists another build of the version line; the file's layout picks its block (D080).
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10), SyntheticTables.Dbd(Plain, build: "1.2.3.1"))]);
        await using var tables = await OpenAsync(world);
        Assert.Equal(TableState.NotChecked, (await ListAsync(tables)).Single().State);
        var entry = await tables.CheckAsync(10, Now, Ct);
        Assert.Equal((TableState.Ready, 2L), (entry.State, entry.Counts!.ReadableRows));
        Assert.Equal(2, (await tables.PageAsync(new(10, Filters: [new("Value", TableFilterOp.Between, "0", "1000")]), Now, Ct)).Rows.Count);
    }

    [Fact]
    public void KeyedSectionsTheReaderWontReadPartiallyAreEncrypted() =>
        Assert.Equal(TableState.Encrypted, TableStates.Reader("DB2_ENCRYPTED"));

    // Follow and referenced-by

    /// <summary>T10 Plain; T11 and T12 point at T10 by ID through Ref. T11's definition covers this build only by layout when <paramref name="layoutOnly"/>.</summary>
    private static IReadOnlyList<SyntheticTable> RelationTables(bool layoutOnly = false)
    {
        IReadOnlyList<ScenarioColumn> pointing = [ScenarioColumn.Id(), ScenarioColumn.Int("Ref", foreignKey: new("T10", "ID"))];
        var rows = new ScenarioRow[] { new(1, ["1", "1"]), new(2, ["2", "2"]) };
        SyntheticTable Pointing(uint id, string dbd) =>
            new(id, $"T{id}", dbd, new FakeFile(id, Content: SyntheticTables.Wdc5(pointing, rows, null).Bytes, Storage: FileStorage.Stored));
        return [Table(10, Stored(10)), Pointing(11, SyntheticTables.Dbd(pointing, build: layoutOnly ? "1.2.3.1" : SyntheticTables.Version)),
            Pointing(12, SyntheticTables.Dbd(pointing))];
    }

    [Fact]
    public async Task ALayoutOnlyCandidateIsUnresolvedAndNeverOpened()
    {
        var world = await SyntheticTables.CreateAsync(Build, RelationTables(layoutOnly: true));
        await using var tables = await OpenAsync(world);
        var result = await tables.ReferencedByAsync(new(10, 1), Now, Ct);
        Assert.Equal((TableReferencesOutcome.Searched, 11u), (result.Outcome, result.Unresolved.Single().FileDataId));
        Assert.Equal(12u, result.Sources.Single().Table.FileDataId);
        Assert.Equal(new uint[] { 1 }, result.Sources[0].RowIds);
        // T10 and T12 were decoded; T11 never was.
        Assert.Equal(2, world.Generated.Decodes);
        // Once checked, it lists checked with its stored layout, which resolves the block: now a source.
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(11, Now, Ct)).State);
        var again = await tables.ReferencedByAsync(new(10, 1), Now, Ct);
        Assert.Empty(again.Unresolved);
        Assert.Equal([(11u, TableReferenceScope.Complete), (12u, TableReferenceScope.Complete)], again.Sources.Select(x => (x.Table.FileDataId, x.Scope)));
    }

    [Fact]
    public async Task AFollowTargetStoredByAnEarlierHandleIsNotDecodedAgain()
    {
        var world = await SyntheticTables.CreateAsync(Build, RelationTables());
        await using (var earlier = await OpenAsync(world)) await earlier.CheckAsync(10, Now, Ct);
        await using var tables = await OpenAsync(world);
        var follow = await tables.FollowAsync(new(12, 2, "Ref"), Now, Ct);
        Assert.Equal((TableFollowOutcome.Found, 2u, TableState.Ready), (follow.Outcome, follow.Rows.Single().Row.Id, follow.Target!.State));
        Assert.Equal(2, world.Generated.Decodes);
    }

    [Fact]
    public async Task ReferenceBoundsComeFromTheOptions()
    {
        var none = await SyntheticTables.CreateAsync(Build, RelationTables(), options: o => o with { MaxReferenceSources = 1 });
        await using (var tables = await OpenAsync(none))
        {
            var result = await tables.ReferencedByAsync(new(10, 1), Now, Ct);
            Assert.Equal([(TableReferenceScope.Complete, null), (TableReferenceScope.NotChecked, TableQueries.RelationSourceLimitCode)],
                result.Sources.Select(s => (s.Scope, s.Code)));
        }
        var budget = await SyntheticTables.CreateAsync(Build, RelationTables(), options: o => o with { MaxReferenceScanRows = 3 });
        await using (var tables = await OpenAsync(budget))
        {
            var result = await tables.ReferencedByAsync(new(10, 1), Now, Ct);
            Assert.Equal([(TableReferenceScope.Complete, null), (TableReferenceScope.NotChecked, TableQueries.RelationBudgetCode)],
                result.Sources.Select(s => (s.Scope, s.Code)));
        }
    }

    [Fact]
    public async Task StoreDamageWhileScanningIsUnknownNotAThrow()
    {
        var world = await SyntheticTables.CreateAsync(Build, RelationTables());
        await using var tables = await OpenAsync(world);
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(11, Now, Ct)).State);
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(12, Now, Ct)).State);
        // The source row is read, then every source table opens and its rows are damaged before the scan.
        var opens = 0;
        world.Generated.AfterOpen = async () => { if (opens++ >= 2) await DamageAsync(world); };
        var result = await tables.ReferencedByAsync(new(10, 1), Now, Ct);
        Assert.Equal(TableReferencesOutcome.Searched, result.Outcome);
        Assert.Equal(TableState.StoreFailed, result.Unresolved.Single().State);
        world.Generated.AfterOpen = null;
        Assert.Equal(2, (await tables.ReferencedByAsync(new(10, 1), Now, Ct)).Sources.Count(s => s.RowIds.Count == 1));
    }

    [Fact]
    public async Task StoreDamageWhileAFollowScansKeepsTheReference()
    {
        IReadOnlyList<ScenarioColumn> pointing = [ScenarioColumn.Id(), ScenarioColumn.Int("Ref", foreignKey: new("T20", "Value"))];
        var world = await SyntheticTables.CreateAsync(Build,
        [
            Table(20, Stored(20)),
            new(21, "T21", SyntheticTables.Dbd(pointing),
                new FakeFile(21, Content: SyntheticTables.Wdc5(pointing, [new(1, ["1", "256"])], null).Bytes, Storage: FileStorage.Stored)),
        ]);
        await using var tables = await OpenAsync(world);
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(20, Now, Ct)).State);
        var target = (await GeneratedFixtures.ClassifyAsync(world.Store)).Where(k => k.Value == GeneratedFixtures.Kind.Segment).Select(k => k.Key).ToList();
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(21, Now, Ct)).State);
        // The source row is read and its table opened, then the target opens and its rows are damaged before the scan.
        var opens = 0;
        world.Generated.AfterOpen = () =>
        {
            if (opens++ == 2) foreach (var key in target) world.Store.Damage(key);
            return Task.CompletedTask;
        };
        var result = await tables.FollowAsync(new(21, 1, "Ref"), Now, Ct);
        world.Generated.AfterOpen = null;
        Assert.Equal(TableFollowOutcome.Unknown, result.Outcome);
        Assert.Equal(new ForeignKey("T20", "Value"), result.Reference);
        Assert.Equal((20u, TableState.StoreFailed), (result.Target!.FileDataId, result.Target.State));
        Assert.Equal(TableRowOutcome.Found, result.Source.Outcome);
        // Read again, the target is rebuilt and the scan finds its row.
        var again = await tables.FollowAsync(new(21, 1, "Ref"), Now, Ct);
        Assert.Equal((TableFollowOutcome.Found, 2u), (again.Outcome, again.Rows.Single().Row.Id));
    }

    // Store damage

    [Fact]
    public async Task StoreDamageWhileReadingIsStoreFailedThenRebuilds()
    {
        var world = await SyntheticTables.CreateAsync(Build, [Table(10, Stored(10))]);
        await using var tables = await OpenAsync(world);
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(10, Now, Ct)).State);
        // Damage the rows once the table has opened, so the read finds it.
        world.Generated.AfterOpen = () => DamageAsync(world);
        var page = await tables.PageAsync(new(10), Now, Ct);
        Assert.Equal((TablePageState.NotReadable, TableState.StoreFailed), (page.State, page.Table.State));
        Assert.NotNull(page.Table.Reason!.Code);
        var row = await tables.GetAsync(10, 1, Now, Ct);
        Assert.Equal((TableRowOutcome.Unknown, TableState.StoreFailed), (row.Outcome, row.Table.State));
        Assert.NotNull(row.Table.Reason!.Code);
        world.Generated.AfterOpen = null;
        Assert.Equal(TableState.Ready, (await tables.CheckAsync(10, Now, Ct)).State);
        Assert.Equal(TableRowOutcome.Found, (await tables.GetAsync(10, 1, Now, Ct)).Outcome);
    }

    private static async Task DamageAsync(SyntheticWorld world)
    {
        foreach (var piece in (await GeneratedFixtures.ClassifyAsync(world.Store)).Where(k => k.Value == GeneratedFixtures.Kind.Segment)) world.Store.Damage(piece.Key);
    }

    [Fact]
    public void OptionsRefuseNullAndANonPositiveScan()
    {
        var options = new TablesOptions((_, _) => Task.FromResult<string?>(null));
        Assert.Throws<ArgumentNullException>(() => options with { BuildVersion = null! });
        Assert.Throws<ArgumentNullException>(() => options with { Limits = null! });
        Assert.Throws<ArgumentNullException>(() => options with { Keys = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { MaxScanRows = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { MaxReferenceSources = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { MaxReferenceScanRows = -1 });
        Assert.Equal((TableQueries.DefaultMaxReferenceSources, TableQueries.DefaultMaxReferenceScanRows),
            (options.MaxReferenceSources, options.MaxReferenceScanRows));
        Assert.Throws<ArgumentNullException>(() => new TablesOptions(null!));
    }
}
