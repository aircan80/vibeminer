using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Bounded physical producers with independent consumers; HTTP/CPU/disk permits are separate.</summary>
public sealed class AcquisitionCoordinator : ISharedAcquisition, IAsyncDisposable
{
    private readonly IEncodedObjectProducer producer;
    private readonly AcquisitionLimits limits;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Work> work = new(StringComparer.Ordinal);
    // Entered third-party observers may outlive a producer. Charge their whole lifetime, including
    // retired work, so repeated fast producers cannot leave unlimited blocked callback Tasks.
    private readonly SemaphoreSlim observerSlots = new(32, 32);
    private readonly AsyncLocal<bool> executing = new();
    private long nextOrder;
    private bool closed;
    private Task? shutdown;

    private sealed class Work(EncodedRequirement requirement, long order)
    {
        internal readonly EncodedRequirement Requirement = requirement;
        internal readonly long Order = order;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<EncodedReceipt> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<Consumer> Consumers = [];
        internal TaskCompletionSource Changed = Signal();
        internal long Revision;
        internal long LastReport;
        internal long PayloadBytes;
        internal string? LastStage;
        internal string? LastUnit;
        internal bool HasReported;
        internal bool Running;
        internal bool Stopping;
        internal AcquisitionPriority Priority => Consumers.Any(c => c.Priority == AcquisitionPriority.Interactive)
            ? AcquisitionPriority.Interactive : AcquisitionPriority.Background;
    }

    private sealed class Consumer(EncodedSourceContext source, AcquisitionPriority priority,
        Func<AcquisitionProgress, Task>? callback)
    {
        internal readonly EncodedSourceContext Source = source;
        internal AcquisitionPriority Priority = priority;
        internal Func<AcquisitionProgress, Task>? Callback = callback;
        internal AcquisitionProgress? Latest;
        internal bool Dispatching;
        internal bool Detached;
        internal long PayloadBaseline;
        internal long? PayloadAtDetach;
    }

    private sealed class Context(AcquisitionCoordinator owner, Work item) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => owner.Snapshot(item);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) =>
            owner.WaitForChangeAsync(item, observedRevision, token);
        public Task ReportAsync(AcquisitionProgress progress) => owner.ReportAsync(item, progress);
    }

    private sealed class Subscription(AcquisitionCoordinator owner, Work item, Consumer consumer) : IAcquisitionSubscription
    {
        private readonly object disposalGate = new();
        private Task? disposal;
        public Task<EncodedReceipt> Completion => item.Completion.Task;
        public long ObservedPayloadBytes
        {
            get { lock (owner.gate) return (consumer.PayloadAtDetach ?? item.PayloadBytes) - consumer.PayloadBaseline; }
        }
        public void SetPriority(AcquisitionPriority priority) => owner.SetPriority(item, consumer, priority);
        public async Task<EncodedReceipt> WaitAsync(CancellationToken token = default)
        {
            try { return await Completion.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await DisposeAsync();
                // A durable publication may win the race with cancellation.
                if (Completion.IsCompletedSuccessfully) return await Completion;
                throw new OperationCanceledException(token);
            }
        }
        public ValueTask DisposeAsync()
        {
            lock (disposalGate) return new(disposal ??= owner.DetachAsync(item, consumer));
        }
    }

    public AcquisitionCoordinator(IEncodedObjectProducer producer, AcquisitionLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        this.producer = producer ?? throw new ArgumentNullException(nameof(producer));
        this.limits = limits ?? new(); clock = timeProvider ?? TimeProvider.System;
        if (this.limits.MaximumProducers is < 1 or > 128 ||
            this.limits.MaximumBackgroundProducers < 1 || this.limits.MaximumBackgroundProducers > this.limits.MaximumProducers ||
            this.limits.ReservedInteractiveAdmissions < 0 || this.limits.ReservedInteractiveAdmissions >= this.limits.MaximumProducers ||
            this.limits.MaximumActive < 1 || this.limits.MaximumActive > this.limits.MaximumProducers ||
            this.limits.MaximumActiveBackground < 1 || this.limits.MaximumActiveBackground > this.limits.MaximumActive ||
            this.limits.MaximumConsumersPerProducer is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public IAcquisitionSubscription Attach(EncodedRequirement requirement, EncodedSourceContext source,
        AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null)
    {
        Validate(requirement, source, priority);
        if (executing.Value)
            throw Error("ACQUISITION_RECURSIVE", "Resolve dependencies before entering a physical encoded producer.");
        lock (gate)
        {
            if (closed) throw Error("ENGINE_STOPPING", "The acquisition service is stopping.", true);
            if (work.TryGetValue(requirement.EncodingKey, out var item))
            {
                if (item.Requirement.EncodedBytes != requirement.EncodedBytes)
                    throw Error("ACQUISITION_SIZE_CONFLICT", "One encoded identity has conflicting expected lengths.");
                if (item.Requirement.VerifierRevision != requirement.VerifierRevision)
                    throw Error("ACQUISITION_VERIFIER_CONFLICT", "One encoded identity requires incompatible verification policies.");
                if (item.Stopping) throw Error("ACQUISITION_STOPPING", "The previous producer is still cleaning up.", true);
                if (item.Consumers.Count >= limits.MaximumConsumersPerProducer)
                    throw Error("ACQUISITION_CAPACITY", "The bounded physical-object consumer capacity is full.", true);
            }
            else
            {
                if (work.Count >= limits.MaximumProducers || priority == AcquisitionPriority.Background &&
                    (work.Count >= limits.MaximumProducers - limits.ReservedInteractiveAdmissions ||
                     work.Values.Count(value => value.Priority == AcquisitionPriority.Background) >= limits.MaximumBackgroundProducers))
                    throw Error("ACQUISITION_CAPACITY", "The bounded acquisition admission capacity is full.", true);
                item = new(requirement, ++nextOrder); work.Add(requirement.EncodingKey, item);
            }
            var consumer = new Consumer(source, priority, progress) { PayloadBaseline = item.PayloadBytes };
            item.Consumers.Add(consumer); Changed(item); Schedule();
            return new Subscription(this, item, consumer);
        }
    }

    public async Task<EncodedReceipt> AcquireAsync(EncodedRequirement requirement, EncodedSourceContext source,
        AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var subscription = Attach(requirement, source, priority, progress);
        return await subscription.WaitAsync(token);
    }

    // Called only under gate. Production defaults allow every bounded admitted state machine to
    // progress, so demotion cannot occupy a second logical bottleneck ahead of reserved HTTP/CPU/
    // disk admission. Smaller custom active limits are nonpreemptive admission policy only.
    private void Schedule()
    {
        while (!closed && work.Values.Count(value => value.Running) < limits.MaximumActive)
        {
            var background = work.Values.Count(value => value.Running && value.Priority == AcquisitionPriority.Background);
            var next = work.Values.Where(value => !value.Running && !value.Stopping && value.Consumers.Count != 0 &&
                    (value.Priority == AcquisitionPriority.Interactive || background < limits.MaximumActiveBackground))
                .OrderByDescending(value => value.Priority).ThenBy(value => value.Order).FirstOrDefault();
            if (next is null) return;
            next.Running = true;
            _ = Task.Run(() => RunAsync(next));
        }
    }

    private async Task RunAsync(Work item)
    {
        EncodedReceipt? result = null; Exception? failure = null;
        try
        {
            executing.Value = true;
            result = await producer.ProduceAsync(item.Requirement, new Context(this, item), item.Cancellation.Token);
            ValidateResult(item.Requirement, result);
        }
        catch (Exception exception) { failure = exception; }
        finally { executing.Value = false; }
        lock (gate)
        {
            // ProduceAsync must finish its owned resource cleanup before returning/throwing.
            item.Running = false; work.Remove(item.Requirement.EncodingKey);
            foreach (var consumer in item.Consumers) { consumer.Latest = null; consumer.Callback = null; }
            Changed(item);
            if (failure is OperationCanceledException canceled) item.Completion.TrySetCanceled(canceled.CancellationToken);
            else if (failure is not null) item.Completion.TrySetException(failure);
            else item.Completion.TrySetResult(result!);
            item.Cancellation.Dispose();
            Schedule();
        }
    }

    private AcquisitionWorkSnapshot Snapshot(Work item)
    {
        lock (gate) return new(item.Revision, item.Priority,
            Array.AsReadOnly(item.Consumers.Select(value => value.Source).Distinct().ToArray()));
    }

    private Task WaitForChangeAsync(Work item, long observedRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) return observedRevision != item.Revision ? Task.CompletedTask : item.Changed.Task.WaitAsync(token);
    }

    private static void Changed(Work item)
    {
        item.Revision++; var previous = item.Changed; item.Changed = Signal(); previous.TrySetResult();
    }

    private void SetPriority(Work item, Consumer consumer, AcquisitionPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw Error("INVALID_REQUEST", "Choose a supported acquisition priority.");
        lock (gate)
        {
            if (consumer.Detached || item.Completion.Task.IsCompleted || consumer.Priority == priority) return;
            consumer.Priority = priority; Changed(item); Schedule();
        }
    }

    private async Task DetachAsync(Work item, Consumer consumer)
    {
        var last = false;
        lock (gate)
        {
            if (consumer.Detached) return;
            consumer.Detached = true; consumer.Callback = null; consumer.Latest = null;
            item.Consumers.Remove(consumer); Changed(item);
            if (!item.Completion.Task.IsCompleted && item.Consumers.Count == 0)
            {
                last = true; item.Stopping = true;
                if (!item.Running)
                {
                    work.Remove(item.Requirement.EncodingKey); item.Completion.TrySetCanceled();
                    item.Cancellation.Dispose();
                }
            }
            if (!last) consumer.PayloadAtDetach = item.PayloadBytes;
            Schedule();
        }
        if (!last) return;
        await CancelAsync(item);
        try { await item.Completion.Task; } catch (Exception) { /* Consumer detachment still awaits cleanup. */ }
        lock (gate) consumer.PayloadAtDetach = item.PayloadBytes;
    }

    private Task ReportAsync(Work item, AcquisitionProgress progress)
    {
        ValidateProgress(progress);
        lock (gate)
        {
            if (item.Completion.Task.IsCompleted) return Task.CompletedTask;
            // Accounting precedes throttling and remains active during last-consumer cleanup.
            // No observer callback is an accounting authority.
            if (progress.Unit == "wireBytes" && progress.Completed is { } payloadBytes)
            {
                if (payloadBytes < item.PayloadBytes)
                    throw Error("ACQUISITION_PROGRESS_INVALID", "Payload byte observations cannot move backwards.");
                item.PayloadBytes = payloadBytes;
            }
            if (item.Stopping) return Task.CompletedTask;
            var now = clock.GetTimestamp();
            if (item.HasReported && item.LastStage == progress.Stage && item.LastUnit == progress.Unit &&
                clock.GetElapsedTime(item.LastReport, now) < TimeSpan.FromMilliseconds(100))
                return Task.CompletedTask;
            item.HasReported = true; item.LastReport = now;
            item.LastStage = progress.Stage; item.LastUnit = progress.Unit;
            foreach (var consumer in item.Consumers)
            {
                if (consumer.Detached || consumer.Callback is null) continue;
                consumer.Latest = progress;
                if (consumer.Dispatching) continue;
                if (!observerSlots.Wait(0)) continue;
                consumer.Dispatching = true; _ = Task.Run(() => DispatchAsync(consumer));
            }
        }
        return Task.CompletedTask;
    }

    private async Task DispatchAsync(Consumer consumer)
    {
        // Subscriber observation is not part of the physical producer's dependency stack.
        executing.Value = false;
        try
        {
            while (true)
            {
                AcquisitionProgress? progress; Func<AcquisitionProgress, Task>? callback;
                lock (gate)
                {
                    progress = consumer.Latest; callback = consumer.Callback; consumer.Latest = null;
                    if (consumer.Detached || progress is null || callback is null) { consumer.Dispatching = false; return; }
                }
                // A callback already admitted here may enter/finish after detach or terminal.
                // Observation must carry its view generation; it cannot mutate producer state.
                try { await callback(progress); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { lock (gate) { consumer.Callback = null; consumer.Latest = null; consumer.Dispatching = false; } return; }
            }
        }
        finally { observerSlots.Release(); }
    }

    private static void Validate(EncodedRequirement requirement, EncodedSourceContext source, AcquisitionPriority priority)
    {
        if (requirement is null || source is null || !Hex(requirement.EncodingKey, 32) ||
            requirement.EncodedBytes is < 1 or > 512L * 1024 * 1024 ||
            requirement.VerifierRevision != "blte-encoded-v1" || !Enum.IsDefined(priority) ||
            source.ProductId is null || source.BuildIdentity is null || source.SourceRegion is null)
            throw Error("INVALID_REQUEST", "Choose a full encoded identity, bounded length and supported verifier.");
        WorkspaceService.Validate(new(source.ProductId, source.BuildIdentity, source.SourceRegion));
        if (!Hex(source.CdnConfigurationSha256, 64) || source.SourceRegion is null ||
            source.SourceRegion.Length is < 2 or > 8 || source.SourceRegion.Any(ch => ch is < 'a' or > 'z'))
            throw Error("INVALID_REQUEST", "Choose an exact pinned source configuration and region.");
        if (source.SourceReference is { } reference &&
            (reference.Region != source.SourceRegion || !Hex(reference.VersionsSha256, 64) ||
             !Hex(reference.CdnsSha256, 64) || reference.VersionsRowOrdinal < 0 || reference.CdnsRowOrdinal < 0 ||
             reference.Generation is not { Length: > 0 and <= 19 } generation || generation[0] == '0' ||
             generation.Any(ch => ch is < '0' or > '9') || !long.TryParse(generation, out _)))
            throw Error("INVALID_REQUEST", "Choose a canonical retained opening source reference for this region.");
    }

    private static void ValidateResult(EncodedRequirement requirement, EncodedReceipt receipt)
    {
        if (receipt is null || receipt.EncodingKey != requirement.EncodingKey || receipt.EncodedBytes != requirement.EncodedBytes ||
            receipt.VerifierRevision != requirement.VerifierRevision || !Hex(receipt.Sha256, 64) || receipt.Location is null)
            throw Error("ACQUISITION_RESULT_INVALID", "The physical producer returned a conflicting encoded receipt.");
    }

    private static void ValidateProgress(AcquisitionProgress progress)
    {
        if (progress is null || string.IsNullOrWhiteSpace(progress.Stage) || progress.Stage.Length > 128 || progress.Stage.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(progress.Unit) || progress.Unit.Length > 32 || progress.Unit.Any(char.IsControl) ||
            progress.Completed is < 0 || progress.Total is < 0 || progress.Total is { } total && progress.Completed > total)
            throw Error("ACQUISITION_PROGRESS_INVALID", "Acquisition progress must have bounded text and consistent measured units.");
    }

    private static bool Hex(string? value, int length) => value is not null && value.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
    private static async Task CancelAsync(Work item)
    {
        try { await item.Cancellation.CancelAsync(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Observe callback failures and continue waiting for producer retirement. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (shutdown is not null) return new(shutdown);
            closed = true;
            var active = work.Values.ToArray();
            foreach (var item in active)
            {
                item.Stopping = true;
                foreach (var consumer in item.Consumers) { consumer.Detached = true; consumer.Callback = null; consumer.Latest = null; }
                item.Consumers.Clear(); Changed(item);
                if (!item.Running)
                {
                    work.Remove(item.Requirement.EncodingKey); item.Completion.TrySetCanceled(); item.Cancellation.Dispose();
                }
            }
            shutdown = Task.Run(async () =>
            {
                foreach (var item in active) await CancelAsync(item);
                foreach (var item in active) try { await item.Completion.Task; } catch (Exception) { }
            });
            return new(shutdown);
        }
    }
}
