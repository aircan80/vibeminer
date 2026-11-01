using System.Text;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class VersionsReplyTests
{
    private const string Header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), D = new('d', 32), E = new('e', 32);
    private static readonly ObjectKey Record = ObjectKey.Parse(ObjectKeyType.HistoryRecord, new string('1', 32));

    private static HistoryEntry Entry(HistoryKind kind = HistoryKind.Versions, string product = "wow") =>
        new(kind, product, "us", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "http://us.patch.example/wow/versions",
            ObjectKey.Parse(ObjectKeyType.HistoryDocument, new string('2', 32)), Record);
    private static string Row(string region, string cdn = "", string build = "", string id = "1000", string name = "1.0.0.1000") =>
        $"{region}|{(build == "" ? A : build)}|{(cdn == "" ? B : cdn)}|{C}|{id}|{name}|{D}";
    private static VersionsRead Read(string text, HistoryEntry? entry = null) =>
        VersionsReply.Read(entry ?? Entry(), Encoding.UTF8.GetBytes(text));
    private static string Reply(params string[] rows) => Header + "\n## seqn = 123\n" + string.Join("\n", rows) + "\n";

    [Fact]
    public void ReadsTwoRegionsInOrder()
    {
        var read = Read(Reply(Row("us"), Row("eu")));
        Assert.Empty(read.Problems);
        Assert.Equal("123", read.Sequence);
        Assert.Equal(["us", "eu"], read.Sightings.Select(s => s.Region));
        Assert.Equal([0, 1], read.Sightings.Select(s => s.Row));
        Assert.All(read.Sightings, s => Assert.Equal(B, s.CdnConfig));
        Assert.Equal("wow", read.Sightings[0].Identity.ProductId);
        Assert.Equal(A, read.Sightings[0].Identity.Fields["BuildConfig"]);
        Assert.False(read.Sightings[0].Identity.Fields.ContainsKey("Region"));
    }

    [Fact]
    public void SameFieldsInTwoRegionsShareAnId() =>
        Assert.Single(Read(Reply(Row("us"), Row("eu"))).Sightings.Select(s => s.Identity.Id).Distinct());

    [Fact]
    public void CdnConfigDoesNotChangeTheId()
    {
        var read = Read(Reply(Row("us", cdn: B), Row("eu", cdn: E)));
        Assert.Equal(read.Sightings[0].Identity.Id, read.Sightings[1].Identity.Id);
        Assert.Equal([B, E], read.Sightings.Select(s => s.CdnConfig));
    }

    [Fact]
    public void DifferentBuildsGetDifferentIds()
    {
        var read = Read(Reply(Row("us"), Row("eu", id: "1001", name: "1.0.0.1001")));
        Assert.NotEqual(read.Sightings[0].Identity.Id, read.Sightings[1].Identity.Id);
    }

    [Fact]
    public void UppercaseHexNormalizes()
    {
        var upper = Read(Reply(Row("us", cdn: B.ToUpperInvariant(), build: A.ToUpperInvariant()))).Sightings.Single();
        var lower = Read(Reply(Row("us"))).Sightings.Single();
        Assert.Equal(B, upper.CdnConfig);
        Assert.Equal(lower.Identity.Id, upper.Identity.Id);
    }

    [Fact]
    public void AcceptsCrlf()
    {
        var read = Read(Reply(Row("us"), Row("eu")).Replace("\n", "\r\n"));
        Assert.Empty(read.Problems);
        Assert.Equal("123", read.Sequence);
        Assert.Equal(2, read.Sightings.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a header\n")]
    [InlineData("Region!STRING:0|BuildConfig!HEX:16\nus\n")]
    [InlineData("Region!STRING:0\rus")]
    public void UnreadableBytesAreOneProblem(string text)
    {
        var read = Read(text);
        Assert.Empty(read.Sightings);
        Assert.Null(read.Sequence);
        var problem = Assert.Single(read.Problems);
        Assert.Equal("VERSIONS_UNREADABLE", problem.Code);
        Assert.Equal(Record, problem.Record);
    }

    [Fact]
    public void InvalidUtf8IsUnreadable() =>
        Assert.Equal("VERSIONS_UNREADABLE", Assert.Single(VersionsReply.Read(Entry(), new byte[] { 0xff, 0xfe, 0x00 }).Problems).Code);

    [Fact]
    public void MissingColumnIsOneProblem()
    {
        var read = Read("Region!STRING:0|BuildConfig!HEX:16|VersionsName!String:0\n## seqn = 7\nus|" + A + "|1.0\n");
        Assert.Empty(read.Sightings);
        Assert.Equal("7", read.Sequence);
        var problem = Assert.Single(read.Problems);
        Assert.Equal("VERSIONS_COLUMNS_MISSING", problem.Code);
        Assert.Contains("CDNConfig", problem.Message);
        Assert.Equal(Record, problem.Record);
    }

    [Fact]
    public void BadRowsAreProblemsAndGoodRowsStillCount()
    {
        var read = Read(Reply(Row("us"), Row("EU!"), Row("kr", cdn: "zz"), Row("tw", build: "short"), Row("cn")));
        Assert.Equal(["us", "cn"], read.Sightings.Select(s => s.Region));
        Assert.Equal([0, 4], read.Sightings.Select(s => s.Row));
        Assert.Equal([1, 2, 3], read.Problems.Select(p => p.Row));
        Assert.All(read.Problems, p => { Assert.Equal("VERSIONS_ROW_INVALID", p.Code); Assert.Equal(Record, p.Record); });
        Assert.Contains("INVALID_REGION", read.Problems[0].Message);
    }

    [Fact]
    public void DuplicateRegionKeepsNeither()
    {
        var read = Read(Reply(Row("us"), Row("eu"), Row("us", cdn: E), Row("us")));
        Assert.Equal("eu", Assert.Single(read.Sightings).Region);
        var problem = Assert.Single(read.Problems);
        Assert.Equal("VERSIONS_REGION_AMBIGUOUS", problem.Code);
        Assert.Equal(2, problem.Row);
        Assert.Equal(Record, problem.Record);
    }

    [Fact]
    public void AnAmbiguousRowThatIsAlsoMalformedGivesBothProblems()
    {
        var read = Read(Reply(Row("us"), Row("us", cdn: "zz")));
        Assert.Empty(read.Sightings);
        Assert.Equal([("VERSIONS_REGION_AMBIGUOUS", (int?)1), ("VERSIONS_ROW_INVALID", 1)], read.Problems.Select(p => (p.Code, p.Row)));
        Assert.True(read.Readable);
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("00", 0L)]
    [InlineData("-1", null)]
    [InlineData("1 ", null)]
    [InlineData("99999999999999999999", null)]
    [InlineData(null, null)]
    public void SequenceNumberIsThePlainDecimal(string? sequence, long? expected) =>
        Assert.Equal(expected, new VersionsRead([], [], sequence).SequenceNumber);

    [Fact]
    public void AWholeReplyProblemIsUnreadable()
    {
        Assert.False(Read("").Readable);
        Assert.False(Read(Reply(Row("us")), Entry(product: "d3")).Readable);
        Assert.True(Read(Reply(Row("us"), Row("EU!"))).Readable);
    }

    [Fact]
    public void UnknownProductIsOneProblem()
    {
        var read = Read(Reply(Row("us")), Entry(product: "d3"));
        Assert.Empty(read.Sightings);
        Assert.Equal("VERSIONS_UNKNOWN_PRODUCT", Assert.Single(read.Problems).Code);
    }

    [Fact]
    public void WrongKindIsOneProblem()
    {
        var read = Read(Reply(Row("us")), Entry(HistoryKind.Cdns));
        Assert.Empty(read.Sightings);
        Assert.Null(read.Sequence);
        Assert.Equal("VERSIONS_WRONG_KIND", Assert.Single(read.Problems).Code);
    }

    [Fact]
    public void NullEntryThrows() => Assert.Throws<ArgumentNullException>(() => VersionsReply.Read(null!, []));
}
