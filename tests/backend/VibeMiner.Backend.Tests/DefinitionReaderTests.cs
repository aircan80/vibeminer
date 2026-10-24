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

    // D080 fixture: AAAA0001 is shared by a Classic 5.5 block and two retail 12.x blocks with different field sets.
    private const string Lines = """
        COLUMNS
        int ID
        int Value
        int Extra

        LAYOUT AAAA0001
        BUILD 5.5.0.62518-5.5.4.70449
        $id$ID<32>
        Value<u8>

        LAYOUT AAAA0001, BBBB0002
        BUILD 12.0.0.65655-12.0.7.68974
        $id$ID<32>
        Value<u16>

        LAYOUT AAAA0001
        BUILD 12.1.7.70424
        $id$ID<32>
        Value<u32>

        LAYOUT CCCC0003
        BUILD 12.1.5.70448
        $id$ID<32>
        Extra<u32>
        """;

    [Fact]
    public void A_listed_build_keeps_exact_build_and_layout_selection_without_a_layout_fallback()
    {
        var document = Parse(Lines);
        var listed = document.SelectFile("12.1.5.70448", "CCCC0003");
        Assert.Equal(("candidate", "build"), (listed.Status, listed.Match));
        Assert.Equal("Extra", Assert.Single(listed.Blocks).Fields[1].Column.Name);
        // The build is listed, so another block's layout is not borrowed even though AAAA0001 exists.
        var other = document.SelectFile("12.1.5.70448", "AAAA0001");
        Assert.Equal(("missing", "build"), (other.Status, other.Match));
        Assert.Empty(document.LayoutCandidates("12.1.5.70448"));
        Assert.True(document.ListsBuild("12.1.5.70448")); Assert.False(document.ListsBuild("12.1.5.70575"));
    }

    [Fact]
    public void An_unlisted_build_uses_the_block_whose_layout_matches_the_file()
    {
        var selected = Parse(Lines).SelectFile("12.1.5.70575", "bbbb0002");
        Assert.Equal(("candidate", "layout"), (selected.Status, selected.Match));
        Assert.Equal(16, Assert.Single(selected.Blocks).Fields[1].IntegerBits);
    }

    [Fact]
    public void A_layout_shared_across_version_lines_prefers_the_nearest_block_on_the_files_line()
    {
        var document = Parse(Lines);
        // Retail 70575 is 151 builds from 12.1.7.70424 and 1601 from 12.0.7.68974.
        Assert.Equal(32, Assert.Single(document.SelectFile("12.1.5.70575", "AAAA0001").Blocks).Fields[1].IntegerBits);
        Assert.Equal(16, Assert.Single(document.SelectFile("12.0.7.69000", "AAAA0001").Blocks).Fields[1].IntegerBits);
        // Classic 5.5.4.70579 shares a build counter with retail but only its own line counts.
        Assert.Equal(8, Assert.Single(document.SelectFile("5.5.4.70579", "AAAA0001").Blocks).Fields[1].IntegerBits);
        // No block on the 1.x line: the layout is not taken from another line.
        var other = document.SelectFile("1.15.8.70000", "AAAA0001");
        Assert.Equal(("missing", "layout"), (other.Status, other.Match));
        Assert.Empty(document.LayoutCandidates("1.15.8.70000"));
        Assert.Equal(["AAAA0001", "BBBB0002", "CCCC0003"], document.LayoutCandidates("12.1.5.70575").Select(value => value.LayoutHash));
        Assert.Equal(["AAAA0001"], document.LayoutCandidates("5.5.4.70579").Select(value => value.LayoutHash));
    }

    [Fact]
    public void Equally_near_blocks_with_different_fields_are_ambiguous_and_identical_ones_are_not()
    {
        var tie = Parse("COLUMNS\nint ID\nint Value\n\nLAYOUT AAAA0001\nBUILD 12.0.0.100\n$id$ID<32>\nValue<u8>\n\nLAYOUT AAAA0001\nBUILD 12.0.0.300\n$id$ID<32>\nValue<u16>\n");
        var selected = tie.SelectFile("12.0.0.200", "AAAA0001");
        Assert.Equal(("ambiguous", "layout"), (selected.Status, selected.Match)); Assert.Equal(2, selected.Blocks.Count);
        Assert.Empty(tie.LayoutCandidates("12.0.0.200"));
        Assert.Equal(8, Assert.Single(tie.SelectFile("12.0.0.150", "AAAA0001").Blocks).Fields[1].IntegerBits);
        var same = Parse("COLUMNS\nint ID\nint Value\n\nLAYOUT AAAA0001\nBUILD 12.0.0.100\n$id$ID<32>\nValue<u8>\n\nLAYOUT AAAA0001\nBUILD 12.0.0.300\n$id$ID<32>\nValue<u8>\n");
        Assert.Equal("candidate", same.SelectFile("12.0.0.200", "AAAA0001").Status);
    }

    [Fact]
    public void Layout_candidates_agree_with_per_file_selection_at_the_block_limit()
    {
        var document = Parse(Lines);
        foreach (var (layout, block) in document.LayoutCandidates("12.1.5.70575"))
            Assert.Same(block, Assert.Single(document.SelectFile("12.1.5.70575", layout).Blocks));
        // 4,000 blocks with distinct layouts (near the line bound), as a fetched definition (#55) could carry.
        var source = new StringBuilder("COLUMNS\nint ID\n");
        for (var i = 0; i < 4000; i++) source.Append(System.Globalization.CultureInfo.InvariantCulture, $"\nLAYOUT {i:X8}\nBUILD 12.0.0.{i + 1}\n$id$ID<32>\n");
        var large = Parse(source.ToString());
        var candidates = large.LayoutCandidates("12.1.5.70575");
        Assert.Equal(4000, candidates.Count); Assert.Equal("00000F9F", candidates[^1].LayoutHash);
    }

    [Fact]
    public void An_unknown_layout_on_an_unlisted_build_fails_clearly()
    {
        var document = Parse(Lines);
        var unknown = document.SelectFile("12.1.5.70575", "DEADBEEF");
        Assert.Equal(("missing", "layout"), (unknown.Status, unknown.Match)); Assert.Empty(unknown.Blocks);
        Assert.Equal("DBD_INVALID", Assert.Throws<DiscoveryException>(() => document.SelectFile("12.1.5.70575", "XYZ")).Code);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => document.SelectFile("12.1.5.70575", "AAAA0001", cancellation.Token));
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
