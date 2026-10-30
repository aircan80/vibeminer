using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record CreatureDisplayMetadataRequest(CreaturesContext Context, string ArtifactId, string CreatureId, int Slot);
public interface ICreatureDisplayMetadataStore
{
    Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request, CancellationToken token);
    Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token);
    /// <summary>Local only, like ReadDisplayAsync: the same chain plus the reviewed facts of each uniquely matched row.</summary>
    Task<CreatureDisplayFactsResult> ReadFactsAsync(CreatureDisplayMetadataRequest request, CancellationToken token) =>
        throw new DiscoveryException("CREATURE_DISPLAY_FACTS_UNAVAILABLE", "Creature display facts aren't available from this engine.");
}
