using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.LibraryInventorySourceContractTests;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventorySourcePersistenceTests
{
    private static LibraryObjectOwner Owner(ObjectStore library, string id = "a") => library.ForOwner(id, LibraryOwnerScope.Build, "encoding", LibraryObjectRole.Original);
    private static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, LibraryInventorySourceDescriptor proof, params ObjectKey[] keys)
    { var stage = await inventory.BeginAsync(); await inventory.AppendAsync(stage, keys); await inventory.PublishSourceAsync(stage, proof); return stage; }

    [Fact]
    public async Task ExplicitV2UpgradePreservesExistingFreshOperationsPayloadsAndMembership()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); var payload = Synthetic.Headerless(2251, 2000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await Owner(library).PutAsync([payload], true); var inventory = Owner(library).Inventory;
            var stage = await inventory.BeginAsync(); await inventory.AppendAsync(stage, [Key(1), Key(2)]); await inventory.PublishAsync(stage);
        }
        MutateIndex(directory, "DROP TABLE library_inventory_source; UPDATE library_format SET version=2");
        var packHashes = PackFiles(directory).ToDictionary(path => Path.GetFileName(path)!, Hash);
        await using (var old = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            Assert.True(await Owner(old).OwnsAsync(payload.Key)); Assert.Equal(2, (await Owner(old).Inventory.ReadPageAsync()).Keys.Count);
            Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Owner(old).Inventory.MatchSourceAsync(Descriptor()))).Code);
            var stage = await Owner(old).Inventory.BeginAsync(); await Owner(old).Inventory.AppendAsync(stage, [Key(3)]); await Owner(old).Inventory.PublishAsync(stage);
        }
        await using (var upgraded = await ObjectStore.OpenLibraryAsync(directory, Quick with { UpgradeLibraryInventorySources = true }))
        {
            Assert.Equal([Key(3)], (await Owner(upgraded).Inventory.ReadPageAsync()).Keys); Assert.Null(await Owner(upgraded).Inventory.MatchSourceAsync(Descriptor(1)));
            await PublishAsync(Owner(upgraded).Inventory, Descriptor(1), Key(3)); Assert.True(await Owner(upgraded).OwnsAsync(payload.Key));
        }
        Assert.Equal(packHashes, PackFiles(directory).ToDictionary(path => Path.GetFileName(path)!, Hash));
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.NotNull(await Owner(reopened).Inventory.MatchSourceAsync(Descriptor(1))); Assert.Equal(payload.Bytes.ToArray(), (await reopened.ReadAsync(payload.Key)).Bytes);
    }
    [Fact]
    public async Task ProofSurvivesReopenWhileInterruptedStagingNeverChangesItsActivePair()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); long generation;
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            generation = (await PublishAsync(Owner(library).Inventory, Descriptor(), Key(1), Key(2))).Value;
            var unfinished = await Owner(library).Inventory.BeginAsync(); await Owner(library).Inventory.AppendAsync(unfinished, [Key(3)]);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(generation, await Owner(reopened).Inventory.MatchSourceAsync(Descriptor())); Assert.Equal([Key(1), Key(2)], (await Owner(reopened).Inventory.ReadPageAsync()).Keys);
        Assert.Equal(0, (await reopened.StatsAsync()).Objects); Assert.False(await Owner(reopened).OwnsAsync(Key(1)));
    }
    [Theory]
    [InlineData(nameof(StorePoint.InventoryBeforePublish), false)]
    [InlineData(nameof(StorePoint.InventorySourceInsidePublish), false)]
    [InlineData(nameof(StorePoint.InventoryInsidePublish), false)]
    [InlineData(nameof(StorePoint.InventoryPublished), true)]
    public async Task CrashLeavesEitherThePreviousOrNewCompleteProofAndGenerationTogether(string fault, bool committed)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); var target = Enum.Parse<StorePoint>(fault); var armed = false;
        var options = Quick with { Fault = point => { if (armed && point == target) throw new SimulatedCrashException(point); } };
        long previous, next;
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            var inventory = Owner(library).Inventory; previous = (await PublishAsync(inventory, Descriptor(1), Key(1))).Value;
            var stage = await inventory.BeginAsync(); next = stage.Value; await inventory.AppendAsync(stage, [Key(10), Key(11)]); armed = true;
            await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishSourceAsync(stage, Descriptor(2, "new-source")));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick); var active = Owner(reopened).Inventory;
        Assert.Equal(committed ? (long?)null : previous, await active.MatchSourceAsync(Descriptor(1)));
        Assert.Equal(committed ? next : (long?)null, await active.MatchSourceAsync(Descriptor(2, "new-source")));
        Assert.Equal(committed ? new[] { Key(10), Key(11) } : new[] { Key(1) }, (await active.ReadPageAsync()).Keys);
        Assert.False(reopened.OpenReport.IndexRebuilt); Assert.Equal(0, reopened.OpenReport.RecoveredEntries);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforePublishPreservesOldPairAndAfterRealCommitReturnsItsOutcome(bool afterCommit)
    {
        using var temp = new TempDirectory(); using var canceled = new CancellationTokenSource(); var armed = false;
        var options = Quick with { Fault = point => { if (armed && point == (afterCommit ? StorePoint.InventoryPublished : StorePoint.InventoryBeforePublish)) canceled.Cancel(); } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options); var inventory = Owner(library).Inventory;
        var previous = await PublishAsync(inventory, Descriptor(1), Key(1)); var next = await inventory.BeginAsync(); await inventory.AppendAsync(next, [Key(2), Key(3)]); armed = true;
        if (afterCommit) await inventory.PublishSourceAsync(next, Descriptor(2), canceled.Token);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.PublishSourceAsync(next, Descriptor(2), canceled.Token));
        Assert.Equal(afterCommit ? (long?)null : previous.Value, await inventory.MatchSourceAsync(Descriptor(1)));
        Assert.Equal(afterCommit ? next.Value : (long?)null, await inventory.MatchSourceAsync(Descriptor(2)));
    }
    [Fact]
    public async Task MatchingProofIsPinnedToOneSnapshotDuringConcurrentCompleteReplacement()
    {
        using var temp = new TempDirectory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim(); var armed = 0;
        var options = Quick with { Fault = point =>
        {
            if (point != StorePoint.InventorySourceSnapshot || Interlocked.Exchange(ref armed, 0) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Proof snapshot test was not released.");
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options); var inventory = Owner(library).Inventory;
        var first = await PublishAsync(inventory, Descriptor(1), Key(1)); var next = await inventory.BeginAsync(); await inventory.AppendAsync(next, [Key(2), Key(3)]);
        Volatile.Write(ref armed, 1); var matching = Task.Run(async () => await inventory.MatchSourceAsync(Descriptor(1)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await inventory.PublishSourceAsync(next, Descriptor(2)); release.Set();
            Assert.Equal(first.Value, await matching.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); await matching; }
        Assert.Null(await inventory.MatchSourceAsync(Descriptor(1))); Assert.Equal(next.Value, await inventory.MatchSourceAsync(Descriptor(2)));
    }
}
