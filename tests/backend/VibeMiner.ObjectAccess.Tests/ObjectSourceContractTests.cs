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
    public async Task OnlyStoredKeysCountAsPresent()
    {
        var a = Synthetic.Config(1); var b = Synthetic.Config(2); var c = Synthetic.Config(3);
        var source = Arrange([a, b]);
        Assert.Equal(0, await source.CountPresentAsync([a.Key, b.Key, c.Key], None));
        await source.GetKeysAsync([new(a.Key), new(b.Key)], Context, FetchPriority.Background, null, None);
        Assert.Equal(2, await source.CountPresentAsync([a.Key, b.Key, c.Key], None));
    }
}

public sealed class RealObjectSourceContractTests : ObjectSourceContract
{
    protected override IObjectSource Arrange(IReadOnlyList<PutObject> available)
    {
        var s = new Scenario();
        foreach (var o in available) s.Serve(o);
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
        return fake;
    }
}
