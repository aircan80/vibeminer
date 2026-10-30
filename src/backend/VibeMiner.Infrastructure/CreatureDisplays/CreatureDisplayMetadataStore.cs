using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Transient selected-slot chains. No model bytes or per-Creature selection rows are acquired or persisted.</summary>
public sealed class CreatureDisplayMetadataStore : ICreatureDisplayMetadataStore, IAsyncDisposable
{
    private readonly CreatureDisplayCoordinator coordinator;
    public CreatureDisplayMetadataStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, CreaturesStore creatures,
        TableDataService tables, AcquisitionResources resources) => coordinator = new(new CreatureDisplaySources(catalog, objects, creatures, tables, resources));
    internal CreatureDisplayMetadataStore(ICreatureDisplaySources sources) => coordinator = new(sources);
    internal (int Consumers, bool Stopping, bool Worker) Pressure => coordinator.Pressure;
    public Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request, CancellationToken token) => coordinator.ReadAsync(request, token);
    public Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token) => coordinator.PrepareAsync(request, progress, token);
    public Task<CreatureDisplayFactsResult> ReadFactsAsync(CreatureDisplayMetadataRequest request, CancellationToken token) => coordinator.ReadFactsAsync(request, token);
    internal Task<GeometryDisplayAuthorizationResult> AuthorizeGeometryAsync(CreatureDisplayMetadataRequest original, CancellationToken token) =>
        coordinator.AuthorizeGeometryAsync(original, token);
    public ValueTask DisposeAsync() => coordinator.DisposeAsync();
}
