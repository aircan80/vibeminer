using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryUnionContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventoryUnionTests
{
    private static LibraryObjectOwner Owner(ObjectStore library, string producer = "one") => library.ForOwner("build-a", LibraryOwnerScope.Build, producer, LibraryObjectRole.Original);
    private static ILibraryInventoryUnion Union(ObjectStore library) => library.ForInventoryOwner("build-a", LibraryOwnerScope.Build, LibraryObjectRole.Original);
    private static Dictionary<string, string> StableFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "writer.lock" && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), Hash);

    [Fact]
    public async Task ReopenKeepsThePublishedUnionAndRegisteredUnpublishedCountWithoutAddingMembership()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var original = Synthetic.Headerless(2191, 67);
        var derived = new PutObject(Key(1), 23, new byte[23], ObjectOrigin.Unknown);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([original, derived], true);
            await PublishAsync(Owner(library).Inventory, original.Key, derived.Key, Key(2));
            await PublishAsync(Owner(library, "two").Inventory, original.Key, Key(3));
            var pending = Owner(library, "pending").Inventory;
            var stage = await pending.BeginAsync();
            await pending.AppendAsync(stage, [Key(4)]);
            var before = StableFiles(directory);
            var rows = Rows(directory);
            var lastRead = LastRead(directory, original.Key);
            AssertUnion(await Union(library).MeasureAsync(), 2, 1, 4, 2, 90);
            Assert.Equal(before, StableFiles(directory));
            Assert.Equal(rows, Rows(directory));
            Assert.Equal(lastRead, LastRead(directory, original.Key));
            Assert.False(await Owner(library).OwnsAsync(original.Key));
            Assert.False(await Owner(library, "two").OwnsAsync(derived.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        AssertUnion(await Union(reopened).MeasureAsync(), 2, 1, 4, 2, 90);
        var refreshing = Owner(reopened).Inventory;
        var next = await refreshing.BeginAsync();
        await refreshing.AppendAsync(next, [Key(10)]);
        AssertUnion(await Union(reopened).MeasureAsync(), 2, 1, 4, 2, 90);
        await refreshing.PublishAsync(next);
        AssertUnion(await Union(reopened).MeasureAsync(), 2, 1, 3, 1, 67);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackLossOrCorruptionDoesNotChangeIndexPresenceEvidence(bool missing)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var item = Synthetic.Headerless(2192, 2000);
        await library.ForOwner("publisher", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original).PutAsync([item], true);
        await PublishAsync(Owner(library).Inventory, item.Key);
        await PublishAsync(Owner(library, "two").Inventory, item.Key, Key(1));
        var (pack, offset, _) = Row(directory, item.Key);
        if (missing) File.Delete(PackPath(directory, pack));
        else Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 30);
        AssertUnion(await Union(library).MeasureAsync(), 2, 0, 2, 1, 2000);
        Assert.False(await Owner(library).OwnsAsync(item.Key));
        Assert.Equal(missing ? ReadState.Lost : ReadState.Corrupt, (await library.ReadAsync(item.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentProducerPublicationOrAcquisitionReturnsOneCoherentUnionSnapshot(bool acquisition)
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = 0;
        var options = Quick with
        {
            Fault = point =>
            {
                if (point != StorePoint.InventoryUnionSnapshot || Interlocked.Exchange(ref armed, 0) != 1) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Union snapshot test was not released.");
            },
        };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var original = Synthetic.Headerless(2193, 71);
        var derived = new PutObject(Key(1), 29, new byte[29], ObjectOrigin.Unknown);
        var source = library.ForOwner("publisher", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original);
        await source.PutAsync([original], true);
        var one = Owner(library).Inventory;
        await PublishAsync(one, original.Key, derived.Key);
        await PublishAsync(Owner(library, "two").Inventory, derived.Key, Key(2));
        var next = await one.BeginAsync();
        await one.AppendAsync(next, [Key(3)]);
        var union = Union(library);
        Volatile.Write(ref armed, 1);
        var measuring = Task.Run(async () => await union.MeasureAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (acquisition) await source.PutAsync([derived], true);
            else await one.PublishAsync(next);
            release.Set();
            AssertUnion(await measuring.WaitAsync(TimeSpan.FromSeconds(10)), 2, 0, 3, 1, 71);
        }
        finally { release.Set(); await measuring; }
        if (acquisition) AssertUnion(await union.MeasureAsync(), 2, 0, 3, 2, 100);
        else AssertUnion(await union.MeasureAsync(), 2, 0, 3, 0, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    public async Task SnapshotAndMidUnionCancellationNeverReturnPartialMeasurements(int cancelAt)
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
                if (cancelAt == 0 && point == StorePoint.InventoryUnionSnapshot || point == StorePoint.InventoryUnionRow && ++rows == cancelAt) canceled.Cancel();
            },
        };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        await PublishAsync(Owner(library).Inventory, Keys(1, 3507));
        await PublishAsync(Owner(library, "two").Inventory, Keys(2001, 3507));
        armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Union(library).MeasureAsync(canceled.Token));
        armed = false;
        if (cancelAt > 0) Assert.Equal(cancelAt, rows);
        AssertUnion(await Union(library).MeasureAsync(), 2, 0, 5507, 0, 0);
    }

    [Theory]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active='bad'")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active=0")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active=-1")]
    [InlineData("UPDATE objects SET length=9223372036854775807 WHERE k=(SELECT k FROM objects ORDER BY k LIMIT 1)")]
    [InlineData("DELETE FROM library_inventory_key WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=CAST(k AS TEXT) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    public async Task UnreadableMetadataOrOverflowNeverFabricatesUnknownOrPartialUnionTotals(string mutation)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var first = Synthetic.Headerless(2194, 11);
        var second = Synthetic.Headerless(2195, 12);
        await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([first, second], true);
        await PublishAsync(Owner(library).Inventory, first.Key, second.Key);
        await PublishAsync(Owner(library, "two").Inventory, first.Key);
        MutateIndex(directory, mutation);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Union(library).MeasureAsync())).Code);
    }

    [Fact]
    public async Task UnsupportedInventoryVersionCannotBecomeAnUnpublishedUnion()
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
        Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Union(legacy).MeasureAsync())).Code);
    }
}
