using Xunit;
using static VibeMiner.Builds.Tests.BuildRegistryTestKit;
using static VibeMiner.Builds.Tests.BuildRegistryContractTests;

namespace VibeMiner.Builds.Tests;

public sealed class BuildRegistryPersistenceTests
{
    [Fact]
    public async Task ReopenInterruptsOnlyAbandonedLoadingAndRetainsCompletedAndUndiscoveredEntries()
    {
        using var directory = new RegistryDirectory(); var first = Observation(); var loaded = Observation(2); var failed = Observation(3);
        BuildRegistryAttempt interrupted;
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path))
        {
            foreach (var observation in new[] { first, loaded, failed }) await registry.ObserveAsync(observation);
            interrupted = await registry.BeginLoadAsync(first.Identity.Id, first.Selection, "frFR");
            await registry.ObserveAsync(first with { Selection = Route(first.Identity, "eu", 'd'), Locale = "deDE", ObservedAt = T0.AddHours(1) });
            var success = await registry.BeginLoadAsync(loaded.Identity.Id, loaded.Selection, "enUS"); await registry.FinishAsync(success, BuildLoadState.Loaded);
            var failure = await registry.BeginLoadAsync(failed.Identity.Id, failed.Selection, "koKR"); await registry.FinishAsync(failure, BuildLoadState.Failed, new("AUTHORED_FAILURE", "synthetic metadata missing"));
        }
        await using var reopened = await BuildRegistry.OpenAsync(directory.Path);
        var restored = Assert.IsType<BuildRegistryEntry>(await reopened.GetAsync(first.Identity.Id));
        Assert.Equal(BuildLoadState.Interrupted, restored.State); Assert.Equal(1, restored.Generation); Assert.NotNull(restored.FinishedAt);
        Assert.Equal(first.Selection, restored.AttemptBuild); Assert.Equal("frFR", restored.AttemptLocale); Assert.Equal("eu", restored.ObservedBuild.Region);
        Assert.Equal(BuildLoadState.Loaded, (await reopened.GetAsync(loaded.Identity.Id))!.State);
        Assert.Equal(BuildLoadState.Failed, (await reopened.GetAsync(failed.Identity.Id))!.State); Assert.Equal(3, (await reopened.ListAsync()).Entries.Count);
        Assert.Equal(BuildRegistryCodes.Stale, (await Assert.ThrowsAsync<BuildRegistryException>(() => reopened.FinishAsync(interrupted, BuildLoadState.Loaded))).Code);
        var retry = await reopened.BeginLoadAsync(first.Identity.Id, restored.ObservedBuild, "deDE"); Assert.Equal(2, retry.Generation); await reopened.FinishAsync(retry, BuildLoadState.Loaded);
    }

    [Fact]
    public async Task SecondWriterIsRefusedBeforeItCanInterruptOrMutateTheFirstWritersAttempt()
    {
        using var directory = new RegistryDirectory(); var observation = Observation();
        await using var first = await BuildRegistry.OpenAsync(directory.Path); await first.ObserveAsync(observation);
        var token = await first.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"); var before = HashFiles(directory.Path);
        Assert.Equal(BuildRegistryCodes.Busy, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
        Assert.Equal(before, HashFiles(directory.Path)); Assert.Equal(BuildLoadState.Loading, (await first.GetAsync(observation.Identity.Id))!.State);
        await first.FinishAsync(token, BuildLoadState.Loaded);
    }

    [Fact]
    public async Task TokensAndCursorsCannotCrossRegistriesEvenWithTheSameCanonicalBuildAndGeneration()
    {
        using var a = new RegistryDirectory(); using var b = new RegistryDirectory(); var observation = Observation();
        await using var first = await BuildRegistry.OpenAsync(a.Path); await using var second = await BuildRegistry.OpenAsync(b.Path);
        foreach (var registry in new[] { first, second }) { await registry.ObserveAsync(observation); await registry.ObserveAsync(Observation(2)); }
        var firstAttempt = await first.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        var secondAttempt = await second.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS");
        Assert.Equal(firstAttempt.Generation, secondAttempt.Generation);
        Assert.Equal(BuildRegistryCodes.Stale, (await Assert.ThrowsAsync<BuildRegistryException>(() => second.FinishAsync(firstAttempt, BuildLoadState.Loaded))).Code);
        var cursor = (await first.ListAsync(1)).Next; Assert.NotNull(cursor);
        Assert.Equal(BuildRegistryCodes.Stale, (await Assert.ThrowsAsync<BuildRegistryException>(() => second.ListAsync(1, cursor))).Code);
        await second.FinishAsync(secondAttempt, BuildLoadState.Loaded); await first.FinishAsync(firstAttempt, BuildLoadState.Failed);
    }

    [Theory]
    [InlineData(nameof(BuildRegistryPoint.BeforeCommit))]
    [InlineData(nameof(BuildRegistryPoint.InsideCommit))]
    public async Task FailedBeginAndFinishTransactionsLeaveGenerationAndCurrentTokenRetryable(string pointName)
    {
        var point = Enum.Parse<BuildRegistryPoint>(pointName);
        using var directory = new RegistryDirectory(); var armed = false;
        var options = new BuildRegistryOptions { Fault = current => { if (armed && current == point) throw new IOException("authored transaction failure"); } };
        await using var registry = await BuildRegistry.OpenAsync(directory.Path, options); var observation = Observation(); await registry.ObserveAsync(observation);
        armed = true;
        Assert.Equal(BuildRegistryCodes.Storage, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"))).Code);
        armed = false; var unchanged = Assert.IsType<BuildRegistryEntry>(await registry.GetAsync(observation.Identity.Id));
        Assert.Equal(0, unchanged.Generation); Assert.Equal(BuildLoadState.Discovered, unchanged.State);
        var token = await registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"); Assert.Equal(1, token.Generation);
        armed = true;
        Assert.Equal(BuildRegistryCodes.Storage, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.FinishAsync(token, BuildLoadState.Loaded))).Code);
        armed = false; var loading = Assert.IsType<BuildRegistryEntry>(await registry.GetAsync(observation.Identity.Id));
        Assert.Equal(BuildLoadState.Loading, loading.State); Assert.Null(loading.FinishedAt); await registry.FinishAsync(token, BuildLoadState.Loaded);
    }

    [Theory]
    [InlineData(nameof(BuildRegistryPoint.BeforeCommit), false)]
    [InlineData(nameof(BuildRegistryPoint.InsideCommit), false)]
    [InlineData(nameof(BuildRegistryPoint.AfterCommit), true)]
    public async Task FailureAroundCommitReopensWithOnlyTheRealDurableAttempt(string pointName, bool committed)
    {
        var point = Enum.Parse<BuildRegistryPoint>(pointName);
        using var directory = new RegistryDirectory(); var armed = false; var observation = Observation();
        var options = new BuildRegistryOptions { Fault = current => { if (armed && current == point) throw new IOException("authored process interruption"); } };
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path, options))
        {
            await registry.ObserveAsync(observation); armed = true;
            Assert.Equal(BuildRegistryCodes.Storage, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"))).Code);
            armed = false;
        }
        await using var reopened = await BuildRegistry.OpenAsync(directory.Path);
        var entry = Assert.IsType<BuildRegistryEntry>(await reopened.GetAsync(observation.Identity.Id));
        Assert.Equal(committed ? BuildLoadState.Interrupted : BuildLoadState.Discovered, entry.State); Assert.Equal(committed ? 1 : 0, entry.Generation);
    }

    [Theory]
    [InlineData(nameof(BuildRegistryPoint.BeforeCommit), false)]
    [InlineData(nameof(BuildRegistryPoint.InsideCommit), true)]
    [InlineData(nameof(BuildRegistryPoint.AfterCommit), true)]
    public async Task CancellationAtPublicationBoundaryReturnsTheActualCommittedOutcome(string pointName, bool committed)
    {
        var point = Enum.Parse<BuildRegistryPoint>(pointName);
        using var directory = new RegistryDirectory(); using var canceled = new CancellationTokenSource(); var armed = false; var observation = Observation();
        var options = new BuildRegistryOptions { Fault = current => { if (armed && current == point) canceled.Cancel(); } };
        await using var registry = await BuildRegistry.OpenAsync(directory.Path, options); await registry.ObserveAsync(observation); armed = true;
        if (committed)
        {
            var token = await registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS", canceled.Token); Assert.Equal(1, token.Generation);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS", canceled.Token));
        var entry = Assert.IsType<BuildRegistryEntry>(await registry.GetAsync(observation.Identity.Id)); Assert.Equal(committed ? 1 : 0, entry.Generation);
        Assert.Equal(committed ? BuildLoadState.Loading : BuildLoadState.Discovered, entry.State);
    }

    [Fact]
    public async Task CloseRejectsNewCallsAndDrainsAcceptedWorkBeforeReleasingWriterExclusion()
    {
        using var directory = new RegistryDirectory(); var observation = Observation(); var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var options = new BuildRegistryOptions { Fault = point =>
        {
            if (!armed || point != BuildRegistryPoint.InsideCommit) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Registry close test was not released.");
        } };
        var registry = await BuildRegistry.OpenAsync(directory.Path, options); armed = true; var observing = registry.ObserveAsync(observation);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var closing = registry.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            Assert.Equal(BuildRegistryCodes.Closed, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.GetAsync(observation.Identity.Id))).Code);
            Assert.Equal(BuildRegistryCodes.Busy, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
            release.Set(); Assert.Equal(BuildLoadState.Discovered, (await observing.WaitAsync(TimeSpan.FromSeconds(10))).State); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await observing; await registry.DisposeAsync(); }
        await using var reopened = await BuildRegistry.OpenAsync(directory.Path); Assert.NotNull(await reopened.GetAsync(observation.Identity.Id));
    }

    [Fact]
    public async Task BoundedAdmissionRefusesOverflowAndShutdownDrainsEveryAcceptedCall()
    {
        using var directory = new RegistryDirectory(); var observation = Observation(); var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var options = new BuildRegistryOptions { Fault = point =>
        {
            if (!armed || point != BuildRegistryPoint.InsideCommit) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Admission test was not released.");
        } };
        var registry = await BuildRegistry.OpenAsync(directory.Path, options); armed = true;
        var observing = registry.ObserveAsync(observation); var queued = new List<Task<BuildRegistryEntry?>>();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 64; i++) queued.Add(registry.GetAsync(observation.Identity.Id));
            Assert.All(queued, task => Assert.False(task.IsCompleted));
            Assert.Equal(BuildRegistryCodes.Busy, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.GetAsync(observation.Identity.Id))).Code);
            var closing = registry.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            Assert.Equal(BuildRegistryCodes.Closed, (await Assert.ThrowsAsync<BuildRegistryException>(() => registry.GetAsync(observation.Identity.Id))).Code);
            release.Set(); await observing.WaitAsync(TimeSpan.FromSeconds(10));
            var drained = await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(64, drained.Length); Assert.All(drained, entry => Assert.Equal(observation.Identity.Id, Assert.IsType<BuildRegistryEntry>(entry).Identity.Id));
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await observing; await registry.DisposeAsync(); }
    }

    [Fact]
    public async Task InterruptedRecoveryFailureNeverAdmitsWorkAndTheNextOpenFinishesRecovery()
    {
        using var directory = new RegistryDirectory();
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path))
            foreach (var observation in new[] { Observation(), Observation(2) })
            { await registry.ObserveAsync(observation); await registry.BeginLoadAsync(observation.Identity.Id, observation.Selection, "enUS"); }
        var options = new BuildRegistryOptions { Fault = point => { if (point == BuildRegistryPoint.RecoveryInterrupted) throw new IOException("authored recovery interruption"); } };
        Assert.Equal(BuildRegistryCodes.Storage, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path, options))).Code);
        await using var recovered = await BuildRegistry.OpenAsync(directory.Path);
        var entries = (await recovered.ListAsync()).Entries; Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => { Assert.Equal(BuildLoadState.Interrupted, entry.State); Assert.Equal(1, entry.Generation); });
    }
}
