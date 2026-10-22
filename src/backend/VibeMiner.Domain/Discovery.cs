using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Domain;

public sealed class DiscoveryException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    /// <summary>The underlying storage or I/O cause, for logs and problem details; never shown as the message.</summary>
    public string? Cause { get; init; }
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

    /// <summary>
    /// UTF-8 byte length of the longest registered product ID. Storage and proof checks bound a saved product
    /// to this so a listed product always fits; membership itself is checked by <see cref="Validate"/>.
    /// </summary>
    public static readonly int MaxIdBytes = Products.Max(p => Encoding.UTF8.GetByteCount(p.Id));

    public static void Validate(string product)
    {
        if (!Products.Any(p => p.Id == product))
            throw new DiscoveryException("INVALID_PRODUCT", "Choose a product from the reviewed registry.");
    }
    /// <summary>The product's position in the registry, which is the cross-product display order.</summary>
    public static int Ordinal(string product)
    {
        for (var i = 0; i < Products.Count; i++) if (Products[i].Id == product) return i;
        throw new DiscoveryException("INVALID_PRODUCT", "Choose a product from the reviewed registry.");
    }
}

public sealed record SourceReceipt(string Region, string Role, string Url, string RetrievedAt,
    string Sha256, int ByteLength, int? RowOrdinal);
/// <summary>One CDN configuration a build has been advertised with, plus the newest versions-row receipt per region.</summary>
public sealed record CdnConfigObservation(string CdnConfig, string FirstSeenAt, string LastSeenAt,
    IReadOnlyList<string> Regions, IReadOnlyList<string> AdvertisedRegions, IReadOnlyList<SourceReceipt> SourceReceipts);
/// <summary>
/// CdnConfig is the configuration this projection is bound to: the newest advertised one in discovery
/// reads, or the exact one an opening/admission was made with. CdnConfigHistory is newest first and holds at most
/// the newest 16 entries; CdnConfigHistoryCount is the build's total, so omitted older entries are stated.
/// </summary>
public sealed record Build(string Id, string ProductId, string Version, string BuildId,
    string BuildConfig, string CdnConfig, string? ProductConfig,
    IReadOnlyDictionary<string, string> IdentityFields, IReadOnlyList<string> Regions,
    IReadOnlyList<string> AdvertisedRegions, IReadOnlyList<string> RouteRegions,
    string FirstSeenAt, string LastSeenAt, bool IsNew, bool IsAdvertised, bool IsLocal,
    string Availability, IReadOnlyList<SourceReceipt> SourceReceipts,
    IReadOnlyList<CdnConfigObservation> CdnConfigHistory, int CdnConfigHistoryCount);
/// <summary>
/// One finding of a discovery check. Severity "problem" means the check did not get everything it asked for, so the
/// product's check is partial. Severity "note" is a regional fact about the official source that leaves the check
/// complete, such as a region advertised without any CDN route (D076).
/// </summary>
public sealed record DiscoveryProblem(string? Region, string Code, string Message, string Severity = DiscoveryProblem.ProblemSeverity)
{
    public const string ProblemSeverity = "problem";
    public const string NoteSeverity = "note";
    /// <summary>A regional note: it is reported, but the check that found it is still complete.</summary>
    public static DiscoveryProblem RegionNote(string region, string code, string message) => new(region, code, message, NoteSeverity);
    /// <summary>The connection of a check that read the versions document: only problems make it partial, never notes.</summary>
    public static string CheckedConnection(IEnumerable<DiscoveryProblem> findings) =>
        findings.Any(finding => finding.Severity != NoteSeverity) ? "partial" : "online";
}
public sealed record DiscoveryPage(string ProductId, IReadOnlyList<Build> Builds, string? NextCursor,
    string? LastCheckedAt, IReadOnlyList<string> CheckedRegions, IReadOnlyList<DiscoveryProblem> Errors,
    bool HasSavedHistory, string Connection);
/// <summary>The saved outcome of one product's latest check: the same facts a single-product page carries, without builds.</summary>
public sealed record ProductDiscoveryStatus(string ProductId, string? LastCheckedAt, IReadOnlyList<string> CheckedRegions,
    IReadOnlyList<DiscoveryProblem> Errors, bool HasSavedHistory, string Connection);
/// <summary>A product whose part of a cross-product refresh did not complete. Its saved evidence is unchanged.</summary>
public sealed record ProductRefreshProblem(string ProductId, string Code, string Message);
/// <summary>
/// One bounded page of builds across every registered product, ordered by registry product order, then numeric
/// BuildId descending (non-numeric last), then build identity. ProductId is the optional product filter (null = all).
/// Products lists every registered product's saved status in registry order whatever the filter.
/// RefreshProblems is empty for reads; a cross-product refresh lists the products whose attempt did not complete.
/// </summary>
public sealed record DiscoveryCatalogPage(string? ProductId, string Filter, IReadOnlyList<Build> Builds, string? NextCursor,
    IReadOnlyList<ProductDiscoveryStatus> Products, IReadOnlyList<ProductRefreshProblem> RefreshProblems);
/// <summary>Cross-product refresh progress: how many products have finished their check out of the registry total.</summary>
public sealed record DiscoveryRefreshProgress(int Completed, int Total);

/// <summary>
/// Build identity v2: product + every versions field except Region and CDNConfig. CDNConfig only lists
/// archives and can be republished for the same build; it is kept per observation, not in the identity.
/// </summary>
public sealed record BuildIdentity(string Id, string Canonical, string ProductId,
    IReadOnlyDictionary<string, string> Fields)
{
    public const string Revision = "vibeminer-build-identity-v2";
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
            throw new DiscoveryException("INVALID_IDENTITY", "A configuration identity is invalid.");
        return value.ToLowerInvariant();
    }
    /// <summary>Recompute an identity from saved identity fields, which never contain Region or CDNConfig.</summary>
    public static BuildIdentity FromFields(string product, IReadOnlyDictionary<string, string> source)
    {
        ProductRegistry.Validate(product);
        foreach (var name in new[] { "BuildConfig", "VersionsName" })
            if (!source.TryGetValue(name, out var value) || value.Length == 0)
                throw new DiscoveryException("INVALID_IDENTITY", "A required build identity field is missing.");
        if (source.ContainsKey("Region") || source.ContainsKey("CDNConfig"))
            throw new DiscoveryException("INVALID_IDENTITY", "Region and CDNConfig are not build identity fields.");
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in source)
        {
            if (name is "BuildConfig" or "ProductConfig")
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
        Frame(text, Revision); Frame(text, product); Frame(text, "Published");
        foreach (var (key, value) in fields) { Frame(text, key); Frame(text, value); }
        var canonical = text.ToString();
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), canonical,
            product, new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(fields));
    }
}
