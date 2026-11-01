using VibeMiner.Builds;

namespace VibeMiner.Files;

/// <summary>
/// Layer 4: a file system per build and locale. Implementations: <see cref="FileSystem"/> (over layers 3 and 2) and the
/// in-memory fake in <c>VibeMiner.Files.Testing</c>; one contract suite runs against both. Keys, BLTE and encryption stay
/// below this surface.
/// </summary>
public interface IFileSystem
{
    /// <summary>
    /// Opens the build through layer 3 and binds it to one locale (a name such as <c>enUS</c>, see <see cref="FileLocales"/>).
    /// Opening fetches only metadata, never file content. An unregistered locale name throws <see cref="ArgumentException"/>.
    /// </summary>
    Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken);
}

/// <summary>
/// One build and locale. The handle holds the build's ROOT and ENCODING lookups while it is referenced, so a per-file call
/// is a lookup. Stored state is never cached: every call asks layer 2, so a file stored a moment ago reads as stored.
/// Calls may run concurrently. Dispose the handle when done: it drops its hold on the build, and every later call throws
/// <see cref="ObjectDisposedException"/>; a list already being read runs on to its end. Layer 3 bounds how many opened builds stay in memory.
/// </summary>
public interface IBuildFiles : IAsyncDisposable
{
    BuildRef Build { get; }
    string Locale { get; }

    /// <summary>
    /// The build's files for this locale that match <paramref name="filter"/>, ascending by FileDataID. Stored state is checked
    /// through layer 2 in batches; a layer 2 failure throws. To page, stop reading and call again with
    /// <see cref="FileFilter.FromId"/> one past the last FileDataID read (there is no next page after <see cref="uint.MaxValue"/>).
    /// </summary>
    IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken);

    /// <summary>One file by FileDataID or path. Never fetches content; a layer 2 failure throws.</summary>
    Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken);

    /// <summary>
    /// True when this build lists the file for this locale (<see cref="FileLookupState.Found"/> or
    /// <see cref="FileLookupState.NotInEncoding"/>). Says nothing about stored state.
    /// </summary>
    Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken);
}

/// <summary>A file asked for by FileDataID or by path (case and slash direction don't matter).</summary>
public readonly record struct FileRef
{
    private FileRef(uint? id, string? path) { FileDataId = id; Path = path; }

    public uint? FileDataId { get; }
    public string? Path { get; }

    public static FileRef Id(uint fileDataId) => new(fileDataId, null);

    public static FileRef ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(null, path);
    }

    public static implicit operator FileRef(uint fileDataId) => Id(fileDataId);

    public override string ToString() => FileDataId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : Path ?? "";
}

/// <summary>Whether a file's bytes are in the profile's object store.</summary>
public enum FileStorage
{
    Stored,
    NotStored,
    /// <summary>The build has no encoded copy to look for (ENCODING doesn't list the file's content key).</summary>
    NoEncodedCopy,
}

/// <summary>A file's format. Detection from magic bytes, with the listfile extension as a fallback, comes in #173.</summary>
public enum FileFormat
{
    Unknown,
}

/// <summary>
/// One file in one build and locale. A file can have several ROOT variants in one locale (low violence, platform, other
/// content flags); every field comes from the one variant this locale reads, the first in ROOT order within the best of:
/// ENCODING lists it and it is neither low violence (content flag 0x80) nor a high-resolution texture (0x1); ENCODING lists it
/// and it isn't low violence; ENCODING lists it; any in the locale. Reads (#172) use the same variant. <see cref="Storage"/>
/// checks the first encoded key ENCODING lists for the content.
/// <see cref="Path"/> is the listfile name, null when the listfile doesn't name the file (names come in #173), even when it
/// was looked up by path. <see cref="Size"/> is the decoded size ENCODING declares. <see cref="ContentKey"/> is the decoded
/// content's MD5 (the CKey) as 32 lowercase hex digits: content identity, the same for the same bytes in any build, so build
/// diffs can compare it. It is never an encoding key or a TACT key. <see cref="Encrypted"/> is ROOT's encrypted content flag
/// (0x8000000), a hint; whether the file can be read comes with reading (#172).
/// </summary>
public sealed record BuildFile(uint FileDataId, string? Path, long? Size, string ContentKey, FileFormat Format, FileStorage Storage,
    bool Encrypted);

public enum FileLookupState
{
    Found,
    /// <summary>
    /// The build doesn't list the FileDataID, or no named ROOT entry matches the path. Retail ROOT mostly has no names, so a
    /// path that misses doesn't prove the file is absent.
    /// </summary>
    UnknownFile,
    /// <summary>The build lists the file, but not for this handle's locale.</summary>
    NotInLocale,
    /// <summary>The build lists the file for this locale, but ENCODING has no encoded copy of it. <see cref="FileStat.File"/> is set, with <see cref="FileStorage.NoEncodedCopy"/>.</summary>
    NotInEncoding,
}

/// <summary>A stat's answer. <see cref="File"/> is set for Found and NotInEncoding.</summary>
public sealed record FileStat(FileLookupState State, BuildFile? File);

/// <summary>
/// What <see cref="IBuildFiles.ListAsync"/> returns. Every set criterion must hold. <see cref="FromId"/> and <see cref="ToId"/>
/// are inclusive. <see cref="PathPrefix"/> matches only named files (case and slash direction don't matter); a null set means
/// any value. Sets compare by reference, so two equal filters aren't equal records.
/// </summary>
public sealed record FileFilter
{
    public static FileFilter All { get; } = new();

    public uint FromId { get; init; }
    public uint ToId { get; init; } = uint.MaxValue;
    public string? PathPrefix { get; init; }
    public IReadOnlySet<FileFormat>? Formats { get; init; }
    public IReadOnlySet<FileStorage>? Storage { get; init; }
    public bool? Encrypted { get; init; }
}

/// <summary>How opening a build and locale ended. Every failure but <see cref="LocaleNotInBuild"/> is layer 3's open state.</summary>
public enum FilesOpenState
{
    Open,
    NeedsNetwork,
    Unavailable,
    Missing,
    Encrypted,
    Corrupt,
    Unsupported,
    StoreFailed,
    /// <summary>The build opened, but its ROOT has no file for this locale.</summary>
    LocaleNotInBuild,
}

/// <summary>
/// The outcome of <see cref="IFileSystem.ForAsync"/>. <see cref="Files"/> is set only when <see cref="State"/> is Open. On a
/// layer 3 failure, <see cref="Code"/> and <see cref="ObservedAt"/> are layer 3's.
/// </summary>
public sealed record FilesOpenResult(BuildRef Build, string Locale, FilesOpenState State, IBuildFiles? Files, string? Code, string? Message,
    DateTimeOffset? ObservedAt);

/// <summary>The locale names a handle can be bound to, with their ROOT locale bits. The same table as layer 3's locale names.</summary>
public static class FileLocales
{
    private static readonly Dictionary<string, uint> Bits = new(StringComparer.Ordinal)
    {
        ["enUS"] = 0x2, ["koKR"] = 0x4, ["frFR"] = 0x10, ["deDE"] = 0x20, ["zhCN"] = 0x40, ["esES"] = 0x80, ["zhTW"] = 0x100,
        ["enGB"] = 0x200, ["enCN"] = 0x400, ["enTW"] = 0x800, ["esMX"] = 0x1000, ["ruRU"] = 0x2000, ["ptBR"] = 0x4000,
        ["itIT"] = 0x8000, ["ptPT"] = 0x10000,
    };

    public static IReadOnlyCollection<string> Names => Bits.Keys;

    public static bool IsKnown(string? locale) => locale is not null && Bits.ContainsKey(locale);

    /// <summary>The ROOT locale bit for a registered name; throws <see cref="ArgumentException"/> otherwise.</summary>
    public static uint BitOf(string locale) =>
        locale is not null && Bits.TryGetValue(locale, out var bit) ? bit : throw new ArgumentException("The locale isn't registered.", nameof(locale));
}
