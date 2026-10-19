using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class CreaturesWire
{
    internal static object Readiness(CreaturesReadiness value) => new
    { value.Snapshot, dependencyState = Name(value.DependencyState), value.SourceArtifactId };
    internal static object Snapshot(CreaturesSnapshot value) => value;
    internal static object Page(CreaturesPage value) => new
    { value.Context, value.ArtifactId, value.Query, value.ExactCreatureId, entries = value.Entries.Select(Summary), value.NextCursor };
    internal static object Inspect(CreatureInspectResult value) => new
    {
        value.Context, value.ArtifactId, value.CreatureId, state = Name(value.State), value.Coverage,
        detail = value.Detail is null ? null : new { summary = Summary(value.Detail.Summary), value.Detail.CreatureRow, value.Detail.Source }
    };
    private static object Summary(CreatureSummary value) => new
    {
        value.Id, name = new { state = Name(value.Name.State), value.Name.Value, value.Name.Issue }, value.Facts,
        displaySlots = value.DisplaySlots.Select(slot => new
        { slot.Slot, slot.DisplayId, slot.Probability, state = Name(slot.State), slot.DisplayField, slot.ProbabilityField })
    };
    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? JsonNamingPolicy.CamelCase.ConvertName(value.ToString())
        : throw new DiscoveryException("CREATURES_STATE_INVALID", "The Creature outcome is inconsistent.");
}
