using System.Text;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class TactConfigurationParserTests
{
    [Fact]
    public void RetainsUnknownFieldsEmptyValuesAndTokenOrder()
    {
        var document = TactConfigurationParser.Parse(Encoding.UTF8.GetBytes(" # build config\r\n\n encoding = first\t second \r\nunknown-9 =\nopaque = hash # literal\n"));
        Assert.Equal(["first", "second"], document.Fields["encoding"]);
        Assert.Empty(document.Fields["unknown-9"]);
        Assert.Equal(["hash", "#", "literal"], document.Fields["opaque"]);
        Assert.Equal(3, document.Fields.Count);
    }

    [Theory]
    [InlineData("a=b\na=c")]
    [InlineData("a")]
    [InlineData("a=b=c")]
    [InlineData("=b")]
    [InlineData("Upper=b")]
    [InlineData("-a=b")]
    [InlineData("a_b=c")]
    [InlineData("a=b\0")]
    [InlineData("# bad\u0001")]
    [InlineData("a=b\rnext=c")]
    [InlineData("a=b\r")]
    [InlineData("\ufeffa=b")]
    public void RejectsMalformedAssignmentsAndUnsupportedControls(string source) =>
        Assert.Equal("CONFIG_INVALID", Assert.Throws<DiscoveryException>(() => Parse(source)).Code);

    [Fact]
    public void RejectsInvalidUtf8() => Assert.Equal("CONFIG_INVALID",
        Assert.Throws<DiscoveryException>(() => TactConfigurationParser.Parse([0xff])).Code);

    [Fact]
    public void BoundsDocumentLinesFieldsAndValues()
    {
        AssertLimit(() => TactConfigurationParser.Parse(new byte[TactConfigurationParser.MaxBytes + 1]));
        AssertLimit(() => Parse(new string('\n', TactConfigurationParser.MaxLines + 1)));
        AssertLimit(() => Parse("#" + new string('x', TactConfigurationParser.MaxLineChars)));
        AssertLimit(() => Parse(string.Concat(Enumerable.Range(0, TactConfigurationParser.MaxFields + 1).Select(i => $"k{i}=\n"))));
        AssertLimit(() => Parse("a=" + string.Join(' ', Enumerable.Repeat("x", TactConfigurationParser.MaxValuesPerField + 1))));
    }

    [Fact]
    public void EmptyDocumentIsValidButStillObservesCancellation()
    {
        Assert.Empty(Parse("").Fields);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TactConfigurationParser.Parse([], canceled.Token));
    }

    private static TactConfiguration Parse(string source) => TactConfigurationParser.Parse(Encoding.UTF8.GetBytes(source));
    private static void AssertLimit(Action action) => Assert.Equal("CONFIG_LIMIT", Assert.Throws<DiscoveryException>(action).Code);
}
