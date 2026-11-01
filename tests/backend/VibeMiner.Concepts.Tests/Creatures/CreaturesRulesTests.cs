using VibeMiner.Concepts.Creatures;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;
using static VibeMiner.Concepts.Tests.Creatures.CreaturesWorld;

namespace VibeMiner.Concepts.Tests.Creatures;

/// <summary>
/// The Creature rules (D088, D089, D101, D107, D462-D465), ported from the old CreaturesProjectionTests,
/// CreatureDisplayPolicyTests, CreatureDisplayCoordinatorTests and CreatureGeometryAppearanceTests to the real
/// <see cref="CreaturesService"/> over the layer 5 and 4 fakes. Synthetic data only.
/// </summary>
public sealed class CreaturesRulesTests
{
    private const TablePriority Now = TablePriority.Interactive;

    private static Task<CreaturesPage> List(IBuildCreatures creatures, CreatureQuery query) => creatures.ListAsync(query, Now, default);

    // Was DeclaredSlotsNumericFactsNamesCopiesAndCoverageRemainExact (the counts are gone; coverage is the table's Partial state).
    [Fact]
    public async Task Slots_facts_names_and_copies_are_read_exactly_and_a_hidden_id_is_never_absent()
    {
        var world = new CreaturesWorld().With(CreatureTable([
            Creature(0, "  ", [123, 123, 0, -1], ["-1", "0", "0.5", "1"]),
            Creature(9, "", copy: true, source: 0),
            Creature(4294967295, "", issues: [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")])], TableState.Partial, protectedIds: new Dictionary<uint, string> { [8] = Key }));
        await using var creatures = await world.OpenAsync();
        var page = await List(creatures, new());
        Assert.Equal([0u, 9, 4294967295], page.Entries.Select(entry => entry.Id));
        Assert.Equal("  ", page.Entries[0].Name.Value);
        Assert.Equal(CreatureNameState.Empty, page.Entries[1].Name.State);
        Assert.Equal((true, 0u), (page.Entries[1].IsCopy, page.Entries[1].CopySourceId!.Value));
        Assert.Equal(new CreatureName(CreatureNameState.Invalid, null, "DB2_TEXT_ENCODING_INVALID"), page.Entries[2].Name);
        Assert.Equal(["-128", "255", "65535", "127"], page.Entries[0].Facts.Select(value => value.Value));
        Assert.Equal(["Classification", "CreatureType", "CreatureFamily", "StartAnimState"], page.Entries[0].Facts.Select(value => value.Column));
        Assert.Equal([123, 123, 0, -1], page.Entries[0].Slots.Select(slot => slot.DisplayId));
        Assert.Equal([-1f, 0f, 0.5f, 1f], page.Entries[0].Slots.Select(slot => slot.Probability));
        Assert.Equal([CreatureSlotState.Declared, CreatureSlotState.Declared, CreatureSlotState.NoDeclaredDisplay, CreatureSlotState.InvalidDisplayReference],
            page.Entries[0].Slots.Select(slot => slot.State));
        Assert.Equal(TableState.Partial, page.Creature!.State);
        Assert.Equal((CreatureResultState.Protected, Key), ((await creatures.GetAsync(8, Now, default)) is var hidden ? hidden.State : default, hidden.ProtectedKey));
        Assert.Equal(CreatureResultState.Unknown, (await creatures.GetAsync(7, Now, default)).State);
        var exact = await List(creatures, new(ExactId: 8));
        Assert.Equal((0, TableRowOutcome.Protected, Key), (exact.Entries.Count, exact.Exact, exact.ProtectedKey));
    }

    // Was SearchUsesOnlyPrimaryLiteralOrdinalIgnoreCaseAndExactIdRemainsDistinct.
    [Fact]
    public async Task Search_uses_only_the_primary_name_as_a_literal_ordinal_ignore_case_match_and_an_exact_id_stays_distinct()
    {
        var world = new CreaturesWorld().With(CreatureTable([Creature(0, "123"), Creature(2, "Straße %_"), Creature(3, "STRASSE %_"), Creature(4, "café"),
            Creature(5, "CAFE"), Creature(9, "  ")]));
        await using var creatures = await world.OpenAsync();
        async Task<uint[]> Search(string query) => [.. (await List(creatures, new(query))).Entries.Select(entry => entry.Id)];
        Assert.Equal(new uint[] { 0 }, await Search("123")); Assert.Equal(new uint[] { 2, 3 }, await Search("%_")); Assert.Equal(new uint[] { 2 }, await Search("straße"));
        Assert.Equal(new uint[] { 4 }, await Search("CAFÉ")); Assert.Equal(new uint[] { 5 }, await Search("cafe"));
        // NameAlt_lang and Title_lang are never searched.
        Assert.Empty(await Search("Alt")); Assert.Empty(await Search("Title"));
        Assert.Equal(0u, Assert.Single((await List(creatures, new(ExactId: 0))).Entries).Id);
        var first = await List(creatures, new(Limit: 2));
        Assert.Equal([0u, 2], first.Entries.Select(entry => entry.Id)); Assert.NotNull(first.Next);
        Assert.Equal([3u, 4], (await List(creatures, new(Limit: 2, After: first.Next))).Entries.Select(entry => entry.Id));
        foreach (var changed in new CreatureQuery[] { new(Limit: 3, After: first.Next), new("x", After: first.Next) })
            Assert.Equal(CreaturesPageState.InvalidCursor, (await List(creatures, changed)).State);
    }

    [Fact]
    public async Task A_changed_creature_table_makes_the_cursor_stale_and_a_scan_is_bounded_per_call()
    {
        var world = new CreaturesWorld(new CreaturesOptions { MaxScanRows = 3 }).With(CreatureTable([.. Enumerable.Range(1, 7).Select(i => Creature((uint)i, i == 6 ? "match" : "other"))]));
        await using var creatures = await world.OpenAsync();
        var first = await List(creatures, new("match"));
        Assert.Equal((0, 3L), (first.Entries.Count, first.Scanned)); Assert.NotNull(first.Next);
        var second = await List(creatures, new("match", After: first.Next));
        Assert.Equal([6u], second.Entries.Select(entry => entry.Id));
        world.With(CreatureTable([Creature(1, "match")]) with { ContentKey = new string('e', 32) });
        Assert.Equal(CreaturesPageState.Stale, (await List(creatures, new("match", After: first.Next))).State);
    }

    // Was HonestUnsupportedProfilesRemainUnsupported (raw, missing, width, unsigned), plus the old array and verified-name checks.
    [Theory]
    [InlineData("raw")]
    [InlineData("missing")]
    [InlineData("width")]
    [InlineData("unsigned")]
    [InlineData("array")]
    [InlineData("unverified")]
    public async Task A_field_the_definition_or_header_doesnt_qualify_is_unsupported(string condition)
    {
        var fields = CreatureFields().Select(field => field.Column.Name switch
        {
            "Name_lang" when condition == "missing" => field with { Column = field.Column with { Name = "UnknownName" } },
            "Classification" when condition == "width" => field with { IntegerBits = 16 },
            "Classification" when condition == "unsigned" => field with { Unsigned = true },
            "DisplayProbability" when condition == "array" => field with { ArrayLength = 3 },
            "CreatureType" when condition == "unverified" => field with { Column = field.Column with { VerifiedName = false } },
            _ => field,
        }).ToArray();
        var columns = Columns(fields);
        if (condition == "raw") columns[1] = columns[1] with { Interpretation = new("authored", 32, false, 32, false, "authored") };
        var rows = condition == "array" ? [] : new[] { Creature(1, "Name") };
        var world = new CreaturesWorld().With(CreatureTable(rows, fields: fields, columns: columns));
        await using var creatures = await world.OpenAsync();
        var page = await List(creatures, new());
        Assert.Equal((CreaturesPageState.SchemaUnsupported, "CREATURES_SCHEMA_UNSUPPORTED"), (page.State, page.Code));
        Assert.Equal(CreatureResultState.SchemaUnsupported, (await creatures.GetAsync(1, Now, default)).State);
    }

    [Theory]
    [InlineData(0, "128")]
    [InlineData(1, "-1")]
    [InlineData(2, "065")]
    [InlineData(4, "2147483648")]
    [InlineData(8, "NaN")]
    [InlineData(8, "0.50")]
    public async Task A_cell_outside_its_field_is_source_invalid(int field, string value)
    {
        var facts = new[] { "-128", "255", "65535", "127" };
        var displays = new[] { 0, 0, 0, 0 };
        var probabilities = new[] { "1", "0", "0", "0" };
        var row = Creature(1, "Name", displays, probabilities, facts);
        var values = row.Values.ToArray();
        values[field < 4 ? 5 + field : field == 4 ? 9 : 13] = value;
        var world = new CreaturesWorld().With(CreatureTable([row with { Values = values }]));
        await using var creatures = await world.OpenAsync();
        Assert.Equal((CreaturesPageState.SourceInvalid, "CREATURES_PROJECTION_INVALID"), ((await List(creatures, new())) is var page ? page.State : default, page.Code));
        Assert.Equal(CreatureResultState.SourceInvalid, (await creatures.GetAsync(1, Now, default)).State);
    }

    [Theory]
    [InlineData("-128", null)]
    [InlineData("0", "Normal")]
    [InlineData("3", "World boss")]
    [InlineData("6", "Minus")]
    [InlineData("7", null)]
    public async Task Classification_0_to_6_has_its_reviewed_label(string classification, string? label)
    {
        var world = new CreaturesWorld().With(CreatureTable([Creature(1, "Name", facts: [classification, "1", "1", "0"])]), DisplayTable([]), ModelTable([]));
        await using var creatures = await world.OpenAsync();
        Assert.Equal(label, (await creatures.GetAsync(1, Now, default)).Detail!.ClassificationLabel);
    }

    private static async Task<CreatureDisplayChain> ChainAsync(CreaturesWorld world, int slot = 0)
    {
        await using var creatures = await world.OpenAsync();
        var result = await creatures.GetAsync(1, Now, default);
        Assert.Equal(CreatureResultState.Found, result.State);
        return result.Detail!.Displays[slot];
    }

    private static FakeTable One(int display = 100) => CreatureTable([Creature(1, "Name", [display, 0, 0, 0])]);

    // Was MissingTargetUsesItsOwnCoverageAndNeverStartsModel: Absent, Unknown and Protected are the display table's own outcomes.
    [Fact]
    public async Task A_display_not_found_is_absent_from_a_complete_table_unknown_in_a_partial_one_and_protected_with_its_key()
    {
        Assert.Equal(CreatureDisplayChainState.DisplayAbsent, (await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([]), ModelTable([])))).State);
        var partial = DisplayTable([], TableState.Partial, protectedIds: new Dictionary<uint, string> { [100] = Key });
        var hidden = await ChainAsync(new CreaturesWorld().With(One(), partial, ModelTable([])));
        Assert.Equal((CreatureDisplayChainState.DisplayProtected, Key), (hidden.State, hidden.ProtectedKey));
        var unknown = await ChainAsync(new CreaturesWorld().With(One(101), partial, ModelTable([])));
        Assert.Equal(CreatureDisplayChainState.DisplayUnknown, unknown.State);
        Assert.Null(unknown.ModelId); Assert.Null(unknown.ModelData);
        var notInBuild = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([], TableState.NotInBuild), ModelTable([])));
        Assert.Equal((CreatureDisplayChainState.DisplayNotReadable, TableState.NotInBuild), (notInBuild.State, notInBuild.Display!.State));
        var model = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([Display(100, "33")]),
            ModelTable([], TableState.Partial, protectedIds: new Dictionary<uint, string> { [33] = Key })));
        Assert.Equal((CreatureDisplayChainState.ModelProtected, Key, 33u), (model.State, model.ProtectedKey, model.ModelId!.Value));
    }

    // Was UnsupportedSourceIsDeliveredAndFencedWithoutInventingEdge.
    [Fact]
    public async Task An_unsupported_display_layout_is_delivered_without_inventing_an_edge()
    {
        var chain = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([Display(100, "33")], layout: "12345678"), ModelTable([Model(33, "7001")])));
        Assert.Equal((CreatureDisplayChainState.DisplayUnsupported, "CREATURE_DISPLAY_SCHEMA_UNSUPPORTED"), (chain.State, chain.Code));
        Assert.NotNull(chain.Display); Assert.Null(chain.DisplayRow); Assert.Null(chain.ModelId); Assert.Null(chain.ModelData);
    }

    // Was ModelZeroIsEvidenceAndNeverClaimsAFileOrAppearance.
    [Fact]
    public async Task A_zero_model_file_is_evidence_and_never_claims_a_file_or_appearance()
    {
        var chain = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([Display(100, "33")]), ModelTable([Model(33, "0")])));
        Assert.Equal(CreatureDisplayChainState.NoDeclaredModelFile, chain.State);
        Assert.Equal("0", chain.ModelRow!.Row.Values[1]);
        Assert.Equal(0u, chain.ModelFileDataId); Assert.Null(chain.Model);
        var noModel = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([Display(100, "0")]), ModelTable([])));
        Assert.Equal((CreatureDisplayChainState.NoDeclaredModel, 0u), (noModel.State, noModel.ModelId!.Value));
        Assert.Null(noModel.ModelData);
    }

    [Theory]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("01")]
    public async Task A_model_id_that_isnt_a_canonical_u16_is_display_invalid(string model)
    {
        var chain = await ChainAsync(new CreaturesWorld().With(One(), DisplayTable([Display(100, model)]), ModelTable([])));
        Assert.Equal((CreatureDisplayChainState.DisplayInvalid, "CREATURE_DISPLAY_INTEGRITY"), (chain.State, chain.Code));
        Assert.Null(chain.ModelId);
    }

    // Was CanonicalEdgeValuesRespectSemanticWidthAndExactEmittedTarget.
    [Theory]
    [InlineData(true, "65535", true)]
    [InlineData(true, "65536", false)]
    [InlineData(false, "4294967295", true)]
    [InlineData(false, "4294967296", false)]
    [InlineData(false, "-1", false)]
    [InlineData(false, "01", false)]
    [InlineData(false, "+1", false)]
    public void Edge_values_respect_the_semantic_width_and_the_exact_row(bool display, string value, bool valid)
    {
        var role = display ? DisplayRole.Display : DisplayRole.Model;
        var row = new Db2Row(123, true, 99, ["123", value]);
        Assert.Equal(valid ? uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null, CreatureDisplayRules.Edge(row, 1, role, 123));
        Assert.Null(CreatureDisplayRules.Edge(row, 1, role, 99));
        Assert.Null(CreatureDisplayRules.Edge(row with { CopySourceId = null }, 1, role, 123));
        Assert.Null(CreatureDisplayRules.Edge(row with { TextIssues = [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")] }, 1, role, 123));
    }

    // Was ExactBundledActualProfilesQualifyOnlyTheirSelectedUnsignedScalar and SemanticEdgeNeverWidensOrAcceptsAnUnqualifiedCandidate.
    [Theory]
    [InlineData("none")]
    [InlineData("signed")]
    [InlineData("width")]
    [InlineData("array")]
    [InlineData("unverified")]
    [InlineData("relation")]
    public void The_edge_is_only_one_verified_unsigned_scalar_of_its_width(string mutation)
    {
        var fields = DisplayFields().Select(field => field.Column.Name != "ModelID" ? field : mutation switch
        {
            "signed" => field with { Unsigned = false },
            "width" => field with { IntegerBits = 32 },
            "array" => field with { ArrayLength = 2 },
            "unverified" => field with { Column = field.Column with { VerifiedName = false } },
            "relation" => field with { RelationStorage = true },
            _ => field,
        }).ToArray();
        var column = CreatureDisplayRules.EdgeColumn(DisplayRole.Display, "7275F5F6", Columns(fields), Schema("7275F5F6", fields));
        if (mutation == "none") Assert.Equal(1, column);
        else Assert.Null(column);
        Assert.Equal(1, CreatureDisplayRules.EdgeColumn(DisplayRole.Model, "0F5449F8", Columns(ModelFields()), Schema("0F5449F8", ModelFields())));
    }

    // Was RawPhysicalColumnCannotSupplyModelAuthority.
    [Fact]
    public void A_raw_physical_column_cant_supply_the_model_id()
    {
        var columns = Columns(DisplayFields());
        columns[1] = columns[1] with { Interpretation = new("raw", 32, true, 16, true, "unqualified") };
        Assert.Null(CreatureDisplayRules.EdgeColumn(DisplayRole.Display, "7275F5F6", columns, Schema("7275F5F6", DisplayFields())));
    }

    // Was Classic_layouts_qualify_the_same_named_edge_and_other_layouts_stay_unsupported (D089).
    [Theory]
    [InlineData(true, "F75F884C", "3FFEE408")]
    [InlineData(false, "3FFEE408", "F75F884C")]
    public void Classic_layouts_qualify_the_same_named_edge_and_other_layouts_stay_unsupported(bool display, string layout, string other)
    {
        var role = display ? DisplayRole.Display : DisplayRole.Model;
        var fields = role == DisplayRole.Display ? DisplayFields() : ModelFields();
        Assert.Equal(1, CreatureDisplayRules.EdgeColumn(role, layout.ToLowerInvariant(), Columns(fields), Schema(layout, fields)));
        Assert.Null(CreatureDisplayRules.EdgeColumn(role, other, Columns(fields), Schema(other, fields)));
    }

    // Was Texture_evidence_reads_all_four_variations_and_refuses_unreadable_fields.
    [Fact]
    public void Texture_evidence_reads_all_four_variations_and_refuses_unreadable_fields()
    {
        var columns = Columns(DisplayFields());
        var values = new[] { "100", "33", "2", "1.5", "7", "0", "0", "-3", "0" };
        var evidence = CreatureDisplayRules.TextureEvidence(columns, new(100, false, null, values))!;
        Assert.Equal(7, evidence.ExtendedDisplayInfoId); Assert.Equal([0, 0, -3, 0], evidence.TextureVariations);
        values[6] = "007";
        Assert.Null(CreatureDisplayRules.TextureEvidence(columns, new(100, false, null, values)));
        values[6] = "0";
        Assert.Null(CreatureDisplayRules.TextureEvidence(columns, new(100, false, null, values, [new(4, "TEXT", "00")])));
        Assert.Null(CreatureDisplayRules.TextureEvidence(columns[..8], new(100, false, null, values[..8])));
    }

    // Was LocalFactsCarryOnlyUniquelyMatchedRowsAndNeverPrepare (D107).
    [Fact]
    public async Task Facts_pick_reviewed_fields_by_base_name_and_a_field_the_layout_lacks_is_unsupported_never_zero()
    {
        // This authored CreatureModelData layout has no ModelScale.
        DbdField[] model = [F("ID", "int", 32, false, id: true, nonInline: true), F("FileDataID", "int", 32, true), F("CollisionHeight", "float")];
        var world = new CreaturesWorld().With(One(), DisplayTable([Display(100, "33", variations: [800, 0, 0, 0])]),
            ModelTable([new(33, false, null, ["33", "777", "2.25"])], fields: model));
        var chain = await ChainAsync(world);
        var display = chain.DisplayFacts!;
        Assert.Equal(CreatureFactsState.Ready, display.State);
        Assert.Equal(["model", "scale", "textureVariations"], display.Facts.Select(fact => fact.Key));
        Assert.Equal(["33"], display.Facts[0].Cells.Select(cell => cell.Value));
        Assert.Equal(3, display.Facts[1].Cells.Single().ColumnIndex);
        Assert.Equal(["TextureVariationFileDataID[0]:800", "TextureVariationFileDataID[1]:0", "TextureVariationFileDataID[2]:0", "TextureVariationFileDataID[3]:0"],
            display.Facts[2].Cells.Select(cell => $"{cell.Column}:{cell.Value}"));
        Assert.Equal(["SizeClass", "ExtendedDisplayInfoID"], display.Raw.Select(cell => cell.Column));
        var facts = chain.ModelFacts!;
        Assert.Equal(["file:Value", "scale:Unsupported"], facts.Facts.Select(fact => $"{fact.Key}:{fact.State}"));
        Assert.Empty(facts.Facts[1].Cells);
        Assert.Equal(["CollisionHeight:2.25"], facts.Raw.Select(cell => $"{cell.Column}:{cell.Value}"));
    }

    // Was A_row_whose_values_were_not_kept_whole_has_no_facts_record.
    [Fact]
    public void A_row_whose_values_werent_kept_whole_has_no_facts()
    {
        Db2Column[] columns = [new("ID", "integer", true), new("ModelID", "integer", false)];
        Assert.Equal(CreatureFactsState.RowNotWhole, CreatureDisplayRules.Facts(DisplayRole.Display, columns, new(100, false, null, ["100"])).State);
        Assert.Equal(CreatureFactsState.RowNotWhole, CreatureDisplayRules.Facts(DisplayRole.Display, columns,
            new(100, false, null, ["100", "0"], [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")])).State);
        Assert.Equal(CreatureFactsState.Ready, CreatureDisplayRules.Facts(DisplayRole.Display, columns, new(100, false, null, ["100", "0"])).State);
    }

    // Was A_fact_wider_than_sixteen_cells_is_refused_whole.
    [Fact]
    public void A_fact_wider_than_sixteen_cells_is_refused_whole()
    {
        Db2Column[] columns = [new("ID", "integer", true), new("ModelID", "integer", false), .. Enumerable.Range(0, 17).Select(i => new Db2Column($"TextureVariationFileDataID[{i}]", "integer", false))];
        var values = new[] { "100", "33" }.Concat(Enumerable.Repeat("0", 17)).ToArray();
        var refused = CreatureDisplayRules.Facts(DisplayRole.Display, columns, new(100, false, null, values));
        Assert.Equal((CreatureFactsState.LimitExceeded, "CREATURES_RESULT_LIMIT"), (refused.State, refused.Code));
        Assert.Empty(refused.Facts);
        Assert.Equal(16, CreatureDisplayRules.Facts(DisplayRole.Display, columns[..18], new(100, false, null, values[..18])).Facts[2].Cells.Count);
    }

    // Model 7001 with skin 900 stored. By default both textures are drawn: texture 0 (type 0, TXID 700) and texture 1 (type 11).
    private static CreaturesWorld Textured(int extended = 0, int[]? variations = null, byte[]? model = null, byte[]? skin = null, params FakeFile[] files) =>
        new CreaturesWorld().With(One(), DisplayTable([Display(100, "33", extended, variations)]), ModelTable([Model(33, "7001")]))
            .WithFiles([Stored(7001, model ?? M2(twoCombos: true)), .. skin is { Length: 0 } ? Array.Empty<FakeFile>() : [Stored(900, skin ?? BothDrawn)], .. files]);

    [Fact]
    public async Task Type_0_reads_its_txid_types_11_to_13_read_the_display_variations_and_other_types_resolve_elsewhere()
    {
        var chain = await ChainAsync(Textured(variations: [800, 0, 0, 0], files: [Stored(700, Blp()), Stored(800, Blp())]));
        var model = chain.Model!;
        Assert.Equal((CreatureModelState.Read, ModelReadState.Read, 900u, ModelReadState.Read),
            (model.State, model.ReadState, model.MainSkinFileDataId!.Value, model.SkinState!.Value));
        Assert.Equal(CreatureAppearanceState.Ready, model.Appearance.State);
        Assert.Equal([(CreatureTextureSource.Txid, (uint?)700, CreatureTextureState.Resolved, true), (CreatureTextureSource.Variation, 800, CreatureTextureState.Resolved, true)],
            model.Textures.Select(texture => (texture.Source, texture.FileDataId, texture.State, texture.Drawn)));
        Assert.Equal([BlpImageState.Ready, BlpImageState.Ready], model.Textures.Select(texture => texture.Image!.Value));
        Assert.Equal(0, model.Textures[1].Variation);
        Assert.Equal(ModelReferenceState.Found, model.Textures[0].Reference!.State);

        // Type 12 is variation 1, never 0; position 3 is kept as data only.
        var twelve = (await ChainAsync(Textured(variations: [800, 801, 0, 803], model: M2(firstType: 12, twoCombos: true), files: [Stored(800, Blp()), Stored(801, Blp())]))).Model!;
        Assert.Equal([(1, (uint?)801), (0, 800)], twelve.Textures.Select(texture => (texture.Variation!.Value, texture.FileDataId)));
        Assert.Equal(CreatureAppearanceState.Ready, twelve.Appearance.State);
        Assert.Equal(803, (await ChainAsync(Textured(variations: [800, 801, 0, 803], model: M2(firstType: 12, twoCombos: true)))).Textures!.TextureVariations[3]);

        // An undrawn texture of another type is data only; a drawn one has no creature binding.
        var other = (await ChainAsync(Textured(model: M2(firstType: 2, twoCombos: true), skin: Skin((1, 1, 0)), files: [Stored(800, Blp())]))).Model!;
        Assert.Equal((CreatureTextureSource.Other, CreatureTextureState.ResolvedElsewhere, false), (other.Textures[0].Source, other.Textures[0].State, other.Textures[0].Drawn));
        Assert.Null(other.Textures[0].Image);
        Assert.Equal(CreatureAppearanceState.Ready, other.Appearance.State);
        var drawnOther = (await ChainAsync(Textured(model: M2(firstType: 2, twoCombos: true), files: [Stored(800, Blp())]))).Model!;
        Assert.Equal((CreatureAppearanceState.MaterialUnsupported, "CREATURE_APPEARANCE_MATERIAL_UNSUPPORTED", 0, 0),
            (drawnOther.Appearance.State, drawnOther.Appearance.Code, drawnOther.Appearance.TextureOrdinal!.Value, drawnOther.Appearance.BatchOrdinal!.Value));
    }

    // D088 counts only the main skin's drawn batches: an unset type-11 texture no batch draws never makes the display unresolved.
    [Fact]
    public async Task Only_textures_the_main_skins_drawn_batches_use_count()
    {
        var undrawn = (await ChainAsync(Textured(variations: [0, 0, 0, 0], model: M2(), skin: FirstOnly, files: [Stored(700, Blp())]))).Model!;
        Assert.Equal(CreatureAppearanceState.Ready, undrawn.Appearance.State);
        Assert.Equal([(true, CreatureTextureState.Resolved), (false, CreatureTextureState.Unresolved)], undrawn.Textures.Select(texture => (texture.Drawn, texture.State)));
        Assert.Null(undrawn.Textures[1].Image);

        // A blend mode 2-7 batch is counted, not drawn (D094): its textures don't count either.
        var blended = (await ChainAsync(Textured(variations: [0, 0, 0, 0], model: M2(twoCombos: true, blends: [0, 2]), skin: Skin((0, 1, 0), (1, 1, 1)),
            files: [Stored(700, Blp())]))).Model!;
        Assert.Equal((CreatureAppearanceState.Ready, (int?)1), (blended.Appearance.State, blended.Appearance.BlendedBatches));
        Assert.Equal([true, false], blended.Textures.Select(texture => texture.Drawn));
        Assert.Equal((int?)0, undrawn.Appearance.BlendedBatches);

        // Layer 5's skin reader already refuses a batch lookup past the model's combos: the skin isn't read.
        var badLookup = (await ChainAsync(Textured(model: M2(), skin: Skin((0, 2, 0))))).Model!;
        Assert.Equal((CreatureAppearanceState.SkinNotRead, ModelReadState.Rejected), (badLookup.Appearance.State, badLookup.SkinState!.Value));
        var badBlend = (await ChainAsync(Textured(model: M2(twoCombos: true, blends: [8])))).Model!;
        Assert.Equal((CreatureAppearanceState.BlendUnsupported, "CREATURE_APPEARANCE_BLEND_UNSUPPORTED", (int?)0),
            (badBlend.Appearance.State, badBlend.Appearance.Code, badBlend.Appearance.BatchOrdinal));
        Assert.Contains("mode 8", badBlend.Appearance.Message);
    }

    // Was AdmitAsync's end check (GeometryAppearanceSources.cs:63): a skin with nothing opaque on triangles has nothing to draw.
    [Fact]
    public async Task A_main_skin_with_no_opaque_batch_on_triangles_is_refused()
    {
        var allBlended = (await ChainAsync(Textured(model: M2(twoCombos: true, blends: [4]), skin: Skin((0, 1, 0), (1, 1, 0))))).Model!;
        Assert.Equal((CreatureAppearanceState.BlendUnsupported, "CREATURE_APPEARANCE_BLEND_UNSUPPORTED", 0, 2),
            (allBlended.Appearance.State, allBlended.Appearance.Code, allBlended.Appearance.BatchOrdinal!.Value, allBlended.Appearance.BlendedBatches!.Value));
        Assert.Contains("mode 4", allBlended.Appearance.Message);
        Assert.All(allBlended.Textures, texture => Assert.Null(texture.Image));
        // No drawn triangles, and no batches at all.
        foreach (var skin in new[] { Skin(0, (0, 2, 0)), Skin(3, Array.Empty<(ushort, ushort, ushort)>()) })
        {
            var empty = (await ChainAsync(Textured(skin: skin))).Model!;
            Assert.Equal((CreatureAppearanceState.MaterialUnsupported, "No batch draws any declared surface triangle."), (empty.Appearance.State, empty.Appearance.Message));
        }
        // Blended layers only on a section with no triangles: still nothing to draw, and not a blend refusal.
        var hollow = (await ChainAsync(Textured(model: M2(twoCombos: true, blends: [4]), skin: Skin(0, (0, 1, 0))))).Model!;
        Assert.Equal(CreatureAppearanceState.MaterialUnsupported, hollow.Appearance.State);
    }

    // Was M2Appearance.AdmitAsync's texture flags rule.
    [Fact]
    public async Task A_drawn_texture_with_flags_outside_the_wrap_bits_is_refused_and_an_undrawn_one_isnt()
    {
        var flagged = (await ChainAsync(Textured(model: M2(twoCombos: true, firstFlags: 7)))).Model!;
        Assert.Equal((CreatureAppearanceState.MaterialUnsupported, 0, 0), (flagged.Appearance.State, flagged.Appearance.TextureOrdinal!.Value, flagged.Appearance.BatchOrdinal!.Value));
        Assert.Contains("U/V wrap", flagged.Appearance.Message);
        var undrawn = (await ChainAsync(Textured(model: M2(twoCombos: true, firstFlags: 7), skin: Skin((1, 1, 0)), files: [Stored(800, Blp())]))).Model!;
        Assert.Equal(CreatureAppearanceState.Ready, undrawn.Appearance.State);
    }

    // Was GeometrySources.cs:55: the geometry profile is checked before the main skin.
    [Theory]
    [InlineData("legacy")]
    [InlineData("skid")]
    [InlineData("bfid")]
    public async Task A_model_outside_the_geometry_profile_is_refused_before_the_main_skin(string kind)
    {
        var model = kind switch { "legacy" => M2(version: 264), "skid" => M2(external: "SKID", twoCombos: true), _ => M2(external: "BFID", twoCombos: true) };
        var read = (await ChainAsync(Textured(extended: 5, model: model))).Model!;
        Assert.Equal((CreatureModelState.Read, CreatureAppearanceState.GeometryUnsupported, "MODEL_UNSUPPORTED", (ModelReadState?)null),
            (read.State, read.Appearance.State, read.Appearance.Code, read.SkinState));
        Assert.Contains(kind switch { "skid" => "SKID 600", "bfid" => "BFID", _ => "unchunked version 264" }, read.Appearance.Message);
    }

    [Fact]
    public async Task A_main_skin_that_cant_be_read_is_its_own_state()
    {
        var missing = (await ChainAsync(Textured(skin: []))).Model!;
        Assert.Equal((CreatureModelState.Read, CreatureAppearanceState.SkinNotRead, ModelReadState.UnknownFile),
            (missing.State, missing.Appearance.State, missing.SkinState!.Value));
        Assert.All(missing.Textures, texture => Assert.False(texture.Drawn));
        var skin = BothDrawn;
        var hidden = (await ChainAsync(Textured(skin: [], files: [Stored(900, skin, [new FileHole(0, skin.Length, Key)])]))).Model!;
        Assert.Equal((CreatureAppearanceState.SkinNotRead, ModelReadState.Protected, Key), (hidden.Appearance.State, hidden.SkinState!.Value, Assert.Single(hidden.SkinKeyNames!)));
    }

    // Was Display_refusals_stop_before_any_texture_demand_and_are_retained (character, zero, negative).
    [Theory]
    [InlineData("character")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("txid")]
    public async Task Display_refusals_stop_before_any_texture_is_read(string kind)
    {
        var world = kind switch
        {
            "character" => Textured(extended: 5),
            "zero" => Textured(variations: [0, 0, 0, 0]),
            "negative" => Textured(variations: [-3, 0, 0, 0]),
            _ => Textured(model: M2(txid: [0, 0], twoCombos: true)),
        };
        var model = (await ChainAsync(world)).Model!;
        Assert.Equal(CreatureModelState.Read, model.State);
        Assert.All(model.Textures, texture => Assert.Null(texture.Image));
        if (kind == "character")
        {
            Assert.Equal((CreatureAppearanceState.CharacterModel, "CREATURE_APPEARANCE_CHARACTER_MODEL"), (model.Appearance.State, model.Appearance.Code));
            Assert.Contains("5", model.Appearance.Message);
            return;
        }
        var ordinal = kind == "txid" ? 0 : 1;
        Assert.Equal((CreatureAppearanceState.TextureUnresolved, "CREATURE_APPEARANCE_TEXTURE_UNRESOLVED", ordinal),
            (model.Appearance.State, model.Appearance.Code, model.Appearance.TextureOrdinal!.Value));
        Assert.Equal((CreatureTextureState.Unresolved, (uint?)null), (model.Textures[ordinal].State, model.Textures[ordinal].FileDataId));
    }

    // GeometrySources refused a model with no positive main SKIN before any appearance rule: it wins over a character model and unreadable evidence.
    [Fact]
    public async Task No_positive_main_skin_is_checked_before_any_appearance_rule()
    {
        var noSkin = (await ChainAsync(Textured(extended: 5, model: M2(skin: 0, twoCombos: true)))).Model!;
        Assert.Equal((CreatureAppearanceState.NoMainSkin, "MODEL_UNSUPPORTED", (uint?)null, (ModelReadState?)null),
            (noSkin.Appearance.State, noSkin.Appearance.Code, noSkin.MainSkinFileDataId, noSkin.SkinState));
        var unreadable = new CreaturesWorld().With(One(), DisplayTable([Display(100, "33") with { TextIssues = [new(5, "DB2_TEXT_ENCODING_INVALID", "ff")] }]),
            ModelTable([Model(33, "7001")])).WithFiles(Stored(7001, M2(skin: 0)));
        Assert.Equal(CreatureAppearanceState.NoMainSkin, (await ChainAsync(unreadable)).Model!.Appearance.State);
    }

    [Fact]
    public async Task Unreadable_texture_evidence_is_unresolved()
    {
        var world = new CreaturesWorld().With(One(), DisplayTable([Display(100, "33") with { TextIssues = [new(5, "DB2_TEXT_ENCODING_INVALID", "ff")] }]),
            ModelTable([Model(33, "7001")])).WithFiles(Stored(7001, M2()), Stored(900, FirstOnly));
        var chain = await ChainAsync(world);
        Assert.Null(chain.Textures);
        Assert.Equal((CreatureAppearanceState.TextureUnresolved, "CREATURE_DISPLAY_TEXTURES_UNREADABLE"), (chain.Model!.Appearance.State, chain.Model.Appearance.Code));
        Assert.Equal(CreatureFactsState.RowNotWhole, chain.DisplayFacts!.State);
    }

    // Was An_encrypted_texture_names_its_ordinal_and_is_never_retained.
    [Fact]
    public async Task An_encrypted_texture_names_its_ordinal_and_its_key()
    {
        var model = (await ChainAsync(Textured(files: [Stored(700, Blp()), Stored(800, Blp(), [new FileHole(0, 148, Key)])]))).Model!;
        Assert.Equal((CreatureAppearanceState.TextureProtected, 1), (model.Appearance.State, model.Appearance.TextureOrdinal!.Value));
        Assert.Equal((BlpImageState.Protected, Key), (model.Textures[1].Image!.Value, Assert.Single(model.Textures[1].HoleKeys!)));
        Assert.Equal(BlpImageState.Ready, model.Textures[0].Image);
        var missing = (await ChainAsync(Textured(files: [Stored(700, Blp())]))).Model!;
        Assert.Equal((CreatureAppearanceState.TextureUnavailable, 1, FileReadState.UnknownFile),
            (missing.Appearance.State, missing.Appearance.TextureOrdinal!.Value, missing.Textures[1].FileState!.Value));
    }

    [Theory]
    [InlineData("unsupported", CreatureAppearanceState.TextureUnsupported, BlpImageState.Unsupported)]
    [InlineData("corrupt", CreatureAppearanceState.TextureCorrupt, BlpImageState.Invalid)]
    public async Task A_texture_header_that_doesnt_read_names_its_state_and_ordinal(string kind, CreatureAppearanceState state, BlpImageState image)
    {
        var bad = kind == "unsupported" ? Blp(encoding: 4) : Blp(length: 100);
        var model = (await ChainAsync(Textured(files: [Stored(700, bad), Stored(800, Blp())]))).Model!;
        Assert.Equal((state, 0, image), (model.Appearance.State, model.Appearance.TextureOrdinal!.Value, model.Textures[0].Image!.Value));
    }

    [Theory]
    [InlineData(BlpImageState.Protected, CreatureAppearanceState.TextureProtected)]
    [InlineData(BlpImageState.Unsupported, CreatureAppearanceState.TextureUnsupported)]
    [InlineData(BlpImageState.LimitExceeded, CreatureAppearanceState.TextureLimit)]
    [InlineData(BlpImageState.MipUnavailable, CreatureAppearanceState.TextureLimit)]
    [InlineData(BlpImageState.Invalid, CreatureAppearanceState.TextureCorrupt)]
    [InlineData(BlpImageState.FileFailed, CreatureAppearanceState.TextureUnavailable)]
    public void Each_texture_header_failure_maps_to_its_appearance_state(BlpImageState image, CreatureAppearanceState state) =>
        Assert.Equal(state, CreatureDisplayRules.TextureFailure(image));

    [Fact]
    public async Task Files_that_dont_open_leave_a_resolved_model_unread_with_layer_4s_state()
    {
        var world = new CreaturesWorld().With(One(), DisplayTable([Display(100, "33")]), ModelTable([Model(33, "7001")]));
        var chain = await ChainAsync(world);
        Assert.Equal(CreatureDisplayChainState.MetadataResolved, chain.State);
        Assert.Equal((CreatureModelState.FilesNotOpen, (ModelReadState?)null, CreatureAppearanceState.ModelNotRead),
            (chain.Model!.State, chain.Model.ReadState, chain.Model.Appearance.State));
        Assert.NotNull(chain.Model.Code);
        Assert.Empty(chain.Model.Textures);
    }

    [Fact]
    public async Task An_encrypted_model_is_a_failed_read_with_its_keys_and_two_slots_read_it_once()
    {
        var world = new CreaturesWorld().With(CreatureTable([Creature(1, "Name", [100, 100, 0, 0])]), DisplayTable([Display(100, "33")]), ModelTable([Model(33, "7001")]))
            .WithFiles(Stored(7001, M2(), [new FileHole(0, M2().Length, Key)]));
        await using var creatures = await world.OpenAsync();
        var displays = (await creatures.GetAsync(1, Now, default)).Detail!.Displays;
        var model = displays[0].Model!;
        Assert.Equal((CreatureModelState.ReadFailed, ModelReadState.Protected, Key), (model.State, model.ReadState, Assert.Single(model.KeyNames!)));
        Assert.Equal(CreatureAppearanceState.ModelNotRead, model.Appearance.State);
        Assert.Same(model.KeyNames, displays[1].Model!.KeyNames);
    }
}
