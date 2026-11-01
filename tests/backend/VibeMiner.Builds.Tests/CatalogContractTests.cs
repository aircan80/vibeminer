using System.Text;
using VibeMiner.Builds.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

/// <summary>
/// One behaviour suite for <see cref="ICatalog"/>: the real <see cref="Catalog"/> over a temp SQLite file and the in-memory
/// fake layers 4 and up test against. If the fake drifts from the real catalog, this suite fails for one of them.
/// </summary>
public abstract class CatalogContract
{
    private const string Header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), D = new('d', 32), E = new('e', 32);
    private static readonly byte[] Cdns = Encoding.UTF8.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0\n## seqn = 1\nus|tpr/wow|cdn.example.invalid\n");
    private static CancellationToken Ct => CancellationToken.None;

    private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, 123, TimeSpan.Zero));
    private readonly InMemoryObjectSource inner;
    private readonly Switchable source;

    protected CatalogContract() { inner = new InMemoryObjectSource { Time = time }; source = new Switchable(inner); }

    /// <summary>A fresh catalog over <paramref name="source"/>, asking the us host.</summary>
    protected abstract Task<ICatalog> CreateAsync(IObjectSource source);

    private Task<ICatalog> OpenAsync() => CreateAsync(source);

    private static byte[] Versions(string seqn, params (string Region, string Build, string Cdn, string Name)[] rows) =>
        Encoding.UTF8.GetBytes($"{Header}\n## seqn = {seqn}\n" + string.Concat(rows.Select(r =>
            $"{r.Region}|{r.Build}|{r.Cdn}|{C}|{r.Name.Split('.')[^1]}|{r.Name}|{D}\n")));

    private static byte[] Both(string seqn, string build, string cdn, string name) =>
        Versions(seqn, ("us", build, cdn, name), ("eu", build, cdn, name));

    /// <summary>Sets the us replies for the next refresh, then moves the clock a minute.</summary>
    private void Reply(string product, byte[] versions)
    {
        inner.AddReply(product, "us", ObservationKind.Versions, versions).AddReply(product, "us", ObservationKind.Cdns, Cdns);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task<HistoryEntry> NewestVersionsAsync() => (await inner.ReadHistoryAsync(new(Kind: HistoryKind.Versions), Ct))[^1];

    private static string Id(string product, string build, string name) => BuildIdentity.Create(product, new Dictionary<string, string>
    {
        ["Region"] = "us", ["BuildConfig"] = build, ["CDNConfig"] = B, ["KeyRing"] = C, ["BuildId"] = name.Split('.')[^1],
        ["VersionsName"] = name, ["ProductConfig"] = D,
    }).Id;

    /// <summary>Every field of the list as text (records holding lists compare by reference).</summary>
    private static string Shape(BuildList list) =>
        $"{list.Product}|{list.LastObservedAt?.ToString("O")}\n" +
        string.Concat(list.Builds.Select(b =>
            $"{b.Product}|{b.Id}|{b.VersionsName}|{b.BuildConfig}|{b.ProductConfig}|{b.FirstSeen:O}|{b.LastSeen:O}|{b.Current}|" +
            string.Join(",", b.Fields.Select(f => $"{f.Key}={f.Value}")) + "\n" +
            string.Concat(b.Regions.Select(r => $"  {r.Region}|{r.CdnConfig}|{r.FirstSeen:O}|{r.LastSeen:O}|{r.Current}|{r.LastRecord}\n")))) +
        string.Concat(list.Problems.Select(p => $"! {p.Code}|{p.Record}|{p.Row}\n"));

    [Fact]
    public async Task RefreshListsBuildsWithExactFields()
    {
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var catalog = await OpenAsync();
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal("wow", refresh.Product);
        Assert.Equal((ObjectState.Present, ObjectState.Present), (refresh.Versions.State, refresh.Cdns.State));
        var at = time.GetUtcNow();
        var record = (await NewestVersionsAsync()).Record;
        var expected =
            $"wow|{at:O}\n" +
            $"wow|{Id("wow", A, "11.0.0.1000")}|11.0.0.1000|{A}|{D}|{at:O}|{at:O}|True|BuildConfig={A},BuildId=1000,KeyRing={C},ProductConfig={D},VersionsName=11.0.0.1000\n" +
            $"  eu|{B}|{at:O}|{at:O}|True|{record}\n" +
            $"  us|{B}|{at:O}|{at:O}|True|{record}\n";
        Assert.Equal(expected, Shape(refresh.List));
    }

    [Fact]
    public async Task FieldsComeBackInOrdinalOrder()
    {
        // Ordinal order puts every capital before "aux"; a culture comparer would put "aux" first.
        var reply = Encoding.UTF8.GetBytes($"Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|VersionsName!String:0|aux!STRING:0\nus|{A}|{B}|11.0.0.1000|x\n");
        Reply("wow", reply);
        var catalog = await OpenAsync();
        await catalog.RefreshAsync("wow", Ct);
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Equal(["BuildConfig", "VersionsName", "aux"], list.Builds.Single().Fields.Keys);
    }

    [Fact]
    public async Task RefreshAppendsOneVersionsAndOneCdnsEntry()
    {
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        await (await OpenAsync()).RefreshAsync("wow", Ct);
        var history = await inner.ReadHistoryAsync(new(), Ct);
        Assert.Equal([(HistoryKind.Versions, "wow", "us"), (HistoryKind.Cdns, "wow", "us")],
            history.Select(h => (h.Kind, h.Product, h.Region)).OrderBy(h => h.Kind));
    }

    [Fact]
    public async Task AnOlderBuildStaysListedAndNotCurrent()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var first = (await catalog.RefreshAsync("wow", Ct)).List;
        var firstAt = time.GetUtcNow();
        var firstRecord = (await NewestVersionsAsync()).Record;
        Reply("wow", Both("1001", E, B, "11.0.0.1001"));
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        var at = time.GetUtcNow();
        var record = (await NewestVersionsAsync()).Record;
        Assert.Equal(
            $"wow|{at:O}\n" +
            $"wow|{Id("wow", E, "11.0.0.1001")}|11.0.0.1001|{E}|{D}|{at:O}|{at:O}|True|BuildConfig={E},BuildId=1001,KeyRing={C},ProductConfig={D},VersionsName=11.0.0.1001\n" +
            $"  eu|{B}|{at:O}|{at:O}|True|{record}\n  us|{B}|{at:O}|{at:O}|True|{record}\n" +
            $"wow|{Id("wow", A, "11.0.0.1000")}|11.0.0.1000|{A}|{D}|{firstAt:O}|{firstAt:O}|False|BuildConfig={A},BuildId=1000,KeyRing={C},ProductConfig={D},VersionsName=11.0.0.1000\n" +
            $"  eu|{B}|{firstAt:O}|{firstAt:O}|False|{firstRecord}\n  us|{B}|{firstAt:O}|{firstAt:O}|False|{firstRecord}\n",
            Shape(list));
        Assert.Equal(first.Builds[0].Id, list.Builds[1].Id);
    }

    [Fact]
    public async Task ARepublishedCdnConfigIsASecondRegionListing()
    {
        var catalog = await OpenAsync();
        Reply("wow", Versions("1000", ("us", A, B, "11.0.0.1000")));
        await catalog.RefreshAsync("wow", Ct);
        var firstAt = time.GetUtcNow();
        var firstRecord = (await NewestVersionsAsync()).Record;
        Reply("wow", Versions("1001", ("us", A, E, "11.0.0.1000")));
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        var at = time.GetUtcNow();
        var record = (await NewestVersionsAsync()).Record;
        var build = Assert.Single(list.Builds);
        Assert.Equal((firstAt, at, true), (build.FirstSeen, build.LastSeen, build.Current));
        Assert.Equal([("us", B, firstAt, firstAt, false, firstRecord), ("us", E, at, at, true, record)],
            build.Regions.Select(r => (r.Region, r.CdnConfig, r.FirstSeen, r.LastSeen, r.Current, r.LastRecord)));
    }

    [Fact]
    public async Task CurrentFollowsTheHighestSeqnEvenWhenALowerOneIsObservedLater()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("2000", E, B, "11.0.0.2000"));
        await catalog.RefreshAsync("wow", Ct);
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        Assert.Equal([("11.0.0.1000", false), ("11.0.0.2000", true)], list.Builds.Select(b => (b.VersionsName, b.Current)));
        Assert.All(list.Builds[0].Regions, r => Assert.False(r.Current));
        Assert.All(list.Builds[1].Regions, r => Assert.True(r.Current));
    }

    [Fact]
    public async Task AnUnreadableNewestReplyKeepsCurrentAndIsAProblem()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var before = (await catalog.RefreshAsync("wow", Ct)).List;
        Reply("wow", Encoding.UTF8.GetBytes("Region!STRING:0\n## seqn = 2000\nus\n"));
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        var newest = await NewestVersionsAsync();
        var problem = Assert.Single(list.Problems);
        Assert.Equal(("VERSIONS_COLUMNS_MISSING", newest.Record, (int?)null), (problem.Code, problem.Record, problem.Row));
        Assert.Equal(time.GetUtcNow(), list.LastObservedAt);
        Assert.True(Assert.Single(list.Builds).Current);
        Assert.Equal(Shape(before with { LastObservedAt = list.LastObservedAt, Problems = list.Problems }), Shape(list));
    }

    [Fact]
    public async Task AnUnavailableReplyIsReportedThenPickedUp()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var before = (await catalog.RefreshAsync("wow", Ct)).List;
        Reply("wow", Both("1001", E, B, "11.0.0.1001"));
        source.BlockDocuments = true;
        var blocked = (await catalog.RefreshAsync("wow", Ct)).List;
        var problem = Assert.Single(blocked.Problems);
        Assert.Equal(("CATALOG_DOCUMENT_UNAVAILABLE", (await NewestVersionsAsync()).Record, (int?)null), (problem.Code, problem.Record, problem.Row));
        Assert.Equal(Shape(before with { Problems = blocked.Problems }), Shape(blocked));
        Assert.Empty((await catalog.ListBuildsAsync("wowt", Ct)).Problems);

        source.BlockDocuments = false;
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Empty(list.Problems);
        Assert.Equal(["11.0.0.1001", "11.0.0.1000"], list.Builds.Select(b => b.VersionsName));
    }

    [Fact]
    public async Task ABadRowDropsOnlyThatRow()
    {
        Reply("wow", Versions("1000", ("us", A, B, "11.0.0.1000"), ("eu", "zz", B, "11.0.0.1000")));
        var list = (await (await OpenAsync()).RefreshAsync("wow", Ct)).List;
        var build = Assert.Single(list.Builds);
        Assert.Equal(["us"], build.Regions.Select(r => r.Region));
        Assert.True(build.Current);
        var problem = Assert.Single(list.Problems);
        Assert.Equal(("VERSIONS_ROW_INVALID", (await NewestVersionsAsync()).Record, (int?)1), (problem.Code, problem.Record, problem.Row));
    }

    [Fact]
    public async Task ProductsAreListedApart()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        Reply("wowt", Both("2000", E, B, "11.0.5.2000"));
        await catalog.RefreshAsync("wow", Ct);
        await catalog.RefreshAsync("wowt", Ct);
        var wow = await catalog.ListBuildsAsync("wow", Ct);
        var ptr = await catalog.ListBuildsAsync("wowt", Ct);
        Assert.Equal(("wow", "11.0.0.1000"), (wow.Product, Assert.Single(wow.Builds).VersionsName));
        Assert.Equal(("wowt", "wowt", "11.0.5.2000"), (ptr.Product, Assert.Single(ptr.Builds).Product, ptr.Builds[0].VersionsName));
        Assert.Equal(Id("wowt", E, "11.0.5.2000"), ptr.Builds[0].Id);
        var beta = await catalog.ListBuildsAsync("wow_beta", Ct);
        Assert.Equal("wow_beta|\n", Shape(beta));
    }

    [Fact]
    public async Task AnUnknownProductThrows()
    {
        var catalog = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListBuildsAsync("diablo", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RefreshAsync("diablo", Ct));
        Assert.Empty(inner.Asked);
        Assert.Empty(await inner.ReadHistoryAsync(new(), Ct));
    }

    [Fact]
    public async Task EntriesAppendedElsewhereShowUpInTheNextList()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        await catalog.RefreshAsync("wow", Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        inner.AddReply("wow", "eu", ObservationKind.Versions, Both("1001", E, B, "11.0.0.1001"));
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "eu"), Ct);
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Equal(time.GetUtcNow(), list.LastObservedAt);
        // Each host's newest reply decides: eu lists the newer build, us still the older one.
        Assert.Equal([("11.0.0.1001", true), ("11.0.0.1000", true)], list.Builds.Select(b => (b.VersionsName, b.Current)));
    }

    [Fact]
    public async Task AnOfflineRefreshStillReturnsTheList()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        var before = (await catalog.RefreshAsync("wow", Ct)).List;
        source.Offline = true;
        Reply("wow", Both("1001", E, B, "11.0.0.1001"));
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal((ObjectState.Unavailable, "CDN_OFFLINE"), (refresh.Versions.State, refresh.Versions.Code));
        Assert.Equal(ObjectState.Unavailable, refresh.Cdns.State);
        Assert.Equal(Shape(before), Shape(refresh.List));
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ListBuildsAsync("wow", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.RefreshAsync("wow", cts.Token));
        Assert.Single((await catalog.ListBuildsAsync("wow", Ct)).Builds);
    }

    [Fact]
    public async Task ARefusedReplyIsReportedWithItsCode()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        source.RefuseDocuments = true;
        var list = (await catalog.RefreshAsync("wow", Ct)).List;
        var problem = Assert.Single(list.Problems);
        Assert.Equal(("CATALOG_DOCUMENT_REFUSED", (await NewestVersionsAsync()).Record, (int?)null), (problem.Code, problem.Record, problem.Row));
        Assert.Contains("OBJECT_HASH_MISMATCH", problem.Message, StringComparison.Ordinal);
        Assert.Empty(list.Builds);
        Assert.Null(list.LastObservedAt);
    }

    [Fact]
    public async Task ManyUnreadableRepliesAreCapped()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        for (var i = 0; i < Catalog.MaxListProblems + 2; i++)
        {
            await inner.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), Ct);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        source.BlockDocuments = true;
        var list = await catalog.ListBuildsAsync("wow", Ct);
        Assert.Equal(Catalog.MaxListProblems + 1, list.Problems.Count);
        Assert.All(list.Problems.Take(Catalog.MaxListProblems), p => Assert.Equal("CATALOG_DOCUMENT_UNAVAILABLE", p.Code));
        Assert.Equal(("CATALOG_PROBLEMS_TRUNCATED", (ObjectKey?)null), (list.Problems[^1].Code, list.Problems[^1].Record));
        Assert.Contains("2 more", list.Problems[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThrowingObservationIsReportedAndTheRefreshGoesOn()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        source.VersionsThrows = true;
        var refresh = await catalog.RefreshAsync("wow", Ct);
        Assert.Equal((ObjectState.Unavailable, "CATALOG_OBSERVE_FAILED"), (refresh.Versions.State, refresh.Versions.Code));
        Assert.Contains("scripted failure", refresh.Versions.Message, StringComparison.Ordinal);
        Assert.Equal(ObjectState.Present, refresh.Cdns.State);
        Assert.Equal("wow|\n", Shape(refresh.List));
        Assert.Equal([HistoryKind.Cdns], (await inner.ReadHistoryAsync(new(), Ct)).Select(h => h.Kind));
    }

    [Fact]
    public async Task TheSummaryReportsUnreviewedWowProductsOnlyAndListsNoBuilds()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        await catalog.RefreshAsync("wow", Ct);
        inner.AddReply("", "us", ObservationKind.Summary, Encoding.UTF8.GetBytes(
            "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 42\nwow|1|\nwow|2|cdn\nwowz|3|\nagent|4|\nwow_classic_era_beta|5|cdn\nwowdev|6|\nwowt|7|\n"));
        var summary = await catalog.RefreshSummaryAsync(Ct);
        Assert.Equal((ObjectState.Present, true), (summary.Summary.State, summary.Summary.Recorded));
        Assert.Equal(["wow_classic_era_beta", "wowdev", "wowz"], summary.NewProducts);
        Assert.Equal(["agent", "wow", "wow_classic_era_beta", "wowdev", "wowt", "wowz"], summary.Listed);
        Assert.Equal("42", summary.Sequence);
        Assert.Empty(summary.Problems);
        var entry = Assert.Single(await inner.ReadHistoryAsync(new(Kind: HistoryKind.Summary), Ct));
        Assert.Equal(("", "us"), (entry.Product, entry.Region));
        // The summary is in the log but never in a list, and an unreviewed product still can't be listed.
        Assert.Single((await catalog.ListBuildsAsync("wow", Ct)).Builds);
        Assert.Equal("wowt|\n", Shape(await catalog.ListBuildsAsync("wowt", Ct)));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.ListBuildsAsync("wowdev", Ct));
    }

    [Fact]
    public async Task AMissingSummaryIsReportedAndListsNothing()
    {
        var catalog = await OpenAsync();
        var summary = await catalog.RefreshSummaryAsync(Ct);
        Assert.Equal(ObjectState.NotFound, summary.Summary.State);
        Assert.Empty(summary.NewProducts);
        Assert.Empty(summary.Listed);
        Assert.Null(summary.Sequence);
        Assert.Empty(await inner.ReadHistoryAsync(new(), Ct));
    }

    [Fact]
    public async Task UseAfterDisposeThrows()
    {
        var catalog = await OpenAsync();
        Reply("wow", Both("1000", A, B, "11.0.0.1000"));
        await catalog.RefreshAsync("wow", Ct);
        var disposable = Assert.IsAssignableFrom<IAsyncDisposable>(catalog);
        await disposable.DisposeAsync();
        await disposable.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.ListBuildsAsync("wow", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.RefreshAsync("wow", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => catalog.RefreshSummaryAsync(Ct));
    }

    /// <summary>Whole-millisecond UTC time that moves only when told.</summary>
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset now = now;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    /// <summary>Forwards to the fake, with the network switched off or history documents unreadable on request.</summary>
    private sealed class Switchable(IObjectSource inner) : IObjectSource
    {
        public volatile bool Offline;
        public volatile bool BlockDocuments;
        public volatile bool RefuseDocuments;
        public volatile bool VersionsThrows;

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            request.Key.Type != ObjectKeyType.HistoryDocument ? inner.GetKeyAsync(request, cdn, priority, cancellationToken)
            : RefuseDocuments ? Task.FromResult(new ObjectResult(request.Key, ObjectState.Refused, Code: "OBJECT_HASH_MISMATCH"))
            : BlockDocuments ? Task.FromResult(new ObjectResult(request.Key, ObjectState.Unavailable, Code: "CDN_OFFLINE"))
            : inner.GetKeyAsync(request, cdn, priority, cancellationToken);

        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) => inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);

        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) => inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);

        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (VersionsThrows && request.Kind == ObservationKind.Versions) throw new InvalidOperationException("scripted failure");
            return Offline ? Task.FromResult(new ObservationResult(ObjectState.Unavailable, Code: "CDN_OFFLINE"))
                : inner.ObserveAsync(request, cancellationToken);
        }

        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.CountPresentAsync(keys, cancellationToken);

        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(query, cancellationToken);
    }
}

/// <summary>The real catalog over a SQLite file in a temp folder, removed afterwards.</summary>
public sealed class RealCatalogContractTests : CatalogContract, IAsyncLifetime
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "vibeminer-catalog-contract-" + Guid.NewGuid().ToString("N"));
    private readonly List<Catalog> opened = [];

    protected override async Task<ICatalog> CreateAsync(IObjectSource source)
    {
        var catalog = await Catalog.OpenAsync(source, Path.Combine(dir, $"catalog-{opened.Count}.sqlite3"), null, CancellationToken.None);
        opened.Add(catalog);
        return catalog;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in opened) await c.DisposeAsync();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}

public sealed class InMemoryCatalogContractTests : CatalogContract
{
    protected override Task<ICatalog> CreateAsync(IObjectSource source) => Task.FromResult<ICatalog>(new InMemoryCatalog(source));
}
