using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Domain;

public sealed class DiscoveryException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record Product(string Id, string Label, string Description);
public static class ProductRegistry
{
    public static readonly IReadOnlyList<Product> Products = Array.AsReadOnly(new[] {
        new Product("wow", "Retail", "World of Warcraft retail"),
        new Product("wowt", "PTR", "World of Warcraft public test realm"),
        new Product("wowxptr", "PTR", "Additional World of Warcraft public test realm"),
        new Product("wow_beta", "Beta", "World of Warcraft beta"),
        new Product("wow_classic", "Classic Progression", "World of Warcraft Classic progression"),
        new Product("wow_classic_ptr", "Classic Progression PTR", "Classic progression public test realm"),
        new Product("wow_classic_era", "Classic Era", "World of Warcraft Classic Era"),
        new Product("wow_classic_era_ptr", "Classic Era PTR", "Classic Era public test realm"),
        new Product("wow_anniversary", "Anniversary", "World of Warcraft anniversary"),
        new Product("wow_classic_beta", "Classic Beta", "World of Warcraft Classic beta"),
        new Product("wow_cn_beta", "Edition unconfirmed", "Reviewed product ID; edition unconfirmed"),
        new Product("wowf", "Edition unconfirmed", "Reviewed product ID; edition unconfirmed"),
        new Product("wow_classic_titan", "Edition unconfirmed", "Reviewed product ID; edition unconfirmed") });

    public static void Validate(string product)
    {
        if (!Products.Any(p => p.Id == product))
            throw new DiscoveryException("INVALID_PRODUCT", "Choose a product from the reviewed registry.");
    }
}

public sealed record SourceReceipt(string Region, string Role, string Url, string RetrievedAt,
    string Sha256, int ByteLength, int? RowOrdinal);
public sealed record Build(string Id, string ProductId, string Version, string BuildId,
    string BuildConfig, string CdnConfig, string? ProductConfig,
    IReadOnlyDictionary<string, string> IdentityFields, IReadOnlyList<string> Regions,
    IReadOnlyList<string> AdvertisedRegions, IReadOnlyList<string> RouteRegions,
    string FirstSeenAt, string LastSeenAt, bool IsNew, bool IsAdvertised, bool IsLocal,
    string Availability, IReadOnlyList<SourceReceipt> SourceReceipts);
public sealed record DiscoveryProblem(string? Region, string Code, string Message);
public sealed record DiscoveryPage(string ProductId, IReadOnlyList<Build> Builds, string? NextCursor,
    string? LastCheckedAt, IReadOnlyList<string> CheckedRegions, IReadOnlyList<DiscoveryProblem> Errors,
    bool HasSavedHistory, string Connection);

public sealed record BuildIdentity(string Id, string Canonical, string ProductId,
    IReadOnlyDictionary<string, string> Fields)
{
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex RegionPattern = new("^[a-z]{2,8}$", RegexOptions.CultureInvariant);
    public static bool IsValidRegion(string value) => RegionPattern.IsMatch(value);
    public static BuildIdentity Create(string product, IReadOnlyDictionary<string, string> source)
    {
        ProductRegistry.Validate(product);
        foreach (var name in new[] { "Region", "BuildConfig", "CDNConfig", "VersionsName" })
            if (!source.TryGetValue(name, out var value) || value.Length == 0)
                throw new DiscoveryException("INVALID_IDENTITY", "A required build identity field is missing.");
        if (!IsValidRegion(source["Region"]))
            throw new DiscoveryException("INVALID_REGION", "A metadata region is invalid.");
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in source)
        {
            if (name == "Region") continue;
            if (name is "BuildConfig" or "CDNConfig" or "ProductConfig")
            {
                if (value.Length != 0 && !HashPattern.IsMatch(value))
                    throw new DiscoveryException("INVALID_IDENTITY", "A configuration identity is invalid.");
                fields[name] = value.ToLowerInvariant();
            }
            else fields[name] = value;
        }
        var text = new StringBuilder();
        static void Frame(StringBuilder builder, string value) => builder.Append(Encoding.UTF8.GetByteCount(value)
            .ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        Frame(text, "vibeminer-build-identity-v1"); Frame(text, product); Frame(text, "Published");
        foreach (var (key, value) in fields) { Frame(text, key); Frame(text, value); }
        var canonical = text.ToString();
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), canonical,
            product, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(fields));
    }
}
