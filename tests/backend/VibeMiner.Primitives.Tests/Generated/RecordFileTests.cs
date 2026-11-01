using System.Buffers.Binary;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using Xunit;
using Record = VibeMiner.Generated.RecordFiles.Record;

namespace VibeMiner.Primitives.Tests.Generated;

/// <summary>Record files (#179) over the in-memory generated store. Synthetic bytes only.</summary>
public sealed class RecordFileTests
{
    private static readonly GeneratedKey ManifestKey = GeneratedKey.Hash("record-file-test"u8);
    private static readonly byte[] Meta = "{\"table\":\"synthetic\"}"u8.ToArray();

    private static byte[] Payload(uint id, int length)
    {
        var bytes = new byte[length];
        var x = id * 2654435761u + 1;
        for (var i = 0; i < length; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; bytes[i] = (byte)(i % 7 == 0 ? x : (uint)(i % 31)); }
        return bytes;
    }

    private static async Task<RecordFileInfo> WriteAsync(IGeneratedStore store, IEnumerable<(uint Id, byte[] Bytes)> records, RecordFileOptions? options = null, byte[]? metadata = null)
    {
        using var writer = new RecordFileWriter(store, options);
        foreach (var (id, bytes) in records) await writer.AddAsync(id, bytes);
        return await writer.CompleteAsync(ManifestKey, metadata ?? Meta);
    }

    private static async Task<RecordFile> OpenAsync(IGeneratedStore store)
    {
        var open = await RecordFile.OpenAsync(store, ManifestKey);
        Assert.Equal(GeneratedReadState.Present, open.State);
        return open.File!;
    }

    private static async Task<List<Record>> AllAsync(RecordFile file, CancellationToken ct = default)
    {
        var list = new List<Record>();
        await foreach (var r in file.ReadAllAsync(ct)) list.Add(r);
        return list;
    }

    private static void AssertSame(IReadOnlyList<(uint Id, byte[] Bytes)> expected, IReadOnlyList<Record> actual, long firstOrdinal = 0)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(firstOrdinal + i, actual[i].Ordinal);
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.True(expected[i].Bytes.AsSpan().SequenceEqual(actual[i].Bytes.Span), $"record {firstOrdinal + i} differs");
        }
    }

    private static List<(uint Id, byte[] Bytes)> Records(int count, Func<int, uint>? id = null, Func<int, int>? length = null) =>
        [.. Enumerable.Range(0, count).Select(i => { var k = id?.Invoke(i) ?? (uint)i + 1; return (k, Payload(k, length?.Invoke(i) ?? 20 + i % 50)); })];

    /// <summary>Puts other bytes under a key, as silent damage the store's own checks missed would look.</summary>
    private static async Task Replace(InMemoryGeneratedStore store, GeneratedKey key, byte[] bytes)
    {
        await store.DeleteAsync([key]);
        await store.PutAsync([new GeneratedPut(key, bytes)]);
    }

    private static async Task<byte[]> BytesOf(IGeneratedStore store, GeneratedKey key) => (await store.ReadAsync(key)).Bytes!;

    [Fact]
    public async Task EmptyFileRoundTrips()
    {
        var store = new InMemoryGeneratedStore();
        var info = await WriteAsync(store, []);
        Assert.Equal((0, 0, 0, 1), (info.Count, info.Blocks, info.Segments, store.Count));
        var file = await OpenAsync(store);
        Assert.Equal(0, file.Count);
        Assert.Equal(Meta, file.Metadata.ToArray());
        Assert.Empty(await file.ReadAsync(0, 50));
        Assert.Null(await file.FindAsync(1));
        Assert.Empty(await AllAsync(file));
        Assert.Equal([ManifestKey], file.Keys);
    }

    [Fact]
    public async Task EmptyMetadataAndEmptyRecordRoundTrip()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, [(7u, Array.Empty<byte>())], metadata: []);
        var file = await OpenAsync(store);
        Assert.True(file.Metadata.IsEmpty);
        var r = Assert.Single(await AllAsync(file));
        Assert.Equal((0L, 7u, 0), (r.Ordinal, r.Id, r.Bytes.Length));
    }

    [Fact]
    public async Task OneRecordRoundTrips()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(1, _ => 42);
        var info = await WriteAsync(store, records);
        Assert.Equal((1, 1, 1, 1), (info.Count, info.Blocks, info.IdChunks, info.Segments));
        var file = await OpenAsync(store);
        AssertSame(records, await file.ReadAsync(0, 10));
        AssertSame(records, await AllAsync(file));
        Assert.Equal(42u, (await file.FindAsync(42))!.Value.Id);
        Assert.Equal(2, file.Keys.Count);
    }

    [Fact]
    public async Task RecordsOverABlockRoundTripInTheirOwnBlocks()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(5, length: i => i % 2 == 0 ? 10 : (16 << 10) + 1000 * i);
        var info = await WriteAsync(store, records);
        var file = await OpenAsync(store);
        AssertSame(records, await AllAsync(file));
        AssertSame(records[1..4], await file.ReadAsync(1, 3), 1);
        Assert.Equal([0L, 1, 2, 3, 4], file.BlockFirstOrdinals);
        Assert.Equal(5, info.Blocks);
    }

    [Fact]
    public async Task ManySmallRecordsRoundTripAcrossSegments()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(300_000, length: i => 4 + i % 29);
        var info = await WriteAsync(store, records, new RecordFileOptions { SegmentBytes = 64 << 10 });
        Assert.True(info.Segments > 5, $"{info.Segments} segments");
        Assert.Equal(info.Segments + 1, store.Count);
        var file = await OpenAsync(store);
        Assert.Equal(300_000, file.Count);
        AssertSame(records, await AllAsync(file));
        Assert.Equal(info.Segments + 1, file.Keys.Count);
        AssertSame(records[123_456..123_506], await file.ReadAsync(123_456, 50), 123_456);
        Assert.Equal(records[299_999].Id, (await file.FindAsync(records[299_999].Id))!.Value.Id);

        // Sequential ids: the index costs far under 8 B per entry.
        var idPerRecord = (double)info.IdIndexBytes / info.Count;
        Assert.True(idPerRecord < 0.5, $"{idPerRecord:F3} B of id index per record");
        Assert.True(info.CompressedBytes < info.UncompressedBytes / 2, $"{info.CompressedBytes} of {info.UncompressedBytes}");
    }

    [Fact]
    public async Task RandomIdsCompressUnderEightBytes()
    {
        var store = new InMemoryGeneratedStore();
        var rng = new Random(5);
        var ids = new HashSet<uint>();
        while (ids.Count < 50_000) ids.Add((uint)rng.Next(1, 2_000_000));
        var records = ids.Select(id => (id, Payload(id, 8))).ToList();
        var info = await WriteAsync(store, records);
        Assert.True((double)info.IdIndexBytes / info.Count < 6, $"{(double)info.IdIndexBytes / info.Count:F2} B per entry");
        var file = await OpenAsync(store);
        foreach (var (id, bytes) in records.Where((_, i) => i % 997 == 0))
            Assert.Equal(bytes, (await file.FindAsync(id))!.Value.Bytes.ToArray());
    }

    [Fact]
    public async Task PagesAtEveryBlockEdge()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(2000, length: i => 30 + i % 90);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048 });
        var file = await OpenAsync(store);
        var firsts = file.BlockFirstOrdinals;
        Assert.True(firsts.Count > 20);
        foreach (var edge in firsts.Skip(1))
            foreach (var start in new[] { edge - 2, edge - 1, edge, edge + 1 })
                foreach (var size in new[] { 1, 2, 3, 50 })
                {
                    var n = (int)Math.Min(size, records.Count - start);
                    AssertSame(records.GetRange((int)start, n), await file.ReadAsync(start, size), start);
                }
        AssertSame(records[^3..], await file.ReadAsync(records.Count - 3, 50), records.Count - 3);
        Assert.Empty(await file.ReadAsync(records.Count, 50));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => file.ReadAsync(records.Count + 1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => file.ReadAsync(-1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => file.ReadAsync(0, -1));
    }

    [Fact]
    public async Task PageReadsTouchOnlyTheBlocksTheyNeed()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(2000, length: _ => 100);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048 });
        var file = await OpenAsync(store);
        var firsts = file.BlockFirstOrdinals;
        var before = store.RangeReads;
        await file.ReadAsync(firsts[3], 5);
        Assert.Equal(before + 1, store.RangeReads);
        await file.ReadAsync(firsts[4] - 1, 2);
        Assert.Equal(before + 3, store.RangeReads);
        await file.FindAsync(records[777].Id);
        Assert.Equal(before + 5, store.RangeReads); // one id chunk, one block
        Assert.Equal(1, store.Reads); // the manifest, once at open
    }

    private static async Task<List<Record>> FromAsync(RecordFile file, long ordinal, CancellationToken ct = default)
    {
        var list = new List<Record>();
        await foreach (var r in file.ReadFromAsync(ordinal, ct)) list.Add(r);
        return list;
    }

    [Fact]
    public async Task ReadFromStreamsFromAnyOrdinalAcrossBlocksAndSegments()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(3000, length: i => 30 + i % 90);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 8 << 10 });
        var file = await OpenAsync(store);
        var firsts = file.BlockFirstOrdinals;
        foreach (var from in new long[] { 0, 1, firsts[1] - 1, firsts[1], firsts[1] + 1, firsts[^1], 1500, records.Count - 1 })
            AssertSame(records[(int)from..], await FromAsync(file, from), from);
        Assert.Empty(await FromAsync(file, records.Count));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FromAsync(file, records.Count + 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => FromAsync(file, -1));
    }

    [Fact]
    public async Task ReadFromFetchesBackToBackBlocksTogether()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(4000, length: _ => 100);
        var info = await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048, SegmentBytes = 64 << 10 });
        var file = await OpenAsync(store);
        var before = store.RangeReads;
        AssertSame(records, await FromAsync(file, 0));
        var reads = store.RangeReads - before;
        // Each segment's blocks fit in one run (segments are under RunBytes), so one range read per segment, not per block.
        Assert.True(reads <= info.Segments, $"{reads} range reads for {info.Segments} segments");
        Assert.True(info.Blocks > 4 * reads, $"{info.Blocks} blocks in {reads} reads");
    }

    [Fact]
    public async Task ReadFromCapsARunAtRunBytes()
    {
        var store = new InMemoryGeneratedStore();
        // Incompressible records, one segment: the walk splits into runs of at most RunBytes.
        var random = new Random(5);
        var records = Enumerable.Range(1, 600).Select(i => { var b = new byte[2000]; random.NextBytes(b); return ((uint)i, b); }).ToList();
        var info = await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 4096, SegmentBytes = 32 << 20 });
        Assert.Equal(1, info.Segments);
        var file = await OpenAsync(store);
        var before = store.RangeReads;
        AssertSame(records, await FromAsync(file, 0));
        var reads = store.RangeReads - before;
        Assert.True(reads >= info.CompressedBytes / RecordFile.RunBytes && reads < info.Blocks, $"{reads} reads, {info.Blocks} blocks");
    }

    [Fact]
    public async Task ReadFromOverADamagedSegmentThrowsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(2000, length: _ => 100);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048, SegmentBytes = 16 << 10 });
        var file = await OpenAsync(store);
        foreach (var key in file.Keys.Skip(1)) store.Damage(key);
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => FromAsync(file, 1000));
        Assert.Equal(GeneratedStoreCodes.Corrupt, e.Code);
    }

    [Fact]
    public async Task ReadFromStopsOnCancellation()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(2000, length: _ => 100);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048 });
        var file = await OpenAsync(store);
        using var cts = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in file.ReadFromAsync(0, cts.Token)) if (++seen == 10) cts.Cancel();
        });
        Assert.Equal(10, seen);
    }

    [Fact]
    public async Task FindsPresentAndAbsentIdsWithUnsortedInput()
    {
        var store = new InMemoryGeneratedStore();
        // Shuffled, gappy ids across several id chunks.
        var rng = new Random(11);
        var records = Records(5000, i => (uint)(i * 3 + 10)).OrderBy(_ => rng.Next()).ToList();
        records.Add((0u, Payload(0, 5)));
        records.Add((uint.MaxValue, Payload(uint.MaxValue, 5)));
        await WriteAsync(store, records);
        var file = await OpenAsync(store);
        AssertSame(records, await AllAsync(file));
        for (var i = 0; i < records.Count; i += 37)
        {
            var found = await file.FindAsync(records[i].Id);
            Assert.NotNull(found);
            Assert.Equal(i, found.Value.Ordinal);
            Assert.Equal(records[i].Bytes, found.Value.Bytes.ToArray());
        }
        foreach (var id in new uint[] { 0, uint.MaxValue, 10, 5000 * 3 + 7 })
            Assert.Equal(id, (await file.FindAsync(id))!.Value.Id);
        foreach (var id in new uint[] { 1, 11, 12, 5000 * 3 + 10, uint.MaxValue - 1 })
            Assert.Null(await file.FindAsync(id));
    }

    [Fact]
    public async Task FindBelowTheFirstIdIsNull()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(10, i => (uint)(100 + i)));
        var file = await OpenAsync(store);
        Assert.Null(await file.FindAsync(99));
        Assert.Null(await file.FindAsync(110));
        Assert.Equal(100u, (await file.FindAsync(100))!.Value.Id);
        Assert.Equal(109u, (await file.FindAsync(109))!.Value.Id);
    }

    [Fact]
    public async Task FindAtOrAfterHitsGapsAndEnds()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(10, i => (uint)(100 + i * 10)));
        var file = await OpenAsync(store);
        Assert.Equal((3L, 130u), Key(await file.FindAtOrAfterAsync(130)));
        Assert.Equal((4L, 140u), Key(await file.FindAtOrAfterAsync(131)));
        Assert.Equal((4L, 140u), Key(await file.FindAtOrAfterAsync(139)));
        Assert.Equal((0L, 100u), Key(await file.FindAtOrAfterAsync(0)));
        Assert.Equal((0L, 100u), Key(await file.FindAtOrAfterAsync(99)));
        Assert.Equal((9L, 190u), Key(await file.FindAtOrAfterAsync(190)));
        Assert.Null(await file.FindAtOrAfterAsync(191));
        Assert.Null(await file.FindAtOrAfterAsync(uint.MaxValue));
        var hit = (await file.FindAtOrAfterAsync(125))!.Value;
        Assert.Equal(Payload(130, 23), hit.Bytes.ToArray());
    }

    [Fact]
    public async Task FindAtOrAfterOnAnEmptyFileIsNull()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, []);
        var file = await OpenAsync(store);
        Assert.Null(await file.FindAtOrAfterAsync(0));
        Assert.Null(await file.FindAtOrAfterAsync(uint.MaxValue));
    }

    [Fact]
    public async Task FindAtOrAfterCrossesIdChunksWithinBoundedReads()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(3000, i => (uint)(i * 10 + 10), _ => 40);
        var info = await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048 });
        Assert.True(info.IdChunks >= 3, $"{info.IdChunks} id chunks");
        var file = await OpenAsync(store);
        // Chunk 0 ends at ordinal 1023 (id 10240); one past it is the next chunk's first entry.
        var lastOfChunk = records[RecordFileFormat.IdChunkEntries - 1].Id;
        var before = store.RangeReads;
        Assert.Equal((1024L, lastOfChunk + 10), Key(await file.FindAtOrAfterAsync(lastOfChunk + 1)));
        Assert.Equal(before + 3, store.RangeReads); // two id chunks, one block
        Assert.Equal((1023L, lastOfChunk), Key(await file.FindAtOrAfterAsync(lastOfChunk)));
        Assert.Equal((2048L, 20490u), Key(await file.FindAtOrAfterAsync(20481)));
        Assert.Equal((2999L, 30000u), Key(await file.FindAtOrAfterAsync(29991)));
        Assert.Null(await file.FindAtOrAfterAsync(30001));
        for (var i = 0; i < records.Count; i += 97)
            Assert.Equal(records[i].Bytes, (await file.FindAtOrAfterAsync(records[i].Id - 9))!.Value.Bytes.ToArray());
    }

    [Fact]
    public async Task FindAtOrAfterFollowsIdsNotOrdinals()
    {
        var store = new InMemoryGeneratedStore();
        var rng = new Random(23);
        var records = Records(2500, i => (uint)(i * 4 + 1)).OrderBy(_ => rng.Next()).ToList();
        await WriteAsync(store, records);
        var file = await OpenAsync(store);
        var ordinalOf = records.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => (long)x.i);
        foreach (var asked in new uint[] { 0, 1, 2, 4, 5, 4093, 4096, 4097, 5000, 9997 })
        {
            var expected = (asked + 2) / 4 * 4 + 1; // the smallest 4k + 1 at or after asked
            Assert.Equal((ordinalOf[expected], expected), Key(await file.FindAtOrAfterAsync(asked)));
        }
        Assert.Null(await file.FindAtOrAfterAsync(9998));
    }

    [Fact]
    public async Task FindAtOrAfterOverADamagedIdIndexThrowsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(500, length: _ => 40);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 4096 });
        var file = await OpenAsync(store);
        store.Damage(file.Keys[^1]); // the id index lives in the last segment
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => file.FindAtOrAfterAsync(records[0].Id));
        Assert.Equal(GeneratedStoreCodes.Corrupt, e.Code);
    }

    private static (long Ordinal, uint Id)? Key(Record? record) => record is { } r ? (r.Ordinal, r.Id) : null;

    [Fact]
    public async Task DuplicateIdIsRefusedAndTheWriterGoesOn()
    {
        var store = new InMemoryGeneratedStore();
        using var writer = new RecordFileWriter(store);
        await writer.AddAsync(5, new byte[3]);
        await writer.AddAsync(9, new byte[3]);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(9, new byte[1]).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(5, new byte[1]).AsTask()); // out of order
        await writer.AddAsync(7, new byte[3]);
        await Assert.ThrowsAsync<ArgumentException>(() => writer.AddAsync(7, new byte[1]).AsTask());
        var info = await writer.CompleteAsync(ManifestKey, Meta);
        Assert.Equal(3, info.Count);
        Assert.Equal([5u, 9u, 7u], (await AllAsync(await OpenAsync(store))).Select(r => r.Id));
    }

    [Fact]
    public async Task LimitsAreRefused()
    {
        var store = new InMemoryGeneratedStore();
        using var writer = new RecordFileWriter(store, new RecordFileOptions { MaxRecords = 2, MaxRecordBytes = 100, MaxMetadataBytes = 10 });
        var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => writer.AddAsync(1, new byte[101]).AsTask());
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        await writer.AddAsync(1, new byte[100]);
        await writer.AddAsync(2, new byte[1]);
        e = await Assert.ThrowsAsync<GeneratedStoreException>(() => writer.AddAsync(3, new byte[1]).AsTask());
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        e = await Assert.ThrowsAsync<GeneratedStoreException>(() => writer.CompleteAsync(ManifestKey, new byte[11]));
        Assert.Equal(GeneratedStoreCodes.Limit, e.Code);
        Assert.False(await store.HasAsync(ManifestKey));
        Assert.Equal(2, (await writer.CompleteAsync(ManifestKey, new byte[10])).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordFileWriter(store, new RecordFileOptions { BlockBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecordFileWriter(store, new RecordFileOptions { MaxRecordBytes = 17 << 20 }));
    }

    [Fact]
    public async Task AnExistingManifestWins()
    {
        var store = new InMemoryGeneratedStore();
        var first = Records(10);
        Assert.False((await WriteAsync(store, first)).ManifestWasPresent);
        var second = Records(20, i => (uint)(1000 + i));
        Assert.True((await WriteAsync(store, second)).ManifestWasPresent);
        AssertSame(first, await AllAsync(await OpenAsync(store)));
    }

    [Fact]
    public async Task DisposeDuringACallFinishesTheWriter()
    {
        var store = new InMemoryGeneratedStore();
        RecordFileWriter? writer = null;
        var hooked = new HookStore(store, () => writer!.Dispose());
        writer = new RecordFileWriter(hooked, new RecordFileOptions { BlockBytes = 64, SegmentBytes = 64 });
        // Each record closes a block; once a block no longer fits, the segment flushes and its Put disposes the writer mid-call.
        uint id = 0;
        while (hooked.Puts == 0) await writer.AddAsync(++id, Payload(id, 100));
        Assert.Equal(1, hooked.Puts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddAsync(3, new byte[1]).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(ManifestKey, Meta));
        writer.Dispose();
        Assert.False(await store.HasAsync(ManifestKey));
    }

    [Fact]
    public async Task FinishedWriterRefusesMoreCalls()
    {
        var store = new InMemoryGeneratedStore();
        using var writer = new RecordFileWriter(store);
        await writer.CompleteAsync(ManifestKey, Meta);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddAsync(1, new byte[1]).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(ManifestKey, Meta));
    }

    [Fact]
    public async Task AbandonedWriterLeavesNoManifest()
    {
        var store = new InMemoryGeneratedStore();
        using (var writer = new RecordFileWriter(store, new RecordFileOptions { SegmentBytes = 4096 }))
            foreach (var (id, bytes) in Records(3000)) await writer.AddAsync(id, bytes);
        Assert.True(store.Count > 0); // full segments were written
        Assert.False(await store.HasAsync(ManifestKey));
        Assert.Equal(GeneratedReadState.Absent, (await RecordFile.OpenAsync(store, ManifestKey)).State);
    }

    [Fact]
    public async Task FailingOrCanceledCompleteLeavesNoManifest()
    {
        // Complete puts the last segment, then the manifest: fail either.
        for (var failAt = 1; failAt <= 2; failAt++)
        {
            var inner = new InMemoryGeneratedStore();
            var store = new FailingStore(inner, failAt);
            using var writer = new RecordFileWriter(store, new RecordFileOptions { SegmentBytes = 16 << 10 });
            foreach (var (id, bytes) in Records(3000)) await writer.AddAsync(id, bytes);
            store.Armed = true;
            await Assert.ThrowsAsync<GeneratedStoreException>(() => writer.CompleteAsync(ManifestKey, Meta));
            Assert.False(await inner.HasAsync(ManifestKey));
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(ManifestKey, Meta));
        }

        var plain = new InMemoryGeneratedStore();
        using var cts = new CancellationTokenSource();
        var canceling = new CancelingStore(plain, cts);
        using var w = new RecordFileWriter(canceling);
        foreach (var (id, bytes) in Records(3000)) await w.AddAsync(id, bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w.CompleteAsync(ManifestKey, Meta, cts.Token));
        Assert.False(await plain.HasAsync(ManifestKey));
    }

    [Fact]
    public async Task RerunAfterAPartialWriteReusesSegments()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(3000);
        using (var writer = new RecordFileWriter(store, new RecordFileOptions { SegmentBytes = 4096 }))
            foreach (var (id, bytes) in records) await writer.AddAsync(id, bytes);
        await WriteAsync(store, records, new RecordFileOptions { SegmentBytes = 4096 });
        AssertSame(records, await AllAsync(await OpenAsync(store)));
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(100));
        var file = await OpenAsync(store);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecordFile.OpenAsync(store, ManifestKey, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.ReadAsync(0, 5, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.FindAsync(5, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AllAsync(file, cts.Token));
        using var writer = new RecordFileWriter(new InMemoryGeneratedStore());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.AddAsync(1, new byte[1], cts.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.CompleteAsync(ManifestKey, Meta, cts.Token));

        // Canceled in the middle of a read-all.
        using var mid = new CancellationTokenSource();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in file.ReadAllAsync(mid.Token)) if (++seen == 3) mid.Cancel();
        });
    }

    [Fact]
    public async Task ManifestDamageReadsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(10));
        store.Damage(ManifestKey);
        Assert.Equal(GeneratedReadState.Corrupt, (await RecordFile.OpenAsync(store, ManifestKey)).State);
    }

    [Fact]
    public async Task EveryTruncatedOrExtendedManifestReadsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(300, length: _ => 40), new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 4096 });
        var good = await BytesOf(store, ManifestKey);
        for (var length = 1; length < good.Length; length++)
        {
            await Replace(store, ManifestKey, good[..length]);
            Assert.Equal(GeneratedReadState.Corrupt, (await RecordFile.OpenAsync(store, ManifestKey)).State);
        }
        await Replace(store, ManifestKey, [.. good, 0]);
        Assert.Equal(GeneratedReadState.Corrupt, (await RecordFile.OpenAsync(store, ManifestKey)).State);
    }

    [Fact]
    public async Task EveryFlippedManifestByteReadsCorruptOrTheSame()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(300, length: _ => 40);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 4096 });
        var good = await BytesOf(store, ManifestKey);
        var corrupt = 0;
        for (var i = 0; i < good.Length; i++)
        {
            var bad = good.ToArray();
            bad[i] ^= 0x41;
            await Replace(store, ManifestKey, bad);
            var open = await RecordFile.OpenAsync(store, ManifestKey);
            if (open.State == GeneratedReadState.Corrupt) { corrupt++; continue; }
            Assert.Equal(GeneratedReadState.Present, open.State);
            Assert.Equal(Meta, open.File!.Metadata.ToArray());
            AssertSame(records, await AllAsync(open.File));
        }
        Assert.True(corrupt >= good.Length - 4, $"{corrupt} of {good.Length} flips read corrupt");
    }

    [Fact]
    public async Task DamagedMissingOrResizedSegmentsThrowCorrupt()
    {
        var records = Records(500, length: _ => 40);
        var options = new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 4096 };

        async Task<(InMemoryGeneratedStore Store, RecordFile File, GeneratedKey Segment)> Fresh()
        {
            var store = new InMemoryGeneratedStore();
            await WriteAsync(store, records, options);
            var file = await OpenAsync(store);
            return (store, file, file.Keys[1]);
        }

        async Task AssertCorrupt(RecordFile file)
        {
            var e = await Assert.ThrowsAsync<GeneratedStoreException>(() => AllAsync(file));
            Assert.Equal(GeneratedStoreCodes.Corrupt, e.Code);
        }

        var (s1, f1, k1) = await Fresh();
        s1.Damage(k1);
        await AssertCorrupt(f1);
        Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => f1.ReadAsync(0, 1))).Code);

        var (s2, f2, k2) = await Fresh();
        await s2.DeleteAsync([k2]);
        await AssertCorrupt(f2);

        var (s3, f3, k3) = await Fresh();
        var bytes = await BytesOf(s3, k3);
        await Replace(s3, k3, bytes[..^1]);
        await AssertCorrupt(f3);
        await Replace(s3, k3, [.. bytes, 0]);
        await AssertCorrupt(f3);

        // The id index lives in the last segment.
        var (s4, f4, _) = await Fresh();
        s4.Damage(f4.Keys[^1]);
        Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => f4.FindAsync(records[0].Id))).Code);
    }

    [Fact]
    public async Task EveryFlippedSegmentByteThrowsCorruptOrReadsTheSame()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(200, length: _ => 40);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 1024 });
        var file = await OpenAsync(store);
        Assert.Equal(2, file.Keys.Count); // one segment: blocks, then the id chunk
        var segment = file.Keys[1];
        var good = await BytesOf(store, segment);
        var caught = 0;
        // Each flip either throws Corrupt or (a zstd header bit the decoder ignores) reads back exactly the original bytes.
        for (var i = 0; i < good.Length; i++)
        {
            var bad = good.ToArray();
            bad[i] ^= 0x10;
            await Replace(store, segment, bad);
            var e = await Xunit.Record.ExceptionAsync(async () =>
            {
                AssertSame(records, await AllAsync(file));
                foreach (var (id, bytes) in records) Assert.Equal(bytes, (await file.FindAsync(id))!.Value.Bytes.ToArray());
            });
            if (e is null) continue;
            Assert.True(e is GeneratedStoreException { Code: GeneratedStoreCodes.Corrupt }, $"flip at {i} of {good.Length}: {e}");
            caught++;
        }
        Assert.True(caught >= good.Length - 32, $"{caught} of {good.Length} flips caught");
    }

    [Fact]
    public async Task InconsistentDirectoriesReadCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(3000, length: _ => 40);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 1024, SegmentBytes = 8192 });
        var good = RecordFileFormat.DecodeManifest(await BytesOf(store, ManifestKey));

        async Task<GeneratedStoreException?> With(Manifest manifest)
        {
            await Replace(store, ManifestKey, RecordFileFormat.EncodeManifest(manifest, 3));
            var open = await RecordFile.OpenAsync(store, ManifestKey);
            if (open.State == GeneratedReadState.Corrupt) return new GeneratedStoreException(GeneratedStoreCodes.Corrupt, open.Problem!);
            Assert.Equal(GeneratedReadState.Present, open.State);
            var e = await Assert.ThrowsAsync<GeneratedStoreException>(async () =>
            {
                await AllAsync(open.File!);
                foreach (var (id, _) in records) await open.File!.FindAsync(id);
            });
            return e;
        }

        BlockEntry[] Blocks(Func<BlockEntry, int, BlockEntry> f) => [.. good.Blocks.Select(f)];
        ChunkEntry[] Chunks(Func<ChunkEntry, int, ChunkEntry> f) => [.. good.Chunks.Select(f)];
        var cases = new List<Manifest>
        {
            good with { Count = good.Count + 1 },
            good with { Count = good.Count - 1 },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { Frame = b.Frame with { Length = b.Frame.Length + 1 } } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { Frame = b.Frame with { Length = b.Frame.Length - 1 } } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { Frame = b.Frame with { CompressedLength = b.Frame.CompressedLength - 1 } } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { Frame = b.Frame with { Offset = b.Frame.Offset + 1 } } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { FirstOrdinal = b.FirstOrdinal + 1 } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { FirstOrdinal = b.FirstOrdinal - 1 } : b) },
            good with { Blocks = Blocks((b, i) => i == 0 ? b with { FirstOrdinal = 1 } : b) },
            good with { Blocks = Blocks((b, i) => i == 3 ? b with { Frame = b.Frame with { Segment = 99 } } : b) },
            good with { Chunks = Chunks((c, i) => i == 1 ? c with { FirstId = c.FirstId + 1 } : c) },
            good with { Chunks = Chunks((c, i) => i == 1 ? c with { FirstId = c.FirstId - 1 } : c) },
            good with { Chunks = Chunks((c, i) => i == 1 ? c with { Entries = c.Entries - 1 } : c) },
            good with { Chunks = Chunks((c, i) => i == 1 ? c with { Frame = c.Frame with { Length = c.Frame.Length + 1 } } : c) },
            good with { Segments = [.. good.Segments.Select((s, i) => i == 0 ? s with { Length = s.Length + 1 } : s)] },
            good with { Segments = [.. good.Segments.Select((s, i) => i == 0 ? s with { Length = s.Length - 1 } : s)] },
            good with { Segments = [.. good.Segments.Select((s, i) => i == 0 ? good.Segments[1] : s)] },
        };
        for (var i = 0; i < cases.Count; i++)
        {
            var e = await With(cases[i]);
            Assert.True(e is { Code: GeneratedStoreCodes.Corrupt }, $"case {i}");
        }
        // The good manifest re-encoded still reads.
        await Replace(store, ManifestKey, RecordFileFormat.EncodeManifest(good, 3));
        AssertSame(records, await AllAsync(await OpenAsync(store)));
    }

    [Fact]
    public async Task WrongMagicOrVersionReadsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        await WriteAsync(store, Records(5));
        var good = await BytesOf(store, ManifestKey);
        foreach (var at in new[] { 0, 4, 6 })
        {
            var bad = good.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(at), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bad.AsSpan(at)) + 1));
            await Replace(store, ManifestKey, bad);
            Assert.Equal(GeneratedReadState.Corrupt, (await RecordFile.OpenAsync(store, ManifestKey)).State);
        }
    }

    [Fact]
    public async Task RecordsKeepTheirBytesAndHoldOnlyWhatWasAskedFor()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(3000, length: i => 40 + i % 60);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 8192 });
        var file = await OpenAsync(store);
        var page = await file.ReadAsync(1000, 3);
        var found = (await file.FindAsync(records[2000].Id))!.Value;
        // Later reads reuse the pooled block buffers; what callers already hold must not change.
        for (var i = 0; i < records.Count; i += 37) await file.ReadAsync(i, 50);
        AssertSame(records.GetRange(1000, 3), page, 1000);
        Assert.True(records[2000].Bytes.AsSpan().SequenceEqual(found.Bytes.Span));
        // A record's memory is a copy of the asked-for records only, not the whole block.
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(found.Bytes, out var segment));
        Assert.True(segment.Array!.Length < records[2000].Bytes.Length + 16, $"one record holds {segment.Array.Length} bytes");
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(page[0].Bytes, out segment));
        Assert.True(segment.Array!.Length < page.Sum(r => r.Bytes.Length) + 3 * 16);
    }

    [Fact]
    public async Task ConcurrentReadsAgree()
    {
        var store = new InMemoryGeneratedStore();
        var records = Records(5000);
        await WriteAsync(store, records, new RecordFileOptions { BlockBytes = 2048 });
        var file = await OpenAsync(store);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async t =>
        {
            for (var i = t; i < records.Count; i += 101)
            {
                AssertSame(records.GetRange(i, Math.Min(7, records.Count - i)), await file.ReadAsync(i, 7), i);
                Assert.Equal(i, (await file.FindAsync(records[i].Id))!.Value.Ordinal);
            }
        }));
    }

    /// <summary>Once armed, throws a store failure on the n-th Put.</summary>
    private sealed class FailingStore(IGeneratedStore inner, int failAt) : IGeneratedStore
    {
        private int puts;
        public bool Armed { get; set; }
        public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken ct = default) => inner.HasAsync(key, ct);
        public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken ct = default) => inner.ReadAsync(key, ct);
        public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken ct = default) => inner.ReadRangeAsync(key, offset, destination, ct);
        public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken ct = default) => inner.DeleteAsync(keys, ct);
        public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken ct = default) => inner.CompactAsync(minDeadFraction, maxPacks, ct);
        public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken ct = default) =>
            Armed && ++puts == failAt ? throw new GeneratedStoreException(GeneratedStoreCodes.WriteFailed, "Scripted failure.") : inner.PutAsync(batch, ct);
    }

    /// <summary>Runs a callback inside each Put, then passes the Put on.</summary>
    private sealed class HookStore(IGeneratedStore inner, Action hook) : IGeneratedStore
    {
        public int Puts { get; private set; }
        public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken ct = default) => inner.HasAsync(key, ct);
        public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken ct = default) => inner.ReadAsync(key, ct);
        public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken ct = default) => inner.ReadRangeAsync(key, offset, destination, ct);
        public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken ct = default) => inner.DeleteAsync(keys, ct);
        public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken ct = default) => inner.CompactAsync(minDeadFraction, maxPacks, ct);
        public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken ct = default)
        {
            Puts++;
            hook();
            return inner.PutAsync(batch, ct);
        }
    }

    /// <summary>Cancels its token on the first Put, then passes the Put on.</summary>
    private sealed class CancelingStore(IGeneratedStore inner, CancellationTokenSource cts) : IGeneratedStore
    {
        public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken ct = default) => inner.HasAsync(key, ct);
        public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken ct = default) => inner.ReadAsync(key, ct);
        public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken ct = default) => inner.ReadRangeAsync(key, offset, destination, ct);
        public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken ct = default) => inner.DeleteAsync(keys, ct);
        public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken ct = default) => inner.CompactAsync(minDeadFraction, maxPacks, ct);
        public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken ct = default)
        {
            cts.Cancel();
            return inner.PutAsync(batch, ct);
        }
    }
}
