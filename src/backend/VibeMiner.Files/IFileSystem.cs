using VibeMiner.Builds;
using VibeMiner.Files.Names;

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

    /// <summary>
    /// The community file names (D105) every handle of this file system reads: <see cref="BuildFile.Path"/>, path lookups and
    /// <see cref="FileFilter.PathPrefix"/>. Handles only use the copy already loaded; <see cref="IFileNames.UpdateAsync"/> fetches.
    /// A handle reads the names once per list batch and per stat; reads and <see cref="IBuildFiles.ExistsAsync"/> by path only
    /// when ROOT's name hash misses; reads and exists checks by FileDataID never read them.
    /// </summary>
    IFileNames Names { get; }
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
    /// Formats are found only for files that pass the other criteria, a few local reads at a time (see <see cref="BuildFile"/>).
    /// </summary>
    IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// One file by FileDataID or path. Never fetches content (a stored file's first bytes are read locally for its format, see
    /// <see cref="BuildFile"/>); a layer 2 failure to check stored state throws.
    /// </summary>
    Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken);

    /// <summary>
    /// True when this build lists the file for this locale (<see cref="FileLookupState.Found"/> or
    /// <see cref="FileLookupState.NotInEncoding"/>). Says nothing about stored state.
    /// </summary>
    Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken);

    /// <summary>
    /// The whole file's decoded bytes, for the variant <see cref="StatAsync"/> reports. Fetches the encoded copy through layer 2
    /// when it isn't stored. A file over <see cref="FileSystemOptions.MaxReadBytes"/> is <see cref="FileReadState.TooLarge"/>;
    /// use <see cref="OpenReadAsync"/>. A Complete whole-file read has also matched the file's content key.
    /// </summary>
    Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken);

    /// <summary>
    /// <paramref name="length"/> decoded bytes from <paramref name="offset"/>, clamped to the end of the file (an offset at or
    /// past the end reads 0 bytes, Complete, at the file's end, and fetches nothing). The end and the TooLarge check use the size
    /// ENCODING declares, before any fetch; bytes whose BLTE layout disagrees with it are Corrupt. Decodes only the BLTE chunks the range overlaps, so a small range of a big file is
    /// cheap once the encoded copy is stored. <paramref name="length"/> over <see cref="FileSystemOptions.MaxReadBytes"/> throws
    /// <see cref="ArgumentOutOfRangeException"/>. A range that isn't the whole file can't be checked against the content key;
    /// its chunks are checked against their own checksums.
    /// </summary>
    Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken);

    /// <summary>
    /// A read-only seekable stream over the decoded file, with its holes listed up front. On a failure the stream is null and the
    /// state says why. Reads are async only (<see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>); the stream decodes
    /// one chunk at a time and holds at most one decoded chunk. A read stops short at the start of a hole, and a read that starts
    /// inside one throws <see cref="FileHoleException"/>. Bytes that fail their checks mid-stream throw <see cref="FileReadException"/>.
    /// The stream outlives the handle, isn't safe for concurrent use, and must be disposed.
    /// </summary>
    Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken);
}

/// <summary>
/// How a read ended. Complete, Partial and Protected carry bytes or named holes; every other state carries none. The lookup
/// states are <see cref="FileLookupState"/>'s. Missing, NeedsNetwork, Unavailable and StoreFailed are layer 2's (Missing is its
/// dated 404, never proof of absence). Corrupt means bytes or declarations failed a check; Unsupported means a well-formed
/// layout or mode this code doesn't read. Bytes decrypted with a known key are proven only by that key (and, on a whole-file
/// read, by the content key).
/// </summary>
public enum FileReadState
{
    /// <summary>Every byte of the range.</summary>
    Complete,
    /// <summary>Some bytes, with the rest in named holes.</summary>
    Partial,
    /// <summary>Every byte of the range lies in holes: no bytes.</summary>
    Protected,
    UnknownFile,
    NotInLocale,
    NotInEncoding,
    Missing,
    /// <summary>Not stored, and the network is off (layer 2 is local only).</summary>
    NeedsNetwork,
    Unavailable,
    Corrupt,
    Unsupported,
    /// <summary>
    /// A known TACT key didn't decrypt its chunk (Salsa20 has no MAC, so this shows as a bad inner mode, zlib or length after the
    /// chunk checksum already proved the encrypted bytes): the key is wrong, not the data.
    /// </summary>
    WrongKey,
    StoreFailed,
    /// <summary>The whole file is over <see cref="FileSystemOptions.MaxReadBytes"/>; read it by range or as a stream.</summary>
    TooLarge,
}

/// <summary>
/// A decoded range this build can't read: an encrypted BLTE chunk whose TACT key isn't known. <see cref="KeyName"/> is the
/// key's name as 16 uppercase hex digits (the 8 name bytes read as a little-endian integer), the same spelling as DB2 section keys.
/// </summary>
public sealed record FileHole(long Offset, long Length, string KeyName);

/// <summary>Decoded bytes starting at <see cref="Offset"/> of the file.</summary>
public sealed record FileSegment(long Offset, ReadOnlyMemory<byte> Bytes);

/// <summary>
/// The outcome of a read. <see cref="Offset"/> and <see cref="Length"/> are the range read, both clamped to the file; on a state
/// that carries nothing, <see cref="Offset"/> is the offset asked for and <see cref="Length"/> is 0. Every hole the range
/// overlaps is listed, clipped to the range. <see cref="Segments"/> and <see cref="Holes"/> are ascending, don't overlap, and
/// together cover the range exactly: Complete has segments only, Protected holes only, Partial both. Segments are maximal (no two
/// are adjacent; a Complete read of more than 0 bytes has one), and so are holes of one key. Bytes are never
/// zero-filled. <see cref="Code"/>, <see cref="Message"/> and <see cref="ObservedAt"/> explain a failure (layer 2's code and
/// time where it came from there).
/// </summary>
public sealed record FileRead(FileReadState State, long Offset, long Length, IReadOnlyList<FileSegment> Segments, IReadOnlyList<FileHole> Holes,
    string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null)
{
    /// <summary>The bytes of a Complete read, else null.</summary>
    public ReadOnlyMemory<byte>? Bytes => State != FileReadState.Complete ? null
        : Segments.Count == 0 ? ReadOnlyMemory<byte>.Empty : (ReadOnlyMemory<byte>?)Segments[0].Bytes;

    public static FileRead Failed(FileReadState state, long offset, string? code, string? message, DateTimeOffset? observedAt = null) =>
        state is FileReadState.Complete or FileReadState.Partial or FileReadState.Protected
            ? throw new ArgumentException("A failed read can't carry bytes or holes.", nameof(state))
            : new(state, offset, 0, [], [], code, message, observedAt);
}

/// <summary>
/// The outcome of <see cref="IBuildFiles.OpenReadAsync"/>. <see cref="Stream"/> is set for Complete (no holes), Partial (some)
/// and Protected (the whole file is holes); every other state has none.
/// </summary>
public sealed record FileOpen(FileReadState State, FileReadStream? Stream, string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null);

/// <summary>A read-only, seekable, async-only stream over one decoded file. <see cref="Stream.Length"/> is the decoded size.</summary>
public abstract class FileReadStream : Stream
{
    /// <summary>Every hole of the file, ascending, known when the stream opened. Still readable after dispose.</summary>
    public abstract IReadOnlyList<FileHole> Holes { get; }
}

/// <summary>A stream read started inside a hole.</summary>
public sealed class FileHoleException(FileHole hole) : IOException($"The range at {hole.Offset} needs TACT key {hole.KeyName}.")
{
    public FileHole Hole { get; } = hole;
}

/// <summary>
/// A stream read failed with a read state other than a hole (Corrupt, Unsupported, a layer 2 failure). <see cref="ObservedAt"/>
/// is layer 2's time where the failure came from there.
/// </summary>
public sealed class FileReadException(FileReadState state, string? code, string? message, DateTimeOffset? observedAt = null)
    : IOException(message ?? code ?? state.ToString())
{
    public FileReadState State { get; } = state;
    public string? Code { get; } = code;
    public DateTimeOffset? ObservedAt { get; } = observedAt;
}

/// <summary>
/// The TACT keys a file system decrypts with: 16-byte keys by key name (the little-endian 8 name bytes). There is no default
/// key list; a key not given leaves its chunks as holes.
/// </summary>
public interface ITactKeys
{
    /// <summary>The 16-byte key named <paramref name="keyName"/>, or null when it isn't known.</summary>
    ReadOnlyMemory<byte>? Find(ulong keyName);
}

/// <summary>A fixed set of TACT keys.</summary>
public sealed class TactKeys : ITactKeys
{
    private readonly Dictionary<ulong, byte[]> keys = [];

    public static ITactKeys None { get; } = new TactKeys();

    public TactKeys Add(ulong keyName, ReadOnlySpan<byte> key)
    {
        if (key.Length != 16) throw new ArgumentException("A TACT key is 16 bytes.", nameof(key));
        keys[keyName] = key.ToArray();
        return this;
    }

    public ReadOnlyMemory<byte>? Find(ulong keyName) => keys.TryGetValue(keyName, out var key) ? key : (ReadOnlyMemory<byte>?)null;
}

/// <summary>
/// A file asked for by FileDataID or by path (ASCII case and slash direction don't matter). A path is looked up first by ROOT's
/// name hash; when ROOT doesn't name it, through the community listfile (D105) when names are on and loaded. ROOT wins when
/// both name a file. Since 8.2 most ROOT entries have no name hash, so the listfile resolves most path lookups. When the
/// listfile gives one name to several FileDataIDs, only those this build lists in this locale count (or, when none is in
/// the locale, those it lists at all), and of those the highest FileDataID is used (usually the newest). A listfile name
/// whose FileDataIDs this build doesn't list is <see cref="FileLookupState.UnknownFile"/>, not an error.
/// </summary>
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

/// <summary>Where <see cref="BuildFile.Path"/> came from.</summary>
public enum FilePathSource
{
    /// <summary>No name: names are off or not loaded, or the listfile doesn't name the file.</summary>
    None,
    /// <summary>The community listfile's name for the FileDataID.</summary>
    Listfile,
    /// <summary>The path asked for, as given, which ROOT's name hash matched; the listfile doesn't name the file. Stat only.</summary>
    RootHash,
}

/// <summary>Whether a file's bytes are in the profile's object store.</summary>
public enum FileStorage
{
    Stored,
    NotStored,
    /// <summary>The build has no encoded copy to look for (ENCODING doesn't list the file's content key).</summary>
    NoEncodedCopy,
}

/// <summary>
/// A file's format, from its first decoded bytes where a magic proves it, else from the listfile name's extension. See
/// <see cref="FileFormatSource"/> for which one said so.
/// </summary>
public enum FileFormat
{
    /// <summary>Neither the bytes nor the name say what the file is.</summary>
    Unknown = 0,
    /// <summary>A DB2 or DBC client table (WDBC through WDC5, 1SLC).</summary>
    Db2,
    /// <summary>A BLP texture.</summary>
    Blp,
    /// <summary>An M2 model by its .m2 name only: the bytes didn't say whether it is chunked or legacy.</summary>
    M2,
    /// <summary>A chunked M2 model (MD21 wrapping MD20).</summary>
    M2Chunked,
    /// <summary>A pre-chunked M2 model (bare MD20).</summary>
    M2Legacy,
    /// <summary>An M2 skin profile (.skin).</summary>
    Skin,
    /// <summary>An external M2 animation (.anim).</summary>
    Anim,
    /// <summary>M2 bone data (.bone).</summary>
    Bone,
    /// <summary>M2 physics data (.phys).</summary>
    Phys,
    /// <summary>An M2 skeleton (.skel).</summary>
    Skel,
    /// <summary>Texture info (.tex).</summary>
    Tex,
    /// <summary>A WMO root file.</summary>
    WmoRoot,
    /// <summary>A WMO group file (name ends _NNN or _NNN_lodN).</summary>
    WmoGroup,
    /// <summary>An ADT root terrain tile.</summary>
    AdtRoot,
    /// <summary>An ADT texture split file (_texN).</summary>
    AdtTex,
    /// <summary>An ADT object split file (_objN).</summary>
    AdtObj,
    /// <summary>An ADT level-of-detail file (_lod).</summary>
    AdtLod,
    /// <summary>A WDT map file.</summary>
    Wdt,
    /// <summary>A WDT split file (_occ, _lgt, _fogs, _mpv, _preload).</summary>
    WdtSplit,
    /// <summary>A WDL low-resolution world file.</summary>
    Wdl,
    /// <summary>A compiled shader (.bls).</summary>
    Bls,
    /// <summary>A .wwf data file.</summary>
    Wwf,
    /// <summary>Ogg audio.</summary>
    Ogg,
    /// <summary>MP3 audio.</summary>
    Mp3,
    /// <summary>RIFF WAVE audio.</summary>
    Wav,
    /// <summary>A TrueType font.</summary>
    Ttf,
    /// <summary>An OpenType font with CFF outlines.</summary>
    Otf,
    /// <summary>A TrueType font collection.</summary>
    Ttc,
    /// <summary>A PNG image.</summary>
    Png,
    /// <summary>A JPEG image.</summary>
    Jpeg,
    /// <summary>Lua source.</summary>
    Lua,
    /// <summary>XML (.xml, .xsd).</summary>
    Xml,
    /// <summary>An addon table of contents (.toc).</summary>
    Toc,
    /// <summary>Plain text.</summary>
    Txt,
    /// <summary>HTML.</summary>
    Html,
}

/// <summary>What said a file's <see cref="FileFormat"/>.</summary>
public enum FileFormatSource
{
    /// <summary>Neither the bytes nor the name said anything; the format is Unknown.</summary>
    None,
    /// <summary>The first decoded bytes proved the format, whatever the name says.</summary>
    Magic,
    /// <summary>The listfile name's extension only; the bytes weren't read or didn't prove anything.</summary>
    Extension,
}

/// <summary>
/// One file in one build and locale. A file can have several ROOT variants in one locale (low violence, platform, other
/// content flags); every field comes from the one variant this locale reads, the first in ROOT order within the best of:
/// ENCODING lists it and it is neither low violence (content flag 0x80) nor a high-resolution texture (0x1); ENCODING lists it
/// and it isn't low violence; ENCODING lists it; any in the locale. Reads (#172) use the same variant. <see cref="Storage"/>
/// checks the first encoded key ENCODING lists for the content.
/// <see cref="Path"/> is a community label from the loaded wow-listfile release (<see cref="IFileNames.StatusAsync"/> gives
/// its tag), not Blizzard data, and the same for every build: it can be a placeholder and can change when a newer release is
/// loaded, so never key on it, and it doesn't prove the build has a file at that path. <see cref="PathSource"/> says where it
/// came from: the listfile name when there is one (even when a lookup by path hit ROOT's name hash under another
/// spelling); else, for a stat by a path ROOT's name hash matched, that path as the caller gave it; else null. Listed files
/// only ever carry a listfile name or none. <see cref="Size"/> is the decoded size ENCODING declares. <see cref="ContentKey"/> is the decoded
/// content's MD5 (the CKey) as 32 lowercase hex digits: content identity, the same for the same bytes in any build, so build
/// diffs can compare it. It is never an encoding key or a TACT key. <see cref="Encrypted"/> is ROOT's encrypted content flag
/// (0x8000000), a hint; whether the file can be read comes with reading (#172). <see cref="Format"/> is the magic of the first
/// decoded bytes when they prove one, else the extension of <see cref="Path"/>, else Unknown; <see cref="FormatSource"/> says
/// which. The bytes are read only when the file is stored, from the store alone (never fetched), and only the first BLTE chunk
/// is decoded. A file not stored, or whose first bytes can't be read (an encrypted first chunk without its key, corrupt, a
/// layer 2 failure), gets its extension's format; that is a state, never an error. What the bytes said is remembered per
/// handle, so a file is read for this at most once; a whole read (<see cref="IBuildFiles.GetAsync"/>) also refreshes it.
/// </summary>
public sealed record BuildFile(uint FileDataId, string? Path, FilePathSource PathSource, long? Size, string ContentKey, FileFormat Format, FileFormatSource FormatSource,
    FileStorage Storage, bool Encrypted);

public enum FileLookupState
{
    Found,
    /// <summary>
    /// The build doesn't list the FileDataID, or neither ROOT's name hash nor the loaded listfile maps the path to a file this
    /// build lists. Names can be off, not loaded yet or incomplete, so a path that misses doesn't prove the file is absent.
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
/// are inclusive. <see cref="PathPrefix"/> matches the start of <see cref="BuildFile.Path"/>, so only files the loaded listfile
/// names (ASCII case and slash direction don't matter); <c>""</c> matches every named file. Names are read once per batch:
/// with names off or not loaded it matches nothing, and when they go off mid-listing the listing ends at the next batch
/// boundary (files already read for the current batch are still returned). A null set means
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
