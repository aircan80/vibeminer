using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Validated owned requests; completion includes actual source/producer cleanup.
/// Shared acquisition remains adapter-owned. Results and all nested collections are bounded, owned
/// immutable graphs, not mutable cache/caller lists. Only ItemEffectsService admits production publishers.</summary>
public interface IItemEffectsStore
{
    Task<ItemEffectsReadiness> ReadAsync(ItemEffectsSelection selection, CancellationToken token);
    Task<ItemEffectsReadiness> PrepareLinksAsync(ItemEffectsSelection selection,
        Func<ItemEffectsProgress, Task> progress, CancellationToken token);
    Task<ItemEffectsPage> ReadPageAsync(ItemEffectsPageRequest request, CancellationToken token);
    Task<ItemEffectDetailsResult> ReadDetailsAsync(ItemEffectDetailsRequest request, CancellationToken token);
    Task<ItemEffectDetailsResult> PrepareDetailsAsync(ItemEffectDetailsRequest request,
        Func<ItemEffectsProgress, Task> progress, CancellationToken token);
    Task<ItemEffectNamesResult> ReadNamesAsync(ItemEffectNamesRequest request, CancellationToken token);
    Task<ItemEffectNamesResult> PrepareNamesAsync(ItemEffectNamesRequest request,
        Func<ItemEffectsProgress, Task> progress, CancellationToken token);
}
