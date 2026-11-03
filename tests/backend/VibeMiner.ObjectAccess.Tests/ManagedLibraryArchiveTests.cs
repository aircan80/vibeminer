using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class ManagedLibraryArchiveTests
{
    private static readonly CdnRoute Route = new(["level3.blizzard.com"], "tpr/wow");
    private static SyntheticArchive Serve(InMemoryLibraryObjectAccess service, PutObject[] items)
    { var archive = new Scenario().Archive(22880, items); service.AddArchive(archive.Key, archive.Bytes); return archive; }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 65)]
    public void GroupAndMemberBoundsRejectInvalidManagedConfiguration(int groups, int members)
    { Assert.Throws<ArgumentException>(() => new InMemoryLibraryObjectAccess(limits: new() { MaxGroups = groups, MaxGroupMembers = members })); }
    [Fact]
    public async Task AdmissionRecheckCutsTheManagedArchiveAroundANewlyIndexedMember()
    {
        await using var service = new InMemoryLibraryObjectAccess(); var items = Enumerable.Range(22880, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = Serve(service, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeArchiveAdmission = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var view = service.ForOwner("a", LibraryOwnerScope.Build, "archive"); var batch = view.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), new(Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Empty(service.ArchiveRequests); service.Add(items[1], true, ObjectOrigin.SharedStore); release.TrySetResult();
            var results = await batch.WaitAsync(TimeSpan.FromSeconds(10)); Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(ObjectOrigin.SharedStore, results[1].Origin);
            Assert.Equal([(0L, 2000L), (4000L, 2000L)], service.ArchiveRequests.OrderBy(request => request.Offset).Select(request => (request.Offset, request.Length))); Assert.Equal(0, service.ActiveGroups);
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task GroupSlotsAreBoundedAndReusedOnlyAfterNetworkRetirement()
    {
        await using var service = new InMemoryLibraryObjectAccess(limits: new() { MaxGroups = 1, MaxGroupMembers = 2 }); var items = Enumerable.Range(22890, 4).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = Serve(service, items);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeRangeFetch = async (_, ct) => { entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); } };
        await using var a = service.ForOwner("a", LibraryOwnerScope.Build, "archive"); await using var b = service.ForOwner("b", LibraryOwnerScope.Build, "archive"); using var cancel = new CancellationTokenSource();
        var first = a.GetKeysAsync(items.Take(2).Select(item => archive.Request(item)).ToArray(), new(Route, [archive.Key]), FetchPriority.Background, null, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, service.ActiveGroups);
        var refused = await b.GetKeysAsync(items.Skip(2).Select(item => archive.Request(item)).ToArray(), new(Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None);
        Assert.All(refused, result => { Assert.Equal(ObjectState.StoreFailed, result.State); Assert.Equal(LibraryObjectAccessCodes.Busy, result.Code); }); Assert.Single(service.ArchiveRequests);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first).WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(drained.Task.IsCompleted); Assert.Equal(0, service.ActiveGroups);
        service.BeforeRangeFetch = null; Assert.All(await b.GetKeysAsync(items.Skip(2).Select(item => archive.Request(item)).ToArray(), new(Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None), result => Assert.True(result.IsPresent));
        Assert.Equal(2, service.ArchiveRequests.Count); Assert.False(service.Owns("a", LibraryOwnerScope.Build, "archive", items[0].Key));
    }
    [Fact]
    public async Task ManagedMergedIntegrityRefusesOnlyTheDamagedMemberWithoutGivingItMembership()
    {
        await using var service = new InMemoryLibraryObjectAccess(); var items = Enumerable.Range(22900, 3).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = Serve(service, items);
        var bytes = (byte[])archive.Bytes.Clone(); bytes[2100] ^= 1; service.AddArchive(archive.Key, bytes);
        await using var view = service.ForOwner("a", LibraryOwnerScope.Build, "archive"); var results = await view.GetKeysAsync(items.Select(item => archive.Request(item)).ToArray(), new(Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None);
        Assert.True(results[0].IsPresent); Assert.False(results[1].IsPresent); Assert.True(results[2].IsPresent); Assert.False(service.Owns("a", LibraryOwnerScope.Build, "archive", items[1].Key)); Assert.Single(service.ArchiveRequests);
    }
    [Fact]
    public async Task ManagedMemberWindowsStayBoundedAndKeepEveryResultIncludingDuplicates()
    {
        await using var service = new InMemoryLibraryObjectAccess(limits: new() { MaxGroups = 2, MaxGroupMembers = 2, MaxFlights = 4 }); var items = Enumerable.Range(22910, 7).Select(seed => Synthetic.Headerless(seed, 2000)).ToArray(); var archive = Serve(service, items);
        service.BeforeRangeFetch = (_, _) => { Assert.InRange(service.ActiveGroups, 1, 2); return Task.CompletedTask; };
        await using var view = service.ForOwner("a", LibraryOwnerScope.Build, "archive"); var requests = items.Select(item => archive.Request(item)).Append(archive.Request(items[0])).ToArray();
        var results = await view.GetKeysAsync(requests, new(Route, [archive.Key]), FetchPriority.Background, null, CancellationToken.None);
        Assert.All(results, result => Assert.True(result.IsPresent)); Assert.Equal(requests.Select(request => request.Key), results.Select(result => result.Key)); Assert.Equal(4, service.ArchiveRequests.Count); Assert.All(service.ArchiveRequests, request => Assert.InRange(request.Length, 1, 4000)); Assert.Equal(0, service.ActiveGroups);
    }
}
