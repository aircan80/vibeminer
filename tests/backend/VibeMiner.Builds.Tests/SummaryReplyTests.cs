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

    // D476 (WoW review): a product named only by flagged rows has no versions document.
    [Fact]
    public void AProductWithOnlyFlaggedRowsHasNoVersionsDocument()
    {
        var read = Read($"{Header}\n## seqn = 9\nwow|1|\nwow|2|cdn\nwowdev4|3|cdn\nwowe3|4|\nwowe3|5|cdn\nwowx|6|bgdl\nwowx|7|cdn\n");
        Assert.Equal(["wow", "wowdev4", "wowe3", "wowx"], read.Products);
        Assert.Equal(["wowdev4", "wowx"], read.WithoutVersions);
        var refreshed = Summaries.Read(new VibeMiner.ObjectAccess.ObservationResult(VibeMiner.ObjectAccess.ObjectState.Present,
            new(VibeMiner.ObjectAccess.ObservationKind.Summary, "", "us", new Uri("https://us.version.battle.net/v2/summary"), DateTimeOffset.UnixEpoch,
                Encoding.UTF8.GetBytes($"{Header}\nwowdev4|3|cdn\nwow|1|\n")), Recorded: true));
        Assert.Equal(["wowdev4"], refreshed.WithoutVersions);
        // Without a Flags column the reply can't say, so no product is marked.
        var unflagged = Read("Product!STRING:0|Seqn!DEC:4\nwow|1\nwowdev4|3\n");
        Assert.True(unflagged.Readable);
        Assert.Equal(["wow", "wowdev4"], unflagged.Products);
        Assert.Empty(unflagged.WithoutVersions);
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
        Assert.Equal(14, Products.Reviewed.Count);
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

    // D476: any WoW product id is served, in layer 1's grammar.
    [Theory]
    [InlineData("wow", true)]
    [InlineData("wowdev2", true)]
    [InlineData("wow_classic_era_beta", true)]
    [InlineData("wow-x", true)]
    [InlineData("agent", false)]
    [InlineData("WOW", false)]
    [InlineData("wow.dev", false)]
    [InlineData("wow/../x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWowTakesLayerOnesGrammarWithAWowPrefix(string? product, bool wow) => Assert.Equal(wow, Products.IsWow(product));

    [Fact]
    public void IsWowBoundsTheIdAtLayerOnesSixtyFourBytes()
    {
        Assert.True(Products.IsWow("wow" + new string('a', 61)));
        Assert.False(Products.IsWow("wow" + new string('a', 62)));
        Assert.All(Products.Ids, id => Assert.True(Products.IsWow(id), id));
    }

    [Fact]
    public void ListedPutsReviewedProductsFirstThenTheOthersInNaturalOrder()
    {
        var listed = Products.Listed(["wownev10", "wownev2", "wowdev", "wow", "wow_classic_era_beta", "agent", "WOWX", "wownev2", "wownev1"]);
        Assert.Equal(Products.Ids, listed.Take(Products.Ids.Count).Select(p => p.Id));
        Assert.All(listed.Take(Products.Ids.Count), p => Assert.True(p.Reviewed));
        Assert.Equal(["wowdev", "wownev1", "wownev2", "wownev10"], listed.Skip(Products.Ids.Count).Select(p => p.Id));
        Assert.All(listed.Skip(Products.Ids.Count), p => Assert.Equal((p.Id, false), (p.Label, p.Reviewed)));
        Assert.Equal(Products.Ids.Count, Products.Listed([]).Count);
        Assert.Equal(Products.MaxUnreviewed, Products.Listed(Enumerable.Range(0, 100).Select(i => $"wowx{i}")).Count - Products.Ids.Count);
    }

    [Fact]
    public void NaturalOrderComparesDigitRunsByValueAndStaysTotal()
    {
        string[] ids = ["wowv10", "wowv", "wowv2", "wowv02", "wowe1", "wowdev", "wowv9"];
        Assert.Equal(["wowdev", "wowe1", "wowv", "wowv2", "wowv02", "wowv9", "wowv10"], ids.Order(Products.NaturalOrder.Instance));
        Assert.Equal(0, Products.NaturalOrder.Instance.Compare("wowv2", "wowv2"));
        Assert.True(Products.NaturalOrder.Instance.Compare("wowv2", "wowv02") < 0);
    }

    [Fact]
    public void NameIsTheLabelOrTheId()
    {
        Assert.Equal("Forever", Products.Name("wowf"));
        Assert.Equal("wowdev", Products.Name("wowdev"));
    }
}
