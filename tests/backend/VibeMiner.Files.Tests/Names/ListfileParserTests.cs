using System.Text;
using VibeMiner.Files.Names;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>
/// The listfile parser's rules (D105, ported from the legacy ListfileCsvParser) and the name folding rules, over authored
/// listfiles only. Lookups on the persisted index are in <see cref="ListfileStoreTests"/>.
/// </summary>
public sealed class ListfileParserTests
{
    /// <summary>What the parser sent to its sink.</summary>
    private sealed class Parsed
    {
        public SortedDictionary<uint, string> Names { get; } = [];
        public long Count { get; set; }
        public string? NameOf(uint id) => Names.TryGetValue(id, out var name) ? name : null;
        public void Add(uint id, ReadOnlySpan<byte> name)
        {
            Assert.True(Names.Count == 0 || Names.Keys.Last() < id);
            Names.Add(id, Encoding.UTF8.GetString(name));
        }
    }

    private static ListfileParser Parser(Parsed into, int maxNames = ListfileParser.MaxNames, int maxBlob = ListfileParser.MaxBlobBytes) =>
        new(into.Add, maxNames, maxBlob);
    private const string Sample = "1;Interface/Icons/Alpha.blp\n5;world/maps/Azeroth/Azeroth.wdt\n900;Sound/Music/Theme.mp3\n";

    private static Parsed Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    private static Parsed Parse(byte[] bytes, int maxNames = ListfileParser.MaxNames, int maxBlob = ListfileParser.MaxBlobBytes)
    {
        var parsed = new Parsed();
        var parser = Parser(parsed, maxNames, maxBlob);
        parser.Feed(bytes);
        parsed.Count = parser.Finish();
        Assert.Equal(parsed.Names.Count, parsed.Count);
        return parsed;
    }

    private static ListfileFormatException Refused(string text, int maxNames = ListfileParser.MaxNames, int maxBlob = ListfileParser.MaxBlobBytes) =>
        Refused(Encoding.UTF8.GetBytes(text), maxNames, maxBlob);

    private static ListfileFormatException Refused(byte[] bytes, int maxNames = ListfileParser.MaxNames, int maxBlob = ListfileParser.MaxBlobBytes) =>
        Assert.Throws<ListfileFormatException>(() => Parse(bytes, maxNames, maxBlob));

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
        var parser = Parser(new Parsed());
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
        Assert.Equal(2, Parse(Encoding.UTF8.GetBytes("1;a\n2;b\n"), 2, 1024).Count);
        Assert.Contains("more than 2 names", Refused("1;a\n2;b\n3;c\n", 2, 1024).Message);
    }

    [Fact]
    public void CapsTheNameBytes()
    {
        Assert.Equal(2, Parse(Encoding.UTF8.GetBytes("1;abc\n2;de\n"), 10, 5).Count);
        Assert.Contains("larger than", Refused("1;abc\n2;def\n", 10, 5).Message);
    }

    [Fact]
    public void ParsesManyNames()
    {
        var text = new StringBuilder();
        for (var id = 0; id < 5000; id++) text.Append(id * 3).Append(";dir/").Append(new string('n', 40)).Append(id).Append('\n');
        var index = Parse(text.ToString());
        Assert.Equal(5000, index.Count);
        Assert.Equal($"dir/{new string('n', 40)}4999", index.NameOf(14997));
    }

    [Fact]
    public void ParsesTheSameWhenFedOneByteAtATime()
    {
        var bytes = Encoding.UTF8.GetBytes("1;a‎é\r\n\n7;Dir/B.blp\r\n4000000000;" + new string('z', ListfileParser.MaxNameBytes));
        var index = new Parsed();
        var parser = Parser(index);
        foreach (var value in bytes) parser.Feed([value]);
        Assert.Equal(3, parser.Finish());
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
            var index = new Parsed();
            var parser = Parser(index);
            parser.Feed(bytes.AsSpan(0, split));
            parser.Feed(bytes.AsSpan(split));
            Assert.Equal(4, parser.Finish());
            Assert.Equal("world/maps/Azeroth/Azeroth.wdt", index.NameOf(5));
            Assert.Equal("x", index.NameOf(901));
        }
    }

    [Fact]
    public void RefusesFeedAfterFinishAndFinishReturnsTheSameCount()
    {
        var parser = Parser(new Parsed());
        parser.Feed("1;a\n"u8);
        Assert.Equal(1, parser.Finish());
        Assert.Equal(1, parser.Finish());
        Assert.Throws<InvalidOperationException>(() => parser.Feed("2;b\n"u8));
    }

    [Fact]
    public void ARefusalFailsTheParserForGood()
    {
        var parser = Parser(new Parsed());
        parser.Feed("1;a\n2;"u8);
        Assert.Throws<ListfileFormatException>(() => parser.Finish());
        Assert.Throws<InvalidOperationException>(() => parser.Finish());
        Assert.Throws<InvalidOperationException>(() => parser.Feed("3;c\n"u8));
    }

    [Fact]
    public void ARefusalInFeedFailsTheParser()
    {
        var parser = Parser(new Parsed());
        Assert.Throws<ListfileFormatException>(() => parser.Feed("2;a\n1;b\n"u8));
        Assert.Throws<InvalidOperationException>(() => parser.Feed("3;c\n"u8));
        Assert.Throws<InvalidOperationException>(() => parser.Finish());
    }

    [Fact]
    public void TheNameBytesCapCountsNamesAfterBidiMarksAreRemoved() =>
        Assert.Equal("abc", Parse(Encoding.UTF8.GetBytes("1;a\u200Eb\u200Fc\n"), 10, 3).NameOf(1));

    [Fact]
    public void SameNameFoldsOnlyAsciiLettersAndSlashes()
    {
        Assert.True(NameFolding.SameName("Interface/Icons/Alpha.blp"u8, "interface\\icons\\alpha.BLP"u8));
        Assert.True(NameFolding.SameName("Café.blp"u8, "CAFé.BLP"u8));
        Assert.False(NameFolding.SameName("Café.blp"u8, "CAFÉ.BLP"u8));
        Assert.False(NameFolding.SameName("a.mp3"u8, "a.mp3 "u8));
    }

    [Fact]
    public void HasPrefixIgnoresAsciiCaseAndSlashDirection()
    {
        Assert.True(NameFolding.HasPrefix("Sound/Music/Theme.mp3", "SOUND\\music/"));
        Assert.True(NameFolding.HasPrefix("Sound/Music/Theme.mp3", ""));
        Assert.True(NameFolding.HasPrefix("a.blp", "A.BLP"));
        Assert.False(NameFolding.HasPrefix("a.blp", "a.blp2"));
        Assert.False(NameFolding.HasPrefix("Café/x", "CAFÉ"));
        Assert.False(NameFolding.HasPrefix("sound/x", "sounds"));
    }
}
