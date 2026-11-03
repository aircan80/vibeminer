using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Authored C# scenarios for shared physical bytes and durable, immutable owner attribution.</summary>
public sealed class LibraryStoreTests
{
    private static string LibraryDir(TempDirectory temp) => Path.Combine(temp.Path, "library");
    private static LibraryObjectOwner Owner(ObjectStore store, string id = "build-a", string producer = "metadata") =>
        store.ForOwner(id, LibraryOwnerScope.Build, producer, LibraryObjectRole.Original);
    private static PutObject Generated(int seed, int length = 2000)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return new(new ObjectKey(ObjectKeyType.Generated, SHA256.HashData(BitConverter.GetBytes(seed)).AsSpan(0, 16)), length, bytes, ObjectOrigin.Unknown);
    }

    [Fact]
    public async Task OriginalAndGeneratedObjectsShareOnePhysicalLibraryAndReopenWithTheirOwners()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var original = Synthetic.Headerless(1, 4000);
        var generated = Generated(2);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            Assert.Equal(ObjectStoreKind.Library, store.Kind);
            var source = Owner(store);
            var derived = store.ForOwner("build-a", LibraryOwnerScope.Build, "tables", LibraryObjectRole.Derived);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await source.PutAsync([original], true)).Outcome);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await derived.PutAsync([generated], true)).Outcome);
            Assert.True(await source.OwnsAsync(original.Key));
            Assert.True(await derived.OwnsAsync(generated.Key));
            Assert.Equal(2, (await store.StatsAsync()).Objects);
            Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(Assert.Single(PackFiles(directory))).AsSpan(12, 4)));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(original.Bytes.ToArray(), (await Owner(reopened).ReadAsync(original.Key)).Bytes);
        var cached = reopened.ForOwner("build-a", LibraryOwnerScope.Build, "tables", LibraryObjectRole.Derived);
        Assert.True(await cached.OwnsAsync(generated.Key));
        Assert.Equal(generated.Bytes.ToArray(), (await cached.ReadAsync(generated.Key)).Bytes);
        Assert.False(reopened.OpenReport.IndexRebuilt);
    }

    [Fact]
    public async Task DuplicatePutsAttributeBothOwnersWithoutAppendingAndKeepTheFirstGeneratedValue()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var original = Generated(10);
        await using var store = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var a = Owner(store);
        var b = Owner(store, "build-b");
        await a.PutAsync([original], true);
        var bytesBefore = PackFiles(directory).Sum(file => new FileInfo(file).Length);
        var replacement = original with { Bytes = new byte[17], DeclaredLength = 17 };
        Assert.Equal(PutOutcome.AlreadyPresent, Assert.Single(await b.PutAsync([replacement], true)).Outcome);
        Assert.True(await a.OwnsAsync(original.Key));
        Assert.True(await b.OwnsAsync(original.Key));
        Assert.Equal(original.Bytes.ToArray(), (await store.ReadAsync(original.Key)).Bytes);
        Assert.Equal(1, (await store.StatsAsync()).Objects);
        Assert.Equal(bytesBefore, PackFiles(directory).Sum(file => new FileInfo(file).Length));
    }

    [Fact]
    public async Task CacheHitsAcquireOwnershipButHasAndMissingReadsDoNot()
    {
        using var temp = new TempDirectory();
        await using var store = await ObjectStore.OpenLibraryAsync(LibraryDir(temp), Quick);
        var item = Synthetic.Headerless(11, 100_000);
        await Owner(store).PutAsync([item], true);
        var wholeReader = Owner(store, "whole-reader");
        Assert.True(await wholeReader.HasAsync(item.Key));
        Assert.False(await wholeReader.OwnsAsync(item.Key));
        Assert.Equal(ReadState.Present, (await wholeReader.ReadAsync(item.Key)).State);
        Assert.True(await wholeReader.OwnsAsync(item.Key));
        var rangeReader = Owner(store, "range-reader");
        var buffer = new byte[100];
        Assert.Equal(ReadState.Present, (await rangeReader.ReadRangeAsync(item.Key, 65_500, buffer)).State);
        Assert.Equal(item.Bytes.Slice(65_500, 100).ToArray(), buffer);
        Assert.True(await rangeReader.OwnsAsync(item.Key));
        var missing = Synthetic.Headerless(12).Key;
        Assert.Equal(ReadState.Absent, (await wholeReader.ReadAsync(missing)).State);
        Assert.False(await wholeReader.OwnsAsync(missing));
    }

    [Fact]
    public async Task FailedReadNeverClaimsCorruptBytes()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var item = Synthetic.Headerless(13);
        await using var store = await ObjectStore.OpenLibraryAsync(directory, Quick);
        await Owner(store).PutAsync([item], true);
        var (pack, offset, _) = Row(directory, item.Key);
        Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 5);
        var reader = Owner(store, "new-reader");
        Assert.Equal(ReadState.Corrupt, (await reader.ReadAsync(item.Key)).State);
        Assert.False(await reader.OwnsAsync(item.Key));
        Assert.Equal(ReadState.Corrupt, (await reader.ReadRangeAsync(item.Key, 0, new byte[10])).State);
        Assert.False(await reader.OwnsAsync(item.Key));
    }

    [Fact]
    public async Task ConcurrentOwnersKeepTheirCapturedIdentityAndOnePhysicalCopy()
    {
        using var temp = new TempDirectory();
        await using var store = await ObjectStore.OpenLibraryAsync(LibraryDir(temp), Quick);
        var item = Synthetic.Headerless(14);
        var owners = Enumerable.Range(0, 16).Select(i => Owner(store, $"build-{i}")).ToArray();
        var results = await Task.WhenAll(owners.Select(owner => owner.PutAsync([item], true)));
        Assert.Equal(1, results.Count(result => Assert.Single(result).Outcome == PutOutcome.Stored));
        foreach (var owner in owners) Assert.True(await owner.OwnsAsync(item.Key));
        Assert.False(await Owner(store, "other-build").OwnsAsync(item.Key));
        Assert.False(await Owner(store, "build-0", "different-producer").OwnsAsync(item.Key));
        Assert.Equal(1, (await store.StatsAsync()).Objects);
    }

    [Fact]
    public async Task HistoryDocumentsAndRecordsAreOwnedAndRemainQueryable()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var observation = Synthetic.Observation(15);
        var objects = HistoryRecordFormat.Objects(observation);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var owner = store.ForOwner("provider:versions", LibraryOwnerScope.Global, "discovery", LibraryObjectRole.Original);
            Assert.All(await owner.AppendHistoryAsync(observation), result => Assert.Equal(PutOutcome.Stored, result.Outcome));
            foreach (var item in objects) Assert.True(await owner.OwnsAsync(item.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        var history = await reopened.ReadHistoryAsync(new HistoryQuery(Limit: 10));
        Assert.Equal(objects[1].Key, Assert.Single(history).Record);
    }

    [Fact]
    public async Task RawPublicationAndUnimplementedMutationPathsAreExplicitlyRefused()
    {
        using var temp = new TempDirectory();
        await using var store = await ObjectStore.OpenLibraryAsync(LibraryDir(temp), Quick);
        var item = Synthetic.Headerless(16);
        Assert.Equal(ObjectStoreCodes.OwnerRequired, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.PutAsync([item], true))).Code);
        Assert.Equal(ObjectStoreCodes.OwnerRequired, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.AppendHistoryAsync(Synthetic.Observation(16)))).Code);
        var owner = Owner(store);
        await owner.PutAsync([item], true);
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.DeleteAsync([item.Key]))).Code);
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => owner.DeleteAsync([item.Key]))).Code);
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.StageAsync(item.Key, item.DeclaredLength))).Code);
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.ScanAsync())).Code);
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.CompactAsync())).Code);
        Assert.Equal(ReadState.Present, (await store.ReadAsync(item.Key)).State);
        Assert.True(await owner.OwnsAsync(item.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0owner")]
    public async Task InvalidOwnerIdentityCannotPublish(string identity)
    {
        using var temp = new TempDirectory();
        await using var store = await ObjectStore.OpenLibraryAsync(LibraryDir(temp), Quick);
        Assert.ThrowsAny<ArgumentException>(() => Owner(store, identity));
        Assert.ThrowsAny<ArgumentException>(() => Owner(store, "owner", identity));
        Assert.ThrowsAny<ArgumentException>(() => Owner(store, new string('é', 257)));
        Assert.ThrowsAny<ArgumentException>(() => Owner(store, "owner", new string('é', 65)));
        Assert.ThrowsAny<ArgumentException>(() => store.ForOwner("owner", (LibraryOwnerScope)99, "source", LibraryObjectRole.Original));
        Assert.ThrowsAny<ArgumentException>(() => store.ForOwner("owner", LibraryOwnerScope.Build, "source", (LibraryObjectRole)99));
        Assert.Equal(0, (await store.StatsAsync()).Objects);
    }

    [Theory]
    [InlineData(ObjectStoreKind.Cdn)]
    [InlineData(ObjectStoreKind.Generated)]
    public async Task LegacyOpenersRefuseLibraryAndLibraryOpenerRefusesLegacyWithoutMutation(ObjectStoreKind legacy)
    {
        using var temp = new TempDirectory();
        var libraryDirectory = LibraryDir(temp);
        var legacyDirectory = Path.Combine(temp.Path, "legacy");
        await using (var store = await ObjectStore.OpenLibraryAsync(libraryDirectory, Quick))
            await Owner(store).PutAsync([Synthetic.Headerless(17)], true);
        await using (var store = await ObjectStore.OpenAsync(legacyDirectory, Quick with { Kind = legacy }))
            await store.PutAsync([legacy == ObjectStoreKind.Generated ? Generated(17) : Synthetic.Headerless(17)], true);
        var beforeLibrary = HashFolder(libraryDirectory);
        var beforeLegacy = HashFolder(legacyDirectory);
        await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(libraryDirectory, Quick with { Kind = legacy }));
        await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(legacyDirectory, Quick));
        Assert.Equal(beforeLibrary, HashFolder(libraryDirectory));
        Assert.Equal(beforeLegacy, HashFolder(legacyDirectory));
    }

    [Fact]
    public async Task APreCanceledPutPublishesNeitherPayloadNorOwnership()
    {
        using var temp = new TempDirectory();
        await using var store = await ObjectStore.OpenLibraryAsync(LibraryDir(temp), Quick);
        var item = Synthetic.Headerless(18);
        var owner = Owner(store);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.PutAsync([item], true, canceled.Token));
        Assert.False(await store.HasAsync(item.Key));
        Assert.False(await owner.OwnsAsync(item.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEstablishedLibraryNeverRebuildsAnAuthoritativeMissingOrDamagedIndex(bool damage)
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
            await Owner(store).PutAsync([Synthetic.Headerless(19)], true);
        var index = Path.Combine(directory, "objects.sqlite3");
        if (damage) Write(index, 0, new byte[100]);
        else File.Delete(index);
        var before = HashFolder(directory);
        var error = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick));
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, error.Code);
        Assert.Equal(before, HashFolder(directory));
    }

    [Theory]
    [InlineData(nameof(StorePoint.EntryWritten), false)]
    [InlineData(nameof(StorePoint.AfterPackSync), false)]
    [InlineData(nameof(StorePoint.InsideIndexCommit), false)]
    [InlineData(nameof(StorePoint.AfterIndexCommit), true)]
    public async Task ACrashPublishesPayloadAndOwnerTogetherAndNeverAdoptsUnownedTails(string point, bool committed)
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var existing = Synthetic.Headerless(20);
        var interrupted = Synthetic.Headerless(21);
        var target = Enum.Parse<StorePoint>(point);
        var armed = 0;
        var options = Quick with { Fault = current => { if (current == target && Volatile.Read(ref armed) == 1) throw new SimulatedCrashException(current); } };
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            await Owner(store).PutAsync([existing], true);
            Volatile.Write(ref armed, 1);
            await Assert.ThrowsAsync<ObjectStoreException>(() => Owner(store, "interrupted-owner").PutAsync([interrupted], true));
        }
        await using var recovered = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.False(recovered.OpenReport.IndexRebuilt);
        Assert.Equal(0, recovered.OpenReport.RecoveredEntries);
        Assert.Equal(committed, await recovered.HasAsync(interrupted.Key));
        Assert.Equal(committed, await Owner(recovered, "interrupted-owner").OwnsAsync(interrupted.Key));
        Assert.True(await Owner(recovered).OwnsAsync(existing.Key));
        Assert.Equal(existing.Bytes.ToArray(), (await recovered.ReadAsync(existing.Key)).Bytes);
        Assert.Equal(0, (await recovered.VerifyAsync(full: true)).CorruptCount);
    }
    [Theory]
    [InlineData("DROP TABLE library_membership")]
    [InlineData("UPDATE library_format SET version=0")]
    [InlineData("UPDATE library_membership SET owner=' '")]
    [InlineData("UPDATE library_membership SET producer=zeroblob(100000)")]
    [InlineData("DELETE FROM library_membership")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_membership SET scope=9")]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_membership SET k=zeroblob(18)")]
    [InlineData("DROP INDEX library_membership_key")]
    public async Task MalformedAuthoritativeOwnershipIsRefusedWithoutRebuilding(string mutation)
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
            await Owner(store).PutAsync([Synthetic.Headerless(30)], true);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "objects.sqlite3")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = mutation;
            command.ExecuteNonQuery();
        }
        var before = HashFolder(directory);
        var error = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick));
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, error.Code);
        Assert.Equal(before, HashFolder(directory));
    }

    [Fact]
    public async Task EmptyEstablishedLibraryAlsoRefusesMissingIndex()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick)) { }
        File.Delete(Path.Combine(directory, "objects.sqlite3"));
        var before = HashFolder(directory);
        var error = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick));
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, error.Code);
        Assert.Equal(before, HashFolder(directory));
    }

    [Fact]
    public async Task CacheHitAndDuplicateMembershipSurviveAnUncleanClose()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var item = Synthetic.Headerless(31);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await Owner(store).PutAsync([item], true);
            Assert.Equal(ReadState.Present, (await Owner(store, "reader").ReadAsync(item.Key)).State);
            Assert.Equal(PutOutcome.AlreadyPresent, Assert.Single(await Owner(store, "duplicate").PutAsync([item], true)).Outcome);
            store.Kill();
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.True(await Owner(reopened, "reader").OwnsAsync(item.Key));
        Assert.True(await Owner(reopened, "duplicate").OwnsAsync(item.Key));
        Assert.False(await reopened.ForOwner("reader", LibraryOwnerScope.Global, "metadata", LibraryObjectRole.Original).OwnsAsync(item.Key));
        Assert.False(await reopened.ForOwner("reader", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Derived).OwnsAsync(item.Key));
    }

    [Fact]
    public async Task ReadOnlyLibraryInspectsCommittedBytesWithoutPublishingOwners()
    {
        using var temp = new TempDirectory();
        var directory = LibraryDir(temp);
        var item = Generated(32);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
            await Owner(store).PutAsync([item], true);
        var before = HashFolder(directory);
        await using (var read = await ObjectStore.OpenReadOnlyAsync(directory, Quick with { Kind = ObjectStoreKind.Library }))
        {
            Assert.Equal(item.Bytes.ToArray(), (await read.ReadAsync(item.Key)).Bytes);
        }
        Assert.Equal(before, HashFolder(directory));
    }

}
