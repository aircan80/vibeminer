using Xunit;
using VibeMiner.Generated;
using VibeMiner.Generated.Testing;

namespace VibeMiner.Primitives.Tests;

/// <summary>The <see cref="IGeneratedStore"/> contract, run against the real store and the in-memory fake. Synthetic bytes only.</summary>
public abstract class GeneratedStoreContractTests : IAsyncLifetime
{
    private IGeneratedStore store = null!;
    protected IGeneratedStore Store => store;

    protected abstract Task<IGeneratedStore> CreateAsync();
    protected virtual Task CloseAsync(IGeneratedStore created) => Task.CompletedTask;
    /// <summary>Damages the payload of a stored object (its <paramref name="bytes"/>) in the middle; may replace <see cref="Store"/>.</summary>
    protected abstract Task DamageAsync(GeneratedKey key, byte[] bytes);
    protected void Replace(IGeneratedStore reopened) => store = reopened;

    public async Task InitializeAsync() => store = await CreateAsync();
    public Task DisposeAsync() => CloseAsync(store);

    internal static GeneratedKey Key(int seed) => GeneratedKey.Hash(BitConverter.GetBytes(seed));

    internal static byte[] Bytes(int seed, int length)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    protected static GeneratedPut Put(int seed, int length = 1000) => new(Key(seed), Bytes(seed, length));

    [Fact]
    public async Task AnUnknownKeyIsAbsentEverywhere()
    {
        Assert.False(await Store.HasAsync(Key(1)));
        Assert.Equal(GeneratedReadState.Absent, (await Store.ReadAsync(Key(1))).State);
        Assert.Null((await Store.ReadAsync(Key(1))).Bytes);
        Assert.Equal(GeneratedReadState.Absent, (await Store.ReadRangeAsync(Key(1), 0, new byte[4])).State);
    }

    [Fact]
    public async Task APutIsStoredAndVisibleOnReturnThenAlreadyPresent()
    {
        var put = Put(1, 5000);
        Assert.Equal([GeneratedPutOutcome.Stored], await Store.PutAsync([put]));
        Assert.True(await Store.HasAsync(put.Key));
        var read = await Store.ReadAsync(put.Key);
        Assert.Equal(GeneratedReadState.Present, read.State);
        Assert.Equal(5000, read.Length);
        Assert.Equal(put.Bytes.ToArray(), read.Bytes);

        // The same key again keeps the first bytes, whatever the second Put carries.
        Assert.Equal([GeneratedPutOutcome.AlreadyPresent], await Store.PutAsync([new GeneratedPut(put.Key, Bytes(99, 10))]));
        Assert.Equal(put.Bytes.ToArray(), (await Store.ReadAsync(put.Key)).Bytes);
    }

    [Fact]
    public async Task ABatchAnswersInOrder()
    {
        await Store.PutAsync([Put(2)]);
        var outcomes = await Store.PutAsync([Put(1), Put(2), Put(3), Put(1)]);
        Assert.Equal([GeneratedPutOutcome.Stored, GeneratedPutOutcome.AlreadyPresent, GeneratedPutOutcome.Stored, GeneratedPutOutcome.AlreadyPresent], outcomes);
        foreach (var seed in new[] { 1, 2, 3 }) Assert.Equal(Bytes(seed, 1000), (await Store.ReadAsync(Key(seed))).Bytes);
        Assert.Empty(await Store.PutAsync([]));
    }

    [Fact]
    public async Task AnEmptyObjectIsALimitAndNothingInItsBatchIsStored()
    {
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => Store.PutAsync([Put(1), new GeneratedPut(Key(2), ReadOnlyMemory<byte>.Empty)]));
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        Assert.False(await Store.HasAsync(Key(1)));
        Assert.False(await Store.HasAsync(Key(2)));
    }

    [Fact]
    public async Task AnObjectOver64MiBIsALimit()
    {
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => Store.PutAsync([new GeneratedPut(Key(1), new byte[GeneratedLimits.MaxObjectBytes + 1])]));
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        Assert.False(await Store.HasAsync(Key(1)));
    }

    [Fact]
    public async Task AnObjectOfExactly64MiBIsStored()
    {
        var bytes = new byte[GeneratedLimits.MaxObjectBytes];
        bytes[0] = 1; bytes[^1] = 2;
        Assert.Equal([GeneratedPutOutcome.Stored], await Store.PutAsync([new GeneratedPut(Key(1), bytes)]));
        var tail = new byte[2];
        Assert.Equal(GeneratedReadState.Present, (await Store.ReadRangeAsync(Key(1), bytes.Length - 2, tail)).State);
        Assert.Equal(new byte[] { 0, 2 }, tail);
    }

    [Fact]
    public async Task TooManyObjectsInOnePutIsALimit()
    {
        var batch = Enumerable.Range(0, GeneratedLimits.MaxBatch + 1).Select(i => Put(i, 1)).ToArray();
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => Store.PutAsync(batch));
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        Assert.False(await Store.HasAsync(Key(0)));
        Assert.Equal(GeneratedLimits.MaxBatch, (await Store.PutAsync(batch[..GeneratedLimits.MaxBatch])).Count);
    }

    [Fact]
    public async Task ANullEntryInABatchThrowsAndNothingIsStored()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.PutAsync([Put(1), null!]));
        Assert.False(await Store.HasAsync(Key(1)));
    }

    [Fact]
    public async Task ADamagedObjectReadsCorruptButARangePastItsEndStillThrows()
    {
        var bytes = Bytes(7, 200_000);
        await Store.PutAsync([new GeneratedPut(Key(7), bytes)]);
        await DamageAsync(Key(7), bytes);
        var whole = await Store.ReadAsync(Key(7));
        Assert.Equal(GeneratedReadState.Corrupt, whole.State);
        Assert.Null(whole.Bytes);
        var destination = new byte[200_000];
        Assert.Equal(GeneratedReadState.Corrupt, (await Store.ReadRangeAsync(Key(7), 0, destination)).State);
        Assert.Equal(new byte[200_000], destination);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Store.ReadRangeAsync(Key(7), 199_999, new byte[2]));
        Assert.True(await Store.HasAsync(Key(7)));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 200_000)]
    [InlineData(65_530, 20)]       // across a 64 KiB block edge
    [InlineData(199_999, 1)]       // the last byte
    [InlineData(150_000, 50_000)]  // up to the end exactly
    [InlineData(200_000, 0)]       // empty, at the end
    public async Task ARangeReadsExactlyItsBytes(long offset, int length)
    {
        var bytes = Bytes(7, 200_000);
        await Store.PutAsync([new GeneratedPut(Key(7), bytes)]);
        var destination = new byte[length];
        var read = await Store.ReadRangeAsync(Key(7), offset, destination);
        Assert.Equal(GeneratedReadState.Present, read.State);
        Assert.Equal(200_000, read.Length);
        Assert.Equal(bytes.AsSpan((int)offset, length).ToArray(), destination);
    }

    [Theory]
    [InlineData(199_999, 2)]
    [InlineData(200_000, 1)]
    [InlineData(200_001, 0)]
    [InlineData(0, 200_001)]
    [InlineData(-1, 1)]
    public async Task ARangePastTheEndThrows(long offset, int length)
    {
        await Store.PutAsync([new GeneratedPut(Key(7), Bytes(7, 200_000))]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Store.ReadRangeAsync(Key(7), offset, new byte[length]));
    }

    [Fact]
    public async Task ARangeLongerThanAnyObjectThrows() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Store.ReadRangeAsync(Key(1), 0, new byte[GeneratedLimits.MaxObjectBytes + 1]));

    [Fact]
    public async Task ACanceledCallThrowsAndStoresNothing()
    {
        await Store.PutAsync([Put(1)]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.HasAsync(Key(1), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.ReadAsync(Key(1), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Store.ReadRangeAsync(Key(1), 0, new byte[1], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.PutAsync([Put(2)], cts.Token));
        Assert.False(await Store.HasAsync(Key(2)));
        Assert.True(await Store.HasAsync(Key(1)));
    }

    [Fact]
    public async Task ConcurrentPutsAndReadsAgree()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Store.PutAsync([Put(i), Put(100 + i % 4)])));
        var reads = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i => (await Store.ReadAsync(Key(i))).Bytes));
        for (var i = 0; i < 16; i++) Assert.Equal(Bytes(i, 1000), reads[i]);
        for (var i = 0; i < 4; i++) Assert.Equal(Bytes(100 + i, 1000), (await Store.ReadAsync(Key(100 + i))).Bytes);
    }
}

/// <summary>The standalone/fake lifecycle contract; borrowed libraries explicitly refuse these operations.</summary>
public abstract class GeneratedMutableStoreContractTests : GeneratedStoreContractTests
{
    [Fact]
    public async Task DeleteRemovesIgnoresUnknownKeysAndAllowsAPutAgain()
    {
        await Store.PutAsync([Put(1), Put(2)]);
        await Store.DeleteAsync([Key(1), Key(3)]);
        Assert.False(await Store.HasAsync(Key(1)));
        Assert.Equal(GeneratedReadState.Absent, (await Store.ReadAsync(Key(1))).State);
        Assert.True(await Store.HasAsync(Key(2)));
        await Store.DeleteAsync([]);
        Assert.Equal([GeneratedPutOutcome.Stored], await Store.PutAsync([Put(1)]));
        Assert.Equal(Bytes(1, 1000), (await Store.ReadAsync(Key(1))).Bytes);
    }

    [Fact]
    public async Task DeletingADamagedObjectAllowsRepair()
    {
        var bytes = Bytes(7, 200_000);
        await Store.PutAsync([new GeneratedPut(Key(7), bytes)]);
        await DamageAsync(Key(7), bytes);
        await Store.DeleteAsync([Key(7)]);
        Assert.Equal([GeneratedPutOutcome.Stored], await Store.PutAsync([new GeneratedPut(Key(7), bytes)]));
        Assert.Equal(bytes, (await Store.ReadAsync(Key(7))).Bytes);
    }

    [Fact]
    public async Task ACanceledDeleteLeavesTheObjectPresent()
    {
        await Store.PutAsync([Put(1)]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.DeleteAsync([Key(1)], cts.Token));
        Assert.True(await Store.HasAsync(Key(1)));
    }

    [Fact]
    public async Task CompactingKeepsLiveObjectsAndDropsNone()
    {
        await Store.PutAsync([Put(1), Put(2), Put(3)]);
        await Store.DeleteAsync([Key(2)]);
        await Store.CompactAsync(minDeadFraction: 0, maxPacks: 4);
        Assert.Equal(Bytes(1, 1000), (await Store.ReadAsync(Key(1))).Bytes);
        Assert.Equal(Bytes(3, 1000), (await Store.ReadAsync(Key(3))).Bytes);
        Assert.Equal(GeneratedReadState.Absent, (await Store.ReadAsync(Key(2))).State);
    }

}

public sealed class GeneratedStoreContractOnRealStore : GeneratedMutableStoreContractTests
{
    private readonly TempDirectory temp = new();

    private string Folder => Path.Combine(temp.Path, GeneratedStore.DefaultFolderName);
    private static readonly GeneratedStoreOptions Quick = new() { CommitDelay = TimeSpan.FromMilliseconds(5) };

    protected override async Task<IGeneratedStore> CreateAsync() => await GeneratedStore.OpenAsync(Folder, Quick);

    protected override async Task DamageAsync(GeneratedKey key, byte[] bytes)
    {
        await ((GeneratedStore)Store).DisposeAsync();
        var pack = Directory.EnumerateFiles(Path.Combine(Folder, "packs")).Single();
        var file = File.ReadAllBytes(pack);
        var at = file.AsSpan().IndexOf(bytes.AsSpan(0, 64));
        Assert.True(at > 0);
        file[at + bytes.Length / 2] ^= 0x40;
        File.WriteAllBytes(pack, file);
        Replace(await GeneratedStore.OpenAsync(Folder, Quick));
    }

    protected override async Task CloseAsync(IGeneratedStore created)
    {
        await ((GeneratedStore)created).DisposeAsync();
        temp.Dispose();
    }
}

public sealed class GeneratedStoreContractOnFake : GeneratedMutableStoreContractTests
{
    protected override Task<IGeneratedStore> CreateAsync() => Task.FromResult<IGeneratedStore>(new InMemoryGeneratedStore());
    protected override Task DamageAsync(GeneratedKey key, byte[] bytes) { ((InMemoryGeneratedStore)Store).Damage(key); return Task.CompletedTask; }
}
