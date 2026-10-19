using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsStoreEvidenceTests
{
    [Fact]
    public async Task EmittedZeroItemBridgeEffectAndSpellAreOrdinaryMathematicalKeys()
    {
        await using var f = await ItemEffectsStoreFixture.Create(twoItems: true, extraItem: 0);
        await f.Publish(ItemEffectsSourceRole.Bridge, await f.Source(ItemEffectsSourceRole.Bridge, [new Db2Row(0, false, null, ["0", "0", "0"])]));
        var selection = f.Selection with { ItemId = "0" };
        var ready = await f.Store.PrepareLinksAsync(selection, _ => Task.CompletedTask, default);
        Assert.True(ready.Admitted); Assert.Equal("1", ready.ObservedAssociations);
        var edge = Assert.Single((await f.Store.ReadPageAsync(new(selection, ready.Snapshot!.ArtifactId), default)).Edges);
        Assert.Equal("0", edge.Bridge.RowId); Assert.Equal("0", edge.ParentItemId.Value); Assert.Equal("0", edge.ItemEffectId.Value);
        var names = await f.Store.ReadNamesAsync(new(selection, ready.Snapshot.ArtifactId,
            f.Desired[ItemEffectsSourceRole.Effect].Artifact.ContentKey, [edge.Key]), default);
        var outcome = Assert.Single(names.Outcomes); Assert.Equal(ItemEffectNameState.Ready, outcome.State);
        Assert.Equal("0", outcome.Effect.SpellId!.Value); Assert.Equal("0", outcome.Target!.RowId); Assert.Equal(" ", outcome.Text);
        Assert.Empty(f.Prepared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroObservedLinksRemainScopedInsteadOfClaimingGlobalNoEffects(bool partial)
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        await f.Publish(ItemEffectsSourceRole.Bridge, await f.Source(ItemEffectsSourceRole.Bridge, [], partial: partial));
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        Assert.Equal(ItemEffectsReadinessState.Ready, ready.State); Assert.Equal("0", ready.ObservedAssociations);
        Assert.Equal(partial ? "unencryptedSections" : "complete", ready.Source!.Snapshot!.Coverage.Scope);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        Assert.Empty(page.Edges); Assert.Null(page.NextCursor);
        Assert.Equal(0, f.Store.Pressure.TargetSlots); Assert.Empty(f.Prepared);
    }

    [Fact]
    public async Task EightNamesKeepOwnPartialAbsenceAmbiguityCopyAndOriginalTextStates()
    {
        await using var f = await ItemEffectsStoreFixture.Create();
        var bridge = Enumerable.Range(0, 8).Select(id => new Db2Row((uint)(500 + id), false, null, [N(500 + id), N(id), "1"])).ToArray();
        var effects = Enumerable.Range(0, 8).Select(id => new Db2Row((uint)id, false, null,
            [N(id), "0", "0", "4294967295", "-1", "-2", "65536", N(id), "70000", "-1"])).ToArray();
        Db2Row[] names = [new(0, false, null, ["0", " "]), new(1, false, null, ["1", ""]),
            new(2, false, null, ["2", ""], [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")]),
            new(3, false, null, ["3", new string('x', 4097)]), new(4, false, null, ["4", "duplicate one"]),
            new(4, false, null, ["4", "duplicate two"]), new(6, false, null, ["6", "valid"]), new(7, true, 0, ["7", " "])];
        foreach (var role in Enum.GetValues<ItemEffectsSourceRole>())
            await f.Publish(role, await f.Source(role, role == ItemEffectsSourceRole.Bridge ? bridge : role == ItemEffectsSourceRole.Effect ? effects : names, partial: role == ItemEffectsSourceRole.Name));
        var ready = await f.Store.PrepareLinksAsync(f.Selection, _ => Task.CompletedTask, default);
        var page = await f.Store.ReadPageAsync(new(f.Selection, ready.Snapshot!.ArtifactId), default);
        var effect = (await f.H.Catalog.ReadItemEffectsSourceAsync(f.H.Opening, ItemEffectsSourceRole.Effect, default))!;
        var request = new ItemEffectNamesRequest(f.Selection, ready.Snapshot.ArtifactId, effect.Artifact.ContentKey, page.Edges.Select(edge => edge.Key).ToArray());
        var result = await f.Store.ReadNamesAsync(request, default);
        Assert.Equal(new[] { ItemEffectNameState.Ready, ItemEffectNameState.EmptyText, ItemEffectNameState.InvalidText,
            ItemEffectNameState.TextTooLarge, ItemEffectNameState.Ambiguous, ItemEffectNameState.NotFoundInAdmittedScope,
            ItemEffectNameState.Ready, ItemEffectNameState.Ready }, result.Outcomes.Select(outcome => outcome.State));
        Assert.Equal(" ", result.Outcomes[0].Text); Assert.Equal("", result.Outcomes[1].Text);
        var invalid = result.Outcomes[2]; Assert.Null(invalid.Text); Assert.Equal("1", result.Sources[1].Snapshot!.TextIssueCount);
        Assert.Equal(invalid.Target! with { ColumnIndex = 1 }, invalid.Issue!.Source);
        Assert.Equal("4097", result.Outcomes[3].TextUnits); Assert.Null(result.Outcomes[3].Text);
        Assert.Equal("2", result.Outcomes[4].ObservedCandidates); Assert.Null(result.Outcomes[4].Target);
        Assert.Equal(ItemEffectsAbsence.UnknownOutsideReadableScope, result.Outcomes[5].Absence);
        Assert.True(result.Outcomes[7].Target!.IsCopy); Assert.Equal("0", result.Outcomes[7].Target!.CopySourceId);
        Assert.Empty(f.Prepared);
    }
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
