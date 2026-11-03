using System.Collections.Concurrent;
using Xunit;
using static VibeMiner.Builds.Tests.BuildRegistryContractTests;
using static VibeMiner.Builds.Tests.BuildRegistryTestKit;

namespace VibeMiner.Builds.Tests;

public sealed class BuildRegistryInitializationTests
{
    private static bool IsDurabilityPoint(BuildRegistryPoint point) => point is BuildRegistryPoint.BeforeDirectorySync or BuildRegistryPoint.DirectorySynced
        or BuildRegistryPoint.BeforeAncestorSync or BuildRegistryPoint.AncestorSynced or BuildRegistryPoint.InitializationDurable;

    private static void AssertDurabilityOrder(string supplied, IReadOnlyList<BuildRegistryPoint> actual)
    {
        var expected = new List<BuildRegistryPoint> { BuildRegistryPoint.BeforeDirectorySync, BuildRegistryPoint.DirectorySynced };
        for (var ancestor = Directory.GetParent(supplied); ancestor is not null; ancestor = ancestor.Parent)
        {
            expected.Add(BuildRegistryPoint.BeforeAncestorSync); expected.Add(BuildRegistryPoint.AncestorSynced);
        }
        expected.Add(BuildRegistryPoint.InitializationDurable);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(nameof(BuildRegistryPoint.BeforeDirectorySync))]
    [InlineData(nameof(BuildRegistryPoint.BeforeAncestorSync))]
    public async Task OpenCannotAdmitWorkUntilMarkerAndNewDirectoryAncestorsAreDurable(string heldPoint)
    {
        using var root = new RegistryDirectory(); var supplied = Path.Combine(root.Path, "new", "nested", "registry");
        Assert.False(Directory.Exists(supplied)); var target = Enum.Parse<BuildRegistryPoint>(heldPoint);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var points = new ConcurrentQueue<BuildRegistryPoint>(); var holdOnce = 1;
        var options = new BuildRegistryOptions { Fault = point =>
        {
            if (IsDurabilityPoint(point)) points.Enqueue(point);
            if (point != target || Interlocked.Exchange(ref holdOnce, 0) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Initialization sync test was not released.");
        } };
        var opening = BuildRegistry.OpenAsync(supplied, options); BuildRegistry? registry = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(opening.IsCompleted);
            Assert.True(File.Exists(Path.Combine(supplied, BuildRegistry.FileName)));
            Assert.Equal(32, new FileInfo(Path.Combine(supplied, BuildRegistry.MarkerName)).Length);
            Assert.Equal(BuildRegistryCodes.Busy, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(supplied))).Code);
            release.Set(); registry = await opening.WaitAsync(TimeSpan.FromSeconds(10));
            AssertDurabilityOrder(supplied, points.ToArray()); Assert.Empty((await registry.ListAsync()).Entries);
            var observation = Observation(); await registry.ObserveAsync(observation);
            var attempt = await registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
            await registry.FinishAsync(attempt, BuildLoadState.Loaded);
        }
        finally { release.Set(); registry ??= await opening; await registry.DisposeAsync(); }
        await using var reopened = await BuildRegistry.OpenAsync(supplied);
        Assert.Equal(BuildLoadState.Loaded, (await reopened.GetAsync(Observation().Identity.Id))!.State);
    }

    [Theory]
    [InlineData(nameof(BuildRegistryPoint.BeforeDirectorySync))]
    [InlineData(nameof(BuildRegistryPoint.DirectorySynced))]
    [InlineData(nameof(BuildRegistryPoint.BeforeAncestorSync))]
    [InlineData(nameof(BuildRegistryPoint.AncestorSynced))]
    [InlineData(nameof(BuildRegistryPoint.InitializationDurable))]
    public async Task FailedInitializationDurabilityNeverReturnsAHandleAndRetryRepeatsTheEntireBarrier(string failedPoint)
    {
        using var root = new RegistryDirectory(); var supplied = Path.Combine(root.Path, "new", "nested", "registry");
        var target = Enum.Parse<BuildRegistryPoint>(failedPoint);
        var options = new BuildRegistryOptions { Fault = point => { if (point == target) throw new IOException("authored directory durability failure"); } };
        Assert.Equal(BuildRegistryCodes.Storage, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(supplied, options))).Code);
        Assert.True(File.Exists(Path.Combine(supplied, BuildRegistry.MarkerName)));
        var points = new ConcurrentQueue<BuildRegistryPoint>();
        var retryOptions = new BuildRegistryOptions { Fault = point => { if (IsDurabilityPoint(point)) points.Enqueue(point); } };
        await using var retried = await BuildRegistry.OpenAsync(supplied, retryOptions);
        AssertDurabilityOrder(supplied, points.ToArray()); Assert.Empty((await retried.ListAsync()).Entries);
        await retried.ObserveAsync(Observation());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedMetadataIsValidatedBeforeAnyDurabilityHookOrWritableMutation(bool newer)
    {
        using var directory = new RegistryDirectory();
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path)) await registry.ObserveAsync(Observation());
        if (newer) Mutate(directory.Database, "PRAGMA user_version=99");
        else File.WriteAllText(Path.Combine(directory.Path, BuildRegistry.MarkerName), "malformed marker");
        var before = HashFiles(directory.Path); var called = false;
        var options = new BuildRegistryOptions { Fault = point => { if (IsDurabilityPoint(point)) { called = true; throw new IOException("Unexpected sync before refusal."); } } };
        Assert.Equal(newer ? BuildRegistryCodes.Newer : BuildRegistryCodes.Unreadable,
            (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path, options))).Code);
        Assert.False(called); Assert.Equal(before, HashFiles(directory.Path));
    }
}
