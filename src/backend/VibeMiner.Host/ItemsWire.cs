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
    internal static object Facts(ItemFactsResult result) => new
    {
        result.Context, result.ArtifactId, result.ItemId, result.PolicyRevision, result.LabelRevision, result.Sparse,
        facts = result.Facts?.Select(fact => new
        {
            fact.Key, fact.State, fact.Agreement,
            values = fact.Values.Select(value => new { value.TableId, value.Table, value.Column, value.ColumnIndex, value.RecordId, value.Value, value.Label }).ToArray()
        }).ToArray()
    };
    internal static object Labels(ItemLabelLists lists)
    {
        static object[] Of(IReadOnlyList<ItemLabel> labels) => labels.Select(label => (object)new { label.Value, label.Label }).ToArray();
        return new { lists.LabelRevision, quality = Of(lists.Quality), inventoryType = Of(lists.InventoryType), bonding = Of(lists.Bonding), trigger = Of(lists.Trigger) };
    }
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
