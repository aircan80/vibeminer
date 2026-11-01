using VibeMiner.Formats.Definitions;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

public sealed class WowLocaleTests
{
    [Fact]
    public void Lists_the_fifteen_root_locales_with_their_flag_bits_in_bit_order()
    {
        Assert.Equal(
        [
            ("enUS", 0x2u), ("koKR", 0x4u), ("frFR", 0x10u), ("deDE", 0x20u), ("zhCN", 0x40u), ("esES", 0x80u), ("zhTW", 0x100u),
            ("enGB", 0x200u), ("enCN", 0x400u), ("enTW", 0x800u), ("esMX", 0x1000u), ("ruRU", 0x2000u), ("ptBR", 0x4000u),
            ("itIT", 0x8000u), ("ptPT", 0x10000u),
        ], WowLocale.All.Select(locale => (locale.Code, locale.Flag)));
        Assert.Equal(15, WowLocale.All.Select(locale => locale.Flag).Distinct().Count());
        Assert.All(WowLocale.All, locale => Assert.Equal(1, System.Numerics.BitOperations.PopCount(locale.Flag)));
    }

    [Fact]
    public void Parses_every_code_exactly_to_its_one_instance()
    {
        foreach (var locale in WowLocale.All)
        {
            Assert.Same(locale, WowLocale.Parse(locale.Code));
            Assert.True(WowLocale.TryParse(locale.Code, out var parsed));
            Assert.Same(locale, parsed);
            Assert.Equal(locale.Code, locale.ToString());
        }
        Assert.Equal(0x200u, WowLocale.Parse("enGB").Flag);
        Assert.NotEqual(WowLocale.EsES, WowLocale.EsMX);
    }

    [Theory]
    [InlineData("enus")]
    [InlineData("ENUS")]
    [InlineData("en-US")]
    [InlineData("en_US")]
    [InlineData(" enUS")]
    [InlineData("enUS ")]
    [InlineData("")]
    [InlineData("xxXX")]
    [InlineData("frCA")]
    public void Refuses_anything_but_an_exact_code(string code)
    {
        var error = Assert.Throws<DefinitionException>(() => WowLocale.Parse(code));
        Assert.Equal("DEFINITION_LOCALE_UNKNOWN", error.Code);
        Assert.False(error.Retryable);
        Assert.False(WowLocale.TryParse(code, out _));
    }

    [Fact]
    public void Refuses_a_null_code()
    {
        Assert.Equal("DEFINITION_LOCALE_UNKNOWN", Assert.Throws<DefinitionException>(() => WowLocale.Parse(null!)).Code);
        Assert.False(WowLocale.TryParse(null, out _));
    }
}
