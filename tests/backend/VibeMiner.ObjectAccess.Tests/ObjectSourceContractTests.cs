using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>
/// One behaviour suite for <see cref="IObjectSource"/>: the real source (in-memory store, scripted CDN) and the in-memory
/// fake layers 3 and up test against. If the fake drifts from the real source, this suite fails for one of them.
/// </summary>
public abstract class ObjectSourceContract
{
    protected static readonly CdnContext Context = new(Scenario.Route);
    protected const string TableId = "0123abcd", TableName = "Map", Revision = "1111111111111111111111111111111111111111";
    protected static readonly byte[] MapDbd = "COLUMNS\nint ID\n\nBUILD 11.0.0.1\n$id$ID<32>\n"u8.ToArray();
    /// <summary>Makes the objects available (on the CDN for the real source) and returns the source.</summary>
    protected abstract IObjectSource Arrange(IReadOnlyList<PutObject> available);
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AnAvailableObjectComesBackWithItsBytes()
    {
        var o = Synthetic.Chunked(1, 3000, 2000);
        var source = Arrange([o]);
        var r = await source.GetKeyAsync(new(o.Key, o.DeclaredLength), Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, o.DeclaredLength), (r.State, r.Length!.Value));
        Assert.Equal(o.Bytes.ToArray(), r.Bytes);
        Assert.NotNull(r.ServedFrom);
    }

    [Fact]
    public async Task AMissingObjectIsADatedRetryableNotFound()
    {
        var source = Arrange([]);
        var r = await source.GetKeyAsync(new(Synthetic.Config(5).Key), Context, FetchPriority.Interactive, None);
        Assert.Equal(ObjectState.NotFound, r.State);
        Assert.True(r.Retryable);
        Assert.NotNull(r.ObservedAt);
        Assert.Null(r.Bytes);
    }

    [Fact]
    public async Task GetKeysAnswersEveryRequestInOrderWithoutBytes()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Headerless(2, 400); var missing = Synthetic.Config(3);
        var source = Arrange([a, b]);
        var results = await source.GetKeysAsync([new(b.Key, 400), new(missing.Key), new(a.Key), new(b.Key, 400)], Context, FetchPriority.Background, null, None);
        Assert.Equal([b.Key, missing.Key, a.Key, b.Key], results.Select(r => r.Key));
        Assert.Equal([ObjectState.Present, ObjectState.NotFound, ObjectState.Present, ObjectState.Present], results.Select(r => r.State));
        Assert.All(results, r => Assert.Null(r.Bytes));
    }

    [Fact]
    public async Task AWrongDeclaredLengthIsRefused()
    {
        var o = Synthetic.Headerless(4, 500);
        var source = Arrange([o]);
        var r = await source.GetKeyAsync(new(o.Key, 499), Context, FetchPriority.Interactive, None);
        Assert.False(r.IsPresent);
        Assert.False(r.Retryable);
    }

    [Fact]
    public async Task GetRangeFillsTheDestination()
    {
        var o = Synthetic.Chunked(5, 1000, 1000);
        var source = Arrange([o]);
        var into = new byte[300];
        var r = await source.GetRangeAsync(new(o.Key, o.DeclaredLength), 1500, into, Context, FetchPriority.Interactive, None);
        Assert.True(r.IsPresent);
        Assert.Null(r.Bytes);
        Assert.Equal(o.Bytes.Slice(1500, 300).ToArray(), into);
    }

    [Fact]
    public async Task CancellationIsNotAResult()
    {
        var o = Synthetic.Config(6);
        var source = Arrange([o]);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetKeyAsync(new(o.Key), Context, FetchPriority.Interactive, cancel.Token));
    }

    [Fact]
    public async Task AVersionReplyIsObserved()
    {
        var source = Arrange([]);
        var r = await source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), None);
        Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        Assert.NotEmpty(r.Observation!.Bytes);
        Assert.Equal(VersionClient.UrlFor("wow", "us", VersionDocumentKind.Versions), r.Observation.Url);
        var entry = Assert.Single(await source.ReadHistoryAsync(new(Product: "wow"), None));
        Assert.Equal((HistoryKind.Versions, "us", r.Observation.ObservedAt), (entry.Kind, entry.Region, entry.ObservedAt));
        var document = await source.GetKeyAsync(new(entry.Document), Context, FetchPriority.Interactive, None);
        Assert.Equal(r.Observation.Bytes, document.Bytes);
    }

    [Fact]
    public async Task TheSummaryIsObservedWithNoProduct()
    {
        var source = Arrange([]);
        var r = await source.ObserveAsync(new(ObservationKind.Summary, "", "us"), None);
        Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        Assert.Equal("https://us.version.battle.net/v2/summary", r.Observation!.Url.AbsoluteUri);
        var entry = Assert.Single(await source.ReadHistoryAsync(new(Kind: HistoryKind.Summary), None));
        Assert.Equal(("", "us", r.Observation.Url.AbsoluteUri), (entry.Product, entry.Region, entry.Url));
        Assert.Empty(await source.ReadHistoryAsync(new(Product: "wow"), None));
    }

    [Fact]
    public async Task ADefinitionIsObservedWithItsRawUrlAndReadBack()
    {
        var source = Arrange([]);
        var r = await source.ObserveAsync(new(ObservationKind.Definition, TableId, "wowdbdefs", TableName), None);
        Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        Assert.Equal(WoWDBDefsClient.DefinitionUrl(Revision, TableName), r.Observation!.Url.AbsoluteUri);
        Assert.Equal(MapDbd, r.Observation.Bytes);
        var entry = Assert.Single(await source.ReadHistoryAsync(new(Product: TableId, Kind: HistoryKind.Definition), None));
        Assert.Equal((TableId, "wowdbdefs", r.Observation.Url.AbsoluteUri, r.Observation.ObservedAt), (entry.Product, entry.Region, entry.Url, entry.ObservedAt));
        var document = await source.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, None);
        Assert.Equal(MapDbd, document.Bytes);
        Assert.Empty(await source.ReadHistoryAsync(new(Kind: HistoryKind.Versions), None));
    }

    [Fact]
    public async Task AFailedDefinitionIsADatedResultAndNothingIsRecorded()
    {
        var source = Arrange([]);
        var r = await source.ObserveAsync(new(ObservationKind.Definition, "fedcba98", "wowdbdefs", "Missing"), None);
        Assert.Equal((ObjectState.NotFound, "CDN_NOT_FOUND", false), (r.State, r.Code, r.Recorded));
        Assert.True(r.ObservedAt.HasValue);
        Assert.Null(r.Observation);
        Assert.Empty(await source.ReadHistoryAsync(new(Kind: HistoryKind.Definition), None));
    }

    [Theory]
    [InlineData(ObservationKind.Definition, "0123ABCD", "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Definition, "0123abc", "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Definition, "0123abcd", "us", "Map")]
    [InlineData(ObservationKind.Definition, "0123abcd", "wowdbdefs", null)]
    [InlineData(ObservationKind.Definition, "0123abcd", "wowdbdefs", "")]
    [InlineData(ObservationKind.Definition, "0123abcd", "wowdbdefs", "../Map")]
    [InlineData(ObservationKind.Versions, "wow", "us", "Map")]
    public async Task ARequestOfTheWrongShapeIsRefused(ObservationKind kind, string product, string region, string? name)
    {
        var source = Arrange([]);
        var r = await source.ObserveAsync(new(kind, product, region, name), None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID", false), (r.State, r.Code, r.Recorded));
        Assert.NotNull(r.Message);
        Assert.Empty(await source.ReadHistoryAsync(new(), None));
    }

    [Fact]
    public async Task OnlyStoredKeysCountAsPresent()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Config(2); var c = Synthetic.Config(3);
        var source = Arrange([a, b]);
        Assert.Equal(0, await source.CountPresentAsync([a.Key, b.Key, c.Key], None));
        await source.GetKeysAsync([new(a.Key), new(b.Key)], Context, FetchPriority.Background, null, None);
        Assert.Equal(2, await source.CountPresentAsync([a.Key, b.Key, c.Key], None));
    }

    [Fact]
    public async Task HasKeysAnswersEachKeyInOrder()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Config(2); var c = Synthetic.Config(3);
        var source = Arrange([a, b, c]);
        Assert.Empty(await source.HasKeysAsync([], None));
        Assert.Equal([false, false, false], await source.HasKeysAsync([a.Key, b.Key, c.Key], None));
        await source.GetKeysAsync([new(a.Key), new(c.Key)], Context, FetchPriority.Background, null, None);
        Assert.Equal([true, false, true, true], await source.HasKeysAsync([a.Key, b.Key, c.Key, a.Key], None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => source.HasKeysAsync(null!, None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.HasKeysAsync([a.Key], new CancellationToken(true)));
    }

    [Fact]
    public async Task FetchingPublishesEachNewlyStoredKeyOnceAndHasKeysAlreadyAgrees()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Headerless(2, 400);
        var source = Arrange([a, b]);
        var subscription = ((IStoreCommits)source).Subscribe();
        var seen = new List<ObjectKey>();
        var reader = Task.Run(async () =>
        {
            await foreach (var commit in subscription.Reader.ReadAllAsync())
            {
                Assert.Empty(commit.Removed);
                Assert.All(await source.HasKeysAsync(commit.Stored, None), Assert.True);
                seen.AddRange(commit.Stored);
            }
        });
        await source.GetKeysAsync([new(a.Key), new(b.Key, 400), new(a.Key)], Context, FetchPriority.Background, null, None);
        subscription.Dispose();   // ends the reader once it has drained what was published
        await reader;
        Assert.Equal(new[] { a.Key, b.Key }.Order(), seen.Order());
        Assert.False(subscription.TakeMissed());
    }

    [Fact]
    public async Task FetchingAStoredKeyPublishesNothing()
    {
        var a = Synthetic.Config(1);
        var source = Arrange([a]);
        await source.GetKeyAsync(new(a.Key), Context, FetchPriority.Interactive, None);
        using var subscription = ((IStoreCommits)source).Subscribe();
        await source.GetKeyAsync(new(a.Key), Context, FetchPriority.Interactive, None);
        await source.GetKeysAsync([new(a.Key)], Context, FetchPriority.Background, null, None);
        Assert.False(subscription.Reader.TryRead(out _));
        Assert.False(subscription.TakeMissed());
    }

    [Fact]
    public async Task ADisposedSubscriptionGetsNothingMore()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Config(2);
        var source = Arrange([a, b]);
        var commits = (IStoreCommits)source;
        var gone = commits.Subscribe();
        using var kept = commits.Subscribe();
        await source.GetKeyAsync(new(a.Key), Context, FetchPriority.Interactive, None);
        Assert.Equal([a.Key], Assert.Single(Drain(gone)).Stored);
        gone.Dispose();
        await source.GetKeyAsync(new(b.Key), Context, FetchPriority.Interactive, None);
        Assert.False(gone.Reader.TryRead(out _));
        Assert.True(gone.Reader.Completion.IsCompleted);
        Assert.Equal([a.Key, b.Key], Drain(kept).SelectMany(c => c.Stored));
    }

    private static List<StoreCommit> Drain(StoreCommitSubscription subscription)
    {
        var commits = new List<StoreCommit>();
        while (subscription.Reader.TryRead(out var commit)) commits.Add(commit);
        return commits;
    }
}

public sealed class RealObjectSourceContractTests : ObjectSourceContract
{
    protected override IObjectSource Arrange(IReadOnlyList<PutObject> available)
    {
        var s = new Scenario();
        foreach (var o in available) s.Serve(o);
        s.Definitions.Add(TableName, Revision, MapDbd);
        return s.Source;
    }
}

public sealed class InMemoryObjectSourceContractTests : ObjectSourceContract
{
    protected override IObjectSource Arrange(IReadOnlyList<PutObject> available)
    {
        var fake = new InMemoryObjectSource();
        foreach (var o in available) fake.Add(o.Key, o.Bytes.ToArray());
        fake.AddReply("wow", "us", ObservationKind.Versions, "Region!STRING:0\nus\n"u8.ToArray());
        fake.AddReply("", "us", ObservationKind.Summary, "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\nwow|1|\n"u8.ToArray());
        fake.AddDefinition(TableId, TableName, Revision, MapDbd);
        return fake;
    }
}
