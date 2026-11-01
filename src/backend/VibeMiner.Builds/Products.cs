namespace VibeMiner.Builds;

/// <summary>The reviewed WoW product ids, in display order. The same list as the engine's product registry.</summary>
public static class Products
{
    public static readonly IReadOnlyList<string> Ids = Array.AsReadOnly(new[] {
        "wow", "wowt", "wowxptr", "wow_beta", "wow_classic", "wow_classic_ptr", "wow_classic_era", "wow_classic_era_ptr",
        "wow_anniversary", "wow_classic_beta", "wow_cn_beta", "wowf", "wow_classic_titan" });

    public static bool IsKnown(string? product) => product is not null && Ids.Contains(product, StringComparer.Ordinal);
}
