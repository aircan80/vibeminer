namespace VibeMiner.Builds;

/// <summary>A reviewed product id and the name people know it by.</summary>
public sealed record ReviewedProduct(string Id, string Label);

/// <summary>
/// The reviewed WoW product ids, in display order, with their friendly names: the engine's product registry
/// (<c>VibeMiner.Domain.ProductRegistry</c>), copied because layer 3 doesn't reference the engine, except that wowxptr reads "PTR 2"
/// here so the two PTRs can be told apart (D165). Adding a product is a reviewed change to both (D017).
/// </summary>
public static class Products
{
    public static readonly IReadOnlyList<ReviewedProduct> Reviewed = Array.AsReadOnly(new ReviewedProduct[] {
        new("wow", "Retail"),
        new("wowt", "PTR"),
        new("wowxptr", "PTR 2"),
        new("wow_beta", "Beta"),
        new("wow_classic", "Classic Progression"),
        new("wow_classic_ptr", "Classic Progression PTR"),
        new("wow_classic_era", "Classic Era"),
        new("wow_classic_era_ptr", "Classic Era PTR"),
        new("wow_anniversary", "Anniversary"),
        new("wow_classic_beta", "Forever Beta"),
        new("wow_cn_beta", "Forever Beta (China)"),
        new("wowf", "Forever"),
        new("wow_classic_titan", "Titan Reforged (China)") });

    public static readonly IReadOnlyList<string> Ids = Array.AsReadOnly(Reviewed.Select(p => p.Id).ToArray());

    public static bool IsKnown(string? product) => product is not null && Ids.Contains(product, StringComparer.Ordinal);

    /// <summary>The friendly name of a reviewed product. Throws <see cref="ArgumentException"/> for any other id.</summary>
    public static string Label(string product) =>
        Reviewed.FirstOrDefault(p => p.Id == product)?.Label ?? throw new ArgumentException($"'{product}' isn't a registered product.", nameof(product));

    /// <summary>Whether a product the version service lists looks like WoW (its id starts with "wow"), reviewed or not.</summary>
    public static bool LooksLikeWow(string product) => product.StartsWith("wow", StringComparison.Ordinal);
}
