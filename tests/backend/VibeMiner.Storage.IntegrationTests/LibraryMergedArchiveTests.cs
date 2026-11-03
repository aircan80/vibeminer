using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Tests;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.Storage.IntegrationTests;

public sealed partial class SharedLibraryAcquisitionTests
{
    private static SyntheticArchive ServeArchive(Fixture fixture, IReadOnlyList<PutObject> members, int gap = 0)
    {
        var scenario = new Scenario(); var archive = scenario.Archive(22700, members, gap);
        fixture.Cdn.Add(CdnObjectKind.Data, archive.Key, archive.Bytes); return archive;
    }
    private static CdnContext ArchiveContext(SyntheticArchive archive) => new(RouteA.Route, [archive.Key]);
    private sealed class CaptureProgress : IProgress<ObjectProgress>
    {
        private readonly object gate = new(); private readonly List<ObjectProgress> values = [];
        public void Report(ObjectProgress value) { lock (gate) values.Add(value); }
        public ObjectProgress[] Snapshot() { lock (gate) return values.ToArray(); }
    }

    [Fact]
    public async Task AdjacentArchiveMembersUseOneRangeAndOnlyRequestedKeysBecomeDurable()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22700, 4).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray();
        var archive = ServeArchive(f, items, gap: 17); var progress = new CaptureProgress(); await using var a = f.View("a");
        var results = await a.GetKeysAsync([archive.Request(items[2]), archive.Request(items[0]), archive.Request(items[2])], ArchiveContext(archive), FetchPriority.Background, progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([items[2].Key, items[0].Key, items[2].Key], results.Select(result => result.Key)); Assert.All(results, result => { Assert.True(result.IsPresent); Assert.Equal(ObjectOrigin.Cdn, result.Origin); Assert.Null(result.Bytes); });
        var range = Assert.Single(f.Cdn.Requests); Assert.Equal(archive.Key, range.Key); Assert.Equal(0L, range.Offset); Assert.Equal(3 * 2000L + 2 * 17, range.Length);
        Assert.Equal(2, (await f.Library.StatsAsync()).Objects);
        foreach (var i in new[] { 0, 2 }) { Assert.True(await f.Owner("a").OwnsAsync(items[i].Key)); Assert.Equal(items[i].Bytes.ToArray(), (await f.Library.ReadAsync(items[i].Key)).Bytes); }
        foreach (var i in new[] { 1, 3 }) { Assert.False(await f.Library.HasAsync(items[i].Key)); Assert.False(await f.Owner("a").OwnsAsync(items[i].Key)); }
        Assert.DoesNotContain(f.Cdn.Requests, request => request.Key != archive.Key);
        Assert.Equal(range.Length, progress.Snapshot().Max(value => value.CdnBytes)); Assert.Equal(3, progress.Snapshot()[^1].Done);
        await f.Service.DisposeAsync(); await f.Library.DisposeAsync(); await using var reopened = await ObjectStore.OpenLibraryAsync(f.DirectoryPath);
        Assert.Equal(2, (await reopened.StatsAsync()).Objects); Assert.True(await reopened.ForOwner("a", LibraryOwnerScope.Build, "download", LibraryObjectRole.Original).OwnsAsync(items[2].Key));
        using var metadata = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(f.DirectoryPath, "objects.sqlite3"), Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await metadata.OpenAsync(); using var claims = metadata.CreateCommand(); claims.CommandText = "SELECT COUNT(*) FROM library_membership WHERE owner<>'a' OR scope<>0 OR producer<>'download' OR role<>0";
        Assert.Equal(0L, (long)(await claims.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ACommittedMiddleMemberCutsTheArchiveRangeInsteadOfBeingDownloadedAgainAsGapBytes()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22710, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        await f.Owner("seed").PutAsync([items[1] with { Origin = ObjectOrigin.SharedStore }], true); await using var a = f.View("a");
        var results = await a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(ServedFrom.Store, results[1].ServedFrom); Assert.Equal(ObjectOrigin.SharedStore, results[1].Origin);
        Assert.Equal([(0L, 2000L), (4000L, 2000L)], f.Cdn.Requests.OrderBy(request => request.Offset).Select(request => (request.Offset!.Value, request.Length!.Value)));
        foreach (var item in items) Assert.True(await f.Owner("a").OwnsAsync(item.Key));
        Assert.Equal(ObjectOrigin.SharedStore, (await f.Library.ReadAsync(items[1].Key)).Info!.Origin);
    }

    [Fact]
    public async Task OneMemberCanLoseItsInitialOwnerWhileAnotherMemberAndAJoinedOwnerSurviveTheSameRange()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22720, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var networkCanceled = false;
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await release.Task.WaitAsync(ct); } catch (OperationCanceledException) { networkCanceled = true; throw; } };
        await using var a = f.View("a"); await using var b = f.View("b"); await using var c = f.View("c"); using var cancel = new CancellationTokenSource();
        var original = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = b.GetKeyAsync(archive.Request(items[0]), new(RouteB.Route, [archive.Key]), FetchPriority.Interactive, CancellationToken.None);
            var third = c.GetKeyAsync(archive.Request(items[2]), ArchiveContext(archive), FetchPriority.Background, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 2); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(networkCanceled); Assert.False(first.IsCompleted); Assert.False(third.IsCompleted); release.TrySetResult(); var survivors = await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(survivors, result => { Assert.True(result.IsPresent); Assert.Equal(ServedFrom.Store, result.ServedFrom); Assert.Equal(ObjectOrigin.Cdn, result.Origin); });
            foreach (var item in items) Assert.False(await f.Owner("a").OwnsAsync(item.Key));
            Assert.True(await f.Owner("b").OwnsAsync(items[0].Key)); Assert.True(await f.Owner("c").OwnsAsync(items[2].Key));
            Assert.False(await f.Library.HasAsync(items[1].Key)); Assert.Equal(2, (await f.Library.StatsAsync()).Objects); Assert.Equal("level3.blizzard.com", Assert.Single(f.Cdn.Requests).Host);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task FinalMergedConsumersCancellationDrainsTheNetworkAndReleasesTheGroupBeforeReturning()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22730, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); } };
        await using var a = f.View("a"); using var cancel = new CancellationTokenSource();
        var batch = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(drained.Task.IsCompleted); Assert.Equal(0, f.Service.ActiveFlights); Assert.Equal(0, f.Service.Streams.BackgroundActive); Assert.Equal(0, f.Service.Streams.InteractiveActive);
        Assert.Single(f.Cdn.Requests); foreach (var item in items) { Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key)); }
        f.Cdn.BeforeAnswer = null; await using var b = f.View("b"); Assert.All(await b.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None), result => Assert.True(result.IsPresent));
        Assert.Equal(2, f.Cdn.Requests.Count);
    }

    [Fact]
    public async Task InteractiveJoinPromotesTheWholeQueuedArchiveRangeWithoutStartingAnotherDownloader()
    {
        await using var f = await Fixture.OpenAsync(new() { Streams = new() { MinBackground = 1, MaxBackground = 1, Interactive = 1 } });
        var holding = Synthetic.Headerless(22740, 2000); f.Serve(holding); var items = Enumerable.Range(22741, 2).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (request, ct) => { if (request.Key.Equals(new CdnKey(holding.Key.KeyBytes()))) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var a = f.View("a"); await using var b = f.View("b"); var first = a.GetKeyAsync(new(holding.Key, holding.DeclaredLength), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var bulk = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
            await UntilAsync(() => f.Service.Streams.BackgroundQueued == 1); var interactive = b.GetKeyAsync(archive.Request(items[1]), ArchiveContext(archive), FetchPriority.Interactive, CancellationToken.None);
            Assert.True((await interactive.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); Assert.All(await bulk.WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.True(result.IsPresent)); Assert.False(first.IsCompleted);
            Assert.Single(f.Cdn.Requests, request => request.Key == archive.Key); Assert.True(await f.Owner("b").OwnsAsync(items[1].Key));
        }
        finally { release.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("short")]
    [InlineData("hash")]
    public async Task InvalidMergedRepliesNeverPublishAdjacentBytesUnderAnUnverifiedMembersKey(string failure)
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22750, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        if (failure == "ignored") f.Cdn.SetMode("level3.blizzard.com", FakeHostMode.IgnoresRanges);
        else if (failure == "short") f.Cdn.Add(CdnObjectKind.Data, archive.Key, archive.Bytes[..^1]);
        else { var bytes = (byte[])archive.Bytes.Clone(); bytes[2100] ^= 1; f.Cdn.Add(CdnObjectKind.Data, archive.Key, bytes); }
        await using var a = f.View("a"); var results = await a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < items.Length; i++)
        {
            if ((failure == "hash" && i != 1) || (failure == "short" && i < 2))
            { Assert.True(results[i].IsPresent); Assert.True(await f.Owner("a").OwnsAsync(items[i].Key)); Assert.Equal(items[i].Bytes.ToArray(), (await f.Library.ReadAsync(items[i].Key)).Bytes); }
            else { Assert.False(results[i].IsPresent); Assert.False(await f.Owner("a").OwnsAsync(items[i].Key)); Assert.False(await f.Library.HasAsync(items[i].Key)); }
        }
        Assert.All(f.Cdn.Requests, request => { Assert.Equal(archive.Key, request.Key); Assert.NotNull(request.Offset); });
        if (failure == "short")
            Assert.Equal([(0L, 6000L), (0L, 2000L), (2000L, 2000L), (4000L, 2000L)], f.Cdn.Requests.Select(request => (request.Offset!.Value, request.Length!.Value)));
        Assert.Equal(failure is "hash" or "short" ? 2 : 0, (await f.Library.StatsAsync()).Objects);
    }
    [Fact]
    public async Task FailedJoinedAttributionCannotReportReadyOrClaimTheJoinerAfterPhysicalPublication()
    {
        var armed = false; var storage = LibraryAcquisitionFaults.AttributionFailure(new() { CommitDelay = TimeSpan.FromMilliseconds(5) }, () => armed);
        await using var f = await Fixture.OpenAsync(storage: storage); var items = Enumerable.Range(22800, 2).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var ownerA = f.Owner("a"); var ownerB = f.Owner("b");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b");
        var bulk = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var joined = b.GetKeyAsync(archive.Request(items[0]), ArchiveContext(archive), FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); armed = true; release.TrySetResult();
            var failed = await joined.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(ObjectState.StoreFailed, failed.State); Assert.Null(failed.Bytes); Assert.False(await ownerB.OwnsAsync(items[0].Key));
            Assert.All(await bulk.WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.Equal(ObjectState.StoreFailed, result.State));
            Assert.True(await ownerA.OwnsAsync(items[0].Key)); Assert.True(await f.Library.HasAsync(items[0].Key)); Assert.Single(f.Cdn.Requests);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMemberLocationOrSizeFailsWithoutPublishingItsBytesAndOtherMembersRemainIndependent(bool sizeConflict)
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22810, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var requests = items.Select(item => archive.Request(item)).ToArray();
        requests[1] = sizeConflict ? requests[1] with { DeclaredLength = 2001 } : requests[1] with { Locations = [new(archive.Key, -1, 2000)] };
        await using var a = f.View("a"); var results = await a.GetKeysAsync(requests, ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ObjectState.Refused, results[1].State); Assert.Equal(sizeConflict ? RangePlanner.SizeConflict : RangePlanner.LocationInvalid, results[1].Code);
        Assert.False(await f.Library.HasAsync(items[1].Key)); Assert.False(await f.Owner("a").OwnsAsync(items[1].Key));
        foreach (var i in new[] { 0, 2 }) { Assert.True(results[i].IsPresent); Assert.True(await f.Owner("a").OwnsAsync(items[i].Key)); }
    }

    [Fact]
    public async Task AFailedGroupDoesNotInventAJoinedOwners404OrReadMutatedCallerSourceLists()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22820, 2).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        f.Cdn.MissingOn("level3.blizzard.com", CdnObjectKind.Data, archive.Key);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b"); CdnKey[] archivesB = [new(Synthetic.Random(22822, 16))];
        var bulk = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var joined = b.GetKeyAsync(archive.Request(items[0]), new(RouteA.Route, archivesB), FetchPriority.Interactive, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); archivesB[0] = archive.Key; release.TrySetResult();
            var result = await joined.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(ObjectState.Unavailable, result.State); Assert.Equal(LibraryObjectAccessCodes.ContextUntried, result.Code); Assert.Null(result.ObservedAt);
            Assert.All(await bulk.WaitAsync(TimeSpan.FromSeconds(10)), failure => Assert.False(failure.IsPresent)); Assert.False(await f.Owner("b").OwnsAsync(items[0].Key));
            Assert.All(f.Cdn.Requests, request => Assert.Equal(archive.Key, request.Key));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ServiceCloseDrainsOneMergedNetworkOperationAndLeavesTheBorrowedLibraryUsable()
    {
        await using var f = await Fixture.OpenAsync(); var items = Enumerable.Range(22830, 2).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); } };
        await using var a = f.View("a"); var bulk = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); await f.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bulk);
        Assert.True(drained.Task.IsCompleted); Assert.Equal(0, f.Service.ActiveFlights); Assert.Single(f.Cdn.Requests);
        foreach (var item in items) { Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("a").OwnsAsync(item.Key)); }
        Assert.Equal(LibraryObjectAccessCodes.Closed, (await Assert.ThrowsAsync<LibraryObjectAccessException>(() => a.GetKeyAsync(new(items[0].Key), RouteA, FetchPriority.Interactive, CancellationToken.None))).Code);
        var independent = Synthetic.Headerless(22832, 1000); Assert.Equal(PutOutcome.Stored, Assert.Single(await f.Owner("independent").PutAsync([independent], true)).Outcome);
    }

    [Fact]
    public async Task AdmissionRechecksCutAroundAMemberCommittedAfterTheBatchWasCaptured()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var limits = new LibraryObjectAccessOptions { Fault = point =>
        {
            if (point != LibraryGroupPoint.BeforeAdmission || Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Archive admission gate was not released.");
        } };
        await using var f = await Fixture.OpenAsync(limits: limits); var items = Enumerable.Range(22840, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items); var seed = f.Owner("seed");
        await using var a = f.View("a"); var bulk = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Empty(f.Cdn.Requests); await seed.PutAsync([items[1] with { Origin = ObjectOrigin.SharedStore }], true); release.Set();
            var results = await bulk.WaitAsync(TimeSpan.FromSeconds(10)); Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(ServedFrom.Store, results[1].ServedFrom); Assert.Equal(ObjectOrigin.SharedStore, results[1].Origin);
            Assert.Equal([(0L, 2000L), (4000L, 2000L)], f.Cdn.Requests.OrderBy(request => request.Offset).Select(request => (request.Offset!.Value, request.Length!.Value)));
            foreach (var item in items) Assert.True(await f.Owner("a").OwnsAsync(item.Key));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task GroupCapacityFailsOnlyNewMembersExplicitlyAndReturnsItsSlotAfterPhysicalRetirement()
    {
        await using var f = await Fixture.OpenAsync(limits: new() { MaxGroups = 1, MaxGroupMembers = 2 });
        var items = Enumerable.Range(22850, 4).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var a = f.View("a"); await using var b = f.View("b");
        var first = a.GetKeysAsync(items.Take(2).Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, f.Service.ActiveGroups);
            var refused = await b.GetKeysAsync(items.Skip(2).Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(refused, result => { Assert.Equal(ObjectState.StoreFailed, result.State); Assert.Equal(LibraryObjectAccessCodes.Busy, result.Code); }); Assert.Single(f.Cdn.Requests);
            foreach (var item in items.Skip(2)) { Assert.False(await f.Library.HasAsync(item.Key)); Assert.False(await f.Owner("b").OwnsAsync(item.Key)); }
            var joined = b.GetKeyAsync(archive.Request(items[0]), ArchiveContext(archive), FetchPriority.Interactive, CancellationToken.None); await UntilAsync(() => f.Service.JoinedConsumers == 1);
            release.TrySetResult(); Assert.True((await joined.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); Assert.All(await first.WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.True(result.IsPresent));
            await UntilAsync(() => f.Service.ActiveGroups == 0);
            Assert.All(await b.GetKeysAsync(items.Skip(2).Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None), result => Assert.True(result.IsPresent)); Assert.Equal(2, f.Cdn.Requests.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task LargeBatchesUseBoundedMemberWindowsAndCountEachGroupsActualWireBytesOnce()
    {
        await using var f = await Fixture.OpenAsync(limits: new() { MaxGroups = 2, MaxGroupMembers = 2, MaxFlights = 4 });
        var items = Enumerable.Range(22860, 7).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items); var progress = new CaptureProgress();
        f.Cdn.BeforeAnswer = (_, _) => { Assert.InRange(f.Service.ActiveGroups, 1, 2); Assert.InRange(f.Service.ActiveFlights, 1, 4); return Task.CompletedTask; };
        await using var a = f.View("a"); var requests = items.Select(item => archive.Request(item)).Append(archive.Request(items[0])).ToArray();
        var results = await a.GetKeysAsync(requests, ArchiveContext(archive), FetchPriority.Background, progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(requests.Select(request => request.Key), results.Select(result => result.Key));
        Assert.Equal(4, f.Cdn.Requests.Count); Assert.All(f.Cdn.Requests, request => { Assert.Equal(archive.Key, request.Key); Assert.InRange(request.Length!.Value, 1, 4000); });
        Assert.Equal(14_000, progress.Snapshot().Max(value => value.CdnBytes)); Assert.Equal(8, progress.Snapshot()[^1].Done); Assert.Equal(7, (await f.Library.StatsAsync()).Objects);
        await UntilAsync(() => f.Service.ActiveGroups == 0 && f.Service.ActiveFlights == 0);
    }

    [Fact]
    public async Task CancelingTheInitialBatchBeforeAdmissionLeavesOnlyLiveJoinedMembersInThePlan()
    {
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var limits = new LibraryObjectAccessOptions { Fault = point =>
        {
            if (point != LibraryGroupPoint.BeforeAdmission || Interlocked.Increment(ref calls) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Archive cancellation admission gate was not released.");
        } };
        await using var f = await Fixture.OpenAsync(limits: limits); var items = Enumerable.Range(22920, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        await using var a = f.View("a"); await using var b = f.View("b"); using var cancel = new CancellationTokenSource();
        var original = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var survivor = b.GetKeyAsync(archive.Request(items[2]), ArchiveContext(archive), FetchPriority.Interactive, CancellationToken.None); await UntilAsync(() => f.Service.JoinedConsumers == 1);
            cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original).WaitAsync(TimeSpan.FromSeconds(10)); Assert.Empty(f.Cdn.Requests); release.Set();
            Assert.True((await survivor.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); var request = Assert.Single(f.Cdn.Requests); Assert.Equal((4000L, 2000L), (request.Offset!.Value, request.Length!.Value));
            foreach (var item in items) Assert.False(await f.Owner("a").OwnsAsync(item.Key));
            Assert.Equal([false, false, true], await b.HasKeysAsync(items.Select(item => item.Key).ToArray(), CancellationToken.None)); Assert.True(await f.Owner("b").OwnsAsync(items[2].Key));
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 65)]
    public async Task InvalidGroupResourceBoundsFailBeforeNetworkOrLibraryMutation(int groups, int members)
    {
        await using var f = await Fixture.OpenAsync(); Assert.Throws<ArgumentException>(() => new LibraryObjectAccessService(f.Library, new ScriptedCdnClient(f.Cdn), limits: new() { MaxGroups = groups, MaxGroupMembers = members }));
        Assert.Empty(f.Cdn.Requests); Assert.Equal(0, (await f.Library.StatsAsync()).Objects);
    }

    [Fact]
    public async Task ActualStreamWaitRechecksCanceledMembersAndRequestsOnlyTheSurvivingBackgroundJoin()
    {
        await using var f = await Fixture.OpenAsync(new() { Streams = new() { MinBackground = 1, MaxBackground = 1, Interactive = 1 } });
        var holding = Synthetic.Headerless(22930, 2000); f.Serve(holding); var items = Enumerable.Range(22931, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var holdingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (request, ct) =>
        {
            if (request.Key.Equals(new CdnKey(holding.Key.KeyBytes()))) { holdingEntered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        await using var blocker = f.View("blocker"); await using var a = f.View("a"); await using var b = f.View("b"); using var cancel = new CancellationTokenSource();
        var occupying = blocker.GetKeyAsync(new(holding.Key, holding.DeclaredLength), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await holdingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var batch = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, cancel.Token);
            await UntilAsync(() => f.Service.Streams.BackgroundQueued == 1);
            var surviving = b.GetKeyAsync(archive.Request(items[2]), ArchiveContext(archive), FetchPriority.Background, CancellationToken.None);
            await UntilAsync(() => f.Service.JoinedConsumers == 1); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(surviving.IsCompleted); Assert.DoesNotContain(f.Cdn.Requests, request => request.Key == archive.Key);
            release.TrySetResult(); Assert.True((await surviving.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent); Assert.True((await occupying.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent);
            var range = Assert.Single(f.Cdn.Requests, request => request.Key == archive.Key); Assert.Equal((4000L, 2000L), (range.Offset!.Value, range.Length!.Value));
            foreach (var item in items) Assert.False(await f.Owner("a").OwnsAsync(item.Key));
            Assert.Equal([false, false, true], await b.HasKeysAsync(items.Select(item => item.Key).ToArray(), CancellationToken.None)); Assert.True(await f.Owner("b").OwnsAsync(items[2].Key));
            Assert.Equal(items[2].Bytes.ToArray(), (await f.Library.ReadAsync(items[2].Key)).Bytes);
        }
        finally { release.TrySetResult(); await occupying; }
    }

    [Fact]
    public async Task ActualStreamWaitRechecksNewlyCommittedMembersAndCutsTheRangeAroundTheirBytes()
    {
        await using var f = await Fixture.OpenAsync(new() { Streams = new() { MinBackground = 1, MaxBackground = 1, Interactive = 1 } });
        var holding = Synthetic.Headerless(22940, 2000); f.Serve(holding); var items = Enumerable.Range(22941, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = ServeArchive(f, items);
        var holdingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cdn.BeforeAnswer = async (request, ct) =>
        {
            if (request.Key.Equals(new CdnKey(holding.Key.KeyBytes()))) { holdingEntered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        await using var blocker = f.View("blocker"); await using var a = f.View("a"); var seed = f.Owner("seed");
        var occupying = blocker.GetKeyAsync(new(holding.Key, holding.DeclaredLength), RouteA, FetchPriority.Background, CancellationToken.None);
        try
        {
            await holdingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var batch = a.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), ArchiveContext(archive), FetchPriority.Background, null, CancellationToken.None);
            await UntilAsync(() => f.Service.Streams.BackgroundQueued == 1); Assert.DoesNotContain(f.Cdn.Requests, request => request.Key == archive.Key);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await seed.PutAsync([items[1] with { Origin = ObjectOrigin.SharedStore }], true)).Outcome);
            release.TrySetResult(); var results = await batch.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True((await occupying.WaitAsync(TimeSpan.FromSeconds(10))).IsPresent);
            Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(ServedFrom.Store, results[1].ServedFrom); Assert.Equal(ObjectOrigin.SharedStore, results[1].Origin);
            Assert.Equal([(0L, 2000L), (4000L, 2000L)], f.Cdn.Requests.Where(request => request.Key == archive.Key).OrderBy(request => request.Offset).Select(request => (request.Offset!.Value, request.Length!.Value)));
            foreach (var item in items) { Assert.True(await f.Owner("a").OwnsAsync(item.Key)); Assert.Equal(item.Bytes.ToArray(), (await f.Library.ReadAsync(item.Key)).Bytes); }
            Assert.True(await seed.OwnsAsync(items[1].Key)); Assert.False(await seed.OwnsAsync(items[0].Key)); Assert.False(await seed.OwnsAsync(items[2].Key));
        }
        finally { release.TrySetResult(); await occupying; }
    }

}
