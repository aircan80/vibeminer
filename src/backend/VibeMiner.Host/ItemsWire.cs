using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class ItemsWire
{
    internal static object Page(ItemsPage page) => new
    {
        page.Context, page.ArtifactId, page.Query, page.ExactItemId,
        entries = page.Entries.Select(Summary).ToArray(), page.NextCursor
    };
    internal static object Inspect(ItemInspectResult result) => new
    {
        result.Context, result.ArtifactId, result.ItemId,
        detail = result.Detail is { } detail ? new { summary = Summary(detail.Summary), detail.ItemRow, detail.SparseRow } : null
    };
    private static object Summary(ItemSummary summary) => new
    {
        summary.Id,
        name = new { state = summary.Name.State switch
        {
            ItemNameState.Valid => "valid", ItemNameState.Empty => "empty",
            ItemNameState.Invalid => "invalid", ItemNameState.MissingSparse => "missingSparse",
            _ => throw new DiscoveryException("ITEMS_STATE_INVALID", "The Items name state is inconsistent.")
        }, summary.Name.Value },
        summary.BaseValues, summary.IconFileDataId
    };
}
