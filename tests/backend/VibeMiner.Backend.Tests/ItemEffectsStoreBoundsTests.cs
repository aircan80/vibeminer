using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsStoreBoundsTests
{
    [Theory]
    [InlineData(ItemEffectsSourceRole.Bridge, "wdc5-values-v7+table-admission-v3", false, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceRole.Name, "wdc5-values-v7+table-admission-projection-v3", true, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v7+table-admission-v3", false, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v7+table-admission-v4", false, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v7+table-admission-projection-v4", true, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceRole.Bridge, "wdc5-values-v7+table-admission-v4", false, ItemEffectsSourceState.Unsupported)]
    [InlineData(ItemEffectsSourceRole.Name, "wdc5-values-v7+table-admission-projection-v4", true, ItemEffectsSourceState.Unsupported)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v999+table-admission-v4", false, ItemEffectsSourceState.Unsupported)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v4+table-admission-v1", false, ItemEffectsSourceState.Unsupported)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v4+table-admission-projection-v1", true, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-fixed-values-v1+table-admission-v1", false, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-fixed-values-v1+table-admission-projection-v1", true, ItemEffectsSourceState.Unsupported)]
    [InlineData(ItemEffectsSourceRole.Bridge, "wdc5-fixed-values-v3+table-admission-v1", false, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Name, "wdc5-values-v5+table-admission-v1", false, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Name, "wdc5-values-v6+table-admission-v1", false, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceRole.Effect, "wdc5-values-v7+table-admission-v2", false, ItemEffectsSourceState.NotPrepared)]
    public void CurrentAndHistoricalInterpretationAreFiniteAndRoleScoped(ItemEffectsSourceRole role, string revision,
        bool projected, ItemEffectsSourceState state) => Assert.Equal(state, ItemEffectsStore.RevisionState(role, revision, projected));
}
