using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

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
        // After a clean close the open runs only the cheap check, so the damage is met in use: a named state, never a raw exception.
        await using (var shared = await ObjectStore.OpenReadOnlyAsync(dir))
            Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await shared.ReadHistoryAsync(new HistoryQuery()))).Code);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.False(store.OpenReport.FullIndexCheck);
            Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await store.ReadHistoryAsync(new HistoryQuery()))).Code);
            var scrub = await store.ScrubAsync();  // the writer's repair: the full check finds it and rebuilds from the packs
            Assert.True(scrub.IndexRebuilt, scrub.IndexProblem);
            Assert.Equal(Synthetic.Observation(1).Url, Assert.Single(await store.ReadHistoryAsync(new HistoryQuery())).Url);
        }
    }

    [Theory]
    [InlineData("DELETE FROM objects WHERE k = (SELECT min(k) FROM objects)", "the index has 25 object rows but its totals record 26")]
    [InlineData("UPDATE objects SET sha256 = zeroblob(32) WHERE k = (SELECT min(k) FROM objects)", "the index's 26 object rows don't match the digest in its totals")]
    [InlineData("DELETE FROM history", "the index has 0 history rows but its totals record 1")]
    [InlineData("UPDATE history SET url = url || 'x'", "the index's 1 history rows don't match the digest in its totals")]
    public async Task AFullCheckNamesWhetherTheCountOrTheDigestDiffers(string damage, string problem)
    {
        // #163: a digest-only mismatch read as "(9 rows, 9 recorded)", which contradicts itself.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.PutAsync(Fill());
            await store.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(1)));
        }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = damage;
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        await using var shared = await ObjectStore.OpenReadOnlyAsync(dir);
        Assert.Equal(problem, (await shared.ScrubAsync()).IndexProblem);
    }

    [Fact]
    public async Task AFullCheckWhileTheWriterCommitsNeverCallsASoundIndexDamaged()
    {
        // Review of 9ba08cd: totals and rows read in different snapshots made a live store's sound index read as unreadable.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var seed = 1000;
        ObjectStore? writer = null;
        // Commits land between the full check's first stamp and its measure: once, or on every try.
        var commits = 0; var always = false;
        void Commit(StorePoint p)
        {
            if (p != StorePoint.IndexMeasuring || writer is null || !Volatile.Read(ref always) && Volatile.Read(ref commits) >= 1) return;
            var o = Synthetic.Headerless(Interlocked.Increment(ref seed), 3000);
            Assert.Equal(PutOutcome.Stored, Assert.Single(writer.PutAsync([o], commitNow: true).GetAwaiter().GetResult()).Outcome);
            Interlocked.Increment(ref commits);
        }
        writer = await ObjectStore.OpenAsync(dir, Quick with { Fault = Commit });
        await using var w = writer;
        await writer.PutAsync(Fill());
        await writer.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(1)));
        // The writer runs, so its marker is unset: a reader's open runs the full check, and must not refuse a sound index.
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir, Quick with { Fault = Commit }))
        {
            Assert.Equal(1, commits);
            Volatile.Write(ref commits, 0);
            var scrub = await reader.ScrubAsync();
            Assert.Null(scrub.IndexProblem);
            Assert.True(scrub.IndexChecked);
            Volatile.Write(ref always, true);  // a writer that never stops: no stable view, so nothing is concluded
            var restless = await reader.ScrubAsync();
            Assert.Null(restless.IndexProblem);
            Assert.False(restless.IndexChecked);
            Volatile.Write(ref always, false);
        }
        // The writer's own scrub measures on readers, so a Put from inside the measure completes: writes aren't held up.
        Volatile.Write(ref commits, 0);
        var mine = await writer.ScrubAsync();
        Assert.Equal((null, false), (mine.IndexProblem, mine.IndexRebuilt));
        Assert.Equal(1, commits);
        Volatile.Write(ref always, true);  // never settles on readers: one pass with the writer held concludes it
        var held = await writer.ScrubAsync();
        Assert.Equal((null, false, true), (held.IndexProblem, held.IndexRebuilt, held.IndexChecked));
    }

    [Fact]
    public async Task AScrubHonoursItsCancellation()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        using var cancel = new CancellationTokenSource();
        await using var store = await ObjectStore.OpenAsync(dir, Quick with { Fault = p => { if (p == StorePoint.IndexMeasuring) cancel.Cancel(); } });
        await store.PutAsync(Fill());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ScrubAsync(ct: cancel.Token));
        Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([Synthetic.Headerless(777)])).Outcome);  // the store goes on
    }

    [Fact]
    public async Task OnlyAnUncleanShutdownMakesTheNextOpenRunTheFullCheck()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.FullIndexCheck is false);  // a new store has nothing to check
            await store.PutAsync([Synthetic.Headerless(1)]);
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) Assert.False(store.OpenReport.FullIndexCheck);
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir))
        {
            Assert.True(await reader.HasAsync(Synthetic.Headerless(1).Key));
            Assert.False(reader.OpenReport.FullIndexCheck);  // closed cleanly: the cheap check only
        }
        var killed = await ObjectStore.OpenAsync(dir, Quick);
        await killed.PutAsync([Synthetic.Headerless(2)]);
        killed.Kill();  // as a crash: the marker the open cleared stays cleared
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir))  // a reader pays the full check too, and says so
        {
            Assert.True(reader.OpenReport.FullIndexCheck);
            Assert.Equal((0, 0L, false), (reader.OpenReport.RecoveredEntries, reader.OpenReport.TruncatedBytes, reader.OpenReport.IndexRebuilt));
            Assert.Empty(reader.OpenReport.Problems);
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.FullIndexCheck);
            Assert.False(store.OpenReport.IndexRebuilt);
            Assert.True(await store.HasAsync(Synthetic.Headerless(2).Key));
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) Assert.False(store.OpenReport.FullIndexCheck);
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir)) Assert.False(reader.OpenReport.FullIndexCheck);
    }

    /// <summary>An index that is structurally sound but no longer says what the packs say: found by the next scrub after a clean close, by the open after a crash.</summary>
    public static TheoryData<string> Damage() => new() { "out-of-order", "dropped-rows", "altered-key", "added-row", "dropped-history" };

    [Theory]
    [MemberData(nameof(Damage))]
    public async Task AnIndexThatHidesOrInventsRowsIsFoundByAScrubOrTheOpenAfterACrash(string damage)
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
        var unclean = Path.Combine(t.Path, "unclean");
        CopyFolder(dir, unclean);
        async Task Whole(ObjectStore store)
        {
            foreach (var o in objects) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
            Assert.False(await store.HasAsync(Synthetic.Headerless(999).Key));
            Assert.False(await store.HasAsync(Synthetic.Headerless(998).Key));
            Assert.Equal(2, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
            var verify = await store.VerifyAsync();
            Assert.Equal((objects.Count + 4, 0L), (verify.Checked, verify.CorruptCount));
        }
        // Damage done after a clean close (D126): the open runs only the cheap check and doesn't see it; the next scrub does.
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir)) Assert.NotNull((await reader.ScrubAsync()).IndexProblem);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.False(store.OpenReport.FullIndexCheck);
            var scrub = await store.ScrubAsync();
            Assert.NotNull(scrub.IndexProblem);
            Assert.True(scrub.IndexRebuilt);
            await Whole(store);
        }
        await using (var reader = await ObjectStore.OpenReadOnlyAsync(dir)) Assert.Null((await reader.ScrubAsync()).IndexProblem);
        // The same damage after an unclean shutdown: the open's full check finds it, a reader refuses, the writer rebuilds.
        var crashed = await ObjectStore.OpenAsync(unclean, Quick);
        crashed.Kill();
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(unclean))).Code);
        await using (var store = await ObjectStore.OpenAsync(unclean, Quick))
        {
            Assert.True(store.OpenReport.FullIndexCheck);
            Assert.True(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));
            await Whole(store);
        }
    }

    private static void SetUserVersion(string index, int version)
    {
        // user_version is the big-endian i32 at offset 60 of the database header; a clean close leaves no -wal to override it.
        var b = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b, version);
        Write(index, 60, b);
    }

    [Theory]
    [InlineData(0)]               // 1 with its low bit flipped
    [InlineData(3)]
    [InlineData(0x40000001)]
    [InlineData(int.MinValue + 1)] // the sign bit flipped
    [InlineData(-5)]              // below 1, more than one bit off
    public async Task AFlippedUserVersionOnAFormatOneIndexIsIndexDamage(int version)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var objects = Fill();
        await using (var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 60_000 }))
        {
            foreach (var o in objects) await store.PutAsync([o]);
            await store.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(1)));
        }
        var index = Path.Combine(dir, "objects.sqlite3");
        SetUserVersion(index, version);
        var before = HashFolder(dir);
        var e = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir));
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, e.Code);
        Assert.Equal(before, HashFolder(dir));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Contains(store.OpenReport.Problems, p => p.Contains("schema version", StringComparison.Ordinal));
            foreach (var o in objects) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
            Assert.Single(await store.ReadHistoryAsync(new HistoryQuery()));
        }
        await using var reader = await ObjectStore.OpenReadOnlyAsync(dir);
        Assert.True(await reader.HasAsync(objects[0].Key));
    }

    [Theory]
    [InlineData(3, "CREATE TABLE later(x)")]                            // one bit from 1, but the schema isn't format 1's
    [InlineData(5, "CREATE INDEX objects_pack ON objects(pack)")]
    [InlineData(3, "ALTER TABLE objects ADD COLUMN extra INTEGER")]
    [InlineData(2, null)]                                               // two bits from 1: newer even with format 1's schema
    [InlineData(0x7FFFFFFF, null)]
    public async Task ANewerIndexIsStillRefusedAndLeftUntouched(int version, string? change)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([Synthetic.Headerless(1)]);
        var index = Path.Combine(dir, "objects.sqlite3");
        if (change is not null)
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Pooling=False"))
            { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = change; cmd.ExecuteNonQuery(); }
        SetUserVersion(index, version);
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Quick))).Code);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir))).Code);
        Assert.Equal(before, HashFolder(dir));
    }

    [Theory]
    [InlineData(null)]                     // an empty database file
    [InlineData("CREATE TABLE half(x)")]   // a create that stopped before format 1's schema was complete
    public async Task AnIndexNeverCommittedIsStillMissingToAReader(string? schema)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([Synthetic.Headerless(1)]);
        var index = Path.Combine(dir, "objects.sqlite3");
        File.Delete(index);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={index};Pooling=False"))
        {
            c.Open();
            if (schema is not null) { using var cmd = c.CreateCommand(); cmd.CommandText = schema; cmd.ExecuteNonQuery(); }
        }
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.Missing, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir))).Code);
        Assert.Equal(before, HashFolder(dir));
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static List<string> ReadAll(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        var lines = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) lines.Add(r.GetValue(0) as string ?? "");
        return lines;
    }

    public static TheoryData<int> Seeds() { var d = new TheoryData<int>(); for (var i = 0; i < 60; i++) d.Add(i); return d; }

    [Theory, Trait("Speed", "Slow")]
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
        // A flipped user_version is index damage, rebuilt (6.3 step 2); a flip in application_id reads as a newer kind: named, nothing changed.
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.NewerFormat) { return; }
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
