using System.Text;
using VibeMiner.Formats.Generated;
using Xunit;

namespace VibeMiner.Formats.Tests.Generated;

/// <summary>What counts toward the definition a generated table is filed under (D350). Synthetic definitions only.</summary>
public sealed class ResolvedDefinitionTests
{
    private const string Head = "COLUMNS\nint ID\nint Value\nint<Other::ID> Ref\nstring Name\nlocstring Text\n\n";
    private const string Fields = "$noninline,id$ID<u32>\nValue<32>\nRef<u16>\nName\n";

    private static DbdBlock Block(string text, int index = 0) => DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(text)).Blocks[index];

    private static string Hash(string fields, string selectors = "LAYOUT 01020304\nBUILD 1.2.3.4\n", string head = Head) =>
        ResolvedDefinition.Hash(Block(head + selectors + fields));

    [Fact]
    public void Selectors_and_comments_dont_count()
    {
        var baseline = Hash(Fields);
        Assert.Matches("^[0-9a-f]{64}$", baseline);
        Assert.Equal(baseline, Hash(Fields, "LAYOUT 0A0B0C0D, 01020304\nBUILD 1.2.3.5\nBUILD 1.2.3.6-1.2.3.9\n"));
        Assert.Equal(baseline, Hash(Fields, "LAYOUT 01020304\nCOMMENT a block comment\n"));
        Assert.Equal(baseline, Hash("$noninline,id$ID<u32> // the key\nValue<32>\nRef<u16>\nName\n"));
        Assert.Equal(baseline, Hash(Fields, head: "COLUMNS\nint ID // a column comment\nint Value\nint<Other::ID> Ref\nstring Name\nlocstring Text\n\n"));
        // Two blocks of one document with the same fields share it.
        var two = Head + "LAYOUT 01020304\nBUILD 1.2.3.4\n" + Fields + "\nLAYOUT 01020304\nBUILD 1.2.3.5\n" + Fields;
        Assert.Equal(ResolvedDefinition.Hash(Block(two, 0)), ResolvedDefinition.Hash(Block(two, 1)));
    }

    [Fact]
    public void Every_field_attribute_that_shapes_rows_counts()
    {
        var baseline = Hash(Fields);
        var others = new[]
        {
            Hash("$noninline,id$ID<u32>\nRef<u16>\nValue<32>\nName\n"),
            Hash("$noninline,id$ID<u32>\nValue<16>\nRef<u16>\nName\n"),
            Hash("$noninline,id$ID<u32>\nValue<u32>\nRef<u16>\nName\n"),
            Hash("$noninline,id$ID<u32>\nValue<32>[2]\nRef<u16>\nName\n"),
            Hash("$id$ID<u32>\nValue<32>\nRef<u16>\nName\n"),
            Hash("$noninline,id$ID<u32>\nValue<32>\n$relation$Ref<u16>\nName\n"),
            Hash("$noninline,id$ID<u32>\nValue<32>\nRef<u16>\nText\n"),
            Hash(Fields, head: "COLUMNS\nint ID\nint Value\nint<Third::ID> Ref\nstring Name\nlocstring Text\n\n"),
            // An unverified int stays raw, and raw-integer rules and relations follow the mark.
            Hash(Fields, head: "COLUMNS\nint ID\nint Value?\nint<Other::ID> Ref\nstring Name\nlocstring Text\n\n"),
            Hash("$noninline,id$ID<u32>\nValue\nRef<u16>\nName\n", head: "COLUMNS\nint ID\nfloat Value\nint<Other::ID> Ref\nstring Name\nlocstring Text\n\n"),
        };
        Assert.Equal(others.Length + 1, others.Append(baseline).Distinct().Count());
    }

    [Fact]
    public void Only_a_locstring_field_makes_the_locale_count()
    {
        Assert.False(ResolvedDefinition.HasLocstrings(Block(Head + "LAYOUT 01020304\n" + Fields)));
        var localized = Block(Head + "LAYOUT 01020304\n" + Fields + "Text\n");
        Assert.True(ResolvedDefinition.HasLocstrings(localized));
        Assert.Equal("enUS", GeneratedTableSource.For(localized, "enUS", GeneratedFixtures.ContentKey, new Db2Limits()).Locale);
        Assert.Null(GeneratedTableSource.For(Block(Head + "LAYOUT 01020304\n" + Fields), "enUS", GeneratedFixtures.ContentKey, new Db2Limits()).Locale);
    }
}
