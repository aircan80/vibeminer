using VibeMiner.Application;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests;

// D095 over authored memberships shaped like Classic 5.5.4 texture entries (locale 0x173F6, content 0x2080000 / 0x2080001).
public sealed class RootTextureVariantsTests
{
    private const uint Locale = 0x173F6, Flags = 0x2080000;
    private static readonly string Standard = new('a', 32), High = new('b', 32), Other = new('c', 32);
    private static RootTargetMatch M(string key, uint content, uint locale = Locale, uint id = 124624, uint second = 0, byte third = 0, long block = 0) =>
        new(block, 0, new(id, key, locale, content | second | ((uint)third << 17), new(content, second, third), null));
    private static RootTargetMatch[] Classic() => [M(High, Flags | 1, block: 0), M(Standard, Flags, block: 1), M(High, Flags | 0x81, block: 2), M(Standard, Flags | 0x80, block: 3)];

    [Fact]
    public void The_HighResTexture_split_with_LowViolence_copies_chooses_the_standard_variant_and_keeps_its_copies()
    {
        var choice = RootTextureVariants.ChooseStandard(Classic())!;
        Assert.Equal((Standard, Flags, High, Flags | 1), (choice.ContentKey, choice.ContentFlags, choice.HighResolutionContentKey, choice.HighResolutionContentFlags));
        Assert.Equal(new long[] { 1, 3 }, choice.Selected.Select(m => m.BlockOrdinal));
        Assert.Equal(RootTextureVariants.Policy, CreatureGeometryValidation.TextureVariantPolicy);
    }

    [Fact]
    public void A_split_without_LowViolence_copies_or_in_the_raw_second_part_is_the_same_rule()
    {
        Assert.Equal(Standard, RootTextureVariants.ChooseStandard([M(Standard, Flags), M(High, Flags | 1)])!.ContentKey);
        Assert.Equal(Standard, RootTextureVariants.ChooseStandard([M(Standard, 0, second: Flags), M(High, 1, second: Flags)])!.ContentKey);
    }

    public static TheoryData<string, RootTargetMatch[]> Disagreements() => new()
    {
        { "one membership", [M(Standard, Flags)] },
        { "one content key", [M(Standard, Flags), M(Standard, Flags | 1)] },
        { "locale differs", [M(Standard, Flags), M(High, Flags | 1, locale: 0x2)] },
        { "another content bit differs", [M(Standard, Flags), M(High, Flags | 1 | 0x8)] },
        { "only another content bit", [M(Standard, Flags), M(High, Flags | 0x8)] },
        { "only LowViolence", [M(Standard, Flags), M(High, Flags | 0x80)] },
        { "raw parts differ", [M(Standard, Flags), M(High, 1, second: Flags)] },
        { "third byte differs", [M(Standard, Flags), M(High, Flags | 1, third: 1)] },
        { "third content key", [M(Standard, Flags), M(High, Flags | 1), M(Other, Flags)] },
        { "LowViolence copy matches neither", [M(Standard, Flags), M(High, Flags | 1), M(Other, Flags | 0x80)] },
        { "LowViolence copy crosses over", [M(Standard, Flags), M(High, Flags | 1), M(High, Flags | 0x80)] },
        { "standard only as LowViolence", [M(Standard, Flags | 0x80), M(High, Flags | 1)] },
        { "high only as LowViolence", [M(Standard, Flags), M(High, Flags | 0x81)] },
        { "two FileDataIDs", [M(Standard, Flags), M(High, Flags | 1, id: 2)] },
    };

    [Theory]
    [MemberData(nameof(Disagreements))]
    public void Any_other_disagreement_has_no_choice(string kind, RootTargetMatch[] memberships) =>
        Assert.True(RootTextureVariants.ChooseStandard(memberships) is null, kind);
}
