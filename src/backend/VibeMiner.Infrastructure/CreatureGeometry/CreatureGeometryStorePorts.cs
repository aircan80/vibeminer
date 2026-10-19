using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

// Controlled tests may implement these internal operation boundaries; the public constructor always
// composes the real C2/source/CAS/catalog adapter. No caller-supplied proof enters the public API.
internal interface ICreatureGeometryStoreWork
{
    Task<GeometryDisplayAuthorizationResult> AuthorizeAsync(CreatureDisplayMetadataRequest selection, CancellationToken token);
    Task<CreatureDisplayMetadataResult> EnsureMetadataAsync(CreatureDisplayMetadataRequest selection, Func<CreatureGeometryProgress, Task> progress, CancellationToken token);
    Task<IGeometryStoreRetained?> FindAsync(GeometryDisplayAuthorization selection, string? expectedArtifact, CancellationToken token);
    Task<IGeometryStorePublication> ProduceAsync(IGeometrySelectionFence selection, IGeometryPayloadBudget payload,
        Func<CreatureGeometryProgress, Task> progress, CancellationToken token);
}
internal interface IGeometryStoreRetained
{
    CreatureGeometryManifest Manifest { get; }
    IReadOnlyList<CreatureGeometrySource> Sources { get; }
    Task<Stream> OpenVerifiedAsync(CancellationToken token);
    Task FinalizeAsync(GeometryDisplayAuthorization selection, CancellationToken token);
}
internal sealed record GeometryStoreFinalized(CreatureGeometryManifest? Manifest, IReadOnlyList<CreatureGeometrySource> Sources);
internal interface IGeometryStorePublication : IAsyncDisposable
{
    CreatureGeometryState State { get; }
    CreatureGeometryManifest? Manifest { get; }
    IReadOnlyList<CreatureGeometrySource> Sources { get; }
    CreatureGeometryProblem? Problem { get; }
    Task<GeometryStoreFinalized> FinalizeAsync(GeometryDisplayAuthorization selection, CancellationToken token);
}
