using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record AcquisitionWorkSnapshot(long Revision, AcquisitionPriority Priority,
    IReadOnlyList<EncodedSourceContext> Sources);

/// <summary>Dynamic priority and source authority; resource admission belongs to the concrete producer.</summary>
public interface IAcquisitionWorkContext
{
    AcquisitionWorkSnapshot Snapshot { get; }
    Task WaitForChangeAsync(long observedRevision, CancellationToken token = default);
    Task ReportAsync(AcquisitionProgress progress);
}

public interface IEncodedObjectProducer
{
    // Resolve prerequisites before entering this physical producer. Recursive coordinator requests
    // are rejected to prevent bounded-parent/dependency deadlocks.
    Task<EncodedReceipt> ProduceAsync(EncodedRequirement requirement, IAcquisitionWorkContext context,
        CancellationToken token);
}

public interface IAcquisitionSubscription : IAsyncDisposable
{
    // Shared producer terminal outcome, after its resource cleanup. Detaching this consumer does
    // not cancel Completion while another consumer remains. Already admitted progress callbacks
    // may enter/finish later and must retain their caller's stale-result generation guard.
    Task<EncodedReceipt> Completion { get; }
    // Monotonic payload response bytes observed while this consumer is attached, including
    // retries. Independent of lossy progress delivery; retained after completion/disposal.
    // A final consumer includes bytes observed during actual producer cancellation cleanup.
    long ObservedPayloadBytes => 0;
    void SetPriority(AcquisitionPriority priority);
    // Cancellation detaches this consumer; the last consumer awaits actual producer retirement.
    // A completed durable receipt may win cancellation. Dispose follows the same cleanup policy.
    Task<EncodedReceipt> WaitAsync(CancellationToken token = default);
}

public interface ISharedAcquisition
{
    IAcquisitionSubscription Attach(EncodedRequirement requirement, EncodedSourceContext source,
        AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null);
    Task<EncodedReceipt> AcquireAsync(EncodedRequirement requirement, EncodedSourceContext source,
        AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null,
        CancellationToken token = default);
}

public sealed record AcquisitionLimits(int MaximumProducers = 128, int MaximumBackgroundProducers = 32,
    int ReservedInteractiveAdmissions = 32, int MaximumActive = 128, int MaximumActiveBackground = 128,
    int MaximumConsumersPerProducer = 32);
