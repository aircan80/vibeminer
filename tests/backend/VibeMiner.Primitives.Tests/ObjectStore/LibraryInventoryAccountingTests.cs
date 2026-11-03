using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryAccountingContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryPairContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryUnionContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventoryAccountingTests
{
    private static LibraryObjectOwner Owner(ObjectStore library, string id = "a") => library.ForOwner(id, LibraryOwnerScope.Build, "one", LibraryObjectRole.Original);
    private static Dictionary<string, string> StableFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "writer.lock" && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), Hash);

    [Fact]
    public async Task AccountingReopensWithoutMutationAndOutsideUnionIndexedBytesKeepTheirPhysicalMembership()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        var indexed = Synthetic.Headerless(2224, 67); var extra = Synthetic.Headerless(2225, 101);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var publisher = Owner(library, "publisher"); await publisher.PutAsync([indexed, extra], true);
            await PublishAsync(Owner(library).Inventory, indexed.Key, Key(1));
            await PublishAsync(Owner(library, "b").Inventory, indexed.Key, Key(2));
            var pending = Owner(library, "pending").Inventory; var staged = await pending.BeginAsync(); await pending.AppendAsync(staged, [extra.Key]);
            var rows = Rows(directory); var files = StableFiles(directory); var lastRead = LastRead(directory, indexed.Key);
            var result = await library.ForInventoryAccounting().MeasureAsync();
            Assert.Equal(2, result.PublishedInventories); Assert.Equal(1, result.UnpublishedInventories);
            AssertCoverage(result.Union, 3, 1, 67); Assert.Equal(2, result.IndexedObjects); Assert.Equal(168, result.IndexedPayloadBytes);
            Assert.Equal(1, result.OutsideUnionIndexedObjects); Assert.Equal(101, result.OutsideUnionIndexedPayloadBytes);
            Assert.Equal(4, result.OwnerReferences); Assert.Equal(2, result.IndexedOwnerReferences); Assert.Equal(134, result.OwnerCreditedIndexedPayloadBytes);
            AssertAccountingReconciles(result); Assert.Equal(rows, Rows(directory)); Assert.Equal(files, StableFiles(directory)); Assert.Equal(lastRead, LastRead(directory, indexed.Key));
            Assert.True(await publisher.OwnsAsync(extra.Key)); Assert.False(await Owner(library).OwnsAsync(indexed.Key)); Assert.False(await Owner(library, "b").OwnsAsync(indexed.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var restored = await reopened.ForInventoryAccounting().MeasureAsync();
        AssertCoverage(restored.Union, 3, 1, 67); Assert.Equal(1, restored.UnpublishedInventories); AssertAccountingReconciles(restored);
        Assert.True(await Owner(reopened, "publisher").OwnsAsync(extra.Key));
        var refresh = Owner(reopened).Inventory; var generation = await refresh.BeginAsync(); await refresh.AppendAsync(generation, [Key(3)]);
        AssertCoverage((await reopened.ForInventoryAccounting().MeasureAsync()).Union, 3, 1, 67);
        await refresh.PublishAsync(generation);
        var changed = await reopened.ForInventoryAccounting().MeasureAsync(); AssertCoverage(changed.Union, 3, 1, 67);
        AssertCoverage(changed.MultipleOwners, 0, 0, 0); Assert.Equal(67, changed.OwnerCreditedIndexedPayloadBytes); AssertAccountingReconciles(changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPacksDoNotChangeCommittedIndexAccounting(bool missing)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var item = Synthetic.Headerless(2226, 2000); await Owner(library, "publisher").PutAsync([item], true);
        await PublishAsync(Owner(library).Inventory, item.Key); await PublishAsync(Owner(library, "b").Inventory, item.Key, Key(1));
        var (pack, offset, _) = Row(directory, item.Key);
        if (missing) File.Delete(PackPath(directory, pack)); else Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 20);
        var result = await library.ForInventoryAccounting().MeasureAsync();
        AssertCoverage(result.Union, 2, 1, 2000); Assert.Equal(1, result.IndexedObjects); Assert.Equal(2000, result.IndexedPayloadBytes);
        Assert.Equal(4000, result.OwnerCreditedIndexedPayloadBytes); AssertAccountingReconciles(result);
        Assert.False(await Owner(library).OwnsAsync(item.Key));
        Assert.Equal(missing ? ReadState.Lost : ReadState.Corrupt, (await library.ReadAsync(item.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPublicationOrAcquisitionKeepsAllCountersInOneSnapshot(bool acquisition)
    {
        using var temp = new TempDirectory(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var armed = 0;
        var options = Quick with { Fault = point =>
        {
            if (point != StorePoint.InventoryAccountingSnapshot || Interlocked.Exchange(ref armed, 0) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Accounting snapshot test was not released.");
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var original = Synthetic.Headerless(2227, 71); var derived = new PutObject(Key(1), 29, new byte[29], ObjectOrigin.Unknown);
        var source = Owner(library, "publisher"); await source.PutAsync([original], true);
        var first = Owner(library).Inventory; await PublishAsync(first, original.Key, derived.Key);
        await PublishAsync(Owner(library, "b").Inventory, derived.Key, Key(2));
        var stage = await first.BeginAsync(); await first.AppendAsync(stage, [Key(3)]);
        var accounting = library.ForInventoryAccounting(); Volatile.Write(ref armed, 1);
        var measuring = Task.Run(async () => await accounting.MeasureAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (acquisition) await source.PutAsync([derived], true); else await first.PublishAsync(stage);
            release.Set(); var before = await measuring.WaitAsync(TimeSpan.FromSeconds(10));
            AssertCoverage(before.Union, 3, 1, 71); AssertCoverage(before.MultipleOwners, 1, 0, 0);
            Assert.Equal(1, before.IndexedObjects); Assert.Equal(71, before.IndexedPayloadBytes); Assert.Equal(4, before.OwnerReferences); Assert.Equal(71, before.OwnerCreditedIndexedPayloadBytes);
            AssertAccountingReconciles(before);
        }
        finally { release.Set(); await measuring; }
        var after = await accounting.MeasureAsync();
        if (acquisition)
        {
            AssertCoverage(after.Union, 3, 2, 100); Assert.Equal(2, after.IndexedObjects); Assert.Equal(100, after.IndexedPayloadBytes);
            Assert.Equal(129, after.OwnerCreditedIndexedPayloadBytes); Assert.Equal(3, after.IndexedOwnerReferences);
        }
        else
        {
            AssertCoverage(after.Union, 3, 0, 0); Assert.Equal(1, after.OutsideUnionIndexedObjects); Assert.Equal(71, after.OutsideUnionIndexedPayloadBytes);
            Assert.Equal(3, after.OwnerReferences); AssertCoverage(after.MultipleOwners, 0, 0, 0);
        }
        AssertAccountingReconciles(after);
    }

    [Theory]
    [InlineData(0)] // pinned snapshot
    [InlineData(1)] // committed-index traversal
    [InlineData(2)] // producer key merge
    public async Task CancellationNeverReturnsPartialAccounting(int phase)
    {
        using var temp = new TempDirectory(); using var canceled = new CancellationTokenSource(); var armed = false; var rows = 0;
        var options = Quick with { Fault = point =>
        {
            if (!armed) return;
            if (phase == 0 && point == StorePoint.InventoryAccountingSnapshot || phase == 1 && point == StorePoint.InventoryAccountingIndexRow ||
                phase == 2 && point == StorePoint.InventoryAccountingRow && ++rows == 1500) canceled.Cancel();
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        await Owner(library, "publisher").PutAsync([Synthetic.Headerless(2228, 2000)], true);
        await PublishAsync(Owner(library).Inventory, Keys(1, 3507)); await PublishAsync(Owner(library, "b").Inventory, Keys(2001, 3507));
        armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await library.ForInventoryAccounting().MeasureAsync(canceled.Token));
        armed = false; if (phase == 2) Assert.Equal(1500, rows);
        var result = await library.ForInventoryAccounting().MeasureAsync(); AssertCoverage(result.Union, 5507, 0, 0); AssertAccountingReconciles(result);
    }

    [Theory]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active='bad' WHERE owner='a'")]
    [InlineData("UPDATE library_inventory_identity SET active=(SELECT active FROM library_inventory_identity WHERE owner='b') WHERE owner='a'")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=CAST(k AS TEXT) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=zeroblob(17) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=zeroblob(18) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("DELETE FROM library_inventory_key WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("UPDATE objects SET length=9223372036854775807")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE objects SET length=0")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE objects SET k=CAST(k AS TEXT)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE objects SET k=zeroblob(18)")]
    public async Task StrictMetadataDamageAndLogicalCreditOverflowRefuseTheWholeAccountingResult(string mutation)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var item = Synthetic.Headerless(2229, 11); await Owner(library, "publisher").PutAsync([item], true);
        await PublishAsync(Owner(library).Inventory, item.Key); await PublishAsync(Owner(library, "b").Inventory, item.Key);
        MutateIndex(directory, mutation);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await library.ForInventoryAccounting().MeasureAsync())).Code);
    }

    [Fact]
    public async Task UnsupportedInventoryVersionIsAnExplicitFailure()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick)) { }
        MutateIndex(directory, """
            DROP TABLE library_inventory_source;
            DROP TABLE library_inventory_key;
            DROP TABLE library_inventory_generation;
            DROP TABLE library_inventory_identity;
            UPDATE library_format SET version=1;
            """);
        await using var legacy = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await legacy.ForInventoryAccounting().MeasureAsync())).Code);
    }
}
