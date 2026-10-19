using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Durable commands are independent of the renderer and background job lifetime.</summary>
public sealed class RetentionService(IRetentionStore store, IRetentionDriver driver)
{
    private readonly SemaphoreSlim mutations = new(1, 1);
    private int stopping;
    private bool restored;
    private bool retired;

    public async Task<RetentionSnapshot> ReadAsync(RetentionTarget target, CancellationToken token = default)
    {
        Validate(target);
        token.ThrowIfCancellationRequested();
        return ObserveFault(await store.ReadAsync(target, token));
    }

    public async Task<IReadOnlyList<RetentionSnapshot>> ListAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var snapshots = await store.ListAsync(token);
        return snapshots.Select(ObserveFault).ToArray();
    }

    public async Task<RetentionSnapshot> SetAsync(RetentionSetRequest request, CancellationToken token = default)
    {
        Validate(request);
        ThrowIfStopping();
        await mutations.WaitAsync(token);
        try
        {
            ThrowIfStopping();
            var committed = await store.SetAsync(request, token);
            // Do not recheck request cancellation after the durable store returns a commit.
            driver.NotifyIntentChanged();
            return ObserveFault(committed);
        }
        finally { mutations.Release(); }
    }

    public async Task RestoreAsync(CancellationToken token = default)
    {
        ThrowIfStopping();
        await mutations.WaitAsync(token);
        try
        {
            ThrowIfStopping();
            if (restored) return;
            await driver.RestoreAsync(token);
            restored = true;
        }
        finally { mutations.Release(); }
    }

    public async Task ShutdownAsync(CancellationToken token = default)
    {
        // Close admission before waiting for an already accepted durable mutation.
        Interlocked.Exchange(ref stopping, 1);
        await mutations.WaitAsync(token);
        try
        {
            if (retired) return;
            await driver.ShutdownAsync(token);
            retired = true;
        }
        finally { mutations.Release(); }
    }

    public static void Validate(RetentionTarget target)
    {
        if (target is null || target.ProductId is null || target.SourceRegion is null ||
            !CanonicalHash(target.BuildIdentity) || !CanonicalHash(target.LedgerId) ||
            target.SourceRegion.Length is < 2 or > 8 ||
            target.SourceRegion.Any(ch => ch is < 'a' or > 'z'))
            throw InvalidRequest();
        ProductRegistry.Validate(target.ProductId);
    }

    private RetentionSnapshot ObserveFault(RetentionSnapshot snapshot)
    {
        var fault = driver.ReadFault();
        if (fault is null) return snapshot;
        // A pending settlement can block the entire FIFO even when its old job already
        // committed a stable state. Preserve all durable intent and evidence fields.
        if (snapshot.Mode == RetentionMode.DownloadAll && !snapshot.Paused &&
            snapshot.State is RetentionState.Queued or RetentionState.Preparing or RetentionState.Running or RetentionState.Waiting)
            return snapshot with { State = RetentionState.Waiting, Problem = fault.Problem,
                Progress = new("Waiting to retry saved download state", "stage") };
        if (snapshot.State == RetentionState.Stopping && snapshot.JobId == fault.JobId)
            return snapshot with { Problem = fault.Problem };
        return snapshot;
    }

    public static void Validate(RetentionSetRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Action) || request.ExpectedRevision is < 0)
            throw InvalidRequest();
        Validate(request.Target);
    }

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref stopping) != 0)
            throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
    }

    private static bool CanonicalHash(string? value) => value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static DiscoveryException InvalidRequest() => new("INVALID_REQUEST",
        "Choose an exact retained build and download ledger, a supported action and a nonnegative intent revision.");
}
