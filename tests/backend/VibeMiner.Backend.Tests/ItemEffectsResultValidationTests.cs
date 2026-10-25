using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsResultValidationTests
{
    internal static readonly ItemEffectDetailsRequest Details = new(ItemEffectsValidationTests.Selection, ItemEffectsValidationTests.Index, [new("0")]);
    internal static readonly ItemEffectNamesRequest Names = new(Details.Selection, Details.BridgeIndexArtifactId, new('d', 64), Details.Edges);
    internal static readonly string Stamp = "2026-10-19T00:00:00Z";

    [Fact]
    public void SourceAndReadinessNullStatesCannotCarryUnadmittedEvidence()
    {
        var selection = Details.Selection;
        ItemEffectsValidation.Validate(new ItemEffectsReadiness(selection, false, ItemEffectsReadinessState.MissingItem), selection);
        ItemEffectsValidation.Validate(new ItemEffectsReadiness(selection, true, ItemEffectsReadinessState.NotPrepared), selection);
        Bad(() => ItemEffectsValidation.Validate(new ItemEffectsReadiness(selection, true, ItemEffectsReadinessState.MissingItem), selection));
        Bad(() => ItemEffectsValidation.Validate(new ItemEffectsReadiness(selection, false, ItemEffectsReadinessState.NotPrepared), selection));
        Bad(() => ItemEffectsValidation.Validate(new ItemEffectsReadiness(selection, true, ItemEffectsReadinessState.NotPrepared,
            Source: new(ItemEffectsSourceRole.Bridge, ItemEffectsSourceState.NotChecked, Source(ItemEffectsSourceRole.Bridge))), selection));
    }

    [Fact]
    public void PartialUniqueZeroEffectKeepsAllNineOriginalInputsAndRawBoundaries()
    {
        var source = Source(ItemEffectsSourceRole.Effect, partial: true);
        var fields = Fields(source); var outcome = new ItemEffectOutcome(new("0"), "0", ItemEffectState.ObservedReady, "1", Target(source, "0"), fields);
        var result = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy, new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Ready, source), [outcome]);
        ItemEffectsValidation.Validate(result, Details);
        Assert.Equal("4294967295", fields[2].Value); Assert.Equal(new TableReference("Spell", "ID"), source.Columns[7].Reference);
        foreach (var column in new[] { 3, 6, 8, 9 })
        {
            var edited = fields.ToArray(); edited[column - 1] = edited[column - 1] with { Evidence = ItemEffectEvidenceKind.StrictNumeric };
            Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [outcome with { Fields = edited }] }, Details));
        }
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [outcome with { Target = outcome.Target! with { RowId = "1" } }] }, Details));
    }

    [Fact]
    public void AmbiguityHasCountAndNoFirstRowAndMissingUsesItsOwnCoverage()
    {
        var source = Source(ItemEffectsSourceRole.Effect, partial: true);
        var ambiguity = new ItemEffectOutcome(new("0"), "1", ItemEffectState.Ambiguous, "2");
        var result = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy, new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Ready, source), [ambiguity]);
        ItemEffectsValidation.Validate(result, Details);
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [ambiguity with { Target = Target(source, "1") }] }, Details));
        var missing = new ItemEffectOutcome(new("0"), "1", ItemEffectState.NotFoundInAdmittedScope, "0", Absence: ItemEffectsAbsence.UnknownOutsideReadableScope);
        ItemEffectsValidation.Validate(result with { Outcomes = [missing] }, Details);
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [missing with { Absence = ItemEffectsAbsence.AbsentFromCompleteSource }] }, Details));
    }

    [Fact]
    public void NegativeEffectDoesNotAcquireOrInventSourceAndZeroSpellHasActualMatchedName()
    {
        var absent = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
            new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotChecked), [new(new("0"), "-1", ItemEffectState.EffectKeyOutOfRange)]);
        ItemEffectsValidation.Validate(absent, Details);
        Bad(() => ItemEffectsValidation.Validate(absent with { Outcomes = [absent.Outcomes[0] with { ItemEffectId = "0" }] }, Details));
        var names = NameResult("  "); ItemEffectsValidation.Validate(names, Names);
        Assert.Equal("0", names.Outcomes[0].Effect.SpellId!.Value); Assert.Equal("0", names.Outcomes[0].Target!.RowId);
        Bad(() => ItemEffectsValidation.Validate(names with { Sources = [names.Sources[0], new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.NotChecked)] }, Names));
    }

    [Fact]
    public void NamesKeepTypedEmptyInvalidOversizedAndPartialPositiveEvidence()
    {
        var value = NameResult("ok"); var outcome = value.Outcomes[0];
        ItemEffectsValidation.Validate(value with { Outcomes = [outcome with { State = ItemEffectNameState.EmptyText, Text = "" }] }, Names);
        var invalid = value with { Sources = [value.Sources[0], value.Sources[1] with { Snapshot = value.Sources[1].Snapshot! with { TextIssueCount = "1" } }],
            Outcomes = [outcome with { State = ItemEffectNameState.InvalidText, Text = null,
                Issue = new("DB2_TEXT_ENCODING_INVALID", outcome.Target! with { ColumnIndex = 1 }) }] };
        ItemEffectsValidation.Validate(invalid, Names);
        ItemEffectsValidation.Validate(value with { Outcomes = [outcome with { State = ItemEffectNameState.TextTooLarge, Text = null, TextUnits = "4097" }] }, Names);
        Bad(() => ItemEffectsValidation.Validate(value with { Outcomes = [outcome with { Text = "" }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(value with { Outcomes = [outcome with { Text = "\ud800" }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(invalid with { Outcomes = [outcome with { State = ItemEffectNameState.InvalidText, Text = null,
            Issue = new("DB2_TEXT_ENCODING_INVALID", outcome.Target! with { ColumnIndex = 0 }) }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(value with { ItemEffectArtifactId = new('9', 64) }, Names));
    }

    [Theory]
    [InlineData("0")]
    [InlineData(null)]
    public void InvalidNameIssueRequiresOriginalSourceIssueCount(string? count)
    {
        var value = NameResult("old"); var outcome = value.Outcomes[0];
        var invalid = value with { Sources = [value.Sources[0], value.Sources[1] with { Snapshot = value.Sources[1].Snapshot! with { TextIssueCount = count! } }],
            Outcomes = [outcome with { State = ItemEffectNameState.InvalidText, Text = null,
                Issue = new("DB2_TEXT_ENCODING_INVALID", outcome.Target! with { ColumnIndex = 1 }) }] };
        Bad(() => ItemEffectsValidation.Validate(invalid, Names));
    }

    [Fact]
    public void SourceIndependentNegativeStatesSurviveStaleTargetButOldNamesNeverDo()
    {
        var problem = new ItemEffectsProblem("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.", true);
        var effect = new ItemEffectSpellEvidence(ItemEffectState.Stale, "0", Problem: problem);
        var result = new ItemEffectNamesResult(Names.Selection, Names.BridgeIndexArtifactId, Names.ItemEffectArtifactId, Names.PolicyRevision,
            [new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Stale, Problem: problem), new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.NotChecked)],
            [new(new("0"), effect, ItemEffectNameState.EffectNotReady)]);
        ItemEffectsValidation.Validate(result, Names);
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [result.Outcomes[0] with { Text = "old" }] }, Names));
    }

    [Theory]
    [InlineData(ItemEffectsSourceState.Stale, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceState.Stale, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceState.Stale, ItemEffectsSourceState.Unavailable)]
    [InlineData(ItemEffectsSourceState.Corrupt, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceState.Corrupt, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceState.Corrupt, ItemEffectsSourceState.Unavailable)]
    [InlineData(ItemEffectsSourceState.ResourceLimit, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceState.ResourceLimit, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceState.ResourceLimit, ItemEffectsSourceState.Unavailable)]
    [InlineData(ItemEffectsSourceState.Unavailable, ItemEffectsSourceState.Ready)]
    [InlineData(ItemEffectsSourceState.Unavailable, ItemEffectsSourceState.NotPrepared)]
    [InlineData(ItemEffectsSourceState.Unavailable, ItemEffectsSourceState.Unavailable)]
    public void LateEffectInvalidationPreservesConsultedNameSourceWithoutDependentFacts(ItemEffectsSourceState effectState, ItemEffectsSourceState nameState)
    {
        var original = NameResult("earlier name");
        var problem = effectState switch
        {
            ItemEffectsSourceState.Stale => new ItemEffectsProblem("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.", true),
            ItemEffectsSourceState.Corrupt => new("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation.", false),
            ItemEffectsSourceState.Unavailable => new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true),
            _ => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.", false)
        };
        var name = nameState switch
        {
            ItemEffectsSourceState.Ready => original.Sources[1],
            ItemEffectsSourceState.NotPrepared => new ItemEffectsSource(ItemEffectsSourceRole.Name, nameState, HistoricalArtifactId: new('e', 64)),
            _ => new(ItemEffectsSourceRole.Name, nameState, Problem: new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true))
        };
        var effect = new ItemEffectSpellEvidence(Enum.Parse<ItemEffectState>(effectState.ToString()), "0", Problem: problem);
        var cleared = new ItemEffectNameOutcome(new("0"), effect, ItemEffectNameState.EffectNotReady);
        var result = original with { Sources = [new(ItemEffectsSourceRole.Effect, effectState, Problem: problem), name], Outcomes = [cleared] };
        ItemEffectsValidation.Validate(result, Names);
        Assert.Same(name, result.Sources[1]);
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { Text = "earlier name" }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { ObservedCandidates = "1" }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { Target = original.Outcomes[0].Target }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { Absence = ItemEffectsAbsence.UnknownOutsideReadableScope }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { Effect = effect with { SpellId = original.Outcomes[0].Effect.SpellId } }] }, Names));
        Bad(() => ItemEffectsValidation.Validate(result with { Outcomes = [cleared with { Effect = effect with { ObservedCandidates = "1" } }] }, Names));
    }

    [Fact]
    public void InitialIneligibleEffectPathsStillRequireUnprobedNameSource()
    {
        var original = NameResult("name"); var unprepared = new ItemEffectsSource(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotPrepared);
        var cases = new[]
        {
            (original.Sources[0], new ItemEffectNameOutcome(new("0"), original.Outcomes[0].Effect with { SpellId = new(ItemEffectsValidation.EffectTable, 7, "-1") }, ItemEffectNameState.SpellKeyOutOfRange)),
            (original.Sources[0], new ItemEffectNameOutcome(new("0"), new(ItemEffectState.NotFoundInAdmittedScope, "0", "0", Absence: ItemEffectsAbsence.UnknownOutsideReadableScope), ItemEffectNameState.EffectNotReady)),
            (unprepared, new ItemEffectNameOutcome(new("0"), new(ItemEffectState.NotPrepared, "0"), ItemEffectNameState.EffectNotReady)),
            (new ItemEffectsSource(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotChecked), new ItemEffectNameOutcome(new("0"), new(ItemEffectState.EffectKeyOutOfRange, "-1"), ItemEffectNameState.EffectNotReady))
        };
        foreach (var (source, outcome) in cases)
        {
            var result = original with { Sources = [source, new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.NotChecked)], Outcomes = [outcome] };
            ItemEffectsValidation.Validate(result, Names);
            Bad(() => ItemEffectsValidation.Validate(result with { Sources = [source, original.Sources[1]] }, Names));
        }
    }

    [Theory]
    [InlineData(ItemEffectsSourceState.Unavailable)] [InlineData(ItemEffectsSourceState.Protected)]
    [InlineData(ItemEffectsSourceState.Unsupported)] [InlineData(ItemEffectsSourceState.Corrupt)]
    [InlineData(ItemEffectsSourceState.ResourceLimit)] [InlineData(ItemEffectsSourceState.Stale)]
    [InlineData(ItemEffectsSourceState.NotInBuild)]
    public void EveryOptionalFailureHasItsOwnFixedProblemAndErasesDependentEvidence(ItemEffectsSourceState state)
    {
        var (code, message) = state switch
        {
            ItemEffectsSourceState.Unavailable => ("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable."),
            ItemEffectsSourceState.Protected => ("ITEM_EFFECTS_SOURCE_PROTECTED", "The selected source is protected."),
            ItemEffectsSourceState.Unsupported => ("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported."),
            ItemEffectsSourceState.Corrupt => ("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation."),
            ItemEffectsSourceState.ResourceLimit => ("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded."),
            ItemEffectsSourceState.NotInBuild => ("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source."),
            _ => ("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.")
        };
        var problem = new ItemEffectsProblem(code, message, state is ItemEffectsSourceState.Unavailable or ItemEffectsSourceState.Stale);
        var details = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy, new(ItemEffectsSourceRole.Effect, state, Problem: problem),
            [new(new("0"), "0", Enum.Parse<ItemEffectState>(state.ToString()), Problem: problem)]);
        ItemEffectsValidation.Validate(details, Details);
        Bad(() => ItemEffectsValidation.Validate(details with { Source = details.Source with { Snapshot = Source(ItemEffectsSourceRole.Effect) } }, Details));
        var names = NameResult("old");
        var failed = names with { Sources = [names.Sources[0], new(ItemEffectsSourceRole.Name, state, Problem: problem)],
            Outcomes = [names.Outcomes[0] with { State = Enum.Parse<ItemEffectNameState>(state.ToString()), ObservedCandidates = null, Target = null, Text = null, Problem = problem }] };
        ItemEffectsValidation.Validate(failed, Names);
        Bad(() => ItemEffectsValidation.Validate(failed with { Outcomes = [failed.Outcomes[0] with { Text = "old" }] }, Names));
        if (state != ItemEffectsSourceState.Stale)
        {
            var readiness = new ItemEffectsReadiness(Details.Selection, true, Enum.Parse<ItemEffectsReadinessState>(state.ToString()), Source: new(ItemEffectsSourceRole.Bridge, state, Problem: problem), Problem: problem);
            ItemEffectsValidation.Validate(readiness, Details.Selection);
            // A table missing from the build is settled for that build: it never claims a Retry (#85).
            var flipped = problem with { Retryable = !problem.Retryable };
            Bad(() => ItemEffectsValidation.Validate(readiness with { Source = readiness.Source! with { Problem = flipped }, Problem = flipped }, Details.Selection));
        }
    }

    [Fact]
    public void FieldUnavailableKeepsOnlySafeSourceAndProblemWithoutInventingLookupFacts()
    {
        var problem = new ItemEffectsProblem("ITEM_EFFECTS_FIELD_UNAVAILABLE", "A required effect input is unavailable.", false);
        var value = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
            new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Ready, Source(ItemEffectsSourceRole.Effect)), [new(new("0"), "0", ItemEffectState.FieldUnavailable, Problem: problem)]);
        ItemEffectsValidation.Validate(value, Details);
        Bad(() => ItemEffectsValidation.Validate(value with { Outcomes = [value.Outcomes[0] with { ObservedCandidates = "1" }] }, Details));
    }

    [Fact]
    public void HistoricalNotPreparedDoesNotCarryCurrentSnapshotOrPermanentStaleProblem()
    {
        var value = new ItemEffectDetailsResult(Details.Selection, Details.BridgeIndexArtifactId, ItemEffectsValidation.InputPolicy,
            new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.NotPrepared, HistoricalArtifactId: new('1', 64)), [new(new("0"), "0", ItemEffectState.NotPrepared)]);
        ItemEffectsValidation.Validate(value, Details);
        Bad(() => ItemEffectsValidation.Validate(value with { Source = value.Source with { HistoricalArtifactId = "not-a-key" } }, Details));
    }

    [Fact]
    public void ReturnedPageCannotImplyLookupForOutOfSignedParentDomain()
    {
        var request = new ItemEffectsPageRequest(Details.Selection with { ItemId = "2147483648" }, Details.BridgeIndexArtifactId);
        var result = new ItemEffectsPage(request.Selection, request.BridgeIndexArtifactId, ItemEffectsValidation.OrderPolicy, Source(ItemEffectsSourceRole.Bridge), "0", [], null);
        Bad(() => ItemEffectsValidation.Validate(result, request));
    }

    [Fact]
    public void PageCursorMustBindItsRequestAndAdvancePastExactlyTheReturnedTuple()
    {
        var request = new ItemEffectsPageRequest(Details.Selection, Details.BridgeIndexArtifactId);
        var source = Source(ItemEffectsSourceRole.Bridge);
        var edge = new ItemEffectsEdge(new("0"), Target(source, "0") with { SourceOrdinal = "0" }, new(source.TableId, 2, "0"), new(source.TableId, 1, "0"));
        var result = new ItemEffectsPage(request.Selection, request.BridgeIndexArtifactId, ItemEffectsValidation.OrderPolicy, source, "2", [edge], ItemEffectsValidationTests.Cursor(request, "1"));
        ItemEffectsValidation.Validate(result, request); // Equal source/index ordinal is valid.
        Bad(() => ItemEffectsValidation.Validate(result with { NextCursor = ItemEffectsValidationTests.Cursor(request with { Limit = 8 }, "1") }, request));
        Bad(() => ItemEffectsValidation.Validate(result with { NextCursor = ItemEffectsValidationTests.Cursor(request, "2") }, request));
        var continuation = request with { After = result.NextCursor };
        var next = result with { Edges = [edge with { Key = new("1"), Bridge = edge.Bridge with { RowId = "1", SourceOrdinal = "1" } }], NextCursor = null };
        ItemEffectsValidation.Validate(next, continuation); // A valid byte-limited continuation skips no occurrence.
        Bad(() => ItemEffectsValidation.Validate(next with { Edges = [edge] }, continuation));
    }

    internal static ItemEffectNamesResult NameResult(string text)
    {
        var effect = Source(ItemEffectsSourceRole.Effect, partial: true); var name = Source(ItemEffectsSourceRole.Name, partial: true);
        return new(Names.Selection, Names.BridgeIndexArtifactId, Names.ItemEffectArtifactId, Names.PolicyRevision,
            [new(ItemEffectsSourceRole.Effect, ItemEffectsSourceState.Ready, effect), new(ItemEffectsSourceRole.Name, ItemEffectsSourceState.Ready, name)],
            [new(new("0"), new(ItemEffectState.ObservedReady, "0", "1", Target(effect, "0"), new(ItemEffectsValidation.EffectTable, 7, "0")), ItemEffectNameState.Ready, "1", Target(name, "0"), text)]);
    }
    internal static ItemEffectsSourceReference Target(TableDataSnapshot source, string id) => new(source.TableId, source.ArtifactId, id, false, null);
    internal static ItemEffectFieldEvidence[] Fields(TableDataSnapshot source) => source.Columns.Skip(1).Select((column, j) =>
    {
        var i = j + 1; var raw = i is 3 or 6 or 8;
        return new ItemEffectFieldEvidence(column.Name, i, raw ? "4294967295" : "0", raw ? ItemEffectEvidenceKind.RawPhysicalInteger : i == 9 ? ItemEffectEvidenceKind.UnverifiedCandidate : ItemEffectEvidenceKind.StrictNumeric,
            column.Interpretation, i switch { 6 => new("SpellCategory", "ID"), 8 => new("ChrSpecialization", "ID"), 9 => new("PlayerCondition", "ID"), _ => null });
    }).ToArray();
    internal static TableDataSnapshot Source(ItemEffectsSourceRole role, bool partial = false)
    {
        var columns = role switch
        {
            ItemEffectsSourceRole.Bridge => new[] { new TableDataColumn("ID", "integer", true, null), new("ItemEffectID", "integer", false, new("ItemEffect", "ID")), new("ItemID", "integer", false, new("Item", "ID")) },
            ItemEffectsSourceRole.Effect => new[] { "ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec", "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID" }.Select((name, i) => new TableDataColumn(name, "integer", i == 0,
                i switch { 7 => new("Spell", "ID"), 9 => new("PlayerCondition", "ID"), _ => null }, i is 3 or 6 or 8 ? new("raw-physical-integer-v1", 32, false, 16, i != 3, "Physical raw evidence.") : null)).ToArray(),
            _ => [new TableDataColumn("ID", "integer", true, null), new("Name_lang", "string", false, null)]
        };
        var (table, fdid, layout, definition, artifact) = role switch
        {
            ItemEffectsSourceRole.Bridge => ("00cb674f", "3177687", "96F083AD", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", new string('f', 64)),
            ItemEffectsSourceRole.Effect => ("4002a5b1", "969941", "4CA77678", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c", new string('d', 64)),
            _ => ("46c66698", "1990283", "782EE721", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667", new string('e', 64))
        };
        var location = new ContentLocation("direct", "0", "1", null);
        MetadataReceipt Metadata(string sourceRole) => new(sourceRole, new('1', 32), new('2', 32), new('3', 64), "1", "1", "https://example.invalid/source", Stamp, Stamp, "test", location);
        return new("wow", Details.Selection.Context.BuildIdentity, "us", table, "enUS", artifact, Stamp, layout, definition, "b9a63096cdd99b26967f17f9515377ae62cadfd7",
            "wdc5-values-v7+table-admission" + (role == ItemEffectsSourceRole.Effect ? "-v4" : "-v3"),
            new(new('1', 32), new('2', 32), new('3', 64), "1", "1", "https://example.invalid/source", Stamp, Stamp, "test", location),
            new(fdid, "enUS", "test", Metadata("encoding"), Metadata("root"), [new("0", "0", new('1', 32), "0", "0", "0", "0", "0", null)]), columns,
            new(partial ? "unencryptedSections" : "complete", partial ? "3" : "2", "2", "0", partial ? "1" : "0", partial ? "1" : "0", "0", partial ? "1" : "0", "0"), "2");
    }
    private static void Bad(Action action) => Assert.Equal("ITEM_EFFECTS_RESULT_INVALID", Assert.Throws<DiscoveryException>(action).Code);
}
