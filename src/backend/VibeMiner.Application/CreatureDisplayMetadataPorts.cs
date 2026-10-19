using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record CreatureDisplayMetadataRequest(CreaturesContext Context, string ArtifactId, string CreatureId, int Slot);
public interface ICreatureDisplayMetadataStore
{
    Task<CreatureDisplayMetadataResult> ReadDisplayAsync(CreatureDisplayMetadataRequest request, CancellationToken token);
    Task<CreatureDisplayMetadataResult> PrepareDisplayAsync(CreatureDisplayMetadataRequest request,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token);
}
