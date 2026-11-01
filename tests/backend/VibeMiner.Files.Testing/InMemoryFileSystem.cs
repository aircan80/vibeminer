using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Files.Names;

namespace VibeMiner.Files.Testing;

/// <summary>
/// One file of a fake build. <see cref="Locales"/> null means every locale. <see cref="Path"/> is the build's own name for
/// the file, as ROOT's name hash is: a lookup by path tries it first, and it is never listed (listed names come from
/// <see cref="InMemoryFileSystem.Names"/>, the listfile side). A null <see cref="ContentKey"/> is the MD5 of
/// <see cref="Content"/>, or without content 32 hex digits made from the FileDataID; a given one must be the content's MD5. A file with
/// <see cref="NotInEncoding"/> lists with <see cref="FileStorage.NoEncodedCopy"/> and no size, whatever <see cref="Storage"/> says.
/// <see cref="Content"/> is the decoded file (it sets the listed size); without it a read answers NeedsNetwork, and the file can't
/// be <see cref="FileStorage.Stored"/> (a stored copy always reads). <see cref="Holes"/>
/// are its ranges no key opens: their bytes are never returned. <see cref="ReadFailure"/> makes every read that fetches fail with
/// that state; a whole read over the limit is TooLarge and an empty range is Complete first, as both come from the listed size.
/// A failure that shows on stream reads (<see cref="FakeReadFailure.OnStreamRead"/>, and always WrongKey) lets the stream open and
/// fails its reads of readable bytes instead. A failed read never marks the file stored. Files never share a stored copy (the real
/// side shares one EKey between files of identical content). The listed format follows the real rules: a stored file's first
/// <see cref="Content"/> bytes up to its first hole, when it has no <see cref="ReadFailure"/>, go through the same detection
/// with the listed name; anything else gets the listed name's extension (none when the file has no name).
/// The fake assumes <see cref="Holes"/> are sorted and doesn't model the real side's first-chunk size cap on detection.
/// </summary>
public sealed record FakeFile(uint FileDataId, string? Path = null, long? Size = 0, FileStorage Storage = FileStorage.NotStored,
    bool Encrypted = false, IReadOnlyList<string>? Locales = null, string? ContentKey = null,
    bool NotInEncoding = false, byte[]? Content = null, IReadOnlyList<FileHole>? Holes = null, FakeReadFailure? ReadFailure = null);

/// <summary>
/// A scripted read failure: a state that carries nothing and isn't a lookup state or TooLarge. <see cref="ObservedAt"/> is passed
/// through; for Missing (layer 2's dated 404) null means the time of the read. With <see cref="OnStreamRead"/> (always for
/// WrongKey, which shows only when a chunk is decoded), <see cref="IBuildFiles.OpenReadAsync"/> succeeds in the state its holes
/// give and every stream read that would return bytes throws <see cref="FileReadException"/>; whole and range reads still fail.
/// </summary>
public sealed record FakeReadFailure(FileReadState State, string Code, string? Message = null, DateTimeOffset? ObservedAt = null,
    bool OnStreamRead = false)
{
    /// <summary>Whether the stream opens and its reads fail.</summary>
    public bool FailsStreamReads => OnStreamRead || State == FileReadState.WrongKey;

    /// <summary>A layer 2 state.</summary>
    public bool IsLayer2 => State is FileReadState.Missing or FileReadState.NeedsNetwork or FileReadState.Unavailable or FileReadState.StoreFailed;

    /// <summary>
    /// A layer 2 state every fetch answers, so the file can't be stored. One on stream reads can: the open fetched the copy.
    /// </summary>
    public bool KeepsNotStored => IsLayer2 && !OnStreamRead;
}

/// <summary>
/// The standard <see cref="IFileSystem"/> test double for layer 5 and up: files are listed from memory, nothing is fetched.
/// Builds are added with their locales and files or scripted to fail; anything else answers NeedsNetwork, as the real file
/// system does offline. Storage can be changed while a handle is open, and the handle sees it on its next call. Names follow
/// the real rules: <see cref="FakeFile.Path"/> is the ROOT name hash side, <see cref="Names"/> the listfile side (off until
/// set), read once per call. Reads serve <see cref="FakeFile.Content"/> and mark the file stored, as a fetch would;
/// <paramref name="maxReadBytes"/> is <see cref="FileSystemOptions.MaxReadBytes"/>.
/// </summary>
public sealed class InMemoryFileSystem(long maxReadBytes = 256L << 20) : IFileSystem
{
    /// <summary>The listfile side of names: off until <see cref="InMemoryFileNames.Use"/>.</summary>
    public InMemoryFileNames Names { get; } = new();

    IFileNames IFileSystem.Names => Names;

    private readonly long maxRead = maxReadBytes >= 0 && maxReadBytes <= Array.MaxLength ? maxReadBytes
        : throw new ArgumentOutOfRangeException(nameof(maxReadBytes));
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, FakeBuildFiles> builds = [];
    private readonly Dictionary<BuildRef, (FilesOpenState State, string Code)> failures = [];

    public InMemoryFileSystem Add(BuildRef build, IReadOnlyList<string> locales, IEnumerable<FakeFile> files)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(locales); ArgumentNullException.ThrowIfNull(files);
        foreach (var locale in locales) FileLocales.BitOf(locale);
        var byId = new SortedDictionary<uint, FakeFile>();
        foreach (var file in files)
        {
            foreach (var locale in file.Locales ?? []) FileLocales.BitOf(locale);
            Validate(file);
            var copy = file with { Content = file.Content?.ToArray(), Holes = file.Holes is null ? null : [.. file.Holes] };
            if (!byId.TryAdd(file.FileDataId, copy)) throw new ArgumentException($"FileDataID {file.FileDataId} is added twice.", nameof(files));
        }
        lock (gate) { builds[build] = new FakeBuildFiles(locales.ToArray(), byId); failures.Remove(build); }
        return this;
    }

    private static void Validate(FakeFile file)
    {
        if (file.ReadFailure is { } failure && failure.State is FileReadState.Complete or FileReadState.Partial or FileReadState.Protected
                or FileReadState.TooLarge or FileReadState.UnknownFile or FileReadState.NotInLocale or FileReadState.NotInEncoding)
            throw new ArgumentException($"File {file.FileDataId}: a scripted read failure can't be {failure.State}.", nameof(file));
        if (file.Storage == FileStorage.Stored && file.Content is null)
            throw new ArgumentException($"File {file.FileDataId}: a stored file needs content.", nameof(file));
        if (file.Storage == FileStorage.Stored && file.ReadFailure is { KeepsNotStored: true } stored)
            throw new ArgumentException($"File {file.FileDataId}: a stored file can't fail with layer 2's {stored.State}.", nameof(file));
        if (file.ReadFailure is { FailsStreamReads: true } onRead && file.Content is null)
            throw new ArgumentException($"File {file.FileDataId}: a {onRead.State} failure on stream reads needs content to open.", nameof(file));
        if (file.ContentKey is { } key && file.Content is { } content
                && !string.Equals(key, Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"File {file.FileDataId}: a content key must be the content's MD5.", nameof(file));
        if (file.Holes is not { Count: > 0 } holes) return;
        if (file.Content is null) throw new ArgumentException($"File {file.FileDataId}: holes need content.", nameof(file));
        FileHole? last = null;
        foreach (var hole in holes)
        {
            if (hole is null || hole.Length <= 0 || hole.Offset < 0 || hole.Offset > file.Content.Length - hole.Length)
                throw new ArgumentException($"File {file.FileDataId}: a hole must be non-empty and inside the content.", nameof(file));
            if (hole.KeyName is not { Length: 16 } name || !name.All(char.IsAsciiHexDigitUpper))
                throw new ArgumentException($"File {file.FileDataId}: a key name is 16 uppercase hex digits.", nameof(file));
            if (last is not null && hole.Offset < last.Offset + last.Length)
                throw new ArgumentException($"File {file.FileDataId}: holes must be ascending and not overlap.", nameof(file));
            if (last is not null && hole.Offset == last.Offset + last.Length && hole.KeyName == last.KeyName)
                throw new ArgumentException($"File {file.FileDataId}: adjacent holes of one key must be one hole.", nameof(file));
            last = hole;
        }
    }

    public InMemoryFileSystem Fail(BuildRef build, FilesOpenState state, string code)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (state is FilesOpenState.Open or FilesOpenState.LocaleNotInBuild) throw new ArgumentException("A scripted failure is a layer 3 open state.", nameof(state));
        lock (gate) { failures[build] = (state, code); builds.Remove(build); }
        return this;
    }

    /// <summary>Changes one file's stored state, as a download or a scrub would. Only a file with content can be stored.</summary>
    public InMemoryFileSystem SetStorage(BuildRef build, uint fileDataId, FileStorage storage)
    {
        lock (gate) builds[build].SetStorage(fileDataId, storage);
        return this;
    }

    public Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failed))
                return Task.FromResult(new FilesOpenResult(build, locale, failed.State, null, failed.Code, $"The build is {failed.State} ({failed.Code}).", null));
            if (builds.TryGetValue(build, out var fake))
                return Task.FromResult(fake.HasLocale(locale)
                    ? new FilesOpenResult(build, locale, FilesOpenState.Open, new Handle(build, locale, fake, Names, maxRead), null, null, null)
                    : new FilesOpenResult(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD",
                        $"The build has no {locale} files.", null));
        }
        return Task.FromResult(new FilesOpenResult(build, locale, FilesOpenState.NeedsNetwork, null, "OBJECT_ACCESS_NEEDS_NETWORK",
            "Not stored, and this source is local only.", null));
    }

    private sealed class FakeBuildFiles(string[] locales, SortedDictionary<uint, FakeFile> files)
    {
        private readonly Lock gate = new();
        private readonly uint[] ids = [.. files.Keys];

        public bool HasLocale(string locale) => locales.Contains(locale) && files.Values.Any(f => InLocale(f, locale));

        public static bool InLocale(FakeFile file, string locale) => file.Locales is null || file.Locales.Contains(locale);

        public void SetStorage(uint id, FileStorage storage)
        {
            lock (gate)
            {
                var file = files[id];
                if (storage == FileStorage.Stored && file.Content is null)
                    throw new ArgumentException($"File {id} has no content, so it can't be stored.", nameof(storage));
                if (storage == FileStorage.Stored && file.ReadFailure is { KeepsNotStored: true } failure)
                    throw new ArgumentException($"File {id} fails with layer 2's {failure.State}, so it can't be stored.", nameof(storage));
                files[id] = file with { Storage = storage };
            }
        }

        /// <summary>A read fetched the file, so it is stored now.</summary>
        public void MarkFetched(uint id) => SetStorage(id, FileStorage.Stored);

        public FakeFile? Find(uint id) { lock (gate) return files.GetValueOrDefault(id); }

        public FakeFile? Find(string path)
        {
            var wanted = Normalize(path);
            lock (gate) return files.Values.FirstOrDefault(f => f.Path is not null && Normalize(f.Path) == wanted);
        }

        public IEnumerable<uint> IdsFrom(uint fromId)
        {
            var start = Array.BinarySearch(ids, fromId);
            for (var i = start < 0 ? ~start : start; i < ids.Length; i++) yield return ids[i];
        }

        public static string Normalize(string path) => path.Replace('/', '\\').ToUpperInvariant();
    }

    private sealed class Handle(BuildRef build, string locale, FakeBuildFiles fake, InMemoryFileNames names, long maxReadBytes) : IBuildFiles
    {
        private int disposed;
        // What a file's first bytes proved, by FileDataID (Unknown: read, nothing proved), as the real handle remembers it.
        private readonly ConcurrentDictionary<uint, FileFormat> proven = new();

        public BuildRef Build { get; } = build;
        public string Locale { get; } = locale;

        public ValueTask DisposeAsync() { Volatile.Write(ref disposed, 1); return ValueTask.CompletedTask; }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ThrowIfDisposed();
            var prefix = filter.PathPrefix;
            var index = names.Current();
            if (prefix is not null && index is null) yield break;
            foreach (var id in fake.IdsFrom(filter.FromId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id > filter.ToId) yield break;
                if (fake.Find(id) is not { } file || !FakeBuildFiles.InLocale(file, Locale)) continue;
                var name = index?.NameOf(id);
                if (prefix is not null && (name is null || !ListfileIndex.HasPrefix(name, prefix))) continue;
                var listed = ToFileWithoutFormat(file, name, name is null ? FilePathSource.None : FilePathSource.Listfile);
                if (filter.Storage is { } storage && !storage.Contains(listed.Storage)) continue;
                if (filter.Encrypted is { } encrypted && listed.Encrypted != encrypted) continue;
                listed = WithFormat(file, listed);
                if (filter.Formats is { } formats && !formats.Contains(listed.Format)) continue;
                yield return listed;
                await Task.Yield();
            }
        }

        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Lookup(file).Stat);
        }

        /// <summary>
        /// By id; or by path, the ROOT side (<see cref="FakeFile.Path"/>) first, then the listfile side: of the ids it gives the
        /// name, the files listed in this locale, else any listed; the highest.
        /// </summary>
        private (FileStat Stat, FakeFile? File) Lookup(FileRef file)
        {
            var index = names.Current();
            FakeFile? found;
            var byRoot = false;
            if (file.FileDataId is { } id) found = fake.Find(id);
            else if (file.Path is { } path)
            {
                found = fake.Find(path);
                byRoot = found is not null;
                if (found is null && index is not null)
                {
                    var listed = index.IdsOf(path).Select(fake.Find).OfType<FakeFile>().ToArray();
                    found = listed.LastOrDefault(f => FakeBuildFiles.InLocale(f, Locale)) ?? listed.LastOrDefault();
                }
            }
            else throw new ArgumentException("An empty file reference.", nameof(file));
            if (found is null) return (new FileStat(FileLookupState.UnknownFile, null), null);
            if (!FakeBuildFiles.InLocale(found, Locale)) return (new FileStat(FileLookupState.NotInLocale, null), null);
            var (name, source) = index?.NameOf(found.FileDataId) is { } n ? (n, FilePathSource.Listfile)
                : byRoot ? (file.Path, FilePathSource.RootHash) : (null, FilePathSource.None);
            var stat = WithFormat(found, ToFileWithoutFormat(found, name, source));
            return (new FileStat(found.NotInEncoding ? FileLookupState.NotInEncoding : FileLookupState.Found, stat), found);
        }

        /// <summary>The listed file to read, or the lookup state that says why there is none.</summary>
        private (FakeFile? File, FileRead? Failed) Listed(FileRef file, long offset)
        {
            var (stat, found) = Lookup(file);
            if (stat.State == FileLookupState.Found && found is not null) return (found, null);
            var state = stat.State switch
            {
                FileLookupState.NotInLocale => FileReadState.NotInLocale,
                FileLookupState.NotInEncoding => FileReadState.NotInEncoding,
                _ => FileReadState.UnknownFile,
            };
            return (null, FileRead.Failed(state, offset, null, $"The file is {stat.State}."));
        }

        /// <summary>What fetching the file would fail with: a scripted failure, or no content offline. Null when it has content.</summary>
        private static Failure? FetchFailure(FakeFile file) =>
            file.ReadFailure is { } failure ? Failure.Of(failure)
            : file.Content is null ? new(FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK", "Not stored, and this source is local only.", null)
            : null;

        /// <summary>
        /// A range already clamped to the listed size (null when no size is listed): an empty one is Complete at once, with no
        /// fetch; anything else fetches (a scripted failure or NeedsNetwork wins) and marks the file stored.
        /// </summary>
        private FileRead Fetch(FakeFile file, long start, int? length, long failedOffset)
        {
            if (length == 0) return new(FileReadState.Complete, start, 0, [], []);
            if (FetchFailure(file) is { } failure) return FileRead.Failed(failure.State, failedOffset, failure.Code, failure.Message, failure.ObservedAt);
            fake.MarkFetched(file.FileDataId);
            return Read(file, start, length!.Value);
        }

        public async Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) =>
            (await StatAsync(file, cancellationToken).ConfigureAwait(false)).State is FileLookupState.Found or FileLookupState.NotInEncoding;

        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var (found, failed) = Listed(file, 0);
            if (found is null) return Task.FromResult(failed!);
            var size = SizeOf(found);
            if (size > maxReadBytes)
                return Task.FromResult(FileRead.Failed(FileReadState.TooLarge, 0, "FILES_READ_TOO_LARGE",
                    $"The file is {size} bytes, over the {maxReadBytes} a whole read returns."));
            var read = Fetch(found, 0, (int?)size, 0);
            if (HeadOf(read) is { } head) Remember(found.FileDataId, head, found.Content?.Length ?? 0);
            return Task.FromResult(read);
        }

        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (file.FileDataId is null && file.Path is null) throw new ArgumentException("An empty file reference.", nameof(file));
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(length, maxReadBytes);
            var (found, failed) = Listed(file, offset);
            if (found is null) return Task.FromResult(failed!);
            if (SizeOf(found) is not { } size) return Task.FromResult(Fetch(found, offset, null, offset));
            var start = Math.Min(offset, size);
            return Task.FromResult(Fetch(found, start, (int)Math.Min(length, size - start), offset));
        }

        public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var (found, failed) = Listed(file, 0);
            if (found is null) return Task.FromResult(new FileOpen(failed!.State, null, failed.Code, failed.Message));
            var onRead = found.ReadFailure is { FailsStreamReads: true } scripted ? scripted : null;
            if (onRead is null && FetchFailure(found) is { } failure)
                return Task.FromResult(new FileOpen(failure.State, null, failure.Code, failure.Message, failure.ObservedAt));
            var content = found.Content!;
            var holes = found.Holes ?? [];
            // The open read the file's layout, so it is stored even when its reads will fail.
            fake.MarkFetched(found.FileDataId);
            return Task.FromResult(new FileOpen(StateOf(content.Length, holes.Sum(h => h.Length)), new FakeReadStream(content, holes, onRead)));
        }

        /// <summary>The listed size: the content's length, else the declared size.</summary>
        private static long? SizeOf(FakeFile file) => file.Content?.Length ?? file.Size;

        /// <summary>A range of the file as maximal segments between its holes, clipped to the range, with bytes copied out.</summary>
        private FileRead Read(FakeFile file, long offset, int length)
        {
            var content = file.Content!;
            long end = offset + length, at = offset;
            var segments = new List<FileSegment>();
            var holes = new List<FileHole>();
            foreach (var hole in file.Holes ?? [])
            {
                long from = Math.Max(hole.Offset, offset), to = Math.Min(hole.Offset + hole.Length, end);
                if (from >= to) continue;
                if (from > at) segments.Add(new(at, content.AsSpan((int)at, (int)(from - at)).ToArray()));
                holes.Add(new(from, to - from, hole.KeyName));
                at = to;
            }
            if (end > at) segments.Add(new(at, content.AsSpan((int)at, (int)(end - at)).ToArray()));
            return new(StateOf(length, holes.Sum(h => h.Length)), offset, length, segments, holes);
        }

        private static FileReadState StateOf(long length, long inHoles) =>
            inHoles == 0 ? FileReadState.Complete : inHoles == length ? FileReadState.Protected : FileReadState.Partial;

        private static BuildFile ToFileWithoutFormat(FakeFile f, string? path, FilePathSource source) => new(f.FileDataId, path, source,
            f.NotInEncoding ? null : f.Content?.Length ?? f.Size,
            f.ContentKey?.ToLowerInvariant()
                ?? (f.Content is { } content ? Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(content))
                    : f.FileDataId.ToString("x32", System.Globalization.CultureInfo.InvariantCulture)), FileFormat.Unknown, FileFormatSource.None,
            f.NotInEncoding ? FileStorage.NoEncodedCopy : f.Storage, f.Encrypted);

        /// <summary>The listed file's format, by the real rules: remembered bytes, else a stored file's first bytes, else the listed name.</summary>
        private BuildFile WithFormat(FakeFile f, BuildFile listed)
        {
            FileFormat proved;
            if (!proven.TryGetValue(f.FileDataId, out proved))
            {
                if (listed.Storage != FileStorage.Stored || LocalHead(f) is not { } head)
                {
                    var (named, source) = Formats.FormatDetection.Detect(null, listed.Path);
                    return listed with { Format = named, FormatSource = source };
                }
                proved = Remember(f.FileDataId, head, f.Content?.Length ?? 0);
            }
            var (format, from) = proved != FileFormat.Unknown ? (proved, FileFormatSource.Magic) : Formats.FormatDetection.Detect(null, listed.Path);
            return listed with { Format = format, FormatSource = from };
        }

        /// <summary>As the real handle: Unknown only from a complete head, and never over a strong entry.</summary>
        private FileFormat Remember(uint fileDataId, byte[] head, long size)
        {
            var evidence = Formats.FormatDetection.FromBytes(head);
            var proved = evidence.Strong ? evidence.Format : FileFormat.Unknown;
            var old = proven.TryGetValue(fileDataId, out var known) ? known : (FileFormat?)null;
            if (proved == FileFormat.Unknown && (head.Length < Math.Min(Formats.FormatDetection.HeadBytes, size) || old is { } strong && strong != FileFormat.Unknown))
                return old ?? FileFormat.Unknown;
            if (old is not null || proven.Count < BuildFiles.MaxRememberedFormats) proven[fileDataId] = proved;
            return proved;
        }

        /// <summary>The first bytes up to the first hole, as a local read of a stored copy gives them; null when it would fail or start in a hole.</summary>
        private static byte[]? LocalHead(FakeFile f)
        {
            if (f.Content is not { } content || f.ReadFailure is not null) return null;
            var length = Math.Min(content.Length, Formats.FormatDetection.HeadBytes);
            if (f.Holes is { Count: > 0 } holes) length = (int)Math.Min(length, holes[0].Offset);
            return length == 0 && content.Length > 0 ? null : content[..length];
        }

        private static byte[]? HeadOf(FileRead read)
        {
            if (read.State is not (FileReadState.Complete or FileReadState.Partial) || read.Offset != 0) return null;
            if (read.Segments.Count == 0) return read.Length == 0 ? [] : null;
            return read.Segments[0].Offset == 0 ? read.Segments[0].Bytes.ToArray() : null;
        }
    }

    /// <summary>A failed read's state, code, message and time; Missing gets the time of the read when none is scripted.</summary>
    private sealed record Failure(FileReadState State, string Code, string Message, DateTimeOffset? ObservedAt)
    {
        public static Failure Of(FakeReadFailure f) => new(f.State, f.Code, f.Message ?? $"The read is {f.State} ({f.Code}).",
            f.ObservedAt ?? (f.State == FileReadState.Missing ? TimeProvider.System.GetUtcNow() : null));
    }

    /// <summary>
    /// An async-only stream over the content, which the fake never changes after Add, so it outlives the handle. With
    /// <paramref name="failure"/>, every read that would return bytes throws <see cref="FileReadException"/> and leaves the position.
    /// </summary>
    private sealed class FakeReadStream(byte[] content, IReadOnlyList<FileHole> holes, FakeReadFailure? failure) : FileReadStream
    {
        private long position;
        private bool disposed;

        public override IReadOnlyList<FileHole> Holes { get; } = holes;
        public override bool CanRead => !disposed;
        public override bool CanSeek => !disposed;
        public override bool CanWrite => false;
        public override long Length { get { ObjectDisposedException.ThrowIf(disposed, this); return content.Length; } }

        public override long Position
        {
            get { ObjectDisposedException.ThrowIf(disposed, this); return position; }
            set { ObjectDisposedException.ThrowIf(disposed, this); ArgumentOutOfRangeException.ThrowIfNegative(value); position = value; }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => content.Length + offset,
                _ => throw new ArgumentException("An unknown seek origin.", nameof(origin)),
            };
            if (target < 0) throw new IOException("A seek before the start of the stream.");
            return position = target;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            if (buffer.Length == 0 || position >= content.Length) return ValueTask.FromResult(0);
            var stop = (long)content.Length;
            foreach (var hole in Holes)
            {
                if (hole.Offset + hole.Length <= position) continue;
                if (hole.Offset <= position) return ValueTask.FromException<int>(new FileHoleException(hole));
                stop = hole.Offset;
                break;
            }
            if (failure is not null)
            {
                var f = Failure.Of(failure);
                return ValueTask.FromException<int>(new FileReadException(f.State, f.Code, f.Message, f.ObservedAt));
            }
            var count = (int)Math.Min(buffer.Length, stop - position);
            content.AsSpan((int)position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Reads are async only.");
        public override int Read(Span<byte> buffer) => throw new NotSupportedException("Reads are async only.");
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read-only.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The stream is read-only.");
        public override void SetLength(long value) => throw new NotSupportedException("The stream is read-only.");
        public override void Flush() { }

        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
    }
}
