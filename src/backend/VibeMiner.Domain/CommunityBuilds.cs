using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Domain;

/// <summary>
/// Where a community build came from (#9, D106). The community source supplies only the build index; every file still
/// comes from Blizzard's CDN. CreatedAt is the source's own date for the row, when it gave a readable one.
/// </summary>
public sealed record CommunityBuildSource(string Source, string SourceUrl, string? CreatedAt, string FirstImportedAt, string LastImportedAt);

/// <summary>
/// One build index row from the community list: product, version, config hashes. Its identity is its own namespace
/// (<see cref="Revision"/>), so it can never collide with or stand in for a Blizzard observation.
/// </summary>
public sealed record CommunityBuildRow(string ProductId, string Version, string BuildConfig, string CdnConfig, string? ProductConfig, string? CreatedAt)
{
    public const string Revision = "vibeminer-community-build-v1";
    private static readonly Regex VersionPattern = new("^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}\\.([0-9]{1,9})$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>The build number: the version's last part.</summary>
    public long BuildNumber => long.Parse(VersionPattern.Match(Version).Groups[1].Value, CultureInfo.InvariantCulture);

    public string Id
    {
        get
        {
            var text = new StringBuilder();
            foreach (var value in new[] { Revision, ProductId, Version, BuildConfig, CdnConfig, ProductConfig ?? "" })
                text.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }

    /// <summary>A row is usable only with a registered product, a four-part version and lowercase 32-hex configs.</summary>
    public static bool Valid(CommunityBuildRow row) =>
        ProductRegistry.Products.Any(product => product.Id == row.ProductId) && VersionPattern.IsMatch(row.Version) &&
        HashPattern.IsMatch(row.BuildConfig) && HashPattern.IsMatch(row.CdnConfig) &&
        (row.ProductConfig is null || HashPattern.IsMatch(row.ProductConfig)) &&
        (row.CreatedAt is null || row.CreatedAt.Length <= 40);
}

/// <summary>
/// The community build list as the engine has it: the Settings switch as read now, the latest attempt, the latest
/// successful import and how many builds it holds. State is off, notFetched, ready or failed (the latest attempt
/// failed; builds from an earlier import stay).
/// </summary>
public sealed record CommunityBuildsStatus(bool Enabled, string State, string Source, string? LastAttemptAt,
    string? FetchedAt, string? NextCheckAfter, int BuildCount, SettingsProblem? Problem)
{
    public const string SourceName = "wago.tools";
    public static CommunityBuildsStatus Off { get; } = new(false, "off", SourceName, null, null, null, 0, null);
}

/// <summary>What one HEAD or GET of one official CDN object found. Missing is a 404, or a 403 on level3.</summary>
public enum CdnPresence { Present, Missing, Unknown }

/// <summary>How many objects of one stage were present, missing, or couldn't be told (5xx, timeouts, offline).</summary>
public sealed record AvailabilityCounts(int Total, int Present, int Missing, int Unknown);

/// <summary>
/// One availability check of a build on Blizzard's CDN (#9, D106). State is available, partlyAvailable, gone or unknown.
/// Scope is the last stage that ran: configs, indexes or archives. Each config state is verified (present and its MD5
/// and grammar checked), missing (on every host), invalid (bytes that don't match the hash) or unknown.
/// </summary>
public sealed record BuildAvailability(string State, string CheckedAt, string Scope, string BuildConfig, string CdnConfig,
    IReadOnlyList<string> Hosts, string BuildConfigState, string CdnConfigState, AvailabilityCounts? Indexes,
    AvailabilityCounts? Archives, SettingsProblem? Problem)
{
    public const string Available = "available";
    public const string PartlyAvailable = "partlyAvailable";
    public const string Gone = "gone";
    public const string Unknown = "unknown";
}
