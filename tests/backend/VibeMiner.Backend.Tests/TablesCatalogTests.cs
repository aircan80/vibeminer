using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TablesCatalogTests
{
    [Fact]
    public async Task Complete_provider_catalogue_pages_all_entries_without_claiming_build_files_or_rows()
    {
        await using var h = await Harness.Create("1.60.1.70450");
        var result = new List<TableCatalogEntry>(); string? after = null;
        do
        {
            var page = await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", Limit: 100, After: after), default);
            Assert.Equal("1346", page.TotalCount); Assert.InRange(page.Entries.Count, 1, 100);
            Assert.Equal(BundledTableDefinitions.Revision, page.Provider.Revision);
            Assert.Equal(BundledTableDefinitions.ManifestSha256, page.Provider.ManifestSha256);
            Assert.Equal("CC-BY-SA-4.0", page.Provider.License);
            result.AddRange(page.Entries); after = page.NextCursor;
        } while (after is not null);
        Assert.Equal(1346, result.Count); Assert.Equal(1346, result.Select(entry => entry.TableId).Distinct().Count());
        Assert.Equal(1270, result.Count(entry => entry.Db2FileDataId is not null));
        Assert.Equal(12, result.Count(entry => entry.DefinitionState == "bundled"));
        Assert.Equal(["Creature", "CreatureDisplayInfo", "CreatureModelData", "Item", "ItemClass", "ItemEffect", "ItemGroupSounds",
            "ItemSparse", "ItemSubClass", "ItemXItemEffect", "Material", "SpellName"], result.Take(12).Select(entry => entry.Name));
        Assert.All(result, entry => { Assert.Equal("notChecked", entry.SourceState); Assert.Equal("notPrepared", entry.RowsState); });
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Theory]
    [InlineData("1.60.1.70450", "9A2A4834")]
    [InlineData("12.1.5.70448", "996192AA")]
    public async Task Exact_Classic_and_retail_candidates_preserve_non_ID_reference_and_source_pin(string version, string layout)
    {
        await using var h = await Harness.Create(version);
        var definition = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "50238ec2"), default);
        Assert.Equal(version, definition.BuildVersion); Assert.Null(definition.Problem);
        Assert.Equal("076ea4c7e80c891b399cc77f285c807071080c0fed95701e447f7c98024ae120", definition.Definition!.Sha256);
        Assert.Contains(BundledTableDefinitions.Revision, definition.Definition.SourceUrl);
        var candidate = Assert.Single(definition.Definition.Candidates); Assert.Equal(layout, candidate.LayoutHash);
        var classId = Assert.Single(candidate.Fields, field => field.Name == "ClassID");
        Assert.Equal(new TableReference("ItemClass", "ClassID"), classId.Reference);
        Assert.Equal(32, classId.IntegerBits); Assert.False(classId.IsId);
        Assert.True(Assert.Single(candidate.Fields, field => field.Name == "ID").NonInline);
        Assert.All(definition.Definition.Columns, field => { Assert.Null(field.IntegerBits); Assert.False(field.IsId); Assert.False(field.NonInline); });
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Fact]
    public async Task ItemSparse_provider_preserves_exact_retail_layout_strings_and_trailing_stat_arrays_without_claiming_rows()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "919be54e"), default);
        Assert.Null(result.Problem);
        Assert.Equal("8664c3b5862dfe4c3a4322941b48b032c57b90412c070deb2028f3e448cf674a", result.Definition!.Sha256);
        var candidate = Assert.Single(result.Definition.Candidates);
        Assert.Equal("1C17D17F", candidate.LayoutHash);
        Assert.Equal("Display_lang", Assert.Single(candidate.Fields, field => field.Name == "Display_lang").Name);
        var stats = Assert.Single(candidate.Fields, field => field.Name == "StatModifier_bonusStat");
        Assert.Equal(10, stats.ArrayLength);
        Assert.True(Assert.Single(candidate.Fields, field => field.Name == "ID").NonInline);
        var entry = Assert.Single((await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", "1572924"), default)).Entries);
        Assert.Equal("notChecked", entry.SourceState); Assert.Equal("notPrepared", entry.RowsState);
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Theory]
    [InlineData("95d8638f", "ItemSubClass", "1261604", "1E67DB87",
        "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c", "ClassID", 8, false, "ItemClass", "ClassID")]
    [InlineData("00cb674f", "ItemXItemEffect", "3177687", "96F083AD",
        "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", "ItemID", 32, true, "Item", "ID")]
    public async Task Relationship_candidates_preserve_inline_and_external_storage_and_exact_target_column(
        string tableId, string name, string fileDataId, string layout, string sha256,
        string relationName, int bits, bool nonInline, string targetTable, string targetColumn)
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", tableId), default);
        Assert.Null(result.Problem);
        Assert.Equal(name, result.Entry.Name); Assert.Equal(fileDataId, result.Entry.Db2FileDataId);
        Assert.Equal("bundled", result.Entry.DefinitionState);
        Assert.Equal("notChecked", result.Entry.SourceState); Assert.Equal("notPrepared", result.Entry.RowsState);
        Assert.Equal(sha256, result.Definition!.Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledTableDefinitions.Revision}/definitions/{name}.dbd",
            result.Definition.SourceUrl);
        var candidate = Assert.Single(result.Definition.Candidates); Assert.Equal(layout, candidate.LayoutHash);
        var relation = Assert.Single(candidate.Fields, field => field.RelationStorage);
        Assert.Equal(relationName, relation.Name); Assert.Equal(bits, relation.IntegerBits);
        Assert.False(relation.Unsigned); Assert.False(relation.IsId); Assert.True(relation.VerifiedName);
        Assert.Equal(nonInline, relation.NonInline); Assert.Equal(1, relation.ArrayLength);
        Assert.Equal(new TableReference(targetTable, targetColumn), relation.Reference);
        var id = Assert.Single(candidate.Fields, field => field.IsId);
        Assert.Equal("ID", id.Name); Assert.False(id.RelationStorage);
        Assert.Equal(nonInline, id.NonInline);
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Theory]
    [InlineData("c9d6b6b3", "Creature", "841631", "6E14C900",
        "3644a603aae0eed595de31933c49e7bd8abea32bc1a1c32f9d754afbfb8f5682", true)]
    [InlineData("bfdaf9f1", "CreatureDisplayInfo", "1108759", "7275F5F6",
        "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", false)]
    [InlineData("7f695fa5", "CreatureModelData", "1365368", "0F5449F8",
        "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a", true)]
    public async Task Creature_metadata_selects_exact_retail_layout_and_preserves_record_id_storage_without_readiness_claims(
        string tableId, string name, string fileDataId, string layout, string sha256, bool externalId)
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", tableId), default);
        Assert.Null(result.Problem); Assert.Equal(name, result.Entry.Name);
        Assert.Equal(fileDataId, result.Entry.Db2FileDataId); Assert.Equal("bundled", result.Entry.DefinitionState);
        Assert.Equal("notChecked", result.Entry.SourceState); Assert.Equal("notPrepared", result.Entry.RowsState);
        Assert.Equal(sha256, result.Definition!.Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledTableDefinitions.Revision}/definitions/{name}.dbd",
            result.Definition.SourceUrl);
        var candidate = Assert.Single(result.Definition.Candidates); Assert.Equal(layout, candidate.LayoutHash);
        var id = Assert.Single(candidate.Fields, field => field.IsId);
        Assert.Equal("ID", id.Name); Assert.Equal(externalId, id.NonInline); Assert.True(id.VerifiedName);
        Assert.All(candidate.Fields, field => Assert.False(field.RelationStorage));
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Fact]
    public async Task Creature_candidate_preserves_name_fields_and_four_paired_display_slots()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "c9d6b6b3"), default);
        var fields = Assert.Single(result.Definition!.Candidates).Fields;
        Assert.Equal(["ID", "Name_lang", "NameAlt_lang", "Title_lang", "TitleAlt_lang"], fields.Take(5).Select(field => field.Name));
        Assert.All(fields.Skip(1).Take(4), field => { Assert.Equal("locstring", field.Family); Assert.True(field.VerifiedName); });
        Assert.Equal("DisplayID", fields[9].Name); Assert.Equal(4, fields[9].ArrayLength);
        Assert.Equal(new TableReference("CreatureDisplayInfo", "ID"), fields[9].Reference);
        Assert.Equal("DisplayProbability", fields[10].Name); Assert.Equal("float", fields[10].Family);
        Assert.Equal(4, fields[10].ArrayLength); Assert.Null(fields[10].Reference);
        Assert.Equal("AlwaysItem", fields[11].Name); Assert.Equal(3, fields[11].ArrayLength);
        Assert.Equal(8, Assert.Single(fields, field => field.Name == "StartAnimState").IntegerBits);
    }

    [Fact]
    public async Task Display_candidate_keeps_model_portrait_and_four_texture_references_distinct()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "bfdaf9f1"), default);
        var fields = Assert.Single(result.Definition!.Candidates).Fields;
        var model = Assert.Single(fields, field => field.Name == "ModelID");
        Assert.Equal(16, model.IntegerBits); Assert.Equal(new TableReference("CreatureModelData", "ID"), model.Reference);
        var texture = Assert.Single(fields, field => field.Name == "TextureVariationFileDataID");
        Assert.Equal(texture, fields[^1]); Assert.Equal(4, texture.ArrayLength);
        Assert.Equal(new TableReference("FileData", "ID"), texture.Reference); Assert.True(texture.VerifiedName);
        Assert.Equal(new TableReference("FileData", "ID"), Assert.Single(fields, field => field.Name == "PortraitTextureFileDataID").Reference);
        Assert.Equal(new TableReference("CreatureDisplayInfo", "ID"), Assert.Single(fields, field => field.Name == "PortraitCreatureDisplayInfoID").Reference);
        var conditional = Assert.Single(fields, field => field.Name == "ConditionalCreatureModelID");
        Assert.False(conditional.VerifiedName); Assert.Equal(new TableReference("ConditionalCreatureModelData", "ID"), conditional.Reference);
        Assert.Equal("float", Assert.Single(fields, field => field.Name == "CreatureModelScale").Family);
        Assert.Equal(8, Assert.Single(fields, field => field.Name == "CreatureModelAlpha").IntegerBits);
    }

    [Fact]
    public async Task Model_candidate_preserves_six_bounds_values_and_distinct_file_scale_and_geoset_inputs()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "7f695fa5"), default);
        var fields = Assert.Single(result.Definition!.Candidates).Fields;
        var bounds = fields[1]; Assert.Equal("GeoBox", bounds.Name); Assert.Equal("float", bounds.Family);
        Assert.Equal(6, bounds.ArrayLength); Assert.Null(bounds.Reference);
        var file = Assert.Single(fields, field => field.Name == "FileDataID");
        Assert.Equal(new TableReference("FileData", "ID"), file.Reference); Assert.Equal(1, file.ArrayLength);
        Assert.Equal("float", Assert.Single(fields, field => field.Name == "ModelScale").Family);
        Assert.Equal(1, Assert.Single(fields, field => field.Name == "CreatureGeosetDataID").ArrayLength);
        Assert.False(Assert.Single(fields, field => field.Name == "WalkSpeed").VerifiedName);
        Assert.False(Assert.Single(fields, field => field.Name == "RunSpeed").VerifiedName);
    }

    [Theory]
    [InlineData("4002a5b1", "ItemEffect", "969941", "4CA77678",
        "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c")]
    [InlineData("46c66698", "SpellName", "1990283", "782EE721",
        "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667")]
    public async Task Effect_and_name_definitions_preserve_exact_source_identity_without_claiming_rows(
        string tableId, string name, string fileDataId, string layout, string sha256)
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", tableId), default);
        Assert.Null(result.Problem); Assert.Equal(name, result.Entry.Name);
        Assert.Equal(fileDataId, result.Entry.Db2FileDataId); Assert.Equal("bundled", result.Entry.DefinitionState);
        Assert.Equal("notChecked", result.Entry.SourceState); Assert.Equal("notPrepared", result.Entry.RowsState);
        Assert.Equal(sha256, result.Definition!.Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledTableDefinitions.Revision}/definitions/{name}.dbd",
            result.Definition.SourceUrl);
        var candidate = Assert.Single(result.Definition.Candidates); Assert.Equal(layout, candidate.LayoutHash);
        var id = Assert.Single(candidate.Fields, field => field.IsId);
        Assert.Equal("ID", id.Name); Assert.True(id.NonInline); Assert.True(id.VerifiedName);
        Assert.All(candidate.Fields, field => Assert.False(field.RelationStorage));
        Assert.False(Directory.Exists(Path.Combine(h.Directory, "objects-v1")));
    }

    [Fact]
    public async Task Selected_ItemEffect_fields_preserve_signed_inputs_targets_and_unverified_condition_without_historical_parent_join()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "4002a5b1"), default);
        var fields = Assert.Single(result.Definition!.Candidates).Fields;
        Assert.Equal(["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec",
            "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"], fields.Select(field => field.Name));
        foreach (var name in new[] { "LegacySlotIndex", "TriggerType" })
        {
            var field = Assert.Single(fields, field => field.Name == name);
            Assert.Equal(8, field.IntegerBits); Assert.True(field.Unsigned); Assert.True(field.VerifiedName);
        }
        var charges = Assert.Single(fields, field => field.Name == "Charges");
        Assert.Equal(16, charges.IntegerBits); Assert.False(charges.Unsigned);
        foreach (var name in new[] { "CoolDownMSec", "CategoryCoolDownMSec", "SpellID" })
        {
            var field = Assert.Single(fields, field => field.Name == name);
            Assert.Equal(32, field.IntegerBits); Assert.False(field.Unsigned); Assert.True(field.VerifiedName);
        }
        Assert.Equal(new TableReference("Spell", "ID"), Assert.Single(fields, field => field.Name == "SpellID").Reference);
        foreach (var (name, target) in new[] { ("SpellCategoryID", "SpellCategory"), ("ChrSpecializationID", "ChrSpecialization") })
        {
            var field = Assert.Single(fields, field => field.Name == name);
            Assert.Equal(16, field.IntegerBits); Assert.True(field.Unsigned); Assert.Equal(new TableReference(target, "ID"), field.Reference);
        }
        var condition = Assert.Single(fields, field => field.Name == "PlayerConditionID");
        Assert.False(condition.VerifiedName); Assert.Equal(new TableReference("PlayerCondition", "ID"), condition.Reference);
        Assert.Contains(result.Definition.Columns, field => field.Name == "ParentItemID");
        Assert.DoesNotContain(fields, field => field.Name == "ParentItemID");
    }

    [Fact]
    public async Task SpellName_has_only_its_exact_record_identity_and_localized_name_not_description_or_effect_fields()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "46c66698"), default);
        var fields = Assert.Single(result.Definition!.Candidates).Fields;
        Assert.Equal(["ID", "Name_lang"], fields.Select(field => field.Name));
        var name = fields[1]; Assert.Equal("locstring", name.Family); Assert.True(name.VerifiedName);
        Assert.Equal(1, name.ArrayLength); Assert.Null(name.Reference); Assert.Null(name.IntegerBits);
        Assert.False(name.NonInline); Assert.False(name.IsId); Assert.False(name.RelationStorage);
    }

    [Theory]
    [InlineData("4002a5b1", "4CA77678", "782EE721")]
    [InlineData("46c66698", "782EE721", "4CA77678")]
    public async Task Effect_definition_selection_refuses_the_other_table_layout_and_an_unlisted_build(
        string tableId, string actualLayout, string otherLayout)
    {
        var provider = await BundledTableDefinitions.LoadAsync();
        var definition = provider.Definitions[tableId].Document;
        Assert.Single(definition.SelectBuild("12.1.5.70448", actualLayout).Blocks);
        var wrongDefinition = definition.SelectBuild("12.1.5.70448", otherLayout);
        Assert.Equal("missing", wrongDefinition.Status); Assert.Empty(wrongDefinition.Blocks);
        var wrongBuild = definition.SelectBuild("0.0.0.1", actualLayout);
        Assert.Equal("missing", wrongBuild.Status); Assert.Empty(wrongBuild.Blocks);
    }

    [Theory]
    [InlineData("ItemSubClass", "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c", 28231)]
    [InlineData("ItemXItemEffect", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb", 11271)]
    [InlineData("Creature", "3644a603aae0eed595de31933c49e7bd8abea32bc1a1c32f9d754afbfb8f5682", 28281)]
    [InlineData("CreatureDisplayInfo", "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", 38876)]
    [InlineData("CreatureModelData", "ace9dfbb1acdec0eae7899273a4e983d851d2de8d6471fd8eb95ac8e70584e2a", 40383)]
    [InlineData("ItemEffect", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c", 27918)]
    [InlineData("SpellName", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667", 23534)]
    public async Task Bundled_definition_resources_match_original_pinned_bytes_and_attribution(string name, string hash, int length)
    {
        var assembly = typeof(DiscoveryCatalog).Assembly;
        async Task<byte[]> Resource(string relative)
        {
            var suffix = ".Tables.Resources.WoWDBDefs." + BundledTableDefinitions.Revision + "." + relative.Replace('/', '.');
            var resource = Assert.Single(assembly.GetManifestResourceNames(), value => value.EndsWith(suffix, StringComparison.Ordinal));
            await using var input = assembly.GetManifestResourceStream(resource)!;
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes); return bytes.ToArray();
        }
        var data = await Resource("definitions/" + name + ".dbd");
        Assert.Equal(length, data.Length); Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(data)));
        using var sources = JsonDocument.Parse(await Resource("sources.json"));
        Assert.Equal(BundledTableDefinitions.Revision, sources.RootElement.GetProperty("revision").GetString());
        Assert.Equal("CC-BY-SA-4.0", sources.RootElement.GetProperty("definitionLicense").GetString());
        var receipt = Assert.Single(sources.RootElement.GetProperty("resources").EnumerateArray(),
            source => source.GetProperty("path").GetString() == "definitions/" + name + ".dbd");
        Assert.Equal(hash, receipt.GetProperty("sha256").GetString()); Assert.Equal(length, receipt.GetProperty("byteLength").GetInt32());
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{BundledTableDefinitions.Revision}/definitions/{name}.dbd",
            receipt.GetProperty("sourceUrl").GetString());
        var manifest = await Resource("manifest.json");
        Assert.Equal(BundledTableDefinitions.ManifestSha256, Convert.ToHexStringLower(SHA256.HashData(manifest)));
        Assert.Equal(1346, BundledTableDefinitions.ParseManifest(manifest).Count);
    }

    [Fact]
    public async Task Missing_bundled_definition_and_missing_exact_build_are_separate_metadata_states()
    {
        await using var h = await Harness.Create("1.2.3.12345");
        var page = await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", "Achievement", 100), default);
        var unbundled = page.Entries.First(entry => entry.DefinitionState == "notBundled");
        var missing = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", unbundled.TableId), default);
        Assert.Null(missing.Definition); Assert.Equal("DEFINITION_NOT_BUNDLED", missing.Problem!.Code);
        // D080: 1.2.3.12345 is not listed, so the preview offers only the layouts a 1.x file could open by.
        var candidate = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "50238ec2"), default);
        Assert.NotNull(candidate.Definition); Assert.Equal("SCHEMA_BUILD_NOT_LISTED", candidate.Problem!.Code);
        Assert.Contains("matches one of its candidate layouts", candidate.Problem.Message);
        var document = (await TablesCatalog.Provider.Value).Definitions["50238ec2"].Document;
        Assert.Equal(document.LayoutCandidates("1.2.3.12345").Select(value => value.LayoutHash), candidate.Definition.Candidates.Select(value => value.LayoutHash));
        Assert.NotEmpty(candidate.Definition.Candidates);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "deadbeef"), default));
    }

    [Fact]
    public async Task A_build_on_a_version_line_no_block_lists_offers_no_layout_candidates()
    {
        await using var h = await Harness.Create("99.0.0.1");
        var definition = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", "50238ec2"), default);
        Assert.Empty(definition.Definition!.Candidates); Assert.Equal("SCHEMA_BUILD_NOT_LISTED", definition.Problem!.Code);
        Assert.Contains("any layout on its version line", definition.Problem.Message);
    }

    [Theory]
    [InlineData("12.1.5.70575")]
    [InlineData("5.5.4.70579")]
    public async Task Newer_unlisted_retail_and_Classic_builds_preview_layout_only_candidates_for_every_bundled_table(string version)
    {
        await using var h = await Harness.Create(version);
        var provider = await TablesCatalog.Provider.Value;
        foreach (var (tableId, bundled) in provider.Definitions)
        {
            var definition = await h.Tables.ReadDefinitionAsync(new("wow", h.Id, "us", tableId), default);
            Assert.Equal("SCHEMA_BUILD_NOT_LISTED", definition.Problem!.Code);
            Assert.Equal(bundled.Document.LayoutCandidates(version).Select(value => value.LayoutHash), definition.Definition!.Candidates.Select(value => value.LayoutHash));
            Assert.All(definition.Definition.Candidates, value => Assert.NotEmpty(value.Fields));
            // Every bundled table has retail 12.x blocks; ItemXItemEffect has no 5.x block at all.
            if (version.StartsWith("12.", StringComparison.Ordinal) || bundled.Document.Blocks.Any(block => block.Builds.Any(range => range.Last.Major == 5)))
                Assert.NotEmpty(definition.Definition.Candidates);
            else Assert.Contains("any layout on its version line", definition.Problem.Message);
        }
    }

    [Fact]
    public async Task Literal_search_includes_bundled_column_names_and_validates_exact_admitted_context()
    {
        await using var h = await Harness.Create("12.1.5.70448");
        var result = await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", "ClassID"), default);
        Assert.Equal("ClassID", result.Query); Assert.Contains(result.Entries, entry => entry.Name == "Item");
        Assert.Contains(result.Entries, entry => entry.Name == "ItemClass");
        Assert.Empty((await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", "%"), default)).Entries);
        Assert.Single((await h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", "841626"), default)).Entries);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Tables.ReadCatalogAsync(new("wow", h.Id, "eu"), default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", Limit: 101), default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Tables.ReadCatalogAsync(new("wow", h.Id, "us", After: "1347"), default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Tables.ReadCatalogAsync(new("wow", h.Id, "us"), cancellation.Token));
    }

    [Theory]
    [InlineData("[{\"tableHash\":\"ABCD1234\"},{\"tableHash\":\"abcd1234\"}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"tableHash\":\"00000001\"}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":4294967296}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":1.5}]")]
    [InlineData("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":0}]")]
    public void Manifest_rejects_duplicate_hashes_and_lossy_or_invalid_file_identities(string json) =>
        Assert.Equal("DEFINITION_PROVIDER_INVALID", Assert.Throws<DiscoveryException>(() => BundledTableDefinitions.ParseManifest(Encoding.UTF8.GetBytes(json))).Code);

    [Fact]
    public void Manifest_preserves_unsigned32_identity_without_signed_int_conversion()
    {
        var entry = Assert.Single(BundledTableDefinitions.ParseManifest("[{\"tableHash\":\"ABCD1234\",\"db2FileDataID\":4294967295}]"u8.ToArray()));
        Assert.Equal("abcd1234", entry.TableId); Assert.Equal("4294967295", entry.Db2FileDataId); Assert.Null(entry.Name);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-tables-" + Guid.NewGuid().ToString("N"));
        internal string Id { get; private set; } = "";
        private DiscoveryCatalog catalog = null!;
        internal TablesCatalog Tables { get; private set; } = null!;
        internal static async Task<Harness> Create(string version)
        {
            var h = new Harness(); var fixture = InventoryFixture.Create();
            h.catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Tables = new(h.catalog);
            var old = fixture.Discovery.Documents.Single(document => document.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(old.Bytes).Replace("1.2.3.12345", version, StringComparison.Ordinal)
                .Replace("|12345|", "|" + version.Split('.')[^1] + "|", StringComparison.Ordinal));
            var row = VibeMiner.Protocols.Tact.MetadataParser.Parse(bytes).Rows.Single();
            var identity = BuildIdentity.Create("wow", row.Fields); h.Id = identity.Id;
            var source = fixture.Discovery with
            {
                Documents = [old with { Bytes = bytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) }, fixture.Discovery.Documents.Single(d => d.Role == "cdns")],
                Observations = [fixture.Discovery.Observations.Single() with { Identity = identity, OriginalFields = row.Fields }]
            };
            var generation = await h.catalog.BeginRefreshAsync("wow", default);
            await h.catalog.CommitAsync("wow", generation, source, DateTimeOffset.UtcNow.ToString("O"), default);
            var context = await h.catalog.ResolveOpeningAsync(new("wow", h.Id, "us"), default);
            var receipts = new List<ConfigurationReceipt>();
            foreach (var (role, key, content) in new[] { ("buildConfig", fixture.BuildKey, fixture.BuildConfig), ("cdnConfig", fixture.CdnKey, fixture.CdnConfig) })
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(content)); var url = InventoryFixture.Url("config", key);
                const string time = "2026-10-19T00:00:00Z";
                await h.catalog.RecordConfigurationAsync(new(key, hash, content.Length, url, time), default);
                receipts.Add(new(role, key, hash, content.Length, url, time, time, 1));
            }
            await h.catalog.CommitWorkspaceAsync(context, receipts, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }
        public async ValueTask DisposeAsync() { await catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
