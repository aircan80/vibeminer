using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.Primitives.Tests;

internal static class ObjectStagingContract
{
    internal static async Task AssertChunkResumeAsync(IObjectReader Reader, IObjectWriter Writer)
    {
        var o = Synthetic.Chunked(1, Enumerable.Repeat(65_536, 6).ToArray());
        var bytes = o.Bytes.ToArray(); var header = 12 + 24 * 6;
        await using (var s = await Writer.StageAsync(o.Key, o.DeclaredLength))
            await s.AppendAsync(bytes.AsMemory(0, header + 2 * 65_536 + 100));
        var bad = bytes.ToArray(); bad[header + 3 * 65_536 + 9] ^= 1;
        await using (var s = await Writer.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(header + 2 * 65_536, s.Length);
            var e = await Assert.ThrowsAsync<ObjectStoreException>(() => s.AppendAsync(bad.AsMemory((int)s.Length)));
            Assert.Equal(ObjectStoreCodes.StagingRejected, e.Code);
            Assert.Equal(header + 3 * 65_536, s.Length);
        }
        Assert.False(await Reader.HasAsync(o.Key));
        await using (var s = await Writer.StageAsync(o.Key, o.DeclaredLength))
        {
            Assert.Equal(header + 3 * 65_536, s.Length);
            await s.AppendAsync(bytes.AsMemory((int)s.Length));
            Assert.Equal(PutOutcome.Stored, (await s.CommitAsync()).Outcome);
        }
        Assert.Equal(bytes, (await Reader.ReadAsync(o.Key)).Bytes);
        await using (var s = await Writer.StageAsync(Synthetic.Headerless(9, 1000).Key, 1000))
        {
            await s.AppendAsync(Synthetic.Headerless(9, 1000).Bytes);
            Assert.Equal(PutOutcome.Stored, (await s.CommitAsync()).Outcome);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => Writer.StageAsync(Synthetic.Config(1).Key, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => Writer.StageAsync(Synthetic.Headerless(9).Key, (512L << 20) + 1));
    }
}
