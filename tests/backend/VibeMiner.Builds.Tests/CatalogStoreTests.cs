using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class CatalogStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeminer-catalog-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(dir, "nested", "catalog.sqlite3");
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, 123, TimeSpan.Zero);
    private static CancellationToken Ct => CancellationToken.None;

    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    private static string Hex(char c) => new(c, 32);
    private static ObjectKey Key(ObjectKeyType type, int n) => ObjectKey.Parse(type, n.ToString("x32", System.Globalization.CultureInfo.InvariantCulture));

    private static HistoryEntry Entry(int n, string region, DateTimeOffset at, string product = "wow") =>
        new(HistoryKind.Versions, product, region, at, $"https://{region}.example.invalid/{product}/versions",
            Key(ObjectKeyType.HistoryDocument, n), Key(ObjectKeyType.HistoryRecord, n));

    private static BuildIdentity Identity(string name, char build, string product = "wow", string? productConfig = null)
    {
        var fields = new Dictionary<string, string> { ["VersionsName"] = name, ["BuildConfig"] = Hex(build), ["BuildId"] = name.Split('.')[^1] };
        if (productConfig is not null) fields["ProductConfig"] = productConfig;
        return BuildIdentity.FromFields(product, fields);
    }

    private static ProjectedEntry Projected(HistoryEntry entry, params (BuildIdentity Id, string Region, string Cdn)[] rows) =>
        Seq(entry, "7", rows);

    private static ProjectedEntry Seq(HistoryEntry entry, string? seqn, params (BuildIdentity Id, string Region, string Cdn)[] rows) =>
        new(entry, new VersionsRead(rows.Select((r, i) => new Sighting(r.Id, r.Region, r.Cdn, i)).ToList(), [], seqn));

    private static async Task<IReadOnlyDictionary<string, bool>> CurrentAsync(CatalogStore store) =>
        (await store.ListAsync("wow", Ct)).Builds.ToDictionary(b => b.VersionsName, b => b.Current);

    private async Task ExecAsync(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        await c.OpenAsync(Ct);
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task AssertRefusedUnchangedAsync(string code)
    {
        var bytes = await File.ReadAllBytesAsync(DbPath, Ct);
        var e = await Assert.ThrowsAsync<CatalogStoreException>(() => CatalogStore.OpenAsync(DbPath, Ct));
        Assert.Equal(code, e.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(DbPath, Ct));
    }

    [Fact]
    public async Task CreatesThenReopensWithRows()
    {
        var build = Identity("11.0.0.1", 'a');
        await using (var store = await CatalogStore.OpenAsync(DbPath, Ct))
            await store.AddAsync(Projected(Entry(1, "us", T0), (build, "us", Hex('c'))), Ct);
        Assert.True(File.Exists(DbPath));
        await using var reopened = await CatalogStore.OpenAsync(DbPath, Ct);
        Assert.True(await reopened.HasAsync(Key(ObjectKeyType.HistoryRecord, 1), Ct));
        Assert.False(await reopened.HasAsync(Key(ObjectKeyType.HistoryRecord, 2), Ct));
        Assert.Equal(build.Id, Assert.Single((await reopened.ListAsync("wow", Ct)).Builds).Id);

        using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(Ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", await cmd.ExecuteScalarAsync(Ct));
        cmd.CommandText = "PRAGMA user_version"; Assert.Equal(1L, await cmd.ExecuteScalarAsync(Ct));
        cmd.CommandText = "PRAGMA application_id"; Assert.Equal((long)CatalogStore.ApplicationId, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task AddingTheSameRecordAgainChangesNothing()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        var entry = Entry(1, "us", T0);
        await store.AddAsync(Projected(entry, (Identity("11.0.0.1", 'a'), "us", Hex('c'))), Ct);
        var before = await store.ListAsync("wow", Ct);
        // Same record, different content: the first add stands.
        await store.AddAsync(Projected(entry, (Identity("11.0.0.2", 'b'), "eu", Hex('d'))), Ct);
        var after = await store.ListAsync("wow", Ct);
        var b = Assert.Single(after.Builds);
        Assert.Equal(Assert.Single(before.Builds).Id, b.Id);
        Assert.Equal("us", Assert.Single(b.Regions).Region);
    }

    [Fact]
    public async Task NewestRoundTripsTheEntry()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        Assert.Null(await store.NewestAsync(Ct));
        var older = Entry(9, "us", T0);
        var newer = new HistoryEntry(HistoryKind.Versions, "wow", "eu", T0.AddMinutes(5).AddTicks(4321),
            "https://eu.example.invalid/wow/versions?x=1", ObjectKey.Parse(ObjectKeyType.HistoryDocument, Hex('e')),
            ObjectKey.Parse(ObjectKeyType.HistoryRecord, Hex('f')));
        await store.AddAsync(Projected(newer), Ct);
        await store.AddAsync(Projected(older), Ct);
        var got = await store.NewestAsync(Ct);
        Assert.Equal(newer with { ObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(newer.ObservedAt.ToUnixTimeMilliseconds()) }, got);
        Assert.Equal(TimeSpan.Zero, got!.ObservedAt.Offset);

        // Same time: the larger record wins.
        await store.AddAsync(Projected(Entry(10, "us", newer.ObservedAt)), Ct);
        Assert.Equal(ObjectKey.Parse(ObjectKeyType.HistoryRecord, Hex('f')), (await store.NewestAsync(Ct))!.Record);
    }

    [Fact]
    public async Task ListsBuildsRegionsAndCurrentFlags()
    {
        var old = Identity("11.0.0.1", 'a');
        var live = Identity("11.0.0.2", 'b', productConfig: Hex('9'));
        var (c1, c2, c3) = (Hex('1'), Hex('2'), Hex('3'));
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        // Host us: old build, then live with a CDN config republished later.
        await store.AddAsync(Projected(Entry(1, "us", T0), (old, "us", c1), (old, "eu", c1)), Ct);
        await store.AddAsync(Projected(Entry(2, "us", T0.AddHours(1)), (live, "us", c2), (live, "eu", c2)), Ct);
        await store.AddAsync(Projected(Entry(3, "us", T0.AddHours(2)), (live, "us", c3), (live, "eu", c2)), Ct);
        // Host eu last saw live with the old CDN config on us.
        await store.AddAsync(Projected(Entry(4, "eu", T0.AddMinutes(90)), (live, "us", c2), (live, "eu", c2)), Ct);

        var list = await store.ListAsync("wow", Ct);
        Assert.Equal("wow", list.Product);
        Assert.Equal(T0.AddHours(2), list.LastObservedAt);
        Assert.Empty(list.Problems);
        Assert.Equal([live.Id, old.Id], list.Builds.Select(b => b.Id));

        var l = list.Builds[0];
        Assert.Equal(("11.0.0.2", Hex('b'), Hex('9')), (l.VersionsName, l.BuildConfig, l.ProductConfig));
        Assert.Equal(live.Fields, l.Fields);
        Assert.True(l.Current);
        Assert.Equal((T0.AddHours(1), T0.AddHours(2)), (l.FirstSeen, l.LastSeen));
        Assert.Equal(
            [("eu", c2, T0.AddHours(1), T0.AddHours(2), true, Key(ObjectKeyType.HistoryRecord, 3)),
             ("us", c2, T0.AddHours(1), T0.AddMinutes(90), true, Key(ObjectKeyType.HistoryRecord, 4)),
             ("us", c3, T0.AddHours(2), T0.AddHours(2), true, Key(ObjectKeyType.HistoryRecord, 3))],
            l.Regions.Select(r => (r.Region, r.CdnConfig, r.FirstSeen, r.LastSeen, r.Current, r.LastRecord)));

        var o = list.Builds[1];
        Assert.Null(o.ProductConfig);
        Assert.False(o.Current);
        Assert.All(o.Regions, r => Assert.False(r.Current));
        Assert.Equal(["eu", "us"], o.Regions.Select(r => r.Region));
        Assert.Equal((T0, T0), (o.FirstSeen, o.LastSeen));
    }

    [Fact]
    public async Task OtherProductsAreNotVisible()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        await store.AddAsync(Projected(Entry(1, "us", T0), (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        await store.AddAsync(Projected(Entry(2, "us", T0.AddHours(1), "wowt"), (Identity("11.0.0.9", 'b', "wowt"), "us", Hex('2'))), Ct);
        var wow = await store.ListAsync("wow", Ct);
        Assert.Equal("11.0.0.1", Assert.Single(wow.Builds).VersionsName);
        Assert.Equal(T0, wow.LastObservedAt);
        var wowt = await store.ListAsync("wowt", Ct);
        Assert.Equal("wowt", Assert.Single(wowt.Builds).Product);
        var empty = await store.ListAsync("wow_beta", Ct);
        Assert.Empty(empty.Builds);
        Assert.Null(empty.LastObservedAt);
    }

    [Fact]
    public async Task ListsProblemsNewestEntryFirstThenRowOrder()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        var later = Entry(1, "us", T0.AddHours(1));
        var earlier = Entry(2, "eu", T0);
        await store.AddAsync(new(later, new([], [new("ROW_BAD", "b", later.Record, 3), new("ROW_BAD", "a", later.Record, 1)], null)), Ct);
        await store.AddAsync(new(earlier, new([], [new("REPLY_UNREADABLE", "whole", earlier.Record)], null)), Ct);
        await store.AddAsync(new(Entry(3, "us", T0, "wowt"), new([], [new("OTHER", "x")], null)), Ct);
        var problems = (await store.ListAsync("wow", Ct)).Problems;
        Assert.Equal(
            [new CatalogProblem("ROW_BAD", "a", later.Record, 1), new("ROW_BAD", "b", later.Record, 3), new("REPLY_UNREADABLE", "whole", earlier.Record)],
            problems);
    }

    [Fact]
    public async Task ProblemsAreCapped()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        var entry = Entry(1, "us", T0);
        await store.AddAsync(new(entry, new([], Enumerable.Range(0, CatalogStore.MaxProblems + 5)
            .Select(i => new CatalogProblem("ROW_BAD", "x", entry.Record, i)).ToList(), null)), Ct);
        var problems = (await store.ListAsync("wow", Ct)).Problems;
        Assert.Equal(CatalogStore.MaxProblems, problems.Count);
        Assert.Equal(Enumerable.Range(0, CatalogStore.MaxProblems), problems.Select(p => p.Row!.Value));
    }

    [Fact]
    public async Task OnlyVersionsEntriesAreAddedOrListed()
    {
        await using (var store = await CatalogStore.OpenAsync(DbPath, Ct))
        {
            var cdns = Entry(1, "us", T0) with { Kind = HistoryKind.Cdns };
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Projected(cdns), Ct));
            Assert.False(await store.HasAsync(cdns.Record, Ct));
            await store.AddAsync(Projected(Entry(2, "us", T0), (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        }
        // A row of another kind written behind the store's back still isn't listed.
        await ExecAsync($"""
            INSERT INTO entries(record, record_type, record_ns, kind, product, region, observed_at, url, document, document_type, document_ns, seqn, readable)
            SELECT randomblob(16), record_type, record_ns, {(int)HistoryKind.Cdns}, product, region, observed_at + 3600000, url, document, document_type,
                document_ns, 99, 1 FROM entries;
            INSERT INTO sightings(entry, row, build, region, cdn_config) SELECT (SELECT MAX(id) FROM entries), 0, build, region, '{Hex('2')}' FROM sightings;
            INSERT INTO problems(entry, ordinal, code, message) SELECT MAX(id), 0, 'X', 'x' FROM entries;
            """);
        await using var reopened = await CatalogStore.OpenAsync(DbPath, Ct);
        var list = await reopened.ListAsync("wow", Ct);
        Assert.Equal(T0, list.LastObservedAt);
        Assert.Empty(list.Problems);
        var b = Assert.Single(list.Builds);
        Assert.Equal(Hex('1'), Assert.Single(b.Regions).CdnConfig);
        Assert.True(b.Current);
    }

    [Fact]
    public async Task AStaleLowerSeqnObservedLaterDoesNotTakeCurrent()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        await store.AddAsync(Seq(Entry(1, "us", T0), "10", (Identity("11.0.0.2", 'b'), "us", Hex('1'))), Ct);
        await store.AddAsync(Seq(Entry(2, "us", T0.AddHours(1)), "9", (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        Assert.Equal(new Dictionary<string, bool> { ["11.0.0.2"] = true, ["11.0.0.1"] = false }, await CurrentAsync(store));
        // Same seqn: the later one wins.
        await store.AddAsync(Seq(Entry(3, "us", T0.AddHours(2)), "10", (Identity("11.0.0.3", 'c'), "us", Hex('1'))), Ct);
        Assert.Equal(new Dictionary<string, bool> { ["11.0.0.3"] = true, ["11.0.0.2"] = false, ["11.0.0.1"] = false }, await CurrentAsync(store));
    }

    [Theory]
    [InlineData("11")]
    [InlineData(null)]
    public async Task AnUnreadableNewestReplyDoesNotClearCurrent(string? seqn)
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        await store.AddAsync(Seq(Entry(1, "us", T0), "10", (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        var bad = Entry(2, "us", T0.AddHours(1));
        await store.AddAsync(new(bad, new([], [new("VERSIONS_COLUMNS_MISSING", "x", bad.Record)], seqn)), Ct);
        var list = await store.ListAsync("wow", Ct);
        Assert.True(Assert.Single(list.Builds).Current);
        Assert.Equal(bad.ObservedAt, list.LastObservedAt);
    }

    [Fact]
    public async Task EntriesWithoutSeqnCountOnlyWhenTheHostHasNone()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        // Host us: a numbered reply beats a later unnumbered one, and a non-numeric seqn counts as none.
        await store.AddAsync(Seq(Entry(1, "us", T0), "5", (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        await store.AddAsync(Seq(Entry(2, "us", T0.AddHours(1)), null, (Identity("11.0.0.2", 'b'), "us", Hex('1'))), Ct);
        await store.AddAsync(Seq(Entry(3, "us", T0.AddHours(2)), "x9", (Identity("11.0.0.3", 'c'), "us", Hex('1'))), Ct);
        // Host eu: none numbered, so the latest observed counts.
        await store.AddAsync(Seq(Entry(4, "eu", T0.AddHours(3)), null, (Identity("11.0.0.4", 'd'), "eu", Hex('1'))), Ct);
        await store.AddAsync(Seq(Entry(5, "eu", T0.AddHours(2)), null, (Identity("11.0.0.5", 'e'), "eu", Hex('1'))), Ct);
        Assert.Equal(new Dictionary<string, bool>
        {
            ["11.0.0.1"] = true, ["11.0.0.2"] = false, ["11.0.0.3"] = false, ["11.0.0.4"] = true, ["11.0.0.5"] = false,
        }, await CurrentAsync(store));
    }

    [Fact]
    public async Task ConcurrentOpenersOfANewFileAllSucceed()
    {
        for (var round = 0; round < 5; round++)
        {
            var path = Path.Combine(dir, $"race{round}", "catalog.sqlite3");
            var stores = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => CatalogStore.OpenAsync(path, Ct))));
            foreach (var store in stores) await store.DisposeAsync();
            await using var again = await CatalogStore.OpenAsync(path, Ct);
            Assert.Empty((await again.ListAsync("wow", Ct)).Builds);
        }
    }

    [Fact]
    public async Task RefusesANewerFileAndLeavesItUnchanged()
    {
        await using (var store = await CatalogStore.OpenAsync(DbPath, Ct))
            await store.AddAsync(Projected(Entry(1, "us", T0), (Identity("11.0.0.1", 'a'), "us", Hex('1'))), Ct);
        await ExecAsync("PRAGMA user_version=2");
        var wal = DbPath + "-wal";
        var walBytes = File.Exists(wal) ? await File.ReadAllBytesAsync(wal, Ct) : null;
        await AssertRefusedUnchangedAsync("CATALOG_NEWER");
        if (walBytes is not null) Assert.Equal(walBytes, await File.ReadAllBytesAsync(wal, Ct));

        using var c = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly;Pooling=False");
        await c.OpenAsync(Ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode"; Assert.Equal("wal", await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task RefusesAVersion1FileMissingATable()
    {
        await using (await CatalogStore.OpenAsync(DbPath, Ct)) { }
        await ExecAsync("DROP TABLE problems");
        await AssertRefusedUnchangedAsync("CATALOG_UNREADABLE");
    }

    [Fact]
    public async Task RefusesAVersion0FileWithTheApplicationId()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await ExecAsync($"PRAGMA application_id={CatalogStore.ApplicationId}; CREATE TABLE entries(x)");
        await AssertRefusedUnchangedAsync("CATALOG_UNREADABLE");
    }

    [Fact]
    public async Task RefusesAGarbageFileAndLeavesItUnchanged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 7)).ToArray();
        await File.WriteAllBytesAsync(DbPath, bytes, Ct);
        await AssertRefusedUnchangedAsync("CATALOG_UNREADABLE");
    }

    [Fact]
    public async Task RefusesAnotherSqliteDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        await ExecAsync("CREATE TABLE other(x)");
        await AssertRefusedUnchangedAsync("CATALOG_UNREADABLE");
    }

    [Fact]
    public async Task ACancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CatalogStore.OpenAsync(DbPath, cts.Token));
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddAsync(Projected(Entry(1, "us", T0)), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.HasAsync(Key(ObjectKeyType.HistoryRecord, 1), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.NewestAsync(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListAsync("wow", cts.Token));
        Assert.False(await store.HasAsync(Key(ObjectKeyType.HistoryRecord, 1), Ct));
    }

    [Fact]
    public async Task AnAddCancelledMidwayLeavesNothing()
    {
        await using var store = await CatalogStore.OpenAsync(DbPath, Ct);
        using var cts = new CancellationTokenSource();
        var entry = Entry(1, "us", T0);
        var sightings = new CancellingList(cts, [
            new Sighting(Identity("11.0.0.1", 'a'), "us", Hex('1'), 0), new Sighting(Identity("11.0.0.1", 'a'), "eu", Hex('1'), 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AddAsync(new(entry, new(sightings, [], "7")), cts.Token));
        Assert.False(await store.HasAsync(entry.Record, Ct));
        Assert.Empty((await store.ListAsync("wow", Ct)).Builds);
    }

    /// <summary>Cancels its token once the first item has been handed out.</summary>
    private sealed class CancellingList(CancellationTokenSource cts, IReadOnlyList<Sighting> items) : IReadOnlyList<Sighting>
    {
        public Sighting this[int index] => items[index];
        public int Count => items.Count;
        public IEnumerator<Sighting> GetEnumerator()
        {
            for (var i = 0; i < items.Count; i++)
            {
                yield return items[i];
                cts.Cancel();
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
