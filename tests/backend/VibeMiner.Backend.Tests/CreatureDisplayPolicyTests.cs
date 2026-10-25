using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureDisplayPolicyTests
{
    [Theory]
    [InlineData("BLTE_RANGE_UNAVAILABLE", CreatureDisplayMetadataState.Protected)]
    [InlineData("DB2_ENCRYPTED", CreatureDisplayMetadataState.Protected)]
    [InlineData("DB2_SCHEMA_MISSING", CreatureDisplayMetadataState.Unsupported)]
    [InlineData("NETWORK_UNAVAILABLE", CreatureDisplayMetadataState.Unavailable)]
    [InlineData("CREATURE_DISPLAY_RESOURCE_LIMIT", CreatureDisplayMetadataState.ResourceLimit)]
    [InlineData("BLTE_INTEGRITY", CreatureDisplayMetadataState.Corrupt)]
    public void OptionalFailuresPreserveProtectedUnsupportedUnavailableCorruptAndLimitStates(string code, CreatureDisplayMetadataState state) =>
        Assert.Equal(state, CreatureDisplayPolicy.FailureState(new DiscoveryException(code, "Authored source outcome.")));
    [Theory]
    [InlineData(CreatureDisplayTargetRole.Display, "ModelID")]
    [InlineData(CreatureDisplayTargetRole.Model, "FileDataID")]
    public async Task ExactBundledActualProfilesQualifyOnlyTheirSelectedUnsignedScalar(CreatureDisplayTargetRole role, string name)
    {
        var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[CreatureDisplayPolicy.Table(role)];
        Assert.Equal(CreatureDisplayPolicy.Definition(role), definition.Sha256);
        var rows = Rows(definition.Document, role);
        var column = CreatureDisplayPolicy.SelectField(definition.Document, "12.1.5.70448", rows, role);
        Assert.Equal(name, rows.Header.Columns[column].Name); Assert.False(rows.Header.Columns[column].IsId);
        Assert.Null(rows.Header.Columns[column].Interpretation);
    }
    [Theory]
    [InlineData("signed")]
    [InlineData("width")]
    [InlineData("array")]
    [InlineData("unverified")]
    [InlineData("id")]
    [InlineData("nonInline")]
    [InlineData("relation")]
    public async Task SemanticEdgeNeverWidensOrAcceptsAnUnqualifiedCandidate(string mutation)
    {
        var provider = await TablesCatalog.Provider.Value; var original = provider.Definitions[CreatureDisplayPolicy.DisplayTable].Document;
        var rows = Rows(original, CreatureDisplayTargetRole.Display);
        var blocks = original.Blocks.Select(block => block with { Fields = block.Fields.Select(field => field.Column.Name != "ModelID" ? field : mutation switch
        {
            "signed" => field with { Unsigned = false },
            "width" => field with { IntegerBits = 32 },
            "array" => field with { ArrayLength = 2 },
            "unverified" => field with { Column = field.Column with { VerifiedName = false } },
            "id" => field with { IsId = true },
            "nonInline" => field with { NonInline = true },
            _ => field with { RelationStorage = true }
        }).ToArray() }).ToArray();
        Assert.Equal("CREATURE_DISPLAY_SCHEMA_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.SelectField(original with { Blocks = blocks }, "12.1.5.70448", rows, CreatureDisplayTargetRole.Display)).Code);
    }
    [Fact]
    public async Task RawPhysicalColumnCannotSupplyModelAuthority()
    {
        var provider = await TablesCatalog.Provider.Value; var definition = provider.Definitions[CreatureDisplayPolicy.DisplayTable].Document;
        var rows = Rows(definition, CreatureDisplayTargetRole.Display); var ordinal = CreatureDisplayPolicy.SelectField(definition, "12.1.5.70448", rows, CreatureDisplayTargetRole.Display);
        var columns = rows.Header.Columns.ToArray(); columns[ordinal] = columns[ordinal] with { Interpretation = new("raw", 32, true, 16, true, "unqualified") };
        Assert.Equal("CREATURE_DISPLAY_SCHEMA_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.SelectField(definition, "12.1.5.70448", rows with { Header = rows.Header with { Columns = columns } }, CreatureDisplayTargetRole.Display)).Code);
    }
    [Theory]
    [InlineData(CreatureDisplayTargetRole.Display, "65535", true)]
    [InlineData(CreatureDisplayTargetRole.Display, "65536", false)]
    [InlineData(CreatureDisplayTargetRole.Model, "4294967295", true)]
    [InlineData(CreatureDisplayTargetRole.Model, "4294967296", false)]
    [InlineData(CreatureDisplayTargetRole.Model, "-1", false)]
    [InlineData(CreatureDisplayTargetRole.Model, "01", false)]
    [InlineData(CreatureDisplayTargetRole.Model, "+1", false)]
    public void CanonicalEdgeValuesRespectSemanticWidthAndExactEmittedTarget(CreatureDisplayTargetRole role, string value, bool valid)
    {
        var row = new Db2Row(123, true, 99, ["123", value]);
        if (valid) Assert.Equal(value, CreatureDisplayPolicy.Edge(row, 1, role, "123"));
        else Assert.Equal("CREATURE_DISPLAY_INTEGRITY", Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.Edge(row, 1, role, "123")).Code);
        Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.Edge(row, 1, role, "99"));
        Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.Edge(row with { CopySourceId = null }, 1, role, "123"));
    }
    [Theory]
    [InlineData(CreatureDisplayTargetRole.Display, "F75F884C")]
    [InlineData(CreatureDisplayTargetRole.Model, "3FFEE408")]
    public async Task Classic_layouts_qualify_the_same_named_edge_and_other_layouts_stay_unsupported(CreatureDisplayTargetRole role, string layout)
    {
        var definition = (await TablesCatalog.Provider.Value).Definitions[CreatureDisplayPolicy.Table(role)].Document;
        var rows = Rows(definition, role, "5.5.4.67849", layout);
        var column = CreatureDisplayPolicy.SelectField(definition, "5.5.4.67849", rows, role);
        Assert.Equal(CreatureDisplayPolicy.Field(role), rows.Header.Columns[column].Name);
        var other = rows with { Header = rows.Header with { LayoutHash = role == CreatureDisplayTargetRole.Display ? "3FFEE408" : "F75F884C" } };
        Assert.Equal("CREATURE_DISPLAY_SCHEMA_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => CreatureDisplayPolicy.SelectField(definition, "5.5.4.67849", other, role)).Code);
    }
    [Fact]
    public async Task Texture_evidence_reads_all_four_variations_and_refuses_unreadable_fields()
    {
        var definition = (await TablesCatalog.Provider.Value).Definitions[CreatureDisplayPolicy.Table(CreatureDisplayTargetRole.Display)].Document;
        var header = Rows(definition, CreatureDisplayTargetRole.Display).Header;
        int Column(string name) => header.Columns.Select((c, i) => (c, i)).Single(x => x.c.Name == name).i;
        var values = header.Columns.Select(_ => "0").ToArray(); values[Column("ExtendedDisplayInfoID")] = "7"; values[Column("TextureVariationFileDataID[2]")] = "-3";
        var evidence = CreatureDisplayPolicy.TextureEvidence(new(1, false, null, values), header)!;
        Assert.Equal("7", evidence.ExtendedDisplayInfoId); Assert.Equal(["0", "0", "-3", "0"], evidence.TextureVariations);
        values[Column("TextureVariationFileDataID[1]")] = "007";
        Assert.Null(CreatureDisplayPolicy.TextureEvidence(new(1, false, null, values), header));
        values[Column("TextureVariationFileDataID[1]")] = "0";
        Assert.Null(CreatureDisplayPolicy.TextureEvidence(new(1, false, null, values, [new(Column("ExtendedDisplayInfoID"), "TEXT", "00")]), header));
    }
    internal static TableRowsArtifact Rows(DbdDocument document, CreatureDisplayTargetRole role, string build, string layout)
    {
        var block = Assert.Single(document.SelectBuild(build, layout).Blocks);
        var columns = block.Fields.SelectMany(field => Enumerable.Range(0, field.ArrayLength).Select(slot =>
            new Db2Column(field.Column.Name + (field.ArrayLength == 1 ? "" : "[" + slot + "]"),
                field.Column.Family == "int" ? "integer" : field.Column.Family == "float" ? "float" : "string", field.IsId))).ToArray();
        return new(new('a', 64), new('b', 64), 64, null!, new(5, CreatureDisplayPolicy.Table(role).ToUpperInvariant(), layout, 1, 0, 0, columns),
            new(1, 1, 0, 0, 0, 0, "complete", 64));
    }
    internal static TableRowsArtifact Rows(DbdDocument document, CreatureDisplayTargetRole role)
    {
        var block = Assert.Single(document.SelectBuild("12.1.5.70448", CreatureDisplayPolicy.Layout(role)).Blocks);
        var columns = block.Fields.SelectMany(field => Enumerable.Range(0, field.ArrayLength).Select(slot =>
            new Db2Column(field.Column.Name + (field.ArrayLength == 1 ? "" : "[" + slot + "]"),
                field.Column.Family == "int" ? "integer" : field.Column.Family == "float" ? "float" : "string", field.IsId))).ToArray();
        return new(new('a', 64), new('b', 64), 64, null!, new(5, CreatureDisplayPolicy.Table(role).ToUpperInvariant(), CreatureDisplayPolicy.Layout(role), 1, 0, 0, columns),
            new(1, 1, 0, 0, 0, 0, "complete", 64));
    }
}
