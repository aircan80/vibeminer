using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record CreatureGeometryRequest(CreatureDisplayMetadataRequest Selection);
public sealed record CreatureGeometryReadRequest(CreatureDisplayMetadataRequest Selection,
    string? ExpectedGeometryArtifactId = null);
public sealed record CreatureGeometryOpenRequest(CreatureDisplayMetadataRequest Selection, string GeometryArtifactId);

/// <summary>Exact current, fully verified local artifact. Dispose completes actual owned read/handle retirement.</summary>
public interface ICreatureGeometryReadLease : IAsyncDisposable
{
    Stream Content { get; }
    CreatureGeometryManifest Manifest { get; }
    /// <summary>Actual stream/lease retirement only. Faults on charged cleanup debt, never merely on expiry/cancel.</summary>
    Task Retirement { get; }
}

/// <summary>Only Prepare may acquire prerequisites. Read/Open are local, never bootstrap B or acquire sources.
/// The adapter owns one shared producer, canonical source/current catalog proof and aggregate payload/staging budgets.
/// Returned values must be bounded immutable owned graphs. Completion includes actual source/worker cleanup;
/// a late returned lease remains owned until awaited Dispose confirms retirement. One immediate two-slot
/// physical reader union covers temporary Read verification, Open opening and held/draining/debt leases;
/// defensive service call counts never authorize another reader pool or an unbounded waiter queue.
/// Production opens run through CreatureGeometryService's absolute30s timer; the store never restarts it.
/// The Open token covers asynchronous opening; a returned lease's Dispose owns cancellation/actual retirement
/// of its held I/O and handles even after the caller's Open token source has been disposed.</summary>
public interface ICreatureGeometryStore
{
    Task<CreatureGeometryResult> ReadAsync(CreatureGeometryReadRequest request, CancellationToken token);
    Task<CreatureGeometryResult> PrepareAsync(CreatureGeometryRequest request,
        Func<CreatureGeometryProgress, Task> progress, CancellationToken token);
    Task<ICreatureGeometryReadLease> OpenAsync(CreatureGeometryOpenRequest request, CancellationToken token);
}
