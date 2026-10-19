using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed partial class CreatureGeometryService
{
    // These two defensive public ownership slots contain the store's physical leases, not another source pool.
    private sealed class LeaseSlot(Operation operation, long started)
    {
        internal readonly Operation Operation = operation;
        internal readonly long Started = started;
        internal readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PublishedRetirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ICreatureGeometryReadLease? Inner;
        internal Task? ClosingTask;
        internal bool Closing;
        internal Exception? Failure;
    }
    public async Task<ICreatureGeometryReadLease> OpenAsync(CreatureGeometryOpenRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested(); var operation = Admit(false, true, out var reserved); var slot = reserved!;
        CancellationTokenRegistration registration = default; ReadLease? result = null;
        try
        {
            var owned = request with { Selection = CreatureGeometryValidation.Own(request.Selection) };
            registration = token.UnsafeRegister(_ => { _ = CancelAsync(operation); }, null);
            operation.Token.ThrowIfCancellationRequested();
            slot.Inner = await store.OpenAsync(owned, operation.Token);
            if (slot.Inner is null) throw new DiscoveryException("CREATURE_GEOMETRY_RESULT_INVALID", "The geometry source returned no read ownership.");
            var manifest = CreatureGeometryValidation.Snapshot(slot.Inner.Manifest);
            if (manifest.ArtifactId != owned.GeometryArtifactId)
                throw new DiscoveryException("CREATURE_GEOMETRY_STALE", "The exact geometry read changed. Retry the selected slot.", true);
            var content = slot.Inner.Content;
            if (content is null || !content.CanRead || !content.CanSeek || content.CanWrite || content.Position != 0 || content.Length != manifest.ByteLength)
                throw new DiscoveryException("CREATURE_GEOMETRY_RESULT_INVALID", "The approved geometry stream differs from its bounded manifest.");
            operation.Token.ThrowIfCancellationRequested();
            lock (gate) { RequireOpen(); if (slot.Closing) throw new OperationCanceledException(operation.Token); result = new(this, slot, content, manifest); }
        }
        catch (Exception error)
        {
            if (error is DiscoveryException discovery && discovery.Code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal)) FailClosed(operation, error);
            slot.Opened.TrySetResult(); await RetireAsync(slot);
            if (error is OperationCanceledException && operation.Expired && !token.IsCancellationRequested)
                throw new DiscoveryException("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", "The geometry read reached its finite deadline.", true);
            throw;
        }
        finally { slot.Opened.TrySetResult(); await FinishAsync(operation, registration, null); }
        if (operation.Token.IsCancellationRequested || token.IsCancellationRequested)
        {
            await RetireAsync(slot);
            if (operation.Expired && !token.IsCancellationRequested)
                throw new DiscoveryException("CREATURE_GEOMETRY_DEADLINE_EXCEEDED", "The geometry read reached its finite deadline.", true);
            throw new OperationCanceledException(token.IsCancellationRequested ? token : operation.Token);
        }
        lock (gate) { if (!slot.Closing) return result!; }
        await RetireAsync(slot); throw new OperationCanceledException(operation.Token);
    }
    private void StartRetirement(LeaseSlot slot)
    {
        lock (gate) { if (slot.Closing) return; slot.Closing = true; ScheduleDeadline(); WakeRetirement(); }
    }
    private void WakeRetirement() { if (retirementWake.CurrentCount == 0) retirementWake.Release(); }
    private async Task RunRetirementsAsync()
    {
        Task? wake = null;
        while (true)
        {
            LeaseSlot[] start;
            lock (gate) start = leases.Where(s => s.Closing && s.ClosingTask is null).ToArray();
            foreach (var slot in start)
            {
                // Only this worker writes ClosingTask; it owns at most two concurrent close tasks.
                slot.ClosingTask = RetireCoreAsync(slot);
            }
            Task[] active; bool done;
            lock (gate)
            {
                active = leases.Where(s => s.ClosingTask is { IsCompleted: false }).Select(s => s.ClosingTask!).ToArray();
                done = workerStopping && leases.All(s => s.Retired.Task.IsCompleted);
            }
            if (done)
            {
                if (wake is not null) { if (!wake.IsCompleted) retirementWake.Release(); await wake; }
                return;
            }
            wake ??= retirementWake.WaitAsync();
            var completed = active.Length == 0 ? wake : await Task.WhenAny(active.Append(wake));
            await completed; if (completed == wake) wake = null;
        }
    }
    private async Task RetireCoreAsync(LeaseSlot slot)
    {
        try
        {
            var notification = CancelAsync(slot.Operation);
            await slot.Opened.Task;
            if (slot.Inner is not null)
            {
                Exception? failure = null;
                try { await slot.Inner.DisposeAsync(); } catch (Exception error) { failure = error; }
                try { await slot.Inner.Retirement; } catch (Exception error) { failure ??= error; }
                if (failure is not null) throw failure;
            }
            await notification;
            lock (gate) { leases.Remove(slot); ScheduleDeadline(); }
            slot.PublishedRetirement.TrySetResult();
        }
        catch (Exception error) { slot.Failure = error; FailClosed(slot.Operation, error); slot.PublishedRetirement.TrySetException(Cleanup()); }
        finally { slot.Retired.TrySetResult(); }
    }
    private async Task RetireAsync(LeaseSlot slot)
    { StartRetirement(slot); await slot.Retired.Task; await slot.PublishedRetirement.Task; }
    private sealed class ReadLease(CreatureGeometryService owner, LeaseSlot slot, Stream content, CreatureGeometryManifest manifest) : ICreatureGeometryReadLease
    {
        public Stream Content { get { lock (owner.gate) { ObjectDisposedException.ThrowIf(slot.Closing, this); return content; } } }
        public CreatureGeometryManifest Manifest => manifest;
        public Task Retirement => slot.PublishedRetirement.Task;
        public ValueTask DisposeAsync() => new(owner.RetireAsync(slot));
    }
}
