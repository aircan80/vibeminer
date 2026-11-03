using VibeMiner.Cdn;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>Shared consumer-view semantics over the real mixed library and the managed upper-layer acquisition fake.</summary>
public abstract class LibraryOwnerSourceContract : IAsyncLifetime
{
    protected static readonly CdnContext Context = new(new CdnRoute(["level3.blizzard.com"], "tpr/wow"));
    protected abstract ILibraryObjectAccess Service { get; }
    protected abstract Task OpenAsync();
    protected abstract long JoinedConsumers { get; }
    protected abstract int FetchCount { get; }
    protected abstract void BeforeFetch(Func<CancellationToken, Task> wait);
    protected abstract void BeforeFetchKey(Func<CdnKey, CancellationToken, Task> wait);
    protected abstract Task SeedArchiveAsync(CdnKey key, byte[] bytes);
    protected abstract void BeforeArchive(Func<CancellationToken, Task> wait);
    protected abstract IReadOnlyList<(CdnKey Key, long Offset, long Length)> ArchiveRequests { get; }
    protected abstract Task CloseAsync();
    protected abstract Task SeedAsync(PutObject item, bool stored, ObjectOrigin origin = ObjectOrigin.Cdn);
    protected abstract Task<bool> OwnsAsync(string owner, LibraryOwnerScope scope, string producer, ObjectKey key, LibraryObjectRole role = LibraryObjectRole.Original);
    public Task InitializeAsync() => OpenAsync(); public Task DisposeAsync() => CloseAsync();

    [Fact]
    public async Task TwoOwnersJoinOnePhysicalFetchWithPrivateBytesAndCompletedClaims()
    {
        var item = Synthetic.Headerless(2260, 2000); await SeedAsync(item, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeforeFetch(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "source"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "source");
        var first = a.GetKeyAsync(new(item.Key, item.DeclaredLength), Context, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var routeB = new CdnContext(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow")); var second = b.GetKeyAsync(new(item.Key, item.DeclaredLength), routeB, FetchPriority.Interactive, CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (JoinedConsumers != 1) await Task.Delay(5, deadline.Token);
            release.TrySetResult(); var results = await Task.WhenAll(first, second);
            Assert.All(results, result => { Assert.True(result.IsPresent); Assert.Equal(ObjectOrigin.Cdn, result.Origin); }); Assert.Equal(ServedFrom.Store, results[1].ServedFrom);
            Assert.NotSame(results[0].Bytes, results[1].Bytes); results[0].Bytes![10] ^= 1; Assert.Equal(item.Bytes.ToArray(), results[1].Bytes);
            Assert.Equal(1, FetchCount); Assert.True(await OwnsAsync("a", LibraryOwnerScope.Build, "source", item.Key)); Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "source", item.Key));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Fact]
    public async Task ColdBatchCompletesLowerOrderBeforeStartingHigherOrderAndRetainsInputSlots()
    {
        var high = Synthetic.Headerless(2273, 1000); var low = Synthetic.Headerless(2274, 1000); await SeedAsync(high, false); await SeedAsync(low, false);
        var entered = new TaskCompletionSource<CdnKey>(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var higherStartedEarly = false;
        BeforeFetchKey(async (key, ct) =>
        {
            entered.TrySetResult(key);
            if (key.Equals(new CdnKey(low.Key.KeyBytes()))) await release.Task.WaitAsync(ct);
            else if (!release.Task.IsCompleted) higherStartedEarly = true;
        });
        await using var view = Service.ForOwner("a", LibraryOwnerScope.Build, "batch");
        var batch = view.GetKeysAsync([new(high.Key, high.DeclaredLength, Order: 9), new(low.Key, low.DeclaredLength, Order: 0), new(high.Key, high.DeclaredLength, Order: 9)], Context, FetchPriority.Background, null, CancellationToken.None);
        try
        {
            Assert.Equal(new CdnKey(low.Key.KeyBytes()), await entered.Task.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(1, FetchCount);
            release.TrySetResult(); var results = await batch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(higherStartedEarly); Assert.Equal([high.Key, low.Key, high.Key], results.Select(result => result.Key)); Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(2, FetchCount);
        }
        finally { release.TrySetResult(); await batch; }
    }

    [Fact]
    public async Task SourceMetadataBoundsRejectBeforeFetchingOrAttribution()
    {
        var item = Synthetic.Headerless(2278); var archive = new CdnKey(item.Key.KeyBytes());
        await using var view = Service.ForOwner("a", LibraryOwnerScope.Build, "source");
        var requests = new[]
        {
            (new ObjectRequest(item.Key, Locations: Enumerable.Repeat(new ArchiveLocation(archive, 0, 100), 65).ToArray()), Context),
            (new ObjectRequest(item.Key), new CdnContext(Context.Route, Enumerable.Repeat(archive, 8193).ToArray()))
        };
        foreach (var (request, context) in requests)
            Assert.Equal(LibraryObjectAccessCodes.Limit, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => view.GetKeyAsync(request, context, FetchPriority.Background, CancellationToken.None))).Code);
        Assert.Equal(0, FetchCount); Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "source", item.Key));
    }

    [Theory]
    [InlineData("archives")]
    [InlineData("locations")]
    [InlineData("length")]
    public async Task JoinedFailureUsesCompleteFrozenSourceIdentityInsteadOfInventingTheJoiners404(string difference)
    {
        var key = Synthetic.Headerless(2275).Key; var x = new CdnKey(Synthetic.Headerless(2276).Key.KeyBytes()); var y = new CdnKey(Synthetic.Headerless(2277).Key.KeyBytes());
        CdnKey[] firstArchives = [x]; CdnKey[] secondArchives = [difference == "archives" ? y : x];
        ArchiveLocation[] firstLocations = difference == "locations" ? [new(x, 0, 1000)] : []; ArchiveLocation[] secondLocations = difference == "locations" ? [new(y, 0, 1000)] : [];
        var firstContext = new CdnContext(Context.Route, firstArchives); var secondContext = new CdnContext(Context.Route, secondArchives);
        var firstRequest = new ObjectRequest(key, Locations: firstLocations); var secondRequest = new ObjectRequest(key, difference == "length" ? 1000 : null, secondLocations);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeforeFetch(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "source"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "source");
        var first = a.GetKeyAsync(firstRequest, firstContext, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var second = b.GetKeyAsync(secondRequest, secondContext, FetchPriority.Interactive, CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (JoinedConsumers != 1) await Task.Delay(5, deadline.Token);
            // Change the joiner to match the original source after admission; its captured difference must survive.
            firstArchives[0] = y; secondArchives[0] = x;
            if (secondLocations.Length != 0) { firstLocations[0] = new(y, 100, 200); secondLocations[0] = new(x, 0, 1000); }
            release.TrySetResult(); var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ObjectState.NotFound, results[0].State); Assert.Equal(ObjectState.Unavailable, results[1].State);
            Assert.Equal(LibraryObjectAccessCodes.ContextUntried, results[1].Code); Assert.Null(results[1].ObservedAt);
            Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "source", key)); Assert.False(await OwnsAsync("b", LibraryOwnerScope.Build, "source", key));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedArchiveGroupsRetainOnlyRequestedMembersAndCutAroundCommittedMembers(bool committedMiddle)
    {
        var items = Enumerable.Range(22780, 4).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = new Scenario().Archive(22780, items);
        await SeedArchiveAsync(archive.Key, archive.Bytes); if (committedMiddle) await SeedAsync(items[1], true, ObjectOrigin.SharedStore);
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "archive");
        var requests = committedMiddle ? items.Take(3).Select(item => archive.Request(item)).ToArray() : new[] { archive.Request(items[2]), archive.Request(items[0]), archive.Request(items[2]) };
        var results = await a.GetKeysAsync(requests, new(Context.Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(requests.Select(request => request.Key), results.Select(result => result.Key)); Assert.All(results, result => Assert.True(result.IsPresent));
        if (committedMiddle)
        {
            Assert.Equal([(0L, 2000L), (4000L, 2000L)], ArchiveRequests.OrderBy(request => request.Offset).Select(request => (request.Offset, request.Length)));
            Assert.Equal(ObjectOrigin.SharedStore, results[1].Origin); Assert.Equal(ServedFrom.Store, results[1].ServedFrom);
        }
        else
        {
            Assert.Equal((archive.Key, 0L, 6000L), Assert.Single(ArchiveRequests)); Assert.Equal([false], await a.HasKeysAsync([items[1].Key], CancellationToken.None));
            Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "archive", items[1].Key));
        }
        Assert.Equal([false], await a.HasKeysAsync([items[3].Key], CancellationToken.None));
        foreach (var request in requests) Assert.True(await OwnsAsync("a", LibraryOwnerScope.Build, "archive", request.Key));
    }

    [Fact]
    public async Task CancelingAnArchiveBatchLeavesOnlyTheIndependentlyJoinedMembersLive()
    {
        var items = Enumerable.Range(22790, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = new Scenario().Archive(22790, items); await SeedArchiveAsync(archive.Key, archive.Bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var networkCanceled = false;
        BeforeArchive(async ct => { entered.TrySetResult(); try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { networkCanceled = true; throw; } });
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "archive"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "archive"); using var cancel = new CancellationTokenSource();
        var batch = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), new(Context.Route, [archive.Key]), FetchPriority.Background, null, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var survivor = b.GetKeyAsync(archive.Request(items[2]), new(Context.Route, [archive.Key]), FetchPriority.Interactive, CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (JoinedConsumers != 1) await Task.Delay(5, deadline.Token);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch).WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(networkCanceled); Assert.False(survivor.IsCompleted);
            release.TrySetResult(); var result = await survivor.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(result.IsPresent); Assert.Equal(ServedFrom.Store, result.ServedFrom); Assert.Equal(ObjectOrigin.Cdn, result.Origin);
            foreach (var item in items) Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "archive", item.Key));
            Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "archive", items[2].Key)); Assert.Equal([false, false, true], await b.HasKeysAsync(items.Select(item => item.Key).ToArray(), CancellationToken.None)); Assert.Single(ArchiveRequests);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task PurePresenceIsReadOnlyButWholeRangeAndHasBasedBatchHitsCreditEachExactCapturedConsumer()
    {
        var item = Synthetic.Headerless(2261, 100_000); var missing = Synthetic.Headerless(2262).Key; await SeedAsync(item, true, ObjectOrigin.SharedStore);
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "metadata"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Global, "tables");
        Assert.Equal([true, false, true], await a.HasKeysAsync([item.Key, missing, item.Key], CancellationToken.None));
        Assert.Equal(2, await a.CountPresentAsync([item.Key, missing, item.Key], CancellationToken.None)); Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key));
        var whole = await a.GetKeyAsync(new(item.Key, item.DeclaredLength), Context, FetchPriority.Interactive, CancellationToken.None);
        Assert.Equal(ObjectState.Present, whole.State); Assert.Equal(ServedFrom.Store, whole.ServedFrom); Assert.Equal(ObjectOrigin.SharedStore, whole.Origin); Assert.Equal(item.Bytes.ToArray(), whole.Bytes);
        Assert.True(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key)); Assert.False(await OwnsAsync("b", LibraryOwnerScope.Global, "tables", item.Key));
        var destination = new byte[100]; var range = await b.GetRangeAsync(new(item.Key, item.DeclaredLength), 65_500, destination, Context, FetchPriority.Interactive, CancellationToken.None);
        Assert.True(range.IsPresent); Assert.Equal(ServedFrom.Store, range.ServedFrom); Assert.Equal(ObjectOrigin.SharedStore, range.Origin); Assert.Equal(item.Bytes.Slice(65_500, 100).ToArray(), destination);
        Assert.True(await OwnsAsync("b", LibraryOwnerScope.Global, "tables", item.Key));
        await using var ensure = Service.ForOwner("a", LibraryOwnerScope.Build, "ensure");
        var results = await ensure.GetKeysAsync([new(item.Key), new(missing), new(item.Key)], Context, FetchPriority.Background, null, CancellationToken.None);
        Assert.Equal([item.Key, missing, item.Key], results.Select(result => result.Key)); Assert.Equal([ObjectState.Present, ObjectState.NotFound, ObjectState.Present], results.Select(result => result.State));
        Assert.All(results, result => Assert.Null(result.Bytes)); Assert.True(await OwnsAsync("a", LibraryOwnerScope.Build, "ensure", item.Key)); Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "ensure", missing));
        Assert.False(await OwnsAsync("a", LibraryOwnerScope.Global, "metadata", item.Key)); Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key, LibraryObjectRole.Derived));
    }
    [Fact]
    public async Task OrderedBatchDuplicatesSharePayloadsAndRetainIndependentConsumerClaims()
    {
        var a = Synthetic.Headerless(2263, 1000); var b = Synthetic.Headerless(2264, 2000); await SeedAsync(a, false); await SeedAsync(b, false);
        await using var first = Service.ForOwner("a", LibraryOwnerScope.Build, "batch"); await using var second = Service.ForOwner("b", LibraryOwnerScope.Build, "batch");
        var results = await first.GetKeysAsync([new(b.Key, b.DeclaredLength), new(a.Key, a.DeclaredLength), new(b.Key, b.DeclaredLength)], Context, FetchPriority.Background, null, CancellationToken.None);
        Assert.Equal([b.Key, a.Key, b.Key], results.Select(result => result.Key)); Assert.All(results, result => Assert.True(result.IsPresent));
        foreach (var item in new[] { a, b }) { Assert.True(await OwnsAsync("a", LibraryOwnerScope.Build, "batch", item.Key)); Assert.False(await OwnsAsync("b", LibraryOwnerScope.Build, "batch", item.Key)); }
        var hit = await second.GetKeysAsync([new(a.Key), new(b.Key), new(a.Key)], Context, FetchPriority.Interactive, null, CancellationToken.None);
        Assert.All(hit, result => { Assert.True(result.IsPresent); Assert.Equal(ServedFrom.Store, result.ServedFrom); });
        Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "batch", a.Key)); Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "batch", b.Key));
    }
    [Fact]
    public async Task ValidationAndPreCancellationNeverAttributeAnUnsuccessfulConsumer()
    {
        var item = Synthetic.Headerless(2265, 1000); await SeedAsync(item, true); await using var view = Service.ForOwner("a", LibraryOwnerScope.Build, "metadata");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => view.GetKeyAsync(new(item.Key), Context, FetchPriority.Interactive, canceled.Token));
        var wrong = await view.GetKeyAsync(new(item.Key, item.DeclaredLength - 1), Context, FetchPriority.Interactive, CancellationToken.None);
        Assert.Equal(ObjectState.Refused, wrong.State); Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => view.GetRangeAsync(new(item.Key, item.DeclaredLength), 999, new byte[2], Context, FetchPriority.Interactive, CancellationToken.None));
        Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key));
        Assert.Equal(LibraryObjectAccessCodes.Limit, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => view.GetKeysAsync(Enumerable.Repeat(new ObjectRequest(item.Key), 1025).ToArray(), Context, FetchPriority.Background, null, CancellationToken.None))).Code);
        Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "metadata", item.Key));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownLengthInvalidRangeCannotBecomeThePublisherOrGainMembership(bool joined)
    {
        var item = Synthetic.Headerless(2272, 2000); await SeedAsync(item, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeforeFetch(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "range"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "range");
        var invalid = a.GetRangeAsync(new(item.Key), item.DeclaredLength + 1, new byte[10], Context, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Task<ObjectResult>? valid = null;
            if (joined)
            {
                valid = b.GetKeyAsync(new(item.Key, item.DeclaredLength), Context, FetchPriority.Interactive, CancellationToken.None);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (JoinedConsumers != 1) await Task.Delay(5, deadline.Token);
            }
            release.TrySetResult();
            ObjectResult? failure = null;
            try { failure = await invalid.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (ArgumentOutOfRangeException) { }
            if (failure is not null) { Assert.Equal(ObjectState.Refused, failure.State); Assert.Null(failure.Bytes); Assert.NotNull(failure.Code); }
            Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "range", item.Key));
            if (valid is not null)
            {
                Assert.True((await valid.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "range", item.Key));
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => a.GetRangeAsync(new(item.Key), item.DeclaredLength + 1, new byte[10], Context, FetchPriority.Interactive, CancellationToken.None));
                Assert.False(await OwnsAsync("a", LibraryOwnerScope.Build, "range", item.Key));
            }
            else Assert.Equal([false], await a.HasKeysAsync([item.Key], CancellationToken.None));
            Assert.Equal(1, FetchCount);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task ViewDisposalRejectsItsNewCallsWithoutClosingTheServiceOrAnotherConsumer()
    {
        var item = Synthetic.Headerless(2266, 1000); await SeedAsync(item, false); var a = Service.ForOwner("a", LibraryOwnerScope.Build, "source"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "source");
        await a.DisposeAsync(); await a.DisposeAsync();
        Assert.Equal(LibraryObjectAccessCodes.Closed, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => a.GetKeyAsync(new(item.Key), Context, FetchPriority.Interactive, CancellationToken.None))).Code);
        Assert.True((await b.GetKeyAsync(new(item.Key), Context, FetchPriority.Interactive, CancellationToken.None)).IsPresent); Assert.True(await OwnsAsync("b", LibraryOwnerScope.Build, "source", item.Key));
        await Service.DisposeAsync(); Assert.Equal(LibraryObjectAccessCodes.Closed, Assert.Throws<LibraryObjectAccessException>(() => Service.ForOwner("c", LibraryOwnerScope.Build, "source")).Code);
    }    [Fact]
    public async Task BothConsumerViewsSeeOnePhysicalCommitAndMembershipOnlyHitsEmitNothing()
    {
        var item = Synthetic.Headerless(2267, 1000); await SeedAsync(item, false);
        await using var a = Service.ForOwner("a", LibraryOwnerScope.Build, "source"); await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "source");
        using var first = a.Subscribe(4); using var second = b.Subscribe(4);
        Assert.True((await a.GetKeyAsync(new(item.Key), Context, FetchPriority.Background, CancellationToken.None)).IsPresent);
        Assert.True((await b.GetKeyAsync(new(item.Key), Context, FetchPriority.Interactive, CancellationToken.None)).IsPresent);
        var x = await first.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); var y = await second.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([item.Key], x.Stored); Assert.Empty(x.Removed); Assert.Equal(x.Stored, y.Stored); Assert.Empty(y.Removed);
        Assert.False(first.Reader.TryRead(out _)); Assert.False(second.Reader.TryRead(out _)); Assert.False(first.TakeMissed()); Assert.False(second.TakeMissed());
    }
    [Fact]
    public async Task BoundedCommitQueueReportsMissedUpdatesAndContinuesAfterDrain()
    {
        var one = Synthetic.Headerless(2268, 1000); var two = Synthetic.Headerless(2269, 1000); var three = Synthetic.Headerless(2270, 1000);
        foreach (var item in new[] { one, two, three }) await SeedAsync(item, false);
        await using var view = Service.ForOwner("a", LibraryOwnerScope.Build, "source"); using var subscription = view.Subscribe(1);
        await view.GetKeyAsync(new(one.Key), Context, FetchPriority.Background, CancellationToken.None); await view.GetKeyAsync(new(two.Key), Context, FetchPriority.Background, CancellationToken.None);
        Assert.Equal([one.Key], (await subscription.Reader.ReadAsync()).Stored); Assert.True(subscription.TakeMissed()); Assert.False(subscription.TakeMissed());
        var range = await view.GetRangeAsync(new(three.Key, three.DeclaredLength), 0, new byte[10], Context, FetchPriority.Interactive, CancellationToken.None); Assert.True(range.IsPresent);
        Assert.Equal([three.Key], (await subscription.Reader.ReadAsync()).Stored); Assert.False(subscription.TakeMissed());
    }
    [Fact]
    public async Task SubscriptionBudgetsReturnOnDisposeAndViewShutdownWithoutClosingOtherViews()
    {
        var item = Synthetic.Headerless(2271, 1000); await SeedAsync(item, false); var a = Service.ForOwner("a", LibraryOwnerScope.Build, "source");
        var subscriptions = Enumerable.Range(0, 64).Select(_ => a.Subscribe(1)).ToArray();
        Assert.Equal(LibraryObjectAccessCodes.Busy, Assert.Throws<LibraryObjectAccessException>(() => a.Subscribe(1)).Code);
        Assert.Equal(LibraryObjectAccessCodes.Limit, Assert.Throws<LibraryObjectAccessException>(() => a.Subscribe(1025)).Code);
        subscriptions[0].Dispose(); subscriptions[0].Dispose(); using var replacement = a.Subscribe(1);
        await a.GetKeyAsync(new(item.Key), Context, FetchPriority.Background, CancellationToken.None);
        await a.DisposeAsync(); Assert.Equal(LibraryObjectAccessCodes.Closed, Assert.Throws<LibraryObjectAccessException>(() => a.Subscribe(1)).Code);
        await using var b = Service.ForOwner("b", LibraryOwnerScope.Build, "source"); var next = Enumerable.Range(0, 64).Select(_ => b.Subscribe(1)).ToArray();
        foreach (var subscription in subscriptions.Append(replacement)) { while (subscription.Reader.TryRead(out _)) { } await subscription.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
        await Service.DisposeAsync(); foreach (var subscription in next) { await subscription.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(10)); subscription.Dispose(); }
    }

}
