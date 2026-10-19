using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class ItemClassificationsWire
{
    internal static object Batch(ItemClassificationBatch batch) => new
    {
        batch.Context, batch.ArtifactId, batch.PolicyRevision,
        sources = batch.Sources.Select(source => new
        {
            kind = Name(source.Kind), state = Name(source.State), source.Snapshot,
            source.IndexArtifactId, source.Problem
        }),
        outcomes = batch.Outcomes.Select(outcome => new
        {
            outcome.ItemId, outcome.Admitted,
            @class = Value(outcome.Class),
            subclass = outcome.Subclass is null ? null : Value(outcome.Subclass)
        })
    };

    private static object Value(ItemClassificationValue value) => new
    {
        state = Name(value.State), value.SourceKeys, value.Text, value.Target,
        value.ObservedCandidates, value.Problem
    };

    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? JsonNamingPolicy.CamelCase.ConvertName(value.ToString())
        : throw new DiscoveryException("ITEM_CLASSIFICATIONS_STATE_INVALID", "The classification outcome is inconsistent.");
}
