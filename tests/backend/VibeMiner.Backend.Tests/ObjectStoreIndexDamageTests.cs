using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Backend.Tests.ObjectStoreTestKit;

namespace VibeMiner.Backend.Tests;

/// <summary>More packs than cached handles, and an index file with a flipped bit: named states or a rebuild, never a raw exception.</summary>
public sealed class ObjectStoreIndexDamageTests
{
    private static List<PutObject> Fill() => Enumerable.Range(0, 24).Select(i => i % 4 == 3 ? Synthetic.Chunked(i, 9000, 70_000) : Synthetic.Headerless(i, 15_000)).ToList();

    [Fact]
    public async Task MorePacksThanHandlesReadVerifyAndCompactEvenUnderConcurrency()
    {
        // Regression for QA on #153: acquiring a pack evicted the entry just added, so its lease wrapped a closed handle.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var options = Quick with { MaxPackBytes = 20_000, PackHandles = 2 };
        var objects = Fill();
        await using (var store = await ObjectStore.OpenAsync(dir, options)) foreach (var o in objects) await store.PutAsync([o]);
        Assert.True(PackFiles(dir).Length > 10);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            foreach (var o in objects)
            {
                Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
                var range = new byte[100];
                Assert.Equal(ReadState.Present, (await store.ReadRangeAsync(o.Key, 500, range)).State);
                Assert.Equal(o.Bytes.Span.Slice(500, 100).ToArray(), range);
            }
            Assert.Equal((objects.Count, 0L, 0L), ((int)(await store.VerifyAsync(full: true)).Checked, (await store.VerifyAsync()).CorruptCount, (await store.VerifyAsync()).LostCount));
            var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
            {
                var rng = new Random(w);
                for (var i = 0; i < 300; i++)
                {
                    var o = objects[rng.Next(objects.Count)];
                    try
                    {
                        var r = await store.ReadAsync(o.Key);
                        if (r.State != ReadState.Present || !r.Bytes.AsSpan().SequenceEqual(o.Bytes.Span)) failures.Enqueue($"{o.Key} {r.State}");
                    }
                    catch (Exception e) { failures.Enqueue($"{o.Key} {e.GetType().Name}: {e.Message}"); }
                }
            })));
            Assert.True(failures.IsEmpty, string.Join("; ", failures.Take(5)));
            await store.DeleteAsync(objects.Where((_, i) => i % 2 == 0).Select(o => o.Key).ToList());
            Assert.True((await store.CompactAsync(new CompactOptions(0.1, 100))).PacksCompacted > 0);
            foreach (var o in objects.Where((_, i) => i % 2 == 1)) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
        }
    }

    [Fact]
    public async Task ANullInAHistoryRowIsANamedStateNotARawException()
    {
        // Review of #153: a history column read as NULL threw InvalidOperationException past the reader pool.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(1)));
        var index = Path.Combine(dir, "objects.sqlite3");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            // Drop NOT NULL from the stored schema (the column list still matches), then damage one row's URL to NULL.
            cmd.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_master SET sql = replace(sql, 'url TEXT NOT NULL', 'url TEXT') WHERE name = 'history'; PRAGMA writable_schema=OFF;";
            cmd.ExecuteNonQuery();
        }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE history SET url = NULL"; cmd.ExecuteNonQuery();
        }
        // The open-time totals see the damaged row: a reader refuses, the writer rebuilds and the record reads whole again.
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir))).Code);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Equal(Synthetic.Observation(1).Url, Assert.Single(await store.ReadHistoryAsync(new HistoryQuery())).Url);
        }
    }

    /// <summary>An index that is structurally sound but no longer says what the packs say. Each must be found at open.</summary>
    public static TheoryData<string> Damage() => new() { "out-of-order", "dropped-rows", "altered-key", "added-row", "dropped-history" };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task AnIndexThatHidesOrInventsRowsIsFoundAtOpenAndRebuilt(string damage)
    {
        // QA on #153: rows out of key order passed quick_check, lookups then missed objects in the packs and Verify looped.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var objects = Fill();
        await using (var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 60_000 }))
        {
            foreach (var o in objects) await store.PutAsync([o]);
            await store.PutAsync([.. HistoryRecordFormat.Objects(Synthetic.Observation(1)), .. HistoryRecordFormat.Objects(Synthetic.Observation(2))]);
        }
        var index = Path.Combine(dir, "objects.sqlite3");
        static byte[] K(ObjectKey key) { var k = new byte[18]; k[0] = (byte)key.Type; k[1] = key.Namespace; key.WriteKey(k.AsSpan(2)); return k; }
        void Sql(string sql, params (string, object)[] values)
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v);
            cmd.ExecuteNonQuery();
        }
        switch (damage)
        {
            case "out-of-order":
                // Flip a high bit in the first key byte of stored rows until SQLite reports rows out of order: lookups would miss them.
                // Prefer QA's case (quick_check passes, integrity_check finds the order broken); any rows out of order will do.
                var bytes = File.ReadAllBytes(index);
                byte[]? chosen = null, fallback = null; var seen = new List<string>();
                foreach (var o in objects)
                foreach (var (offset, mask) in new[] { (2, 0x80), (2, 0x40), (3, 0x80), (2, 0x01), (17, 0x80) })
                {
                    var at = bytes.AsSpan().IndexOf(K(o.Key));
                    if (at < 0 || chosen is not null) continue;
                    var copy = bytes.ToArray(); copy[at + offset] ^= (byte)mask;
                    File.WriteAllBytes(index, copy);
                    using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Mode=ReadOnly;Pooling=False");
                    c.Open();
                    using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA quick_check";
                    var quick = cmd.ExecuteScalar() as string;
                    cmd.CommandText = "PRAGMA integrity_check";
                    var full = string.Join("\n", ReadAll(cmd));
                    seen.Add($"{quick} | {full}");
                    if (!full.Contains("PRIMARY KEY order", StringComparison.Ordinal) && !full.Contains("out of order", StringComparison.Ordinal)) continue;
                    if (quick == "ok") chosen = copy; else fallback ??= copy;
                }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Assert.True((chosen ?? fallback) is not null, "no flip put rows out of key order: " + string.Join(" || ", seen.Distinct().Take(8)));
                File.WriteAllBytes(index, (chosen ?? fallback)!);
                break;
            case "dropped-rows": Sql("DELETE FROM objects WHERE k IN ($a, $b)", ("$a", K(objects[3].Key)), ("$b", K(objects[7].Key))); break;
            case "altered-key": Sql("UPDATE objects SET k = $n WHERE k = $o", ("$n", K(Synthetic.Headerless(999).Key)), ("$o", K(objects[5].Key))); break;
            case "added-row": Sql("INSERT INTO objects SELECT $n, pack, offset, length, sha256, NULL FROM objects WHERE k = $o", ("$n", K(Synthetic.Headerless(998).Key)), ("$o", K(objects[5].Key))); break;
            case "dropped-history": Sql("DELETE FROM history WHERE record = (SELECT record FROM history LIMIT 1)"); break;
        }
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir))).Code);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));
            foreach (var o in objects) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
            Assert.False(await store.HasAsync(Synthetic.Headerless(999).Key));
            Assert.False(await store.HasAsync(Synthetic.Headerless(998).Key));
            Assert.Equal(2, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
            var verify = await store.VerifyAsync();
            Assert.Equal((objects.Count + 4, 0L), (verify.Checked, verify.CorruptCount));
        }
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir)) Assert.True(await reader.HasAsync(objects[3].Key));
    }

    private static List<string> ReadAll(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        var lines = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) lines.Add(r.GetValue(0) as string ?? "");
        return lines;
    }

    public static TheoryData<int> Seeds() { var d = new TheoryData<int>(); for (var i = 0; i < 60; i++) d.Add(i); return d; }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task AFlippedBitInTheIndexRebuildsItOrIsANamedStateNeverARawException(int seed)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var objects = Fill();
        await using (var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 60_000 }))
        {
            foreach (var o in objects) await store.PutAsync([o]);
            await store.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(seed)));
            await store.DeleteAsync([objects[0].Key]);
        }
        var index = Path.Combine(dir, "objects.sqlite3");
        var rng = new Random(seed);
        var length = new FileInfo(index).Length;
        // Half the runs hit the first pages (header, schema, store and pack rows), half anywhere.
        var at = seed % 2 == 0 ? rng.NextInt64(Math.Min(length, 3 * 4096)) : rng.NextInt64(length);
        Flip(index, at, (byte)(1 << rng.Next(8)));

        try { await using var shared = await ObjectStore.OpenReadOnlyAsync(dir); await Exercise(shared, shared, () => shared.VerifyAsync(), objects, rebuilt: false); }
        catch (ObjectStoreException) { /* a named state is a right answer for a reader that can't rebuild */ }

        ObjectStore writer;
        try { writer = await ObjectStore.OpenAsync(dir, Quick); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.NewerFormat) { return; }  // a flip in user_version reads as a newer index: named, nothing changed
        await using (writer) await Exercise(writer, writer, () => writer.VerifyAsync(), objects, writer.OpenReport.IndexRebuilt);
        // Whatever it found, a Scan from the packs gives the full answer back.
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.ScanAsync();
            foreach (var o in objects.Skip(1)) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
            Assert.False(await store.HasAsync(objects[0].Key));
            Assert.Single(await store.ReadHistoryAsync(new HistoryQuery()));
        }
    }

    /// <summary>Every call answers with a state or a named exception. After a rebuild, every object is exact.</summary>
    private static async Task Exercise(IObjectReader store, IHistoryReader history, Func<Task<VerifyReport>> verify, List<PutObject> objects, bool rebuilt)
    {
        foreach (var o in objects.Skip(1))
        {
            try
            {
                var r = await store.ReadAsync(o.Key);
                if (r.State == ReadState.Present) Assert.Equal(o.Bytes.ToArray(), r.Bytes);
                else Assert.False(rebuilt, $"{o.Key} read {r.State} after a rebuild");
                await store.HasAsync(o.Key);
                await store.ReadRangeAsync(o.Key, 0, new byte[10]);
            }
            catch (ObjectStoreException) { Assert.False(rebuilt); }
        }
        try { await history.ReadHistoryAsync(new HistoryQuery()); await verify(); }
        catch (ObjectStoreException) { Assert.False(rebuilt); }
    }
}
