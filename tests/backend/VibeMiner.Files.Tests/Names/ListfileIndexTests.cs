using System.Text;
using VibeMiner.Files.Names;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>
/// The listfile parser's rules (D105, ported from the legacy ListfileCsvParser) and the in-memory names index, over
/// authored listfiles only.
/// </summary>
public sealed class ListfileIndexTests
{
    private const string Sample = "1;Interface/Icons/Alpha.blp\n5;world/maps/Azeroth/Azeroth.wdt\n900;Sound/Music/Theme.mp3\n";

    private static ListfileIndex Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    private static ListfileIndex Parse(byte[] bytes, ListfileParser? parser = null)
    {
        parser ??= new ListfileParser();
        parser.Feed(bytes);
        return parser.Complete();
    }

    private static ListfileFormatException Refused(string text, ListfileParser? parser = null) =>
        Refused(Encoding.UTF8.GetBytes(text), parser);

    private static ListfileFormatException Refused(byte[] bytes, ListfileParser? parser = null) =>
        Assert.Throws<ListfileFormatException>(() => Parse(bytes, parser));

    [Fact]
    public void ParsesAscendingLines()
    {
        var index = Parse(Sample);
        Assert.Equal(3, index.Count);
        Assert.Equal("Interface/Icons/Alpha.blp", index.NameOf(1));
        Assert.Equal("world/maps/Azeroth/Azeroth.wdt", index.NameOf(5));
        Assert.Equal("Sound/Music/Theme.mp3", index.NameOf(900));
        Assert.Null(index.NameOf(0));
        Assert.Null(index.NameOf(2));
        Assert.Null(index.NameOf(uint.MaxValue));
    }

    [Fact]
    public void AcceptsTheLargestFileDataId() => Assert.Equal("a", Parse("4294967295;a").NameOf(uint.MaxValue));

    [Theory]
    [InlineData("5;a\n3;b\n", "isn’t in FileDataID order")]
    [InlineData("5;a\n5;b\n", "isn’t in FileDataID order")]
    [InlineData("1;\n", "name of 0 bytes")]
    [InlineData("01;a\n", "leading zero")]
    [InlineData("4294967296;a\n", "above 4294967295")]
    [InlineData(";a\n", "doesn’t start with a FileDataID")]
    [InlineData("1a;b\n", "doesn’t start with a FileDataID")]
    [InlineData("-1;a\n", "doesn’t start with a FileDataID")]
    [InlineData("12345678901;a\n", "doesn’t start with a FileDataID")]
    [InlineData("1 a\n", "doesn’t start with a FileDataID")]
    [InlineData("1;a\tb\n", "control character")]
    [InlineData("1;a\u007fb\n", "control character")]
    [InlineData("1;a\rb\n", "control character")]
    [InlineData("1;a\u0080b\n", "control character")]
    [InlineData("1;a\u0085b\n", "control character")]
    [InlineData("1;a\u009Fb\n", "control character")]
    [InlineData("", "no names")]
    [InlineData("\n\r\n\n", "no names")]
    public void RefusesBadLines(string text, string detail) => Assert.Contains(detail, Refused(text).Message);

    [Fact]
    public void ReportsTheLineNumber() => Assert.Contains("Line 3", Refused("1;a\n\n2;\n").Message);

    [Fact]
    public void NamesAreOneToMaxNameBytes()
    {
        var longest = new string('x', ListfileParser.MaxNameBytes);
        Assert.Equal(longest, Parse($"1;{longest}\n").NameOf(1));
        Assert.Contains("1025 bytes", Refused($"1;{longest}x\n").Message);
    }

    [Fact]
    public void RefusesALineLongerThanTheLimitAcrossChunks()
    {
        var parser = new ListfileParser();
        parser.Feed("1;"u8);
        var error = Assert.Throws<ListfileFormatException>(() => parser.Feed(new byte[ListfileParser.MaxLineBytes]));
        Assert.Contains("longer than", error.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0x31, 0x3b, 0x61, 0xff, 0x0a })]               // 0xFF is never UTF-8
    [InlineData(new byte[] { 0x31, 0x3b, 0xc3, 0x0a })]                     // truncated sequence
    [InlineData(new byte[] { 0x31, 0x3b, 0xc0, 0xaf, 0x0a })]               // overlong '/'
    [InlineData(new byte[] { 0x31, 0x3b, 0xed, 0xa0, 0x80, 0x0a })]         // surrogate
    public void RefusesInvalidUtf8(byte[] bytes) => Assert.Contains("valid UTF-8", Refused(bytes).Message);

    [Fact]
    public void KeepsTheCharactersNextToC1Controls() => Assert.Equal("a\u00A0b\u007Ec\u0100", Parse("1;a\u00A0b\u007Ec\u0100\n").NameOf(1));

    [Fact]
    public void KeepsValidMultibyteNames() => Assert.Equal("café/ü€😀.blp", Parse("1;café/ü€😀.blp\n").NameOf(1));

    [Fact]
    public void RemovesBidiFormattingCharacters()
    {
        string[] marks = ["؜", "‎", "‏", "‪", "‫", "‬", "‭", "‮", "⁦", "⁧", "⁨", "⁩"];
        var index = Parse($"1;a{string.Concat(marks)}b.blp\n2;‮cd‎\n");
        Assert.Equal("ab.blp", index.NameOf(1));
        Assert.Equal("cd", index.NameOf(2));
        Assert.Equal(new uint[] { 1u }, index.IdsOf("AB.BLP"));
        // Neighbours that aren't bidi marks stay.
        Assert.Equal("‍⁥ ⁪", Parse("1;‍⁥ ⁪\n").NameOf(1));
    }

    [Fact]
    public void RefusesANameMadeOnlyOfBidiMarks() => Assert.Contains("only of direction marks", Refused("1;‎‮\n").Message);

    [Theory]
    [InlineData("1;a\n2;b\n")]
    [InlineData("1;a\r\n2;b\r\n")]
    [InlineData("1;a\r\n2;b\n")]
    [InlineData("1;a\n2;b")]
    [InlineData("1;a\r\n2;b\r")]
    [InlineData("\n1;a\n\r\n\n2;b\n\n")]
    public void HandlesLineEndingsAndBlankLines(string text)
    {
        var index = Parse(text);
        Assert.Equal(2, index.Count);
        Assert.Equal("a", index.NameOf(1));
        Assert.Equal("b", index.NameOf(2));
    }

    [Fact]
    public void CapsTheNumberOfNames()
    {
        Assert.Equal(2, Parse(Encoding.UTF8.GetBytes("1;a\n2;b\n"), new ListfileParser(2, 1024)).Count);
        Assert.Contains("more than 2 names", Refused("1;a\n2;b\n3;c\n", new ListfileParser(2, 1024)).Message);
    }

    [Fact]
    public void CapsTheNameBytes()
    {
        Assert.Equal(2, Parse(Encoding.UTF8.GetBytes("1;abc\n2;de\n"), new ListfileParser(10, 5)).Count);
        Assert.Contains("larger than", Refused("1;abc\n2;def\n", new ListfileParser(10, 5)).Message);
    }

    [Fact]
    public void GrowsPastItsInitialStorage()
    {
        var text = new StringBuilder();
        for (var id = 0; id < 5000; id++) text.Append(id * 3).Append(";dir/").Append(new string('n', 40)).Append(id).Append('\n');
        var index = Parse(text.ToString());
        Assert.Equal(5000, index.Count);
        Assert.Equal($"dir/{new string('n', 40)}4999", index.NameOf(14997));
        Assert.Equal(new uint[] { 4999u * 3 }, index.IdsOf($"DIR\\{new string('N', 40)}4999"));
    }

    [Fact]
    public void ParsesTheSameWhenFedOneByteAtATime()
    {
        var bytes = Encoding.UTF8.GetBytes("1;a‎é\r\n\n7;Dir/B.blp\r\n4000000000;" + new string('z', ListfileParser.MaxNameBytes));
        var parser = new ListfileParser();
        foreach (var value in bytes) parser.Feed([value]);
        var index = parser.Complete();
        Assert.Equal(3, parser.Count);
        Assert.Equal("aé", index.NameOf(1));
        Assert.Equal("Dir/B.blp", index.NameOf(7));
        Assert.Equal(new string('z', ListfileParser.MaxNameBytes), index.NameOf(4_000_000_000));
    }

    [Fact]
    public void ParsesTheSameAtEverySplitPoint()
    {
        var bytes = Encoding.UTF8.GetBytes(Sample + "901;x\r\n");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var parser = new ListfileParser();
            parser.Feed(bytes.AsSpan(0, split));
            parser.Feed(bytes.AsSpan(split));
            var index = parser.Complete();
            Assert.Equal(4, index.Count);
            Assert.Equal("world/maps/Azeroth/Azeroth.wdt", index.NameOf(5));
            Assert.Equal("x", index.NameOf(901));
        }
    }

    [Fact]
    public void RefusesFeedAfterCompleteAndCompleteReturnsTheSameIndex()
    {
        var parser = new ListfileParser();
        parser.Feed("1;a\n"u8);
        var index = parser.Complete();
        Assert.Same(index, parser.Complete());
        Assert.Throws<InvalidOperationException>(() => parser.Feed("2;b\n"u8));
    }

    [Fact]
    public void ARefusalFailsTheParserForGood()
    {
        var parser = new ListfileParser();
        parser.Feed("1;a\n2;"u8);
        Assert.Throws<ListfileFormatException>(() => parser.Complete());
        Assert.Throws<InvalidOperationException>(() => parser.Complete());
        Assert.Throws<InvalidOperationException>(() => parser.Feed("3;c\n"u8));
    }

    [Fact]
    public void ARefusalInFeedFailsTheParser()
    {
        var parser = new ListfileParser();
        Assert.Throws<ListfileFormatException>(() => parser.Feed("2;a\n1;b\n"u8));
        Assert.Throws<InvalidOperationException>(() => parser.Feed("3;c\n"u8));
        Assert.Throws<InvalidOperationException>(() => parser.Complete());
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(1L << 22)]
    public void TheSizeHintNeverChangesTheIndex(long expectedBytes)
    {
        var text = new StringBuilder();
        for (var id = 1; id <= 3000; id++) text.Append(id).Append(";dir/file").Append(id).Append(".blp\n");
        var index = Parse(Encoding.UTF8.GetBytes(text.ToString()), new ListfileParser(expectedBytes));
        Assert.Equal(3000, index.Count);
        Assert.Equal("dir/file1.blp", index.NameOf(1));
        Assert.Equal("dir/file3000.blp", index.NameOf(3000));
        Assert.Equal(new uint[] { 2999u }, index.IdsOf("DIR/FILE2999.BLP"));
        Assert.Equal(3000, index.From(0).Count());
    }

    [Fact]
    public void TheNameBytesCapCountsNamesAfterBidiMarksAreRemoved() =>
        Assert.Equal("abc", Parse(Encoding.UTF8.GetBytes("1;a\u200Eb\u200Fc\n"), new ListfileParser(10, 3)).NameOf(1));

    [Fact]
    public void IdsOfIgnoresCaseAndSlashDirection()
    {
        var index = Parse(Sample);
        Assert.Equal(new uint[] { 1u }, index.IdsOf("Interface/Icons/Alpha.blp"));
        Assert.Equal(new uint[] { 1u }, index.IdsOf("interface\\icons\\alpha.BLP"));
        Assert.Equal(new uint[] { 5u }, index.IdsOf("WORLD\\MAPS/AZEROTH\\AZEROTH.WDT"));
        Assert.Equal(new uint[] { 900u }, index.IdsOf("sound/music/theme.mp3"));
        Assert.Empty(index.IdsOf("sound/music/theme.mp"));
        Assert.Empty(index.IdsOf("sound/music/theme.mp3 "));
        Assert.Empty(index.IdsOf(""));
        Assert.Empty(index.IdsOf("\uD800"));
        Assert.Empty(index.IdsOf(new string('a', ListfileParser.MaxNameBytes + 1)));
    }

    [Fact]
    public void IdsOfFoldsOnlyAsciiLetters()
    {
        var index = Parse("1;Café.blp\n");
        Assert.Equal(new uint[] { 1u }, index.IdsOf("CAFé.BLP"));
        Assert.Empty(index.IdsOf("CAFÉ.BLP"));
    }

    [Fact]
    public void IdsOfReturnsEveryIdOfARepeatedNameAscending()
    {
        var index = Parse("3;Dir/Same.blp\n4;other\n8;dir\\SAME.blp\n9;DIR/same.BLP\n");
        Assert.Equal(new uint[] { 3u, 8, 9 }, index.IdsOf("dir/same.blp"));
        Assert.Equal("dir\\SAME.blp", index.NameOf(8));
    }

    [Fact]
    public void HashCollisionsFallBackToComparingNames()
    {
        // Every name hashes the same, so each lookup has to compare names.
        var parser = new ListfileParser(100, 1 << 16, _ => 42);
        var index = Parse(Encoding.UTF8.GetBytes("2;b.blp\n3;a.blp\n10;A.BLP\n11;c.blp\n"), parser);
        Assert.Equal(new uint[] { 3u, 10 }, index.IdsOf("a.blp"));
        Assert.Equal(new uint[] { 2u }, index.IdsOf("B.blp"));
        Assert.Equal(new uint[] { 11u }, index.IdsOf("c.blp"));
        Assert.Empty(index.IdsOf("d.blp"));
    }

    [Fact]
    public void TheDefaultHashIgnoresCaseAndSlashDirection()
    {
        Assert.Equal(ListfileIndex.Hash("a/B.blp"u8), ListfileIndex.Hash("A\\b.BLP"u8));
        Assert.NotEqual(ListfileIndex.Hash("a/b.blp"u8), ListfileIndex.Hash("a/c.blp"u8));
    }

    [Fact]
    public void FromListsAscendingFromAnId()
    {
        var index = Parse(Sample);
        Assert.Equal([(1u, "Interface/Icons/Alpha.blp"), (5u, "world/maps/Azeroth/Azeroth.wdt"), (900u, "Sound/Music/Theme.mp3")], index.From(0));
        Assert.Equal([(5u, "world/maps/Azeroth/Azeroth.wdt"), (900u, "Sound/Music/Theme.mp3")], index.From(5));
        Assert.Equal([(900u, "Sound/Music/Theme.mp3")], index.From(6));
        Assert.Empty(index.From(901));
        Assert.Empty(index.From(uint.MaxValue));
    }

    [Fact]
    public void HasPrefixIgnoresAsciiCaseAndSlashDirection()
    {
        Assert.True(ListfileIndex.HasPrefix("Sound/Music/Theme.mp3", "SOUND\\music/"));
        Assert.True(ListfileIndex.HasPrefix("Sound/Music/Theme.mp3", ""));
        Assert.True(ListfileIndex.HasPrefix("a.blp", "A.BLP"));
        Assert.False(ListfileIndex.HasPrefix("a.blp", "a.blp2"));
        Assert.False(ListfileIndex.HasPrefix("Café/x", "CAFÉ"));
        Assert.False(ListfileIndex.HasPrefix("sound/x", "sounds"));
    }
}
