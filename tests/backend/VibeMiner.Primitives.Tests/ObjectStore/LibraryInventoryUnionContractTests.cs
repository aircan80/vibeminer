using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class LibraryInventoryUnionContractTests : IAsyncLifetime
{
    protected abstract Task OpenAsync();
    protected abstract Task CloseAsync();
    protected abstract ILibraryInventory Producer(string producer, string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original);
    protected abstract ILibraryInventoryUnion Union(string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original);
    protected abstract Task IndexAsync(PutObject item);
    public Task InitializeAsync() => OpenAsync();
    public Task DisposeAsync() => CloseAsync();

    internal static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, params ObjectKey[] keys)
    {
        var generation = await inventory.BeginAsync();
        foreach (var batch in keys.Chunk(1024)) await inventory.AppendAsync(generation, batch);
        await inventory.PublishAsync(generation);
        return generation;
    }

    internal static void AssertUnion(LibraryInventoryUnionMeasurement result, long published, long unpublished, long? expected, long? present, long? bytes)
    {
        Assert.Equal(published, result.PublishedProducers);
        Assert.Equal(unpublished, result.UnpublishedProducers);
        Assert.Equal(expected, result.ExpectedKeys);
        Assert.Equal(present, result.IndexedPresentKeys);
        Assert.Equal(expected - present, result.MissingKeys);
        Assert.Equal(bytes, result.IndexedPayloadBytes);
        Assert.Equal(result.ExpectedKeys, result.IndexedPresentKeys + result.MissingKeys);
    }

    [Fact]
    public async Task CapturingViewsDoesNotRegisterProducersAndOnlyPublishedEmptyReturnsZeros()
    {
        var union = Union();
        _ = Producer("not-started");
        AssertUnion(await union.MeasureAsync(), 0, 0, null, null, null);
        var first = Producer("pending");
        var stage = await first.BeginAsync();
        await first.AppendAsync(stage, [Key(1)]);
        AssertUnion(await union.MeasureAsync(), 0, 1, null, null, null);
        await first.AbandonAsync(stage);
        AssertUnion(await union.MeasureAsync(), 0, 1, null, null, null);
        await PublishAsync(Producer("empty"));
        AssertUnion(await union.MeasureAsync(), 1, 1, 0, 0, 0);
    }

    [Fact]
    public async Task FullTypedKeysDeduplicateAcrossProducersBeforeCountingOrSummingBytes()
    {
        var original = Synthetic.Headerless(219, 47);
        var derived = original with { Key = new ObjectKey(ObjectKeyType.Generated, original.Key.KeyBytes()), Bytes = new byte[31], DeclaredLength = 31 };
        await IndexAsync(original);
        await IndexAsync(derived);
        await IndexAsync(Synthetic.Headerless(220, 2000));
        await PublishAsync(Producer("one"), original.Key, derived.Key, Key(1), derived.Key);
        await PublishAsync(Producer("two"), original.Key, derived.Key, Key(2));
        await Producer("unpublished").BeginAsync();
        AssertUnion(await Union().MeasureAsync(), 2, 1, 4, 2, 78);
    }

    [Fact]
    public async Task ExactOwnerScopeAndRoleExcludeUnrelatedPublishedAndUnpublishedIdentities()
    {
        await PublishAsync(Producer("same"), Key(1));
        foreach (var other in new[]
        {
            Producer("same", owner: "build-b"), Producer("same", scope: LibraryOwnerScope.Global),
            Producer("same", role: LibraryObjectRole.Derived), Producer("pending", owner: "build-b"),
        }) await PublishAsync(other, Key(2), Key(3));
        await Producer("pending", scope: LibraryOwnerScope.Global).BeginAsync();
        AssertUnion(await Union().MeasureAsync(), 1, 0, 1, 0, 0);
        AssertUnion(await Union("build-b").MeasureAsync(), 2, 0, 2, 0, 0);
        AssertUnion(await Union(scope: LibraryOwnerScope.Global).MeasureAsync(), 1, 1, 2, 0, 0);
        AssertUnion(await Union(role: LibraryObjectRole.Derived).MeasureAsync(), 1, 0, 2, 0, 0);
    }

    [Fact]
    public async Task ProducerRefreshUsesThePriorActiveSetUntilCompleteReplacementIsPublished()
    {
        var one = Producer("one");
        await PublishAsync(one, Key(1), Key(2));
        await PublishAsync(Producer("two"), Key(2), Key(3));
        var staged = await one.BeginAsync();
        await one.AppendAsync(staged, [Key(10)]);
        AssertUnion(await Union().MeasureAsync(), 2, 0, 3, 0, 0);
        await one.PublishAsync(staged);
        AssertUnion(await Union().MeasureAsync(), 2, 0, 3, 0, 0);
        await PublishAsync(one);
        AssertUnion(await Union().MeasureAsync(), 2, 0, 2, 0, 0);
    }

    [Fact]
    public async Task MultiBatchUnionDeduplicatesThousandsOfKeysAcrossProducers()
    {
        await PublishAsync(Producer("one"), Keys(1, 3507));
        await PublishAsync(Producer("two"), Keys(2001, 3507));
        await PublishAsync(Producer("three"), Keys(3001, 1000));
        AssertUnion(await Union().MeasureAsync(), 3, 0, 5507, 0, 0);
    }

    [Fact]
    public async Task ActiveProducerBoundRefusesOverflowWithoutFabricatingPartialTotals()
    {
        var union = Union();
        for (var i = 0; i < LibraryInventoryUnionLimits.MaxPublishedProducers; i++) await PublishAsync(Producer($"p-{i}"), Key(1));
        for (var i = 0; i < 270; i++) await Producer($"pending-{i}").BeginAsync();
        AssertUnion(await union.MeasureAsync(), LibraryInventoryUnionLimits.MaxPublishedProducers, 270, 1, 0, 0);
        await PublishAsync(Producer("over-limit"), Key(2));
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await union.MeasureAsync())).Code);
    }

    [Fact]
    public void UnionFactoryValidatesTheCapturedOwnerScopeAndRole()
    {
        Assert.ThrowsAny<ArgumentException>(() => Union(owner: " "));
        Assert.ThrowsAny<ArgumentException>(() => Union(scope: (LibraryOwnerScope)99));
        Assert.ThrowsAny<ArgumentException>(() => Union(role: (LibraryObjectRole)99));
    }

    [Fact]
    public async Task CanceledAndClosedMeasurementsHaveExplicitFailures()
    {
        var union = Union();
        await PublishAsync(Producer("one"), Key(1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await union.MeasureAsync(canceled.Token));
        AssertUnion(await union.MeasureAsync(), 1, 0, 1, 0, 0);
        await CloseAsync();
        Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await union.MeasureAsync())).Code);
    }
}

public sealed class LibraryInventoryUnionContractOnRealStore : LibraryInventoryUnionContractTests
{
    private readonly TempDirectory temp = new();
    private ObjectStore library = null!;
    protected override async Task OpenAsync() => library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
    protected override ILibraryInventory Producer(string producer, string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role).Inventory;
    protected override ILibraryInventoryUnion Union(string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => library.ForInventoryOwner(owner, scope, role);
    protected override async Task IndexAsync(PutObject item) => Assert.Equal(PutOutcome.Stored,
        Assert.Single(await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
}

public sealed class LibraryInventoryUnionContractOnFake : LibraryInventoryUnionContractTests
{
    private readonly InMemoryLibraryInventories library = new();
    protected override Task OpenAsync() => Task.CompletedTask;
    protected override async Task CloseAsync() => await library.DisposeAsync();
    protected override ILibraryInventory Producer(string producer, string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role);
    protected override ILibraryInventoryUnion Union(string owner = "build-a", LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => library.ForInventoryOwner(owner, scope, role);
    protected override Task IndexAsync(PutObject item) { library.SetIndexedObject(item.Key, item.DeclaredLength); return Task.CompletedTask; }

    [Fact]
    public async Task CheckedByteOverflowIsAnExplicitMetadataFailure()
    {
        await PublishAsync(Producer("one"), Key(1), Key(2));
        await PublishAsync(Producer("two"), Key(1));
        library.SetIndexedObject(Key(1), long.MaxValue);
        library.SetIndexedObject(Key(2), 1);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Union().MeasureAsync())).Code);
        library.RemoveIndexedObject(Key(2));
        AssertUnion(await Union().MeasureAsync(), 2, 0, 2, 1, long.MaxValue);
    }
}
