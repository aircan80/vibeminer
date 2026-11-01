using System.Text.RegularExpressions;

namespace VibeMiner.Builds;

/// <summary>A reviewed product id and the name people know it by.</summary>
public sealed record ReviewedProduct(string Id, string Label);

/// <summary>
/// A product the build list shows: a reviewed one with its label, or a WoW product the version service's summary lists that
/// isn't reviewed, labelled by its id (D476).
/// </summary>
public sealed record ListedProduct(string Id, string Label, bool Reviewed);

/// <summary>
/// The WoW products. <see cref="Reviewed"/> are the ones with a friendly name, in display order: the engine's product registry
/// (<c>VibeMiner.Domain.ProductRegistry</c>), copied because layer 3 doesn't reference the engine, except that wowxptr reads
/// "PTR 2" here so the two PTRs can be told apart (D165), and wow_classic_era_beta is here only, since the old registry goes with
/// #158. Any other id that <see cref="IsWow"/> accepts is a WoW product too: it is listed, refreshed and opened like a reviewed
/// one, labelled by its id (D476).
/// </summary>
public static partial class Products
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
        new("wow_classic_era_beta", "Classic Era Beta"),
        new("wow_anniversary", "Anniversary"),
        new("wow_classic_beta", "Forever Beta"),
        new("wow_cn_beta", "Forever Beta (China)"),
        new("wowf", "Forever"),
        new("wow_classic_titan", "Titan Reforged (China)") });

    public static readonly IReadOnlyList<string> Ids = Array.AsReadOnly(Reviewed.Select(p => p.Id).ToArray());

    /// <summary>The most unreviewed products <see cref="Listed"/> returns.</summary>
    public const int MaxUnreviewed = 64;

    /// <summary>Layer 1's product grammar (1-64 of <c>a-z 0-9 _ -</c>) and a "wow" prefix.</summary>
    [GeneratedRegex("^wow[a-z0-9_-]{0,61}$", RegexOptions.CultureInvariant)]
    private static partial Regex WowPattern();

    /// <summary>Whether the id is reviewed.</summary>
    public static bool IsKnown(string? product) => product is not null && Ids.Contains(product, StringComparer.Ordinal);

    /// <summary>
    /// Whether the id is a WoW product layer 3 serves: "wow" then up to 61 of <c>a-z 0-9 _ -</c>, so it fits layer 1's history
    /// grammar. Every reviewed id is one.
    /// </summary>
    public static bool IsWow(string? product) => product is not null && WowPattern().IsMatch(product);

    /// <summary>The friendly name of a reviewed product. Throws <see cref="ArgumentException"/> for any other id.</summary>
    public static string Label(string product) =>
        Reviewed.FirstOrDefault(p => p.Id == product)?.Label ?? throw new ArgumentException($"'{product}' isn't a registered product.", nameof(product));

    /// <summary>What to call a WoW product: a reviewed one's label, any other's id.</summary>
    public static string Name(string product) => Reviewed.FirstOrDefault(p => p.Id == product)?.Label ?? product;

    /// <summary>Whether a product the version service lists looks like WoW (its id starts with "wow"), reviewed or not.</summary>
    public static bool LooksLikeWow(string product) => product.StartsWith("wow", StringComparison.Ordinal);

    /// <summary>
    /// The products the build list shows: every reviewed one in display order, then the unreviewed ids among
    /// <paramref name="unreviewed"/> that <see cref="IsWow"/> accepts, each once, in natural order (wownev2 before wownev10), at
    /// most <see cref="MaxUnreviewed"/>.
    /// </summary>
    public static IReadOnlyList<ListedProduct> Listed(IEnumerable<string> unreviewed)
    {
        ArgumentNullException.ThrowIfNull(unreviewed);
        var others = unreviewed.Where(p => IsWow(p) && !IsKnown(p)).Distinct(StringComparer.Ordinal)
            .Order(NaturalOrder.Instance).Take(MaxUnreviewed).Select(p => new ListedProduct(p, p, false));
        return [.. Reviewed.Select(p => new ListedProduct(p.Id, p.Label, true)), .. others];
    }

    /// <summary>Ordinal, except that runs of digits compare by value, so wownev2 sorts before wownev10.</summary>
    internal sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var c = string.CompareOrdinal(a, b);
                    if (c != 0) return c;
                    // Equal values: fewer leading zeros first, so the order stays total.
                    if (i - si != j - sj) return (i - si).CompareTo(j - sj);
                    continue;
                }
                if (x[i] != y[j]) return x[i].CompareTo(y[j]);
                i++; j++;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
