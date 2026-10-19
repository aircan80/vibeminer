using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class RetentionServiceTests
{
    private static readonly RetentionTarget Target = new("wow", new string('a', 64), "us", new string('b', 64));
    private static readonly RetentionSetRequest Request = new(Target, RetentionAction.DownloadAll, 0);
    private static readonly RetentionSnapshot Saved = new(Target, "retention-1", 1, RetentionMode.DownloadAll,
        false, RetentionState.Queued, "downloadManifestAllVariants", 7, 800,
        new(0, 0, null, "notAssessed", null, 0, 0, 0, 0, 0), null, 0, 0, 0, 0, 1, null);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<object[]> InvalidTargets()
    {
        yield return [null!];
        yield return [Target with { ProductId = null! }];
        yield return [Target with { ProductId = "../wow" }];
        yield return [Target with { BuildIdentity = null! }];
        yield return [Target with { BuildIdentity = new string('A', 64) }];
        yield return [Target with { BuildIdentity = new string('a', 63) }];
        yield return [Target with { LedgerId = null! }];
        yield return [Target with { LedgerId = new string('B', 64) }];
        yield return [Target with { LedgerId = new string('b', 65) }];
        yield return [Target with { SourceRegion = null! }];
        yield return [Target with { SourceRegion = "US" }];
        yield return [Target with { SourceRegion = "us\n" }];
        yield return [Target with { SourceRegion = "us/path" }];
    }

    [Theory, MemberData(nameof(InvalidTargets))]
    public async Task Invalid_identity_never_reads_mutates_or_wakes_work(RetentionTarget target)
    {
        var store = new Store(); var driver = new Driver(); var service = new RetentionService(store, driver);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(target));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(Request with { Target = target }));
        Assert.Equal(0, store.Reads + store.Sets); Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Invalid_mutation_shape_is_a_typed_failure_before_durable_work()
    {
        var store = new Store(); var driver = new Driver(); var service = new RetentionService(store, driver);
        foreach (var request in new RetentionSetRequest[] { null!, Request with { Action = (RetentionAction)999 },
            Request with { Action = (RetentionAction)(-1) }, Request with { ExpectedRevision = -1 } })
        {
            var error = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(request));
            Assert.Equal("INVALID_REQUEST", error.Code);
        }
        Assert.Equal(0, store.Sets); Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Local_queries_forward_exact_context_and_do_not_restore_wake_or_wait_for_driver()
    {
        var restoreStarted = Signal(); var releaseRestore = Signal();
        var driver = new Driver { Restore = async _ => { restoreStarted.SetResult(); await releaseRestore.Task; } };
        var store = new Store(); var service = new RetentionService(store, driver);
        var restore = service.RestoreAsync(); await restoreStarted.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        Assert.Same(Saved, await service.ReadAsync(Target, cancellation.Token));
        Assert.Same(Target, store.LastTarget); Assert.Equal(cancellation.Token, store.ReadToken);
        Assert.Equal([Saved], await service.ListAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, store.ListToken); Assert.Equal(0, driver.Notifications);
        Assert.False(restore.IsCompleted);
        releaseRestore.SetResult(); await restore.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Pre_canceled_commands_and_queries_never_reach_store_or_driver()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var store = new Store(); var driver = new Driver(); var service = new RetentionService(store, driver);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Target, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetAsync(Request, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RestoreAsync(cancellation.Token));
        Assert.Equal(0, store.Reads + store.Lists + store.Sets + driver.Restores + driver.Notifications);
    }

    [Fact]
    public async Task Durable_ack_precedes_wakeup_and_does_not_await_job_execution()
    {
        var entered = Signal(); var commit = Signal(); var events = new List<string>();
        var store = new Store { Set = async (_, _) =>
        { entered.SetResult(); await commit.Task; events.Add("commit"); return Saved; } };
        var driver = new Driver { Notify = () => events.Add("notify") };
        var service = new RetentionService(store, driver);
        var command = service.SetAsync(Request); await entered.Task.WaitAsync(Deadline);
        Assert.False(command.IsCompleted); Assert.Equal(0, driver.Notifications);
        commit.SetResult(); Assert.Same(Saved, await command.WaitAsync(Deadline));
        Assert.Equal(["commit", "notify"], events);
        Assert.Equal(0, driver.Restores + driver.Shutdowns);
    }

    [Fact]
    public async Task Durable_commit_wins_request_cancellation_and_preserves_intent_revision()
    {
        using var cancellation = new CancellationTokenSource();
        var committed = Saved with { Revision = long.MaxValue, State = RetentionState.Running };
        var store = new Store { Set = (_, _) => { cancellation.Cancel(); return Task.FromResult(committed); } };
        var driver = new Driver(); var service = new RetentionService(store, driver);
        var request = Request with { Action = RetentionAction.Resume, ExpectedRevision = long.MaxValue };
        Assert.Same(committed, await service.SetAsync(request, cancellation.Token));
        Assert.Same(request, store.LastRequest); Assert.Equal(cancellation.Token, store.SetToken);
        Assert.Equal(1, driver.Notifications);
    }

    [Theory]
    [InlineData("RETENTION_STALE")]
    [InlineData("RETENTION_CAPACITY")]
    [InlineData("CATALOG_IO")]
    public async Task Failed_durable_mutation_never_signals_background_work(string code)
    {
        var failure = new DiscoveryException(code, "Authored storage failure.");
        var store = new Store { Set = (_, _) => Task.FromException<RetentionSnapshot>(failure) };
        var driver = new Driver(); var service = new RetentionService(store, driver);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(Request)));
        Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Canceled_mutation_waiter_does_not_change_durable_intent_or_notify()
    {
        var entered = Signal(); var commit = Signal();
        var store = new Store { Set = async (_, _) => { entered.SetResult(); await commit.Task; return Saved; } };
        var driver = new Driver(); var service = new RetentionService(store, driver);
        var first = service.SetAsync(Request); await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var second = service.SetAsync(Request with { Action = RetentionAction.Pause }, cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Deadline));
        Assert.Equal(1, store.Sets); Assert.Equal(0, driver.Notifications);
        commit.SetResult(); await first.WaitAsync(Deadline); Assert.Equal(1, driver.Notifications);
    }

    [Fact]
    public async Task Successful_restore_is_serialized_and_idempotent()
    {
        var entered = Signal(); var release = Signal();
        var driver = new Driver { Restore = async _ => { entered.SetResult(); await release.Task; } };
        var service = new RetentionService(new Store(), driver);
        var first = service.RestoreAsync(); await entered.Task.WaitAsync(Deadline);
        var second = service.RestoreAsync(); Assert.False(second.IsCompleted);
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
        await service.RestoreAsync(); Assert.Equal(1, driver.Restores); Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Failed_restore_does_not_claim_success_and_can_be_retried()
    {
        var attempts = 0; var failure = new DiscoveryException("CATALOG_IO", "Authored restore failure.");
        var driver = new Driver { Restore = _ => ++attempts == 1 ? Task.FromException(failure) : Task.CompletedTask };
        var service = new RetentionService(new Store(), driver);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.RestoreAsync()));
        await service.RestoreAsync(); await service.RestoreAsync(); Assert.Equal(2, driver.Restores);
    }

    [Fact]
    public async Task Shutdown_closes_admission_waits_accepted_commit_then_actual_driver_retirement()
    {
        var entered = Signal(); var commit = Signal(); var cleanupStarted = Signal(); var cleanup = Signal();
        var store = new Store { Set = async (_, _) => { entered.SetResult(); await commit.Task; return Saved; } };
        var driver = new Driver { Shutdown = async _ => { cleanupStarted.SetResult(); await cleanup.Task; } };
        var service = new RetentionService(store, driver);
        var command = service.SetAsync(Request); await entered.Task.WaitAsync(Deadline);
        var shutdown = service.ShutdownAsync(); Assert.False(shutdown.IsCompleted); Assert.Equal(0, driver.Shutdowns);
        var rejected = await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(Request));
        Assert.Equal("ENGINE_STOPPING", rejected.Code); Assert.True(rejected.Retryable);
        commit.SetResult(); Assert.Same(Saved, await command.WaitAsync(Deadline));
        await cleanupStarted.Task.WaitAsync(Deadline); Assert.Equal(1, driver.Notifications);
        Assert.False(shutdown.IsCompleted);
        Assert.Same(Saved, await service.ReadAsync(Target));
        cleanup.SetResult(); await shutdown.WaitAsync(Deadline); await service.ShutdownAsync();
        Assert.Equal(1, driver.Shutdowns); Assert.Equal(1, store.Sets);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.RestoreAsync())).Code);
    }

    [Fact]
    public async Task Interrupted_shutdown_remains_closed_and_retry_observes_cleanup()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var driver = new Driver(); var service = new RetentionService(new Store(), driver);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ShutdownAsync(cancellation.Token));
        Assert.Equal(0, driver.Shutdowns);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(Request))).Code);
        await service.ShutdownAsync(); Assert.Equal(1, driver.Shutdowns);
    }

    [Fact]
    public async Task Failed_driver_cleanup_is_observed_and_not_marked_retired_before_retry()
    {
        var attempts = 0; var failure = new DiscoveryException("STAGING_IO", "Authored cleanup failure.");
        var driver = new Driver { Shutdown = _ => ++attempts == 1 ? Task.FromException(failure) : Task.CompletedTask };
        var service = new RetentionService(new Store(), driver);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.ShutdownAsync()));
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.SetAsync(Request))).Code);
        await service.ShutdownAsync(); await service.ShutdownAsync(); Assert.Equal(2, driver.Shutdowns);
    }

    [Theory]
    [InlineData(RetentionState.Queued)] [InlineData(RetentionState.Preparing)]
    [InlineData(RetentionState.Running)] [InlineData(RetentionState.Waiting)]
    public async Task Parked_worker_fault_is_visible_queue_wide_without_changing_intent_or_waking_work(RetentionState state)
    {
        var value = Saved with { State = state };
        var problem = new RetentionProblem("STORAGE_FAILURE", "Saved download state needs a retry.", true);
        var driver = new Driver { Fault = new("an-older-already-settled-job", problem) };
        var service = new RetentionService(new Store { Value = value }, driver);
        var read = await service.ReadAsync(Target); var listed = Assert.Single(await service.ListAsync());
        foreach (var snapshot in new[] { read, listed })
        {
            Assert.Equal(RetentionState.Waiting, snapshot.State); Assert.Same(problem, snapshot.Problem);
            Assert.Equal(value, snapshot with { State = value.State, Problem = value.Problem, Progress = value.Progress });
        }
        Assert.Equal(0, driver.Notifications + driver.Restores);
    }

    [Theory]
    [InlineData(RetentionState.Completed)] [InlineData(RetentionState.Failed)]
    [InlineData(RetentionState.Paused)] [InlineData(RetentionState.OnDemand)]
    [InlineData(RetentionState.Stopping)]
    public async Task Unrelated_worker_fault_preserves_stable_states_and_withdrawn_intent(RetentionState state)
    {
        var value = Saved with { State = state, Paused = state == RetentionState.Paused,
            Mode = state == RetentionState.OnDemand ? RetentionMode.OnDemand : RetentionMode.DownloadAll };
        var driver = new Driver { Fault = new("another-job", new("STORAGE_FAILURE", "A worker is parked.", true)) };
        var service = new RetentionService(new Store { Value = value }, driver);
        Assert.Same(value, await service.ReadAsync(Target)); Assert.Same(value, Assert.Single(await service.ListAsync()));
        Assert.Equal(0, driver.Notifications);
    }

    [Fact]
    public async Task Matching_cleanup_fault_explains_stopping_without_reenabling_withdrawn_bulk()
    {
        var value = Saved with { Mode = RetentionMode.OnDemand, State = RetentionState.Stopping };
        var problem = new RetentionProblem("STORAGE_FAILURE", "Cleanup finished; saving its state needs a retry.", true);
        var service = new RetentionService(new Store { Value = value }, new Driver { Fault = new(value.JobId, problem) });
        var result = await service.ReadAsync(Target);
        Assert.Same(problem, result.Problem);
        Assert.Equal(value, result with { Problem = value.Problem });
    }

    [Fact]
    public async Task Idempotent_policy_command_wakes_recovery_and_returns_committed_intent_after_fault_clears()
    {
        var value = Saved with { State = RetentionState.Paused, Paused = true };
        var driver = new Driver { Fault = new(value.JobId, new("STORAGE_FAILURE", "A worker is parked.", true)) };
        driver.Notify = () => driver.Fault = null;
        var store = new Store { Value = value, Set = (_, _) => Task.FromResult(value) };
        var service = new RetentionService(store, driver);
        var ack = await service.SetAsync(new(Target, RetentionAction.Pause, value.Revision));
        Assert.Same(value, ack); Assert.True(ack.Paused); Assert.Equal(1, driver.Notifications);
        Assert.Equal(RetentionAction.Pause, store.LastRequest!.Action); Assert.Equal(0, store.Reads);
    }

    private sealed class Store : IRetentionStore
    {
        public RetentionSnapshot Value = Saved;
        public int Reads, Lists, Sets;
        public RetentionTarget? LastTarget;
        public RetentionSetRequest? LastRequest;
        public CancellationToken ReadToken, ListToken, SetToken;
        public Func<RetentionSetRequest, CancellationToken, Task<RetentionSnapshot>> Set = (_, _) => Task.FromResult(Saved);
        public Task<RetentionSnapshot> ReadAsync(RetentionTarget target, CancellationToken token)
        { Reads++; LastTarget = target; ReadToken = token; return Task.FromResult(Value); }
        public Task<IReadOnlyList<RetentionSnapshot>> ListAsync(CancellationToken token)
        { Lists++; ListToken = token; return Task.FromResult<IReadOnlyList<RetentionSnapshot>>([Value]); }
        public Task<RetentionSnapshot> SetAsync(RetentionSetRequest request, CancellationToken token)
        { Sets++; LastRequest = request; SetToken = token; return Set(request, token); }
    }

    private sealed class Driver : IRetentionDriver
    {
        public RetentionDriverFault? Fault;
        public RetentionDriverFault? ReadFault() => Fault;
        public int Restores, Notifications, Shutdowns;
        public Func<CancellationToken, Task> Restore = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> Shutdown = _ => Task.CompletedTask;
        public Action? Notify;
        public Task RestoreAsync(CancellationToken token) { Restores++; return Restore(token); }
        public void NotifyIntentChanged() { Notifications++; Notify?.Invoke(); }
        public Task ShutdownAsync(CancellationToken token) { Shutdowns++; return Shutdown(token); }
    }
}
