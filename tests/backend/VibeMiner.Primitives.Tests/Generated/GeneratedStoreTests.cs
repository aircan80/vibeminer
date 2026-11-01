using Xunit;
using VibeMiner.Generated;
using VibeMiner.Objects;
using static VibeMiner.Primitives.Tests.GeneratedStoreContractTests;

namespace VibeMiner.Primitives.Tests;

/// <summary>What only the real generated store does: its folder on disk, damage in its packs, and refusing a folder of the other kind.</summary>
public sealed class GeneratedStoreTests
{
    private static readonly GeneratedStoreOptions Quick = new() { CommitDelay = TimeSpan.FromMilliseconds(5) };
    private static string Dir(TempDirectory t) => Path.Combine(t.Path, GeneratedStore.DefaultFolderName);

    [Fact]
    public async Task AReopenedStoreKeepsItsObjects()
    {
        using var t = new TempDirectory();
        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
            await store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 3000)), new GeneratedPut(Key(2), Bytes(2, 70_000))]);
        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
        {
            Assert.Equal(Bytes(1, 3000), (await store.ReadAsync(Key(1))).Bytes);
            Assert.Equal(Bytes(2, 70_000), (await store.ReadAsync(Key(2))).Bytes);
            var stats = await store.StatsAsync();
            Assert.Equal(2, stats.Objects);
            Assert.True(stats.LiveBytes > 73_000);
            Assert.Equal([GeneratedPutOutcome.AlreadyPresent], await store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 3000))]));
        }
    }

    [Fact]
    public async Task DeletingTheFolderGivesAnEmptyStore()
    {
        using var t = new TempDirectory();
        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
            await store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 3000))]);
        Directory.Delete(Dir(t), recursive: true);
        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
        {
            Assert.False(await store.HasAsync(Key(1)));
            Assert.Equal(0, (await store.StatsAsync()).Objects);
            Assert.Equal([GeneratedPutOutcome.Stored], await store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 3000))]));
        }
    }

    [Fact]
    public async Task AFlippedByteInAPackReadsCorruptNeverBytes()
    {
        using var t = new TempDirectory();
        var bytes = Bytes(5, 150_000);
        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
            await store.PutAsync([new GeneratedPut(Key(5), bytes), new GeneratedPut(Key(6), Bytes(6, 2000))]);

        var pack = Directory.EnumerateFiles(Path.Combine(Dir(t), "packs")).Single();
        var file = File.ReadAllBytes(pack);
        var at = file.AsSpan().IndexOf(bytes.AsSpan(0, 64));
        Assert.True(at > 0);
        file[at + 100_000] ^= 0x40;   // inside the third 64 KiB block
        File.WriteAllBytes(pack, file);

        await using (var store = await GeneratedStore.OpenAsync(Dir(t), Quick))
        {
            var read = await store.ReadAsync(Key(5));
            Assert.Equal(GeneratedReadState.Corrupt, read.State);
            Assert.Null(read.Bytes);
            Assert.NotNull(read.Problem);
            var destination = new byte[10];
            Assert.Equal(GeneratedReadState.Corrupt, (await store.ReadRangeAsync(Key(5), 99_995, destination)).State);
            Assert.Equal(new byte[10], destination);
            // Blocks away from the damage still read, and so does the other object.
            Assert.Equal(GeneratedReadState.Present, (await store.ReadRangeAsync(Key(5), 0, destination)).State);
            Assert.Equal(bytes.AsSpan(0, 10).ToArray(), destination);
            Assert.Equal(Bytes(6, 2000), (await store.ReadAsync(Key(6))).Bytes);
            // Deleting and storing it again repairs it.
            await store.DeleteAsync([Key(5)]);
            Assert.Equal([GeneratedPutOutcome.Stored], await store.PutAsync([new GeneratedPut(Key(5), bytes)]));
            Assert.Equal(bytes, (await store.ReadAsync(Key(5))).Bytes);
        }
    }

    [Fact]
    public async Task ALostPackReadsCorrupt()
    {
        using var t = new TempDirectory();
        await using var store = await GeneratedStore.OpenAsync(Dir(t), Quick);
        await store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 3000))]);
        foreach (var pack in Directory.EnumerateFiles(Path.Combine(Dir(t), "packs"))) File.Delete(pack);
        Assert.Equal(GeneratedReadState.Corrupt, (await store.ReadAsync(Key(1))).State);
        Assert.Equal(GeneratedReadState.Corrupt, (await store.ReadRangeAsync(Key(1), 0, new byte[1])).State);
    }

    [Fact]
    public async Task ACdnStoreFolderIsUnusable()
    {
        using var t = new TempDirectory();
        await using (var cdn = await ObjectStore.OpenAsync(Dir(t))) { }
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => GeneratedStore.OpenAsync(Dir(t), Quick));
        Assert.Equal(GeneratedStoreCodes.Unusable, e.Code);
        // Nothing was changed: the folder still opens as the Cdn store it is.
        await using var again = await ObjectStore.OpenAsync(Dir(t));
        Assert.Equal(ObjectStoreKind.Cdn, again.Kind);
    }

    [Fact]
    public async Task ASecondWriterOfTheSameFolderIsBusyNotUnusable()
    {
        using var t = new TempDirectory();
        await using (var first = await GeneratedStore.OpenAsync(Dir(t), Quick))
        {
            await first.PutAsync([new GeneratedPut(Key(1), Bytes(1, 100))]);
            var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => GeneratedStore.OpenAsync(Dir(t), Quick));
            Assert.Equal(GeneratedStoreCodes.Busy, e.Code);
            Assert.True(await first.HasAsync(Key(1)));
        }
        await using var again = await GeneratedStore.OpenAsync(Dir(t), Quick);
        Assert.True(await again.HasAsync(Key(1)));
    }

    [Fact]
    public async Task ABatchOverTheLayerOnePutBoundGoesInSeveralCommits()
    {
        using var t = new TempDirectory();
        var commits = new List<int>();
        await using var store = await GeneratedStore.OpenAsync(Dir(t), Quick with { MaxCommitBytes = 25_000, AfterCommit = n => { lock (commits) commits.Add(n); } });
        var batch = Enumerable.Range(0, 7).Select(i => new GeneratedPut(Key(i), Bytes(i, 10_000))).ToArray();
        Assert.All(await store.PutAsync(batch), o => Assert.Equal(GeneratedPutOutcome.Stored, o));
        Assert.Equal([2, 2, 2, 1], commits);
        for (var i = 0; i < 7; i++) Assert.Equal(Bytes(i, 10_000), (await store.ReadAsync(Key(i))).Bytes);
        // An object over the per-commit bound still goes, alone.
        commits.Clear();
        Assert.Equal([GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored], await store.PutAsync([new GeneratedPut(Key(20), Bytes(20, 40_000)), new GeneratedPut(Key(21), Bytes(21, 1))]));
        Assert.Equal([1, 1], commits);
        Assert.Equal(Bytes(20, 40_000), (await store.ReadAsync(Key(20))).Bytes);
    }

    [Fact]
    public async Task ABatchCanceledBetweenCommitsKeepsWhatWasCommittedAndARetryFinishesIt()
    {
        using var t = new TempDirectory();
        using var cts = new CancellationTokenSource();
        var commits = 0;
        await using var store = await GeneratedStore.OpenAsync(Dir(t), Quick with { MaxCommitBytes = 25_000, AfterCommit = _ => { if (++commits == 1) cts.Cancel(); } });
        var batch = Enumerable.Range(0, 6).Select(i => new GeneratedPut(Key(i), Bytes(i, 10_000))).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync(batch, cts.Token));
        Assert.Equal(1, commits);
        Assert.True(await store.HasAsync(Key(0)));
        Assert.True(await store.HasAsync(Key(1)));
        for (var i = 2; i < 6; i++) Assert.False(await store.HasAsync(Key(i)));

        var retry = await store.PutAsync(batch);
        Assert.Equal([GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored, GeneratedPutOutcome.Stored], retry);
        for (var i = 0; i < 6; i++) Assert.Equal(Bytes(i, 10_000), (await store.ReadAsync(Key(i))).Bytes);
    }

    [Fact]
    public async Task ACallAfterCloseIsClosed()
    {
        using var t = new TempDirectory();
        var store = await GeneratedStore.OpenAsync(Dir(t), Quick);
        await store.DisposeAsync();
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(async () => await store.ReadAsync(Key(1)));
        Assert.Equal(GeneratedStoreCodes.Closed, e.Code);
        e = await Assert.ThrowsAsync<GeneratedStoreException>(() => store.PutAsync([new GeneratedPut(Key(1), Bytes(1, 10))]));
        Assert.Equal(GeneratedStoreCodes.Closed, e.Code);
    }

    [Fact]
    public async Task CompactRewritesAPackOfDeletedObjects()
    {
        using var t = new TempDirectory();
        await using var store = await GeneratedStore.OpenAsync(Dir(t), Quick with { MaxPackBytes = 100_000 });
        await store.PutAsync(Enumerable.Range(0, 8).Select(i => new GeneratedPut(Key(i), Bytes(i, 20_000))).ToArray());
        await store.DeleteAsync(Enumerable.Range(0, 3).Select(Key).ToArray());
        var report = await store.CompactAsync(minDeadFraction: 0.1, maxPacks: 4);
        Assert.True(report.PacksCompacted >= 1);
        for (var i = 3; i < 8; i++) Assert.Equal(Bytes(i, 20_000), (await store.ReadAsync(Key(i))).Bytes);
    }
}
