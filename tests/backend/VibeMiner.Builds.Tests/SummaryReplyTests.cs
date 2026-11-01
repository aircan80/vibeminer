using System.Text;
using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class SummaryReplyTests
{
    private const string Header = "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0";

    private static SummaryRead Read(string text) => SummaryReply.Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ListsEachProductOnceSortedWhateverItsFlags()
    {
        var read = Read($"{Header}\n## seqn = 77\nwowt|5|\nwow|3|cdn\nwow|4|\nagent|1|bgdl\nwow|2|bgdl\n");
        Assert.True(read.Readable);
        Assert.Empty(read.Problems);
        Assert.Equal("77", read.Sequence);
        Assert.Equal(["agent", "wow", "wowt"], read.Products);
    }

    [Fact]
    public void ABadRowIsAProblemAndTheOthersStillCount()
    {
        var read = Read($"{Header}\nWoW Beta|1|\n|2|\nwow_beta|3|\n");
        Assert.True(read.Readable);
        Assert.Equal(["wow_beta"], read.Products);
        Assert.Equal([("SUMMARY_ROW_INVALID", (int?)0), ("SUMMARY_ROW_INVALID", 1)], read.Problems.Select(p => (p.Code, p.Row)));
    }

    [Fact]
    public void ManyBadRowsAreCapped()
    {
        var read = Read(Header + "\n" + string.Concat(Enumerable.Range(0, Catalog.MaxListProblems + 3).Select(i => $"BAD{i}|1|\n")));
        Assert.Equal(Catalog.MaxListProblems + 1, read.Problems.Count);
        Assert.Equal(("CATALOG_PROBLEMS_TRUNCATED", (int?)null), (read.Problems[^1].Code, read.Problems[^1].Row));
        Assert.Contains("3 more", read.Problems[^1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Name!STRING:0|Seqn!DEC:4\nwow|1\n", "SUMMARY_COLUMNS_MISSING")]
    [InlineData("not a table\n", "SUMMARY_UNREADABLE")]
    [InlineData("", "SUMMARY_UNREADABLE")]
    [InlineData("Product!STRING:0|Seqn!DEC:4\nwow\n", "SUMMARY_UNREADABLE")]
    public void AnUnreadableReplyListsNothing(string text, string code)
    {
        var read = Read(text);
        Assert.False(read.Readable);
        Assert.Empty(read.Products);
        Assert.Equal(code, Assert.Single(read.Problems).Code);
    }

    [Fact]
    public void ReviewedProductsCarryTheRegistryNames()
    {
        Assert.Equal(13, Products.Reviewed.Count);
        Assert.Equal(Products.Reviewed.Select(p => p.Id), Products.Ids);
        Assert.Equal("Retail", Products.Label("wow"));
        Assert.Equal("Classic Era PTR", Products.Label("wow_classic_era_ptr"));
        // The two PTRs read differently (D165).
        Assert.Equal("PTR", Products.Label("wowt"));
        Assert.Equal("PTR 2", Products.Label("wowxptr"));
        Assert.All(Products.Reviewed, p => Assert.False(string.IsNullOrWhiteSpace(p.Label)));
        Assert.Throws<ArgumentException>(() => Products.Label("wowdev"));
        Assert.True(Products.LooksLikeWow("wowdev"));
        Assert.True(Products.LooksLikeWow("wow_classic_era_beta"));
        Assert.False(Products.LooksLikeWow("agent"));
        Assert.False(Products.LooksLikeWow("fenris"));
    }
}
