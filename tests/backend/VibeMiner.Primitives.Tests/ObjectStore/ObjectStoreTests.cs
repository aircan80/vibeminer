using Xunit;
using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class ObjectStoreTests
{
    [Fact]
    public async Task EveryKeyTypeRoundTripsWithItsOriginAndIsVisibleOnlyAsCommitted()
    {
        using var t = new TempDirectory();
        var objects = new[]
        {
            Synthetic.Config(1), Synthetic.Index(2, 500), Synthetic.Index(3, 300, offsetWidth: 0), Synthetic.Index(4, 0),
            Synthetic.Headerless(5, 9), Synthetic.Headerless(6, 70_000), Synthetic.Chunked(7, 10, 65_536, 65_537, 1),
            Synthetic.Config(8) with { Origin = ObjectOrigin.SharedStore },
        };
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        var results = await store.PutAsync([.. objects, .. HistoryRecordFormat.Objects(Synthetic.Observation(1))]);
        Assert.All(results, r => Assert.Equal(PutOutcome.Stored, r.Outcome));
        foreach (var o in objects)
        {
            Assert.True(await store.HasAsync(o.Key));
            var read = await store.ReadAsync(o.Key);
            Assert.Equal(ReadState.Present, read.State);
            Assert.Equal(o.Bytes.ToArray(), read.Bytes);
            Assert.Equal(o.Origin, read.Info!.Origin);
            Assert.Equal(o.Bytes.Length, read.Info.Length);
        }
        Assert.False(await store.HasAsync(Synthetic.Config(99).Key));
        Assert.Equal(ReadState.Absent, (await store.ReadAsync(Synthetic.Config(99).Key)).State);
    }

    [Fact]
    public async Task StatsCountTheCommittedObjectsHistoryAndPacksForTheWriterAndAReader()
    {
        using var t = new TempDirectory();
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        var empty = await store.StatsAsync();
        Assert.Equal((0L, 0L, 0L), (empty.Objects, empty.HistoryRecords, empty.LiveBytes));
        await store.PutAsync([Synthetic.Config(1), Synthetic.Headerless(2, 9), .. HistoryRecordFormat.Objects(Synthetic.Observation(1))], commitNow: true);
        var stats = await store.StatsAsync();
        Assert.Equal((1, 4L, 1L), (stats.Packs, stats.Objects, stats.HistoryRecords));
        Assert.InRange(stats.LiveBytes, 1, stats.PackBytes);
        Assert.Equal(new FileInfo(PackFiles(StoreDir(t)).Single()).Length, stats.PackBytes);
        await using var reader = await ObjectStore.OpenReadOnlyAsync(StoreDir(t));
        Assert.Equal(stats, await reader.StatsAsync());
    }

    [Fact]
    public async Task BytesThatFailTheirKeyTypeCheckNeverReachAPack()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        var config = Synthetic.Config(1);
        var chunked = Synthetic.Chunked(2, 100, 200);
        var badChunk = chunked.Bytes.ToArray(); badChunk[^5] ^= 1;
        var index = Synthetic.Index(3, 400);
        var badPage = index.Bytes.ToArray(); badPage[10] ^= 1;
        var fileIndex = Synthetic.Index(4, 50, offsetWidth: 0);
        var headerless = Synthetic.Headerless(5);
        var shortChunks = Synthetic.Chunked(6, 100, 200);
        var shortBytes = shortChunks.Bytes[..^1].ToArray();
        var record = HistoryRecordFormat.Objects(Synthetic.Observation(7))[1];
        var cases = new (PutObject o, string code)[]
        {
            (config with { Key = ObjectKey.Parse(ObjectKeyType.Config, new string('a', 32)) }, ObjectRefusalCodes.KeyMismatch),
            (config with { DeclaredLength = config.DeclaredLength + 1 }, ObjectRefusalCodes.SizeMismatch),
            (chunked with { Bytes = badChunk }, ObjectRefusalCodes.BlteInvalid),
            (shortChunks with { Bytes = shortBytes, DeclaredLength = shortBytes.Length }, ObjectRefusalCodes.SizeMismatch),
            (index with { Bytes = badPage }, ObjectRefusalCodes.IndexInvalid),
            (fileIndex with { Key = new ObjectKey(ObjectKeyType.ArchiveIndex, fileIndex.Key.KeyBytes()) }, ObjectRefusalCodes.IndexUnsupported),
            (TableFormat(chunked, 0x10), ObjectRefusalCodes.BlteUnsupported),
            (headerless with { Key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([1])) }, ObjectRefusalCodes.KeyMismatch),
            (headerless with { Key = new ObjectKey((ObjectKeyType)7, headerless.Key.KeyBytes()) }, ObjectRefusalCodes.KeyTypeUnsupported),
            (headerless with { Key = new ObjectKey(ObjectKeyType.Encoded, headerless.Key.KeyBytes(), 1) }, ObjectRefusalCodes.NamespaceUnsupported),
            (record, ObjectRefusalCodes.HistoryDocumentMissing),
            (new PutObject(headerless.Key, (64L << 20) + 1, headerless.Bytes), ObjectRefusalCodes.SizeMismatch),
        };
        foreach (var (o, code) in cases)
        {
            var r = Assert.Single(await store.PutAsync([o]));
            Assert.Equal(PutOutcome.Refused, r.Outcome);
            Assert.Equal(code, r.Code);
            Assert.False(await store.HasAsync(o.Key));
        }
        Assert.All(PackFiles(dir), p => Assert.True(new FileInfo(p).Length <= 64));
    }

    /// <summary>The same chunked object with another chunk table format byte, rekeyed so only the format is wrong.</summary>
    private static PutObject TableFormat(PutObject chunked, byte format)
    {
        var b = chunked.Bytes.ToArray(); b[8] = format;
        var headerLength = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4));
        return chunked with { Key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b.AsSpan(0, headerLength))), Bytes = b };
    }

    [Fact]
    public async Task ADuplicateKeyIsStoredOnceAndReportsAlreadyPresent()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Headerless(1, 5000);
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        var first = await store.PutAsync([o, o]);
        Assert.Equal([PutOutcome.Stored, PutOutcome.AlreadyPresent], first.Select(r => r.Outcome));
        var length = new FileInfo(PackFiles(dir).Single()).Length;
        Assert.Equal(PutOutcome.AlreadyPresent, Assert.Single(await store.PutAsync([o])).Outcome);
        Assert.Equal(length, new FileInfo(PackFiles(dir).Single()).Length);
    }

    [Fact]
    public async Task NothingIsVisibleUntilTheBatchIsCommittedAfterThePackSync()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        using var reached = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var gate = 0;
        var options = Quick with { Fault = p => { if (p == StorePoint.AfterPackSync && Volatile.Read(ref gate) == 1) { reached.Release(); release.Wait(); } } };
        await using var store = await ObjectStore.OpenAsync(dir, options);
        await store.PutAsync([Synthetic.Config(0)]);
        await using var other = await ObjectStore.OpenReadOnlyAsync(dir);
        var o = Synthetic.Headerless(1, 3000);
        Volatile.Write(ref gate, 1);
        var put = store.PutAsync([o], commitNow: true);
        Assert.True(await reached.WaitAsync(TimeSpan.FromSeconds(30)));
        // The bytes are in the pack and synced, but no reader in this or another instance can see them.
        Assert.False(await store.HasAsync(o.Key));
        Assert.Equal(ReadState.Absent, (await store.ReadAsync(o.Key)).State);
        Assert.False(await other.HasAsync(o.Key));
        Assert.False(put.IsCompleted);
        Volatile.Write(ref gate, 0); release.Set();
        Assert.Equal(PutOutcome.Stored, Assert.Single(await put).Outcome);
        Assert.True(await store.HasAsync(o.Key));
        Assert.True(await other.HasAsync(o.Key));
    }

    private sealed class ShiftedTime : TimeProvider
    {
        public long ShiftTicks;
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref ShiftTicks));
    }

    [Fact]
    public async Task AGroupCommitClosesAtItsSizeBoundsAtTheDelayOrWhenNothingElseIsQueued()
    {
        using var t = new TempDirectory();
        var events = new System.Collections.Concurrent.ConcurrentQueue<StorePoint>();
        var time = new ShiftedTime();
        using var blocked = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(true);
        var shiftAt = int.MaxValue; var written = 0;
        var options = new ObjectStoreOptions
        {
            CommitDelay = TimeSpan.FromHours(1), CommitObjects = 8, CommitBytes = 100_000, Time = time,
            Fault = p =>
            {
                if (p is StorePoint.EntryWritten or StorePoint.BeforePackSync) events.Enqueue(p);
                if (p == StorePoint.EntryStarted && !release.IsSet) { blocked.Release(); release.Wait(); }
                // Two hours pass once the first entry of the batch is pending (EntryWritten comes just before it is).
                if (p == StorePoint.EntryWritten && Interlocked.Increment(ref written) == Volatile.Read(ref shiftAt)) Interlocked.Add(ref time.ShiftTicks, TimeSpan.FromHours(2).Ticks);
            },
        };
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), options);
        List<int> Batches()
        {
            var sizes = new List<int>(); var n = 0;
            while (events.TryDequeue(out var e)) { if (e == StorePoint.EntryWritten) n++; else { sizes.Add(n); n = 0; } }
            Assert.Equal(0, n);
            return sizes;
        }
        // Nothing else queued: a lone Put commits at once, an hour's delay notwithstanding.
        Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([Synthetic.Headerless(1)]).WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.Equal([1], Batches());
        // Object bound: 20 objects in one Put are 8 + 8 + 4.
        await store.PutAsync(Enumerable.Range(10, 20).Select(i => Synthetic.Headerless(i)).ToList());
        Assert.Equal([8, 8, 4], Batches());
        // Byte bound: 60 KB objects close a batch at the second (120 KB of entries is past 100 KB).
        await store.PutAsync(Enumerable.Range(40, 5).Select(i => Synthetic.Headerless(i, 60_000)).ToList());
        Assert.Equal([2, 2, 1], Batches());
        // Delay bound: a second Put is already queued, so only the delay can close the first one's batch before it.
        Volatile.Write(ref shiftAt, Volatile.Read(ref written) + 2);
        release.Reset();
        var first = store.PutAsync([Synthetic.Headerless(60), Synthetic.Headerless(61)]);
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(30)));
        var second = store.PutAsync([Synthetic.Headerless(62), Synthetic.Headerless(63)]);  // in the queue once PutAsync returns
        release.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([2, 2], Batches());
        // Without the delay passing, the same two queued Puts share one batch.
        release.Reset();
        var third = store.PutAsync([Synthetic.Headerless(70), Synthetic.Headerless(71)]);
        Assert.True(await blocked.WaitAsync(TimeSpan.FromSeconds(30)));
        var fourth = store.PutAsync([Synthetic.Headerless(72), Synthetic.Headerless(73)]);
        release.Set();
        await Task.WhenAll(third, fourth).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([4], Batches());
    }

    [Fact]
    public async Task ARangeReadChecksOnlyTheBlocksUnderTheRange()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Chunked(1, Enumerable.Repeat(100_000, 30).ToArray());  // about 2.9 MiB, 46 blocks
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        await store.PutAsync([o]);
        var bytes = o.Bytes.ToArray();
        foreach (var (at, n) in new[] { (0L, 1), (0L, 65_536), (65_535L, 2), (100L, 300_000), (bytes.Length - 7L, 7), (bytes.Length - 70_000L, 70_000), (1_000_000L, 0) })
        {
            var buffer = new byte[n];
            var r = await store.ReadRangeAsync(o.Key, at, buffer);
            Assert.Equal(ReadState.Present, r.State);
            Assert.Equal(bytes.AsSpan((int)at, n).ToArray(), buffer);
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.ReadRangeAsync(o.Key, bytes.Length - 1, new byte[2]));
        // Damage block 40 and everything after it: ranges before it still read, ranges over it and whole reads are Corrupt.
        var (pack, offset, _) = Row(dir, o.Key);
        var damageFrom = offset + 96 + 40L * 65_536;
        Write(PackPath(dir, pack), damageFrom, new byte[bytes.Length - 40 * 65_536]);
        var early = new byte[1000];
        Assert.Equal(ReadState.Present, (await store.ReadRangeAsync(o.Key, 39L * 65_536, early)).State);
        Assert.Equal(bytes.AsSpan(39 * 65_536, 1000).ToArray(), early);
        var late = new byte[10];
        var bad = await store.ReadRangeAsync(o.Key, 40L * 65_536 + 5, late);
        Assert.Equal(ReadState.Corrupt, bad.State);
        Assert.Equal(new byte[10], late);  // nothing copied
        var whole = await store.ReadAsync(o.Key);
        Assert.Equal(ReadState.Corrupt, whole.State);
        Assert.Null(whole.Bytes);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("header")]
    [InlineData("table")]
    [InlineData("truncate")]
    [InlineData("sha-only")]
    public async Task ATruncatedOrBitFlippedEntryReadsCorruptAndReturnsNoBytes(string damage)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Headerless(1, 100_000);
        var neighbour = Synthetic.Headerless(2, 1000);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([neighbour, o]);
        var (pack, offset, length) = Row(dir, o.Key);
        var path = PackPath(dir, pack);
        switch (damage)
        {
            case "payload": Flip(path, offset + 96 + 70_000); break;
            case "header": Flip(path, offset + 30); break;
            case "table": Flip(path, offset + 96 + length + 1); break;
            case "truncate": using (var f = new FileStream(path, FileMode.Open)) f.SetLength(offset + 96 + length - 10); break;
            case "sha-only":
                // Change a byte and fix its block checksum: only the SHA-256 (first read in a session, up to 1 MiB) can tell.
                var payload = File.ReadAllBytes(path).AsSpan((int)offset + 96, (int)length).ToArray();
                payload[5] ^= 1;
                var crc = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(crc, PackFormat.Crc32C(payload.AsSpan(0, 65_536)));
                Write(path, offset + 96 + 5, payload.AsSpan(5, 1)); Write(path, offset + 96 + length, crc);
                break;
        }
        await using var reopened = await ObjectStore.OpenAsync(dir, Quick);
        var read = await reopened.ReadAsync(o.Key);
        Assert.Equal(ReadState.Corrupt, read.State);
        Assert.Null(read.Bytes);
        Assert.NotNull(read.Problem);
        if (damage != "sha-only")
        {
            var range = new byte[100];
            Assert.Equal(ReadState.Corrupt, (await reopened.ReadRangeAsync(o.Key, damage == "payload" ? 69_990 : 0, range)).State);
        }
        var verify = await reopened.VerifyAsync();
        Assert.Equal(1, verify.CorruptCount);
        Assert.Equal([o.Key], verify.Corrupt);
        Assert.Equal(ReadState.Present, (await reopened.ReadAsync(neighbour.Key)).State);
    }

    [Fact]
    public async Task AMissingPackReadsLost()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var a = Synthetic.Headerless(1, 300_000); var b = Synthetic.Headerless(2, 300_000);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 400_000 })) { await store.PutAsync([a]); await store.PutAsync([b]); }
        var (pack, _, _) = Row(dir, a.Key);
        File.Delete(PackPath(dir, pack));
        await using var reopened = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 400_000 });
        Assert.Contains(reopened.OpenReport.Problems, p => p.Contains("missing", StringComparison.Ordinal));
        Assert.Equal(ReadState.Lost, (await reopened.ReadAsync(a.Key)).State);
        Assert.Equal(ReadState.Present, (await reopened.ReadAsync(b.Key)).State);
        Assert.Equal(1, (await reopened.VerifyAsync()).LostCount);
    }

    [Fact]
    public async Task RecoveryIndexesWholeTailEntriesInOrderKeepsTheFirstCopyAndTruncatesTheTornRest()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var committed = Synthetic.Headerless(1, 1000);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([committed]);
        var path = PackFiles(dir).Single();
        var committedLength = new FileInfo(path).Length;
        var seq = NextSeq(dir);
        var fresh = Synthetic.Headerless(2, 2000);
        var gone = Synthetic.Headerless(3, 500);
        var refused = Synthetic.Headerless(4, 600);
        var wrongKey = refused with { Key = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([9])) };
        var firstCopy = RawEntry(fresh.Key, fresh.Bytes.Span, seq);
        Append(path, firstCopy);                                                  // a whole, uncommitted entry
        Append(path, RawEntry(committed.Key, committed.Bytes.Span, seq + 1));      // a later copy of a committed key
        Append(path, RawEntry(fresh.Key, fresh.Bytes.Span, seq + 2));              // a later copy of a tail key
        Append(path, RawEntry(gone.Key, gone.Bytes.Span, seq + 3));
        Append(path, RawEntry(gone.Key, [], seq + 4, tombstone: true));            // added and deleted inside the tail
        Append(path, RawEntry(gone.Key, [], seq + 5, tombstone: true));            // the same delete again
        Append(path, RawEntry(wrongKey.Key, wrongKey.Bytes.Span, seq + 6));        // whole, but fails its key check
        var spoofed = Synthetic.Headerless(6, 700);
        Append(path, RawEntry(spoofed.Key, Synthetic.Headerless(77, 700).Bytes.Span, seq + 7));  // other bytes under its key: refused
        var damaged = Synthetic.Headerless(7, 800);
        var damagedEntry = RawEntry(damaged.Key, damaged.Bytes.Span, seq + 8); damagedEntry[PackFormat.EntryHeaderBytes + 10] ^= 1;
        Append(path, damagedEntry);                                                // sound header, damaged bytes, in the middle
        Append(path, RawEntry(spoofed.Key, spoofed.Bytes.Span, seq + 9));          // the right bytes for that key, later
        var torn = RawEntry(Synthetic.Headerless(5).Key, Synthetic.Headerless(5).Bytes.Span, seq + 10);
        Append(path, torn.AsSpan(0, torn.Length - 3));                             // torn
        await using (var reopened = await ObjectStore.OpenAsync(dir, Quick))
        {
            var problems = string.Join("; ", reopened.OpenReport.Problems);
            Assert.Equal(torn.Length - 3, reopened.OpenReport.TruncatedBytes);     // only the torn end: the damaged entry has sound ones after it
            Assert.Contains("fails its key check", problems, StringComparison.Ordinal);
            Assert.Contains("is damaged", problems, StringComparison.Ordinal);
            Assert.Equal(ReadState.Present, (await reopened.ReadAsync(fresh.Key)).State);
            Assert.Equal(committedLength, Row(dir, fresh.Key).offset);            // the first copy
            Assert.Equal(PackFormat.PackHeaderBytes, Row(dir, committed.Key).offset);
            Assert.False(await reopened.HasAsync(gone.Key), problems);
            Assert.False(await reopened.HasAsync(wrongKey.Key));
            Assert.False(await reopened.HasAsync(damaged.Key));
            Assert.Equal(spoofed.Bytes.ToArray(), (await reopened.ReadAsync(spoofed.Key)).Bytes);
            Assert.True(NextSeq(dir) > seq + 9);
        }
        // Scan rebuilds exactly what recovery decided.
        var before = Rows(dir);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.False(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));  // recovery, refusals and re-appends kept the totals exact
            await store.ScanAsync();
        }
        Assert.Equal(before, Rows(dir));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) Assert.False(store.OpenReport.IndexRebuilt);
    }

    [Fact]
    public async Task ScanChecksEveryKeyOfEntriesPastASealedPacksCommittedLength()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var options = Quick with { MaxPackBytes = 50_000 };
        var a = Synthetic.Headerless(1, 30_000); var b = Synthetic.Headerless(2, 30_000);
        await using (var store = await ObjectStore.OpenAsync(dir, options)) { await store.PutAsync([a]); await store.PutAsync([b]); }
        var sealedPack = PackPath(dir, Row(dir, a.Key).pack);
        Assert.NotEqual(Row(dir, a.Key).pack, Row(dir, b.Key).pack);
        var seq = NextSeq(dir);
        var good = Synthetic.Headerless(3, 1000);
        var wrongKey = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([9]));
        var otherSpace = Synthetic.Headerless(5, 700);
        var bigConfig = new byte[(16 << 20) + 1];
        var bigConfigKey = new ObjectKey(ObjectKeyType.Config, MD5.HashData(bigConfig));
        Append(sealedPack, RawEntry(good.Key, good.Bytes.Span, seq));
        Append(sealedPack, RawEntry(wrongKey, Synthetic.Headerless(4, 600).Bytes.Span, seq + 1));                     // fails its key check
        Append(sealedPack, RawEntry(new ObjectKey(ObjectKeyType.Encoded, otherSpace.Key.KeyBytes(), 1), otherSpace.Bytes.Span, seq + 2));  // namespace 1
        Append(sealedPack, RawEntry(bigConfigKey, bigConfig, seq + 3));                                                // over a config's limit
        Append(sealedPack, RawEntry(b.Key, [], seq + 4, tombstone: true));                                             // a synced delete
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Assert.Contains(store.OpenReport.Problems, p => p.Contains("past its committed length; left alone", StringComparison.Ordinal));
            Assert.False(await store.HasAsync(good.Key));                                  // the writer leaves them alone
            var report = await store.ScanAsync();
            var problems = string.Join("; ", report.Problems);
            Assert.Equal(3, report.Unverifiable);
            Assert.Equal(0, report.Corrupt);
            Assert.Equal(3, report.Problems.Count(p => p.Contains("fails its key check", StringComparison.Ordinal)));
            Assert.Contains("bytes past its committed length replayed with every check", problems, StringComparison.Ordinal);
            Assert.Equal(good.Bytes.ToArray(), (await store.ReadAsync(good.Key)).Bytes);  // sound and proven: indexed
            Assert.False(await store.HasAsync(wrongKey));
            Assert.False(await store.HasAsync(new ObjectKey(ObjectKeyType.Encoded, otherSpace.Key.KeyBytes(), 1)));
            Assert.False(await store.HasAsync(bigConfigKey));
            Assert.False(await store.HasAsync(b.Key));
            Assert.True(await store.HasAsync(a.Key));
        }
        var rows = Rows(dir);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Assert.False(store.OpenReport.IndexRebuilt);
            Assert.DoesNotContain(store.OpenReport.Problems, p => p.Contains("committed length", StringComparison.Ordinal));
            // The refused entries now sit inside the committed length: a second Scan still leaves them out.
            Assert.Equal(3, (await store.ScanAsync()).Unverifiable);
        }
        Assert.Equal(rows, Rows(dir));
        // With no index there is no committed length to go by: the rebuild at open makes the same decisions.
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Contains(store.OpenReport.Problems, p => p.Contains("fails its key check", StringComparison.Ordinal));
        }
        Assert.Equal(rows, Rows(dir));
    }

    [Fact]
    public async Task FullVerifyCountsARowPutWouldRefuseAsCorruptWhileQuickVerifyDoesNot()
    {
        // Rows a Scan before D372 could index: a namespace other than 0, a length over the type's limit. Their bytes are sound.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var options = Quick with { MaxPackBytes = 50_000 };
        var a = Synthetic.Headerless(1, 30_000); var b = Synthetic.Headerless(2, 30_000);
        await using (var store = await ObjectStore.OpenAsync(dir, options)) { await store.PutAsync([a]); await store.PutAsync([b]); }
        var pack = Row(dir, a.Key).pack;
        var path = PackPath(dir, pack);
        var seq = NextSeq(dir);
        var o = Synthetic.Headerless(3, 700);
        var otherSpace = new ObjectKey(ObjectKeyType.Encoded, o.Key.KeyBytes(), 1);
        var bigConfig = new byte[(16 << 20) + 1];
        var bigConfigKey = new ObjectKey(ObjectKeyType.Config, MD5.HashData(bigConfig));
        var rows = new List<(ObjectKey key, long offset, long length, byte[] sha)>();
        foreach (var (key, bytes, s) in new[] { (otherSpace, o.Bytes.ToArray(), seq), (bigConfigKey, bigConfig, seq + 1) })
        {
            rows.Add((key, new FileInfo(path).Length, bytes.Length, SHA256.HashData(bytes)));
            Append(path, RawEntry(key, bytes, s));
        }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Pooling=False"))
        {
            c.Open();
            foreach (var (key, offset, length, sha) in rows)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO objects(k, pack, offset, length, sha256) VALUES($k, $p, $o, $l, $s)";
                cmd.Parameters.AddWithValue("$k", key.ToIndexKey()); cmd.Parameters.AddWithValue("$p", pack); cmd.Parameters.AddWithValue("$o", offset);
                cmd.Parameters.AddWithValue("$l", length); cmd.Parameters.AddWithValue("$s", sha);
                cmd.ExecuteNonQuery();
            }
        }
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            var quick = await store.VerifyAsync();
            Assert.Equal(4, quick.Checked);
            Assert.Equal(0, quick.CorruptCount);
            var full = await store.VerifyAsync(full: true);
            Assert.Equal(2, full.CorruptCount);
            Assert.Equal(new[] { bigConfigKey, otherSpace }.Order(), full.Corrupt.Order());
        }
    }

    [Fact]
    public async Task ScanReplaysAPackTheIndexDoesNotListWithEveryCheck()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var a = Synthetic.Headerless(1, 1000);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([a]);
        var first = PackFiles(dir).Single();
        var storeId = File.ReadAllBytes(first).AsSpan(16, 16).ToArray();
        var unlisted = PackPath(dir, 9);
        var header = new byte[PackFormat.PackHeaderBytes];
        PackFormat.WritePackHeader(header, 9, storeId, 1_790_000_000_000, ObjectStoreKind.Cdn);
        File.WriteAllBytes(unlisted, header);
        var seq = NextSeq(dir);
        var good = Synthetic.Headerless(2, 2000);
        var wrongKey = new ObjectKey(ObjectKeyType.Encoded, MD5.HashData([8]));
        Append(unlisted, RawEntry(wrongKey, Synthetic.Headerless(3, 600).Bytes.Span, seq));
        Append(unlisted, RawEntry(good.Key, good.Bytes.Span, seq + 1));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.Contains(store.OpenReport.Problems, p => p.Contains("pack 9 is not listed", StringComparison.Ordinal));
            Assert.False(await store.HasAsync(good.Key));
            var report = await store.ScanAsync();
            Assert.Equal(2, report.Packs);
            Assert.Equal(1, report.Unverifiable);
            Assert.Contains(report.Problems, p => p.Contains("pack 9 had no row in the index; replayed with every check", StringComparison.Ordinal));
            Assert.Equal(good.Bytes.ToArray(), (await store.ReadAsync(good.Key)).Bytes);
            Assert.False(await store.HasAsync(wrongKey));
            Assert.True(await store.HasAsync(a.Key));
        }
        var rows = Rows(dir);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
            Assert.DoesNotContain(store.OpenReport.Problems, p => p.Contains("not listed", StringComparison.Ordinal));  // Scan listed it
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) Assert.True(store.OpenReport.IndexRebuilt);
        Assert.Equal(rows, Rows(dir));
    }

    [Fact]
    public async Task RecoveryRefusesATailEntryPutWouldRefuseBeforeItsTypeCheck()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var a = Synthetic.Headerless(1, 1000);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([a]);
        var o = Synthetic.Headerless(2, 900);
        var otherSpace = new ObjectKey(ObjectKeyType.Encoded, o.Key.KeyBytes(), 1);  // bytes match the key, but namespace 1 is refused by Put
        Append(PackFiles(dir).Single(), RawEntry(otherSpace, o.Bytes.Span, NextSeq(dir)));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.Contains(store.OpenReport.Problems, p => p.Contains("fails its key check (OBJECT_NAMESPACE_UNSUPPORTED)", StringComparison.Ordinal));
            Assert.False(await store.HasAsync(otherSpace));
        }
        var rows = Rows(dir);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.ScanAsync();
        Assert.Equal(rows, Rows(dir));
    }

    [Fact]
    public async Task ScanRebuildsAnIdenticalIndexAndKeepsLastReadTimes()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var options = Quick with { MaxPackBytes = 300_000 };
        var objects = Enumerable.Range(0, 60).Select(i => i % 3 == 0 ? Synthetic.Chunked(i, 2000, 30_000) : Synthetic.Headerless(i, 1000 + i * 300)).ToList();
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            await store.PutAsync(objects);
            await store.PutAsync([.. HistoryRecordFormat.Objects(Synthetic.Observation(1)), .. HistoryRecordFormat.Objects(Synthetic.Observation(2, "wowt", "eu"))]);
            await store.DeleteAsync(objects.Take(20).Select(o => o.Key).ToList());
            await store.PutAsync(objects.Take(5).ToList());              // re-put after delete
            var compacted = await store.CompactAsync(new CompactOptions(0.01, 10));
            Assert.True(compacted.PacksCompacted > 0);
            await store.ReadAsync(objects[30].Key);
        }
        var before = Rows(dir);
        Assert.NotNull(LastRead(dir, objects[30].Key));
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Assert.False(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));  // totals exact after deletes, re-puts and compaction
            var report = await store.ScanAsync();
            Assert.Equal(0, report.Corrupt);
            Assert.Equal(before, Rows(dir));
            Assert.Equal(ReadState.Present, (await store.ReadAsync(objects[2].Key)).State);    // re-put
            Assert.False(await store.HasAsync(objects[10].Key));                            // deleted: not brought back
        }
        Assert.NotNull(LastRead(dir, objects[30].Key));
        // Losing the index altogether: the next open rebuilds it from the packs.
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Equal(2, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
        }
        Assert.Equal(before, Rows(dir));
    }

    [Fact]
    public async Task CompactRewritesDeadSpaceKeepsEveryLiveObjectAndRefusesHistoryDeletes()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var options = Quick with { MaxPackBytes = 200_000 };
        var objects = Enumerable.Range(0, 40).Select(i => Synthetic.Headerless(i, 20_000)).ToList();
        await using var store = await ObjectStore.OpenAsync(dir, options);
        await store.PutAsync(objects);
        var history = HistoryRecordFormat.Objects(Synthetic.Observation(3));
        await store.PutAsync(history);
        var size = PackFiles(dir).Sum(p => new FileInfo(p).Length);
        var deleted = await store.DeleteAsync(objects.Where((_, i) => i % 2 == 0).Select(o => o.Key).Append(Synthetic.Config(5).Key).Append(history[0].Key).ToList());
        Assert.Equal(20, deleted.Count(d => d.Outcome == DeleteOutcome.Deleted));
        Assert.Equal(DeleteOutcome.Absent, deleted[^2].Outcome);
        Assert.Equal(DeleteOutcome.Refused, deleted[^1].Outcome);
        var report = await store.CompactAsync(new CompactOptions(0.2, 100));
        Assert.True(report.PacksCompacted >= 3);
        Assert.Equal(0, report.SkippedCorrupt);
        Assert.True(PackFiles(dir).Sum(p => new FileInfo(p).Length) < size * 0.75);
        for (var i = 0; i < objects.Count; i++)
        {
            var r = await store.ReadAsync(objects[i].Key);
            Assert.Equal(i % 2 == 0 ? ReadState.Absent : ReadState.Present, r.State);
            if (i % 2 == 1) Assert.Equal(objects[i].Bytes.ToArray(), r.Bytes);
        }
        Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
        var rows = Rows(dir);
        await store.ScanAsync();
        Assert.Equal(rows, Rows(dir));
        await using var again = await ObjectStore.OpenReadOnlyAsync(dir);  // passes the open-time totals check: compaction kept them exact
        Assert.Equal(20, (await again.VerifyAsync()).Checked - 2);
    }

    [Fact]
    public async Task ManyReadersAndOneWriterNeverSeeABadOrMissingCommittedObject()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 1 << 20 });
        await store.PutAsync([Synthetic.Config(0)]);
        await using var shared = await ObjectStore.OpenReadOnlyAsync(dir);
        var committed = new List<PutObject>();
        var gate = new object();
        using var stop = new CancellationTokenSource();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        long reads = 0;
        async Task Reader(IObjectReader source, int seed)
        {
            var rng = new Random(seed);
            while (!stop.IsCancellationRequested)
            {
                PutObject o;
                lock (gate) { if (committed.Count == 0) { o = Synthetic.Config(0); } else o = committed[rng.Next(committed.Count)]; }
                if (rng.Next(3) == 0)
                {
                    var at = rng.Next(o.Bytes.Length); var buffer = new byte[Math.Min(5000, o.Bytes.Length - at)];
                    var r = await source.ReadRangeAsync(o.Key, at, buffer);
                    if (r.State != ReadState.Present || !buffer.AsSpan().SequenceEqual(o.Bytes.Span.Slice(at, buffer.Length))) failures.Enqueue($"range {o.Key} {r.State} {r.Problem}");
                }
                else
                {
                    var r = await source.ReadAsync(o.Key);
                    if (r.State != ReadState.Present || !r.Bytes.AsSpan().SequenceEqual(o.Bytes.Span)) failures.Enqueue($"read {o.Key} {r.State} {r.Problem}");
                }
                Interlocked.Increment(ref reads);
            }
        }
        var readers = Enumerable.Range(0, 8).Select(i => Task.Run(() => Reader(i % 2 == 0 ? store : (IObjectReader)shared, i))).ToArray();
        for (var b = 0; b < 30; b++)
        {
            var batch = Enumerable.Range(0, 10).Select(i => Synthetic.Headerless(b * 10 + i + 1, 1000 + (b * 10 + i) * 97)).ToList();
            await store.PutAsync(batch);
            lock (gate) committed.AddRange(batch);
            if (b == 10) await store.CompactAsync(new CompactOptions(0, 100));
            if (b == 20) await store.ScanAsync();
        }
        while (Interlocked.Read(ref reads) < 2000) await Task.Delay(10);
        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.True(failures.IsEmpty, $"{failures.Count} bad reads: " + string.Join("; ", failures.Take(5)));
    }

    [Fact]
    public async Task AReadOnlySecondStoreServesReadsAndNeverChangesAByte()
    {
        using var t = new TempDirectory();
        var sharedDir = Path.Combine(t.Path, "shared");
        var mine = Synthetic.Headerless(1); var theirs = Synthetic.Headerless(2);
        await using (var writer = await ObjectStore.OpenAsync(sharedDir, Quick)) await writer.PutAsync([theirs, .. HistoryRecordFormat.Objects(Synthetic.Observation(1))]);
        var before = HashFolder(sharedDir);
        await using (var shared = await ObjectStore.OpenReadOnlyAsync(sharedDir))
        await using (var profile = await ObjectStore.OpenAsync(Path.Combine(t.Path, "profile"), Quick))
        {
            await profile.PutAsync([mine]);
            var layered = new LayeredObjectReader(profile, shared);
            var a = await layered.ReadAsync(mine.Key); var b = await layered.ReadAsync(theirs.Key);
            Assert.Equal((ReadState.Present, false), (a.State, a.FromSecondary));
            Assert.Equal((ReadState.Present, true), (b.State, b.FromSecondary));
            Assert.Equal(theirs.Bytes.ToArray(), b.Bytes);
            var range = new byte[10];
            Assert.True((await layered.ReadRangeAsync(theirs.Key, 5, range)).FromSecondary);
            Assert.Equal(ReadState.Absent, (await layered.ReadAsync(Synthetic.Headerless(3).Key)).State);
            Assert.True(await layered.HasAsync(theirs.Key));
            Assert.Single(await shared.ReadHistoryAsync(new HistoryQuery()));
            Assert.Equal(3, (await shared.VerifyAsync(full: true)).Checked);
            // Nothing that writes can even be called: the read-only store and the layered reader offer no writing interface.
            foreach (var type in new[] { typeof(ReadOnlyObjectStore), typeof(LayeredObjectReader) })
            foreach (var writing in new[] { typeof(IObjectWriter), typeof(IHistoryLog), typeof(IObjectStoreMaintenance) })
                Assert.False(writing.IsAssignableFrom(type), $"{type.Name} must not be an {writing.Name}");
            Assert.True(typeof(IObjectReader).IsAssignableFrom(typeof(ReadOnlyObjectStore)));
        }
        Assert.Equal(before, HashFolder(sharedDir));
        Assert.Equal(ObjectStoreCodes.Missing, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(Path.Combine(t.Path, "none")))).Code);
    }

    [Fact]
    public async Task OnlyOneWriterOpensAStore()
    {
        using var t = new TempDirectory();
        await using var first = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        Assert.Equal(ObjectStoreCodes.Busy, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(StoreDir(t), Quick))).Code);
    }

    [Theory]
    [InlineData("pack")]
    [InlineData("entry")]
    [InlineData("index")]
    public async Task ANewerFormatIsRefusedByNameAndLeftUntouched(string what)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await store.PutAsync([Synthetic.Headerless(1)]);
        var pack = PackFiles(dir).Single();
        switch (what)
        {
            case "pack":
                var header = File.ReadAllBytes(pack).AsSpan(0, 64).ToArray();
                header[4] = 2; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), PackFormat.Crc32C(header.AsSpan(0, 60)));
                Write(pack, 0, header);
                break;
            case "entry": Append(pack, RawEntry(Synthetic.Headerless(2).Key, Synthetic.Headerless(2).Bytes.Span, 99, version: 2)); break;
            case "index":
                using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Pooling=False"))
                { c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version=2"; cmd.ExecuteNonQuery(); }
                break;
        }
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Quick))).Code);
        if (what != "entry")
            Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(dir))).Code);
        Assert.Equal(before, HashFolder(dir));
    }

    [Fact]
    public async Task ARefusedOpenChangesNothingEvenWithCleanupWaiting()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var objects = Enumerable.Range(0, 12).Select(i => Synthetic.Headerless(i, 20_000)).ToList();
        var crash = Quick with { MaxPackBytes = 100_000, Fault = p => { if (p == StorePoint.CompactCopied) throw new SimulatedCrashException(p); } };
        var store = await ObjectStore.OpenAsync(dir, crash);
        await store.PutAsync(objects);
        await store.DeleteAsync(objects.Take(6).Select(o => o.Key).ToList());
        await Assert.ThrowsAsync<ObjectStoreException>(() => store.CompactAsync(new CompactOptions(0.1, 1)));  // leaves a compaction target behind
        var pack = PackFiles(dir)[^2];                                                                          // the active pack, below the target
        Append(pack, RawEntry(Synthetic.Headerless(99).Key, Synthetic.Headerless(99).Bytes.Span, 1_000_000, version: 2));
        var before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Quick))).Code);
        Assert.Equal(before, HashFolder(dir));
        // With the index gone, the rebuild is refused the same way and leaves no scratch index behind.
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        before = HashFolder(dir);
        Assert.Equal(ObjectStoreCodes.NewerFormat, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(dir, Quick))).Code);
        Assert.Equal(before, HashFolder(dir));
    }

    [Fact]
    public async Task APackFromAnotherStoreIsRefused()
    {
        using var t = new TempDirectory();
        var a = Path.Combine(t.Path, "a"); var b = Path.Combine(t.Path, "b");
        await using (var s = await ObjectStore.OpenAsync(a, Quick with { MaxPackBytes = 400 })) for (var i = 0; i < 3; i++) await s.PutAsync([Synthetic.Headerless(i)]);
        await using (var s = await ObjectStore.OpenAsync(b, Quick)) await s.PutAsync([Synthetic.Headerless(9)]);
        File.Copy(PackPath(a, 3), PackPath(b, 3));   // a well-formed pack, numbered to fit, from another store
        Assert.Equal(ObjectStoreCodes.ForeignPack, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenAsync(b, Quick))).Code);
    }

    [Fact]
    public async Task TheHistoryLogKeepsRawDatedRepliesPerProductAndRegionAndSurvivesEverything()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var observations = new[]
        {
            Synthetic.Observation(1, "wow", "us"), Synthetic.Observation(2, "wow", "eu"), Synthetic.Observation(3, "wow", "us", HistoryKind.Cdns),
            Synthetic.Observation(4, "wowt", "us"), Synthetic.Observation(5, "wow", "us"), Synthetic.Observation(8, "wow", "eu", HistoryKind.Bgdl),
        };
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            foreach (var o in observations) Assert.All(await store.PutAsync(HistoryRecordFormat.Objects(o)), r => Assert.Equal(PutOutcome.Stored, r.Outcome));
            // The same reply observed again later is a new record that shares the document.
            var again = HistoryRecordFormat.Objects(observations[0] with { ObservedAt = observations[0].ObservedAt.AddDays(1) });
            Assert.Equal([PutOutcome.AlreadyPresent, PutOutcome.Stored], (await store.PutAsync(again)).Select(r => r.Outcome));
            // The same observation twice is one entry.
            Assert.All(await store.PutAsync(HistoryRecordFormat.Objects(observations[1])), r => Assert.Equal(PutOutcome.AlreadyPresent, r.Outcome));
        }
        async Task Check(ObjectStore store)
        {
            var all = await store.ReadHistoryAsync(new HistoryQuery());
            Assert.Equal(7, all.Count);
            Assert.Single(all, e => e.Kind == HistoryKind.Bgdl);
            Assert.Equal(all.OrderBy(e => e.ObservedAt).Select(e => e.Record), all.Select(e => e.Record));
            var usVersions = await store.ReadHistoryAsync(new HistoryQuery("wow", "us", HistoryKind.Versions));
            Assert.Equal(3, usVersions.Count);
            var page1 = await store.ReadHistoryAsync(new HistoryQuery(Limit: 4));
            var page2 = await store.ReadHistoryAsync(new HistoryQuery(After: page1[^1], Limit: 4));
            Assert.Equal(all.Select(e => e.Record), page1.Concat(page2).Select(e => e.Record));
            var first = all[0];
            Assert.Equal(("wow", "us", HistoryKind.Versions, observations[0].Url), (first.Product, first.Region, first.Kind, first.Url));
            Assert.Equal(observations[0].ObservedAt, first.ObservedAt);
            Assert.Equal(observations[0].Reply.ToArray(), (await store.ReadAsync(first.Document)).Bytes);
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick)) await Check(store);
        // A crash right after a history commit, then an index loss: the log comes back from the packs alone.
        var crash = Quick with { Fault = p => { if (p == StorePoint.AfterIndexCommit) throw new SimulatedCrashException(p); } };
        var crashed = await ObjectStore.OpenAsync(dir, crash);
        await Assert.ThrowsAsync<ObjectStoreException>(() => crashed.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(6, "classic", "kr"))));
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.Single(await store.ReadHistoryAsync(new HistoryQuery("classic")));
        }
        Assert.Throws<ArgumentException>(() => HistoryRecordFormat.Objects(Synthetic.Observation(7, "Bad Product")));
    }

    [Fact]
    public async Task AHistoryCursorKeepsWhatIsStrictlyAfterItEvenWhenNoEntryEqualsIt()
    {
        using var t = new TempDirectory();
        await using var store = await ObjectStore.OpenAsync(StoreDir(t), Quick);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        // Two entries share a millisecond, so the record key breaks the tie.
        foreach (var o in new[] { Synthetic.Observation(1, at: at), Synthetic.Observation(2, "wowt", at: at), Synthetic.Observation(3, at: at.AddSeconds(1)) })
            await store.AppendHistoryAsync(o);
        var all = await store.ReadHistoryAsync(new HistoryQuery());
        Assert.Equal(3, all.Count);
        Assert.True(all[0].Record.CompareTo(all[1].Record) < 0);
        HistoryEntry Cursor(DateTimeOffset time, byte fill) => all[0] with { ObservedAt = time, Record = new ObjectKey(ObjectKeyType.HistoryRecord, Enumerable.Repeat(fill, 16).ToArray()) };
        async Task<ObjectKey[]> After(HistoryEntry cursor) => (await store.ReadHistoryAsync(new HistoryQuery(After: cursor))).Select(e => e.Record).ToArray();

        Assert.Equal(all.Skip(1).Select(e => e.Record), await After(all[0]));
        Assert.Equal(all.Select(e => e.Record), await After(Cursor(at.AddMilliseconds(-1), 0xff)));
        Assert.Equal(all.Select(e => e.Record), await After(Cursor(at, 0x00)));
        Assert.Equal([all[2].Record], await After(Cursor(at, 0xff)));
        Assert.Equal([all[2].Record], await After(Cursor(at.AddMilliseconds(500), 0x00)));
        Assert.Empty(await After(Cursor(at.AddSeconds(1), 0xff)));
    }

    [Fact]
    public async Task ASummaryHasNoProductAndSurvivesARebuildFromThePacks()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var summary = Synthetic.Observation(1, "", "us", HistoryKind.Summary) with { Url = "https://us.version.battle.net/v2/summary" };
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.AppendHistoryAsync(summary);
            await store.AppendHistoryAsync(Synthetic.Observation(2));
        }
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            var entry = Assert.Single(await store.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Summary)));
            Assert.Equal(("", "us", summary.Url), (entry.Product, entry.Region, entry.Url));
            Assert.Single(await store.ReadHistoryAsync(new HistoryQuery(Product: "")));
            Assert.Equal(2, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
        }
        // Only a summary has no product, and a summary never has one.
        Assert.Throws<ArgumentException>(() => HistoryRecordFormat.Objects(Synthetic.Observation(3, "")));
        Assert.Throws<ArgumentException>(() => HistoryRecordFormat.Objects(Synthetic.Observation(3, "wow", "us", HistoryKind.Summary)));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryRecordFormat.Objects(Synthetic.Observation(3, "wow", "us", (HistoryKind)9)));
    }

    [Fact]
    public async Task ADefinitionKeepsItsTableIdRegionAndCommitUrlAndSurvivesARebuildFromThePacks()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        const string url = "https://raw.githubusercontent.com/wowdev/WoWDBDefs/1111111111111111111111111111111111111111/definitions/Item.dbd";
        var definition = Synthetic.Observation(1, "50238ec2", "wowdbdefs", HistoryKind.Definition) with { Url = url };
        var objects = HistoryRecordFormat.Objects(definition);
        Assert.True(HistoryRecordFormat.TryParse(objects[1].Bytes.Span, out var parsed));
        Assert.Equal((HistoryKind.Definition, "50238ec2", "wowdbdefs", url, definition.ObservedAt.ToUnixTimeMilliseconds(), objects[0].Key),
            (parsed.Kind, parsed.Product, parsed.Region, parsed.Url, parsed.ObservedMs, parsed.Document));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.AppendHistoryAsync(definition);
            await store.AppendHistoryAsync(Synthetic.Observation(2));
        }
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            var entry = Assert.Single(await store.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.Definition)));
            Assert.Equal(("50238ec2", "wowdbdefs", url, definition.ObservedAt), (entry.Product, entry.Region, entry.Url, entry.ObservedAt));
            Assert.Equal(definition.Reply.ToArray(), (await store.ReadAsync(entry.Document)).Bytes);
        }
        // A definition always names its table.
        Assert.Throws<ArgumentException>(() => HistoryRecordFormat.Objects(Synthetic.Observation(3, "", "wowdbdefs", HistoryKind.Definition)));
    }

    [Fact]
    public async Task AListfileReleaseAndItsPartsKeepTheirTagRegionAndUrlAndSurviveARebuildFromThePacks()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        const string tag = "202611010042";
        const string releaseUrl = "https://api.github.com/repos/wowdev/wow-listfile/releases/latest";
        const string partUrl = $"https://github.com/wowdev/wow-listfile/releases/download/{tag}/community-listfile-withcapitals.csv";
        var release = new HistoryObservation(HistoryKind.ListfileRelease, "wowlistfile", "latest", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            releaseUrl, "{\"tag_name\":\"202611010042\"}"u8.ToArray());
        // A full 8 MiB part fits one document; the last part is shorter.
        var full = new byte[8 * 1024 * 1024];
        for (var i = 0; i < full.Length; i++) full[i] = (byte)(i * 31 + 7);
        var part0 = new HistoryObservation(HistoryKind.ListfilePart, tag, "p0", release.ObservedAt.AddSeconds(1), partUrl, full);
        var part63 = new HistoryObservation(HistoryKind.ListfilePart, tag, "p63", release.ObservedAt.AddSeconds(2), partUrl, "1;a.blp\n"u8.ToArray());
        foreach (var o in new[] { release, part0, part63 })
        {
            var objects = HistoryRecordFormat.Objects(o);
            Assert.True(HistoryRecordFormat.TryParse(objects[1].Bytes.Span, out var parsed));
            Assert.Equal((o.Kind, o.Product, o.Region, o.Url, o.ObservedAt.ToUnixTimeMilliseconds(), objects[0].Key),
                (parsed.Kind, parsed.Product, parsed.Region, parsed.Url, parsed.ObservedMs, parsed.Document));
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.AppendHistoryAsync(release);
            await store.AppendHistoryAsync(part0);
            await store.AppendHistoryAsync(part63);
            await store.AppendHistoryAsync(Synthetic.Observation(2));
        }
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            var r = Assert.Single(await store.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.ListfileRelease)));
            Assert.Equal(("wowlistfile", "latest", releaseUrl, release.ObservedAt), (r.Product, r.Region, r.Url, r.ObservedAt));
            Assert.Equal(release.Reply.ToArray(), (await store.ReadAsync(r.Document)).Bytes);
            var parts = await store.ReadHistoryAsync(new HistoryQuery(Product: tag, Kind: HistoryKind.ListfilePart));
            Assert.Equal(["p0", "p63"], parts.Select(p => p.Region).Order(StringComparer.Ordinal));
            var p0 = parts.Single(p => p.Region == "p0");
            Assert.Equal((partUrl, part0.ObservedAt), (p0.Url, p0.ObservedAt));
            Assert.Equal(full, (await store.ReadAsync(p0.Document)).Bytes);
        }
    }

    [Fact]
    public async Task AListfileDeltaKeepsItsTagRegionAndUrlAndSurvivesARebuildFromThePacks()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        const string tag = "202611010042";
        const string url = $"https://github.com/wowdev/wow-listfile/releases/download/{tag}/community-listfile-withcapitals.csv";
        var delta = new HistoryObservation(HistoryKind.ListfileDelta, tag, "delta", new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            url, "an opaque delta document"u8.ToArray());
        var objects = HistoryRecordFormat.Objects(delta);
        Assert.Equal(8, objects[1].Bytes.Span[1]);
        Assert.True(HistoryRecordFormat.TryParse(objects[1].Bytes.Span, out var parsed));
        Assert.Equal((HistoryKind.ListfileDelta, tag, "delta", url, delta.ObservedAt.ToUnixTimeMilliseconds(), objects[0].Key),
            (parsed.Kind, parsed.Product, parsed.Region, parsed.Url, parsed.ObservedMs, parsed.Document));
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.AppendHistoryAsync(delta);
            await store.AppendHistoryAsync(Synthetic.Observation(2));
        }
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            var entry = Assert.Single(await store.ReadHistoryAsync(new HistoryQuery(Kind: HistoryKind.ListfileDelta)));
            Assert.Equal((tag, "delta", url, delta.ObservedAt), (entry.Product, entry.Region, entry.Url, entry.ObservedAt));
            Assert.Equal(delta.Reply.ToArray(), (await store.ReadAsync(entry.Document)).Bytes);
        }
        // A delta always names its release.
        Assert.Throws<ArgumentException>(() => HistoryRecordFormat.Objects(delta with { Product = "" }));
    }

    [Theory]
    [InlineData(1, 0)]   // versions with no product
    [InlineData(6, 0)]   // a listfile release with no product
    [InlineData(7, 0)]   // a listfile part with no tag
    [InlineData(4, 3)]   // a summary with a product
    [InlineData(8, 0)]   // a listfile delta with no tag
    [InlineData(9, 3)]   // a kind past listfile delta
    [InlineData(0, 3)]
    public void TheRecordGrammarTiesAnEmptyProductToTheSummary(byte kind, int productLength)
    {
        var product = new string('w', productLength);
        var region = "us"u8.ToArray();
        var b = new byte[28 + 1 + productLength + 1 + region.Length + 2];
        b[0] = 1; b[1] = kind; b[28] = (byte)productLength;
        System.Text.Encoding.ASCII.GetBytes(product, b.AsSpan(29));
        b[29 + productLength] = (byte)region.Length; region.CopyTo(b, 30 + productLength);
        Assert.False(HistoryRecordFormat.TryParse(b, out _));
        // The same bytes with the matching kind parse.
        b[1] = productLength == 0 ? (byte)4 : (byte)1;
        Assert.True(HistoryRecordFormat.TryParse(b, out var parsed));
        Assert.Equal(product, parsed.Product);
    }

    [Fact]
    public async Task ScanAndRebuildFindNoDanglingHistoryWhenEveryDocumentIsStored()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        // Four records sharing two documents (#153).
        var a = Synthetic.Observation(1, "wow", "us"); var b = Synthetic.Observation(2, "wow", "eu");
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            foreach (var o in new[] { a, a with { ObservedAt = a.ObservedAt.AddDays(1) }, b, b with { ObservedAt = b.ObservedAt.AddDays(1) } })
                await store.AppendHistoryAsync(o);
            Assert.Equal(4, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
            var report = await store.ScanAsync();
            Assert.Empty(report.Problems);
        }
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.True(store.OpenReport.IndexRebuilt);
            Assert.DoesNotContain(store.OpenReport.Problems, p => p.Contains("history", StringComparison.Ordinal));
            Assert.Equal(4, (await store.ReadHistoryAsync(new HistoryQuery())).Count);
        }
    }

    [Fact]
    public async Task ReadsRecordTheirDayInTheIndex()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var o = Synthetic.Headerless(1);
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            await store.PutAsync([o]);
            Assert.Null(LastRead(dir, o.Key));
            await store.ReadAsync(o.Key);
        }
        Assert.Equal(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400, LastRead(dir, o.Key));
    }
}
