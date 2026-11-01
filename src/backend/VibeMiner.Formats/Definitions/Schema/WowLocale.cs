namespace VibeMiner.Formats.Definitions;

/// <summary>One of the 15 ROOT locales, by its exact code and its ROOT locale flag bit (docs/formats/root.md).
/// Only the static instances exist, so two equal locales are the same code and bit.</summary>
public sealed record WowLocale
{
    public static readonly WowLocale EnUS = new("enUS", 0x2);
    public static readonly WowLocale KoKR = new("koKR", 0x4);
    public static readonly WowLocale FrFR = new("frFR", 0x10);
    public static readonly WowLocale DeDE = new("deDE", 0x20);
    public static readonly WowLocale ZhCN = new("zhCN", 0x40);
    public static readonly WowLocale EsES = new("esES", 0x80);
    public static readonly WowLocale ZhTW = new("zhTW", 0x100);
    public static readonly WowLocale EnGB = new("enGB", 0x200);
    public static readonly WowLocale EnCN = new("enCN", 0x400);
    public static readonly WowLocale EnTW = new("enTW", 0x800);
    public static readonly WowLocale EsMX = new("esMX", 0x1000);
    public static readonly WowLocale RuRU = new("ruRU", 0x2000);
    public static readonly WowLocale PtBR = new("ptBR", 0x4000);
    public static readonly WowLocale ItIT = new("itIT", 0x8000);
    public static readonly WowLocale PtPT = new("ptPT", 0x10000);

    /// <summary>Every locale, in flag bit order.</summary>
    public static IReadOnlyList<WowLocale> All { get; } = Array.AsReadOnly(
        [EnUS, KoKR, FrFR, DeDE, ZhCN, EsES, ZhTW, EnGB, EnCN, EnTW, EsMX, RuRU, PtBR, ItIT, PtPT]);

    private WowLocale(string code, uint flag) { Code = code; Flag = flag; }

    public string Code { get; }
    public uint Flag { get; }

    /// <summary>The locale with exactly this code, case-sensitive. Anything else is <c>DEFINITION_LOCALE_UNKNOWN</c>.</summary>
    public static WowLocale Parse(string code)
    {
        if (code is not null)
            foreach (var locale in All)
                if (string.Equals(locale.Code, code, StringComparison.Ordinal)) return locale;
        throw new DefinitionException("DEFINITION_LOCALE_UNKNOWN", "The locale is not one of the 15 ROOT locale codes, spelled exactly.");
    }

    public static bool TryParse(string? code, out WowLocale locale)
    {
        foreach (var candidate in All)
            if (string.Equals(candidate.Code, code, StringComparison.Ordinal)) { locale = candidate; return true; }
        locale = null!;
        return false;
    }

    public override string ToString() => Code;
}
