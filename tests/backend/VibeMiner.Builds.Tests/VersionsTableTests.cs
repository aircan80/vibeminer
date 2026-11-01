using System.Text;
using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class VersionsTableTests
{
    private static VersionsTable Parse(string text) => VersionsTable.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ReadsColumnsRowsAndSequence()
    {
        var table = Parse("Region!STRING:0|BuildId!DEC:4\r\n## seqn = 42\n# other comment\n\nus|1\neu|2\n## seqn = 42\n");
        Assert.Equal(["Region", "BuildId"], table.Columns);
        Assert.Equal("42", table.Sequence);
        Assert.Equal([0, 1], table.Rows.Select(r => r.Ordinal));
        Assert.Equal("2", table.Rows[1].Fields["BuildId"]);
    }

    [Theory]
    [InlineData("Region|BuildId!DEC:4\nus|1\n")]
    [InlineData("Region!STRING:0|Region!STRING:0\nus|us\n")]
    [InlineData("Region!STRING:0|BuildId!DEC:4\nus\n")]
    [InlineData("Region!STRING:0\n## seqn = 1\n## seqn = 2\n")]
    [InlineData("Region!STRING:0\rus\n")]
    [InlineData("Region!STRING:0\nu\0s\n")]
    [InlineData("## seqn = 1\n")]
    public void MalformedTablesThrow(string text) => Assert.Throws<FormatException>(() => Parse(text));

    [Fact]
    public void InvalidUtf8Throws() => Assert.Throws<FormatException>(() => VersionsTable.Parse(new byte[] { 0x41, 0xff, 0x0a }));

    [Fact]
    public void OverSizeThrows()
    {
        Assert.Throws<FormatException>(() => VersionsTable.Parse(new byte[VersionsTable.MaxBytes + 1]));
        Assert.Throws<FormatException>(() => Parse("A!STRING:0\n" + new string('x', VersionsTable.MaxLineChars + 1) + "\n"));
        Assert.Throws<FormatException>(() => Parse(string.Join("|", Enumerable.Range(0, VersionsTable.MaxColumns + 1).Select(i => $"C{i}!DEC:4"))));
        Assert.Throws<FormatException>(() => Parse("A!DEC:4\n" + string.Concat(Enumerable.Repeat("1\n", VersionsTable.MaxRows + 1))));
        Assert.Equal(VersionsTable.MaxRows, Parse("A!DEC:4\n" + string.Concat(Enumerable.Repeat("1\n", VersionsTable.MaxRows))).Rows.Count);
    }
}
