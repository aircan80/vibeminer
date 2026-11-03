using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Composition;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.Storage.IntegrationTests;

public sealed class SharedLibraryAcquisitionTests
{
    private static readonly CdnContext RouteA = new(new CdnRoute(["level3.blizzard.com"], "tpr/wow"));
    private static readonly CdnContext RouteB = new(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"));
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vibeminer-shared-acquisition-" + Guid.NewGuid().ToString("N"));
        public readonly FakeCdn Cdn = new(); public ObjectStore Library = null!; public LibraryObjectAccessService Service = null!;
        public static async Task<Fixture> OpenAsync(ObjectSourceOptions? options = null, LibraryObjectAccessOptions? limits = null, ObjectStoreOptions? storage = null)
        {
            var f = new Fixture(); f.Library = await ObjectStore.OpenLibraryAsync(f.DirectoryPath, storage ?? new() { CommitDelay = TimeSpan.FromMilliseconds(5) });
            f.Service = new(f.Library, new ScriptedCdnClient(f.Cdn), options, limits); return f;
        }
        public void Serve(PutObject item) => Cdn.Add(CdnObjectKind.Data, new(item.Key.KeyBytes()), item.Bytes.ToArray());
        public ILibraryObjectSource View(string owner) => Service.ForOwner(owner, LibraryOwnerScope.Build, "download");
        public LibraryObjectOwner Owner(string owner) => Library.ForOwner(owner, LibraryOwnerScope.Build, "download", LibraryObjectRole.Original);
        public async ValueTask DisposeAsync() { await Service.DisposeAsync(); await Library.DisposeAsync(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }
    private static async Task UntilAsync(Func<bool> condition)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(5, deadline.Token); }
    private static void Damage(Fixture fixture, PutObject item)
    {
        var pack = Assert.Single(Directory.GetFiles(Path.Combine(fixture.DirectoryPath, "packs"), "*.vmp")); var bytes = File.ReadAllBytes(pack);
        var at = bytes.AsSpan().IndexOf(item.Bytes.Span[..32]); Assert.True(at > 0); bytes[at + 40] ^= 0x40; File.WriteAllBytes(pack, bytes);
    }

    [Fact]
    public async Task JoinedOwnersUseOneActualRouteTransferAndPrivateCallerBytesWithDurableClaims()
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22610, 2000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b");
        var first = a.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteB, FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); release.TrySetResult(); var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(results, result => { Assert.True(result.IsPresent); Assert.Equal(ObjectOrigin.Cdn, result.Origin); }); Assert.Equal(ServedFrom.Store, results[1].ServedFrom);
            Assert.Equal(item.Bytes.ToArray(), results[0].Bytes); Assert.Equal(item.Bytes.ToArray(), results[1].Bytes); Assert.NotSame(results[0].Bytes, results[1].Bytes);
            results[0].Bytes![100] ^= 1; Assert.Equal(item.Bytes.ToArray(), results[1].Bytes); Assert.Equal(item.Bytes.ToArray(), (await f.Library.ReadAsync(item.Key)).Bytes);
            Assert.True(await f.Owner("a").OwnsAsync(item.Key)); Assert.True(await f.Owner("b").OwnsAsync(item.Key));
            Assert.Equal("level3.blizzard.com", Assert.Single(f.Cdn.Requests).Host); Assert.Equal(1, (await f.Library.StatsAsync()).Objects);
            await f.Service.DisposeAsync(); await f.Library.DisposeAsync();
            await using var reopened = await ObjectStore.OpenLibraryAsync(f.DirectoryPath); Assert.True(await reopened.ForOwner("a", LibraryOwnerScope.Build, "download", LibraryObjectRole.Original).OwnsAsync(item.Key));
            Assert.True(await reopened.ForOwner("b", LibraryOwnerScope.Build, "download", LibraryObjectRole.Original).OwnsAsync(item.Key));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task JoinedFailureDoesNotInventA404ObservationForAnUncontactedConsumerRoute()
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22611, 2000); f.Serve(item);
        f.Cdn.MissingOn("level3.blizzard.com", CdnObjectKind.Data, new(item.Key.KeyBytes()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b"); var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); release.TrySetResult(); var results = await Task.WhenAll(first, second);
            Assert.Equal(ObjectState.NotFound, results[0].State); Assert.NotNull(results[0].ObservedAt);
            Assert.Equal(ObjectState.Unavailable, results[1].State); Assert.Equal(LibraryObjectAccessCodes.ContextUntried, results[1].Code); Assert.Null(results[1].ObservedAt);
            Assert.DoesNotContain(f.Cdn.Requests, request => request.Host == "us.cdn.blizzard.com"); Assert.False(await f.Owner("b").OwnsAsync(item.Key));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingOrDisposingOneOwnerLeavesTheOtherTransferConsumerAndOnlyLivePublicationOwner(bool dispose)
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22612, 3000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var stopped = 0;
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { Interlocked.Increment(ref stopped); throw; } };
        var a = f.View("a"); await using var b = f.View("b"); using var cancel = new CancellationTokenSource();
        var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1);
            if (dispose) await a.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); else cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, stopped); Assert.False(second.IsCompleted);
            release.TrySetResult(); Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent);
            Assert.False(await f.Owner("a").OwnsAsync(item.Key)); Assert.True(await f.Owner("b").OwnsAsync(item.Key)); Assert.Single(f.Cdn.Requests);
        }
        finally { release.TrySetResult(); await a.DisposeAsync(); }
    }

    [Fact]
    public async Task FinalConsumerCancellationDrainsBeforeReturnAndAFreshOwnerCanRetry()
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22613, 2000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); } };
        await using var a = f.View("a"); using var cancel = new CancellationTokenSource(); var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(drained.Task.IsCompleted); Assert.Equal(0, f.Service.ActiveFlights); Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key));
        f.Cdn.BeforeAnswer = null; await using var b = f.View("b"); Assert.True((await b.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None)).IsPresent); Assert.True(await f.Owner("b").OwnsAsync(item.Key));
    }

    [Fact]
    public async Task InteractiveOwnerPromotesTheSharedQueuedBackgroundKeyWithoutASecondRouteTransfer()
    {
        await using var f = await Fixture.OpenAsync(new() { Streams = new() { MinBackground = 1, MaxBackground = 1, Interactive = 1 } });
        var held = Synthetic.Headerless(22614, 1000); var queued = Synthetic.Headerless(22615, 1000); f.Serve(held); f.Serve(queued);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (request, ct) => { if (request.Key != new CdnKey(held.Key.KeyBytes())) return; entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var bulk = f.View("bulk"); await using var screen = f.View("screen"); var first = bulk.GetKeyAsync(new(held.Key), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var background = bulk.GetKeyAsync(new(queued.Key), RouteA, FetchPriority.Background, CancellationToken.None);
            await UntilAsync(() => f.Service.Streams.BackgroundQueued == 1); var interactive = screen.GetKeyAsync(new(queued.Key), RouteB, FetchPriority.Interactive, CancellationToken.None);
            Assert.True((await interactive.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); Assert.False(first.IsCompleted);
            Assert.True(await f.Owner("screen").OwnsAsync(queued.Key)); Assert.Equal("level3.blizzard.com", Assert.Single(f.Cdn.Requests, request => request.Key == new CdnKey(queued.Key.KeyBytes())).Host);
            release.TrySetResult(); Assert.True((await first).IsPresent); Assert.True((await background).IsPresent);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task AJoinedWrongDeclaredLengthIsRefusedWithoutClaimingOrChangingTheValidPhysicalTransfer()
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22616, 2000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b"); var first = a.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var wrong = await b.GetKeyAsync(new(item.Key, item.DeclaredLength - 1), RouteB, FetchPriority.Interactive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ObjectState.Refused, wrong.State); Assert.Equal(ObjectRefusalCodes.SizeMismatch, wrong.Code); Assert.False(await f.Owner("b").OwnsAsync(item.Key));
            release.TrySetResult(); Assert.True((await first).IsPresent); Assert.Single(f.Cdn.Requests);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthOutOfRangeConsumerCannotOwnNewBytesAndAValidJoinedConsumerCan(bool joined)
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22631, 2000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b");
        var invalid = a.GetRangeAsync(new(item.Key), item.DeclaredLength + 1, new byte[10], RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Task<ObjectResult>? valid = null;
            if (joined)
            {
                valid = b.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteB, FetchPriority.Interactive, CancellationToken.None);
                await UntilAsync(() => f.Service.JoinedConsumers == 1);
            }
            release.TrySetResult(); await AssertRangeFailureAsync(invalid);
            Assert.False(await f.Owner("a").OwnsAsync(item.Key));
            if (valid is not null)
            {
                var result = await valid.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(result.IsPresent); Assert.Equal(item.Bytes.ToArray(), result.Bytes);
                Assert.True(await f.Owner("b").OwnsAsync(item.Key)); Assert.Equal(1, (await f.Library.StatsAsync()).Objects);
                Assert.Equal(item.Bytes.ToArray(), (await f.Library.ReadAsync(item.Key)).Bytes);
            }
            else { Assert.False(await f.Library.HasAsync(item.Key)); Assert.Equal(0, (await f.Library.StatsAsync()).Objects); }
            Assert.Equal("level3.blizzard.com", Assert.Single(f.Cdn.Requests).Host);
        }
        finally { release.TrySetResult(); }
    }
    private static async Task AssertRangeFailureAsync(Task<ObjectResult> operation)
    {
        ObjectResult result;
        try { result = await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (ArgumentOutOfRangeException) { return; }
        Assert.Equal(ObjectState.Refused, result.State); Assert.Null(result.Bytes); Assert.NotNull(result.Code);
    }

    [Fact]
    public async Task LargeHeaderlessTransferRetargetsItsCompletedVerifiedPartToTheLiveOwnerWithoutRefetch()
    {
        await using var f = await Fixture.OpenAsync(new() { InMemoryObjectBytes = 1 << 20 }); var item = Synthetic.Headerless(22617, 3 << 20); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b"); using var cancel = new CancellationTokenSource();
        var first = a.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteA, FetchPriority.Background, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteB, FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            release.TrySetResult(); var result = await second.WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(result.IsPresent); Assert.Null(result.Bytes); Assert.Equal(item.DeclaredLength, result.Length);
            Assert.False(await f.Owner("a").OwnsAsync(item.Key)); Assert.True(await f.Owner("b").OwnsAsync(item.Key)); Assert.Single(f.Cdn.Requests);
            var tail = new byte[100]; Assert.Equal(ReadState.Present, (await f.Library.ReadRangeAsync(item.Key, item.DeclaredLength - tail.Length, tail)).State);
            Assert.Equal(item.Bytes.Span[^100..].ToArray(), tail); Assert.Empty(Directory.GetFiles(Path.Combine(f.DirectoryPath, "staging")));
            await using var unknown = f.View("unknown"); var cached = await unknown.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None);
            Assert.True(cached.IsPresent); Assert.Null(cached.Bytes); Assert.Equal(item.DeclaredLength, cached.Length); Assert.True(await f.Owner("unknown").OwnsAsync(item.Key));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task FinalStreamingCancellationKeepsOnlyProvenChunksForAnotherOwnersResume()
    {
        await using var f = await Fixture.OpenAsync(new() { InMemoryObjectBytes = 1 << 20 }); var item = Synthetic.Chunked(22618, Enumerable.Repeat(256 << 10, 12).ToArray()); f.Serve(item);
        var resumableRoute = new CdnContext(new CdnRoute(["level3.blizzard.com", "us.cdn.blizzard.com"], "tpr/wow"));
        f.Cdn.BreakOnceAfter(CdnObjectKind.Data, new(item.Key.KeyBytes()), 1_000_000);
        var retrying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (request, ct) =>
        {
            if (request.Offset is null) return; retrying.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); }
        };
        await using var a = f.View("a"); using var cancel = new CancellationTokenSource();
        var first = a.GetKeyAsync(new(item.Key, item.DeclaredLength), resumableRoute, FetchPriority.Background, cancel.Token);
        await retrying.Task.WaitAsync(TimeSpan.FromSeconds(20)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(drained.Task.IsCompleted); Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key));
        f.Cdn.BeforeAnswer = null; var before = f.Cdn.Requests.Count; await using var b = f.View("b");
        var result = await b.GetKeyAsync(new(item.Key, item.DeclaredLength), resumableRoute, FetchPriority.Interactive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(result.IsPresent); Assert.Equal(ObjectOrigin.Unknown, result.Origin);
        var resumed = Assert.Single(f.Cdn.Requests.Skip(before)); Assert.Equal(12 + 24 * 12 + 3L * (256 << 10), resumed.Offset);
        Assert.True(await f.Owner("b").OwnsAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key));
    }

    [Fact]
    public async Task ReprovenCompleteStagingPublishesWithoutContactingTheNewRouteOrInventingNetworkOrigin()
    {
        await using var f = await Fixture.OpenAsync(new() { InMemoryObjectBytes = 1 << 20 }); var item = Synthetic.Headerless(22630, 3 << 20); f.Serve(item);
        await using (var abandoned = await f.Owner("a").StageAsync(item.Key, item.DeclaredLength)) await abandoned.AppendAsync(item.Bytes);
        await using var b = f.View("b"); var result = await b.GetKeyAsync(new(item.Key, item.DeclaredLength), RouteB, FetchPriority.Interactive, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(result.IsPresent); Assert.Equal(ServedFrom.Store, result.ServedFrom); Assert.Equal(ObjectOrigin.Unknown, result.Origin); Assert.Empty(f.Cdn.Requests);
        Assert.False(await f.Owner("a").OwnsAsync(item.Key)); Assert.True(await f.Owner("b").OwnsAsync(item.Key)); Assert.Null(result.Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownCorruptOrLostLibraryBytesFailTypedWithoutDeletingOrFetchingThemAgain(bool lost)
    {
        await using var f = await Fixture.OpenAsync(); var item = Synthetic.Headerless(22619, 2000); f.Serve(item); await f.Owner("seed").PutAsync([item], true);
        if (lost) File.Delete(Assert.Single(Directory.GetFiles(Path.Combine(f.DirectoryPath, "packs"), "*.vmp"))); else Damage(f, item);
        await using var view = f.View("a"); var result = await view.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Interactive, CancellationToken.None);
        Assert.Equal(ObjectState.Refused, result.State); Assert.Equal(lost ? LibraryObjectAccessCodes.Lost : LibraryObjectAccessCodes.Corrupt, result.Code);
        var ensure = await view.GetKeysAsync([new(item.Key), new(item.Key)], RouteB, FetchPriority.Background, null, CancellationToken.None);
        Assert.All(ensure, failure => Assert.Equal(result.Code, failure.Code)); Assert.Empty(f.Cdn.Requests); Assert.True(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key));
    }

    [Fact]
    public async Task KnownDamageCapacityNeverEvictsAnEntryOrTurnsOverflowIntoIndexOnlySuccess()
    {
        await using var f = await Fixture.OpenAsync(limits: new() { MaxKnownDamage = 1 });
        var first = Synthetic.Headerless(22620, 2000); var second = Synthetic.Headerless(22621, 2000); await f.Owner("seed").PutAsync([first, second], true); Damage(f, first); Damage(f, second);
        await using var a = f.View("a"); Assert.Equal(LibraryObjectAccessCodes.Corrupt, (await a.GetKeyAsync(new(first.Key), RouteA, FetchPriority.Interactive, CancellationToken.None)).Code);
        Assert.Equal(LibraryObjectAccessCodes.Corrupt, (await a.GetKeyAsync(new(second.Key), RouteA, FetchPriority.Interactive, CancellationToken.None)).Code);
        var again = await a.GetKeysAsync([new(first.Key), new(second.Key)], RouteA, FetchPriority.Background, null, CancellationToken.None);
        Assert.Equal(LibraryObjectAccessCodes.Corrupt, again[0].Code); Assert.Equal(LibraryObjectAccessCodes.DamageLimit, again[1].Code); Assert.All(again, result => Assert.False(result.IsPresent));
        Assert.Empty(f.Cdn.Requests); Assert.False(await f.Owner("a").OwnsAsync(second.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPayloadPublicationOrCacheAttributionNeverReturnsReadyOrFabricatesClaims(bool attribution)
    {
        var armed = 0; var baseOptions = new ObjectStoreOptions { CommitDelay = TimeSpan.FromMilliseconds(5) };
        var options = attribution ? LibraryAcquisitionFaults.AttributionFailure(baseOptions, () => Interlocked.Exchange(ref armed, 0) == 1)
            : LibraryAcquisitionFaults.PublicationFailure(baseOptions, () => Interlocked.Exchange(ref armed, 0) == 1);
        await using var f = await Fixture.OpenAsync(storage: options); var item = Synthetic.Headerless(22622, 2000); f.Serve(item);
        if (attribution) await f.Owner("seed").PutAsync([item], true); var captured = f.Owner("a"); await using var a = f.View("a"); Volatile.Write(ref armed, 1);
        var result = await a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Interactive, CancellationToken.None);
        Assert.Equal(ObjectState.StoreFailed, result.State); Assert.Null(result.Bytes); Assert.False(await captured.OwnsAsync(item.Key)); Assert.Equal(attribution, await f.Library.HasAsync(item.Key));
    }

    [Fact]
    public async Task ViewOperationFlightAndConsumerLimitsRefuseWithoutGrowingWorkOrClaims()
    {
        await using var f = await Fixture.OpenAsync(limits: new() { MaxViews = 2, MaxOperations = 3, MaxFlights = 1, MaxConsumersPerFlight = 1, MaxBatch = 1 });
        await using var a = f.View("a"); var b = f.View("b"); Assert.Equal(LibraryObjectAccessCodes.Busy, Assert.Throws<LibraryObjectAccessException>(() => f.View("c")).Code);
        await b.DisposeAsync(); await using var c = f.View("c"); var item = Synthetic.Headerless(22623, 2000); var other = Synthetic.Headerless(22624, 2000); f.Serve(item); f.Serve(other);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }; var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var duplicate = await c.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None); Assert.Equal(ObjectState.StoreFailed, duplicate.State); Assert.Equal(LibraryObjectAccessCodes.Busy, duplicate.Code);
            var overflow = await c.GetKeyAsync(new(other.Key), RouteA, FetchPriority.Background, CancellationToken.None); Assert.Equal(ObjectState.StoreFailed, overflow.State); Assert.Equal(LibraryObjectAccessCodes.Busy, overflow.Code);
            Assert.Equal(LibraryObjectAccessCodes.Limit, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => c.GetKeysAsync([new(item.Key), new(other.Key)], RouteA, FetchPriority.Background, null, CancellationToken.None))).Code);
            Assert.Single(f.Cdn.Requests); Assert.False(await f.Owner("c").OwnsAsync(item.Key)); release.TrySetResult(); Assert.True((await first).IsPresent);
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task BothViewsReceiveTheSinglePhysicalCommitBeforeJoinedAttributionFinishes()
    {
        var claimEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var claimRelease = new ManualResetEventSlim(); var armed = 1;
        var options = LibraryAcquisitionFaults.AttributionGate(new() { CommitDelay = TimeSpan.FromMilliseconds(5) }, () =>
        {
            if (Interlocked.Exchange(ref armed, 0) != 1) return; claimEntered.TrySetResult();
            if (!claimRelease.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Attribution/feed test was not released.");
        });
        await using var f = await Fixture.OpenAsync(storage: options); var item = Synthetic.Headerless(22627, 2000); f.Serve(item);
        var networkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var networkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { networkEntered.TrySetResult(); await networkRelease.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b"); using var feedA = a.Subscribe(2); using var feedB = b.Subscribe(2); var ownerB = f.Owner("b");
        var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await networkEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); networkRelease.TrySetResult(); await claimEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var storedA = await feedA.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); var storedB = await feedB.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([item.Key], storedA.Stored); Assert.Equal([item.Key], storedB.Stored); Assert.Empty(storedA.Removed); Assert.Empty(storedB.Removed);
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); Assert.False(await ownerB.OwnsAsync(item.Key));
            claimRelease.Set(); Assert.All(await Task.WhenAll(first, second), result => Assert.True(result.IsPresent)); Assert.True(await ownerB.OwnsAsync(item.Key));
            await b.GetKeysAsync([new(item.Key), new(item.Key)], RouteB, FetchPriority.Background, null, CancellationToken.None);
            Assert.False(feedA.Reader.TryRead(out _)); Assert.False(feedB.Reader.TryRead(out _)); Assert.Single(f.Cdn.Requests);
        }
        finally { networkRelease.TrySetResult(); claimRelease.Set(); await first; }
    }

    [Fact]
    public async Task SharedOperationAdmissionIsFailFastAndServiceCloseDrainsFinalConsumerWithoutClosingLibrary()
    {
        await using var f = await Fixture.OpenAsync(limits: new() { MaxOperations = 1 }); var item = Synthetic.Headerless(22628, 2000); f.Serve(item);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); } };
        await using var a = f.View("a"); await using var b = f.View("b"); var first = a.GetKeyAsync(new(item.Key), RouteA, FetchPriority.Background, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LibraryObjectAccessCodes.Busy, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => b.HasKeysAsync([item.Key], CancellationToken.None))).Code);
        Assert.Equal(LibraryObjectAccessCodes.Busy, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => b.GetKeyAsync(new(item.Key), RouteB, FetchPriority.Interactive, CancellationToken.None))).Code);
        await f.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(drained.Task.IsCompleted); Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key));
        Assert.Equal(PutOutcome.Stored, Assert.Single(await f.Owner("unrelated").PutAsync([Synthetic.Headerless(22629, 1000)], true)).Outcome);
    }

    [Fact]
    public async Task CompositionDisposalFencesItsViewsButLeavesTheExplicitlyBorrowedLibraryUsable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibeminer-library-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var library = await ObjectStore.OpenLibraryAsync(directory); var item = Synthetic.Headerless(22625, 2000);
            await library.ForOwner("seed", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true);
            var composed = new LibraryAcquisitionService(library); var view = composed.Objects.ForOwner("a", LibraryOwnerScope.Build, "metadata");
            Assert.True((await view.GetKeyAsync(new(item.Key), null, FetchPriority.Interactive, CancellationToken.None)).IsPresent);
            await composed.DisposeAsync(); await composed.DisposeAsync();
            Assert.Equal(LibraryObjectAccessCodes.Closed, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => view.GetKeyAsync(new(item.Key), null, FetchPriority.Interactive, CancellationToken.None))).Code);
            Assert.True(await library.ForOwner("a", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original).OwnsAsync(item.Key));
            Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("other", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([Synthetic.Headerless(22626, 2000)], true)).Outcome);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
