using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>One shared producer and one immediate two-member physical Read/Open union. Application
/// owns absolute deadlines; this adapter owns actual source/stream/notification/budget retirement.</summary>
public sealed partial class CreatureGeometryStore : ICreatureGeometryStore, IAsyncDisposable
{
    private readonly ICreatureGeometryStoreWork work;
    private readonly GeometryPayloadBudget payload;
    private readonly IGeometryArtifactWorkBudget cpu;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    // Existing2 consumers +one sequential source revalidator; no other selection is admitted.
    private readonly SemaphoreSlim producerCapture = new(1,1);
    private readonly List<ReadSlot> readers = new(2);
    private Group? producer;
    private bool closed;
    private Exception? debt;
    private TaskCompletionSource? shutdown;
    internal CreatureGeometryStore(ICreatureGeometryStoreWork work, GeometryPayloadBudget payload,
        IGeometryArtifactWorkBudget cpu, TimeProvider? clock = null)
    { this.work = work; this.payload = payload; this.cpu = cpu; this.clock = clock ?? TimeProvider.System; }
    internal (int Consumers, int Readers, long Payload, bool Closed) Pressure
    { get { lock (gate) return (producer?.Consumers.Count ?? 0, readers.Count, payload.ReservedBytes, closed); } }
    private void RequireOpen()
    { if (debt is not null) throw Cleanup(); if (closed) throw new DiscoveryException("ENGINE_STOPPING", "Geometry work is stopping.", true); }
    private void FailClosed(Exception error) { lock (gate) { debt ??= error; closed = true; } }
    private static DiscoveryException Busy() => new("CREATURE_GEOMETRY_BUSY", "Geometry ownership is at capacity or still retiring.", true);
    private static DiscoveryException Cleanup() => new("CREATURE_GEOMETRY_CLEANUP_FAILED", "Geometry cleanup could not retire; its reservation remains charged.", true);
    private static bool CleanupFailure(Exception error) => error is GeometrySourceCleanupException || error is DiscoveryException e && (e.Code=="ASSET_SOURCE_CLEANUP"||e.Code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveCancellationAsync(CancellationTokenSource cancellation)
    { try { await cancellation.CancelAsync(); } catch (AggregateException) { } }
    private static CreatureDisplayMetadataRequest Own(CreatureDisplayMetadataRequest selection) => selection with { Context = selection.Context with { } };
    // A metadata problem is never a geometry limit, so its headline is only ever encrypted, unsupported or not drawn (#116).
    private static CreatureGeometryProblem MetadataProblem(CreatureGeometryState state, CreatureDisplayMetadataProblem problem) =>
        new(problem.Code, problem.Message, problem.Retryable, CreatureGeometryDependency.Metadata, problem.Target,
            GeometrySourceClosure.Headline(state, problem.Code, problem.Retryable, GeometryLimitOwner.Engine));
    private static CreatureGeometryResult Result(CreatureDisplayMetadataRequest request, GeometryDisplayAuthorizationResult metadata,
        CreatureGeometryState state, IReadOnlyList<CreatureGeometrySource>? sources = null, CreatureGeometryManifest? manifest = null, CreatureGeometryProblem? problem = null,
        CreatureGeometryAppearance? appearance = null) =>
        CreatureGeometryValidation.Snapshot(new(request.Context, request.ArtifactId, request.CreatureId, request.Slot,
            CreatureGeometryValidation.Policy, metadata.Core.Opening.Reference, state, metadata.Metadata,
            sources ?? Array.Empty<CreatureGeometrySource>(), manifest, problem, state == CreatureGeometryState.Ready ? appearance : null), request);
    private static CreatureGeometryResult MetadataResult(CreatureDisplayMetadataRequest request, GeometryDisplayAuthorizationResult value)
    {
        var state = value.Metadata.State == CreatureDisplayMetadataState.NotPrepared ? CreatureGeometryState.NotPrepared : CreatureGeometryState.MetadataNotResolved;
        return Result(request, value, state, problem: value.Problem is null ? null : MetadataProblem(state, value.Problem));
    }

    public ValueTask DisposeAsync()
    {
        Group? group; Consumer[] consumers; ReadSlot[] held; TaskCompletionSource completion;
        lock (gate)
        {
            if (shutdown is not null) return new(shutdown.Task);
            closed = true; completion = shutdown = Signal(); group = producer; consumers = group?.Consumers.ToArray() ?? []; held = readers.ToArray();
        }
        _ = CloseAsync(); return new(completion.Task);
        async Task CloseAsync()
        {
            try
            {
                foreach (var consumer in consumers) Stop(consumer);
                foreach (var slot in held) StartReadRetirement(slot);
                if (group is not null) await group.Retired.Task;
                await Task.WhenAll(held.Select(s => s.Retired.Task));
                lock (gate) if (debt is not null) throw Cleanup();
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
