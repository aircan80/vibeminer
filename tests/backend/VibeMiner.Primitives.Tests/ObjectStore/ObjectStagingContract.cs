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
    internal static async Task<ObjectKey> AssertFinishedSessionCannotAbandonNewerAsync(IObjectReader reader,
        IObjectWriter first, IObjectWriter second, IObjectWriter third, int terminal)
    {
        var item = Synthetic.Chunked(2290 + terminal, 65_536, 65_536, 65_536);
        const int header = 12 + 24 * 3;
        var old = await first.StageAsync(item.Key, item.DeclaredLength);
        try
        {
            if (terminal == 2)
            {
                await old.AppendAsync(item.Bytes);
                Assert.Equal(PutOutcome.Stored, (await old.CommitAsync()).Outcome);
            }
            else
            {
                await old.AppendAsync(item.Bytes[..(header + 65_536 + 31)]);
                if (terminal == 0) await old.DisposeAsync(); else await old.AbandonAsync();
            }
            await using (var newer = await second.StageAsync(item.Key, item.DeclaredLength))
            {
                Assert.Equal(terminal == 0 ? header + 65_536 : 0, newer.Length);
                var nextEnd = header + 2 * 65_536 + 73;
                await newer.AppendAsync(item.Bytes.Slice((int)newer.Length, nextEnd - (int)newer.Length));
                await old.AbandonAsync(); await old.AbandonAsync(); await old.DisposeAsync();
                if (terminal == 2) Assert.Equal(item.Bytes.ToArray(), (await reader.ReadAsync(item.Key)).Bytes);
            }
            // A fake's newer private buffer can hide loss of its shared resumable bytes. Reopen a third session to prove retention.
            await using var resumed = await third.StageAsync(item.Key, item.DeclaredLength);
            Assert.Equal(header + 2 * 65_536, resumed.Length);
            await resumed.AppendAsync(item.Bytes[(int)resumed.Length..]);
            Assert.Equal(terminal == 2 ? PutOutcome.AlreadyPresent : PutOutcome.Stored, (await resumed.CommitAsync()).Outcome);
            Assert.Equal(item.Bytes.ToArray(), (await reader.ReadAsync(item.Key)).Bytes);
            return item.Key;
        }
        finally { await old.DisposeAsync(); }
    }

}
