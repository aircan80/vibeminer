using Xunit;

namespace VibeMiner.Builds.Tests;

public sealed class BuildIdentityTests
{
    // Expected values were computed once with the engine's BuildIdentity (VibeMiner.Domain) for the same inputs.
    [Fact]
    public void CreateMatchesTheEngineIdentity()
    {
        var id = BuildIdentity.Create("wow", new Dictionary<string, string>
        {
            ["Region"] = "us", ["BuildConfig"] = new('A', 32), ["CDNConfig"] = new('b', 32), ["KeyRing"] = "",
            ["BuildId"] = "1000", ["VersionsName"] = "1.0.0.1000", ["ProductConfig"] = new('D', 32),
        });
        Assert.Equal("1f154ffdac5b9bc96002d6bf48a9959da1b30bdeaa0029ad87bc702715378c77", id.Id);
        Assert.Equal("27:vibeminer-build-identity-v23:wow9:Published11:BuildConfig32:" + new string('a', 32) + "7:BuildId4:1000"
            + "7:KeyRing0:13:ProductConfig32:" + new string('d', 32) + "12:VersionsName10:1.0.0.1000", id.Canonical);
    }

    [Fact]
    public void FromFieldsMatchesTheEngineIdentity()
    {
        var id = BuildIdentity.FromFields("wow_classic_era", new Dictionary<string, string>
        {
            ["BuildConfig"] = new('e', 32), ["VersionsName"] = "1.15.7.61582 é", ["BuildId"] = "61582", ["ProductConfig"] = "",
        });
        Assert.Equal("ffa49e6c9d4ce8d072d24fb0ee62d6a0572d349f0366ff7bfbd564f2e62bbc2b", id.Id);
        Assert.EndsWith("12:VersionsName15:1.15.7.61582 é", id.Canonical);
    }

    [Theory]
    [InlineData("nope", "INVALID_PRODUCT")]
    [InlineData("wow", "INVALID_IDENTITY")]
    public void BadInputHasACode(string product, string code)
    {
        var e = Assert.Throws<BuildIdentityException>(() =>
            BuildIdentity.FromFields(product, new Dictionary<string, string> { ["BuildConfig"] = new('a', 32) }));
        Assert.Equal(code, e.Code);
    }

    [Fact]
    public void RegionAndCdnConfigAreNotIdentityFields() =>
        Assert.Equal("INVALID_IDENTITY", Assert.Throws<BuildIdentityException>(() => BuildIdentity.FromFields("wow",
            new Dictionary<string, string> { ["BuildConfig"] = new('a', 32), ["VersionsName"] = "1", ["Region"] = "us" })).Code);

    [Fact]
    public void ProductsListsTheReviewedIds()
    {
        Assert.Equal(13, Products.Ids.Count);
        Assert.True(Products.IsKnown("wow_classic_titan"));
        Assert.False(Products.IsKnown("WOW"));
        Assert.False(Products.IsKnown(null));
    }

    // #208: the 1.60 line is WoW: Forever, and wow_classic_titan is China's Titan Reforged (D475).
    [Theory]
    [InlineData("wowf", "Forever")]
    [InlineData("wow_classic_beta", "Forever Beta")]
    [InlineData("wow_cn_beta", "Forever Beta (China)")]
    [InlineData("wow_classic_titan", "Titan Reforged (China)")]
    public void IdentifiedProductsCarryTheirEditionLabel(string product, string label) => Assert.Equal(label, Products.Label(product));
}
