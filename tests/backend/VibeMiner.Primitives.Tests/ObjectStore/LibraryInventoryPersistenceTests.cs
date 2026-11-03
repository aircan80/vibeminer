using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Persistence and publication boundaries over isolated temporary mixed libraries.</summary>
public sealed class LibraryInventoryPersistenceTests
{
    private static LibraryObjectOwner Owner(ObjectStore store, string owner = "build-a", string producer = "metadata",
        LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => store.ForOwner(owner, scope, producer, role);

    private static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, params ObjectKey[] keys)
    {
        var generation = await inventory.BeginAsync();
        foreach (var batch in keys.Chunk(1024)) await inventory.AppendAsync(generation, batch);
        await inventory.PublishAsync(generation);
        return generation;
    }

    private static async Task<List<ObjectKey>> ReadAllAsync(ILibraryInventory inventory)
    {
        var keys = new List<ObjectKey>();
        LibraryInventoryCursor? cursor = null;
        var pages = 0;
        do
        {
            var page = await inventory.ReadPageAsync(1024, cursor);
            Assert.InRange(page.Keys.Count, 0, 1024);
            keys.AddRange(page.Keys);
            cursor = page.Next;
            Assert.True(++pages < 20, "Authored inventory traversal failed to terminate.");
        } while (cursor is not null);
        return keys;
    }

    [Fact]
    public async Task EveryOwnerProducerScopeAndRoleIdentityRemainsIndependentDuringRefresh()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
        var owners = (from owner in new[] { "build-a", "build-b" }
                      from producer in new[] { "metadata", "tables" }
                      from scope in new[] { LibraryOwnerScope.Build, LibraryOwnerScope.Global }
                      from role in new[] { LibraryObjectRole.Original, LibraryObjectRole.Derived }
                      select Owner(library, owner, producer, scope, role)).ToArray();
        var shared = Key(100);
        for (var i = 0; i < owners.Length; i++) await PublishAsync(owners[i].Inventory, shared, Key(i + 1));
        var refresh = await owners[0].Inventory.BeginAsync();
        await owners[0].Inventory.AppendAsync(refresh, [Key(999)]);
        for (var i = 0; i < owners.Length; i++)
            Assert.Equal(new[] { Key(i + 1), shared }.Order(), await ReadAllAsync(owners[i].Inventory));
        await owners[0].Inventory.PublishAsync(refresh);
        Assert.Equal([Key(999)], await ReadAllAsync(owners[0].Inventory));
        for (var i = 1; i < owners.Length; i++)
            Assert.Equal(new[] { Key(i + 1), shared }.Order(), await ReadAllAsync(owners[i].Inventory));
        Assert.Equal(0, (await library.StatsAsync()).Objects);
    }

    [Fact]
    public async Task ExpectedAbsentKeysNeverFabricatePayloadPresenceOrOwnershipAndLaterPutSatisfiesThem()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
        var owner = Owner(library);
        var original = Synthetic.Headerless(215, 4000);
        var generated = new PutObject(Key(1), 7, "derived"u8.ToArray(), ObjectOrigin.Unknown);
        var active = await PublishAsync(owner.Inventory, original.Key, generated.Key);
        foreach (var key in new[] { original.Key, generated.Key })
        {
            Assert.True((await owner.Inventory.LookupAsync(key)).Contains);
            Assert.False(await owner.HasAsync(key));
            Assert.Equal(ReadState.Absent, (await owner.ReadAsync(key)).State);
            Assert.False(await owner.OwnsAsync(key));
        }
        Assert.Equal(0, (await library.StatsAsync()).Objects);
        Assert.All(await owner.PutAsync([original, generated], true), result => Assert.Equal(PutOutcome.Stored, result.Outcome));
        foreach (var key in new[] { original.Key, generated.Key })
        {
            var lookup = await owner.Inventory.LookupAsync(key);
            Assert.Equal(active.Value, lookup.Generation);
            Assert.True(lookup.Contains);
            Assert.True(await owner.HasAsync(key));
            Assert.True(await owner.OwnsAsync(key));
        }
    }

    [Fact]
    public async Task CleanReopenPreservesLargeActiveInventoriesAndNeverActivatesInterruptedStaging()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var keys = Keys(1, 3511);
        long active;
        LibraryInventoryGeneration interrupted;
        LibraryInventoryGeneration staged;
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var inventory = Owner(library).Inventory;
            active = (await PublishAsync(inventory, keys)).Value;
            interrupted = await inventory.BeginAsync();
            await inventory.AppendAsync(interrupted, Keys(10000, 1024));
            var unpublished = Owner(library, "unpublished").Inventory;
            staged = await unpublished.BeginAsync();
            await unpublished.AppendAsync(staged, [Key(77)]);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var reopenedInventory = Owner(reopened).Inventory;
        Assert.Equal(active, (await reopenedInventory.ReadPageAsync()).Generation);
        Assert.Equal(keys.Order(), await ReadAllAsync(reopenedInventory));
        Assert.Null((await Owner(reopened, "unpublished").Inventory.ReadPageAsync()).Generation);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => reopenedInventory.AppendAsync(interrupted, [Key(99999)]))).Code);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => reopenedInventory.PublishAsync(interrupted))).Code);
        var unpublishedInventory = Owner(reopened, "unpublished").Inventory;
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => unpublishedInventory.AppendAsync(staged, [Key(78)]))).Code);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => unpublishedInventory.PublishAsync(staged))).Code);
        Assert.Equal(active, (await reopenedInventory.ReadPageAsync()).Generation);
        Assert.Equal(keys.Order(), await ReadAllAsync(reopenedInventory));
        Assert.Null((await unpublishedInventory.ReadPageAsync()).Generation);
        Assert.Equal(0, (await reopened.StatsAsync()).Objects);
        Assert.False(reopened.OpenReport.IndexRebuilt);
    }

    [Fact]
    public async Task GenerationTokensAndCursorsCannotBeUsedThroughAnotherIdentityOrLibrary()
    {
        using var firstTemp = new TempDirectory();
        using var secondTemp = new TempDirectory();
        await using var first = await ObjectStore.OpenLibraryAsync(DirectoryPath(firstTemp), Quick);
        await using var second = await ObjectStore.OpenLibraryAsync(DirectoryPath(secondTemp), Quick);
        var inventory = Owner(first).Inventory;
        await PublishAsync(inventory, Key(1), Key(2));
        var cursor = (await inventory.ReadPageAsync(1)).Next;
        Assert.NotNull(cursor);
        var token = await inventory.BeginAsync();
        await inventory.AppendAsync(token, [Key(88)]);
        foreach (var other in new[] { Owner(first, "other").Inventory, Owner(first, producer: "other").Inventory,
                     Owner(first, scope: LibraryOwnerScope.Global).Inventory, Owner(first, role: LibraryObjectRole.Derived).Inventory, Owner(second).Inventory })
        {
            await PublishAsync(other, Key(1), Key(2));
            var otherStage = await other.BeginAsync();
            await other.AppendAsync(otherStage, [Key(1), Key(2)]);
            Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => other.AppendAsync(token, [Key(3)]))).Code);
            Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => other.PublishAsync(token))).Code);
            Assert.Equal(ObjectStoreCodes.InventoryCursorStale, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await other.ReadPageAsync(1, cursor))).Code);
            await other.PublishAsync(otherStage);
            Assert.Equal([Key(1), Key(2)], await ReadAllAsync(other));
        }
        await inventory.PublishAsync(token);
        Assert.Equal([Key(88)], await ReadAllAsync(inventory));
    }

    [Theory]
    [InlineData(nameof(StorePoint.InventoryBeforePublish), false)]
    [InlineData(nameof(StorePoint.InventoryInsidePublish), false)]
    [InlineData(nameof(StorePoint.InventoryPublished), true)]
    public async Task CrashAtPublishLeavesOnlyThePreviousOrTheNewCompleteGeneration(string faultPoint, bool published)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var previous = Keys(1, 1500);
        var next = Keys(10000, 3300);
        var armed = false;
        var point = Enum.Parse<StorePoint>(faultPoint);
        var options = Quick with { Fault = current => { if (armed && current == point) throw new SimulatedCrashException(current); } };
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            var inventory = Owner(library).Inventory;
            await PublishAsync(inventory, previous);
            var stage = await inventory.BeginAsync();
            foreach (var batch in next.Chunk(1024)) await inventory.AppendAsync(stage, batch);
            armed = true;
            await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishAsync(stage));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal((published ? next : previous).Order(), await ReadAllAsync(Owner(reopened).Inventory));
        Assert.Equal(0, (await reopened.StatsAsync()).Objects);
        Assert.False(reopened.OpenReport.IndexRebuilt);
    }

    [Fact]
    public async Task FailureAfterADurableAppendCannotActivateOrReplaceThePriorInventory()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var armed = false;
        var options = Quick with { Fault = point => { if (armed && point == StorePoint.InventoryStaged) throw new SimulatedCrashException(point); } };
        LibraryInventoryGeneration stage;
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            var inventory = Owner(library).Inventory;
            await PublishAsync(inventory, Key(1));
            stage = await inventory.BeginAsync();
            armed = true;
            await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.AppendAsync(stage, Keys(100, 1024)));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var reopenedInventory = Owner(reopened).Inventory;
        Assert.Equal([Key(1)], await ReadAllAsync(reopenedInventory));
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => reopenedInventory.PublishAsync(stage))).Code);
        Assert.Equal([Key(1)], await ReadAllAsync(reopenedInventory));
    }

    [Fact]
    public async Task CancellationRacingCloseCannotPublishARetainedStagingTokenAfterReopen()
    {
        using var temp = new TempDirectory();
        using var canceled = new CancellationTokenSource();
        var directory = DirectoryPath(temp);
        var armed = false;
        ObjectStore library = null!;
        Task? closing = null;
        var options = Quick with
        {
            Fault = point =>
            {
                if (!armed || point != StorePoint.InventoryBeforePublish) return;
                canceled.Cancel();
                // Close the writer before the canceled call can enqueue its durable poison operation.
                closing = library.DisposeAsync().AsTask();
            },
        };
        library = await ObjectStore.OpenLibraryAsync(directory, options);
        LibraryInventoryGeneration interrupted;
        try
        {
            var inventory = Owner(library).Inventory;
            await PublishAsync(inventory, Key(1));
            interrupted = await inventory.BeginAsync();
            await inventory.AppendAsync(interrupted, [Key(2), Key(3)]);
            armed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.PublishAsync(interrupted, canceled.Token));
            Assert.NotNull(closing);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await library.DisposeAsync(); }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var reopenedInventory = Owner(reopened).Inventory;
        Assert.Equal([Key(1)], await ReadAllAsync(reopenedInventory));
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => reopenedInventory.PublishAsync(interrupted))).Code);
        Assert.Equal([Key(1)], await ReadAllAsync(reopenedInventory));
    }

    [Fact]
    public async Task CancellationAfterRealPublicationReturnsSuccessAndKeepsTheCommittedGeneration()
    {
        using var temp = new TempDirectory();
        using var canceled = new CancellationTokenSource();
        var armed = false;
        var options = Quick with { Fault = point => { if (armed && point == StorePoint.InventoryPublished) canceled.Cancel(); } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var inventory = Owner(library).Inventory;
        await PublishAsync(inventory, Key(1));
        var next = await inventory.BeginAsync();
        await inventory.AppendAsync(next, [Key(2), Key(3)]);
        armed = true;
        await inventory.PublishAsync(next, canceled.Token);
        Assert.True(canceled.IsCancellationRequested);
        Assert.Equal(next.Value, (await inventory.ReadPageAsync()).Generation);
        Assert.Equal([Key(2), Key(3)], await ReadAllAsync(inventory));
    }

    [Fact]
    public async Task ClosingDrainsAcceptedPublicationAndRejectsNewInventoryOperations()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = false;
        var options = Quick with
        {
            Fault = point =>
            {
                if (!armed || point != StorePoint.InventoryInsidePublish) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Publication test was not released.");
            },
        };
        var library = await ObjectStore.OpenLibraryAsync(directory, options);
        var inventory = Owner(library).Inventory;
        var stage = await inventory.BeginAsync();
        await inventory.AppendAsync(stage, [Key(1), Key(2)]);
        armed = true;
        var publishing = inventory.PublishAsync(stage);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var closing = library.DisposeAsync().AsTask();
            Assert.False(closing.IsCompleted);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.BeginAsync())).Code);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.AppendAsync(stage, [Key(3)]))).Code);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishAsync(stage))).Code);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.AbandonAsync(stage))).Code);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.LookupAsync(Key(1)))).Code);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.ReadPageAsync())).Code);
            release.Set();
            await publishing.WaitAsync(TimeSpan.FromSeconds(10));
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await publishing; await library.DisposeAsync(); }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal([Key(1), Key(2)], await ReadAllAsync(Owner(reopened).Inventory));
    }
}
