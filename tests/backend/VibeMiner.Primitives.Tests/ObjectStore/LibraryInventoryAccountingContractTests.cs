using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.LibraryInventoryUnionContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryPairContractTests;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class LibraryInventoryAccountingContractTests : IAsyncLifetime
{
    protected abstract Task OpenAsync();
    protected abstract Task CloseAsync();
    protected abstract ILibraryInventory Producer(string owner, LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original, string producer = "one");
    protected abstract ILibraryInventoryAccounting Accounting();
    protected abstract Task IndexAsync(PutObject item);
    public Task InitializeAsync() => OpenAsync();
    public Task DisposeAsync() => CloseAsync();

    internal static void AssertAccountingReconciles(LibraryInventoryAccountingMeasurement result)
    {
        var union = Assert.IsType<LibraryInventoryCoverage>(result.Union);
        var partitions = new[]
        {
            new[] { result.SingleOwner, result.MultipleOwners },
            new[] { result.OriginalOnly, result.DerivedOnly, result.BothRoles },
            new[] { result.BuildOnly, result.GlobalOnly, result.BothScopes },
        };
        foreach (var partition in partitions)
        {
            var buckets = partition.Select(bucket => Assert.IsType<LibraryInventoryCoverage>(bucket)).ToArray();
            Assert.Equal(union.ExpectedKeys, buckets.Sum(bucket => bucket.ExpectedKeys));
            Assert.Equal(union.IndexedPresentKeys, buckets.Sum(bucket => bucket.IndexedPresentKeys));
            Assert.Equal(union.MissingKeys, buckets.Sum(bucket => bucket.MissingKeys));
            Assert.Equal(union.IndexedPayloadBytes, buckets.Sum(bucket => bucket.IndexedPayloadBytes));
        }
        Assert.Equal(union.ExpectedKeys, union.IndexedPresentKeys + union.MissingKeys);
        Assert.Equal(result.IndexedObjects, union.IndexedPresentKeys + result.OutsideUnionIndexedObjects);
        Assert.Equal(result.IndexedPayloadBytes, union.IndexedPayloadBytes + result.OutsideUnionIndexedPayloadBytes);
        Assert.True(result.OwnerReferences >= union.ExpectedKeys);
        Assert.True(result.IndexedOwnerReferences >= union.IndexedPresentKeys);
        Assert.True(result.OwnerCreditedIndexedPayloadBytes >= union.IndexedPayloadBytes);
    }

    internal static void AssertInventoryUnknown(LibraryInventoryAccountingMeasurement result)
    {
        Assert.Null(result.Union); Assert.Null(result.OutsideUnionIndexedObjects); Assert.Null(result.OutsideUnionIndexedPayloadBytes);
        Assert.Null(result.OwnerReferences); Assert.Null(result.IndexedOwnerReferences); Assert.Null(result.OwnerCreditedIndexedPayloadBytes);
        Assert.Null(result.SingleOwner); Assert.Null(result.MultipleOwners); Assert.Null(result.OriginalOnly); Assert.Null(result.DerivedOnly);
        Assert.Null(result.BothRoles); Assert.Null(result.BuildOnly); Assert.Null(result.GlobalOnly); Assert.Null(result.BothScopes);
    }

    [Fact]
    public async Task IndependentIndexTotalsAreKnownWithoutPublishedInventoriesAndPublishedEmptyHasZeroBuckets()
    {
        await IndexAsync(Synthetic.Headerless(2221, 101));
        _ = Producer("not-registered");
        var empty = await Accounting().MeasureAsync();
        Assert.Equal(0, empty.PublishedInventories); Assert.Equal(0, empty.UnpublishedInventories);
        Assert.Equal(1, empty.IndexedObjects); Assert.Equal(101, empty.IndexedPayloadBytes); AssertInventoryUnknown(empty);
        var pending = Producer("pending"); var stage = await pending.BeginAsync(); await pending.AppendAsync(stage, [Key(1)]);
        var staged = await Accounting().MeasureAsync();
        Assert.Equal(0, staged.PublishedInventories); Assert.Equal(1, staged.UnpublishedInventories); AssertInventoryUnknown(staged);
        await PublishAsync(Producer("empty"));
        var measured = await Accounting().MeasureAsync();
        Assert.Equal(1, measured.PublishedInventories); Assert.Equal(1, measured.UnpublishedInventories);
        AssertCoverage(measured.Union, 0, 0, 0); Assert.Equal(1, measured.OutsideUnionIndexedObjects); Assert.Equal(101, measured.OutsideUnionIndexedPayloadBytes);
        Assert.Equal(0, measured.OwnerReferences); Assert.Equal(0, measured.OwnerCreditedIndexedPayloadBytes); AssertAccountingReconciles(measured);
    }

    [Fact]
    public async Task LogicalOwnersDeduplicateAcrossRolesAndProducersAndEveryDisjointPartitionReconciles()
    {
        var original = Synthetic.Headerless(2222, 47);
        var typedCollision = original with { Key = new ObjectKey(ObjectKeyType.Generated, original.Key.KeyBytes()), Bytes = new byte[31], DeclaredLength = 31 };
        var scopes = new PutObject(Key(10), 59, new byte[59], ObjectOrigin.Unknown);
        var derivedScopes = new PutObject(Key(11), 23, new byte[23], ObjectOrigin.Unknown);
        foreach (var item in new[] { original, typedCollision, scopes, derivedScopes, Synthetic.Headerless(2223, 101) }) await IndexAsync(item);
        await PublishAsync(Producer("a"), original.Key, typedCollision.Key, Key(1), scopes.Key);
        await PublishAsync(Producer("a", producer: "two"), original.Key, original.Key);
        await PublishAsync(Producer("a", role: LibraryObjectRole.Derived), original.Key);
        await PublishAsync(Producer("b", role: LibraryObjectRole.Derived), typedCollision.Key, derivedScopes.Key);
        await PublishAsync(Producer("g", LibraryOwnerScope.Global, LibraryObjectRole.Derived), Key(2), derivedScopes.Key);
        await PublishAsync(Producer("a", LibraryOwnerScope.Global), scopes.Key);
        await Producer("pending").BeginAsync();
        var result = await Accounting().MeasureAsync();
        Assert.Equal(6, result.PublishedInventories); Assert.Equal(1, result.UnpublishedInventories);
        Assert.Equal(5, result.IndexedObjects); Assert.Equal(261, result.IndexedPayloadBytes);
        AssertCoverage(result.Union, 6, 4, 160);
        Assert.Equal(9, result.OwnerReferences); Assert.Equal(7, result.IndexedOwnerReferences); Assert.Equal(273, result.OwnerCreditedIndexedPayloadBytes);
        AssertCoverage(result.SingleOwner, 3, 1, 47); AssertCoverage(result.MultipleOwners, 3, 3, 113);
        AssertCoverage(result.OriginalOnly, 2, 1, 59); AssertCoverage(result.DerivedOnly, 2, 1, 23); AssertCoverage(result.BothRoles, 2, 2, 78);
        AssertCoverage(result.BuildOnly, 3, 2, 78); AssertCoverage(result.GlobalOnly, 1, 0, 0); AssertCoverage(result.BothScopes, 2, 2, 82);
        AssertAccountingReconciles(result);
    }

    [Fact]
    public async Task InactiveAndStagedRefreshKeysAreExcludedUntilPublication()
    {
        var producer = Producer("a");
        await PublishAsync(producer, Key(1)); await PublishAsync(Producer("b"), Key(1), Key(2));
        var refresh = await producer.BeginAsync(); await producer.AppendAsync(refresh, [Key(3)]);
        AssertCoverage((await Accounting().MeasureAsync()).Union, 2, 0, 0);
        await producer.PublishAsync(refresh);
        var result = await Accounting().MeasureAsync();
        AssertCoverage(result.Union, 3, 0, 0); Assert.Equal(3, result.OwnerReferences);
        AssertCoverage(result.MultipleOwners, 0, 0, 0); AssertAccountingReconciles(result);
    }

    [Fact]
    public async Task LargeInventoriesStreamAcrossMultipleOwnersWithoutCountingRepeatedProducerReferences()
    {
        await PublishAsync(Producer("a"), Keys(1, 3507));
        await PublishAsync(Producer("a", producer: "duplicate"), Keys(1, 3507));
        await PublishAsync(Producer("b", role: LibraryObjectRole.Derived), Keys(2001, 3507));
        await PublishAsync(Producer("g", LibraryOwnerScope.Global), Keys(3001, 1000));
        var result = await Accounting().MeasureAsync();
        AssertCoverage(result.Union, 5507, 0, 0); Assert.Equal(8014, result.OwnerReferences);
        AssertCoverage(result.SingleOwner, 3507, 0, 0); AssertCoverage(result.MultipleOwners, 2000, 0, 0); AssertAccountingReconciles(result);
    }

    [Fact]
    public async Task Global512PublishedHeadBoundDoesNotSilentlyTruncateOrCapRegisteredUnpublishedInventories()
    {
        for (var i = 0; i < LibraryInventoryAccountingLimits.MaxPublishedInventories; i++) await PublishAsync(Producer($"owner-{i}"), Key(1));
        for (var i = 0; i < 520; i++) await Producer($"pending-{i}").BeginAsync();
        var full = await Accounting().MeasureAsync();
        Assert.Equal(512, full.PublishedInventories); Assert.Equal(520, full.UnpublishedInventories);
        AssertCoverage(full.Union, 1, 0, 0); Assert.Equal(512, full.OwnerReferences); AssertAccountingReconciles(full);
        await PublishAsync(Producer("over-limit"), Key(2));
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Accounting().MeasureAsync())).Code);
    }

    [Fact]
    public async Task CanceledAndClosedAccountingNeverReturnsFabricatedZeros()
    {
        var accounting = Accounting(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await accounting.MeasureAsync(canceled.Token));
        await CloseAsync();
        Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await accounting.MeasureAsync())).Code);
    }
}

public sealed class LibraryInventoryAccountingContractOnRealStore : LibraryInventoryAccountingContractTests
{
    private readonly TempDirectory temp = new(); private ObjectStore library = null!;
    protected override async Task OpenAsync() => library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
    protected override ILibraryInventory Producer(string owner, LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original, string producer = "one") => library.ForOwner(owner, scope, producer, role).Inventory;
    protected override ILibraryInventoryAccounting Accounting() => library.ForInventoryAccounting();
    protected override async Task IndexAsync(PutObject item) => Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
}

public sealed class LibraryInventoryAccountingContractOnFake : LibraryInventoryAccountingContractTests
{
    private readonly InMemoryLibraryInventories library = new();
    protected override Task OpenAsync() => Task.CompletedTask;
    protected override async Task CloseAsync() => await library.DisposeAsync();
    protected override ILibraryInventory Producer(string owner, LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original, string producer = "one") => library.ForOwner(owner, scope, producer, role);
    protected override ILibraryInventoryAccounting Accounting() => library.ForInventoryAccounting();
    protected override Task IndexAsync(PutObject item) { library.SetIndexedObject(item.Key, item.DeclaredLength); return Task.CompletedTask; }

    [Fact]
    public async Task IndependentIndexByteOverflowFailsEvenWithoutPublishedInventories()
    {
        library.SetIndexedObject(Key(1), long.MaxValue); library.SetIndexedObject(Key(2), 1);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Accounting().MeasureAsync())).Code);
    }

    [Fact]
    public async Task LogicalCreditOverflowFailsEvenWhenUniqueIndexedBytesFit()
    {
        await PublishAsync(Producer("a"), Key(1)); await PublishAsync(Producer("b"), Key(1));
        library.SetIndexedObject(Key(1), long.MaxValue);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Accounting().MeasureAsync())).Code);
        library.SetIndexedObject(Key(1), 11); library.RemoveIndexedObject(Key(1));
        var result = await Accounting().MeasureAsync(); AssertCoverage(result.Union, 1, 0, 0); AssertAccountingReconciles(result);
    }
}
