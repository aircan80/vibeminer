using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryMeasurementContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventoryMeasurementTests
{
    private static Dictionary<string, string> StableFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "writer.lock" && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), Hash);

    private static LibraryObjectOwner Owner(ObjectStore library, string id = "build-a", string producer = "metadata",
        LibraryOwnerScope scope = LibraryOwnerScope.Build, LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(id, scope, producer, role);
    private static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, params ObjectKey[] keys)
    {
        var stage = await inventory.BeginAsync();
        foreach (var batch in keys.Chunk(1024)) await inventory.AppendAsync(stage, batch);
        await inventory.PublishAsync(stage);
        return stage;
    }

    [Fact]
    public async Task SharedIndexedObjectsCountForExactInventoriesWithoutAddingPhysicalMembershipOrAnyMutation()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var original = Synthetic.Headerless(2181, 500);
        var derived = new PutObject(Key(1), 37, new byte[37], ObjectOrigin.Unknown);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var source = Owner(library, "publisher");
            await source.PutAsync([original, derived, Synthetic.Headerless(2182, 1234)], true);
            var a = Owner(library);
            var b = Owner(library, "build-b", "tables", LibraryOwnerScope.Global, LibraryObjectRole.Derived);
            var independentProducer = Owner(library, producer: "other");
            var first = await PublishAsync(a.Inventory, original.Key, derived.Key, Key(2), derived.Key);
            var second = await PublishAsync(b.Inventory, derived.Key, Key(3));
            var other = await PublishAsync(independentProducer.Inventory, original.Key);
            var staging = await a.Inventory.BeginAsync();
            await a.Inventory.AppendAsync(staging, [Key(4)]);
            var rows = Rows(directory);
            var folder = StableFiles(directory);
            var originalLastRead = LastRead(directory, original.Key);
            var derivedLastRead = LastRead(directory, derived.Key);
            for (var i = 0; i < 3; i++)
            {
                AssertMeasured(await a.Inventory.MeasureAsync(), first.Value, 3, 2, 537);
                AssertMeasured(await b.Inventory.MeasureAsync(), second.Value, 2, 1, 37);
                AssertMeasured(await independentProducer.Inventory.MeasureAsync(), other.Value, 1, 1, 500);
            }
            Assert.Equal(rows, Rows(directory));
            Assert.Equal(folder, StableFiles(directory));
            Assert.Equal(originalLastRead, LastRead(directory, original.Key));
            Assert.Equal(derivedLastRead, LastRead(directory, derived.Key));
            foreach (var owner in new[] { a, b, independentProducer })
            {
                Assert.False(await owner.OwnsAsync(original.Key));
                Assert.False(await owner.OwnsAsync(derived.Key));
            }
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var measured = await Owner(reopened).Inventory.MeasureAsync();
        var reopenedGeneration = Assert.IsType<long>(measured.Generation);
        AssertMeasured(measured, reopenedGeneration, 3, 2, 537);
        Assert.False(await Owner(reopened).OwnsAsync(original.Key));
        Assert.False(await Owner(reopened).OwnsAsync(derived.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPacksStillCountIndexedObjectsWithoutPayloadAccess(bool removePack)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var original = Synthetic.Headerless(2183, 2000);
        await Owner(library, "publisher").PutAsync([original], true);
        var owner = Owner(library);
        var active = await PublishAsync(owner.Inventory, original.Key, Key(1));
        var (pack, offset, _) = Row(directory, original.Key);
        if (removePack) File.Delete(PackPath(directory, pack));
        else Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 30);
        var before = StableFiles(directory);
        AssertMeasured(await owner.Inventory.MeasureAsync(), active.Value, 2, 1, 2000);
        Assert.Equal(before, StableFiles(directory));
        Assert.False(await owner.OwnsAsync(original.Key));
        Assert.Equal(removePack ? ReadState.Lost : ReadState.Corrupt, (await library.ReadAsync(original.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPublicationOrPayloadCommitCannotMixMeasurementSnapshots(bool payloadCommit)
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = 0;
        var options = Quick with
        {
            Fault = point =>
            {
                if (point != StorePoint.InventoryMeasureSnapshot || Interlocked.Exchange(ref armed, 0) != 1) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Snapshot test was not released.");
            },
        };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var source = Owner(library, "publisher");
        var original = Synthetic.Headerless(2184, 41);
        var derived = new PutObject(Key(1), 59, new byte[59], ObjectOrigin.Unknown);
        await source.PutAsync([original], true);
        var inventory = Owner(library).Inventory;
        var before = await PublishAsync(inventory, original.Key, derived.Key, Key(2));
        LibraryInventoryGeneration? next = null;
        if (!payloadCommit)
        {
            next = await inventory.BeginAsync();
            await inventory.AppendAsync(next, [derived.Key, Key(3)]);
        }
        Volatile.Write(ref armed, 1);
        var measuring = Task.Run(async () => await inventory.MeasureAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (payloadCommit) await source.PutAsync([derived], true);
            else await inventory.PublishAsync(next!);
            release.Set();
            AssertMeasured(await measuring.WaitAsync(TimeSpan.FromSeconds(10)), before.Value, 3, 1, 41);
        }
        finally { release.Set(); await measuring; }
        if (payloadCommit) AssertMeasured(await inventory.MeasureAsync(), before.Value, 3, 2, 100);
        else AssertMeasured(await inventory.MeasureAsync(), next!.Value, 2, 0, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    public async Task CancellationAtPinnedSnapshotOrDuringLargeTraversalNeverReturnsPartialMeasurements(int cancelAtRow)
    {
        using var temp = new TempDirectory();
        using var canceled = new CancellationTokenSource();
        var armed = false;
        var rows = 0;
        var options = Quick with
        {
            Fault = point =>
            {
                if (!armed) return;
                if (cancelAtRow == 0 && point == StorePoint.InventoryMeasureSnapshot ||
                    point == StorePoint.InventoryMeasureRow && ++rows == cancelAtRow) canceled.Cancel();
            },
        };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var inventory = Owner(library).Inventory;
        var active = await PublishAsync(inventory, Keys(1, 3507));
        armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inventory.MeasureAsync(canceled.Token));
        armed = false;
        if (cancelAtRow > 0) Assert.Equal(cancelAtRow, rows);
        AssertMeasured(await inventory.MeasureAsync(), active.Value, 3507, 0, 0);
    }

    [Theory]
    [InlineData("UPDATE objects SET length=9223372036854775807 WHERE k=(SELECT k FROM objects ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE objects SET length=0 WHERE k=(SELECT k FROM objects ORDER BY k LIMIT 1)")]
    [InlineData("DELETE FROM library_inventory_key WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active='bad'")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active=0")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active=-1")]
    public async Task OverflowOrUnreadableIndexedMetadataCannotBecomeAPartialOrZeroMeasurement(string mutation)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var first = Synthetic.Headerless(2185, 11);
        var second = Synthetic.Headerless(2186, 12);
        await Owner(library, "publisher").PutAsync([first, second], true);
        var inventory = Owner(library).Inventory;
        await PublishAsync(inventory, first.Key, second.Key);
        MutateIndex(directory, mutation);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.MeasureAsync())).Code);
    }

    [Theory]
    [InlineData(0)] // another owner
    [InlineData(1)] // another scope
    [InlineData(2)] // another producer
    [InlineData(3)] // another role
    [InlineData(4)] // this identity's unpublished staging generation
    public async Task ActivePointerToForeignOrUnpublishedGenerationCannotFabricateMeasuredZeros(int mismatch)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var inventory = Owner(library).Inventory;
        await PublishAsync(inventory, Key(1));
        LibraryInventoryGeneration invalidActive;
        if (mismatch == 4) invalidActive = await inventory.BeginAsync();
        else
        {
            var other = mismatch switch
            {
                0 => Owner(library, id: "other"),
                1 => Owner(library, scope: LibraryOwnerScope.Global),
                2 => Owner(library, producer: "other"),
                _ => Owner(library, role: LibraryObjectRole.Derived),
            };
            invalidActive = await PublishAsync(other.Inventory);
        }
        MutateIndex(directory, $"UPDATE library_inventory_identity SET active={invalidActive.Value} WHERE owner='build-a' AND scope=0 AND producer='metadata' AND role=0");
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.MeasureAsync())).Code);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.LookupAsync(Key(1)))).Code);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.ReadPageAsync())).Code);
    }

    [Fact]
    public async Task V1LibraryMeasurementRequiresTheExplicitInventoryUpgrade()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick)) { }
        MutateIndex(directory, """
            DROP TABLE library_inventory_key;
            DROP TABLE library_inventory_generation;
            DROP TABLE library_inventory_identity;
            UPDATE library_format SET version=1;
            """);
        await using var legacy = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired,
            (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Owner(legacy).Inventory.MeasureAsync())).Code);
    }
}
