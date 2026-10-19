using VibeMiner.Domain;

namespace VibeMiner.Application;

public interface IRetentionStore
{
    // Local projections only; reading never starts acquisition or reconciles payloads.
    Task<RetentionSnapshot> ReadAsync(RetentionTarget target, CancellationToken token);
    Task<IReadOnlyList<RetentionSnapshot>> ListAsync(CancellationToken token);

    // Acknowledge only committed intent. Enforce exact admitted plan, intent revision
    // and nine occupied slots, including an OnDemand job still Stopping until cleanup.
    // A committed result wins client cancellation; a pre-commit cancellation may roll back.
    Task<RetentionSnapshot> SetAsync(RetentionSetRequest request, CancellationToken token);
}

public interface IRetentionDriver
{
    Task RestoreAsync(CancellationToken token);

    // Pure local observation of an operationally parked worker; never wakes work.
    RetentionDriverFault? ReadFault() => null;

    // Nonthrowing, coalesced wakeup only. Never synchronously run or await job work.
    void NotifyIntentChanged();

    // Preserve durable intent and await actual subscriptions/session/staging retirement.
    Task ShutdownAsync(CancellationToken token);
}
