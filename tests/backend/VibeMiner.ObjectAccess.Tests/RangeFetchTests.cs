using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>Range reads of big loose objects fetch only the proven header and the chunks under the range (D340).</summary>
public sealed class RangeFetchTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const int Chunk = 16 << 10;
    private static readonly ObjectSourceOptions Ranged = new() { RangeFetchMinBytes = 64 << 10 };

    /// <summary>Ten 16 KiB chunks: 160 KiB plus a 252-byte header, so the 64 KiB header probe is never the whole object.</summary>
    private static PutObject Big(int seed, int chunks = 10, int size = Chunk) => Synthetic.Chunked(seed, Enumerable.Repeat(size, chunks).ToArray());
    private static long Header(int chunks) => 12 + 24 * chunks;
    private static long ChunkStart(int chunks, int index, int size = Chunk) => Header(chunks) + (long)index * size;

    private static async Task<(ObjectResult Result, byte[] Bytes)> ReadAsync(IObjectSource source, CdnContext context, PutObject o, long offset, int length,
        CancellationToken? token = null)
    {
        var into = new byte[length];
        var r = await source.GetRangeAsync(new(o.Key, o.DeclaredLength), offset, into, context, FetchPriority.Interactive, token ?? None);
        return (r, into);
    }

    [Fact]
    public async Task ABigLooseObjectIsReadWithSingleRangesOfTheHeaderAndOneChunkAndNothingIsStored()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(1));
        var offset = ChunkStart(10, 5) + 100;
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 200);

        Assert.Equal((ObjectState.Present, ServedFrom.Cdn, ObjectOrigin.Cdn, o.DeclaredLength), (r.State, r.ServedFrom!.Value, r.Origin!.Value, r.Length!.Value));
        Assert.Equal(o.Bytes.Slice((int)offset, 200).ToArray(), bytes);
        Assert.Equal([(0L, (long)CdnFetcher.HeaderProbeBytes), (ChunkStart(10, 5), Chunk)], s.Cdn.Requests.Select(q => (q.Offset!.Value, q.Length!.Value)));
        Assert.Equal((0, 0), (s.Store.Count, s.Store.Puts));
        Assert.Equal((0, 0), (s.Source.Streams.InteractiveActive, s.Source.Streams.BackgroundActive));
    }

    [Fact]
    public async Task TheHeaderIsFetchedOnceAcrossReadsAndARangeOverTwoChunksIsOneRequest()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(2));
        await ReadAsync(s.Source, s.Context, o, ChunkStart(10, 1), 10);
        s.Cdn.ClearRequests();

        var offset = ChunkStart(10, 4) - 50;      // the last 50 bytes of chunk 3 and the first 50 of chunk 4
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 100);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice((int)offset, 100).ToArray(), bytes);
        var q = Assert.Single(s.Cdn.Requests);
        Assert.Equal((ChunkStart(10, 3), 2L * Chunk), (q.Offset!.Value, q.Length!.Value));
    }

    [Fact]
    public async Task ASpanOverTheRequestBoundIsSplitIntoSequentialSingleRanges()
    {
        var s = new Scenario { Options = Ranged with { InteractiveInFlightBytes = 40 << 10 } };
        var o = s.Serve(Big(3));
        var offset = ChunkStart(10, 2) + 1;
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 4 * Chunk);   // chunks 2 to 6
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice((int)offset, 4 * Chunk).ToArray(), bytes);
        Assert.Equal([(ChunkStart(10, 2), 2L * Chunk), (ChunkStart(10, 4), 2L * Chunk), (ChunkStart(10, 6), Chunk)],
            s.Cdn.Requests.Skip(1).Select(q => (q.Offset!.Value, q.Length!.Value)));
    }

    [Fact]
    public async Task AReadInsideTheHeaderIsServedFromTheProvenHeader()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(4));
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, 4, 40);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice(4, 40).ToArray(), bytes);
        Assert.Single(s.Cdn.Requests);

        // Across the end of the header: the header from memory, then the first chunk.
        s.Cdn.ClearRequests();
        (r, bytes) = await ReadAsync(s.Source, s.Context, o, Header(10) - 8, 16);
        Assert.Equal(o.Bytes.Slice((int)Header(10) - 8, 16).ToArray(), bytes);
        Assert.Equal([(Header(10), (long)Chunk)], s.Cdn.Requests.Select(q => (q.Offset!.Value, q.Length!.Value)));
    }

    [Fact]
    public async Task AHeaderLongerThanTheProbeIsFetchedWholeAsASecondRange()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(5, chunks: 3000, size: 30));
        Assert.True(Header(3000) > CdnFetcher.HeaderProbeBytes);
        var offset = ChunkStart(3000, 2000, 30) + 3;
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 20);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice((int)offset, 20).ToArray(), bytes);
        Assert.Equal([(0L, (long)CdnFetcher.HeaderProbeBytes), (0L, Header(3000)), (ChunkStart(3000, 2000, 30), 30L)],
            s.Cdn.Requests.Select(q => (q.Offset!.Value, q.Length!.Value)));
    }

    [Fact]
    public async Task ABadChunkMovesToTheNextHostAndIsRefusedWhenEveryHostIsBad()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(6));
        s.Cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, Scenario.KeyOf(o.Key));   // flips a byte in the middle: chunk 4
        var middle = (int)o.DeclaredLength / 2;
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, middle - 10, 20);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(o.Bytes.Slice(middle - 10, 20).ToArray(), bytes);
        Assert.Equal([Scenario.L3, Scenario.L3, Scenario.Us], s.Cdn.Requests.Select(q => q.Host));

        s.Cdn.CorruptOn(Scenario.Us, CdnObjectKind.Data, Scenario.KeyOf(o.Key));
        s.Cdn.ClearRequests();
        (r, _) = await ReadAsync(s.Source, s.Context, o, middle - 10, 20);
        Assert.Equal((ObjectState.Refused, ObjectRefusalCodes.BlteInvalid), (r.State, r.Code));
        Assert.Equal(2, s.Cdn.Requests.Count);
        Assert.All(s.Cdn.Requests, q => Assert.NotNull(q.Offset));
        Assert.Equal(0, s.Store.Count);
    }

    [Fact]
    public async Task AHeaderThatIsNotTheKeysIsRefusedOnEveryHostAndTheWholeObjectIsNeverAskedFor()
    {
        var s = new Scenario { Options = Ranged };
        var o = Big(7);
        var wrong = o.Bytes.ToArray(); wrong[30] ^= 1;     // a chunk MD5 in the table
        s.Cdn.Add(CdnObjectKind.Data, Scenario.KeyOf(o.Key), wrong);
        var (r, _) = await ReadAsync(s.Source, s.Context, o, ChunkStart(10, 1), 10);
        Assert.Equal((ObjectState.Refused, ObjectRefusalCodes.KeyMismatch), (r.State, r.Code));
        Assert.Equal([Scenario.L3, Scenario.Us], s.Cdn.Requests.Select(q => q.Host));
        Assert.All(s.Cdn.Requests, q => Assert.Equal((0L, (long)CdnFetcher.HeaderProbeBytes), (q.Offset!.Value, q.Length!.Value)));
        Assert.Equal(0, s.Store.Count);
    }

    [Fact]
    public async Task AProbeClaimingALongerHeaderThanTheRealOneTakesTheWholePath()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(15));
        var key = Scenario.KeyOf(o.Key);
        var lie = o.Bytes.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(lie.AsSpan(4), 100_000);   // past the probe, inside the object
        // The first host's probe passes its check but claims a header 100 000 bytes long; every later answer is the real object.
        var first = true;
        s.Cdn.BeforeAnswer = (q, _) =>
        {
            s.Cdn.Add(CdnObjectKind.Data, key, first ? lie : o.Bytes.ToArray());
            first = false;
            return Task.CompletedTask;
        };
        var offset = ChunkStart(10, 6) + 7;
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 50);

        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(o.Bytes.Slice((int)offset, 50).ToArray(), bytes);
        Assert.Equal([(0L, (long?)CdnFetcher.HeaderProbeBytes), (0L, 100_000L), (0L, 100_000L), (null, null)],
            s.Cdn.Requests.Select(q => (q.Offset, q.Length)));
        Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(o.Key));
    }

    [Fact]
    public async Task AReadTouchingAChunkOverTheRequestBoundTakesTheWholePath()
    {
        var s = new Scenario { Options = Ranged with { InteractiveInFlightBytes = 40 << 10 } };
        var o = s.Serve(Synthetic.Chunked(16, Chunk, Chunk, 48 << 10, Chunk, Chunk));   // chunk 2 is over the 40 KiB bound

        // A read of the small chunk before it is still a range.
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, ChunkStart(5, 1), 10);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice((int)ChunkStart(5, 1), 10).ToArray(), bytes);
        Assert.All(s.Cdn.Requests, q => Assert.NotNull(q.Offset));
        Assert.Equal(0, s.Store.Count);

        // From chunk 1 into chunk 2: the proven header is kept, and no range is asked for; the whole object is fetched and stored.
        s.Cdn.ClearRequests();
        var offset = ChunkStart(5, 2) - 5;
        (r, bytes) = await ReadAsync(s.Source, s.Context, o, offset, 10);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice((int)offset, 10).ToArray(), bytes);
        Assert.Equal([(null, null)], s.Cdn.Requests.Select(q => (q.Offset, q.Length)));
        Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(o.Key));
    }

    [Fact]
    public async Task TheSeventeenthHeaderEvictsTheOldestAndAReReadOfItProbesAgain()
    {
        var s = new Scenario { Options = Ranged };
        var objects = Enumerable.Range(100, 17).Select(seed => s.Serve(Big(seed))).ToArray();
        foreach (var o in objects) Assert.True((await ReadAsync(s.Source, s.Context, o, ChunkStart(10, 1), 10)).Result.IsPresent);

        // The newest is kept: only its chunk.
        s.Cdn.ClearRequests();
        await ReadAsync(s.Source, s.Context, objects[16], ChunkStart(10, 1), 10);
        Assert.Equal([ChunkStart(10, 1)], s.Cdn.Requests.Select(q => q.Offset!.Value));
        // The oldest went first: probed again.
        s.Cdn.ClearRequests();
        var (_, bytes) = await ReadAsync(s.Source, s.Context, objects[0], ChunkStart(10, 1), 10);
        Assert.Equal(objects[0].Bytes.Slice((int)ChunkStart(10, 1), 10).ToArray(), bytes);
        Assert.Equal([0L, ChunkStart(10, 1)], s.Cdn.Requests.Select(q => q.Offset!.Value));
        // Re-adding it evicted the next oldest.
        s.Cdn.ClearRequests();
        await ReadAsync(s.Source, s.Context, objects[1], ChunkStart(10, 1), 10);
        Assert.Equal([0L, ChunkStart(10, 1)], s.Cdn.Requests.Select(q => q.Offset!.Value));
    }

    [Fact]
    public async Task HeadersOverEightMebibytesEvictTheOldest()
    {
        // 40 000 chunks: a 960 012-byte header each, so the ninth takes the kept headers past 8 MiB while under 16 tables.
        const int chunks = 40_000, size = 30;
        var s = new Scenario { Options = Ranged };
        var objects = Enumerable.Range(200, 9).Select(seed => s.Serve(Big(seed, chunks, size))).ToArray();
        Assert.True(9 * Header(chunks) > 8L << 20 && 8 * Header(chunks) <= 8L << 20);
        var at = ChunkStart(chunks, 5, size);
        foreach (var o in objects) Assert.True((await ReadAsync(s.Source, s.Context, o, at, 10)).Result.IsPresent);

        s.Cdn.ClearRequests();
        await ReadAsync(s.Source, s.Context, objects[8], at, 10);
        Assert.Equal([at], s.Cdn.Requests.Select(q => q.Offset!.Value));
        s.Cdn.ClearRequests();
        await ReadAsync(s.Source, s.Context, objects[1], at, 10);
        Assert.Equal([at], s.Cdn.Requests.Select(q => q.Offset!.Value));
        s.Cdn.ClearRequests();
        var (_, bytes) = await ReadAsync(s.Source, s.Context, objects[0], at, 10);
        Assert.Equal(objects[0].Bytes.Slice((int)at, 10).ToArray(), bytes);
        Assert.Equal([(0L, (long)CdnFetcher.HeaderProbeBytes), (0L, Header(chunks)), (at, (long)size)],
            s.Cdn.Requests.Select(q => (q.Offset!.Value, q.Length!.Value)));
    }

    [Fact]
    public async Task AMissingObjectIsNotFound()
    {
        var s = new Scenario { Options = Ranged };
        var o = Big(8);
        var (r, _) = await ReadAsync(s.Source, s.Context, o, ChunkStart(10, 1), 10);
        Assert.Equal((ObjectState.NotFound, "CDN_NOT_FOUND"), (r.State, r.Code));
        Assert.All(s.Cdn.Requests, q => Assert.NotNull(q.Offset));
    }

    [Fact]
    public async Task AHeaderlessObjectFallsBackToTheWholePath()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Synthetic.Headerless(9, 100_000));
        var (r, bytes) = await ReadAsync(s.Source, s.Context, o, 50_000, 100);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice(50_000, 100).ToArray(), bytes);
        Assert.Equal([(0L, (long?)CdnFetcher.HeaderProbeBytes), (null, null)], s.Cdn.Requests.Select(q => (q.Offset, q.Length)));
        Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(o.Key));
    }

    [Fact]
    public async Task SmallObjectsAndStoredOrSharedCopiesKeepTodaysPath()
    {
        // Under the bound: fetched whole and stored first.
        var s = new Scenario { Options = Ranged };
        var small = s.Serve(Synthetic.Chunked(10, 2000, 3000));
        await ReadAsync(s.Source, s.Context, small, 2500, 10);
        Assert.Null(Assert.Single(s.Cdn.Requests).Offset);
        Assert.NotNull(s.Store.OriginOf(small.Key));

        // Stored: no request at all.
        var stored = Big(11);
        await s.Store.SeedAsync(stored);
        s.Cdn.ClearRequests();
        var (r, bytes) = await ReadAsync(s.Source, s.Context, stored, ChunkStart(10, 3), 10);
        Assert.Equal(ServedFrom.Store, r.ServedFrom);
        Assert.Equal(stored.Bytes.Slice((int)ChunkStart(10, 3), 10).ToArray(), bytes);
        Assert.Empty(s.Cdn.Requests);

        // In the shared store: copied in, never fetched.
        var shared = new InMemoryObjectStore();
        var copy = Big(12);
        await shared.SeedAsync(copy);
        var t = new Scenario { Options = Ranged, Shared = shared };
        (r, bytes) = await ReadAsync(t.Source, t.Context, copy, ChunkStart(10, 3), 10);
        Assert.True(r.IsPresent);
        Assert.Equal(copy.Bytes.Slice((int)ChunkStart(10, 3), 10).ToArray(), bytes);
        Assert.Empty(t.Cdn.Requests);
        Assert.Equal(ObjectOrigin.SharedStore, t.Store.OriginOf(copy.Key));
    }

    [Fact]
    public async Task CancelingARangeReadReleasesItsStreamAndStoresNothing()
    {
        var s = new Scenario { Options = Ranged };
        var o = s.Serve(Big(13));
        using var cancel = new CancellationTokenSource();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Cdn.BeforeAnswer = async (q, token) =>
        {
            if (q.Offset == 0) return;
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var read = ReadAsync(s.Source, s.Context, o, ChunkStart(10, 2), 10, cancel.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal((0, 0), (s.Source.Streams.InteractiveActive, s.Source.Streams.BackgroundActive));
        Assert.Equal(0, s.Store.Count);

        // The source still works afterwards, and the proven header is kept.
        s.Cdn.BeforeAnswer = null;
        s.Cdn.ClearRequests();
        var (r, _) = await ReadAsync(s.Source, s.Context, o, ChunkStart(10, 2), 10);
        Assert.True(r.IsPresent);
        Assert.Single(s.Cdn.Requests);
    }

    [Fact]
    public async Task TheCdnOnlySourceReadsABigLooseObjectByRange()
    {
        var s = new Scenario();
        var o = s.Serve(Big(14));
        var cdnOnly = new CdnObjectSource(new ScriptedCdnClient(s.Cdn), [], ObjectVerification.Default,
            new ObjectSourceOptions { RangeFetchMinBytes = 64 << 10, InMemoryObjectBytes = 64 << 10 });
        var offset = ChunkStart(10, 7) + 5;
        var (r, bytes) = await ReadAsync(cdnOnly, s.Context, o, offset, 30);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(o.Bytes.Slice((int)offset, 30).ToArray(), bytes);
        Assert.All(s.Cdn.Requests, q => Assert.NotNull(q.Offset));
        // A whole read of the same object is still over this source's in-memory bound.
        Assert.Equal("OBJECT_ACCESS_TOO_LARGE", (await cdnOnly.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context, FetchPriority.Interactive, None)).Code);
    }
}
