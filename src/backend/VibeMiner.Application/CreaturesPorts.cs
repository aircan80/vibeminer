using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record CreaturesPageRequest(CreaturesContext Context, string ArtifactId,
    string Query = "", string? ExactCreatureId = null, int Limit = 50, string? After = null);
public sealed record CreatureInspectRequest(CreaturesContext Context, string ArtifactId, string CreatureId);

public interface ICreaturesStore
{
    Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token);
    Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token);
    Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token);
    Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token);
}
