using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record ItemClassificationBatchRequest(ItemsContext Context, string ArtifactId,
    IReadOnlyList<string> ItemIds, bool IncludeSubclass = false, string PolicyRevision = "classification-label-index-v1");
public interface IItemClassificationsStore
{
    bool SupportsSubclassLabels => false;
    Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token);
}
