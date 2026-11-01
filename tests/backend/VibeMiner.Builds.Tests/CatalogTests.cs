using System.Text;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class CatalogTests : IDisposable
{
    private const string Header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), D = new('d', 32), E = new('e', 32);
    private static CancellationToken Ct => CancellationToken.None;

    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeminer-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, 123, TimeSpan.Zero));
    private readonly InMemoryObjectSource inner;
    private readonly ScriptedSource source;
    private string DbPath => Path.Combine(dir, "catalog.sqlite3");

    public CatalogTests() { inner = new InMemoryObjectSource { Time = time }; source = new ScriptedSource(inner); }
    public void Dispose() { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }

    private static byte[] Versions(string build, string name, string id, string cdn = "") =>
        Encoding.UTF8.GetBytes($"{Header}\n## seqn = {id}\nus|{build}|{(cdn == "" ? B : cdn)}|{C}|{id}|{name}|{D}\neu|{build}|{(cdn == "" ? B : cdn)}|{C}|{id}|{name}|{D}\n");
    private static readonly byte[] Cdns = Encoding.UTF8.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0\n## seqn = 1\nus|tpr/wow|cdn.example.invalid\n");

    private void Reply(string product, byte[] versions)
    {
        inner.AddReply(product, "us", ObservationKind.Versions, versions).AddReply(product, "us", ObservationKind.Cdns, Cdns);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    private Task<Catalog> OpenAsync() => Catalog.OpenAsync(source, DbPath, null, Ct);

    private static object Shape(BuildList list) => string.Join("\n", list.Builds.Select(b =>
        $"{b.Id}|{b.VersionsName}|{b.BuildConfig}|{b.ProductConfig}|{b.FirstSeen:O}|{b.LastSeen:O}|{b.Current}|" +
        string.Join(";", b.Regions.Select(r => $"{r.Region},{r.CdnConfig},{r.FirstSeen:O},{r.LastSeen:O},{r.Current},{r.LastRecord}"))))
        + "|" + list.LastObservedAt?.ToString("O") + "|" + list.Problems.Count;

    [Fact]
    public async Task RefreshListsBuildsAndAppendsHistory()
    {
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await using var catalog = await OpenAsync();
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal(ObjectState.Present, refresh.Versions.State);
        Assert.Equal(ObjectState.Present, refresh.Cdns.State);
        var build = Assert.Single(refresh.List.Builds);
        Assert.Equal("11.0.0.1000", build.VersionsName);
        Assert.Equal(A, build.BuildConfig);
        Assert.Equal(D, build.ProductConfig);
        Assert.True(build.Current);
        Assert.Equal(["eu", "us"], build.Regions.Select(r => r.Region));
        Assert.Empty(refresh.List.Problems);
        Assert.Equal(time.GetUtcNow(), refresh.List.LastObservedAt);

        var history = await inner.ReadHistoryAsync(new HistoryQuery(), Ct);
        Assert.Single(history, h => h.Kind == HistoryKind.Versions);
        Assert.Single(history, h => h.Kind == HistoryKind.Cdns);
    }

    [Fact]
    public async Task SecondRefreshKeepsTheOlderBuild()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        var first = Assert.Single((await catalog.RefreshAsync("wow", Ct)).List.Builds);
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        Assert.Equal(2, list.Builds.Count);
        Assert.Equal("11.0.0.1001", list.Builds[0].VersionsName);
        Assert.True(list.Builds[0].Current);
        Assert.Equal(first.Id, list.Builds[1].Id);
        Assert.False(list.Builds[1].Current);
        Assert.Equal(first.FirstSeen, list.Builds[1].FirstSeen);
    }

    [Fact]
    public async Task NetworkOffReportsUnavailableAndKeepsTheList()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        var before = (await catalog.RefreshAsync("wow", Ct)).List;
        source.Offline = true;
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal(ObjectState.Unavailable, refresh.Versions.State);
        Assert.Equal("CDN_OFFLINE", refresh.Versions.Code);
        Assert.Equal(ObjectState.Unavailable, refresh.Cdns.State);
        Assert.Equal(Shape(before), Shape(refresh.List));
    }

    [Fact]
    public async Task ReopenGivesTheSameList()
    {
        BuildList before;
        await using (var catalog = await OpenAsync())
        {
            Reply("wow", Versions(A, "11.0.0.1000", "1000"));
            await catalog.RefreshAsync("wow", Ct);
            Reply("wow", Versions(E, "11.0.0.1001", "1001", cdn: C));
            before = (await catalog.RefreshAsync("wow", Ct)).List;
        }
        await using var reopened = await OpenAsync();
        Assert.Equal(Shape(before), Shape(await reopened.ListBuildsAsync("wow", Ct)));
    }

    [Fact]
    public async Task LostStoreIsRebuiltFromHistory()
    {
        BuildList before;
        await using (var catalog = await OpenAsync())
        {
            Reply("wow", Versions(A, "11.0.0.1000", "1000"));
            await catalog.RefreshAsync("wow", Ct);
            Reply("wow", Versions(E, "11.0.0.1001", "1001"));
            before = (await catalog.RefreshAsync("wow", Ct)).List;
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(DbPath + suffix);
        Assert.False(File.Exists(DbPath));
        await using var rebuilt = await OpenAsync();
        Assert.Equal(Shape(before), Shape(await rebuilt.ListBuildsAsync("wow", Ct)));
    }

    [Fact]
    public async Task EntriesAppendedElsewhereShowUpInTheNextList()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "eu"), Ct);  // no eu reply: not appended
        inner.AddReply("wow", "eu", ObservationKind.Versions, Versions(E, "11.0.0.1001", "1001"));
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "eu"), Ct);
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Equal(2, list.Builds.Count);
        Assert.Equal("11.0.0.1001", list.Builds[0].VersionsName);
        Assert.True(list.Builds[0].Current);
        // The us host still lists the older build as current.
        Assert.True(list.Builds[1].Current);
    }

    [Fact]
    public async Task ProductListExcludesOtherProducts()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        Reply("wowt", Versions(E, "11.0.5.2000", "2000"));
        await catalog.RefreshAsync("wow", Ct);
        await catalog.RefreshAsync("wowt", Ct);
        var wow = await catalog.ListBuildsAsync("wow", Ct);
        var ptr = await catalog.ListBuildsAsync("wowt", Ct);
        Assert.Equal("11.0.0.1000", Assert.Single(wow.Builds).VersionsName);
        Assert.Equal("11.0.5.2000", Assert.Single(ptr.Builds).VersionsName);
        Assert.All(ptr.Builds, b => Assert.Equal("wowt", b.Product));
        Assert.Empty((await catalog.ListBuildsAsync("wow_beta", Ct)).Builds);
    }

    [Fact]
    public async Task UnknownProductThrows()
    {
        await using var catalog = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListBuildsAsync("diablo", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RefreshAsync("diablo", Ct));
        Assert.Empty(inner.Asked);
    }

    [Fact]
    public async Task BadSourceRegionThrows() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Catalog.OpenAsync(source, DbPath, new CatalogOptions("US"), Ct));

    [Fact]
    public async Task UnreadableDocumentIsReportedThenPickedUp()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        source.BlockDocuments = true;
        var blocked = (await catalog.RefreshAsync("wow", Ct)).List;
        var problem = Assert.Single(blocked.Problems);
        Assert.Equal("CATALOG_DOCUMENT_UNAVAILABLE", problem.Code);
        var newest = (await inner.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Versions), Ct))[^1];
        Assert.Equal(newest.Record, problem.Record);
        Assert.Single(blocked.Builds);
        Assert.Empty((await catalog.ListBuildsAsync("wowt", Ct)).Problems);
        // Still reported while unreadable.
        Assert.Single((await catalog.ListBuildsAsync("wow", Ct)).Problems);

        source.BlockDocuments = false;
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Empty(list.Problems);
        Assert.Equal(2, list.Builds.Count);
        Assert.Equal("11.0.0.1001", list.Builds[0].VersionsName);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ListBuildsAsync("wow", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.RefreshAsync("wow", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Catalog.OpenAsync(source, DbPath, null, cts.Token));

        // Cancelled while reading a reply: nothing is added, and the next list picks it up.
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct);
        using var mid = new CancellationTokenSource();
        inner.BeforeAnswer = async (_, token) => { await mid.CancelAsync(); token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ListBuildsAsync("wow", mid.Token));
        inner.BeforeAnswer = null;
        Assert.Single((await catalog.ListBuildsAsync("wow", Ct)).Builds);
    }

    [Fact]
    public async Task LateEntryWithAnOlderTimeShowsUpInTheNextList()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        time.Advance(TimeSpan.FromMinutes(20));
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        await catalog.RefreshAsync("wow", Ct);
        // Another refresh appended this one later, stamped before the newest entry.
        time.Advance(TimeSpan.FromMinutes(-5));
        inner.AddReply("wow", "eu", ObservationKind.Versions, Versions(E, "11.0.0.1001", "1001", cdn: C));
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "eu"), Ct);
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Contains(list.Builds[0].Regions, r => r.CdnConfig == C);
        Assert.NotNull(source.Queries[^1].After);
    }

    [Fact]
    public async Task PermanentlyRefusedEntryIsReportedWithoutRescanning()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        time.Advance(TimeSpan.FromMinutes(20));
        Reply("wow", Versions(E, "11.0.0.1001", "1001"));
        var bad = (await source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct)).Observation!;
        var entries = await inner.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Versions), Ct);
        var (first, badEntry) = (entries[0], entries[^1]);
        source.Document = r => r.Key == badEntry.Document ? new ObjectResult(r.Key, ObjectState.Refused, Code: "OBJECT_KEY_MISMATCH") : null;
        Assert.Equal(bad.ObservedAt, badEntry.ObservedAt);
        for (var i = 0; i < 3; i++)
        {
            var problem = Assert.Single((await catalog.ListBuildsAsync("wow", Ct)).Problems);
            Assert.Equal("CATALOG_DOCUMENT_REFUSED", problem.Code);
            Assert.Contains("OBJECT_KEY_MISMATCH", problem.Message);
            Assert.Equal(badEntry.Record, problem.Record);
        }
        // Only the first two syncs (nothing old enough to resume after yet) read from the start.
        Assert.Equal([null, null, first.Record, first.Record], source.Queries.Select(q => q.After?.Record));
        Assert.Equal(3, inner.Asked.Count(a => a.Key == badEntry.Document));  // found once, then retried by key
    }

    [Fact]
    public async Task TransientProblemsAreCapped()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        source.BlockDocuments = true;
        for (var i = 0; i < 60; i++) { await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct); time.Advance(TimeSpan.FromMilliseconds(1)); }
        var problems = (await catalog.ListBuildsAsync("wow", Ct)).Problems;
        Assert.Equal(51, problems.Count);
        Assert.All(problems.Take(50), p => Assert.Equal("CATALOG_DOCUMENT_UNAVAILABLE", p.Code));
        Assert.Equal("CATALOG_PROBLEMS_TRUNCATED", problems[^1].Code);
        Assert.Contains("10", problems[^1].Message);
    }

    [Theory]
    [InlineData(ObjectState.Refused, "CATALOG_DOCUMENT_REFUSED")]
    [InlineData(ObjectState.StoreFailed, "CATALOG_DOCUMENT_STORE_FAILED")]
    [InlineData(ObjectState.NotFound, "CATALOG_DOCUMENT_UNAVAILABLE")]
    [InlineData(ObjectState.Unavailable, "CATALOG_DOCUMENT_UNAVAILABLE")]
    [InlineData(ObjectState.NeedsNetwork, "CATALOG_DOCUMENT_UNAVAILABLE")]
    [InlineData(ObjectState.Present, "CATALOG_DOCUMENT_TOO_LARGE")]
    public async Task DocumentStatesStayDistinct(ObjectState state, string code)
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        source.Document = r => new ObjectResult(r.Key, state, Code: "LAYER2_CODE", Length: state == ObjectState.Present ? 1L << 30 : null);
        var problem = Assert.Single((await catalog.RefreshAsync("wow", Ct)).List.Problems);
        Assert.Equal(code, problem.Code);
        Assert.Contains("LAYER2_CODE", problem.Message);
    }

    [Fact]
    public async Task ObserveThatThrowsIsReportedAndTheListServed()
    {
        await using var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        source.ThrowOnObserve = true;
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal(ObjectState.Unavailable, refresh.Versions.State);
        Assert.Equal("CATALOG_OBSERVE_FAILED", refresh.Versions.Code);
        Assert.Equal("CATALOG_OBSERVE_FAILED", refresh.Cdns.Code);
        Assert.Equal(4, source.Observed);
        Assert.Single(refresh.List.Builds);
    }

    [Fact]
    public async Task UseAfterDisposeThrows()
    {
        var catalog = await OpenAsync();
        Reply("wow", Versions(A, "11.0.0.1000", "1000"));
        await catalog.RefreshAsync("wow", Ct);
        await catalog.DisposeAsync();
        await catalog.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.ListBuildsAsync("wow", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.RefreshAsync("wow", Ct));
    }

    [Fact]
    public async Task ConcurrentCallsListEveryProductCorrectly()
    {
        await using var catalog = await OpenAsync();
        string[] products = ["wow", "wowt", "wowxptr", "wow_beta"];
        for (var i = 0; i < products.Length; i++) Reply(products[i], Versions(A, $"11.0.{i}.1000", "1000"));
        var refreshes = products.Select(p => catalog.RefreshAsync(p, Ct)).ToArray();
        var lists = products.Select(p => catalog.ListBuildsAsync(p, Ct)).ToArray();
        var refreshed = await Task.WhenAll(refreshes);
        var listed = await Task.WhenAll(lists);
        for (var i = 0; i < products.Length; i++)
        {
            Assert.Equal($"11.0.{i}.1000", Assert.Single(refreshed[i].List.Builds).VersionsName);
            Assert.All(listed[i].Builds, b => Assert.Equal(products[i], b.Product));
            Assert.Equal($"11.0.{i}.1000", Assert.Single((await catalog.ListBuildsAsync(products[i], Ct)).Builds).VersionsName);
        }
    }

    [Fact]
    public async Task HistoryOverSeveralPagesIsRead()
    {
        await using var catalog = await OpenAsync();
        catalog.PageSize = 2;
        for (var i = 0; i < 5; i++)
        {
            inner.AddReply("wow", "us", ObservationKind.Versions, Versions(A, $"11.0.0.{1000 + i}", $"{1000 + i}"));
            await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Equal(5, list.Builds.Count);
        Assert.Empty(list.Problems);
        Assert.Equal(3, source.Queries.Count);
    }

    /// <summary>Whole-millisecond UTC time that moves only when told.</summary>
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>
    /// Forwards to the fake, with the network switched off, history documents unreadable or scripted, or observations
    /// throwing on request. Records every history query.
    /// </summary>
    private sealed class ScriptedSource(IObjectSource inner) : IObjectSource
    {
        private readonly Lock gate = new();
        private readonly List<HistoryQuery> queries = [];
        private int observed;
        public volatile bool Offline;
        public volatile bool BlockDocuments;
        public volatile bool ThrowOnObserve;
        /// <summary>When it returns a result for a history document, that is the answer.</summary>
        public Func<ObjectRequest, ObjectResult?>? Document;

        public IReadOnlyList<HistoryQuery> Queries { get { lock (gate) return queries.ToArray(); } }
        public int Observed => Volatile.Read(ref observed);

        public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
        {
            var real = await inner.GetKeyAsync(request, cdn, priority, cancellationToken);
            if (request.Key.Type != ObjectKeyType.HistoryDocument) return real;
            if (BlockDocuments) return new ObjectResult(request.Key, ObjectState.Unavailable, Code: "CDN_OFFLINE");
            return Document?.Invoke(request) ?? real;
        }

        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) => inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);

        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) => inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);

        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref observed);
            if (ThrowOnObserve) throw new InvalidOperationException("Synthetic observe failure.");
            return Offline ? Task.FromResult(new ObservationResult(ObjectState.Unavailable, Code: "CDN_OFFLINE"))
                : inner.ObserveAsync(request, cancellationToken);
        }

        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.CountPresentAsync(keys, cancellationToken);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.HasKeysAsync(keys, cancellationToken);

        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
        {
            lock (gate) queries.Add(query);
            return inner.ReadHistoryAsync(query, cancellationToken);
        }
    }
}
