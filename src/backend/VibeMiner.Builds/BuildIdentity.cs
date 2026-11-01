using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Builds;

/// <summary>A build identity that can't be made; <see cref="Code"/> says why.</summary>
public sealed class BuildIdentityException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Build identity v2: product + every versions field except Region and CDNConfig. A port of the engine's identity whose
/// canonical string and Id are byte-identical for the same inputs; CDNConfig is kept per observation, not in the identity.
/// </summary>
public sealed record BuildIdentity(string Id, string Canonical, string ProductId, IReadOnlyDictionary<string, string> Fields)
{
    public const string Revision = "vibeminer-build-identity-v2";
    private static readonly Regex HashPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex RegionPattern = new("^[a-z]{2,8}$", RegexOptions.CultureInvariant);

    public static bool IsValidRegion(string value) => RegionPattern.IsMatch(value);

    /// <summary>The identity of one versions row.</summary>
    public static BuildIdentity Create(string product, IReadOnlyDictionary<string, string> source)
    {
        CheckProduct(product);
        foreach (var name in new[] { "Region", "BuildConfig", "CDNConfig", "VersionsName" })
            if (!source.TryGetValue(name, out var value) || value.Length == 0)
                throw new BuildIdentityException("INVALID_IDENTITY", "A required build identity field is missing.");
        if (!IsValidRegion(source["Region"])) throw new BuildIdentityException("INVALID_REGION", "A region is invalid.");
        _ = ObservedCdnConfig(source);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in source)
            if (name is not ("Region" or "CDNConfig")) fields[name] = value;
        return FromFields(product, fields);
    }

    /// <summary>The normalized CDNConfig of one versions row. It is required but not part of the identity.</summary>
    public static string ObservedCdnConfig(IReadOnlyDictionary<string, string> source)
    {
        if (!source.TryGetValue("CDNConfig", out var value) || !HashPattern.IsMatch(value))
            throw new BuildIdentityException("INVALID_IDENTITY", "A configuration identity is invalid.");
        return value.ToLowerInvariant();
    }

    /// <summary>Recomputes an identity from saved identity fields, which never contain Region or CDNConfig.</summary>
    public static BuildIdentity FromFields(string product, IReadOnlyDictionary<string, string> source)
    {
        CheckProduct(product);
        foreach (var name in new[] { "BuildConfig", "VersionsName" })
            if (!source.TryGetValue(name, out var value) || value.Length == 0)
                throw new BuildIdentityException("INVALID_IDENTITY", "A required build identity field is missing.");
        if (source.ContainsKey("Region") || source.ContainsKey("CDNConfig"))
            throw new BuildIdentityException("INVALID_IDENTITY", "Region and CDNConfig are not build identity fields.");
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in source)
        {
            if (name is "BuildConfig" or "ProductConfig")
            {
                if (value.Length != 0 && !HashPattern.IsMatch(value))
                    throw new BuildIdentityException("INVALID_IDENTITY", "A configuration identity is invalid.");
                fields[name] = value.ToLowerInvariant();
            }
            else fields[name] = value;
        }
        var text = new StringBuilder();
        Frame(text, Revision); Frame(text, product); Frame(text, "Published");
        foreach (var (key, value) in fields) { Frame(text, key); Frame(text, value); }
        var canonical = text.ToString();
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), canonical, product,
            new ReadOnlyDictionary<string, string>(fields));
    }

    private static void Frame(StringBuilder text, string value) =>
        text.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    private static void CheckProduct(string product)
    {
        if (!Products.IsWow(product)) throw new BuildIdentityException("INVALID_PRODUCT", "A product is \"wow\" then up to 61 of a-z, 0-9, _ or -.");
    }
}
