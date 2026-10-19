using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class ItemIconsWire
{
    internal static object Batch(ItemIconBatch batch) => new
    {
        batch.Context, batch.ArtifactId,
        outcomes = batch.Outcomes.Select(outcome => new
        {
            outcome.ItemId, outcome.FileDataId,
            status = Enum.IsDefined(outcome.Status) ? JsonNamingPolicy.CamelCase.ConvertName(outcome.Status.ToString())
                : throw new DiscoveryException("ITEM_ICONS_STATE_INVALID", "The icon outcome is inconsistent."),
            outcome.Image, outcome.Problem
        }).ToArray()
    };
}
