using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>The real object store and the real CDN client (over the fake CDN's HTTP front end), end to end.</summary>
public sealed class RealStoreIntegrationTests : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vm-objectaccess-" + Guid.NewGuid().ToString("N"));
    private readonly FakeCdn cdn = new() { Time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)) };
    private readonly List<IAsyncDisposable> open = [];
    private static readonly ObjectStoreOptions Quick = new() { CommitDelay = TimeSpan.FromMilliseconds(5) };

    public Task InitializeAsync() { Directory.CreateDirectory(root); return Task.CompletedTask; }
    public async Task DisposeAsync()
    {
        foreach (var s in open) await s.DisposeAsync();
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private async Task<ObjectStore> OpenAsync(string name)
    {
        var store = await ObjectStore.OpenAsync(Path.Combine(root, name), Quick);
        open.Add(store);
        return store;
    }

    private async Task<ReadOnlyObjectStore> OpenReadOnlyAsync(string name)
    {
        var store = await ObjectStore.OpenReadOnlyAsync(Path.Combine(root, name), Quick);
        open.Add(store);
        return store;
    }

    private ObjectSource Source(ObjectStore store, IObjectReader? shared = null, ObjectSourceOptions? options = null)
    {
        return new ObjectSource(store, store, store, shared,
            new CdnClient(new HttpClient(cdn.Handler()), new CdnClientOptions { Time = cdn.Time }),
            [new VersionServiceObservations(new VersionClient(new HttpClient(new Scenario.VersionHandler())))], ObjectVerification.Default, options);
    }

    private PutObject Serve(PutObject o)
    {
        var kind = o.Key.Type switch { ObjectKeyType.Config => CdnObjectKind.Config, ObjectKeyType.Encoded => CdnObjectKind.Data, _ => CdnObjectKind.Index };
        cdn.Add(kind, Scenario.KeyOf(o.Key), o.Bytes.ToArray());
        return o;
    }

    [Fact]
    public async Task ABuildsMetadataLandsInTheStoreAndAReopenedStoreNeedsNoRequests()
    {
        var store = await OpenAsync("own");
        var members = Enumerable.Range(0, 30).Select(i => Synthetic.Headerless(i, 2000 + i)).ToArray();
        var archiveKey = new CdnKey(Synthetic.Random(99, 16));
        var archive = members.SelectMany(m => m.Bytes.ToArray()).ToArray();
        cdn.Add(CdnObjectKind.Data, archiveKey, archive);
        long at = 0;
        var requests = new List<ObjectRequest>
        {
            new(Serve(Synthetic.Config(1)).Key), new(Serve(Synthetic.Config(2)).Key),
            new(Serve(Synthetic.Index(3, 500)).Key), new(Serve(Synthetic.Index(4, 200, offsetWidth: 0)).Key),
        };
        var loose = Serve(Synthetic.Chunked(5, 40_000, 30_000));
        requests.Add(new(loose.Key, loose.DeclaredLength));
        foreach (var m in members) { requests.Add(new(m.Key, m.DeclaredLength, [new(archiveKey, at, m.DeclaredLength)])); at += m.DeclaredLength; }
        var context = new CdnContext(Scenario.Route, [archiveKey]);

        var first = await Source(store).GetKeysAsync(requests, context, FetchPriority.Background, null, None);
        Assert.All(first, r => Assert.Equal(ObjectState.Present, r.State));
        Assert.Equal(6, cdn.Requests.Count);
        foreach (var r in requests)
        {
            var read = await store.ReadAsync(r.Key);
            Assert.Equal((ReadState.Present, ObjectOrigin.Cdn), (read.State, read.Info!.Origin));
        }

        await store.DisposeAsync(); open.Remove(store);
        var reopened = await OpenAsync("own");
        cdn.ClearRequests();
        var second = await Source(reopened).GetKeysAsync(requests, context, FetchPriority.Background, null, None);
        Assert.All(second, r => Assert.Equal((ObjectState.Present, ServedFrom.Store), (r.State, r.ServedFrom!.Value)));
        Assert.Empty(cdn.Requests);
        Assert.Equal(requests.Count, await Source(reopened).CountPresentAsync(requests.Select(r => r.Key).ToArray(), None));
    }

    [Fact]
    public async Task ALargeObjectIsStagedAndABrokenStreamResumesOnlyTheRestFromTheNextHost()
    {
        var store = await OpenAsync("own");
        var options = new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 };
        var big = Serve(Synthetic.Chunked(7, Enumerable.Repeat(256 * 1024, 12).ToArray()));   // 3 MiB in 12 chunks
        var key = Scenario.KeyOf(big.Key);
        cdn.BreakOnceAfter(CdnObjectKind.Data, key, 1_000_000);
        var source = Source(store, options: options);
        var context = new CdnContext(Scenario.Route);

        var resumed = await source.GetKeyAsync(new(big.Key, big.DeclaredLength), context, FetchPriority.Background, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (resumed.State, resumed.ServedFrom!.Value));
        Assert.Null(resumed.Bytes);
        Assert.Equal(2, cdn.Requests.Count);
        var second = cdn.Requests[1];
        var headerLength = 12 + 24 * 12;
        Assert.NotEqual(cdn.Requests[0].Host, second.Host);
        Assert.Equal(headerLength + 3L * 256 * 1024, second.Offset);   // the first three whole chunks were proven and kept
        Assert.Equal(big.DeclaredLength - second.Offset, second.Length);

        var into = new byte[4096];
        var range = await source.GetRangeAsync(new(big.Key, big.DeclaredLength), 2_000_000, into, context, FetchPriority.Interactive, None);
        Assert.True(range.IsPresent);
        Assert.Equal(big.Bytes.Slice(2_000_000, 4096).ToArray(), into);
        Assert.Equal(2, cdn.Requests.Count);
    }

    [Fact]
    public async Task ALargeObjectWithBadBytesOnOneHostResumesFromTheNextHost()
    {
        var store = await OpenAsync("own");
        var big = Serve(Synthetic.Chunked(9, Enumerable.Repeat(256 * 1024, 12).ToArray()));
        var key = Scenario.KeyOf(big.Key);
        cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, key);    // a byte in chunk 6
        var source = Source(store, options: new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 });

        var r = await source.GetKeyAsync(new(big.Key, big.DeclaredLength), new CdnContext(Scenario.Route), FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(big.Bytes.ToArray(), (await store.ReadAsync(big.Key)).Bytes);
        var headerLength = 12 + 24 * 12;
        Assert.Equal([(Scenario.L3, (long?)null), (Scenario.Us, headerLength + 5L * 256 * 1024)], cdn.Requests.Select(q => (q.Host, q.Offset)));
    }

    [Fact]
    public async Task ALargeObjectFailingOnlyTheWholeCheckOnOneHostRestartsOnTheNextHost()
    {
        var store = await OpenAsync("own");
        // Headerless BLTE has no chunk checks: a bad byte passes every append and fails only the whole-object MD5 at commit.
        var big = Serve(Synthetic.Headerless(10, 3 << 20));
        var key = Scenario.KeyOf(big.Key);
        cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, key);
        var source = Source(store, options: new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 });

        var r = await source.GetKeyAsync(new(big.Key, big.DeclaredLength), new CdnContext(Scenario.Route), FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));
        Assert.Equal(big.Bytes.ToArray(), (await store.ReadAsync(big.Key)).Bytes);
        Assert.Equal([(Scenario.L3, (long?)null), (Scenario.Us, (long?)null)], cdn.Requests.Select(q => (q.Host, q.Offset)));
    }

    [Fact]
    public async Task ALargeObjectFailingTheWholeCheckOnEveryHostIsRefused()
    {
        var store = await OpenAsync("own");
        var big = Serve(Synthetic.Headerless(11, 3 << 20));
        var key = Scenario.KeyOf(big.Key);
        cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, key).CorruptOn(Scenario.Us, CdnObjectKind.Data, key);
        var source = Source(store, options: new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 });

        var r = await source.GetKeyAsync(new(big.Key, big.DeclaredLength), new CdnContext(Scenario.Route), FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Refused, ObjectRefusalCodes.KeyMismatch), (r.State, r.Code));
        Assert.Equal([Scenario.L3, Scenario.Us], cdn.Requests.Select(q => q.Host));
        Assert.False(await store.HasAsync(big.Key));
    }

    [Fact]
    public async Task ALargeObjectWithBadBytesOnEveryHostIsRefused()
    {
        var store = await OpenAsync("own");
        var big = Serve(Synthetic.Chunked(9, Enumerable.Repeat(256 * 1024, 12).ToArray()));
        var key = Scenario.KeyOf(big.Key);
        cdn.CorruptOn(Scenario.L3, CdnObjectKind.Data, key).CorruptOn(Scenario.Us, CdnObjectKind.Data, key);
        var source = Source(store, options: new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 });

        var r = await source.GetKeyAsync(new(big.Key, big.DeclaredLength), new CdnContext(Scenario.Route), FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Refused, ObjectStoreCodes.StagingRejected), (r.State, r.Code));
        Assert.Equal([Scenario.L3, Scenario.Us], cdn.Requests.Select(q => q.Host));
        Assert.False(await store.HasAsync(big.Key));
    }

    [Fact]
    public async Task TheSharedDevStoreServesAndTheProfileCopyRecordsIt()
    {
        var sharedWriter = await OpenAsync("shared");
        var o = Synthetic.Chunked(8, 9000, 9000);
        var put = await sharedWriter.PutAsync([o], commitNow: true);
        Assert.Equal(PutOutcome.Stored, put[0].Outcome);
        var shared = await OpenReadOnlyAsync("shared");
        var own = await OpenAsync("own");
        var r = await Source(own, shared).GetKeyAsync(new(o.Key, o.DeclaredLength), new(Scenario.Route), FetchPriority.Interactive, None);
        Assert.Equal(ServedFrom.SharedStore, r.ServedFrom);
        Assert.Equal(o.Bytes.ToArray(), r.Bytes);
        Assert.Equal(ObjectOrigin.SharedStore, (await own.ReadAsync(o.Key)).Info!.Origin);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task TwoCallersForALargeSharedObjectShareOneCopy()
    {
        var sharedWriter = await OpenAsync("shared");
        var big = Synthetic.Chunked(31, Enumerable.Repeat(256 * 1024, 12).ToArray());   // 3 MiB: one staged copy
        Assert.Equal(PutOutcome.Stored, (await sharedWriter.PutAsync([big], commitNow: true))[0].Outcome);
        var shared = new GatedReader(await OpenReadOnlyAsync("shared"));
        var own = await OpenAsync("own");
        var source = Source(own, shared, new ObjectSourceOptions { InMemoryObjectBytes = 1 << 20 });
        ObjectRequest[] request = [new(big.Key, big.DeclaredLength)];

        var first = source.GetKeysAsync(request, new CdnContext(Scenario.Route), FetchPriority.Background, null, None);
        await shared.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));       // the first caller is mid-copy
        var second = source.GetKeysAsync(request, new CdnContext(Scenario.Route), FetchPriority.Background, null, None);
        await shared.SecondHas.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAny(second, Task.Delay(200));                          // a second staging would fail here
        shared.Release.SetResult();

        foreach (var r in await Task.WhenAll(first, second)) Assert.Equal(ObjectState.Present, r[0].State);
        Assert.Equal(1, shared.Copies);
        Assert.Equal(ObjectOrigin.SharedStore, (await own.ReadAsync(big.Key)).Info!.Origin);
        Assert.Empty(cdn.Requests);
    }

    /// <summary>Holds the first range read until released, and says when a second caller has asked for the key.</summary>
    private sealed class GatedReader(IObjectReader inner) : IObjectReader
    {
        public readonly TaskCompletionSource Reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondHas = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Copies;
        private int has;

        public async ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default)
        {
            var answer = await inner.HasAsync(key, ct);
            if (Interlocked.Increment(ref has) == 2) SecondHas.TrySetResult();
            return answer;
        }

        public ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default) => inner.ReadAsync(key, ct);

        public async ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            if (offset == 0 && Interlocked.Increment(ref Copies) == 1) { Reading.TrySetResult(); await Release.Task.WaitAsync(ct); }
            return await inner.ReadRangeAsync(key, offset, destination, ct);
        }
    }

    [Fact]
    public async Task ObservationsGoIntoTheRealHistoryLog()
    {
        var store = await OpenAsync("own");
        var source = Source(store);
        var r = await source.ObserveAsync(new(ObservationKind.Versions, "wow", "eu"), None);
        Assert.True(r.Recorded);
        var entry = Assert.Single(await source.ReadHistoryAsync(new(Product: "wow"), None));
        Assert.Equal(("eu", HistoryKind.Versions), (entry.Region, entry.Kind));
        var document = await source.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, None);
        Assert.Equal(r.Observation!.Bytes, document.Bytes);
    }
}
