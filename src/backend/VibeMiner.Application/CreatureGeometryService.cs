using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Defensive application admissions. Production Host additionally retains shared Creature2/4
/// preparation/publication credits through terminal Flush. The store owns physical sharing and resource budgets.</summary>
public sealed partial class CreatureGeometryService : IAsyncDisposable
{
    public static readonly TimeSpan PreparationLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ReadLeaseLifetime = TimeSpan.FromSeconds(30);
    private readonly ICreatureGeometryStore store;
    private readonly TimeProvider clock;
    private readonly ITimer deadlineTimer;
    private readonly SemaphoreSlim retirementWake = new(0, 1);
    private readonly Task retirementWorker;
    private readonly object gate = new();
    private readonly HashSet<Operation> operations = [];
    private readonly HashSet<LeaseSlot> leases = [];
    private int preparations, localCalls;
    private bool closed;
    private Exception? cleanupFailure;
    private TaskCompletionSource? shutdown;
    private bool workerStopping;

    public CreatureGeometryService(ICreatureGeometryStore store, TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store)); clock = timeProvider ?? TimeProvider.System;
        deadlineTimer = clock.CreateTimer(_ => DeadlineTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        retirementWorker = RunRetirementsAsync();
    }

    private sealed class Operation
    {
        internal Operation(bool prepare, long started) { Prepare = prepare; Started = started; Cancellation = new(); Token = Cancellation.Token; }
        internal readonly bool Prepare;
        internal readonly object Gate = new();
        internal readonly CancellationTokenSource Cancellation;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationToken Token;
        internal TaskCompletionSource? Notification;
        internal readonly long Started;
        internal bool Finished, CleanupFailed;
        internal volatile bool Expired;
    }
    private Operation Admit(bool prepare, bool open, out LeaseSlot? slot)
    {
        lock (gate)
        {
            RequireOpen();
            if (prepare ? preparations == 2 : localCalls == 2)
                throw new DiscoveryException(prepare ? "CREATURES_BUSY" : "CREATURES_QUERY_BUSY", "Geometry work is at its bounded capacity or retiring.", true);
            if (open && leases.Count == 2) throw new DiscoveryException("CREATURE_GEOMETRY_BUSY", "Geometry read leases are at capacity or retiring.", true);
            var operation = new Operation(prepare, clock.GetTimestamp()); operations.Add(operation);
            if (prepare) preparations++; else localCalls++;
            slot = open ? new LeaseSlot(operation, operation.Started) : null;
            if (slot is not null) leases.Add(slot);
            ScheduleDeadline();
            return operation;
        }
    }
    public static void Validate(CreatureGeometryRequest request) => CreatureGeometryValidation.Validate(request);
    public static void Validate(CreatureGeometryReadRequest request) => CreatureGeometryValidation.Validate(request);
    public static void Validate(CreatureGeometryOpenRequest request) => CreatureGeometryValidation.Validate(request);
    public Task<CreatureGeometryResult> ReadAsync(CreatureGeometryReadRequest request, CancellationToken token = default)
    {
        Validate(request); return ExecuteAsync(request.Selection, false, null, token,
            (selection, _, ct) => store.ReadAsync(request with { Selection = selection }, ct), request.ExpectedGeometryArtifactId);
    }
    public Task<CreatureGeometryResult> PrepareAsync(CreatureGeometryRequest request,
        Func<CreatureGeometryProgress, Task>? progress = null, CancellationToken token = default)
    {
        Validate(request); return ExecuteAsync(request.Selection, true, progress, token,
            (selection, report, ct) => store.PrepareAsync(new(selection), report, ct), null);
    }
    private async Task<CreatureGeometryResult> ExecuteAsync(CreatureDisplayMetadataRequest selection, bool prepare,
        Func<CreatureGeometryProgress, Task>? progress, CancellationToken token,
        Func<CreatureDisplayMetadataRequest, Func<CreatureGeometryProgress, Task>, CancellationToken, Task<CreatureGeometryResult>> work,
        string? expectedArtifact)
    {
        token.ThrowIfCancellationRequested(); var operation = Admit(prepare, false, out _);
        CreatureGeometryProgressObserver? observer = null; CancellationTokenRegistration registration = default;
        CreatureGeometryResult? completed = null;
        try
        {
            var owned = CreatureGeometryValidation.Own(selection);
            registration = token.UnsafeRegister(_ => { _ = CancelAsync(operation); }, null);
            operation.Token.ThrowIfCancellationRequested();
            observer = progress is null ? null : new(progress, clock);
            Task Report(CreatureGeometryProgress value)
            { CreatureGeometryValidation.ValidateProgress(value); return observer?.ReportAsync(value) ?? Task.CompletedTask; }
            var result = await work(owned, Report, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            completed = CreatureGeometryValidation.Snapshot(result, owned, expectedArtifact);
        }
        catch (OperationCanceledException) when (operation.Expired && !token.IsCancellationRequested)
        { throw new DiscoveryException("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", "Geometry preparation reached its finite deadline.", true); }
        catch (DiscoveryException error) when (error.Code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal))
        { FailClosed(operation, error); throw Cleanup(); }
        finally { await FinishAsync(operation, registration, observer); }
        token.ThrowIfCancellationRequested();
        if (operation.Expired) throw new DiscoveryException("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", "Geometry preparation reached its finite deadline.", true);
        operation.Token.ThrowIfCancellationRequested(); return completed!;
    }
    // Caller holds gate. There is one service timer; all deadlines start at admission, not completion.
    private void ScheduleDeadline()
    {
        var remaining = Timeout.InfiniteTimeSpan;
        void Include(long started, TimeSpan lifetime)
        { var next = lifetime - clock.GetElapsedTime(started); if (next < TimeSpan.Zero) next = TimeSpan.Zero; if (remaining == Timeout.InfiniteTimeSpan || next < remaining) remaining = next; }
        foreach (var operation in operations) if (operation.Prepare && !operation.Finished && !operation.Expired) Include(operation.Started, PreparationLifetime);
        foreach (var slot in leases) if (!slot.Closing && !slot.Operation.Expired) Include(slot.Started, ReadLeaseLifetime);
        deadlineTimer.Change(remaining, Timeout.InfiniteTimeSpan);
    }
    private void DeadlineTick()
    {
        Operation[] due; LeaseSlot[] expired;
        lock (gate)
        {
            due = operations.Where(o => o.Prepare && !o.Finished && !o.Expired && clock.GetElapsedTime(o.Started) >= PreparationLifetime).ToArray();
            expired = leases.Where(s => !s.Closing && !s.Operation.Expired && clock.GetElapsedTime(s.Started) >= ReadLeaseLifetime).ToArray();
            foreach (var operation in due) operation.Expired = true;
            foreach (var slot in expired) slot.Operation.Expired = true;
            ScheduleDeadline();
        }
        foreach (var operation in due) _ = CancelAsync(operation);
        foreach (var slot in expired) StartRetirement(slot);
    }
    private static Task CancelAsync(Operation operation)
    {
        TaskCompletionSource completion; bool start;
        lock (operation.Gate)
        {
            if (operation.Finished) return Task.CompletedTask;
            start = operation.Notification is null;
            completion = operation.Notification ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = NotifyAsync(); return completion.Task;
        async Task NotifyAsync()
        {
            try { await operation.Cancellation.CancelAsync(); }
            catch (AggregateException) { }
            finally { completion.TrySetResult(); }
        }
    }
    private async Task FinishAsync(Operation operation, CancellationTokenRegistration registration, CreatureGeometryProgressObserver? observer)
    {
        try
        {
            await registration.DisposeAsync();
            if (observer is not null) await observer.CloseAsync();
        }
        catch (Exception error) { FailClosed(operation, error); throw Cleanup(); }
        finally
        {
            Task notification;
            lock (operation.Gate) { operation.Finished = true; notification = operation.Notification?.Task ?? Task.CompletedTask; }
            await notification; operation.Cancellation.Dispose();
            lock (gate)
            {
                if (!operation.CleanupFailed)
                { operations.Remove(operation); if (operation.Prepare) preparations--; else localCalls--; }
                ScheduleDeadline();
                operation.Retired.TrySetResult();
            }
        }
    }
    private void FailClosed(Operation operation, Exception error)
    { lock (gate) { operation.CleanupFailed = true; cleanupFailure ??= error; closed = true; } }
    private void RequireOpen()
    {
        if (cleanupFailure is not null) throw Cleanup();
        if (closed) throw new DiscoveryException("ENGINE_STOPPING", "Geometry work is stopping.", true);
    }
    private static DiscoveryException Cleanup() => new("CREATURE_GEOMETRY_CLEANUP_FAILED", "Geometry ownership could not retire; its reservation remains charged.", true);

    public ValueTask DisposeAsync()
    {
        Operation[] accepted; LeaseSlot[] held; TaskCompletionSource completion;
        lock (gate)
        {
            if (shutdown is not null) return new(shutdown.Task);
            closed = true; completion = shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            accepted = operations.ToArray(); held = leases.ToArray();
        }
        _ = CloseAsync(); return new(completion.Task);
        async Task CloseAsync()
        {
            try
            {
                // Start every cancellation/close before awaiting any blocked peer.
                foreach (var operation in accepted) _ = CancelAsync(operation);
                foreach (var slot in held) StartRetirement(slot);
                await Task.WhenAll(accepted.Select(o => o.Retired.Task).Concat(held.Select(s => s.Retired.Task)));
                foreach (var slot in held) try { await slot.PublishedRetirement.Task; } catch (Exception) { /* Observed; fail-closed state is reported below. */ }
                await deadlineTimer.DisposeAsync();
                lock (gate) { workerStopping = true; WakeRetirement(); }
                await retirementWorker; retirementWake.Dispose();
                lock (gate) { if (cleanupFailure is not null) throw Cleanup(); }
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
