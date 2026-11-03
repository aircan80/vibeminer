using System.Security.Cryptography;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.GeneratedStoreContractTests;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests.Generated;

/// <summary>The common generated read/write contract also applies to a borrowed mixed library.</summary>
public sealed class GeneratedStoreContractOnBorrowedLibrary : GeneratedStoreContractTests
{
    private readonly TempDirectory temp = new();
    private ObjectStore library = null!;
    private string DirectoryPath => Path.Combine(temp.Path, "library");

    protected override async Task<IGeneratedStore> CreateAsync()
    {
        library = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick);
        return GeneratedStore.Borrow(BorrowedGeneratedStoreTests.Owner(library));
    }

    protected override Task DamageAsync(GeneratedKey key, byte[] bytes)
    {
        var (pack, offset, _) = Row(DirectoryPath, BorrowedGeneratedStoreTests.ObjectKey(key));
        Flip(PackPath(DirectoryPath, pack), offset + PackFormat.EntryHeaderBytes + bytes.Length / 2);
        return Task.CompletedTask;
    }

    protected override async Task CloseAsync(IGeneratedStore created)
    {
        await ((GeneratedStore)created).DisposeAsync();
        await library.DisposeAsync();
        temp.Dispose();
    }
}

/// <summary>Authored generated objects and synthetic records; every library lives in a test-owned temporary folder.</summary>
public sealed class BorrowedGeneratedStoreTests
{
    internal static LibraryObjectOwner Owner(ObjectStore library, string id = "build-a", string producer = "tables",
        LibraryOwnerScope scope = LibraryOwnerScope.Build) => library.ForOwner(id, scope, producer, LibraryObjectRole.Derived);
    internal static ObjectKey ObjectKey(GeneratedKey key) => new(ObjectKeyType.Generated, key.ToBytes());
    private static GeneratedPut Put(int seed, int length = 1000) => new(Key(seed), Bytes(seed, length));
    private static string Folder(TempDirectory temp) => Path.Combine(temp.Path, "library");
    private static long PackBytes(string folder) => PackFiles(folder).Sum(f => new FileInfo(f).Length);

    [Fact]
    public async Task CapturesADerivedOwnerAndPreservesGeneratedKeyBytesAndNamespace()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var original = library.ForOwner("source", LibraryOwnerScope.Build, "cdn", LibraryObjectRole.Original);
        Assert.Throws<ArgumentException>(() => GeneratedStore.Borrow(original));
        var keyBytes = SHA256.HashData("authored-generated-identity"u8).AsSpan(0, 16).ToArray();
        var key = GeneratedKey.Hash("authored-generated-identity"u8);
        Assert.Equal(keyBytes, key.ToBytes());
        var owner = Owner(library);
        await using var generated = GeneratedStore.Borrow(owner);
        Assert.Equal(library.Folder, generated.Folder);
        await generated.PutAsync([new GeneratedPut(key, "authored payload"u8.ToArray())]);
        Assert.Equal("authored payload"u8.ToArray(), (await library.ReadAsync(new ObjectKey(ObjectKeyType.Generated, keyBytes))).Bytes);
        Assert.True(await owner.OwnsAsync(ObjectKey(key)));
        Assert.False(await Owner(library, "build-a", "other").OwnsAsync(ObjectKey(key)));
        Assert.False(await Owner(library, "build-a", scope: LibraryOwnerScope.Global).OwnsAsync(ObjectKey(key)));
        Assert.False(await library.ForOwner("build-a", LibraryOwnerScope.Build, "tables", LibraryObjectRole.Original).OwnsAsync(ObjectKey(key)));
        Assert.Equal(1, (await library.StatsAsync()).Objects);
        Assert.DoesNotContain(Directory.GetDirectories(temp.Path), path => path != library.Folder);
    }

    [Fact]
    public async Task OrderedDuplicatesAttachOwnersWithoutRewritingFirstCommittedPayloads()
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        await using (var library = await ObjectStore.OpenLibraryAsync(folder, Quick))
        {
            await using var first = GeneratedStore.Borrow(Owner(library));
            await using var second = GeneratedStore.Borrow(Owner(library, "build-b"));
            await first.PutAsync([Put(2)]);
            Assert.Equal([GeneratedPutOutcome.Stored, GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.Stored, GeneratedPutOutcome.AlreadyPresent],
                await second.PutAsync([Put(1), Put(2, 17), Put(3), Put(1, 19)]));
            var before = PackBytes(folder);
            Assert.All(await first.PutAsync([Put(1, 31), Put(2, 7), Put(3, 11)]), result => Assert.Equal(GeneratedPutOutcome.AlreadyPresent, result));
            Assert.Equal(before, PackBytes(folder));
            Assert.Equal(3, (await library.StatsAsync()).Objects);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(folder, Quick);
        foreach (var seed in new[] { 1, 2, 3 })
        {
            Assert.Equal(Bytes(seed, 1000), (await reopened.ReadAsync(ObjectKey(Key(seed)))).Bytes);
            Assert.True(await Owner(reopened).OwnsAsync(ObjectKey(Key(seed))));
            Assert.True(await Owner(reopened, "build-b").OwnsAsync(ObjectKey(Key(seed))));
        }
    }

    [Fact]
    public async Task ConcurrentOwnersReturnOneStoredWinnerAndAllKeepTheirCapturedIdentity()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var owners = Enumerable.Range(0, 12).Select(i => Owner(library, $"build-{i}")).ToArray();
        var facades = owners.Select(owner => GeneratedStore.Borrow(owner)).ToArray();
        try
        {
            var results = await Task.WhenAll(facades.Select((facade, i) => facade.PutAsync([new GeneratedPut(Key(1), Bytes(i, 1000))])));
            var winner = Assert.Single(Enumerable.Range(0, results.Length), i => Assert.Single(results[i]) == GeneratedPutOutcome.Stored);
            Assert.Equal(11, results.Count(result => result[0] == GeneratedPutOutcome.AlreadyPresent));
            Assert.Equal(Bytes(winner, 1000), (await library.ReadAsync(ObjectKey(Key(1)))).Bytes);
            foreach (var owner in owners) Assert.True(await owner.OwnsAsync(ObjectKey(Key(1))));
            Assert.False(await Owner(library, "unrelated").OwnsAsync(ObjectKey(Key(1))));
            Assert.Equal(1, (await library.StatsAsync()).Objects);
        }
        finally { foreach (var facade in facades) await facade.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheHitsAttachDurableOwnershipBeforeReturningButHasDoesNot(bool range)
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        await using (var library = await ObjectStore.OpenLibraryAsync(folder, Quick))
        {
            await using var writer = GeneratedStore.Borrow(Owner(library));
            await writer.PutAsync([Put(1, 100_000)]);
        }
        await using (var library = await ObjectStore.OpenLibraryAsync(folder, Quick))
        {
            var owner = Owner(library, "reader");
            await using var reader = GeneratedStore.Borrow(owner);
            Assert.True(await reader.HasAsync(Key(1)));
            Assert.False(await owner.OwnsAsync(ObjectKey(Key(1))));
            if (range)
            {
                var buffer = new byte[100];
                Assert.Equal(GeneratedReadState.Present, (await reader.ReadRangeAsync(Key(1), 65_500, buffer)).State);
                Assert.Equal(Bytes(1, 100_000).AsSpan(65_500, 100).ToArray(), buffer);
            }
            else Assert.Equal(Bytes(1, 100_000), (await reader.ReadAsync(Key(1))).Bytes);
            Assert.True(await owner.OwnsAsync(ObjectKey(Key(1))));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(folder, Quick);
        Assert.True(await Owner(reopened, "reader").OwnsAsync(ObjectKey(Key(1))));
    }

    [Fact]
    public async Task AbsentCorruptAndCanceledReadsNeverClaimANewOwner()
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(folder, Quick);
        await using var writer = GeneratedStore.Borrow(Owner(library));
        await writer.PutAsync([Put(1, 100_000), Put(2)]);
        var (pack, offset, _) = Row(folder, ObjectKey(Key(1)));
        Flip(PackPath(folder, pack), offset + PackFormat.EntryHeaderBytes + 7);
        var owner = Owner(library, "reader");
        await using var reader = GeneratedStore.Borrow(owner);
        Assert.Equal(GeneratedReadState.Absent, (await reader.ReadAsync(Key(99))).State);
        Assert.Equal(GeneratedReadState.Absent, (await reader.ReadRangeAsync(Key(99), 0, new byte[10])).State);
        var corrupt = await reader.ReadAsync(Key(1));
        Assert.Equal(GeneratedReadState.Corrupt, corrupt.State);
        Assert.Null(corrupt.Bytes);
        Assert.NotNull(corrupt.Problem);
        var destination = new byte[10];
        Assert.Equal(GeneratedReadState.Corrupt, (await reader.ReadRangeAsync(Key(1), 0, destination)).State);
        Assert.Equal(new byte[10], destination);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadRangeAsync(Key(1), 100_000, new byte[1]));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadAsync(Key(2), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadRangeAsync(Key(2), 0, new byte[1], canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.PutAsync([Put(2), Put(3)], canceled.Token));
        foreach (var seed in new[] { 1, 2, 3, 99 }) Assert.False(await owner.OwnsAsync(ObjectKey(Key(seed))));
        Assert.False(await library.HasAsync(ObjectKey(Key(3))));
    }

    [Fact]
    public async Task FullBatchValidationHappensBeforeAnySplitCommitOrOwnershipAttachment()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        await using var writer = GeneratedStore.Borrow(Owner(library));
        await writer.PutAsync([Put(1)]);
        var owner = Owner(library, "invalid-batch");
        var commits = 0;
        await using var generated = GeneratedStore.Borrow(owner, new() { MaxCommitBytes = 1000, AfterCommit = _ => commits++ });
        var error = await Assert.ThrowsAsync<GeneratedStoreException>(() => generated.PutAsync([Put(1), Put(2), new GeneratedPut(Key(3), ReadOnlyMemory<byte>.Empty)]));
        Assert.Equal(GeneratedStoreCodes.Limit, error.Code);
        await Assert.ThrowsAsync<ArgumentNullException>(() => generated.PutAsync([Put(1), Put(2), null!]));
        Assert.Equal(0, commits);
        Assert.False(await library.HasAsync(ObjectKey(Key(2))));
        Assert.False(await owner.OwnsAsync(ObjectKey(Key(1))));
        Assert.False(await owner.OwnsAsync(ObjectKey(Key(2))));
    }

    [Fact]
    public async Task CancellationBetweenBoundedCommitsKeepsOnlyRealValuesAndOwnersAndRetryFinishesInOrder()
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        using var canceled = new CancellationTokenSource();
        var batch = Enumerable.Range(0, 6).Select(i => Put(i, 10_000)).ToArray();
        await using (var library = await ObjectStore.OpenLibraryAsync(folder, Quick))
        {
            await using var existing = GeneratedStore.Borrow(Owner(library, "existing"));
            await existing.PutAsync([batch[0], batch[2]]);
            var owner = Owner(library);
            var commits = new List<int>();
            await using var generated = GeneratedStore.Borrow(owner, new()
            {
                MaxCommitBytes = 25_000,
                AfterCommit = count => { commits.Add(count); if (commits.Count == 1) canceled.Cancel(); },
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generated.PutAsync(batch, canceled.Token));
            Assert.Equal([2], commits);
            for (var i = 0; i < 6; i++)
            {
                Assert.Equal(i is 0 or 1 or 2, await library.HasAsync(ObjectKey(Key(i))));
                Assert.Equal(i < 2, await owner.OwnsAsync(ObjectKey(Key(i))));
            }
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(folder, Quick);
        var reopenedOwner = Owner(reopened);
        for (var i = 0; i < 6; i++) Assert.Equal(i < 2, await reopenedOwner.OwnsAsync(ObjectKey(Key(i))));
        var retryCommits = new List<int>();
        await using var retry = GeneratedStore.Borrow(reopenedOwner, new() { MaxCommitBytes = 25_000, AfterCommit = retryCommits.Add });
        Assert.Equal([GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored], await retry.PutAsync(batch));
        Assert.Equal([2, 2, 2], retryCommits);
        foreach (var put in batch)
        {
            Assert.True(await reopenedOwner.OwnsAsync(ObjectKey(put.Key)));
            Assert.Equal(put.Bytes.ToArray(), (await retry.ReadAsync(put.Key)).Bytes);
        }
    }

    [Fact]
    public async Task DeleteAndCompactExplicitlyRefuseAndPreserveEveryOwnersBytes()
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        await using var library = await ObjectStore.OpenLibraryAsync(folder, Quick);
        var original = library.ForOwner("source", LibraryOwnerScope.Build, "cdn", LibraryObjectRole.Original);
        var originalPut = Synthetic.Headerless(7);
        await original.PutAsync([originalPut], true);
        await using var first = GeneratedStore.Borrow(Owner(library));
        var secondOwner = Owner(library, "other");
        await using var second = GeneratedStore.Borrow(secondOwner);
        await first.PutAsync([Put(1)]);
        await second.PutAsync([Put(1, 17)]);
        var before = PackBytes(folder);
        foreach (var keys in new[] { new[] { Key(1) }, Array.Empty<GeneratedKey>() })
            Assert.Equal(GeneratedStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<GeneratedStoreException>(() => first.DeleteAsync(keys))).Code);
        Assert.Equal(GeneratedStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<GeneratedStoreException>(() => first.CompactAsync())).Code);
        Assert.Equal(GeneratedStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<GeneratedStoreException>(async () => await first.StatsAsync())).Code);
        Assert.Equal(before, PackBytes(folder));
        Assert.Equal(Bytes(1, 1000), (await second.ReadAsync(Key(1))).Bytes);
        Assert.True(await secondOwner.OwnsAsync(ObjectKey(Key(1))));
        Assert.Equal(originalPut.Bytes.ToArray(), (await original.ReadAsync(originalPut.Key)).Bytes);
    }

    [Fact]
    public async Task DisposeDrainsAcceptedWorkRejectsNewCallsAndLeavesOriginalAndSiblingConsumersUsable()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var commits = 0;
        var facade = GeneratedStore.Borrow(Owner(library), new()
        {
            MaxCommitBytes = 1000,
            AfterCommit = _ =>
            {
                if (Interlocked.Increment(ref commits) != 1) return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test did not release the accepted put.");
            },
        });
        await using var sibling = GeneratedStore.Borrow(Owner(library, "sibling"));
        var put = Task.Run(() => facade.PutAsync([Put(1), Put(2)]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var disposing = facade.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            await AssertClosedAsync(facade);
            release.Set();
            Assert.All(await put.WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.Equal(GeneratedPutOutcome.Stored, result));
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
            await facade.DisposeAsync();
            await AssertClosedAsync(facade);
            Assert.Equal(Bytes(2, 1000), (await sibling.ReadAsync(Key(2))).Bytes);
            await sibling.PutAsync([Put(3)]);
            var original = library.ForOwner("original", LibraryOwnerScope.Build, "cdn", LibraryObjectRole.Original);
            var item = Synthetic.Headerless(8);
            await original.PutAsync([item], true);
            Assert.Equal(item.Bytes.ToArray(), (await original.ReadAsync(item.Key)).Bytes);
        }
        finally { release.Set(); await put; await facade.DisposeAsync(); }
    }

    private static async Task AssertClosedAsync(GeneratedStore facade)
    {
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(async () => await facade.HasAsync(Key(1)))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(async () => await facade.ReadAsync(Key(1)))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(async () => await facade.ReadRangeAsync(Key(1), 0, new byte[1]))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(() => facade.PutAsync([Put(9)]))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(() => facade.PutAsync([]))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(() => facade.DeleteAsync([Key(1)]))).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(() => facade.CompactAsync())).Code);
        Assert.Equal(GeneratedStoreCodes.Closed, (await Assert.ThrowsAsync<GeneratedStoreException>(async () => await facade.StatsAsync())).Code);
    }

    [Fact]
    public async Task SyntheticRecordFilesKeepEveryEncodedObjectUnchangedAndRoundTripAfterReopen()
    {
        using var temp = new TempDirectory();
        var folder = Folder(temp);
        var manifest = GeneratedKey.Hash("authored-synthetic-record-file"u8);
        var metadata = "authored metadata"u8.ToArray();
        var records = SyntheticRows.Generate(700, seed: 214).ToArray();
        var options = new RecordFileOptions { BlockBytes = 2048, SegmentBytes = 4096 };
        var baseline = new InMemoryGeneratedStore();
        static async Task WriteAsync(IGeneratedStore store, GeneratedKey key, byte[] metadata, (uint Id, byte[] Bytes)[] records, RecordFileOptions options)
        {
            using var writer = new RecordFileWriter(store, options);
            foreach (var record in records) await writer.AddAsync(record.Id, record.Bytes);
            await writer.CompleteAsync(key, metadata);
        }
        await WriteAsync(baseline, manifest, metadata, records, options);
        await using (var library = await ObjectStore.OpenLibraryAsync(folder, Quick))
        {
            var owner = Owner(library);
            await using var generated = GeneratedStore.Borrow(owner);
            await WriteAsync(generated, manifest, metadata, records, options);
            Assert.True(baseline.Count > 3);
            Assert.Equal(baseline.Count, (await library.StatsAsync()).Objects);
            foreach (var key in baseline.Keys)
            {
                Assert.Equal((await baseline.ReadAsync(key)).Bytes, (await library.ReadAsync(ObjectKey(key))).Bytes);
                Assert.True(await owner.OwnsAsync(ObjectKey(key)));
            }
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(folder, Quick);
        await using var reader = GeneratedStore.Borrow(Owner(reopened, "reader"));
        var open = await RecordFile.OpenAsync(reader, manifest);
        Assert.Equal(GeneratedReadState.Present, open.State);
        var file = Assert.IsType<RecordFile>(open.File);
        Assert.Equal(metadata, file.Metadata.ToArray());
        Assert.Equal(records.Length, file.Count);
        var ordinal = 0;
        await foreach (var record in file.ReadAllAsync())
        {
            Assert.Equal(ordinal, record.Ordinal);
            Assert.Equal(records[ordinal].Id, record.Id);
            Assert.Equal(records[ordinal++].Bytes, record.Bytes.ToArray());
        }
        Assert.Equal(records.Length, ordinal);
        Assert.Equal(records[333].Bytes, (await file.FindAsync(records[333].Id))!.Value.Bytes.ToArray());
        foreach (var key in file.Keys) Assert.True(await Owner(reopened, "reader").OwnsAsync(ObjectKey(key)));
    }
}
