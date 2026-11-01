using System.Text.RegularExpressions;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Builds;

/// <summary>
/// One build to open: a registered product, its build and CDN configs (32 hex digits, kept lowercase) and the <c>cdns</c>
/// row whose route fetches what isn't stored. A null <see cref="Region"/> takes the <c>us</c> row, then any usable row;
/// <see cref="BuildRegions.ChooseAsync"/> picks a listing whose region has a usable route.
/// </summary>
public sealed partial record BuildRef
{
    public BuildRef(string Product, string BuildConfig, string CdnConfig, string? Region = null)
    {
        if (!Products.IsKnown(Product)) throw new ArgumentException("The product isn't registered.", nameof(Product));
        if (Region is not null && !RegionPattern().IsMatch(Region)) throw new ArgumentException("A region is 1-32 letters, digits, '-' or '_'.", nameof(Region));
        this.Product = Product; this.BuildConfig = Hex(BuildConfig, nameof(BuildConfig)); this.CdnConfig = Hex(CdnConfig, nameof(CdnConfig));
        this.Region = Region;
    }

    public string Product { get; }
    public string BuildConfig { get; }
    public string CdnConfig { get; }
    public string? Region { get; }

    public void Deconstruct(out string product, out string buildConfig, out string cdnConfig, out string? region)
    { product = Product; buildConfig = BuildConfig; cdnConfig = CdnConfig; region = Region; }

    private static string Hex(string value, string name) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit) ? value.ToLowerInvariant()
            : throw new ArgumentException("A config key is 32 hexadecimal digits.", name);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex RegionPattern();
}

/// <summary>How an open ended. Every failure stays its own state; only <see cref="Open"/> carries a build.</summary>
public enum BuildOpenState
{
    Open,
    /// <summary>A part isn't stored and there is no network (offline, or no CDN route). Retryable.</summary>
    NeedsNetwork,
    /// <summary>The CDN couldn't be used just now (timeout, 5xx, broken connection). Retryable.</summary>
    Unavailable,
    /// <summary>Every CDN host answered 404 at <see cref="BuildOpenResult.ObservedAt"/>. A dated observation, not absence.</summary>
    Missing,
    Encrypted,
    Corrupt,
    Unsupported,
    /// <summary>The object store couldn't take the bytes.</summary>
    StoreFailed,
}

/// <summary>The part of a build an open failed on.</summary>
public enum BuildPart { BuildConfig, CdnConfig, Encoding, Root, ArchiveIndex, FileIndex }

/// <summary>
/// The outcome of one open. <see cref="Opened"/> is set only when <see cref="State"/> is Open. On a failure,
/// <see cref="Code"/> is layer 2's or the parser's stable code and <see cref="Part"/> says where.
/// </summary>
public sealed record BuildOpenResult(BuildRef Build, BuildOpenState State, IBuild? Opened, BuildPart? Part, string? Code, string? Message,
    DateTimeOffset? ObservedAt);

/// <summary>
/// What an opened build says about itself. <see cref="Locales"/> are the names of the standard locale bits in the ROOT
/// locale union, lowest bit first; the union doesn't prove a build ships those locales.
/// </summary>
public sealed record BuildMetadata(string? BuildName, CdnKey RootCKey, CdnKey EncodingCKey, CdnKey EncodingEKey, int ArchiveCount,
    int FileCount, IReadOnlyList<string> Locales);

public enum FileResolutionState
{
    Found,
    /// <summary>ROOT doesn't list the FileDataID, or the path's name hash.</summary>
    UnknownFile,
    /// <summary>ROOT lists the file, but ENCODING has none of its variants' CKeys.</summary>
    NotInEncoding,
}

/// <summary>
/// One ROOT membership of a file. <see cref="EKey"/> is the first EKey ENCODING lists for <see cref="CKey"/> (null when the
/// CKey isn't in ENCODING). <see cref="Location"/> is the archive range that holds it; null means a loose object.
/// </summary>
public sealed record FileVariant(uint LocaleFlags, ulong ContentFlags, CdnKey CKey, long? DecodedSize, CdnKey? EKey, long? EncodedSize,
    ArchiveLocation? Location);

/// <summary>A lookup's answer. <see cref="FileDataId"/> is null for <see cref="FileResolutionState.UnknownFile"/>.</summary>
public sealed record FileResolution(FileResolutionState State, uint? FileDataId, IReadOnlyList<FileVariant> Variants);

/// <summary>
/// An opened build: its metadata and the resolver layer 4 asks for a file's variants. Lookups never fetch and may run
/// concurrently.
/// </summary>
public interface IBuild
{
    BuildRef Build { get; }
    BuildMetadata Metadata { get; }
    /// <summary>The CDN route and CDN config archive list for layer 4's fetches; null when no <c>cdns</c> route is known.</summary>
    CdnContext? Cdn { get; }
    /// <summary>Every ROOT membership of the FileDataID, in ROOT order.</summary>
    FileResolution Resolve(uint fileDataId);
    /// <summary>The file whose ROOT name hash matches <paramref name="path"/> (case and slash direction don't matter).</summary>
    FileResolution Resolve(string path);
    /// <summary>
    /// FileDataIDs from <paramref name="fromId"/> (inclusive), ascending and each once, with at least one ROOT variant whose
    /// locale flags include <paramref name="locale"/> (a <see cref="BuildMetadata.Locales"/> name). Content flags don't
    /// filter. Lazy; each enumeration is independent. Throws <see cref="ArgumentException"/> for an unknown locale name.
    /// </summary>
    IEnumerable<uint> FileDataIds(string locale, uint fromId = 0);
}

/// <summary>
/// Layer 3's build opener. Implementations: <see cref="BuildOpener"/> (through layer 2) and the in-memory fake in
/// <c>VibeMiner.Builds.Testing</c>; one contract suite runs against both. A successful open is reused; concurrent opens of
/// one build share one open, and cancelling one caller cancels only its wait.
/// </summary>
public interface IBuilds
{
    Task<BuildOpenResult> OpenAsync(BuildRef build, CancellationToken cancellationToken);
}

/// <summary>Locale bit names, in bit order.</summary>
internal static class BuildLocales
{
    private static readonly (uint Bit, string Name)[] Known =
    [
        (0x2, "enUS"), (0x4, "koKR"), (0x10, "frFR"), (0x20, "deDE"), (0x40, "zhCN"), (0x80, "esES"), (0x100, "zhTW"),
        (0x200, "enGB"), (0x400, "enCN"), (0x800, "enTW"), (0x1000, "esMX"), (0x2000, "ruRU"), (0x4000, "ptBR"), (0x8000, "itIT"),
        (0x10000, "ptPT"),
    ];

    public static IReadOnlyList<string> Of(uint union) => Array.AsReadOnly(Known.Where(k => (union & k.Bit) != 0).Select(k => k.Name).ToArray());

    /// <summary>The bit of a locale name, exactly as <see cref="Of"/> spells it.</summary>
    public static uint Bit(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var k in Known)
            if (k.Name == name) return k.Bit;
        throw new ArgumentException($"Unknown locale '{name}'.", nameof(name));
    }
}
