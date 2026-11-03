using Xunit;
using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class ObjectStoreStagingTests
{
    [Fact]
    public async Task AStagedObjectResumesFromItsLastProvenChunk()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Chunked(1, Enumerable.Repeat(65_536, 10).ToArray());
        var bytes = o.Bytes.ToArray();
        var header = 12 + 24 * 10;
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(0, s.Length);
            await s.AppendAsync(bytes.AsMemory(0, header + 3 * 65_536 + 1000));  // three whole chunks and part of the fourth
            Assert.False(await store.HasAsync(o.Key));
        }
        // Garbage past the proven bytes, as unsynced pages after a power cut would leave.
        var part = Directory.GetFiles(Path.Combine(dir, "staging")).Single();
        Append(part, new byte[5000]);
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(header + 3 * 65_536, s.Length);
            await s.AppendAsync(bytes.AsMemory((int)s.Length));
            var r = await s.CommitAsync();
            Assert.Equal(PutOutcome.Stored, r.Outcome);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "staging")));
        Assert.Equal(bytes, (await store.ReadAsync(o.Key)).Bytes);
    }

    [Fact]
    public async Task ABadChunkIsCutOffAndRefused()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Chunked(2, Enumerable.Repeat(65_536, 6).ToArray());
        var bytes = o.Bytes.ToArray();
        var header = 12 + 24 * 6;
        var bad = bytes.ToArray(); bad[header + 2 * 65_536 + 7] ^= 1;   // inside the third chunk
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength))
        {
            var e = await Assert.ThrowsAsync<ObjectStoreException>(() => s.AppendAsync(bad));
            Assert.Equal(ObjectStoreCodes.StagingRejected, e.Code);
            Assert.Equal(header + 2 * 65_536, s.Length);
            await Assert.ThrowsAsync<ObjectStoreException>(() => s.AppendAsync(bytes.AsMemory(0, 1)));
        }
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(header + 2 * 65_536, s.Length);
            await s.AppendAsync(bytes.AsMemory((int)s.Length));
            Assert.Equal(PutOutcome.Stored, (await s.CommitAsync()).Outcome);
        }
        // A wrong header is refused before any chunk: nothing to resume from.
        var other = Synthetic.Chunked(3, 1000, 1000);
        var wrong = other.Bytes.ToArray(); wrong[20] ^= 1;
        await using (var s = await store.StageAsync(other.Key, other.DeclaredLength))
            Assert.Equal(ObjectStoreCodes.StagingRejected, (await Assert.ThrowsAsync<ObjectStoreException>(() => s.AppendAsync(wrong))).Code);
        await using (var s = await store.StageAsync(other.Key, other.DeclaredLength)) Assert.Equal(0, s.Length);
    }

    [Fact]
    public async Task HeaderlessStagingHasNoPartialProofAndStartsAgain()
    {
        using var t = new TempDirectory();
        var o = Synthetic.Headerless(4, 300_000);
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength)) await s.AppendAsync(o.Bytes[..200_000]);
        await using (var s = await store.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(0, s.Length);
            await s.AppendAsync(o.Bytes);
            Assert.Equal(PutOutcome.Stored, (await s.CommitAsync()).Outcome);
        }
        var bad = Synthetic.Headerless(5, 1000);
        var wrongKey = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([1]));
        await using (var s = await store.StageAsync(wrongKey, bad.DeclaredLength))
        {
            await s.AppendAsync(bad.Bytes);
            var r = await s.CommitAsync();
            Assert.Equal((PutOutcome.Refused, ObjectRefusalCodes.KeyMismatch), (r.Outcome, r.Code));
        }
        Assert.False(await store.HasAsync(wrongKey));
    }

    [Fact]
    public async Task StagingIsBounded()
    {
        using var t = new TempDirectory();
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick with { MaxStagings = 2, MaxStagingBytes = 1 << 20 });
        var a = Synthetic.Headerless(1, 1000); var b = Synthetic.Headerless(2, 1000); var c = Synthetic.Headerless(3, 1000);
        await using var sa = await store.StageAsync(a.Key, 1000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageAsync(a.Key, 1000));
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.StageAsync(b.Key, 1 << 20))).Code);
        await using var sb = await store.StageAsync(b.Key, 1000);
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(() => store.StageAsync(c.Key, 1000))).Code);
        await sb.AbandonAsync();
        await using var sc = await store.StageAsync(c.Key, 1000);
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(Synthetic.Config(1).Key, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => store.StageAsync(Synthetic.Headerless(9).Key, (512L << 20) + 1));
    }

    [Theory, Trait("Speed", "Slow")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A512MiBObjectStagesCommitsAndReadsByRange(bool mixedLibrary)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        const long total = 512L << 20;
        const int count = 512;
        var header = 12 + 24 * count;
        var sizes = Enumerable.Repeat((int)((total - header) / count), count).ToArray();
        sizes[^1] += (int)(total - header - sizes.Sum(s => (long)s));
        byte[] Chunk(int i) { var b = new byte[sizes[i]]; new Random(1000 + i).NextBytes(b); b[0] = (byte)'N'; return b; }
        var head = new byte[header];
        "BLTE"u8.CopyTo(head); BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), (uint)header);
        head[8] = 0x0F; head[10] = count >> 8; head[11] = count & 0xFF;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12 + i * 24), (uint)sizes[i]);
            MD5.HashData(Chunk(i)).CopyTo(head, 12 + i * 24 + 8);
        }
        var key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(head));
        await using var store = mixedLibrary ? await ObjectStore.OpenLibraryAsync(dir, Quick) : await ObjectStore.OpenAsync(dir, Quick);
        var owner = mixedLibrary ? store.ForOwner("large", LibraryOwnerScope.Build, "streaming", LibraryObjectRole.Original) : null;
        IObjectWriter writer = owner is null ? store : owner;
        await using (var s = await writer.StageAsync(key, total))
        {
            await s.AppendAsync(head);
            for (var i = 0; i < count / 2; i++) await s.AppendAsync(Chunk(i));
        }
        await using (var s = await writer.StageAsync(key, total))
        {
            Assert.Equal(header + sizes.Take(count / 2).Sum(x => (long)x), s.Length);
            for (var i = count / 2; i < count; i++) await s.AppendAsync(Chunk(i));
            Assert.Equal(PutOutcome.Stored, (await s.CommitAsync()).Outcome);
        }
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadAsync(key))).Code);
        var last = Chunk(count - 1);
        var tail = new byte[4096];
        Assert.Equal(ReadState.Present, (await store.ReadRangeAsync(key, total - tail.Length, tail)).State);
        Assert.Equal(last.AsSpan(last.Length - tail.Length).ToArray(), tail);
        var middle = new byte[100];
        var at = header + sizes.Take(300).Sum(x => (long)x);
        Assert.Equal(ReadState.Present, (await store.ReadRangeAsync(key, at, middle)).State);
        Assert.Equal(Chunk(300).AsSpan(0, 100).ToArray(), middle);
        Assert.Equal(0, (await store.VerifyAsync()).CorruptCount);
        if (owner is not null) Assert.True(await owner.OwnsAsync(key));
    }
}
