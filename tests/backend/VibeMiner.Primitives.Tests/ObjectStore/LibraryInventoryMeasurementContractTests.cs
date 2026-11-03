using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Measurements report committed-index coverage, without promising verified physical payloads.</summary>
public abstract class LibraryInventoryMeasurementContractTests : IAsyncLifetime
{
    protected ILibraryInventory Inventory { get; private set; } = null!;
    protected abstract Task<ILibraryInventory> CreateAsync();
    protected abstract Task IndexAsync(PutObject item);
    protected abstract Task CloseAsync();
    public async Task InitializeAsync() => Inventory = await CreateAsync();
    public Task DisposeAsync() => CloseAsync();

    private async Task<LibraryInventoryGeneration> PublishAsync(params ObjectKey[] keys)
    {
        var stage = await Inventory.BeginAsync();
        foreach (var chunk in keys.Chunk(1024)) await Inventory.AppendAsync(stage, chunk);
        await Inventory.PublishAsync(stage);
        return stage;
    }

    internal static void AssertMeasured(LibraryInventoryMeasurement actual, long generation, long expected, long present, long bytes)
    {
        Assert.Equal(generation, actual.Generation);
        Assert.Equal(expected, actual.ExpectedKeys);
        Assert.Equal(present, actual.IndexedPresentKeys);
        Assert.Equal(expected - present, actual.MissingKeys);
        Assert.Equal(bytes, actual.IndexedPayloadBytes);
        Assert.Equal(actual.ExpectedKeys, actual.IndexedPresentKeys + actual.MissingKeys);
    }

    [Fact]
    public async Task UnpublishedHasUnknownMeasurementsAndPublishedEmptyHasMeasuredZeros()
    {
        Assert.Equal(new LibraryInventoryMeasurement(null, null, null, null, null), await Inventory.MeasureAsync());
        var staging = await Inventory.BeginAsync();
        await Inventory.AppendAsync(staging, [Key(1)]);
        Assert.Equal(new LibraryInventoryMeasurement(null, null, null, null, null), await Inventory.MeasureAsync());
        await Inventory.AbandonAsync(staging);
        var empty = await PublishAsync();
        AssertMeasured(await Inventory.MeasureAsync(), empty.Value, 0, 0, 0);
    }

    [Fact]
    public async Task AllMissingUniqueKeysHaveZeroIndexedBytesAndNoInventedMissingLength()
    {
        var generation = await PublishAsync(Key(1), Key(2), Key(1));
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 2, 0, 0);
    }

    [Fact]
    public async Task UniqueTypedKeysCountOnlyExpectedCommittedIndexRowsAndExcludeStagingAndExtras()
    {
        var original = Synthetic.Headerless(218, 117);
        var derived = original with { Key = new ObjectKey(ObjectKeyType.Generated, original.Key.KeyBytes()), Bytes = new byte[23], DeclaredLength = 23 };
        var extra = Synthetic.Headerless(219, 1000);
        await IndexAsync(original);
        await IndexAsync(derived);
        await IndexAsync(extra);
        var generation = await PublishAsync(original.Key, derived.Key, original.Key, Key(999));
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 3, 2, 140);
        var staging = await Inventory.BeginAsync();
        await Inventory.AppendAsync(staging, [extra.Key]);
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 3, 2, 140);
        await Inventory.AbandonAsync(staging);
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 3, 2, 140);
    }

    [Fact]
    public async Task ThousandsOfExpectedKeysAreMeasuredWithoutAnyPagingRequirementForTheCaller()
    {
        var keys = Keys(1, 3507);
        var original = Synthetic.Headerless(220, 17);
        await IndexAsync(original);
        var generation = await PublishAsync([.. keys, original.Key, original.Key]);
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 3508, 1, 17);
    }

    [Fact]
    public async Task PreCanceledMeasurementReturnsCancellationAndLeavesTheInventoryIntact()
    {
        var generation = await PublishAsync(Key(1), Key(2));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Inventory.MeasureAsync(canceled.Token));
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 2, 0, 0);
    }

    [Fact]
    public async Task ClosedMeasurementHasAnExplicitClosedFailure()
    {
        await PublishAsync(Key(1));
        await CloseAsync();
        Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Inventory.MeasureAsync())).Code);
    }
}

public sealed class LibraryInventoryMeasurementContractOnRealStore : LibraryInventoryMeasurementContractTests
{
    private readonly TempDirectory temp = new();
    private ObjectStore library = null!;
    protected override async Task<ILibraryInventory> CreateAsync()
    {
        library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
        return library.ForOwner("expected", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original).Inventory;
    }
    protected override async Task IndexAsync(PutObject item) => Assert.Equal(PutOutcome.Stored,
        Assert.Single(await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
}

public sealed class LibraryInventoryMeasurementContractOnFake : LibraryInventoryMeasurementContractTests
{
    private readonly InMemoryLibraryInventories library = new();
    protected override Task<ILibraryInventory> CreateAsync() => Task.FromResult(library.ForOwner("expected", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original));
    protected override Task IndexAsync(PutObject item) { library.SetIndexedObject(item.Key, item.DeclaredLength); return Task.CompletedTask; }
    protected override async Task CloseAsync() => await library.DisposeAsync();

    [Fact]
    public async Task RemovingIndexPresenceChangesCoverageWithoutChangingTheExpectedGeneration()
    {
        var key = Key(1);
        var generation = await Inventory.BeginAsync();
        await Inventory.AppendAsync(generation, [key]);
        await Inventory.PublishAsync(generation);
        library.SetIndexedObject(key, 4321);
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 1, 1, 4321);
        library.RemoveIndexedObject(key);
        AssertMeasured(await Inventory.MeasureAsync(), generation.Value, 1, 0, 0);
    }

    [Fact]
    public async Task IndexedByteSumsAreCheckedInsteadOfWrappingToAPartialSuccess()
    {
        var generation = await Inventory.BeginAsync();
        await Inventory.AppendAsync(generation, [Key(1), Key(2)]);
        await Inventory.PublishAsync(generation);
        library.SetIndexedObject(Key(1), long.MaxValue);
        library.SetIndexedObject(Key(2), 1);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Inventory.MeasureAsync())).Code);
    }
}
