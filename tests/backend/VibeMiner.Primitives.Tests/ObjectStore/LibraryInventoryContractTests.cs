using VibeMiner.Objects;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Bounded expected-key semantics shared by library persistence and its test double.</summary>
public abstract class LibraryInventoryContractTests : IAsyncLifetime
{
    protected ILibraryInventory Inventory { get; private set; } = null!;
    protected abstract Task<ILibraryInventory> CreateAsync();
    protected virtual Task CloseAsync() => Task.CompletedTask;
    public async Task InitializeAsync() => Inventory = await CreateAsync();
    public Task DisposeAsync() => CloseAsync();

    protected static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, params ObjectKey[] keys)
    {
        var generation = await inventory.BeginAsync();
        foreach (var chunk in keys.Chunk(1024)) await inventory.AppendAsync(generation, chunk);
        await inventory.PublishAsync(generation);
        return generation;
    }

    [Fact]
    public async Task UnpublishedStagedAndPublishedEmptyAreDistinct()
    {
        var unpublished = await Inventory.ReadPageAsync();
        Assert.Null(unpublished.Generation);
        Assert.Empty(unpublished.Keys);
        Assert.Null(unpublished.Next);
        Assert.Null((await Inventory.LookupAsync(Key(1))).Generation);
        var staged = await Inventory.BeginAsync();
        await Inventory.AppendAsync(staged, [Key(1)]);
        Assert.Null((await Inventory.ReadPageAsync()).Generation);
        Assert.False((await Inventory.LookupAsync(Key(1))).Contains);
        await Inventory.AbandonAsync(staged);
        var empty = await PublishAsync(Inventory);
        var published = await Inventory.ReadPageAsync();
        Assert.Equal(empty.Value, published.Generation);
        Assert.Empty(published.Keys);
        Assert.Null(published.Next);
        var lookup = await Inventory.LookupAsync(Key(1));
        Assert.Equal(empty.Value, lookup.Generation);
        Assert.False(lookup.Contains);
    }

    [Fact]
    public async Task RefreshIsInvisibleUntilPublishAndAbandonPreservesTheOldSet()
    {
        var first = await PublishAsync(Inventory, Key(1), Key(2));
        var refresh = await Inventory.BeginAsync();
        await Inventory.AppendAsync(refresh, [Key(3), Key(4)]);
        Assert.Equal(first.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(1), Key(2)], (await Inventory.ReadPageAsync()).Keys);
        await Inventory.AbandonAsync(refresh);
        Assert.Equal(first.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.PublishAsync(refresh))).Code);
        var replacement = await PublishAsync(Inventory, Key(3));
        Assert.Equal(replacement.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.False((await Inventory.LookupAsync(Key(1))).Contains);
        Assert.True((await Inventory.LookupAsync(Key(3))).Contains);
    }

    [Fact]
    public async Task NewBeginFencesEveryEarlierTokenEvenIfTheNewerStageIsAbandoned()
    {
        var active = await PublishAsync(Inventory, Key(1));
        var stale = await Inventory.BeginAsync();
        await Inventory.AppendAsync(stale, [Key(2)]);
        var current = await Inventory.BeginAsync();
        await Inventory.AppendAsync(current, [Key(3)]);
        await Inventory.AbandonAsync(current);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.AppendAsync(stale, [Key(4)]))).Code);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.PublishAsync(stale))).Code);
        Assert.Equal(active.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(1)], (await Inventory.ReadPageAsync()).Keys);
    }

    [Fact]
    public async Task ThousandsOfOriginalAndGeneratedKeysDeduplicateAndTraverseInStableBoundedOrder()
    {
        var keys = Keys(1, 3507);
        var stage = await Inventory.BeginAsync();
        foreach (var batch in keys.Reverse().Chunk(700)) await Inventory.AppendAsync(stage, batch);
        await Inventory.AppendAsync(stage, [keys[2000], keys[1], keys[2000], keys[^1]]);
        await Inventory.PublishAsync(stage);
        var expected = keys.Order().ToArray();
        var actual = new List<ObjectKey>();
        LibraryInventoryCursor? cursor = null;
        var pages = 0;
        do
        {
            var page = await Inventory.ReadPageAsync(1024, cursor);
            Assert.Equal(stage.Value, page.Generation);
            Assert.InRange(page.Keys.Count, 1, 1024);
            actual.AddRange(page.Keys);
            cursor = page.Next;
            Assert.True(++pages <= 4, "Traversal did not terminate at the expected inventory bound.");
        } while (cursor is not null);
        Assert.Equal(4, pages);
        Assert.Equal(expected, actual);
        Assert.True((await Inventory.LookupAsync(keys[1500])).Contains);
        Assert.False((await Inventory.LookupAsync(Key(99999))).Contains);
    }

    [Fact]
    public async Task PageCursorsAreRejectedWhenTheActiveGenerationChanges()
    {
        await PublishAsync(Inventory, Key(1), Key(2), Key(3));
        var page = await Inventory.ReadPageAsync(1);
        Assert.NotNull(page.Next);
        var firstReplay = await Inventory.ReadPageAsync(1, page.Next);
        var secondReplay = await Inventory.ReadPageAsync(1, page.Next);
        Assert.Equal(firstReplay.Generation, secondReplay.Generation);
        Assert.Equal(firstReplay.Keys, secondReplay.Keys);
        await PublishAsync(Inventory, Key(10), Key(11));
        Assert.Equal(ObjectStoreCodes.InventoryCursorStale, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Inventory.ReadPageAsync(1, page.Next))).Code);
    }

    [Fact]
    public async Task OverlargeBatchesPoisonTheirStageAndLeaveTheActiveGenerationIntact()
    {
        var active = await PublishAsync(Inventory, Key(1));
        var stage = await Inventory.BeginAsync();
        await Inventory.AppendAsync(stage, [Key(2)]);
        var error = await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.AppendAsync(stage, Keys(10, 1025)));
        Assert.Equal(ObjectStoreCodes.Limit, error.Code);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.PublishAsync(stage))).Code);
        Assert.Equal(active.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(1)], (await Inventory.ReadPageAsync()).Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public async Task PageLimitsAreValidated(int limit)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Inventory.ReadPageAsync(limit));
        Assert.Null((await Inventory.ReadPageAsync()).Generation);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(255, 0)]
    [InlineData(7, 1)]
    public async Task InvalidKeyAnywhereInAnAppendPoisonsTheStage(int type, int keyNamespace)
    {
        var active = await PublishAsync(Inventory, Key(1));
        var stage = await Inventory.BeginAsync();
        await Inventory.AppendAsync(stage, [Key(2)]);
        var invalid = new ObjectKey((ObjectKeyType)type, new byte[16], (byte)keyNamespace);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Inventory.AppendAsync(stage, [Key(3), invalid]));
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.PublishAsync(stage))).Code);
        Assert.Equal(active.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(1)], (await Inventory.ReadPageAsync()).Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOfAppendOrPublishCannotExposeAnIncompleteReplacement(bool publish)
    {
        var active = await PublishAsync(Inventory, Key(1));
        var stage = await Inventory.BeginAsync();
        await Inventory.AppendAsync(stage, [Key(2)]);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        if (publish) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inventory.PublishAsync(stage, canceled.Token));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inventory.AppendAsync(stage, [Key(3)], canceled.Token));
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => Inventory.PublishAsync(stage))).Code);
        Assert.Equal(active.Value, (await Inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(1)], (await Inventory.ReadPageAsync()).Keys);
    }

    [Fact]
    public async Task PreCanceledReadAndBeginHaveCanceledOutcomesWithoutReplacingTheActiveSet()
    {
        var active = await PublishAsync(Inventory, Key(1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Inventory.BeginAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Inventory.LookupAsync(Key(1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Inventory.ReadPageAsync(ct: canceled.Token));
        Assert.Equal(active.Value, (await Inventory.ReadPageAsync()).Generation);
    }
}

public sealed class LibraryInventoryContractOnRealStore : LibraryInventoryContractTests
{
    private readonly TempDirectory temp = new();
    private ObjectStore library = null!;
    protected override async Task<ILibraryInventory> CreateAsync()
    {
        library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
        return library.ForOwner("build-a", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original).Inventory;
    }
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
}

public sealed class LibraryInventoryContractOnFake : LibraryInventoryContractTests
{
    private readonly VibeMiner.Objects.Testing.InMemoryLibraryInventories library = new();
    protected override Task<ILibraryInventory> CreateAsync() => Task.FromResult(library.ForOwner("build-a", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original));
    protected override async Task CloseAsync() => await library.DisposeAsync();
}
