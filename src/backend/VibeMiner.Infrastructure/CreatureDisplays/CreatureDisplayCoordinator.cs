using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record CreatureDisplayProbe(CreatureDisplayTarget Target, CreatureDisplayCatalogBinding Binding,
    string? Edge, CreatureDisplayMetadataState? Failure = null, CreatureDisplayMetadataProblem? Problem = null);
internal interface ICreatureDisplaySources : IAsyncDisposable
{
    Task<CreaturesDisplayAuthorization> AuthorizeAsync(CreatureDisplayMetadataRequest request, CancellationToken token);
    Task<CreatureDisplayProbe?> ProbeAsync(CreaturesDisplayAuthorization core, CreatureDisplayTargetRole role, string id, CancellationToken token);
    Task EnsureAsync(CreaturesContext context, CreatureDisplayTargetRole role, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token);
    Task FinishAsync(CreatureDisplayMetadataRequest request, CreaturesDisplayAuthorization core,
        IReadOnlyList<CreatureDisplayProbe> delivered, CancellationToken token);
}

/// <summary>One worker, one optional prerequisite and a two-consumer wakeable wavefront. Local
/// early outcomes and late joiners never wait on another selection's model-table acquisition.</summary>
internal sealed partial class CreatureDisplayCoordinator(ICreatureDisplaySources sources) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<Consumer> consumers = new(2);
    private TaskCompletionSource wake = Signal();
    private Task? worker;
    private bool producerStopping;
    private volatile bool closed;
    private int reads;
    private TaskCompletionSource? readsRetired;
    private readonly CancellationTokenSource shutdown = new();
    internal (int Consumers, bool Stopping, bool Worker) Pressure
    { get { lock (gate) return (consumers.Count, producerStopping, worker is not null); } }
    private enum Phase { Accepted, Display, Model, Terminal }
    private sealed class Consumer(CreatureDisplayMetadataRequest request, Func<CreaturesPreparationProgress, Task> progress,
        CancellationToken token, bool shared = false)
    {
        internal readonly CreatureDisplayMetadataRequest Request = request;
        /// <summary>Owned by the worker, so a stop may leave it subscribed to a retiring prerequisite.</summary>
        internal readonly bool Shared = shared;
        internal readonly CreatureDisplayProgressObserver Progress = new(progress);
        internal readonly CancellationToken CallerToken = token;
        internal readonly CancellationTokenSource Cancellation = new();
        internal CancellationToken Token => Cancellation.Token;
        internal readonly object LifetimeGate = new();
        internal TaskCompletionSource? Notification;
        internal bool Finished;
        internal readonly TaskCompletionSource<CreatureDisplayMetadataResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CreaturesDisplayAuthorization? Core;
        internal CreatureDisplaySlot? Selected;
        internal readonly List<CreatureDisplayProbe> Delivered = new(2);
        internal Phase Phase;
        internal string? ModelId;
        internal bool Reauthorize;
        internal readonly HashSet<CreatureDisplayTargetRole> Prepared = [];
    }
    private sealed class Prerequisite(CreatureDisplayTargetRole role)
    {
        internal readonly CreatureDisplayTargetRole Role = role;
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task Work = Task.CompletedTask;
        internal Task? Notification;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Wake() { lock (gate) wake.TrySetResult(); }
    internal async Task<CreatureDisplayMetadataResult> PrepareAsync(CreatureDisplayMetadataRequest request,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
    {
        CreatureDisplayMetadataService.Validate(request); token.ThrowIfCancellationRequested();
        var consumer = new Consumer(request, progress, token, shared: true);
        lock (gate)
        {
            if (closed) throw Stopping();
            if (producerStopping || consumers.Count >= 2 || consumers.Count != 0 && consumers[0].Request.Context != request.Context)
                throw Busy();
            consumers.Add(consumer); wake.TrySetResult();
            worker ??= RunAsync();
        }
        using var cancellation = token.Register(() => Stop(consumer));
        try { return await consumer.Completion.Task; }
        finally
        {
            await RetireConsumerAsync(consumer);
            await consumer.Progress.CloseAsync();
            lock (gate) { consumers.Remove(consumer); consumer.Retired.TrySetResult(); wake.TrySetResult(); }
        }
    }
    internal Task<CreatureDisplayMetadataResult> ReadAsync(CreatureDisplayMetadataRequest request, CancellationToken token) =>
        ExecuteLocalAsync(request, token, static (_, result) => result);
    private async Task<T> ExecuteLocalAsync<T>(CreatureDisplayMetadataRequest request, CancellationToken token,
        Func<Consumer, CreatureDisplayMetadataResult, T> project)
    {
        CreatureDisplayMetadataService.Validate(request); token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (closed) throw Stopping();
            if (reads >= 2) throw new DiscoveryException("CREATURES_QUERY_BUSY", "Local Creature display reads are at their bounded capacity or retiring.", true);
            if (reads++ == 0) readsRetired = Signal();
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        var consumer = new Consumer(request, _ => Task.CompletedTask, lifetime.Token);
        using var cancellation = lifetime.Token.Register(() => Stop(consumer));
        try
        {
            await AdvanceAsync(consumer);
            CreatureDisplayMetadataResult result;
            if (consumer.Phase == Phase.Terminal) result = await consumer.Completion.Task;
            else
            {
                var table = consumer.Phase == Phase.Display ? CreatureDisplayPolicy.DisplayTable : CreatureDisplayPolicy.ModelTable;
                result = await FinishAsync(consumer, CreatureDisplayMetadataState.NotPrepared, required: table);
            }
            // Only private owned projections run here, before this shared local-query credit retires.
            return project(consumer, result);
        }
        finally { await RetireConsumerAsync(consumer); await consumer.Progress.CloseAsync(); lock (gate) if (--reads == 0) readsRetired!.TrySetResult(); }
    }
    private async Task RunAsync()
    {
        await Task.Yield();
        Prerequisite? prerequisite = null;
        try
        {
            while (true)
            {
                Consumer[] current; Task changed;
                lock (gate)
                {
                    if (wake.Task.IsCompleted) wake = Signal();
                    changed = wake.Task; current = consumers.ToArray();
                }
                if (prerequisite is not null && prerequisite.Work.IsCompleted)
                {
                    Exception? failure = null;
                    try { await prerequisite.Work; } catch (Exception error) { failure = error; }
                    if (prerequisite.Notification is not null) await prerequisite.Notification;
                    var role = prerequisite.Role; prerequisite.Cancellation.Dispose(); prerequisite = null;
                    foreach (var consumer in current.Where(c => Needs(c, role)))
                    {
                        consumer.Reauthorize = true;
                        if (failure is null) consumer.Prepared.Add(role);
                        if (failure is not null && !Canceled(consumer)) await FailAsync(consumer, role, failure);
                    }
                }
                foreach (var consumer in current)
                {
                    if (consumer.Phase == Phase.Terminal || Canceled(consumer)) continue;
                    try { await AdvanceAsync(consumer); }
                    catch (Exception error) { CompleteFailure(consumer, error); }
                }
                if (prerequisite is not null)
                {
                    // Capture stops before the live-demand decision. A consumer stopped after that decision
                    // counted as live here, so it may settle only on a later pass that sees it as stopped.
                    var stopped = current.Where(c => Canceled(c) && c.Phase != Phase.Terminal).ToArray();
                    if (!current.Any(c => Needs(c, prerequisite.Role) && !Canceled(c)) && prerequisite.Notification is null)
                    {
                        lock (gate) producerStopping = !consumers.Any(c => c.Phase != Phase.Terminal && !Canceled(c));
                        prerequisite.Notification = CancelAsync(prerequisite.Cancellation);
                    }
                    // A canceled last work owner retains its subscription until actual source retirement.
                    var sourceStillRetiring = prerequisite.Notification is not null &&
                        (!prerequisite.Work.IsCompleted || !prerequisite.Notification.IsCompleted);
                    if (!sourceStillRetiring)
                        foreach (var consumer in stopped) Terminate(consumer, new OperationCanceledException(consumer.Token));
                }
                else
                {
                    foreach (var consumer in current.Where(c => Canceled(c) && c.Phase != Phase.Terminal))
                        Terminate(consumer, new OperationCanceledException(consumer.Token));
                    CreaturesContext? nextContext = null;
                    lock (gate)
                    {
                        // Recheck actual live demand at the CMD admission boundary, after local outcomes.
                        var next = consumers.FirstOrDefault(c => c.Phase == Phase.Display && !Canceled(c)) ??
                            consumers.FirstOrDefault(c => c.Phase == Phase.Model && !Canceled(c));
                        if (next is not null)
                        {
                            var role = next.Phase == Phase.Display ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model;
                            prerequisite = new(role);
                            nextContext = next.Request.Context;
                        }
                    }
                    if (nextContext is not null)
                    {
                        // No source, observer or Tables call runs under the coordinator registry lock.
                        var role = prerequisite!.Role;
                        prerequisite.Work = sources.EnsureAsync(nextContext, role,
                            value => ReportAsync(role, value), prerequisite.Cancellation.Token);
                    }
                }
                lock (gate)
                {
                    if (prerequisite is null && consumers.All(c => c.Phase == Phase.Terminal)) { producerStopping = false; worker = null; return; }
                    if (wake.Task.IsCompleted) continue;
                }
                if (prerequisite is null) await changed;
                else await Task.WhenAny(changed, prerequisite.Work);
            }
        }
        catch (Exception error)
        {
            if (prerequisite is not null)
            {
                await CancelAsync(prerequisite.Cancellation);
                try { await prerequisite.Work; } catch (Exception) { }
                prerequisite.Cancellation.Dispose();
            }
            lock (gate)
            {
                foreach (var consumer in consumers) Terminate(consumer, error);
                producerStopping = false; worker = null;
            }
        }
    }
    private bool Canceled(Consumer consumer) => closed || consumer.CallerToken.IsCancellationRequested;
    private void Stop(Consumer consumer)
    {
        TaskCompletionSource notification;
        lock (consumer.LifetimeGate)
        {
            if (consumer.Finished || consumer.Notification is not null) { Wake(); return; }
            consumer.Notification = notification = Signal();
        }
        _ = NotifyAsync(); Wake();
        async Task NotifyAsync()
        { try { await consumer.Cancellation.CancelAsync(); } catch (AggregateException) { } finally { notification.TrySetResult(); } }
    }
    private static async Task RetireConsumerAsync(Consumer consumer)
    {
        Task notification;
        lock (consumer.LifetimeGate) { consumer.Finished = true; notification = consumer.Notification?.Task ?? Task.CompletedTask; }
        await notification; consumer.Cancellation.Dispose();
    }
    private static bool Needs(Consumer c, CreatureDisplayTargetRole role) => c.Phase ==
        (role == CreatureDisplayTargetRole.Display ? Phase.Display : Phase.Model);
    private Task ReportAsync(CreatureDisplayTargetRole role, CreaturesPreparationProgress value)
    {
        Consumer[] observed;
        lock (gate) observed = consumers.Where(c => Needs(c, role) && !Canceled(c)).ToArray();
        foreach (var consumer in observed) consumer.Progress.Post(value);
        return Task.CompletedTask;
    }
    private async Task AdvanceAsync(Consumer consumer)
    {
        consumer.Token.ThrowIfCancellationRequested();
        if (consumer.Phase == Phase.Accepted || consumer.Reauthorize)
        {
            var current = await sources.AuthorizeAsync(consumer.Request, consumer.Token);
            if (consumer.Core is not null) CreatureDisplayPolicy.SameCore(consumer.Core, current, consumer.Request.Slot);
            consumer.Core = current; consumer.Reauthorize = false;
            consumer.Selected = CreatureDisplayPolicy.Select(current, consumer.Request);
            if (current.Summary is null)
            { await SettleAsync(consumer, CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope, CreatureDisplayPolicy.Absence(current.Snapshot.CreatureSource.Coverage)); return; }
            if (consumer.Selected!.State != CreatureDisplayState.Declared)
            {
                await SettleAsync(consumer, consumer.Selected.State == CreatureDisplayState.NoDeclaredDisplay
                    ? CreatureDisplayMetadataState.NoDeclaredDisplay : CreatureDisplayMetadataState.InvalidDisplayReference); return;
            }
            if (consumer.Phase == Phase.Accepted) consumer.Phase = Phase.Display;
        }
        while (consumer.Phase is Phase.Display or Phase.Model)
        {
            var role = consumer.Phase == Phase.Display ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model;
            var id = role == CreatureDisplayTargetRole.Display ? consumer.Selected!.DisplayId : consumer.ModelId!;
            CreatureDisplayProbe? probe;
            try { probe = await sources.ProbeAsync(consumer.Core!, role, id, consumer.Token); }
            catch (Exception error) { await FailAsync(consumer, role, error); return; }
            if (probe is null)
            {
                if (consumer.Prepared.Contains(role)) await FailAsync(consumer, role,
                    new DiscoveryException("CREATURE_DISPLAY_SOURCE_UNAVAILABLE", "The completed prerequisite did not leave a locally admitted exact target source.", true));
                return;
            }
            consumer.Delivered.Add(probe);
            if (probe.Failure is { } failure) { await SettleAsync(consumer, failure, problem: probe.Problem); return; }
            var source = probe.Target;
            if (source.MatchCount == "0")
            {
                await SettleAsync(consumer, role == CreatureDisplayTargetRole.Display ? CreatureDisplayMetadataState.DisplayMissingInAdmittedScope : CreatureDisplayMetadataState.ModelMissingInAdmittedScope,
                    CreatureDisplayPolicy.Absence(source.Snapshot.Coverage)); return;
            }
            if (source.MatchCount != "1")
            { await SettleAsync(consumer, role == CreatureDisplayTargetRole.Display ? CreatureDisplayMetadataState.DisplayAmbiguous : CreatureDisplayMetadataState.ModelAmbiguous); return; }
            if (probe.Edge == "0")
            { await SettleAsync(consumer, role == CreatureDisplayTargetRole.Display ? CreatureDisplayMetadataState.NoDeclaredModel : CreatureDisplayMetadataState.NoDeclaredModelFile); return; }
            if (role == CreatureDisplayTargetRole.Display) { consumer.ModelId = probe.Edge; consumer.Phase = Phase.Model; }
            else { await SettleAsync(consumer, CreatureDisplayMetadataState.MetadataResolved, file: probe.Edge); return; }
        }
    }
    private async Task SettleAsync(Consumer consumer, CreatureDisplayMetadataState state,
        CreatureDisplayAbsence? absence = null, string? file = null, CreatureDisplayMetadataProblem? problem = null)
    {
        try { var result = await FinishAsync(consumer, state, absence, file, problem: problem); consumer.Phase = Phase.Terminal; consumer.Completion.TrySetResult(result); }
        catch (Exception error) { CompleteFailure(consumer, error); }
    }
    private async Task<CreatureDisplayMetadataResult> FinishAsync(Consumer consumer, CreatureDisplayMetadataState state,
        CreatureDisplayAbsence? absence = null, string? file = null, string? required = null, CreatureDisplayMetadataProblem? problem = null)
    {
        consumer.Token.ThrowIfCancellationRequested();
        var core = consumer.Core!;
        var result = new CreatureDisplayMetadataResult(consumer.Request.Context, consumer.Request.ArtifactId,
            consumer.Request.CreatureId, consumer.Request.Slot, CreatureDisplayMetadataService.MetadataPolicy,
            core.Opening.Reference, new(CreaturesPolicy.Table, core.Snapshot.CreatureSource.ArtifactId, consumer.Request.CreatureId),
            core.Snapshot.CreatureSource.Coverage, consumer.Selected, state, CreatureDisplayAppearance.NotEvaluated,
            Array.AsReadOnly(consumer.Delivered.Select(p => p.Target).ToArray()), absence, consumer.ModelId, file, required, problem);
        await CreaturesResponseCounter.RequireAsync(result, consumer.Token);
        await sources.FinishAsync(consumer.Request, core, consumer.Delivered, consumer.Token);
        return result;
    }
    private async Task FailAsync(Consumer consumer, CreatureDisplayTargetRole role, Exception error)
    {
        if (error is OperationCanceledException || error is DiscoveryException fatal &&
            (fatal.Code.StartsWith("CREATURES_", StringComparison.Ordinal) || fatal.Code is "CREATURE_DISPLAY_STALE" or "ENGINE_STOPPING"))
        { CompleteFailure(consumer, error); return; }
        var problem = CreatureDisplayPolicy.Problem(error, role);
        await SettleAsync(consumer, CreatureDisplayPolicy.FailureState(error), problem: problem);
    }
    private void CompleteFailure(Consumer consumer, Exception error)
    {
        // A stop can surface as the cancellation of the consumer's own local step. A stopped shared
        // consumer still settles only in RunAsync's retirement pass, after any source it owns retired.
        if (consumer.Shared && error is OperationCanceledException && Canceled(consumer)) return;
        Terminate(consumer, error);
    }
    private static void Terminate(Consumer consumer, Exception error)
    {
        consumer.Phase = Phase.Terminal;
        if (error is OperationCanceledException canceled) consumer.Completion.TrySetCanceled(canceled.CancellationToken);
        else consumer.Completion.TrySetException(error);
    }
    private static async Task CancelAsync(CancellationTokenSource cancellation)
    { try { await cancellation.CancelAsync(); } catch (AggregateException) { } }
    private static DiscoveryException Busy() => new("CREATURES_BUSY", "Creature display demand is at its two-consumer capacity or another exact context is retiring.", true);
    private static DiscoveryException Stopping() => new("ENGINE_STOPPING", "The engine is stopping.", true);
    public async ValueTask DisposeAsync()
    {
        Task producer, readDrain; Task[] consumerDrain; Consumer[] stopped;
        lock (gate) { closed = true; wake.TrySetResult(); producer = worker ?? Task.CompletedTask; readDrain = readsRetired?.Task ?? Task.CompletedTask; stopped = consumers.ToArray(); consumerDrain = stopped.Select(c => c.Retired.Task).ToArray(); }
        foreach (var consumer in stopped) Stop(consumer);
        await CancelAsync(shutdown); await Task.WhenAll(consumerDrain.Append(producer).Append(readDrain));
        await sources.DisposeAsync();
    }
}
