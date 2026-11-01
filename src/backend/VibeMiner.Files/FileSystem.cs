using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Files.Formats;
using VibeMiner.Files.Names;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Files;

public sealed record FileSystemOptions
{
    /// <summary>Files whose stored state one layer 2 call checks while listing, and files one ensure batch fetches.</summary>
    public int PresenceBatch { get; init; } = 4096;
    /// <summary>The least time between two <see cref="IBuildFiles.EnsureAsync"/> progress reports (the final one always goes); positive.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Times the progress reports.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>The most decoded bytes one <see cref="IBuildFiles.GetAsync"/> or <see cref="IBuildFiles.ReadRangeAsync"/> returns.</summary>
    public long MaxReadBytes { get; init; } = 256L << 20;
    /// <summary>The TACT keys encrypted chunks are decrypted with.</summary>
    public ITactKeys Keys { get; init; } = TactKeys.None;
    /// <summary>The community file names switch (D105); null leaves names off.</summary>
    public IFileNameSwitch? Names { get; init; }
    /// <summary>The generated store the names index and its download staging live in (#186); null leaves names off.</summary>
    public IGeneratedStore? NamesStore { get; init; }
}

/// <summary>
/// The real <see cref="IFileSystem"/>: opens builds through layer 3 and asks layer 2 which encoded copies are stored. A handle
/// holds the opened <see cref="IBuild"/>, so a file is a ROOT and ENCODING lookup in memory plus a batched index check. One
/// <see cref="FileNames"/> over the same layer 2 source and <see cref="FileSystemOptions.NamesStore"/> names files for every handle;
/// handles never fetch names, and a names read that fails only leaves names out.
/// </summary>
public sealed class FileSystem : IFileSystem
{
    private readonly IBuilds builds;
    private readonly IObjectSource objects;
    private readonly FileSystemOptions options;
    private readonly FileNames names;

    public FileSystem(IBuilds builds, IObjectSource objects, FileSystemOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builds); ArgumentNullException.ThrowIfNull(objects);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PresenceBatch, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxReadBytes, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxReadBytes, Array.MaxLength, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Keys, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ProgressInterval, TimeSpan.Zero, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Time, nameof(options));
        this.builds = builds; this.objects = objects; this.options = options;
        names = new FileNames(objects, options.NamesStore, options.Names);
    }

    public IFileNames Names => names;

    public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        var bit = FileLocales.BitOf(locale);
        var opened = await builds.OpenAsync(build, cancellationToken).ConfigureAwait(false);
        if (opened.State != BuildOpenState.Open || opened.Opened is not { } built)
            return new(build, locale, Map(opened.State), null, opened.Code, opened.Message, opened.ObservedAt);
        if (!built.Metadata.Locales.Contains(locale))
            return new(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", $"The build has no {locale} files.", null);
        return new(build, locale, FilesOpenState.Open, new BuildFiles(built, locale, bit, objects, options, names), null, null, null);
    }

    private static FilesOpenState Map(BuildOpenState state) => state switch
    {
        BuildOpenState.NeedsNetwork => FilesOpenState.NeedsNetwork,
        BuildOpenState.Unavailable => FilesOpenState.Unavailable,
        BuildOpenState.Missing => FilesOpenState.Missing,
        BuildOpenState.Encrypted => FilesOpenState.Encrypted,
        BuildOpenState.Corrupt => FilesOpenState.Corrupt,
        BuildOpenState.Unsupported => FilesOpenState.Unsupported,
        BuildOpenState.StoreFailed => FilesOpenState.StoreFailed,
        _ => throw new InvalidOperationException($"An open without a build can't be {state}."),
    };
}

/// <summary>How one locale picks a file's variant from its ROOT memberships.</summary>
internal static class Variants
{
    /// <summary>ROOT content flag: the file is encrypted.</summary>
    public const ulong Encrypted = 0x8000000;
    /// <summary>ROOT content flag: the low-violence alternative, used only when nothing else is in the locale.</summary>
    public const ulong LowViolence = 0x80;
    /// <summary>ROOT content flag: the optional high-resolution copy of a texture (D095).</summary>
    public const ulong HighResTexture = 0x1;

    /// <summary>
    /// The variant this locale reads, first in ROOT order within the best rank: ENCODING lists it and it is neither low
    /// violence nor high resolution; then ENCODING lists it and it isn't low violence; then ENCODING lists it; then any in the
    /// locale. Null when none is in the locale.
    /// </summary>
    public static FileVariant? Pick(IReadOnlyList<FileVariant> variants, uint localeBit)
    {
        FileVariant? best = null;
        var bestRank = int.MaxValue;
        foreach (var v in variants)
        {
            if ((v.LocaleFlags & localeBit) == 0) continue;
            var rank = v.EKey is null ? 3 : (v.ContentFlags & LowViolence) != 0 ? 2 : (v.ContentFlags & HighResTexture) != 0 ? 1 : 0;
            if (rank == 0) return v;
            if (rank < bestRank) { best = v; bestRank = rank; }
        }
        return best;
    }

    public static ObjectKey KeyOf(FileVariant variant) => new(ObjectKeyType.Encoded, variant.EKey!.Value.ToBytes());

    /// <summary>The layer 2 request for an encoded variant: its EKey, the encoded size ENCODING declares and its archive location.</summary>
    public static ObjectRequest RequestOf(FileVariant variant) => new(KeyOf(variant), variant.EncodedSize, variant.Location is { } l ? [l] : null);
}

internal sealed partial class BuildFiles : IBuildFiles
{
    /// <summary>The most files whose byte evidence one handle remembers by default; past it, nothing more is added.</summary>
    internal const int MaxRememberedFormats = 1_000_000;
    /// <summary>A first BLTE chunk over this many encoded or decoded bytes isn't read for format detection.</summary>
    internal const int DetectionChunkBytes = 1 << 20;
    /// <summary>The most format detection reads one listing runs at once.</summary>
    internal const int DetectionReads = 8;

    private readonly uint localeBit;
    private readonly IObjectSource objects;
    private readonly int batch;
    private readonly FileSystemOptions options;
    private readonly LayoutCache layouts = new();
    private readonly FileNames names;
    private readonly Action counted;
    private IBuild? build;
    private long decodedChunks;
    // What a file's first decoded bytes proved, by FileDataID: a strong format, or Unknown when they were read and proved
    // nothing. Only byte results are kept; the name is applied on every answer.
    private readonly ConcurrentDictionary<uint, FileFormat> proven = new();
    private int provenCount;

    /// <summary>The most files this handle remembers byte evidence for (tests lower it).</summary>
    internal int RememberLimit { get; set; } = MaxRememberedFormats;

    /// <summary>How many files this handle remembers byte evidence for.</summary>
    internal int Remembered => Volatile.Read(ref provenCount);

    public BuildFiles(IBuild opened, string locale, uint localeBit, IObjectSource objects, FileSystemOptions options, FileNames names)
    {
        build = opened; this.names = names; Build = opened.Build; Locale = locale;
        this.localeBit = localeBit; this.objects = objects; this.options = options; batch = options.PresenceBatch;
        counted = () => Interlocked.Increment(ref decodedChunks);
    }

    public BuildRef Build { get; }
    public string Locale { get; }

    /// <summary>BLTE chunks decoded by this handle's reads so far.</summary>
    internal long DecodedChunks => Interlocked.Read(ref decodedChunks);

    public ValueTask DisposeAsync() { Volatile.Write(ref build, null); return ValueTask.CompletedTask; }

    private IBuild Opened => Volatile.Read(ref build) ?? throw new ObjectDisposedException(nameof(IBuildFiles), "The file handle is disposed.");

    public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return ListFilesAsync(filter, cancellationToken);
    }

    private async IAsyncEnumerable<BuildFile> ListFilesAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var (file, _) in ListEntriesAsync(Opened, filter, detectFormats: true, cancellationToken).ConfigureAwait(false))
            yield return file;
    }

    /// <summary>
    /// The listing with each file's variant. Without <paramref name="detectFormats"/> and with no format filter, no format is
    /// detected (every file's format is left Unknown, source None), so no stored bytes are read.
    /// </summary>
    private async IAsyncEnumerable<(BuildFile File, FileVariant Variant)> ListEntriesAsync(IBuild build, FileFilter filter, bool detectFormats,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (filter.FromId > filter.ToId) yield break;
        if (filter.Formats is { Count: 0 }) yield break;
        var prefix = filter.PathPrefix;

        var pending = new List<(uint Id, FileVariant Variant)>(batch);
        var pendingNames = new List<string?>(batch);
        using var ids = build.FileDataIds(Locale, filter.FromId).GetEnumerator();
        // Names walk beside the build's ids (both ascend), so naming a batch is a merge, not a lookup per file.
        await using var named = new NameCursor(cancellationToken);
        var more = true;
        var walked = 0;
        // The first FileDataID the next batch can hold.
        ulong next = filter.FromId;
        while (more)
        {
            // The names as they are now, read once per batch; a newer copy restarts the walk where the listing is.
            var index = await names.CurrentAsync(cancellationToken).ConfigureAwait(false);
            if (prefix is not null && index is null) yield break;
            await named.UseAsync(index, next).ConfigureAwait(false);
            pending.Clear();
            pendingNames.Clear();
            while (pending.Count < batch && (more = ids.MoveNext()))
            {
                // A narrow filter can walk far between batches; stay cancellable.
                if ((++walked & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var id = ids.Current;
                if (id > filter.ToId) { more = false; break; }
                next = (ulong)id + 1;
                var name = await named.NameOfAsync(id).ConfigureAwait(false);
                // A names read that failed: with a prefix nothing more can match, so the listing ends here.
                if (prefix is not null && named.Failed) { more = false; break; }
                if (prefix is not null && (name is null || !NameFolding.HasPrefix(name, prefix))) continue;
                if (Variants.Pick(build.Resolve(id).Variants, localeBit) is not { } variant) continue;
                if (filter.Encrypted is { } encrypted && IsEncrypted(variant) != encrypted) continue;
                pending.Add((id, variant));
                pendingNames.Add(name);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.Count == 0) continue;

            var storage = await StorageOfAsync(pending, cancellationToken).ConfigureAwait(false);
            if (!detectFormats && filter.Formats is null)
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    if (filter.Storage is { } wanted && !wanted.Contains(storage[i])) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return (ToFile(pending[i].Id, pending[i].Variant, storage[i], pendingNames[i], pendingNames[i] is null ? FilePathSource.None : FilePathSource.Listfile),
                        pending[i].Variant);
                }
                continue;
            }
            // Formats of the files that pass the other filters, at most DetectionReads ahead of the reader, yielded in order.
            using var detection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var window = new Queue<(BuildFile File, FileVariant Variant, ValueTask<(FileFormat, FileFormatSource)> Format)>(DetectionReads);
            try
            {
                for (var i = 0; i <= pending.Count; i++)
                {
                    if (i < pending.Count)
                    {
                        if (filter.Storage is { } wanted && !wanted.Contains(storage[i])) continue;
                        var file = ToFile(pending[i].Id, pending[i].Variant, storage[i], pendingNames[i], pendingNames[i] is null ? FilePathSource.None : FilePathSource.Listfile);
                        window.Enqueue((file, pending[i].Variant, FormatOfAsync(file, pending[i].Variant, detection.Token)));
                        if (window.Count < DetectionReads) continue;
                    }
                    while (window.Count > 0 && (i == pending.Count || window.Count >= DetectionReads))
                    {
                        var (file, variant, format) = window.Dequeue();
                        var (detected, source) = await format.ConfigureAwait(false);
                        if (filter.Formats is { } formats && !formats.Contains(detected)) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return (file with { Format = detected, FormatSource = source }, variant);
                    }
                }
            }
            finally
            {
                // A reader that stops early leaves detection reads running: stop them and wait, so none outlives the listing.
                if (window.Count > 0)
                {
                    await detection.CancelAsync().ConfigureAwait(false);
                    while (window.Count > 0)
                    {
                        try { await window.Dequeue().Format.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
        }
    }

    public async Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken)
    {
        var build = Opened;
        ThrowIfEmpty(file);
        cancellationToken.ThrowIfCancellationRequested();
        // The names as they are now, read once for the lookup and the label.
        var index = await names.CurrentAsync(cancellationToken).ConfigureAwait(false);
        var found = await FindAsync(build, file, index, cancellationToken).ConfigureAwait(false);
        var resolved = found.Resolution;
        if (resolved.State == FileResolutionState.UnknownFile || resolved.FileDataId is not { } fileDataId) return new(FileLookupState.UnknownFile, null);
        if (Variants.Pick(resolved.Variants, localeBit) is not { } variant) return new(FileLookupState.NotInLocale, null);
        var (path, source) = index is not null && await index.NameOfAsync(fileDataId, cancellationToken).ConfigureAwait(false) is { } name ? (name, FilePathSource.Listfile)
            : found.ByRootHash ? (file.Path, FilePathSource.RootHash) : (null, FilePathSource.None);
        var storage = await StorageOfAsync([(fileDataId, variant)], cancellationToken).ConfigureAwait(false);
        var stat = ToFile(fileDataId, variant, storage[0], path, source);
        var (format, formatSource) = await FormatOfAsync(stat, variant, cancellationToken).ConfigureAwait(false);
        return new(variant.EKey is null ? FileLookupState.NotInEncoding : FileLookupState.Found, stat with { Format = format, FormatSource = formatSource });
    }

    public async Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken)
    {
        var build = Opened;
        ThrowIfEmpty(file);
        cancellationToken.ThrowIfCancellationRequested();
        // Lookups in memory only: names are read only when ROOT doesn't know the path, and nothing asks layer 2 for objects.
        var resolved = (await FindAsync(build, file, cancellationToken).ConfigureAwait(false)).Resolution;
        return resolved.State != FileResolutionState.UnknownFile && Variants.Pick(resolved.Variants, localeBit) is not null;
    }

    public async Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken)
    {
        var build = Opened;
        ThrowIfEmpty(file);
        cancellationToken.ThrowIfCancellationRequested();
        var (reader, failed, fileDataId) = ReaderFor(build, await FindAsync(build, file, cancellationToken).ConfigureAwait(false), 0);
        if (reader is null) return failed!;
        if (reader.Size > options.MaxReadBytes)
            return FileRead.Failed(FileReadState.TooLarge, 0, "FILES_READ_TOO_LARGE", $"The file is {reader.Size} bytes, over the {options.MaxReadBytes} a whole read returns.");
        // An empty file needs no fetch, but still has to be the content ENCODING names.
        var read = reader.Size == 0 ? new FileRead(FileReadState.Complete, 0, 0, [], [])
            : await ReadAsync(reader, 0, (int)reader.Size, 0, cancellationToken).ConfigureAwait(false);
        if (read.State == FileReadState.Complete && !reader.MatchesContentKey(read.Bytes!.Value.Span))
            return FileRead.Failed(FileReadState.Corrupt, 0, "BLTE_INTEGRITY", "The decoded file doesn't match its content key.");
        // The bytes are here: remember what they say, so the next list or stat needs no detection read.
        if (HeadOf(read) is { } head) Remember(fileDataId, head.Span, reader.Size);
        return read;
    }

    public async Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken)
    {
        var build = Opened;
        ThrowIfEmpty(file);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, options.MaxReadBytes);
        cancellationToken.ThrowIfCancellationRequested();
        var (reader, failed, _) = ReaderFor(build, await FindAsync(build, file, cancellationToken).ConfigureAwait(false), offset);
        if (reader is null) return failed!;
        // Clamped to the declared size before any fetch; an empty range fetches nothing.
        var start = Math.Min(offset, reader.Size);
        var clamped = (int)Math.Min(length, reader.Size - start);
        if (clamped == 0) return new(FileReadState.Complete, start, 0, [], []);
        return await ReadAsync(reader, start, clamped, offset, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken)
    {
        var build = Opened;
        ThrowIfEmpty(file);
        cancellationToken.ThrowIfCancellationRequested();
        var (reader, failed, _) = ReaderFor(build, await FindAsync(build, file, cancellationToken).ConfigureAwait(false), 0);
        if (reader is null) return new(failed!.State, null, failed.Code, failed.Message, failed.ObservedAt);
        // An empty file needs no fetch, but still has to be the content ENCODING names.
        if (reader.Size == 0 && !reader.MatchesContentKey([]))
            return new(FileReadState.Corrupt, null, "BLTE_INTEGRITY", "The decoded file doesn't match its content key.");
        try { return await BuildFileStream.OpenAsync(reader, cancellationToken).ConfigureAwait(false); }
        catch (FileReadFailure f) { return new(f.State, null, f.Code, f.Message, f.ObservedAt); }
        catch (BlteFormatException b) { return new(b.State, null, b.Code, b.Message); }
    }

    /// <summary>A lookup's resolution, and whether a path matched ROOT's name hash.</summary>
    private readonly record struct Found(FileResolution Resolution, bool ByRootHash);

    /// <summary>The lookup below, reading the names only when ROOT's name hash misses the path.</summary>
    private async ValueTask<Found> FindAsync(IBuild build, FileRef file, CancellationToken cancellationToken)
    {
        if (file.Path is not { } path) return await FindAsync(build, file, null, cancellationToken).ConfigureAwait(false);
        var byRoot = build.Resolve(path);
        if (byRoot.State != FileResolutionState.UnknownFile) return new(byRoot, true);
        var index = await names.CurrentAsync(cancellationToken).ConfigureAwait(false);
        return await FromListfileAsync(build, path, index, cancellationToken).ConfigureAwait(false) is { } listed ? new(listed, false) : new(byRoot, false);
    }

    /// <summary>
    /// A FileDataID resolves as it is. A path resolves by ROOT's name hash first, then through <paramref name="index"/>: of the
    /// FileDataIDs it gives the name, those this build lists in this locale, else those it lists at all; the highest of them.
    /// </summary>
    private async ValueTask<Found> FindAsync(IBuild build, FileRef file, LoadedNames? index, CancellationToken cancellationToken)
    {
        if (file.FileDataId is { } id) return new(build.Resolve(id), false);
        var path = file.Path ?? throw new ArgumentException("An empty file reference.", nameof(file));
        var byRoot = build.Resolve(path);
        if (byRoot.State != FileResolutionState.UnknownFile) return new(byRoot, true);
        return await FromListfileAsync(build, path, index, cancellationToken).ConfigureAwait(false) is { } listed ? new(listed, false) : new(byRoot, false);
    }

    private async ValueTask<FileResolution?> FromListfileAsync(IBuild build, string path, LoadedNames? index, CancellationToken cancellationToken)
    {
        if (index is null) return null;
        FileResolution? inLocale = null, listed = null;
        // Ascending, so the last kept is the highest.
        foreach (var id in await index.IdsOfAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var resolved = build.Resolve(id);
            if (resolved.State == FileResolutionState.UnknownFile) continue;
            listed = resolved;
            if (Variants.Pick(resolved.Variants, localeBit) is not null) inLocale = resolved;
        }
        return inLocale ?? listed;
    }

    private static void ThrowIfEmpty(FileRef file)
    {
        if (file.FileDataId is null && file.Path is null) throw new ArgumentException("An empty file reference.", nameof(file));
    }

    /// <summary>
    /// The reader for the variant <see cref="StatAsync"/> reports, over the build's CDN context as it is now; or the lookup state,
    /// at <paramref name="offset"/>, that says why there is none.
    /// </summary>
    private (FileReader? Reader, FileRead? Failed, uint FileDataId) ReaderFor(IBuild build, Found found, long offset)
    {
        var resolved = found.Resolution;
        if (resolved.State == FileResolutionState.UnknownFile || resolved.FileDataId is not { } id)
            return (null, FileRead.Failed(FileReadState.UnknownFile, offset, null, "The file is UnknownFile."), 0);
        if (Variants.Pick(resolved.Variants, localeBit) is not { } variant)
            return (null, FileRead.Failed(FileReadState.NotInLocale, offset, null, "The file is NotInLocale."), id);
        if (variant.EKey is null) return (null, FileRead.Failed(FileReadState.NotInEncoding, offset, null, "The file is NotInEncoding."), id);
        if (variant.DecodedSize is null)
            return (null, FileRead.Failed(FileReadState.Corrupt, offset, "FILES_NO_DECLARED_SIZE", "ENCODING lists the file without a decoded size."), id);
        return (new FileReader(objects, variant, build.Cdn, options.Keys, layouts, options.MaxReadBytes, counted), null, id);
    }

    /// <summary>
    /// The file's format: what its first decoded bytes proved (remembered per handle), else what a local-only read of them
    /// proves now when the file is stored, else its name's extension, else Unknown. Never fails: bytes that can't be read
    /// locally (not stored, a hole or bad bytes at the start, a layer 2 failure) just aren't evidence.
    /// </summary>
    private async ValueTask<(FileFormat, FileFormatSource)> FormatOfAsync(BuildFile file, FileVariant variant, CancellationToken cancellationToken)
    {
        if (proven.TryGetValue(file.FileDataId, out var known)) return Answer(known, file.Path);
        if (file.Storage != FileStorage.Stored) return FormatDetection.Detect(null, file.Path);
        var head = await LocalHeadAsync(variant, cancellationToken).ConfigureAwait(false);
        return head is { } bytes ? Answer(Remember(file.FileDataId, bytes.Span, variant.DecodedSize ?? 0), file.Path)
            : FormatDetection.Detect(null, file.Path);
    }

    /// <summary>What a remembered byte result and the name say together: the same answer <see cref="FormatDetection.Detect"/> gives.</summary>
    private static (FileFormat, FileFormatSource) Answer(FileFormat proved, string? path) =>
        proved != FileFormat.Unknown ? (proved, FileFormatSource.Magic) : FormatDetection.Detect(null, path);

    /// <summary>
    /// What <paramref name="head"/> proves for a file of <paramref name="size"/> decoded bytes, remembered while there is room: a
    /// strong format, or Unknown only when the head is complete (min(<see cref="FormatDetection.HeadBytes"/>, size) bytes; a
    /// shorter one, cut by a hole, may prove more once the key is known). A strong entry is never replaced by Unknown.
    /// </summary>
    private FileFormat Remember(uint fileDataId, ReadOnlySpan<byte> head, long size)
    {
        var evidence = FormatDetection.FromBytes(head);
        var proved = evidence.Strong ? evidence.Format : FileFormat.Unknown;
        var old = proven.TryGetValue(fileDataId, out var known) ? known : (FileFormat?)null;
        if (proved == FileFormat.Unknown && (head.Length < Math.Min(FormatDetection.HeadBytes, size) || old is { } strong && strong != FileFormat.Unknown))
            return old ?? FileFormat.Unknown;
        if (old is not null) { if (old != proved) proven[fileDataId] = proved; }
        else if (Volatile.Read(ref provenCount) < RememberLimit && proven.TryAdd(fileDataId, proved)) Interlocked.Increment(ref provenCount);
        return proved;
    }

    /// <summary>
    /// Up to <see cref="FormatDetection.HeadBytes"/> leading decoded bytes from the store only: no CDN context, so layer 2 never
    /// fetches. Decodes only the chunks those bytes lie in (the first, for any real file); a first chunk over
    /// <see cref="DetectionChunkBytes"/> encoded or decoded isn't read for this. Null when the bytes before the first hole don't
    /// start at 0, or the read fails or is canceled by anything but <paramref name="cancellationToken"/>.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>?> LocalHeadAsync(FileVariant variant, CancellationToken cancellationToken)
    {
        if (variant.EKey is null || variant.DecodedSize is not { } size) return null;
        if (size == 0) return ReadOnlyMemory<byte>.Empty;
        var reader = new FileReader(objects, variant, null, options.Keys, layouts, options.MaxReadBytes, counted);
        try
        {
            var layout = await reader.LayoutAsync(cancellationToken).ConfigureAwait(false);
            if (layout.Chunks.Count == 0 || layout.Chunks[0].DecodedSize > DetectionChunkBytes || layout.Chunks[0].EncodedSize > DetectionChunkBytes)
                return null;
            return HeadOf(await reader.ReadAsync(0, (int)Math.Min(FormatDetection.HeadBytes, size), cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>The bytes from offset 0 up to the first hole of a read that starts at 0, or null when it has none.</summary>
    private static ReadOnlyMemory<byte>? HeadOf(FileRead read)
    {
        if (read.State is not (FileReadState.Complete or FileReadState.Partial) || read.Offset != 0) return null;
        if (read.Segments.Count == 0) return read.Length == 0 ? ReadOnlyMemory<byte>.Empty : null;
        return read.Segments[0].Offset == 0 ? read.Segments[0].Bytes : null;
    }

    /// <summary>A clamped, non-empty range; a failure is reported at <paramref name="askedOffset"/>.</summary>
    private static async Task<FileRead> ReadAsync(FileReader reader, long offset, int length, long askedOffset, CancellationToken cancellationToken)
    {
        try { return await reader.ReadAsync(offset, length, cancellationToken).ConfigureAwait(false); }
        catch (FileReadFailure f) { return FileRead.Failed(f.State, askedOffset, f.Code, f.Message, f.ObservedAt); }
        catch (BlteFormatException b) { return FileRead.Failed(b.State, askedOffset, b.Code, b.Message); }
    }

    /// <summary>
    /// One layer 2 index check for every file that has an encoded copy; the rest have no encoded copy. Only the first EKey
    /// ENCODING lists for the CKey is checked, so a store holding another EKey of the same content reads NotStored.
    /// </summary>
    private async Task<FileStorage[]> StorageOfAsync(IReadOnlyList<(uint Id, FileVariant Variant)> files, CancellationToken cancellationToken)
    {
        var result = new FileStorage[files.Count];
        var keys = new List<ObjectKey>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Variant.EKey is null) result[i] = FileStorage.NoEncodedCopy;
            else keys.Add(Variants.KeyOf(files[i].Variant));
        }
        if (keys.Count == 0) return result;
        var present = await objects.HasKeysAsync(keys, cancellationToken).ConfigureAwait(false);
        if (present.Count != keys.Count) throw new InvalidOperationException("Layer 2 answered a different number of keys than asked.");
        for (int i = 0, k = 0; i < files.Count; i++)
            if (files[i].Variant.EKey is not null) result[i] = present[k++] ? FileStorage.Stored : FileStorage.NotStored;
        return result;
    }

    private static bool IsEncrypted(FileVariant variant) => (variant.ContentFlags & Variants.Encrypted) != 0;

    /// <summary>The listed file with its format still unset; <see cref="FormatOfAsync"/> fills it in.</summary>
    private static BuildFile ToFile(uint id, FileVariant variant, FileStorage storage, string? path, FilePathSource pathSource) =>
        new(id, path, pathSource, variant.EKey is null ? null : variant.DecodedSize, variant.CKey.Hex, FileFormat.Unknown, FileFormatSource.None, storage,
            IsEncrypted(variant));
}

/// <summary>
/// Names for ascending FileDataIDs by walking <see cref="LoadedNames.FromAsync"/> beside them: one step per name passed, so a
/// listing names its files in one merge rather than a search each. One streaming read is kept open across batches and restarted
/// when the copy changes.
/// </summary>
internal sealed class NameCursor(CancellationToken cancellationToken) : IAsyncDisposable
{
    private LoadedNames? index;
    private IAsyncEnumerator<(uint Id, string Name)>? names;
    private bool has;

    /// <summary>Whether a read of the copy being walked failed, so names past it are missing.</summary>
    public bool Failed => index?.IsDamaged == true;

    /// <summary>Walks <paramref name="current"/> from <paramref name="from"/> on, unless it is the copy already being walked.</summary>
    public async ValueTask UseAsync(LoadedNames? current, ulong from)
    {
        if (ReferenceEquals(current, index) && (names is not null || current is null)) return;
        if (names is not null) await names.DisposeAsync().ConfigureAwait(false);
        index = current;
        names = current is null || from > uint.MaxValue ? null : current.FromAsync((uint)from, cancellationToken).GetAsyncEnumerator(cancellationToken);
        has = names is not null && await names.MoveNextAsync().ConfigureAwait(false);
    }

    /// <summary>The name of <paramref name="id"/>; each call's id must be above the last one's.</summary>
    public async ValueTask<string?> NameOfAsync(uint id)
    {
        while (has && names!.Current.Id < id) has = await names.MoveNextAsync().ConfigureAwait(false);
        return has && names!.Current.Id == id ? names.Current.Name : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (names is not null) await names.DisposeAsync().ConfigureAwait(false);
        names = null;
    }
}
