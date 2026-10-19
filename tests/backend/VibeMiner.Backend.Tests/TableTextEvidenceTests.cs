using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableTextEvidenceTests
{
    private static readonly TableDataColumn[] Columns = [new("ID", "integer", true, null), new("Description", "string", false, null), new("Name", "string", false, null)];
    private static TableTextIssue Issue(int column = 1, string raw = "61ff62") => new(column, TableTextEvidence.InvalidUtf8, raw);

    [Fact]
    public void Malformed_text_preserves_exact_bytes_without_changing_valid_text_or_empty_strings()
    {
        Assert.Equal(3, TableTextEvidence.Validate([Issue()], Columns, ["1", "", "A valid name"]));
        Assert.Equal(0, TableTextEvidence.Validate(null, Columns, ["1", "", "A valid name"]));
        Assert.Equal(2, TableTextEvidence.Validate([Issue(1, "ff"), Issue(2, "c3")], Columns, ["1", "", ""]));
    }

    [Theory]
    [InlineData("validUtf8")][InlineData("uppercase")][InlineData("odd")][InlineData("empty")]
    [InlineData("nul")][InlineData("index")][InlineData("integer")][InlineData("duplicate")]
    [InlineData("order")][InlineData("value")][InlineData("code")][InlineData("budget")]
    public void Forged_or_ambiguous_text_evidence_is_rejected(string problem)
    {
        TableTextIssue[] issues = [Issue()]; string[] values = ["1", "", ""];
        issues = problem switch
        {
            "validUtf8" => [Issue(raw: "6162")], "uppercase" => [Issue(raw: "FF")], "odd" => [Issue(raw: "ff0")],
            "empty" => [Issue(raw: "")], "nul" => [Issue(raw: "ff00")], "index" => [Issue(3)],
            "integer" => [Issue(0)], "duplicate" => [Issue(), Issue()], "order" => [Issue(2), Issue(1)],
            "code" => [Issue() with { Code = "UNKNOWN" }],
            "budget" => [Issue(raw: new('f', TableTextEvidence.MaximumRawCellBytes * 2 + 2))], _ => issues
        };
        if (problem == "value") values[1] = "guessed text";
        Assert.Equal("TABLE_TEXT_EVIDENCE_INVALID", Assert.Throws<DiscoveryException>(() => TableTextEvidence.Validate(issues, Columns, values)).Code);
    }
}
