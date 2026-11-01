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
/// <see cref="DecryptedKeys"/> are the keys of encrypted chunks a known key opened: the stream's
/// <see cref="FileReadStream.ChunkKeys"/> lists them with the holes' keys.
/// The fake assumes <see cref="Holes"/> are sorted and doesn't model the real side's first-chunk size cap on detection.
/// </summary>
public sealed record FakeFile(uint FileDataId, string? Path = null, long? Size = 0, FileStorage Storage = FileStorage.NotStored,
    bool Encrypted = false, IReadOnlyList<string>? Locales = null, string? ContentKey = null,
    bool NotInEncoding = false, byte[]? Content = null, IReadOnlyList<FileHole>? Holes = null, FakeReadFailure? ReadFailure = null,
    IReadOnlyList<string>? DecryptedKeys = null);

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
/// Ensure follows the real rules on the same data: a stored file costs no request; a file with a layer 2
/// <see cref="FakeFile.ReadFailure"/> that keeps it not stored ends in that state with its code, one without content is
/// NeedsNetwork, and any other is fetched (decode failures such as Corrupt don't show, as nothing is decoded) and marked
/// stored. Files are fetched one at a time in selection order; concurrent ensures of one file share one fetch. Every file that
/// needed a fetch is recorded in <see cref="EnsureRequests"/> with its priority (a joined one too), every fetch that ran in
/// <see cref="EnsureFetches"/>. Bytes are the content's length, else the listed size.
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
    private readonly List<(uint FileDataId, EnsurePriority Priority)> ensureRequests = [];
    private readonly List<uint> ensureFetches = [];

    /// <summary><see cref="FileSystemOptions.ProgressInterval"/>; positive.</summary>
    public TimeSpan ProgressInterval
    {
        get => progressInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            progressInterval = value;
        }
    }
    private TimeSpan progressInterval = TimeSpan.FromMilliseconds(100);
    /// <summary><see cref="FileSystemOptions.Time"/>.</summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;
    /// <summary>When set, every ensure fetch waits for this first (a joined one doesn't call it); cancellation ends the wait.</summary>
    public Func<uint, EnsurePriority, CancellationToken, Task>? BeforeFetch { get; set; }
    /// <summary>Every file an ensure asked to fetch, with its priority, in order; a call that joined a running fetch included.</summary>
    public IReadOnlyList<(uint FileDataId, EnsurePriority Priority)> EnsureRequests { get { lock (gate) return ensureRequests.ToArray(); } }
    /// <summary>Every fetch an ensure ran, in order.</summary>
    public IReadOnlyList<uint> EnsureFetches { get { lock (gate) return ensureFetches.ToArray(); } }

    /// <summary>When set, called with every request <see cref="EnsureRequests"/> records, after recording it.</summary>
    public Action<uint, EnsurePriority>? OnRequest { get; set; }

    private void Requested(uint id, EnsurePriority priority)
    {
        lock (gate) ensureRequests.Add((id, priority));
        OnRequest?.Invoke(id, priority);
    }
    private void Fetched(uint id) { lock (gate) ensureFetches.Add(id); }

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
                    ? new FilesOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale, fake, Names, maxRead), null, null, null)
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

        // Ensure fetches running, by FileDataID.
        private readonly Dictionary<uint, Task<(FileEnsureState State, string? Code)>> fetching = [];

        /// <summary>The running fetch of a file to join, or null after claiming it with <paramref name="mine"/>.</summary>
        public Task<(FileEnsureState State, string? Code)>? JoinOrClaim(uint id, TaskCompletionSource<(FileEnsureState, string?)> mine)
        {
            lock (gate)
            {
                if (fetching.TryGetValue(id, out var running)) return running;
                fetching[id] = mine.Task;
                return null;
            }
        }

        public void Release(uint id, TaskCompletionSource<(FileEnsureState, string?)> mine)
        {
            lock (gate) if (fetching.TryGetValue(id, out var t) && t == mine.Task) fetching.Remove(id);
        }
    }

    private sealed class Handle(InMemoryFileSystem owner, BuildRef build, string locale, FakeBuildFiles fake, InMemoryFileNames names, long maxReadBytes) : IBuildFiles
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
                if (prefix is not null && (name is null || !NameFolding.HasPrefix(name, prefix))) continue;
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
            if (!FakeBuildFiles.InLocale(found, Locale)) return (new FileStat(FileLookupState.NotInLocale, null), found);
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
            return Task.FromResult(new FileOpen(StateOf(content.Length, holes.Sum(h => h.Length)), new FakeReadStream(content, holes, onRead, found.DecryptedKeys ?? [])));
        }

        public async Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            ThrowIfDisposed();
            var tally = new EnsureTally(progress, owner.Time, owner.ProgressInterval);
            var toFetch = new List<(int Index, FakeFile File, long Bytes)>();
            try
            {
                if (selection.Kind == EnsureSelectionKind.Matching)
                {
                    try
                    {
                        await foreach (var listed in ListAsync(selection.Filter!, cancellationToken).ConfigureAwait(false))
                        {
                            var file = fake.Find(listed.FileDataId)!;
                            Select(tally, toFetch, FileRef.Id(listed.FileDataId), file, FileLookupState.Found);
                            tally.Offer();
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return await tally.FinishAsync(canceled: true).ConfigureAwait(false);
                    }
                }
                else
                {
                    var ids = new HashSet<uint>();
                    var unknown = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var file in selection.Files!)
                    {
                        var (stat, found) = Lookup(file);
                        if (found is null) { if (unknown.Add(EnsureTally.UnknownKey(file))) tally.Settle(tally.Add(file, null, 0), FileEnsureState.UnknownFile, null, 0); continue; }
                        if (ids.Add(found.FileDataId)) Select(tally, toFetch, file, found, stat.State);
                    }
                }
                tally.Listed();
                tally.Offer();
                foreach (var (index, file, bytes) in toFetch)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (state, code) = await FetchAsync(file, priority, cancellationToken).ConfigureAwait(false);
                        tally.Settle(index, state, code, bytes);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return await tally.FinishAsync(canceled: true).ConfigureAwait(false);
                    }
                    tally.Offer();
                }
                return await tally.FinishAsync(canceled: false).ConfigureAwait(false);
            }
            catch { tally.Abandon(); throw; }
        }

        /// <summary>Counts a found file and settles it unless it needs a fetch.</summary>
        private void Select(EnsureTally tally, List<(int, FakeFile, long)> toFetch, FileRef asked, FakeFile file, FileLookupState state)
        {
            if (state == FileLookupState.NotInLocale) { tally.Settle(tally.Add(asked, file.FileDataId, 0), FileEnsureState.NotInLocale, null, 0); return; }
            if (file.NotInEncoding) { tally.Settle(tally.Add(asked, file.FileDataId, 0), FileEnsureState.NotInEncoding, null, 0); return; }
            var bytes = SizeOf(file) ?? 0;
            var index = tally.Add(asked, file.FileDataId, bytes);
            if (file.Storage == FileStorage.Stored) tally.Settle(index, FileEnsureState.AlreadyStored, null, bytes);
            else toFetch.Add((index, file, bytes));
        }

        /// <summary>Fetches a file not stored when selected, or joins the ensure already fetching it.</summary>
        private async Task<(FileEnsureState, string?)> FetchAsync(FakeFile selected, EnsurePriority priority, CancellationToken cancellationToken)
        {
            var id = selected.FileDataId;
            owner.Requested(id, priority);
            while (true)
            {
                var mine = new TaskCompletionSource<(FileEnsureState, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (fake.JoinOrClaim(id, mine) is { } running)
                {
                    try { return await running.WaitAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; }
                }
                try
                {
                    if (owner.BeforeFetch is { } hold) await hold(id, priority, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = fake.Find(id)!;
                    (FileEnsureState, string?) result;
                    if (file.Storage == FileStorage.Stored) result = (FileEnsureState.Stored, null);
                    else if (file.ReadFailure is { KeepsNotStored: true } failure) result = (StateOf(failure.State), failure.Code);
                    else if (file.Content is null) result = (FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK");
                    else { fake.MarkFetched(id); owner.Fetched(id); result = (FileEnsureState.Stored, null); }
                    mine.TrySetResult(result);
                    return result;
                }
                catch (Exception e)
                {
                    if (e is OperationCanceledException) mine.TrySetCanceled(); else mine.TrySetException(e);
                    throw;
                }
                finally { fake.Release(id, mine); }
            }
        }

        private static FileEnsureState StateOf(FileReadState state) => state switch
        {
            FileReadState.Missing => FileEnsureState.Missing,
            FileReadState.NeedsNetwork => FileEnsureState.NeedsNetwork,
            FileReadState.Unavailable => FileEnsureState.Unavailable,
            FileReadState.StoreFailed => FileEnsureState.StoreFailed,
            _ => throw new InvalidOperationException($"{state} isn't a layer 2 state."),
        };

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
    private sealed class FakeReadStream(byte[] content, IReadOnlyList<FileHole> holes, FakeReadFailure? failure, IReadOnlyList<string> decrypted)
        : FileReadStream
    {
        public override IReadOnlyList<string> ChunkKeys { get; } =
            [.. holes.Select(h => h.KeyName).Concat(decrypted).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
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
