using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DefinitionReaderTests
{
    private const string Source = """
        COLUMNS
        int ID
        int<Target::ClassID> ClassID? // author confidence
        locstring Name
        int Sounds

        LAYOUT ABCD1234, 00000001
        BUILD 1.2.3.4, 1.60.1.70450
        BUILD 12.1.5.70000-12.1.5.70448
        $noninline,id$ID<32>
        $relation$ClassID<u8> // field note
        Name
        Sounds<u32>[4]
        """;

    [Fact]
    public void Preserves_candidate_metadata_widths_annotations_references_confidence_comments_and_arrays()
    {
        var definition = Parse(Source);
        Assert.Equal(4, definition.Columns.Count);
        var selected = definition.SelectBuild("1.60.1.70450");
        Assert.Equal("candidate", selected.Status);
        var block = Assert.Single(selected.Blocks);
        Assert.Equal(["ABCD1234", "00000001"], block.LayoutHashes);
        Assert.True(block.Fields[0].IsId); Assert.True(block.Fields[0].NonInline);
        Assert.False(block.Fields[0].Unsigned); Assert.Equal(32, block.Fields[0].IntegerBits);
        var field = block.Fields[1];
        Assert.Equal(new DbdReference("Target", "ClassID"), field.Column.Reference);
        Assert.False(field.Column.VerifiedName); Assert.True(field.RelationStorage);
        Assert.Equal("author confidence\nfield note", field.Comment);
        Assert.True(field.Unsigned); Assert.Equal(8, field.IntegerBits);
        Assert.Null(block.Fields[2].IntegerBits); Assert.Null(block.Fields[2].Unsigned);
        Assert.Equal(4, block.Fields[3].ArrayLength);
        Assert.Equal("missing", definition.SelectBuild("1.60.1.70449").Status);
        Assert.Equal("candidate", definition.SelectBuild("12.1.5.70448", "abcd1234").Status);
        Assert.Equal("missing", definition.SelectBuild("12.1.5.70449").Status);
        Assert.Equal("missing", definition.SelectBuild("12.1.5.70448", "DEADBEEF").Status);
    }

    [Fact]
    public void Overlapping_exact_selectors_remain_ambiguous_instead_of_choosing_first_or_latest()
    {
        var source = Source + "\n\nLAYOUT ABCD1234\nBUILD 1.60.1.70450\nID<32>\n";
        var selected = Parse(source).SelectBuild("1.60.1.70450", "ABCD1234");
        Assert.Equal("ambiguous", selected.Status); Assert.Equal(2, selected.Blocks.Count);
        Assert.NotEqual(selected.Blocks[0].Fields.Count, selected.Blocks[1].Fields.Count);
    }

    [Fact]
    public void Legacy_build_only_blocks_remain_metadata_without_an_invented_layout()
    {
        var document = Parse("COLUMNS\nint ID\n\nBUILD 3.0.1.8622-3.3.5.12340\n$id$ID\n");
        var block = Assert.Single(document.SelectBuild("3.1.0.9000").Blocks);
        Assert.Empty(block.LayoutHashes); Assert.Null(block.Fields[0].IntegerBits);
        Assert.Equal("missing", document.SelectBuild("3.1.0.9000", "ABCD1234").Status);
    }

    [Theory]
    [InlineData("COLUMNS\nint ID\nint ID\n\nBUILD 1.2.3.4\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT ABCD1234\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4\nOther<32>")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4\nID<32>\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nLAYOUT ABCD1234\nLAYOUT ABCD1234\nBUILD 1.2.3.4\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.9-1.2.3.4\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4,\nID<32>")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4\nID<32>\nBUILD 1.2.3.5")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4\nID<32>[0]")]
    [InlineData("COLUMNS\nint ID\n\nBUILD 1.2.3.4\nID<32>\0")]
    public void Rejects_malformed_or_incomplete_definition_grammar(string text) =>
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => Parse(text)).Code);

    [Theory]
    [InlineData("COLUMNS\nbool Value\n\nBUILD 1.2.3.4\nValue")]
    [InlineData("COLUMNS\nfloat Value\n\nBUILD 1.2.3.4\nValue<32>")]
    [InlineData("COLUMNS\nint Value\n\nBUILD 1.2.3.4\nValue<24>")]
    [InlineData("COLUMNS\nint Value\n\nBUILD 1.2.3.4\n$unknown$Value<32>")]
    public void Unsupported_families_widths_and_annotations_do_not_silently_degrade(string text) =>
        Assert.Equal("DBD_UNSUPPORTED", Assert.Throws<DiscoveryException>(() => Parse(text)).Code);

    [Fact]
    public void Bounds_bytes_lines_columns_blocks_fields_selectors_arrays_comments_and_observes_cancellation()
    {
        var limits = new DbdLimits[] { new(MaxBytes: 10), new(MaxLines: 2), new(MaxLineCharacters: 8), new(MaxColumns: 2),
            new(MaxFieldsPerBlock: 2), new(MaxSelectors: 2), new(MaxArrayLength: 2), new(MaxCommentCharacters: 4) };
        foreach (var limit in limits)
            Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() => DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(Source), limit)).Code);
        Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() =>
            DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(Source + "\n\nBUILD 1.2.3.5\nID<32>"), new(MaxBlocks: 1))).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(Source), token: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Parse(Source).SelectBuild("1.2.3.4", token: cancellation.Token));
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => DbdDefinitionReader.Parse([0xff])).Code);
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => Parse(Source.Replace('\n', '\r'))).Code);
        Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() => Parse("COLUMNS\nint " + new string('X', 129) + "\n\nBUILD 1.2.3.4\nID<32>" )).Code);
        Assert.Equal("DBD_LIMIT", Assert.Throws<DiscoveryException>(() => Parse("COLUMNS\nint<" + new string('X', 129) + "::ID> Value\n\nBUILD 1.2.3.4\nValue<32>" )).Code);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.04")]
    [InlineData("1.2.3.4294967296")]
    [InlineData("1.2.3.-1")]
    public void Build_selection_requires_the_exact_numeric_version_without_truncation_or_fallback(string build) =>
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => Parse(Source).SelectBuild(build)).Code);

    private static DbdDocument Parse(string text) => DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(text));
}
