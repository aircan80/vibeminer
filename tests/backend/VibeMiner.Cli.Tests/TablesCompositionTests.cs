using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Generated;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>
/// The tables composition root over a fake store handle (the in-memory layer 2 source, nothing stored, no network) and a real
/// generated store in a temporary data folder that the test deletes.
/// </summary>
public sealed class TablesCompositionTests : IDisposable
{
    private readonly string data = Path.Combine(Path.GetTempPath(), "vibeminer-cli-tables-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStores stores = new();

    public void Dispose()
    {
        if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
    }

    [Theory]
    [InlineData("WOW-63796patch11.2.5_Retail", "11.2.5.63796")]
    [InlineData("WOW-58123patch11.0.7_PTR", "11.0.7.58123")]
    [InlineData("WOW-1000patch1.0.0", "1.0.0.1000")]
    [InlineData("WOW-1patch1.0_Test", null)]
    [InlineData("11.2.5.63796", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void BuildNamesGiveTheFourPartVersion(string? name, string? version) => Assert.Equal(version, TablesComposition.VersionOf(name));

    [Fact]
    public async Task OpensTheGeneratedStoreAndClosesItInOrder()
    {
        var order = new List<string>();
        var handle = new ProbeHandle(await stores.OpenReadOnlyAsync("/data/vm/objects", default),
            async () => order.Add(await GeneratedStoreIsFreeAsync() ? "store after generated" : "store before generated"));
        var lease = await TablesComposition.LeaseAsync(handle, data, fetchDefinitions: false, default);
        Assert.True(Directory.Exists(TablesComposition.GeneratedFolder(data)));
        Assert.EndsWith(GeneratedStore.DefaultFolderName, TablesComposition.GeneratedFolder(data), StringComparison.Ordinal);

        // A build nothing stores: the keyless open says why, no keys are kept, and nothing throws.
        var build = new BuildRef("wow", new string('a', 32), new string('b', 32), "us");
        var opened = await lease.Tables.ForAsync(build, "enUS", default);
        Assert.NotEqual(FilesOpenState.Open, opened.State);
        Assert.Null(((TablesComposition)lease.Tables).KeysOf(build));

        // The generated store is the one writer while the lease is open.
        var busy = await Assert.ThrowsAsync<GeneratedStoreException>(() => GeneratedStore.OpenAsync(TablesComposition.GeneratedFolder(data)));
        Assert.Equal(GeneratedStoreCodes.Busy, busy.Code);

        await lease.DisposeAsync();
        Assert.Equal(["store after generated"], order);
        Assert.Equal(0, stores.Open);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.Tables.ForAsync(build, "enUS", default));
    }

    /// <summary>Whether the generated store's lock is free (it was closed before the store handle).</summary>
    private async Task<bool> GeneratedStoreIsFreeAsync()
    {
        try
        {
            await (await GeneratedStore.OpenAsync(TablesComposition.GeneratedFolder(data))).DisposeAsync();
            return true;
        }
        catch (GeneratedStoreException) { return false; }
    }

    /// <summary>A store handle that runs <paramref name="beforeClose"/> as it is disposed, before the real one closes.</summary>
    private sealed class ProbeHandle(IStoreHandle inner, Func<Task> beforeClose) : IStoreHandle
    {
        public bool Writer => inner.Writer;
        public VibeMiner.ObjectAccess.IObjectSource Objects => inner.Objects;
        public Task<ICatalog> CatalogAsync(CancellationToken ct) => inner.CatalogAsync(ct);
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => inner.StatsAsync(ct);
        public Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct) => inner.ScrubAsync(progress, ct);
        public async ValueTask DisposeAsync()
        {
            await beforeClose();
            await inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task ABusyGeneratedStoreLeavesTheHandleWithTheCaller()
    {
        await using var holder = await GeneratedStore.OpenAsync(TablesComposition.GeneratedFolder(data));
        var handle = await stores.OpenReadOnlyAsync("/data/vm/objects", default);
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => TablesComposition.LeaseAsync(handle, data, false, default));
        Assert.Equal(GeneratedStoreCodes.Busy, e.Code);
        Assert.Equal(1, stores.Open);
        await handle.DisposeAsync();
        Assert.Contains("generated store", BrowseSession.Explain(e), StringComparison.Ordinal);
    }

    private static readonly BuildRef X = new("wow", new string('a', 32), new string('b', 32), "us");
    private static readonly BuildRef Y = new("wow", new string('c', 32), new string('b', 32), "us");
    private const string KeyName = "ABCDEF0123456789";
    private static readonly byte[] KeyBytes = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

    private static string S(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The build's own TactKey and TactKeyLookup tables: one key, ID 7, named <see cref="KeyName"/> (TACTID read little-endian).</summary>
    private static FakeTable[] KeyTables()
    {
        Db2Column[] keyColumns = [new("ID", "integer", true), .. Enumerable.Range(0, 16).Select(i => new Db2Column($"Key[{i}]", "integer", false))];
        Db2Column[] lookupColumns = [new("ID", "integer", true), .. Enumerable.Range(0, 8).Select(i => new Db2Column($"TACTID[{i}]", "integer", false))];
        var name = BitConverter.GetBytes(0xABCDEF0123456789UL);
        return
        [
            new(BuildTactKeys.TactKeyFileDataId, BuildTactKeys.TactKeyName, TableState.Ready, keyColumns,
                [new Db2Row(7, false, null, [S(7), .. KeyBytes.Select(b => S(b))])]),
            new(BuildTactKeys.TactKeyLookupFileDataId, BuildTactKeys.TactKeyLookupName, TableState.Ready, lookupColumns,
                [new Db2Row(7, false, null, [S(7), .. name.Select(b => S(b))])]),
        ];
    }

    /// <summary>One open as the composition asked for it: layer 4's options and the tables options.</summary>
    private sealed record Made(FileSystemOptions Files, TablesOptions Options)
    {
        public bool Keyless => ReferenceEquals(Files.Keys, TactKeys.None);
    }

    /// <summary>A composition whose keyless opens go to <paramref name="bootstrap"/> and keyed ones to <paramref name="served"/>.</summary>
    private static TablesComposition Compose(ITables bootstrap, ITables served, List<Made> made) =>
        new(new InMemoryObjectSource(), null, (_, files, options) =>
        {
            var m = new Made(files, options);
            lock (made) made.Add(m);
            return m.Keyless ? bootstrap : served;
        });

    private static string Fingerprint => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(KeyBytes));
    private const ulong Name = 0xABCDEF0123456789UL;

    [Fact]
    public async Task EachBuildIsReadWithItsOwnKeysOnly()
    {
        var bootstrap = new InMemoryTables().Add(X, "enUS", KeyTables()).Add(Y, "enUS", []);
        var served = new InMemoryTables().Add(X, "enUS", []).Add(Y, "enUS", []);
        var made = new List<Made>();
        await using var composition = Compose(bootstrap, served, made);

        var x = await composition.ForAsync(X, "enUS", default);
        Assert.Equal(FilesOpenState.Open, x.State);
        await x.Tables!.DisposeAsync();
        Assert.Equal(2, made.Count);
        Assert.True(made[0].Keyless);
        Assert.Null(made[0].Options.Keys(X).FingerprintOf(KeyName));
        var forX = made[1];
        Assert.Equal([KeyName], composition.KeysOf(X)!.Names);
        Assert.Equal(KeyBytes, forX.Files.Keys.Find(Name)!.Value.ToArray());
        Assert.Equal(Fingerprint, forX.Options.Keys(X).FingerprintOf(KeyName));
        // That open's options never give X's key to another build.
        Assert.Null(forX.Options.Keys(Y).FingerprintOf(KeyName));

        // Y ships no key tables: an empty set (complete, so kept), and X's key doesn't reach it.
        await (await composition.ForAsync(Y, "enUS", default)).Tables!.DisposeAsync();
        var forY = made[^1];
        Assert.False(forY.Keyless);
        Assert.Equal(0, composition.KeysOf(Y)!.Count);
        Assert.Null(forY.Files.Keys.Find(Name));
        Assert.Null(forY.Options.Keys(Y).FingerprintOf(KeyName));

        // A second open of X reuses the kept set: nothing is paged again.
        var paged = bootstrap.Calls(nameof(IBuildTables.PageAsync));
        await (await composition.ForAsync(X, "enUS", default)).Tables!.DisposeAsync();
        Assert.Equal(paged, bootstrap.Calls(nameof(IBuildTables.PageAsync)));
        Assert.Equal(2, bootstrap.Calls(nameof(ITables.ForAsync)));
        Assert.Equal(3, served.Calls(nameof(ITables.ForAsync)));
    }

    [Fact]
    public async Task AnOpenHandleKeepsItsSnapshotWhenTheSetIsEvicted()
    {
        var others = Enumerable.Range(1, TablesComposition.MaxKeyedBuilds).Select(i => new BuildRef("wow", S(i).PadLeft(32, '0'), new string('b', 32), "us")).ToArray();
        var bootstrap = new InMemoryTables().Add(X, "enUS", KeyTables());
        var served = new InMemoryTables().Add(X, "enUS", []);
        foreach (var b in others) { bootstrap.Add(b, "enUS", []); served.Add(b, "enUS", []); }
        var made = new List<Made>();
        await using var composition = Compose(bootstrap, served, made);

        var held = await composition.ForAsync(X, "enUS", default);
        var forX = made[^1];
        foreach (var b in others) await (await composition.ForAsync(b, "enUS", default)).Tables!.DisposeAsync();
        Assert.Null(composition.KeysOf(X));
        Assert.All(others, b => Assert.NotNull(composition.KeysOf(b)));

        // The handle opened before the eviction still decrypts and files with the same set.
        Assert.Equal(KeyBytes, forX.Files.Keys.Find(Name)!.Value.ToArray());
        Assert.Equal(Fingerprint, forX.Options.Keys(X).FingerprintOf(KeyName));
        await held.Tables!.DisposeAsync();

        // The next open of X reads its keys again, into a new snapshot with the same key.
        var keyless = made.Count(m => m.Keyless);
        await (await composition.ForAsync(X, "enUS", default)).Tables!.DisposeAsync();
        Assert.Equal(keyless + 1, made.Count(m => m.Keyless));
        Assert.Equal(Fingerprint, made[^1].Options.Keys(X).FingerprintOf(KeyName));
        Assert.NotSame(forX.Files.Keys, made[^1].Files.Keys);
    }

    [Fact]
    public async Task AFailedKeylessOpenIsTheAnswerAndIsRetried()
    {
        var bootstrap = new InMemoryTables();
        var made = new List<Made>();
        await using var composition = Compose(bootstrap, new InMemoryTables(), made);
        Assert.Equal(FilesOpenState.NeedsNetwork, (await composition.ForAsync(X, "enUS", default)).State);
        Assert.Null(composition.KeysOf(X));
        Assert.Single(made);
        bootstrap.Add(X, "enUS", KeyTables());
        Assert.Equal(FilesOpenState.NeedsNetwork, (await composition.ForAsync(X, "enUS", default)).State); // the served side has no X
        Assert.Equal(1, composition.KeysOf(X)!.Count);
    }

    [Fact]
    public async Task AnIncompleteLoadServesItsOpenAndIsReadAgain()
    {
        // TactKey isn't stored and the CDN answered 404: the load is empty and may change, so it isn't kept.
        var tables = KeyTables();
        var bootstrap = new InMemoryTables().Add(X, "enUS", [tables[0] with { State = TableState.Missing, Columns = null, Rows = null }, tables[1]]);
        var served = new InMemoryTables().Add(X, "enUS", []);
        var made = new List<Made>();
        await using var composition = Compose(bootstrap, served, made);

        Assert.Equal(FilesOpenState.Open, (await composition.ForAsync(X, "enUS", default)).State);
        Assert.Null(composition.KeysOf(X));
        Assert.Null(made[^1].Files.Keys.Find(Name));
        Assert.False(made[^1].Keyless);

        // Stale twice in a row: not kept either.
        var stale = new StaleKeys(new InMemoryTables().Add(X, "enUS", KeyTables()));
        var made2 = new List<Made>();
        await using var again = Compose(stale, served, made2);
        await (await again.ForAsync(X, "enUS", default)).Tables!.DisposeAsync();
        Assert.Null(again.KeysOf(X));
        Assert.Equal(BuildTactKeyTableOutcome.Stale, stale.Last!.KeyTable.Outcome);

        // Once the build answers fully, the next open keeps the set.
        stale.Stale = false;
        await (await again.ForAsync(X, "enUS", default)).Tables!.DisposeAsync();
        Assert.Equal([KeyName], again.KeysOf(X)!.Names);
        Assert.Equal(2, made2.Count(m => m.Keyless));
    }

    [Fact]
    public void CompleteMeansSettledAndNotStale()
    {
        TableEntry Entry(TableState state) => new(1, "T", state, true, null, null, null);
        foreach (var state in new[] { TableState.NotChecked, TableState.NotStored, TableState.NeedsNetwork, TableState.Missing, TableState.Unavailable, TableState.StoreFailed })
            Assert.False(TablesComposition.Settled(new BuildTactKeyTable(Entry(state), BuildTactKeyTableOutcome.NotReadable)), state.ToString());
        foreach (var state in new[] { TableState.Ready, TableState.Partial, TableState.NotInBuild, TableState.NoDefinition, TableState.Encrypted, TableState.Corrupt })
            Assert.True(TablesComposition.Settled(new BuildTactKeyTable(Entry(state), BuildTactKeyTableOutcome.NotReadable)), state.ToString());
        Assert.False(TablesComposition.Settled(new BuildTactKeyTable(Entry(TableState.Ready), BuildTactKeyTableOutcome.Stale)));
    }

    /// <summary>Pages the TactKey table as Stale while <see cref="Stale"/> is set; records the last set a load gave via a real load.</summary>
    private sealed class StaleKeys(ITables inner) : ITables
    {
        public bool Stale { get; set; } = true;
        public BuildTactKeySet? Last { get; private set; }

        public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var opened = await inner.ForAsync(build, locale, cancellationToken);
            if (opened.Tables is not { } tables) return opened;
            var wrapped = new Handle(this, tables);
            // Record what a load over this handle gives, as the composition will see it.
            Last = await BuildTactKeys.LoadAsync(wrapped, TablePriority.Interactive, cancellationToken);
            return opened with { Tables = wrapped };
        }

        private sealed class Handle(StaleKeys owner, IBuildTables inner) : IBuildTables
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken ct) => inner.ListAsync(filter, priority, ct);
            public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken ct) => inner.CheckAsync(table, priority, ct);
            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken ct) => inner.SchemaAsync(table, priority, ct);
            public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken ct)
            {
                var page = await inner.PageAsync(request, priority, ct);
                return owner.Stale && request.Table.FileDataId == BuildTactKeys.TactKeyFileDataId ? page with { State = TablePageState.Stale, Rows = [] } : page;
            }
            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken ct) => inner.GetAsync(table, id, priority, ct);
            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken ct) => inner.FollowAsync(request, priority, ct);
            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken ct) => inner.ReferencedByAsync(request, priority, ct);
        }
    }

    /// <summary>The writer lock as one process sees it: a second writer open is OBJECT_STORE_BUSY until the first closes.</summary>
    private void LockWriter()
    {
        var writers = 0;
        stores.BeforeOpen = (writer, _) =>
        {
            if (writer && writers > 0) throw new ObjectStoreException(ObjectStoreCodes.Busy, "Held.");
            if (writer && stores.WriterFailure is null) writers++;
            return Task.CompletedTask;
        };
        stores.Disposing = writer => { if (writer) writers--; };
    }

    private sealed class NoProgress : IProgress<string> { public void Report(string value) { } }

    private BrowseSession Session(out List<bool> composed, out Func<int> closed, InMemoryTables? tables = null)
    {
        var session = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName));
        var seen = new List<bool>();
        var closes = 0;
        var fake = tables ?? new InMemoryTables().Add(X, "enUS", []);
        session.UseTables((store, folder, fetch, _) =>
        {
            Assert.Equal(data, folder);
            Assert.False(fetch);
            seen.Add(store.Writer);
            return Task.FromResult(new TablesLease(fake, async () => { closes++; await store.DisposeAsync(); }));
        });
        composed = seen;
        closed = () => closes;
        return session;
    }

    [Fact]
    public async Task BrowseKeepsOneCompositionAndRefreshSharesItsWriter()
    {
        LockWriter();
        var session = Session(out var composed, out var closed);
        for (var i = 0; i < 3; i++) await using (await session.OpenTables!(session, default)) { }
        Assert.Equal([true], composed);
        Assert.True(session.Tables!.Writer);
        Assert.Equal(1, session.WriterLeases);

        // r while the composition holds the writer: it borrows the same handle instead of finding the store busy.
        var line = await BuildsView.Refresh.Run(session, new NoProgress(), default);
        Assert.NotEqual(BuildsView.BusyMessage, line);
        Assert.StartsWith("refresh done", line, StringComparison.Ordinal);
        Assert.Equal(1, stores.Opened.Count(o => o.Writer));
        Assert.Equal(1, session.WriterLeases);
        Assert.Equal(1, stores.Open);

        // Tables still load after r, from the same composition.
        await using (var lease = await session.OpenTables!(session, default))
        {
            var opened = await lease.Tables.ForAsync(X, "enUS", default);
            Assert.Equal(FilesOpenState.Open, opened.State);
            await opened.Tables!.DisposeAsync();
        }
        Assert.Equal([true], composed);

        await session.DisposeAsync();
        Assert.Equal(1, closed());
        Assert.Equal(0, session.WriterLeases);
        Assert.Equal(0, stores.Open);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenTables!(session, default));

        // With nothing holding it, r takes the lock itself and lets it go.
        var again = Session(out _, out _);
        Assert.StartsWith("refresh done", await BuildsView.Refresh.Run(again, new NoProgress(), default), StringComparison.Ordinal);
        Assert.Equal(0, again.WriterLeases);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ABusyStoreAtTheFirstLoadGivesAReadOnlyCompositionAndRStillWorks()
    {
        LockWriter();
        stores.WriterFailure = ObjectStoreCodes.Busy;
        var session = Session(out var composed, out _);
        await using (await session.OpenTables!(session, default)) { }
        Assert.Equal([false], composed);
        Assert.False(session.Tables!.Writer);
        Assert.Equal(0, session.WriterLeases);
        Assert.Equal([false], stores.WriterCreate);

        // The app closed: r takes the writer; the read-only composition doesn't hold it.
        stores.WriterFailure = null;
        Assert.StartsWith("refresh done", await BuildsView.Refresh.Run(session, new NoProgress(), default), StringComparison.Ordinal);
        Assert.Equal(0, session.WriterLeases);
        await session.DisposeAsync();
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AComposeThatThrowsClosesTheHandleAndIsRetried()
    {
        var session = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName));
        var calls = 0;
        session.UseTables((store, _, _, _) => ++calls == 1 ? throw new InvalidOperationException()
            : Task.FromResult(new TablesLease(new InMemoryTables(), store.DisposeAsync)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenTables!(session, default));
        Assert.Equal(0, stores.Open);
        Assert.Equal(0, session.WriterLeases);
        await using (await session.OpenTables!(session, default)) { }
        Assert.Equal(2, calls);
        await session.DisposeAsync();
        Assert.Equal(0, stores.Open);
    }
}
