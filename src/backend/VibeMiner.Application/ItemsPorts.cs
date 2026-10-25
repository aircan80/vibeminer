using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record ItemsPageRequest(ItemsContext Context, string ArtifactId,
    string Query = "", string? ExactItemId = null, int Limit = 50, string? After = null);
public sealed record ItemInspectRequest(ItemsContext Context, string ArtifactId, string ItemId);

public interface IItemsStore
{
    Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token);

    // Use the shared TableDataService for sequential prerequisite demand. Do not
    // bypass its producer ownership or turn TABLE_DATA_BUSY into polling.
    Task<ItemsSnapshot> PrepareAsync(ItemsContext context,
        Func<ItemsPreparationProgress, Task> progress, CancellationToken token);
    Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token);
    Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token);
    // item-facts-v1: whether the admitted ItemSparse holds a row with this ID, for an ID outside the Item scope
    // (ItemSparse-only rows exist). Same artifact checks as InspectAsync; it reads one row page and never prepares.
    Task<bool> SparseRowExistsAsync(ItemInspectRequest request, CancellationToken token);
}
