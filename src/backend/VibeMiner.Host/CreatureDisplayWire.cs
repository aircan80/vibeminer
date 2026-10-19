using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Host;

internal static class CreatureDisplayWire
{
    internal static object Result(CreatureDisplayMetadataResult value) => new
    {
        value.Context, value.ArtifactId, value.CreatureId, value.Slot, value.MetadataPolicy,
        value.Opening, value.CreatureSource, value.CreatureCoverage,
        selected = value.Selected is null ? null : new
        {
            value.Selected.Slot, value.Selected.DisplayId, value.Selected.Probability,
            state = Name(value.Selected.State), value.Selected.DisplayField, value.Selected.ProbabilityField
        },
        state = Name(value.State), appearance = Name(value.Appearance),
        sources = value.Sources.Select(source => new
        { role = Name(source.Role), source.Snapshot, source.MatchCount, source.Row }),
        absence = value.Absence is { } absence ? Name(absence) : null,
        value.ModelId, value.ModelFileDataId, value.RequiredTableId,
        problem = value.Problem is null ? null : new
        {
            value.Problem.Code, value.Problem.Message, value.Problem.Retryable,
            target = value.Problem.Target is { } target ? Name(target) : null
        }
    };
    private static string Name<T>(T value) where T : struct, Enum => Enum.IsDefined(value)
        ? JsonNamingPolicy.CamelCase.ConvertName(value.ToString())
        : throw new DiscoveryException("CREATURE_DISPLAY_STATE_INVALID", "The display metadata outcome is inconsistent.");
}
