using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ParserIdentityTests
{
    [Fact]
    public void PreservesExactSourceFieldsDescriptorsSequenceAndOrdinals()
    {
        var text = Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu")).Replace("\n", "\r\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
        var parsed = MetadataParser.Parse(Fixtures.Utf8(text));
        Assert.Equal("00012", parsed.Sequence);
        Assert.Equal("String", parsed.Columns.Single(column => column.Name == "VersionsName").Type);
        Assert.Equal("", parsed.Rows[0].Fields["KeyRing"]);
        Assert.Equal("00123", parsed.Rows[0].Fields["BuildId"]);
        Assert.Equal(Fixtures.C, parsed.Rows[0].Fields["ProductConfig"]);
        Assert.Equal(1, parsed.Rows[1].Ordinal);
    }
    [Theory]
    [InlineData("A!STRING:0|A!STRING:0\nx|y")]
    [InlineData("A!STRING:0\nx|y")]
    [InlineData("A!STRING:0|B!STRING:0\nx")]
    [InlineData("A\nx")]
    [InlineData("A!STRING:0\nx\0")]
    [InlineData("A!STRING:0\rx")]
    [InlineData("## seqn = 001\nA!STRING:0\nx\n## seqn = 002")]
    public void RejectsMalformedSource(string source) => Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(Fixtures.Utf8(source)));
    [Fact]
    public void RejectsInvalidUtf8() => Assert.Throws<DiscoveryException>(() => MetadataParser.Parse([0xff, 0xfe]));
    [Fact]
    public void BoundsAllLinesIncludingHeadersAndComments()
    {
        Assert.Equal("METADATA_LIMIT", Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(Fixtures.Utf8(new string('A', MetadataParser.MaxLineChars + 1)))).Code);
        Assert.Equal("METADATA_LIMIT", Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(Fixtures.Utf8("#" + new string('a', MetadataParser.MaxLineChars)))).Code);
        Assert.Equal("METADATA_LIMIT", Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(new byte[MetadataParser.MaxBytes + 1])).Code);
    }
    [Fact]
    public void BoundsRowsAndColumns()
    {
        var tooManyRows = "A!STRING:0\n" + string.Concat(Enumerable.Repeat("x\n", MetadataParser.MaxRows + 1));
        Assert.Equal("METADATA_LIMIT", Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(Fixtures.Utf8(tooManyRows))).Code);
        var tooManyColumns = string.Join('|', Enumerable.Range(0, MetadataParser.MaxColumns + 1).Select(i => $"A{i}!STRING:0"));
        Assert.Equal("METADATA_LIMIT", Assert.Throws<DiscoveryException>(() => MetadataParser.Parse(Fixtures.Utf8(tooManyColumns))).Code);
    }
    [Fact]
    public void ParseObservesCancellation()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MetadataParser.Parse(Fixtures.Utf8(Fixtures.Versions(Fixtures.Row())), canceled.Token));
    }
    [Fact]
    public void IdentityNormalizesKnownHashesButPreservesEveryUnknownFieldAndOptionalPresence()
    {
        var first = Fixtures.Fields(); var second = Fixtures.Fields(Fixtures.A.ToUpperInvariant()); second["Region"] = "eu";
        Assert.Equal(BuildIdentity.Create("wow", first).Canonical, BuildIdentity.Create("wow", first).Canonical);
        Assert.Equal(BuildIdentity.Create("wow", first).Id, BuildIdentity.Create("wow", second).Id);
        foreach (var key in new[] { "BuildConfig", "CDNConfig", "VersionsName", "BuildId", "Opaque", "KeyRing", "ProductConfig" })
        {
            var changed = Fixtures.Fields(); changed[key] = key is "BuildConfig" or "CDNConfig" or "ProductConfig" ? Fixtures.C : "changed";
            Assert.NotEqual(BuildIdentity.Create("wow", first).Id, BuildIdentity.Create("wow", changed).Id);
        }
        var empty = Fixtures.Fields(); empty["ProductConfig"] = "";
        Assert.NotEqual(BuildIdentity.Create("wow", first).Id, BuildIdentity.Create("wow", empty).Id);
        Assert.NotEqual(BuildIdentity.Create("wow", first).Id, BuildIdentity.Create("wowt", first).Id);
    }
    [Fact]
    public void LengthFramedIdentityDoesNotAliasDelimiterValues()
    {
        var first = Fixtures.Fields(unknown: "a|b:c"); var second = Fixtures.Fields(unknown: "a"); second["b"] = "c";
        Assert.NotEqual(BuildIdentity.Create("wow", first).Id, BuildIdentity.Create("wow", second).Id);
        Assert.Throws<DiscoveryException>(() => BuildIdentity.Create("wow", Fixtures.Fields(config: "broken")));
    }
}
