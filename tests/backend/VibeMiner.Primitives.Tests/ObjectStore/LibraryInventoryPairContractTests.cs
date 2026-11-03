using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.LibraryInventoryUnionContractTests;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class LibraryInventoryPairContractTests : IAsyncLifetime
{
    protected static readonly LibraryInventorySelection Left = new("build-a", LibraryOwnerScope.Build, LibraryObjectRole.Original);
    protected static readonly LibraryInventorySelection Right = new("build-b", LibraryOwnerScope.Global, LibraryObjectRole.Derived);
    protected abstract Task OpenAsync();
    protected abstract Task CloseAsync();
    protected abstract ILibraryInventory Producer(LibraryInventorySelection selection, string producer = "one");
    protected abstract ILibraryInventoryPair Pair(LibraryInventorySelection left, LibraryInventorySelection right);
    protected abstract Task IndexAsync(PutObject item);
    public Task InitializeAsync() => OpenAsync();
    public Task DisposeAsync() => CloseAsync();

    internal static void AssertCoverage(LibraryInventoryCoverage? actual, long expected, long present, long bytes)
    {
        Assert.Equal(new LibraryInventoryCoverage(expected, present, expected - present, bytes), actual);
    }

    internal static void AssertReconciles(LibraryInventoryPairMeasurement result)
    {
        var union = Assert.IsType<LibraryInventoryCoverage>(result.Union);
        var intersection = Assert.IsType<LibraryInventoryCoverage>(result.Intersection);
        var leftOnly = Assert.IsType<LibraryInventoryCoverage>(result.LeftOnly);
        var rightOnly = Assert.IsType<LibraryInventoryCoverage>(result.RightOnly);
        static void Sum(long? side, long only, long shared) => Assert.Equal(side, checked(only + shared));
        Sum(result.Left.ExpectedKeys, leftOnly.ExpectedKeys, intersection.ExpectedKeys);
        Sum(result.Left.IndexedPresentKeys, leftOnly.IndexedPresentKeys, intersection.IndexedPresentKeys);
        Sum(result.Left.MissingKeys, leftOnly.MissingKeys, intersection.MissingKeys);
        Sum(result.Left.IndexedPayloadBytes, leftOnly.IndexedPayloadBytes, intersection.IndexedPayloadBytes);
        Sum(result.Right.ExpectedKeys, rightOnly.ExpectedKeys, intersection.ExpectedKeys);
        Sum(result.Right.IndexedPresentKeys, rightOnly.IndexedPresentKeys, intersection.IndexedPresentKeys);
        Sum(result.Right.MissingKeys, rightOnly.MissingKeys, intersection.MissingKeys);
        Sum(result.Right.IndexedPayloadBytes, rightOnly.IndexedPayloadBytes, intersection.IndexedPayloadBytes);
        Assert.Equal(union.ExpectedKeys, leftOnly.ExpectedKeys + intersection.ExpectedKeys + rightOnly.ExpectedKeys);
        Assert.Equal(union.IndexedPresentKeys, leftOnly.IndexedPresentKeys + intersection.IndexedPresentKeys + rightOnly.IndexedPresentKeys);
        Assert.Equal(union.MissingKeys, leftOnly.MissingKeys + intersection.MissingKeys + rightOnly.MissingKeys);
        Assert.Equal(union.IndexedPayloadBytes, leftOnly.IndexedPayloadBytes + intersection.IndexedPayloadBytes + rightOnly.IndexedPayloadBytes);
    }

    [Fact]
    public async Task UnpublishedSidePreservesKnownSideButPairBucketsStayUnknownUntilPublishedEmpty()
    {
        var pair = Pair(Left, Right);
        var unknown = await pair.MeasureAsync();
        AssertUnion(unknown.Left, 0, 0, null, null, null);
        AssertUnion(unknown.Right, 0, 0, null, null, null);
        Assert.Null(unknown.Union);
        await PublishAsync(Producer(Left), Key(1));
        var stage = await Producer(Right).BeginAsync();
        await Producer(Right).AppendAsync(stage, [Key(2)]);
        var partial = await pair.MeasureAsync();
        AssertUnion(partial.Left, 1, 0, 1, 0, 0);
        AssertUnion(partial.Right, 0, 1, null, null, null);
        Assert.Null(partial.Union); Assert.Null(partial.Intersection); Assert.Null(partial.LeftOnly); Assert.Null(partial.RightOnly);
        var reversed = await Pair(Right, Left).MeasureAsync();
        AssertUnion(reversed.Left, 0, 1, null, null, null); AssertUnion(reversed.Right, 1, 0, 1, 0, 0);
        Assert.Null(reversed.Union); Assert.Null(reversed.Intersection); Assert.Null(reversed.LeftOnly); Assert.Null(reversed.RightOnly);
        await Producer(Right).AbandonAsync(stage);
        await PublishAsync(Producer(Right));
        var measured = await pair.MeasureAsync();
        AssertUnion(measured.Right, 1, 0, 0, 0, 0);
        AssertCoverage(measured.Union, 1, 0, 0); AssertCoverage(measured.Intersection, 0, 0, 0);
        AssertCoverage(measured.LeftOnly, 1, 0, 0); AssertCoverage(measured.RightOnly, 0, 0, 0);
        AssertReconciles(measured);
    }

    [Fact]
    public async Task PairDeduplicatesTypedKeysAcrossProducersAndReconcilesEveryBucket()
    {
        var original = Synthetic.Headerless(2201, 47);
        var derived = original with { Key = new ObjectKey(ObjectKeyType.Generated, original.Key.KeyBytes()), Bytes = new byte[31], DeclaredLength = 31 };
        await IndexAsync(original); await IndexAsync(derived); await IndexAsync(Synthetic.Headerless(2202, 1000));
        await PublishAsync(Producer(Left), original.Key, derived.Key, Key(1), derived.Key);
        await PublishAsync(Producer(Left, "two"), original.Key, Key(2));
        await PublishAsync(Producer(Right), original.Key, derived.Key, Key(2), Key(3));
        await Producer(Right, "pending").BeginAsync();
        var result = await Pair(Left, Right).MeasureAsync();
        AssertUnion(result.Left, 2, 0, 4, 2, 78); AssertUnion(result.Right, 1, 1, 4, 2, 78);
        AssertCoverage(result.Union, 5, 2, 78); AssertCoverage(result.Intersection, 3, 2, 78);
        AssertCoverage(result.LeftOnly, 1, 0, 0); AssertCoverage(result.RightOnly, 1, 0, 0);
        AssertReconciles(result);
    }

    [Fact]
    public async Task DisjointIdenticalAndPublishedEmptySelectionsHavePairLocalMeanings()
    {
        await PublishAsync(Producer(Left), Key(1), Key(2));
        await PublishAsync(Producer(Right), Key(3));
        var disjoint = await Pair(Left, Right).MeasureAsync();
        AssertCoverage(disjoint.Union, 3, 0, 0); AssertCoverage(disjoint.Intersection, 0, 0, 0);
        AssertCoverage(disjoint.LeftOnly, 2, 0, 0); AssertCoverage(disjoint.RightOnly, 1, 0, 0); AssertReconciles(disjoint);
        var identical = await Pair(Left, Left).MeasureAsync();
        AssertCoverage(identical.Union, 2, 0, 0); AssertCoverage(identical.Intersection, 2, 0, 0);
        AssertCoverage(identical.LeftOnly, 0, 0, 0); AssertCoverage(identical.RightOnly, 0, 0, 0); AssertReconciles(identical);
        await PublishAsync(Producer(Left)); await PublishAsync(Producer(Right));
        var empty = await Pair(Left, Right).MeasureAsync();
        AssertCoverage(empty.Union, 0, 0, 0); AssertCoverage(empty.Intersection, 0, 0, 0); AssertReconciles(empty);
    }

    [Fact]
    public async Task ScopeRoleAndUnrelatedThirdOwnersRemainExcluded()
    {
        await PublishAsync(Producer(Left), Key(1));
        var global = new LibraryInventorySelection(Left.Owner, LibraryOwnerScope.Global, Left.Role);
        var derived = new LibraryInventorySelection(Left.Owner, Left.Scope, LibraryObjectRole.Derived);
        var third = new LibraryInventorySelection("third", Left.Scope, Left.Role);
        await PublishAsync(Producer(global), Key(1), Key(2));
        await PublishAsync(Producer(derived), Key(1), Key(3));
        await PublishAsync(Producer(third), Key(1), Key(99));
        var scopes = await Pair(Left, global).MeasureAsync();
        AssertCoverage(scopes.Union, 2, 0, 0); AssertCoverage(scopes.Intersection, 1, 0, 0); AssertReconciles(scopes);
        var roles = await Pair(Left, derived).MeasureAsync();
        AssertCoverage(roles.Union, 2, 0, 0); AssertCoverage(roles.Intersection, 1, 0, 0); AssertReconciles(roles);
    }

    [Fact]
    public async Task LargeMultiBatchOverlapIsDeduplicatedBeforePairAccounting()
    {
        await PublishAsync(Producer(Left), Keys(1, 3507));
        await PublishAsync(Producer(Left, "two"), Keys(1001, 2000));
        await PublishAsync(Producer(Right), Keys(2001, 3507));
        var result = await Pair(Left, Right).MeasureAsync();
        AssertCoverage(result.Union, 5507, 0, 0); AssertCoverage(result.Intersection, 1507, 0, 0);
        AssertCoverage(result.LeftOnly, 2000, 0, 0); AssertCoverage(result.RightOnly, 2000, 0, 0); AssertReconciles(result);
    }

    [Fact]
    public async Task PerSideBoundsAllow512HeadsAndRefuseTheEntirePairBeyondEither256()
    {
        for (var i = 0; i < LibraryInventoryUnionLimits.MaxPublishedProducers; i++)
        {
            await PublishAsync(Producer(Left, $"p-{i}"), Key(1));
            await PublishAsync(Producer(Right, $"p-{i}"), Key(1));
        }
        var result = await Pair(Left, Right).MeasureAsync();
        AssertUnion(result.Left, 256, 0, 1, 0, 0); AssertUnion(result.Right, 256, 0, 1, 0, 0);
        AssertCoverage(result.Intersection, 1, 0, 0); AssertReconciles(result);
        AssertReconciles(await Pair(Left, Left).MeasureAsync());
        await PublishAsync(Producer(Right, "over-limit"), Key(2));
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Pair(Left, Right).MeasureAsync())).Code);
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Pair(Right, Left).MeasureAsync())).Code);
    }

    [Fact]
    public async Task PairLifetimeAndCancellationReturnEstablishedFailures()
    {
        var pair = Pair(Left, Right);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pair.MeasureAsync(canceled.Token));
        await CloseAsync();
        Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await pair.MeasureAsync())).Code);
    }
}

public sealed class LibraryInventoryPairContractOnRealStore : LibraryInventoryPairContractTests
{
    private readonly TempDirectory temp = new(); private ObjectStore library = null!;
    protected override async Task OpenAsync() => library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
    protected override ILibraryInventory Producer(LibraryInventorySelection selection, string producer = "one") => library.ForOwner(selection.Owner, selection.Scope, producer, selection.Role).Inventory;
    protected override ILibraryInventoryPair Pair(LibraryInventorySelection left, LibraryInventorySelection right) => library.ForInventoryPair(left, right);
    protected override async Task IndexAsync(PutObject item) => Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
}

public sealed class LibraryInventoryPairContractOnFake : LibraryInventoryPairContractTests
{
    private readonly InMemoryLibraryInventories library = new();
    protected override Task OpenAsync() => Task.CompletedTask;
    protected override async Task CloseAsync() => await library.DisposeAsync();
    protected override ILibraryInventory Producer(LibraryInventorySelection selection, string producer = "one") => library.ForOwner(selection.Owner, selection.Scope, producer, selection.Role);
    protected override ILibraryInventoryPair Pair(LibraryInventorySelection left, LibraryInventorySelection right) => library.ForInventoryPair(left, right);
    protected override Task IndexAsync(PutObject item) { library.SetIndexedObject(item.Key, item.DeclaredLength); return Task.CompletedTask; }

    [Fact]
    public async Task PairUnionByteOverflowFailsEvenWhenEachSideFits()
    {
        await PublishAsync(Producer(Left), Key(1)); await PublishAsync(Producer(Right), Key(2));
        library.SetIndexedObject(Key(1), long.MaxValue); library.SetIndexedObject(Key(2), 1);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Pair(Left, Right).MeasureAsync())).Code);
    }
}
