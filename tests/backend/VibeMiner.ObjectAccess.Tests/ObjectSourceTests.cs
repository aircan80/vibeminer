using System.Diagnostics;
using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class ObjectSourceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task MetadataLandsVerifiedWithItsOriginAndASecondRunMakesNoRequests()
    {
        var s = new Scenario();
        var config = s.Serve(Synthetic.Config(1));
        var index = s.Serve(Synthetic.Index(2, 400));
        var fileIndex = s.Serve(Synthetic.Index(3, 300, offsetWidth: 0));
        var loose = s.Serve(Synthetic.Chunked(4, 5000, 7000));
        var members = Enumerable.Range(10, 6).Select(i => Synthetic.Headerless(i, 300 + i)).ToArray();
        var archive = s.Archive(1, members);
        ObjectRequest[] requests = [new(config.Key), new(index.Key), new(fileIndex.Key), new(loose.Key, loose.DeclaredLength),
            ..members.Select(m => archive.Request(m))];

        var first = await s.Source.GetKeysAsync(requests, s.Context, FetchPriority.Background, null, None);
        Assert.All(first, r => Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value)));
        Assert.All(requests, r => Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(r.Key)));
        Assert.Equal(1 + 1 + 1 + 1 + 1, s.Cdn.Requests.Count);   // config, two indexes, the loose object, one merged range

        s.Cdn.ClearRequests();
        var second = await s.Source.GetKeysAsync(requests, s.Context, FetchPriority.Background, null, None);
        Assert.All(second, r => Assert.Equal((ObjectState.Present, ServedFrom.Store), (r.State, r.ServedFrom!.Value)));
        Assert.Empty(s.Cdn.Requests);
        var bytes = await s.Source.GetKeyAsync(new(config.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal(config.Bytes.ToArray(), bytes.Bytes);
        Assert.Empty(s.Cdn.Requests);
    }

    [Fact]
    public async Task AnUndeclaredLengthReplyIsCappedAtWhatItIsCharged()
    {
        var s = new Scenario();
        var small = s.Serve(Synthetic.Config(1));
        var big = s.Serve(Synthetic.Config(2, (int)CdnFetcher.UnknownLengthBytes + 1));
        var r = await s.Source.GetKeysAsync([new(small.Key), new(big.Key)], s.Context, FetchPriority.Background, null, None);
        Assert.Equal(ObjectState.Present, r.Single(x => x.Key == small.Key).State);
        var refused = r.Single(x => x.Key == big.Key);
        Assert.Equal((ObjectState.Refused, "CDN_TOO_LARGE"), (refused.State, refused.Code));
        Assert.Null(s.Store.OriginOf(big.Key));
        // Declared, the same object is charged its length and lands.
        Assert.Equal(ObjectState.Present, (await s.Source.GetKeyAsync(new(big.Key, big.DeclaredLength), s.Context, FetchPriority.Interactive, None)).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStoreCheckThatFailsBeforeTheTransferFailsOnlyThatObjectAndReleasesTheKey(bool canceled)
    {
        var s = new Scenario();
        var config = s.Serve(Synthetic.Config(5));
        var other = s.Serve(Synthetic.Config(6));
        using var cts = new CancellationTokenSource();
        var calls = 0;
        // The first HasAsync is GetKeys' own; the second is the fetcher's check just before the transfer.
        s.Store.BeforeHas = (key, token) =>
        {
            if (key != config.Key || Interlocked.Increment(ref calls) != 2) return ValueTask.CompletedTask;
            if (!canceled) throw new ObjectStoreException(ObjectStoreCodes.Closed, "closed (fake)");
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        var first = s.Source.GetKeysAsync([new(config.Key), new(other.Key)], s.Context, FetchPriority.Background, null, cts.Token);
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        else
        {
            var r = await first;
            Assert.Equal((ObjectState.StoreFailed, ObjectStoreCodes.Closed), (r[0].State, r[0].Code));
            Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r[1].State, r[1].ServedFrom!.Value));
            Assert.Null(s.Store.OriginOf(config.Key));
        }

        s.Store.BeforeHas = null;
        var second = await s.Source.GetKeysAsync([new(config.Key)], s.Context, FetchPriority.Background, null, None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(second[0].IsPresent);
        Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(config.Key));
    }

    [Fact]
    public async Task ArchivedObjectsComeInMergedRangesAndGapsOverTheThresholdSplitThem()
    {
        var s = new Scenario { Options = new() { Ranges = new() { GapBytes = 1000 } } };
        var members = Enumerable.Range(0, 20).Select(i => Synthetic.Headerless(i, 500)).ToArray();
        var archive = s.Archive(2, members, gap: 600);
        var progress = new List<ObjectProgress>();
        var all = await s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Background,
            new SyncProgress(progress), None);
        Assert.All(all, r => Assert.True(r.IsPresent));
        var range = Assert.Single(s.Cdn.Requests);
        Assert.Equal((0L, 20 * 1100L - 600), (range.Offset!.Value, range.Length!.Value));
        Assert.Equal(20, progress.Max(p => p.Done));
        Assert.Equal(20 * 1100L - 600, progress.Max(p => p.CdnBytes));
        Assert.Equal(20, s.Store.Count);

        var t = new Scenario { Options = new() { Ranges = new() { GapBytes = 1000 } } };
        var other = t.Archive(2, members, gap: 600);
        var everyOther = members.Where((_, i) => i % 2 == 0).Select(m => other.Request(m)).ToArray();   // gaps of 1,700 bytes
        Assert.All(await t.Source.GetKeysAsync(everyOther, t.Context, FetchPriority.Background, null, None), r => Assert.True(r.IsPresent));
        Assert.Equal(10, t.DataRequests);
        Assert.All(t.Cdn.Requests, r => Assert.Equal(500, r.Length));
    }

    [Fact]
    public async Task OneBadObjectInARangeFailsAloneAfterARequestOfItsOwn()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 5).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(3, members);
        // The index points member 2 at the wrong bytes: its location is shifted into member 3.
        var requests = members.Select(m => archive.Request(m)).ToArray();
        var bad = archive.Locations[members[2].Key];
        requests[2] = requests[2] with { Locations = [bad with { Offset = bad.Offset + 1 }] , DeclaredLength = null };
        requests = [.. requests.Where((_, i) => i != 3)];   // keep the spans from overlapping
        var results = await s.Source.GetKeysAsync(requests, s.Context, FetchPriority.Background, null, None);
        Assert.Equal([true, true, false, true], results.Select(r => r.IsPresent));
        Assert.Equal((ObjectState.Refused, ObjectRefusalCodes.BlteInvalid), (results[2].State, results[2].Code));
        Assert.Equal(2, s.DataRequests);                   // the merged range, then the bad one on its own
        Assert.False(await s.Store.HasAsync(members[2].Key));
    }

    [Fact]
    public async Task ARangeMemberWithBadBytesOnOneHostComesFromTheNextHost()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 5).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(5, members);
        s.Cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, archive.Key);    // flips a byte of member 2
        var results = await s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Interactive, null, None);
        Assert.All(results, r => Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value)));
        Assert.Equal(members[2].Bytes.ToArray(), (await s.Store.ReadAsync(members[2].Key)).Bytes);
        Assert.Equal([(Scenario.L3, 0L), (Scenario.Us, 800L)], s.Cdn.Requests.Select(r => (r.Host, r.Offset!.Value)));

        // The host that sent bad bytes goes last for a while.
        var next = s.Serve(Synthetic.Config(9));
        s.Cdn.ClearRequests();
        Assert.True((await s.Source.GetKeyAsync(new(next.Key), s.Context, FetchPriority.Interactive, None)).IsPresent);
        Assert.Equal(Scenario.Us, Assert.Single(s.Cdn.Requests).Host);
    }

    [Fact]
    public async Task ALooseObjectWithBadBytesOnOneHostComesFromTheNextHostAndThatHostGoesLast()
    {
        var s = new Scenario();
        var bad = s.Serve(Synthetic.Config(33));
        s.Cdn.CorruptOn(Scenario.L3, CdnObjectKind.Config, Scenario.KeyOf(bad.Key));   // the client's check fails on L3
        var r = await s.Source.GetKeyAsync(new(bad.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal([Scenario.L3, Scenario.Us], s.Cdn.Requests.Select(q => q.Host));

        var next = s.Serve(Synthetic.Config(34));
        s.Cdn.ClearRequests();
        Assert.True((await s.Source.GetKeyAsync(new(next.Key), s.Context, FetchPriority.Interactive, None)).IsPresent);
        Assert.Equal(Scenario.Us, Assert.Single(s.Cdn.Requests).Host);
    }

    [Fact]
    public async Task ALargeObjectWhoseBytesTheStoreRefusedComesFromTheNextHostAndThatHostGoesLast()
    {
        var s = new Scenario { Options = new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 } };
        var big = s.Serve(Synthetic.Chunked(31, Enumerable.Repeat(256 * 1024, 12).ToArray()));
        s.Cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, Scenario.KeyOf(big.Key));     // a byte in a middle chunk: refused at staging
        var r = await s.Source.GetKeyAsync(new(big.Key, big.DeclaredLength), s.Context, FetchPriority.Background, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(big.Bytes.ToArray(), (await s.Store.ReadAsync(big.Key)).Bytes);
        Assert.Equal([Scenario.L3, Scenario.Us], s.Cdn.Requests.Select(q => q.Host));

        var next = s.Serve(Synthetic.Config(32));
        s.Cdn.ClearRequests();
        Assert.True((await s.Source.GetKeyAsync(new(next.Key), s.Context, FetchPriority.Background, None)).IsPresent);
        Assert.Equal(Scenario.Us, Assert.Single(s.Cdn.Requests).Host);
    }

    [Fact]
    public async Task ARangeMemberWithBadBytesOnEveryHostIsRefused()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 5).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(5, members);
        s.Cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, archive.Key).CorruptOn(Scenario.Us, CdnObjectKind.Data, archive.Key);
        var results = await s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Interactive, null, None);
        Assert.Equal([true, true, false, true, true], results.Select(r => r.IsPresent));
        Assert.Equal(ObjectState.Refused, results[2].State);
        Assert.NotNull(results[2].Code);
        Assert.Equal([(Scenario.L3, 0L), (Scenario.Us, 800L)], s.Cdn.Requests.Select(r => (r.Host, r.Offset!.Value)));
        Assert.False(await s.Store.HasAsync(members[2].Key));
    }

    [Fact]
    public async Task EncryptedMembersOfARangeAreStoredExactlyAsServed()
    {
        var s = new Scenario();
        var encrypted = Encrypted(21, 900, 700);
        var clear = Synthetic.Chunked(22, 500, 400);
        var archive = s.Archive(13, [clear, encrypted]);
        var results = await s.Source.GetKeysAsync([archive.Request(clear), archive.Request(encrypted)], s.Context, FetchPriority.Background, null, None);
        Assert.All(results, r => Assert.True(r.IsPresent));
        Assert.Single(s.Cdn.Requests);
        var read = await s.Store.ReadAsync(encrypted.Key);
        Assert.Equal(encrypted.Bytes.ToArray(), read.Bytes);
    }

    /// <summary>A chunked BLTE object whose chunks are mode 'E' (an 8-byte key name, a 4-byte IV, cipher 'S', then opaque bytes).</summary>
    private static PutObject Encrypted(int seed, params int[] chunkSizes)
    {
        var headerLength = 12 + 24 * chunkSizes.Length;
        var b = new byte[headerLength + chunkSizes.Sum()];
        new Random(seed).NextBytes(b.AsSpan(headerLength));
        "BLTE"u8.CopyTo(b);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)headerLength);
        b[8] = 0x0F; b[11] = (byte)chunkSizes.Length;
        var at = headerLength;
        for (var i = 0; i < chunkSizes.Length; i++)
        {
            b[at] = (byte)'E'; b[at + 1] = 8; b[at + 10] = 4; b[at + 15] = (byte)'S';
            var d = b.AsSpan(12 + i * 24);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d, (uint)chunkSizes[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d[4..], (uint)(chunkSizes[i] - 16));
            System.Security.Cryptography.MD5.HashData(b.AsSpan(at, chunkSizes[i])).CopyTo(d[8..]);
            at += chunkSizes[i];
        }
        return new(new ObjectKey(ObjectKeyType.Encoded, System.Security.Cryptography.MD5.HashData(b.AsSpan(0, headerLength))), b.Length, b);
    }

    [Fact]
    public async Task AMergedRangeThatFailsEverywhereFallsBackToOneRequestPerObject()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 4).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(4, members);
        s.Cdn.SetMode(Scenario.L3, FakeHostMode.Unavailable).SetMode(Scenario.Us, FakeHostMode.Unavailable);
        var results = await s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Background, null, None);
        Assert.All(results, r => Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS"), (r.State, r.Code)));
        Assert.All(results, r => Assert.True(r.Retryable));
        Assert.Equal(2 + 4 * 2, s.Cdn.Requests.Count);     // one range on both hosts, then each object on both hosts
    }

    [Fact]
    public async Task A200ToARangeMovesToTheNextHostAndThatHostGoesLastForAWhile()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 3).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(5, members);
        s.Cdn.SetMode(Scenario.L3, FakeHostMode.IgnoresRanges);
        Assert.All(await s.Source.GetKeysAsync([archive.Request(members[0])], s.Context, FetchPriority.Background, null, None), r => Assert.True(r.IsPresent));
        Assert.Equal([Scenario.L3, Scenario.Us], s.Cdn.Requests.Select(r => r.Host));
        s.Cdn.ClearRequests();
        Assert.All(await s.Source.GetKeysAsync([archive.Request(members[1])], s.Context, FetchPriority.Background, null, None), r => Assert.True(r.IsPresent));
        Assert.Equal([Scenario.Us], s.Cdn.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task BackgroundWorkPrefersHttp2Hosts()
    {
        var s = new Scenario();
        s.Cdn.Http2(Scenario.Us);
        var a = s.Serve(Synthetic.Config(1)); var b = s.Serve(Synthetic.Config(2)); var c = s.Serve(Synthetic.Config(3));
        await s.Source.GetKeyAsync(new(a.Key), s.Context, FetchPriority.Background, None);   // L3, HTTP/1.1
        s.Cdn.MissingOn(Scenario.L3, CdnObjectKind.Config, Scenario.KeyOf(b.Key));
        await s.Source.GetKeyAsync(new(b.Key), s.Context, FetchPriority.Background, None);   // L3 404, then Us answers over HTTP/2
        s.Cdn.ClearRequests();
        await s.Source.GetKeyAsync(new(c.Key), s.Context, FetchPriority.Background, None);
        Assert.Equal(Scenario.Us, Assert.Single(s.Cdn.Requests).Host);
        var d = s.Serve(Synthetic.Config(4));
        s.Cdn.ClearRequests();
        await s.Source.GetKeyAsync(new(d.Key), s.Context, FetchPriority.Interactive, None);  // interactive keeps the advertised order
        Assert.Equal(Scenario.L3, Assert.Single(s.Cdn.Requests).Host);
    }

    [Fact]
    public async Task A404EverywhereIsDatedAndRetryableAndNothingIsStored()
    {
        var s = new Scenario();
        var o = Synthetic.Headerless(1);
        var r = await s.Source.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.NotFound, "CDN_NOT_FOUND"), (r.State, r.Code));
        Assert.Equal(s.Cdn.Time.GetUtcNow(), r.ObservedAt);
        Assert.True(r.Retryable);
        Assert.Equal(0, s.Store.Count);
        s.Serve(o);
        Assert.True((await s.Source.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context, FetchPriority.Interactive, None)).IsPresent);
    }

    [Fact]
    public async Task OfflineIsUnavailableAndNoContextIsNamed()
    {
        var s = new Scenario();
        s.Cdn.SetMode(Scenario.L3, FakeHostMode.Offline).SetMode(Scenario.Us, FakeHostMode.Offline);
        var o = s.Serve(Synthetic.Config(1));
        var r = await s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Unavailable, "CDN_OFFLINE"), (r.State, r.Code));
        var none = await s.Source.GetKeyAsync(new(o.Key), null, FetchPriority.Interactive, None);
        Assert.Equal("OBJECT_ACCESS_NO_CDN_CONTEXT", none.Code);
    }

    [Fact]
    public async Task SizeConflictsOverlapsAndKeysTheCdnDoesntServeAreRefusedWithoutARequest()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 3).Select(i => Synthetic.Headerless(i, 400)).ToArray();
        var archive = s.Archive(6, members);
        var conflict = archive.Request(members[0]) with { DeclaredLength = 401 };
        var loc = archive.Locations[members[1].Key];
        var overlapA = archive.Request(members[1]);
        var overlapB = archive.Request(members[2]) with { Locations = [archive.Locations[members[2].Key] with { Offset = loc.Offset + 10 }] };
        var history = new ObjectRequest(new ObjectKey(ObjectKeyType.HistoryDocument, new byte[16]));
        var config = Synthetic.Config(9);
        var located = new ObjectRequest(config.Key, null, [loc]);
        var results = await s.Source.GetKeysAsync([conflict, overlapA, overlapB, history, located], s.Context, FetchPriority.Background, null, None);
        Assert.Equal([RangePlanner.SizeConflict, RangePlanner.Overlap, RangePlanner.Overlap, "OBJECT_NOT_ON_CDN", "OBJECT_LOCATION_UNSUPPORTED"], results.Select(r => r.Code));
        Assert.All(results, r => Assert.Equal(ObjectState.Refused, r.State));
        Assert.Empty(s.Cdn.Requests);
    }

    [Fact]
    public async Task LowerOrderGroupsAreFetchedFirstThenArchiveOrder()
    {
        var s = new Scenario { Options = new() { Streams = new() { MinBackground = 1, MaxBackground = 1, Interactive = 1 } } };
        var a = Enumerable.Range(0, 2).Select(i => Synthetic.Headerless(i, 300)).ToArray();
        var b = Enumerable.Range(10, 2).Select(i => Synthetic.Headerless(i, 300)).ToArray();
        var first = s.Archive(7, a); var second = s.Archive(8, b);
        var looseLater = s.Serve(Synthetic.Headerless(50, 300));
        var looseFirst = s.Serve(Synthetic.Headerless(51, 300));
        ObjectRequest[] requests = [new(looseLater.Key, 300, null, 1), second.Request(b[0], 1), first.Request(a[0], 1),
            new(looseFirst.Key, 300, null, 0), second.Request(b[1], 0)];
        var results = await s.Source.GetKeysAsync(requests, s.Context, FetchPriority.Background, null, None);
        Assert.All(results, r => Assert.True(r.IsPresent));
        Assert.Equal([second.Key, Scenario.KeyOf(looseFirst.Key), first.Key, second.Key, Scenario.KeyOf(looseLater.Key)], s.Cdn.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task AnInteractiveFetchIsServedWhile32BackgroundStreamsAreBusy()
    {
        var s = new Scenario { Options = new() { Streams = new() { MinBackground = 32, MaxBackground = 32, Interactive = 4 }, Ranges = new() { GapBytes = 0, TargetBytes = 1, CapBytes = 1 << 20 } } };
        var members = Enumerable.Range(0, 64).Select(i => Synthetic.Headerless(i, 300)).ToArray();
        var archive = s.Archive(9, members, gap: 1);
        var hold = new TaskCompletionSource();
        var started = 0;
        var all32 = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = async (r, token) =>
        {
            if (r.Key != archive.Key) return;
            if (Interlocked.Increment(ref started) == 32) all32.TrySetResult();
            await hold.Task.WaitAsync(token);
        };
        var bulk = s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Background, null, None);
        await all32.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(32, s.Source.Streams.BackgroundActive);

        var config = s.Serve(Synthetic.Config(1));
        var clock = Stopwatch.StartNew();
        var r = await s.Source.GetKeyAsync(new(config.Key), s.Context, FetchPriority.Interactive, None).WaitAsync(TimeSpan.FromSeconds(30));
        clock.Stop();
        Assert.True(r.IsPresent);
        Assert.Equal(32, s.Source.Streams.BackgroundActive);
        Assert.Equal(32, Volatile.Read(ref started));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"interactive took {clock.Elapsed}");

        hold.SetResult();
        Assert.All(await bulk, x => Assert.True(x.IsPresent));
    }

    [Fact]
    public async Task RangesLargerThanHalfTheByteBudgetNeverDeadlock()
    {
        // Regression: units were taken one at a time, so two 600 KiB ranges could each hold part of a 1 MiB budget and wait forever.
        var s = new Scenario { Options = new() { InFlightBytes = 1 << 20, Ranges = new() { GapBytes = 0, TargetBytes = 1, CapBytes = 1 << 20 } } };
        var members = Enumerable.Range(0, 24).Select(i => Synthetic.Headerless(i, 600 << 10)).ToArray();
        var archive = s.Archive(12, members, gap: 1);
        var results = await s.Source.GetKeysAsync(members.Select(m => archive.Request(m)).ToArray(), s.Context, FetchPriority.Background, null, None)
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.All(results, r => Assert.True(r.IsPresent));
        Assert.Equal(24, s.DataRequests);
    }

    [Fact]
    public async Task InteractiveWorkDoesntWaitBehindBackgroundBytes()
    {
        var s = new Scenario { Options = new() { InFlightBytes = 1 << 20 } };
        var bulk = Enumerable.Range(0, 4).Select(i => s.Serve(Synthetic.Headerless(i, 1 << 20))).ToArray();
        var hold = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = (r, token) =>
        {
            if (r.Kind != CdnObjectKind.Data) return Task.CompletedTask;
            holding.TrySetResult();
            return hold.Task.WaitAsync(token);
        };
        var background = s.Source.GetKeysAsync(bulk.Select(o => new ObjectRequest(o.Key, o.DeclaredLength)).ToArray(), s.Context, FetchPriority.Background, null, None);
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var config = s.Serve(Synthetic.Config(1));
        var r = await s.Source.GetKeyAsync(new(config.Key), s.Context, FetchPriority.Interactive, None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.IsPresent);
        hold.SetResult();
        Assert.All(await background, x => Assert.True(x.IsPresent));
    }

    [Fact]
    public async Task AnArchiveEntryTooLargeForARangeIsRefused()
    {
        var s = new Scenario();
        var o = Synthetic.Headerless(1, 300);
        var r = await s.Source.GetKeysAsync([new(o.Key, 100L << 20, [new(new CdnKey(new byte[16]), 0, 100L << 20)])], s.Context, FetchPriority.Background, null, None);
        Assert.Equal("OBJECT_LOCATION_TOO_LARGE", r[0].Code);
        Assert.Empty(s.Cdn.Requests);
    }

    [Fact]
    public async Task AScreenWaitingOnAQueuedBackgroundObjectPromotesItAndSharesTheTransfer()
    {
        var s = new Scenario { Options = new() { Streams = new() { MinBackground = 1, MaxBackground = 2, Interactive = 1 } } };
        var blocker = s.Serve(Synthetic.Headerless(1, 300));
        var queued = s.Serve(Synthetic.Headerless(2, 300));
        var hold = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = (r, token) => r.Key == Scenario.KeyOf(blocker.Key) ? hold.Task.WaitAsync(token) : Task.CompletedTask;
        var bulk = s.Source.GetKeysAsync([new(blocker.Key, 300), new(queued.Key, 300)], s.Context, FetchPriority.Background, null, None);
        await Until(() => s.Source.Streams.BackgroundQueued == 1);   // blocker holds the one stream; queued waits behind it
        var screen = await s.Source.GetKeyAsync(new(queued.Key, 300), s.Context, FetchPriority.Interactive, None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(screen.IsPresent);
        Assert.Equal(queued.Bytes.ToArray(), screen.Bytes);
        Assert.Single(s.Cdn.Requests, r => r.Key == Scenario.KeyOf(queued.Key));
        hold.SetResult();
        Assert.All(await bulk, x => Assert.True(x.IsPresent));
    }

    [Fact]
    public async Task AScreenWaitingOnABackgroundObjectStillWaitingForBytesPromotesIt()
    {
        // Regression: promotion only reached the stream queue, so an object waiting on background bytes stayed stuck behind them.
        var s = new Scenario { Options = new() { InFlightBytes = 1 << 20 } };
        var blocker = s.Serve(Synthetic.Headerless(1, 1 << 20));
        var queued = s.Serve(Synthetic.Headerless(2, 300));
        var hold = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = (r, token) =>
        {
            if (r.Key != Scenario.KeyOf(blocker.Key)) return Task.CompletedTask;
            holding.TrySetResult();
            return hold.Task.WaitAsync(token);
        };
        var bulk = s.Source.GetKeysAsync([new(blocker.Key, blocker.DeclaredLength)], s.Context, FetchPriority.Background, null, None);
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var waiting = s.Source.GetKeysAsync([new(queued.Key, 300)], s.Context, FetchPriority.Background, null, None);
        await Until(() => s.Source.Fetcher.BackgroundBytesWaiting == 1);   // waiting behind the blocker's bytes
        Assert.False(waiting.IsCompleted);
        var screen = await s.Source.GetKeyAsync(new(queued.Key, 300), s.Context, FetchPriority.Interactive, None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(screen.IsPresent);
        Assert.False(bulk.IsCompleted);
        Assert.Single(s.Cdn.Requests, r => r.Key == Scenario.KeyOf(queued.Key));
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10)))[0].IsPresent);
        hold.SetResult();
        Assert.All(await bulk.WaitAsync(TimeSpan.FromSeconds(30)), x => Assert.True(x.IsPresent));
    }

    [Fact]
    public async Task ConcurrentCallersForOneKeyShareOneTransfer()
    {
        var s = new Scenario();
        var o = s.Serve(Synthetic.Chunked(3, 4000, 4000));
        var hold = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = (_, token) => hold.Task.WaitAsync(token);
        var calls = Enumerable.Range(0, 8).Select(i => s.Source.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context,
            i % 2 == 0 ? FetchPriority.Interactive : FetchPriority.Background, None)).ToArray();
        await Until(() => s.Source.Fetcher.Joins == calls.Length - 1);   // every other caller is waiting on the one transfer
        hold.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.Equal(o.Bytes.ToArray(), r.Bytes));
        Assert.Single(s.Cdn.Requests);
    }

    [Fact]
    public async Task ACanceledCallerLeavesNothingBehindAndAWaitingCallerFetchesItself()
    {
        var s = new Scenario();
        var o = s.Serve(Synthetic.Config(5));
        var hold = new TaskCompletionSource();
        s.Cdn.BeforeAnswer = (_, token) => hold.Task.WaitAsync(token);
        using var cancel = new CancellationTokenSource();
        var owner = s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Background, cancel.Token);
        while (s.Cdn.Requests.Count == 0) await Task.Delay(5);
        var waiter = s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None);
        await Until(() => s.Source.Fetcher.Joins == 1);
        s.Cdn.BeforeAnswer = null;
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        var r = await waiter.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(o.Bytes.ToArray(), r.Bytes);
        Assert.Equal(0, s.Source.Streams.BackgroundActive + s.Source.Streams.InteractiveActive);
    }

    [Fact]
    public async Task TheSharedDevStoreIsCopiedInAndRecordedAsSuchWithNoCdnRequest()
    {
        var shared = new InMemoryObjectStore();
        var s = new Scenario { Shared = shared };
        var o = Synthetic.Chunked(8, 3000, 3000);
        await shared.SeedAsync(o);
        var one = await s.Source.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ServedFrom.SharedStore, ObjectOrigin.SharedStore), (one.ServedFrom!.Value, s.Store.OriginOf(o.Key)!.Value));
        Assert.Equal(o.Bytes.ToArray(), one.Bytes);
        var p = Synthetic.Config(8); await shared.SeedAsync(p);
        var many = await s.Source.GetKeysAsync([new(p.Key)], s.Context, FetchPriority.Background, null, None);
        Assert.Equal(ServedFrom.SharedStore, many[0].ServedFrom);
        Assert.Empty(s.Cdn.Requests);
        var again = await s.Source.GetKeyAsync(new(o.Key, o.DeclaredLength), s.Context, FetchPriority.Interactive, None);
        Assert.Equal(ServedFrom.Store, again.ServedFrom);
    }

    [Fact]
    public async Task ABadCopyInTheSharedStoreIsNotTakenAndTheCdnServesIt()
    {
        var shared = new InMemoryObjectStore();
        var s = new Scenario { Shared = shared };
        var o = Synthetic.Config(3);
        var wrong = o.Bytes.ToArray(); wrong[5] ^= 1;
        await shared.SeedAsync(o);
        shared.Tamper(o.Key, wrong);
        s.Serve(o);
        var r = await s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ServedFrom.Cdn, ObjectOrigin.Cdn), (r.ServedFrom!.Value, s.Store.OriginOf(o.Key)!.Value));
    }

    [Fact]
    public async Task ALargeSharedCopyWithABadChunkIsFetchedFromTheCdnAndTheBatchCarriesOn()
    {
        var shared = new InMemoryObjectStore();
        var s = new Scenario { Shared = shared, Options = new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 } };
        var big = s.Serve(Synthetic.Chunked(21, Enumerable.Repeat(256 * 1024, 12).ToArray()));
        var wrong = big.Bytes.ToArray(); wrong[^1000] ^= 1;     // a byte in the last chunk
        await shared.SeedAsync(big);
        shared.Tamper(big.Key, wrong);
        var fromShared = Synthetic.Chunked(22, 3000, 3000); await shared.SeedAsync(fromShared);
        var fromCdn = s.Serve(Synthetic.Config(23));

        var r = await s.Source.GetKeysAsync([new(fromShared.Key, fromShared.DeclaredLength), new(big.Key, big.DeclaredLength), new(fromCdn.Key)],
            s.Context, FetchPriority.Background, null, None);
        Assert.Equal([ServedFrom.SharedStore, ServedFrom.Cdn, ServedFrom.Cdn], r.Select(x => x.ServedFrom!.Value));
        Assert.All(r, x => Assert.Equal(ObjectState.Present, x.State));
        Assert.Equal(big.Bytes.ToArray(), (await s.Store.ReadAsync(big.Key)).Bytes);
        Assert.Equal(ObjectOrigin.Cdn, s.Store.OriginOf(big.Key));
    }

    [Fact]
    public async Task ADamagedStoredCopyIsReplacedFromTheCdn()
    {
        var s = new Scenario();
        var o = s.Serve(Synthetic.Config(2));
        await s.Store.SeedAsync(o);
        s.Store.Corrupt(o.Key);
        var r = await s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Single(s.Cdn.Requests);
    }

    [Fact]
    public async Task RangesAreReadFromTheStoreAfterTheObjectIsMade()
    {
        var s = new Scenario();
        var o = s.Serve(Synthetic.Chunked(4, 2000, 3000));
        var into = new byte[100];
        var r = await s.Source.GetRangeAsync(new(o.Key, o.DeclaredLength), 2500, into, s.Context, FetchPriority.Interactive, None);
        Assert.True(r.IsPresent);
        Assert.Equal(o.Bytes.Slice(2500, 100).ToArray(), into);
        var again = await s.Source.GetRangeAsync(new(o.Key, o.DeclaredLength), 0, into, s.Context, FetchPriority.Interactive, None);
        Assert.Equal(ServedFrom.Store, again.ServedFrom);
        Assert.Single(s.Cdn.Requests);
    }

    [Fact]
    public async Task VersionRepliesGoIntoTheHistoryLog()
    {
        var s = new Scenario();
        var r = await s.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), None);
        Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        var h = Assert.Single(s.Store.History);
        Assert.Equal((HistoryKind.Cdns, "wow", "us", "https://us.version.battle.net/v2/products/wow/cdns"), (h.Kind, h.Product, h.Region, h.Url));
        Assert.Equal(r.Observation!.Bytes, h.Reply.ToArray());
    }

    [Fact]
    public async Task TheLocalOnlySourceNeverTouchesTheNetwork()
    {
        var store = new InMemoryObjectStore(); var shared = new InMemoryObjectStore();
        var a = Synthetic.Config(1); await store.SeedAsync(a);
        var b = Synthetic.Config(2); await shared.SeedAsync(b);
        var local = new LocalObjectSource(store, store, shared);
        Assert.Equal(ServedFrom.Store, (await local.GetKeyAsync(new(a.Key), null, FetchPriority.Interactive, None)).ServedFrom);
        Assert.Equal(ServedFrom.SharedStore, (await local.GetKeyAsync(new(b.Key), null, FetchPriority.Interactive, None)).ServedFrom);
        var missing = await local.GetKeysAsync([new(Synthetic.Config(3).Key), new(a.Key)], new(Scenario.Route), FetchPriority.Background, null, None);
        Assert.Equal([ObjectState.NeedsNetwork, ObjectState.Present], missing.Select(r => r.State));
        Assert.Equal(ObjectState.NeedsNetwork, (await local.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), None)).State);
        store.Corrupt(a.Key);
        Assert.Equal("OBJECT_ACCESS_CORRUPT", (await local.GetKeyAsync(new(a.Key), null, FetchPriority.Interactive, None)).Code);
    }

    [Fact]
    public async Task TheCdnOnlySourceFetchesEveryTimeAndStoresNothing()
    {
        var s = new Scenario();
        var members = Enumerable.Range(0, 3).Select(i => Synthetic.Headerless(i, 300)).ToArray();
        var archive = s.Archive(11, members);
        var cdnOnly = new CdnObjectSource(new ScriptedCdnClient(s.Cdn), [new VersionServiceObservations(new VersionClient(new HttpClient(new Scenario.VersionHandler())))], ObjectVerification.Default);
        var r = await cdnOnly.GetKeyAsync(archive.Request(members[1]), s.Context, FetchPriority.Interactive, None);
        Assert.Equal(members[1].Bytes.ToArray(), r.Bytes);
        await cdnOnly.GetKeyAsync(archive.Request(members[1]), s.Context, FetchPriority.Interactive, None);
        Assert.Equal(2, s.Cdn.Requests.Count);
        var big = await cdnOnly.GetKeyAsync(new(members[0].Key, 100L << 20), s.Context, FetchPriority.Interactive, None);
        Assert.Equal("OBJECT_ACCESS_TOO_LARGE", big.Code);
        var observed = await cdnOnly.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), None);
        Assert.Equal(0, await cdnOnly.CountPresentAsync([members[1].Key], None));
        Assert.Equal([false, false], await cdnOnly.HasKeysAsync([members[1].Key, members[0].Key], None));
        Assert.Empty(await cdnOnly.ReadHistoryAsync(new(), None));
        Assert.Equal((ObjectState.Present, false), (observed.State, observed.Recorded));
    }

    /// <summary>Waits for a state the source reaches on its own; the deadline is a deadlock guard.</summary>
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class SyncProgress(List<ObjectProgress> into) : IProgress<ObjectProgress>
    {
        public void Report(ObjectProgress value) { lock (into) into.Add(value); }
    }
}
