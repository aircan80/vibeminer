using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryPairContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryUnionContractTests;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventoryPairTests
{
    private static readonly LibraryInventorySelection Left = new("build-a", LibraryOwnerScope.Build, LibraryObjectRole.Original);
    private static readonly LibraryInventorySelection Right = new("build-b", LibraryOwnerScope.Global, LibraryObjectRole.Derived);
    private static LibraryObjectOwner Owner(ObjectStore library, LibraryInventorySelection selection, string producer = "one") => library.ForOwner(selection.Owner, selection.Scope, producer, selection.Role);
    private static ILibraryInventoryPair Pair(ObjectStore library) => library.ForInventoryPair(Left, Right);
    private static Dictionary<string, string> StableFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "writer.lock" && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), Hash);

    [Fact]
    public async Task ReopenAndRefreshPreservePairEvidenceWithoutAddingMembershipOrMutatingIndex()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        var indexed = Synthetic.Headerless(2203, 67);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([indexed], true);
            await PublishAsync(Owner(library, Left).Inventory, indexed.Key, Key(1));
            await PublishAsync(Owner(library, Left, "two").Inventory, indexed.Key);
            await PublishAsync(Owner(library, Right).Inventory, indexed.Key, Key(2));
            await Owner(library, Right, "pending").Inventory.BeginAsync();
            var files = StableFiles(directory); var rows = Rows(directory); var lastRead = LastRead(directory, indexed.Key);
            var result = await Pair(library).MeasureAsync();
            AssertUnion(result.Left, 2, 0, 2, 1, 67); AssertUnion(result.Right, 1, 1, 2, 1, 67);
            AssertCoverage(result.Union, 3, 1, 67); AssertCoverage(result.Intersection, 1, 1, 67); AssertReconciles(result);
            Assert.Equal(files, StableFiles(directory)); Assert.Equal(rows, Rows(directory)); Assert.Equal(lastRead, LastRead(directory, indexed.Key));
            Assert.False(await Owner(library, Left).OwnsAsync(indexed.Key)); Assert.False(await Owner(library, Right).OwnsAsync(indexed.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        AssertCoverage((await Pair(reopened).MeasureAsync()).Intersection, 1, 1, 67);
        var refresh = Owner(reopened, Left).Inventory;
        var stage = await refresh.BeginAsync(); await refresh.AppendAsync(stage, [Key(10)]);
        AssertCoverage((await Pair(reopened).MeasureAsync()).Union, 3, 1, 67);
        await refresh.PublishAsync(stage);
        var changed = await Pair(reopened).MeasureAsync();
        AssertCoverage(changed.Union, 3, 1, 67); AssertCoverage(changed.LeftOnly, 1, 0, 0); AssertReconciles(changed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrCorruptPackStillCountsPairIndexEvidenceWithoutPayloadReads(bool missing)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var indexed = Synthetic.Headerless(2204, 2000);
        await library.ForOwner("publisher", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original).PutAsync([indexed], true);
        await PublishAsync(Owner(library, Left).Inventory, indexed.Key, Key(1));
        await PublishAsync(Owner(library, Right).Inventory, indexed.Key);
        var (pack, offset, _) = Row(directory, indexed.Key);
        if (missing) File.Delete(PackPath(directory, pack)); else Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 20);
        var result = await Pair(library).MeasureAsync();
        AssertCoverage(result.Union, 2, 1, 2000); AssertCoverage(result.Intersection, 1, 1, 2000); AssertReconciles(result);
        Assert.False(await Owner(library, Left).OwnsAsync(indexed.Key));
        Assert.Equal(missing ? ReadState.Lost : ReadState.Corrupt, (await library.ReadAsync(indexed.Key)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPublicationOrAcquisitionCannotMixPairSnapshots(bool acquisition)
    {
        using var temp = new TempDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); var armed = 0;
        var options = Quick with
        {
            Fault = point =>
            {
                if (point != StorePoint.InventoryPairSnapshot || Interlocked.Exchange(ref armed, 0) != 1) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Pair snapshot test was not released.");
            },
        };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        var original = Synthetic.Headerless(2205, 71);
        var derived = new PutObject(Key(1), 29, new byte[29], ObjectOrigin.Unknown);
        var source = library.ForOwner("publisher", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original);
        await source.PutAsync([original], true);
        var left = Owner(library, Left).Inventory;
        await PublishAsync(left, original.Key, derived.Key);
        await PublishAsync(Owner(library, Right).Inventory, derived.Key, Key(2));
        var next = await left.BeginAsync(); await left.AppendAsync(next, [Key(3)]);
        Volatile.Write(ref armed, 1);
        var measuring = Task.Run(async () => await Pair(library).MeasureAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (acquisition) await source.PutAsync([derived], true); else await left.PublishAsync(next);
            release.Set();
            var before = await measuring.WaitAsync(TimeSpan.FromSeconds(10));
            AssertCoverage(before.Union, 3, 1, 71); AssertCoverage(before.Intersection, 1, 0, 0);
            AssertCoverage(before.LeftOnly, 1, 1, 71); AssertCoverage(before.RightOnly, 1, 0, 0); AssertReconciles(before);
        }
        finally { release.Set(); await measuring; }
        var after = await Pair(library).MeasureAsync();
        if (acquisition) { AssertCoverage(after.Union, 3, 2, 100); AssertCoverage(after.Intersection, 1, 1, 29); }
        else { AssertCoverage(after.Union, 3, 0, 0); AssertCoverage(after.Intersection, 0, 0, 0); }
        AssertReconciles(after);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    public async Task SnapshotOrMidPairCancellationNeverReturnsPartialBuckets(int cancelAt)
    {
        using var temp = new TempDirectory(); using var canceled = new CancellationTokenSource(); var armed = false; var rows = 0;
        var options = Quick with { Fault = point =>
        {
            if (!armed) return;
            if (cancelAt == 0 && point == StorePoint.InventoryPairSnapshot || point == StorePoint.InventoryPairRow && ++rows == cancelAt) canceled.Cancel();
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), options);
        await PublishAsync(Owner(library, Left).Inventory, Keys(1, 3507));
        await PublishAsync(Owner(library, Right).Inventory, Keys(2001, 3507));
        armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Pair(library).MeasureAsync(canceled.Token));
        armed = false;
        if (cancelAt > 0) Assert.Equal(cancelAt, rows);
        var result = await Pair(library).MeasureAsync();
        AssertCoverage(result.Union, 5507, 0, 0); AssertCoverage(result.Intersection, 1507, 0, 0); AssertReconciles(result);
    }

    [Theory]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET active='bad' WHERE owner='build-a'")]
    [InlineData("UPDATE library_inventory_identity SET active=(SELECT active FROM library_inventory_identity WHERE owner='build-b') WHERE owner='build-a'")]
    [InlineData("UPDATE objects SET length=9223372036854775807 WHERE k=(SELECT k FROM objects ORDER BY k LIMIT 1)")]
    [InlineData("DELETE FROM library_inventory_key WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=CAST(k AS TEXT) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)")]
    public async Task MetadataDamageOrOverflowRefusesTheEntirePair(string mutation)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var first = Synthetic.Headerless(2206, 11); var second = Synthetic.Headerless(2207, 12);
        await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([first, second], true);
        await PublishAsync(Owner(library, Left).Inventory, first.Key);
        await PublishAsync(Owner(library, Right).Inventory, second.Key);
        MutateIndex(directory, mutation);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Pair(library).MeasureAsync())).Code);
    }

    [Fact]
    public async Task V1InventoryVersionCannotBecomeAnUnknownPairSuccess()
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
        Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Pair(legacy).MeasureAsync())).Code);
    }

    [Fact]
    public void SelectionAndPairFactoryValidateArguments()
    {
        Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySelection(" ", LibraryOwnerScope.Build, LibraryObjectRole.Original));
        Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySelection("owner", (LibraryOwnerScope)99, LibraryObjectRole.Original));
        Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySelection("owner", LibraryOwnerScope.Build, (LibraryObjectRole)99));
    }
}
