using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;
using static VibeMiner.Concepts.Tests.ItemEffects.EffectsWorld;

namespace VibeMiner.Concepts.Tests.ItemEffects;

/// <summary>
/// The old item effects rule tests (ItemEffectsStoreTests, ItemEffectsStoreEvidenceTests, ItemEffectsResultValidationTests,
/// ItemEffectsDefinitionSourceTests, ItemEffectAdmissionTests' layer 6 side, ItemEffectsValidationTests' request rules) on the
/// real <see cref="ItemEffectsService"/> over layer 5 fakes. Synthetic rows; definition blocks are the bundled ones.
/// </summary>
public sealed class ItemEffectsRulesTests
{
    private static async Task<ItemEffectsPage> PageAsync(EffectsWorld world, uint item, int limit = ItemEffectQueries.MaxLimit)
    {
        await using var effects = await world.OpenAsync();
        return await effects.GetAsync(item, new(limit), Now, default);
    }

    // ImmutableLinksPreserveOccurrencesCopiesAndZero..., PartialUniqueZeroEffectKeepsAllNineOriginalInputsAndRawBoundaries,
    // NegativeEffectDoesNotAcquireOrInventSource...
    [Fact]
    public async Task Retail_links_keep_every_occurrence_copy_and_zero_key_and_a_negative_effect_is_out_of_range()
    {
        var world = new EffectsWorld().With(await Retail());
        var page = await PageAsync(world, 1);
        Assert.Equal(ItemEffectsPageState.Ok, page.State);
        Assert.Equal(ItemEffectsLink.ItemXItemEffect, page.Link);
        Assert.Null(page.Next);
        Assert.Equal([500u, 501, 502, 503], page.Entries.Select(entry => entry.Link.Row.Id));
        Assert.Equal(["0", "0", "-1", "0"], page.Entries.Select(entry => entry.ItemEffectId));
        Assert.All(page.Entries, entry => Assert.Equal("1", entry.ParentItemId));
        Assert.True(page.Entries[3].Link.Row.IsCopy);
        Assert.Equal(500u, page.Entries[3].Link.Row.CopySourceId);
        Assert.Equal([ItemEffectState.ObservedReady, ItemEffectState.ObservedReady, ItemEffectState.EffectKeyOutOfRange, ItemEffectState.ObservedReady],
            page.Entries.Select(entry => entry.Effect.State));
        Assert.Null(page.Entries[2].Effect.Row);
        Assert.Equal([ItemEffectNameState.Ready, ItemEffectNameState.Ready, ItemEffectNameState.EffectNotReady, ItemEffectNameState.Ready],
            page.Entries.Select(entry => entry.Name.State));
        Assert.All(page.Entries.Where(entry => entry.Name.State == ItemEffectNameState.Ready), entry => Assert.Equal(" ", entry.Name.Text));
        Assert.Equal((ItemEffectsSourceState.Ready, ItemEffectsSourceState.Ready, ItemEffectsSourceState.Ready), (page.Bridge.State, page.Effect.State, page.Name.State));
        // One item lookup, one ItemEffect lookup for the repeated key 0, one SpellName lookup for spell 0.
        Assert.Equal(3, world.Tables.Calls(nameof(IBuildTables.GetAsync)));
    }

    // EmittedZeroItemBridgeEffectAndSpellAreOrdinaryMathematicalKeys
    [Fact]
    public async Task Zero_is_an_ordinary_item_link_effect_and_spell_key()
    {
        var world = new EffectsWorld().With(ItemTable([Item(0)]), await BridgeTable([Bridge(0, 0, 0)]), await RetailTable([Effect(0, 0)]),
            await NameTable([Name(0, " ")]));
        var entry = Assert.Single((await PageAsync(world, 0)).Entries);
        Assert.Equal((0u, "0", "0"), (entry.Link.Row.Id, entry.ParentItemId, entry.ItemEffectId));
        Assert.Equal("0", entry.Effect.SpellId);
        Assert.Equal((ItemEffectNameState.Ready, " ", 0u), (entry.Name.State, entry.Name.Text, entry.Name.Row!.Row.Id));
    }

    // LocalReadVerifiesOnlyBridgeAndMissingItemNeverProbesPoisonedOptionalSource; the Item side of Missing, Unknown and Protected.
    [Fact]
    public async Task An_item_that_isnt_found_never_reads_a_link_effect_or_name_table()
    {
        var world = new EffectsWorld().With(await Retail());
        var missing = await PageAsync(world, 9);
        Assert.Equal(ItemEffectsPageState.MissingItem, missing.State);
        Assert.Null(missing.Link);
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked),
            (missing.Bridge.State, missing.Effect.State, missing.Name.State));
        Assert.Equal(0, world.Tables.Calls(nameof(IBuildTables.SchemaAsync)));
        Assert.Equal(0, world.Tables.Calls(nameof(IBuildTables.PageAsync)));

        var partial = new EffectsWorld().With(ItemTable([Item(1)], TableState.Partial, new Dictionary<uint, string> { [5] = Key }));
        Assert.Equal(ItemEffectsPageState.ItemUnknown, (await PageAsync(partial, 9)).State);
        var hidden = await PageAsync(partial, 5);
        Assert.Equal((ItemEffectsPageState.ItemProtected, Key), (hidden.State, hidden.ProtectedKey));
        var unreadable = await PageAsync(new EffectsWorld().With(ItemTable([], TableState.NeedsNetwork)), 1);
        Assert.Equal((ItemEffectsPageState.ItemNotReadable, TableState.NeedsNetwork), (unreadable.State, unreadable.Item!.State));
    }

    // ReturnedPageCannotImplyLookupForOutOfSignedParentDomain; UnsignedItemDomainIsPreservedWithoutSignedWrap
    [Fact]
    public async Task An_item_above_int32_is_out_of_range_and_no_link_is_looked_up()
    {
        var world = new EffectsWorld().With(ItemTable([Item(2147483648)]), await BridgeTable([Bridge(1, 0, -2147483648)]), await RetailTable([Effect(0)]),
            await NameTable([]));
        var page = await PageAsync(world, 2147483648);
        Assert.Equal(ItemEffectsPageState.ItemKeyOutOfRange, page.State);
        Assert.Equal(2147483648u, page.ItemId);
        Assert.Empty(page.Entries);
        Assert.Equal(0, world.Tables.Calls(nameof(IBuildTables.PageAsync)));
    }

    // ZeroObservedLinksRemainScopedInsteadOfClaimingGlobalNoEffects
    [Theory]
    [InlineData(TableState.Ready)]
    [InlineData(TableState.Partial)]
    public async Task No_links_is_an_empty_page_that_keeps_the_link_tables_scope(TableState state)
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(504, 7, 2)], state), await RetailTable([]), await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal(ItemEffectsPageState.Ok, page.State);
        Assert.Empty(page.Entries);
        Assert.Null(page.Next);
        Assert.Equal(state, page.Bridge.Table!.State);
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked), (page.Effect.State, page.Name.State));
    }

    // EightNamesKeepOwnPartialAbsenceAmbiguityCopyAndOriginalTextStates, NamesKeepTypedEmptyInvalidOversizedAndPartialPositiveEvidence
    [Fact]
    public async Task Names_keep_their_text_states_absence_scope_protection_and_copies()
    {
        var bridge = Enumerable.Range(0, 8).Select(i => Bridge((uint)(500 + i), i, 1)).ToArray();
        var effects = Enumerable.Range(0, 8).Select(i => Effect((uint)i, i)).ToArray();
        Db2Row[] names = [Name(0, " "), Name(1, ""), Name(2, "", issues: [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")]),
            Name(3, new string('x', 4097)), Name(4, new string('y', 4096)), Name(6, "valid"), Name(7, " ", true, 0)];
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable(bridge), await RetailTable(effects),
            await NameTable(names, TableState.Partial, new Dictionary<uint, string> { [5] = Key }));
        var page = await PageAsync(world, 1);
        Assert.Equal([ItemEffectNameState.Ready, ItemEffectNameState.EmptyText, ItemEffectNameState.InvalidText, ItemEffectNameState.TextTooLarge,
            ItemEffectNameState.Ready, ItemEffectNameState.Protected, ItemEffectNameState.Ready, ItemEffectNameState.Ready], page.Entries.Select(entry => entry.Name.State));
        var named = page.Entries.Select(entry => entry.Name).ToArray();
        Assert.Equal((" ", ""), (named[0].Text, named[1].Text));
        Assert.Equal((null, "DB2_TEXT_ENCODING_INVALID", 2u), (named[2].Text, named[2].IssueCode, named[2].Row!.Row.Id));
        Assert.Equal((null, 4097), (named[3].Text, named[3].TextUnits));
        Assert.Equal(4096, named[4].Text!.Length);
        Assert.Equal(Key, named[5].ProtectedKey);
        Assert.True(named[7].Row!.Row.IsCopy);
        Assert.Equal(0u, named[7].Row!.Row.CopySourceId);

        // Absence on a complete table is proven; on a Partial table it is only outside the readable scope.
        var complete = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), await RetailTable([Effect(0, 9)]), await NameTable([]));
        Assert.Equal((ItemEffectNameState.NotFoundInAdmittedScope, ItemEffectsAbsence.AbsentFromCompleteSource),
            ((await PageAsync(complete, 1)).Entries[0].Name.State, (await PageAsync(complete, 1)).Entries[0].Name.Absence));
        var scoped = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), await RetailTable([Effect(0, 9)]),
            await NameTable([], TableState.Partial));
        var unknown = (await PageAsync(scoped, 1)).Entries[0].Name;
        Assert.Equal((ItemEffectNameState.NotFoundInAdmittedScope, ItemEffectsAbsence.UnknownOutsideReadableScope), (unknown.State, unknown.Absence));
    }

    // The old RowInvalid for names: a name longer than 65536 units, malformed UTF-16, or a text issue that isn't the encoding one.
    [Theory]
    [InlineData("long")]
    [InlineData("surrogate")]
    [InlineData("issue")]
    public async Task A_name_that_isnt_text_makes_the_name_source_corrupt_and_keeps_the_effects(string defect)
    {
        var row = defect switch
        {
            "long" => Name(0, new string('x', 65537)),
            "surrogate" => Name(0, "\ud800"),
            _ => Name(0, "", issues: [new(1, "DB2_TEXT_OTHER", "ff")]),
        };
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), await RetailTable([Effect(0, 0)]), await NameTable([row]));
        var page = await PageAsync(world, 1);
        Assert.Equal(ItemEffectsSourceState.Corrupt, page.Name.State);
        Assert.Equal("ITEM_EFFECTS_SOURCE_CORRUPT", page.Name.Problem!.Code);
        Assert.Equal(ItemEffectState.ObservedReady, page.Entries[0].Effect.State);
        Assert.Equal(ItemEffectNameState.SourceNotReady, page.Entries[0].Name.State);
        Assert.Null(page.Entries[0].Name.Text);
    }

    // AmbiguityHasCountAndNoFirstRowAndMissingUsesItsOwnCoverage (the missing half; see D461 for ambiguity), plus a keyed row.
    [Fact]
    public async Task A_missing_effect_says_whether_its_absence_is_proven_and_a_keyed_one_names_its_key()
    {
        var complete = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 3, 1)]), await RetailTable([Effect(0)]), await NameTable([]));
        var absent = (await PageAsync(complete, 1)).Entries[0];
        Assert.Equal((ItemEffectState.NotFoundInAdmittedScope, ItemEffectsAbsence.AbsentFromCompleteSource), (absent.Effect.State, absent.Effect.Absence));
        Assert.Null(absent.Effect.Row);
        Assert.Equal(ItemEffectNameState.EffectNotReady, absent.Name.State);

        var partial = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 3, 1), Bridge(2, 4, 1)]),
            await RetailTable([Effect(0)], TableState.Partial, protectedIds: new Dictionary<uint, string> { [4] = Key }), await NameTable([]));
        var page = await PageAsync(partial, 1);
        Assert.Equal((ItemEffectState.NotFoundInAdmittedScope, ItemEffectsAbsence.UnknownOutsideReadableScope), (page.Entries[0].Effect.State, page.Entries[0].Effect.Absence));
        Assert.Equal((ItemEffectState.Protected, Key), (page.Entries[1].Effect.State, page.Entries[1].Effect.ProtectedKey));
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    // NarrowedRetailInputsReadAtTheirDeclaredWidthWithTheirDeclaredReference, PartialUniqueZeroEffectKeepsAllNineOriginalInputsAndRawBoundaries
    [Fact]
    public async Task Each_input_reads_at_its_declared_width_or_as_raw_evidence_and_hints_are_never_looked_up()
    {
        string[] inputs = ["255", "0", "4294967295", "-5", "-6", "65535", "0", "65535", "-1"];
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]),
            await RetailTable([Effect(0, inputs: inputs)], TableState.Partial, RetailColumns(3)), await NameTable([Name(0, "Zero")]));
        var page = await PageAsync(world, 1);
        var fields = page.Entries[0].Effect.Fields!;
        Assert.Equal(ItemEffectState.ObservedReady, page.Entries[0].Effect.State);
        Assert.Equal(inputs, fields.Select(field => field.Value));
        Assert.Equal(Enumerable.Range(1, 9), fields.Select(field => field.ColumnIndex));
        Assert.Equal([ItemEffectEvidence.StrictNumeric, ItemEffectEvidence.StrictNumeric, ItemEffectEvidence.RawPhysicalInteger, ItemEffectEvidence.StrictNumeric,
            ItemEffectEvidence.StrictNumeric, ItemEffectEvidence.StrictNumeric, ItemEffectEvidence.StrictNumeric, ItemEffectEvidence.StrictNumeric,
            ItemEffectEvidence.UnverifiedCandidate], fields.Select(field => field.Evidence));
        Assert.Equal(Raw(3), fields[2].Interpretation);
        Assert.All(fields.Where(field => field.ColumnIndex != 3), field => Assert.Null(field.Interpretation));
        Assert.Equal([null, null, null, null, null, new ForeignKey("SpellCategory", "ID"), null, new ForeignKey("ChrSpecialization", "ID"), new ForeignKey("PlayerCondition", "ID")],
            fields.Select(field => field.CandidateReference));
        // Item, ItemEffect 0 and SpellName 0: no SpellCategory, ChrSpecialization or PlayerCondition lookup.
        Assert.Equal(3, world.Tables.Calls(nameof(IBuildTables.GetAsync)));
        Assert.Equal("Zero", page.Entries[0].Name.Text);
    }

    // The width cases the old tests refused: a narrowed Charges outside int16 or a raw value, a u16 above 65535, a negative u16,
    // a u8 above 255, a non-canonical cell, a text issue on an input. The SpellID still names the spell (the old spell evidence).
    [Theory]
    [InlineData(3, "4294901760")]
    [InlineData(3, "32768")]
    [InlineData(6, "65536")]
    [InlineData(8, "-1")]
    [InlineData(1, "256")]
    [InlineData(2, "-1")]
    [InlineData(4, "2147483648")]
    [InlineData(9, "01")]
    [InlineData(5, "issue")]
    public async Task An_input_outside_its_rule_is_field_unavailable_and_the_spell_is_still_named(int column, string value)
    {
        var inputs = new[] { "1", "2", "3", "4", "5", "6", "0", "8", "9" };
        IReadOnlyList<Db2TextIssue>? issues = null;
        if (value == "issue") issues = [new(column, "DB2_TEXT_ENCODING_INVALID", "ff")];
        else inputs[column - 1] = value;
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), await RetailTable([Effect(0, inputs: inputs, issues: issues)]),
            await NameTable([Name(0, "Zero")]));
        var entry = (await PageAsync(world, 1)).Entries[0];
        Assert.Equal(ItemEffectState.FieldUnavailable, entry.Effect.State);
        Assert.Null(entry.Effect.Fields);
        Assert.Equal(0u, entry.Effect.Row!.Row.Id);
        Assert.Equal((ItemEffectNameState.Ready, "Zero"), (entry.Name.State, entry.Name.Text));
    }

    [Fact]
    public async Task A_spell_id_that_isnt_an_int32_leaves_the_name_unlooked_and_a_negative_one_is_out_of_range()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1), Bridge(2, 1, 1)]),
            await RetailTable([Effect(0, inputs: ["1", "2", "3", "4", "5", "6", "2147483648", "8", "9"]), Effect(1, -1)]), await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal([ItemEffectState.FieldUnavailable, ItemEffectState.ObservedReady], page.Entries.Select(entry => entry.Effect.State));
        Assert.Equal([ItemEffectNameState.EffectNotReady, ItemEffectNameState.SpellKeyOutOfRange], page.Entries.Select(entry => entry.Name.State));
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    // Actual_production_profile_does_not_relax_unmatched_or_malformed_metadata, and D108's raw evidence shape.
    [Theory]
    [InlineData("raw spell")]
    [InlineData("raw charges unsigned")]
    [InlineData("raw 16-bit physical")]
    [InlineData("layout")]
    [InlineData("format")]
    [InlineData("field width")]
    [InlineData("column name")]
    public async Task An_effect_table_that_isnt_the_reviewed_profile_is_unsupported_and_the_links_still_list(string defect)
    {
        var columns = RetailColumns();
        TableSchema? schema = null;
        var layout = RetailLayout;
        switch (defect)
        {
            case "raw spell": columns[7] = columns[7] with { Interpretation = Raw(7) }; break;
            case "raw charges unsigned": columns[3] = columns[3] with { Interpretation = Raw(6) }; break;
            case "raw 16-bit physical": columns[6] = columns[6] with { Interpretation = Raw(6) with { PhysicalBits = 16 } }; break;
            case "layout": layout = "EB717848"; break;
            case "column name": columns[5] = columns[5] with { Name = "Cooldown" }; break;
            case "field width":
                var block = await Schema("4002a5b1", RetailVersion, RetailLayout);
                schema = TableSchema.FromBlock(new DbdBlock([RetailLayout], [], [.. block.Columns.Select((column, i) => new DbdField(
                    new(column.Name, "int", column.VerifiedName, null, column.ForeignKey is { } key ? new(key.Table, key.Column) : null),
                    i == 7 ? 16 : column.Bits, column.Unsigned, 1, column.Id != KeyStorage.None, column.Id == KeyStorage.NonInline, false, null))], null),
                    SchemaMatch.Build, WowLocale.EnUS);
                break;
        }
        var effect = await RetailTable([Effect(0)], columns: columns, schema: schema, layout: layout);
        if (defect == "format") effect = effect with { Counts = effect.Counts! with { Format = "WDC4" } };
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), effect, await NameTable([Name(0, "Zero")]));
        var page = await PageAsync(world, 1);
        Assert.Equal(ItemEffectsPageState.Ok, page.State);
        Assert.Equal(ItemEffectsSourceState.Unsupported, page.Effect.State);
        Assert.Equal(("ITEM_EFFECTS_SOURCE_UNSUPPORTED", false, null), (page.Effect.Problem!.Code, page.Effect.Problem.Retryable, page.Effect.Problem.Definition));
        Assert.Equal(ItemEffectState.SourceNotReady, page.Entries[0].Effect.State);
        Assert.Equal(ItemEffectNameState.EffectNotReady, page.Entries[0].Name.State);
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    // ClassicLinksThroughItemEffectParentItemIdWithOneItemEffectSource
    [Fact]
    public async Task Classic_links_through_item_effect_parent_item_id_and_each_row_is_its_own_effect()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), NotInBuild(BridgeFdid, "ItemXItemEffect"),
            await ClassicTable([Classic(0, 1, 0), Classic(7, 1, -1, ["1", "2", "3", "4", "5", "6", "-1", "65535", "9"]), Classic(8, 2), Classic(9, 1, -1, copy: true, source: 7)]),
            await NameTable([Name(0, "Zero")]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.Ok, ItemEffectsLink.ParentItemId), (page.State, page.Link));
        Assert.Equal(("ItemEffect", "ItemEffect"), (page.Bridge.Table!.Name, page.Effect.Table!.Name));
        Assert.Equal([0u, 7, 9], page.Entries.Select(entry => entry.Link.Row.Id));
        Assert.All(page.Entries, entry => Assert.Equal((entry.Link.Row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "1"), (entry.ItemEffectId, entry.ParentItemId)));
        Assert.All(page.Entries, entry => Assert.Equal(ItemEffectState.ObservedReady, entry.Effect.State));
        var seven = page.Entries[1].Effect.Fields!;
        Assert.Equal(["1", "2", "3", "4", "5", "6", "-1", "65535", "9"], seven.Select(field => field.Value));
        Assert.All(seven.Take(8), field => Assert.Equal(ItemEffectEvidence.StrictNumeric, field.Evidence));
        Assert.All(seven, field => Assert.Null(field.Interpretation));
        Assert.Equal(ItemEffectEvidence.UnverifiedCandidate, seven[8].Evidence);
        Assert.True(page.Entries[2].Effect.Row!.Row.IsCopy);
        Assert.Equal([ItemEffectNameState.Ready, ItemEffectNameState.SpellKeyOutOfRange, ItemEffectNameState.SpellKeyOutOfRange], page.Entries.Select(entry => entry.Name.State));
    }

    // TheBundledItemEffectDefinitionChoosesEachBuildsLink, on the block layer 5 resolves.
    [Theory]
    [InlineData("12.1.5.70448", RetailLayout, "ItemXItemEffect")]
    [InlineData("1.60.1.70450", RetailLayout, "ItemXItemEffect")]
    [InlineData("1.15.9.70003", ClassicLayout, "ParentItemId")]
    [InlineData("5.5.4.70449", ClassicLayout, "ParentItemId")]
    [InlineData("2.5.6.70006", ClassicLayout, "ParentItemId")]
    [InlineData("1.15.10.71000", ClassicLayout, "ParentItemId")]
    [InlineData("1.15.10.71000", RetailLayout, "ItemXItemEffect")]
    [InlineData("1.60.2.71000", RetailLayout, "ItemXItemEffect")]
    public async Task The_item_effect_block_layer_5_resolves_chooses_the_link(string version, string layout, string link)
    {
        Assert.Equal(link, ItemEffectProfiles.Choose(await Schema("4002a5b1", version, layout), null).ToString());
    }

    [Theory]
    [InlineData("12.1.5.70448", "ItemXItemEffect")]
    [InlineData("11.2.7.64978", "ItemXItemEffect")]
    [InlineData("1.15.9.70003", "ParentItemId")]
    [InlineData("5.5.4.70449", "ParentItemId")]
    public async Task A_listed_build_chooses_its_link_before_the_item_effect_file_is_read(string version, string link)
    {
        Assert.Equal(link, ItemEffectProfiles.Choose(await BuildSchema("4002a5b1", version), null).ToString());
    }

    // AnUndecidedBuildsUnknownItemEffectLayoutReadsAsUnsupportedNotAsAMissingBridge
    [Fact]
    public async Task With_no_item_effect_block_and_no_bridge_block_item_effect_is_read_and_says_why()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), NotInBuild(BridgeFdid, "ItemXItemEffect"),
            NoDefinition(EffectFdid, "ItemEffect", DefinitionState.NoMatch), await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.BridgeNotReady, ItemEffectsLink.ParentItemId), (page.State, page.Link));
        Assert.Equal((ItemEffectsSourceState.Unsupported, "ItemEffect", DefinitionState.NoMatch),
            (page.Bridge.State, page.Bridge.Table!.Name, page.Bridge.Problem!.Definition));
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked), (page.Effect.State, page.Name.State));
    }

    // ABridgeTableTheBuildDoesNotShipIsNotInBuildAndNotRetryable, A_link_table_the_ROOT_does_not_list_is_not_in_build...
    [Theory]
    [InlineData(TableState.NotInBuild, ItemEffectsSourceState.NotInBuild, "ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", false)]
    [InlineData(TableState.NotInLocale, ItemEffectsSourceState.NotInLocale, "ITEM_EFFECTS_SOURCE_NOT_IN_LOCALE", false)]
    [InlineData(TableState.NeedsNetwork, ItemEffectsSourceState.Unavailable, "ITEM_EFFECTS_SOURCE_UNAVAILABLE", true)]
    [InlineData(TableState.Missing, ItemEffectsSourceState.Unavailable, "ITEM_EFFECTS_SOURCE_UNAVAILABLE", true)]
    [InlineData(TableState.Encrypted, ItemEffectsSourceState.Protected, "ITEM_EFFECTS_SOURCE_PROTECTED", false)]
    [InlineData(TableState.Rejected, ItemEffectsSourceState.Corrupt, "ITEM_EFFECTS_SOURCE_CORRUPT", false)]
    [InlineData(TableState.TooLarge, ItemEffectsSourceState.ResourceLimit, "ITEM_EFFECTS_LIMIT", false)]
    [InlineData(TableState.Unsupported, ItemEffectsSourceState.Unsupported, "ITEM_EFFECTS_SOURCE_UNSUPPORTED", false)]
    public async Task A_link_table_that_isnt_readable_keeps_its_layer_5_state_and_answers_a_fixed_problem(TableState table, ItemEffectsSourceState state, string code, bool retryable)
    {
        var bridge = await BridgeTable([], table);
        var world = new EffectsWorld().With(ItemTable([Item(1)]), bridge, await RetailTable([Effect(0)]), await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.BridgeNotReady, ItemEffectsLink.ItemXItemEffect), (page.State, page.Link));
        Assert.Equal((state, table), (page.Bridge.State, page.Bridge.Table!.State));
        Assert.Equal((code, retryable, null), (page.Bridge.Problem!.Code, page.Bridge.Problem.Retryable, page.Bridge.Problem.Definition));
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked), (page.Effect.State, page.Name.State));
        Assert.Empty(page.Entries);
    }

    // AnEffectTableTheBuildDoesNotShipLeavesLinksAndNeverConsultsNames
    [Fact]
    public async Task An_effect_table_the_build_doesnt_ship_leaves_the_links_and_never_reads_names()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1), Bridge(2, -1, 1)]),
            NotInBuild(EffectFdid, "ItemEffect", await BuildSchema("4002a5b1", RetailVersion)), await NameTable([Name(0, "Zero")]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.Ok, ItemEffectsLink.ItemXItemEffect), (page.State, page.Link));
        Assert.Equal((ItemEffectsSourceState.NotInBuild, false), (page.Effect.State, page.Effect.Problem!.Retryable));
        Assert.Equal([ItemEffectState.SourceNotReady, ItemEffectState.EffectKeyOutOfRange], page.Entries.Select(entry => entry.Effect.State));
        Assert.All(page.Entries, entry => Assert.Equal(ItemEffectNameState.EffectNotReady, entry.Name.State));
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    public static TheoryData<DefinitionState, string?, ItemEffectsSourceState, bool> Definitions => new()
    {
        { DefinitionState.FetchFailed, "DEFINITION_FETCH_OFFLINE", ItemEffectsSourceState.Unavailable, true },
        { DefinitionState.FetchFailed, "DEFINITION_FETCH_FAILED", ItemEffectsSourceState.Unavailable, true },
        { DefinitionState.FetchFailed, "DEFINITION_FETCH_TOO_LARGE", ItemEffectsSourceState.Unsupported, false },
        { DefinitionState.FetchFailed, "DEFINITION_FETCH_INVALID", ItemEffectsSourceState.Unsupported, false },
        { DefinitionState.FetchFailed, "DEFINITION_FETCH_NOT_FOUND", ItemEffectsSourceState.Unsupported, false },
        { DefinitionState.FetchingOff, null, ItemEffectsSourceState.Unsupported, false },
        { DefinitionState.NoMatch, null, ItemEffectsSourceState.Unsupported, false },
    };

    // A_link_table_no_definition_reads_is_a_source_state_that_names_the_table_and_switch
    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task A_link_table_no_definition_reads_is_a_source_state_with_its_definition_state(DefinitionState definition, string? code,
        ItemEffectsSourceState state, bool retryable)
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), NoDefinition(BridgeFdid, "ItemXItemEffect", definition, code), await RetailTable([Effect(0)]),
            await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.BridgeNotReady, ItemEffectsLink.ItemXItemEffect), (page.State, page.Link));
        Assert.Equal((state, retryable, definition), (page.Bridge.State, page.Bridge.Problem!.Retryable, page.Bridge.Problem.Definition));
        Assert.Equal(code, page.Bridge.Table!.Reason!.Code);
    }

    // An_inputs_table_no_definition_reads_keeps_the_links_and_fails_only_its_own_source
    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task An_inputs_table_no_definition_reads_keeps_the_links_and_fails_only_its_own_source(DefinitionState definition, string? code,
        ItemEffectsSourceState state, bool retryable)
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1)]), NoDefinition(EffectFdid, "ItemEffect", definition, code),
            await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.Ok, ItemEffectsLink.ItemXItemEffect), (page.State, page.Link));
        Assert.Equal(ItemEffectsSourceState.Ready, page.Bridge.State);
        Assert.Equal((state, retryable, definition), (page.Effect.State, page.Effect.Problem!.Retryable, page.Effect.Problem.Definition));
        Assert.Equal(ItemEffectState.SourceNotReady, Assert.Single(page.Entries).Effect.State);
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    // A_names_table_no_definition_reads_leaves_the_ready_effect_source_and_its_evidence
    [Theory]
    [MemberData(nameof(Definitions))]
    public async Task A_names_table_no_definition_reads_leaves_the_ready_effect_source_and_its_evidence(DefinitionState definition, string? code,
        ItemEffectsSourceState state, bool retryable)
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 0, 1), Bridge(2, 1, 1)]), await RetailTable([Effect(0, 0), Effect(1, -1)]),
            NoDefinition(NameFdid, "SpellName", definition, code));
        var page = await PageAsync(world, 1);
        Assert.Equal(ItemEffectsSourceState.Ready, page.Effect.State);
        Assert.Equal((state, retryable, definition), (page.Name.State, page.Name.Problem!.Retryable, page.Name.Problem.Definition));
        Assert.All(page.Entries, entry => Assert.Equal(ItemEffectState.ObservedReady, entry.Effect.State));
        Assert.Equal([ItemEffectNameState.SourceNotReady, ItemEffectNameState.SpellKeyOutOfRange], page.Entries.Select(entry => entry.Name.State));
    }

    // Only_a_definition_failure_for_the_table_the_role_reads_carries_a_definition
    [Fact]
    public async Task Only_a_definition_problem_carries_a_definition_state()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([], TableState.NeedsNetwork), await RetailTable([Effect(0)]),
            NoDefinition(NameFdid, "SpellName", DefinitionState.FetchFailed, "DEFINITION_FETCH_OFFLINE"));
        var page = await PageAsync(world, 1);
        Assert.Equal(new ItemEffectsProblem("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true), page.Bridge.Problem);
        Assert.Equal(ItemEffectsSourceState.NotChecked, page.Name.State);
    }

    // InitialIneligibleEffectPathsStillRequireUnprobedNameSource, SourceIndependentNegativeStatesSurviveStaleTargetButOldNamesNeverDo
    [Fact]
    public async Task A_role_with_no_eligible_key_upstream_is_never_read()
    {
        var negative = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, -1, 1), Bridge(2, -7, 1)]), await RetailTable([Effect(0)]),
            await NameTable([]));
        var page = await PageAsync(negative, 1);
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked), (page.Effect.State, page.Name.State));
        Assert.All(page.Entries, entry => Assert.Equal((ItemEffectState.EffectKeyOutOfRange, ItemEffectNameState.EffectNotReady), (entry.Effect.State, entry.Name.State)));
        Assert.Null(page.Effect.Table);

        var unfound = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 5, 1)]), await RetailTable([Effect(0)], TableState.Partial),
            await NameTable([Name(0, "Zero")]));
        var scoped = await PageAsync(unfound, 1);
        Assert.Equal(ItemEffectsSourceState.Ready, scoped.Effect.State);
        Assert.Equal(ItemEffectsSourceState.NotChecked, scoped.Name.State);
        Assert.Equal(ItemEffectNameState.EffectNotReady, scoped.Entries[0].Name.State);
    }

    // D458 on D471: a link table far larger than one filtered page, with the item's links spread across it, answers in one call
    // from the handle's relation index; the index is built once and serves the next item without another scan.
    [Fact]
    public async Task A_large_link_table_answers_from_the_relation_index_in_one_call_and_is_scanned_once()
    {
        var rows = Enumerable.Range(0, 100_000).Select(i => Bridge((uint)(100 + i), 0, i % 25_000 == 24_999 ? 1 : 2)).ToArray();
        var world = new EffectsWorld(maxScanRows: 1000).With(ItemTable([Item(1), Item(2)]), await BridgeTable(rows), await RetailTable([Effect(0)]),
            await NameTable([Name(0, "Zero")]));
        await using var effects = await world.OpenAsync();
        var page = await effects.GetAsync(1, new(), Now, default);
        Assert.Equal((ItemEffectsPageState.Ok, 4, null), (page.State, page.Matches, page.Next));
        Assert.Equal([25_099u, 50_099, 75_099, 100_099], page.Entries.Select(entry => entry.Link.Row.Id));
        Assert.All(page.Entries, entry => Assert.Equal("Zero", entry.Name.Text));
        var pages = world.Tables.Calls(nameof(IBuildTables.PageAsync));
        var other = await effects.GetAsync(2, new(3), Now, default);
        Assert.Equal((99_996, 3), (other.Matches, other.Entries.Count));
        Assert.NotNull(other.Next);
        // No second scan: only the three read-backs by ordinal.
        Assert.Equal(pages + 3, world.Tables.Calls(nameof(IBuildTables.PageAsync)));
    }

    [Fact]
    public async Task A_relation_index_past_its_budget_is_index_limit_never_an_empty_page()
    {
        var rows = Enumerable.Range(0, 20).Select(i => Bridge((uint)(100 + i), 0, 1)).ToArray();
        var world = new EffectsWorld(new() { MaxRelationIndexEntries = 10 }).With(ItemTable([Item(1)]), await BridgeTable(rows), await RetailTable([Effect(0)]),
            await NameTable([]));
        await using var effects = await world.OpenAsync();
        var page = await effects.GetAsync(1, new(), Now, default);
        Assert.Equal((ItemEffectsPageState.IndexLimit, "RELATION_INDEX_LIMIT"), (page.State, page.Code));
        Assert.Empty(page.Entries);
        Assert.Null(page.Next);
        Assert.Equal(ItemEffectsSourceState.Ready, page.Bridge.State);
        Assert.Equal((ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked), (page.Effect.State, page.Name.State));
    }

    // PageCursorMustBindItsRequestAndAdvancePastExactlyTheReturnedTuple, CursorBindingCannotBeReusedForAnotherPageLimitOrSelection
    [Fact]
    public async Task A_cursor_continues_its_own_item_and_limit_only_and_a_changed_table_is_stale()
    {
        var world = new EffectsWorld().With(await Retail());
        await using var effects = await world.OpenAsync();
        var first = await effects.GetAsync(1, new(2), Now, default);
        Assert.Equal([500u, 501], first.Entries.Select(entry => entry.Link.Row.Id));
        var second = await effects.GetAsync(1, new(2, first.Next), Now, default);
        Assert.Equal([502u, 503], second.Entries.Select(entry => entry.Link.Row.Id));
        Assert.Null(second.Next);
        Assert.Equal(ItemEffectsPageState.InvalidCursor, (await effects.GetAsync(1, new(3, first.Next), Now, default)).State);
        Assert.Equal(ItemEffectsPageState.InvalidCursor, (await effects.GetAsync(2, new(2, first.Next), Now, default)).State);

        var tables = await Retail();
        tables[1] = tables[1] with { ContentKey = new string('e', 32) };
        world.With(tables);
        await using var changed = await world.OpenAsync();
        Assert.Equal(ItemEffectsPageState.Stale, (await changed.GetAsync(1, new(2, first.Next), Now, default)).State);
        // The same handle: its index reads back under the old identity, is rebuilt, and the old cursor is Stale, never re-based.
        Assert.Equal(ItemEffectsPageState.Stale, (await effects.GetAsync(1, new(2, first.Next), Now, default)).State);
        var fresh = await effects.GetAsync(1, new(2), Now, default);
        Assert.Equal((ItemEffectsPageState.Ok, 4), (fresh.State, fresh.Matches));
    }

    // ItemEffectsValidationTests: the page limit is 1 to 16 and a cursor must be framed.
    [Theory]
    [InlineData(0, null)]
    [InlineData(17, null)]
    [InlineData(16, "not a cursor!")]
    [InlineData(16, "")]
    public async Task A_bad_limit_or_a_malformed_cursor_throws(int limit, string? after)
    {
        await using var effects = await new EffectsWorld().With(await Retail()).OpenAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => effects.GetAsync(1, new(limit, after), Now, default));
    }

    [Fact]
    public async Task A_link_row_that_doesnt_name_an_int32_effect_makes_the_link_source_corrupt()
    {
        var world = new EffectsWorld().With(ItemTable([Item(1)]), await BridgeTable([Bridge(1, 2147483648, 1)]), await RetailTable([Effect(0)]), await NameTable([]));
        var page = await PageAsync(world, 1);
        Assert.Equal((ItemEffectsPageState.BridgeNotReady, ItemEffectsSourceState.Corrupt), (page.State, page.Bridge.State));
    }
}
