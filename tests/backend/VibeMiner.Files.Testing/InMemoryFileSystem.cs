using System.Runtime.CompilerServices;
using VibeMiner.Builds;

namespace VibeMiner.Files.Testing;

/// <summary>
/// One file of a fake build. <see cref="Locales"/> null means every locale. <see cref="Path"/> resolves stats by path (as
/// ROOT's name hash does) and is the listed name when the file system lists names. A null <see cref="ContentKey"/> takes 32 hex digits made from the FileDataID. A file with
/// <see cref="NotInEncoding"/> lists with <see cref="FileStorage.NoEncodedCopy"/> and no size, whatever <see cref="Storage"/> says.
/// </summary>
public sealed record FakeFile(uint FileDataId, string? Path = null, long? Size = 0, FileStorage Storage = FileStorage.NotStored,
    bool Encrypted = false, IReadOnlyList<string>? Locales = null, string? ContentKey = null, FileFormat Format = FileFormat.Unknown,
    bool NotInEncoding = false);

/// <summary>
/// The standard <see cref="IFileSystem"/> test double for layer 5 and up: files are listed from memory, nothing is fetched.
/// Builds are added with their locales and files or scripted to fail; anything else answers NeedsNetwork, as the real file
/// system does offline. Storage can be changed while a handle is open, and the handle sees it on its next call. With
/// <paramref name="listNames"/> false, no file is named (Path null, a path prefix matches nothing), as before names come in
/// #173; paths still resolve.
/// </summary>
public sealed class InMemoryFileSystem(bool listNames = false) : IFileSystem
{
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
            if (!byId.TryAdd(file.FileDataId, file)) throw new ArgumentException($"FileDataID {file.FileDataId} is added twice.", nameof(files));
        }
        lock (gate) { builds[build] = new FakeBuildFiles(locales.ToArray(), byId); failures.Remove(build); }
        return this;
    }

    public InMemoryFileSystem Fail(BuildRef build, FilesOpenState state, string code)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (state is FilesOpenState.Open or FilesOpenState.LocaleNotInBuild) throw new ArgumentException("A scripted failure is a layer 3 open state.", nameof(state));
        lock (gate) { failures[build] = (state, code); builds.Remove(build); }
        return this;
    }

    /// <summary>Changes one file's stored state, as a download or a scrub would.</summary>
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
                    ? new FilesOpenResult(build, locale, FilesOpenState.Open, new Handle(build, locale, fake, listNames), null, null, null)
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

        public void SetStorage(uint id, FileStorage storage) { lock (gate) files[id] = files[id] with { Storage = storage }; }

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

    private sealed class Handle(BuildRef build, string locale, FakeBuildFiles fake, bool listNames) : IBuildFiles
    {
        private int disposed;

        public BuildRef Build { get; } = build;
        public string Locale { get; } = locale;

        public ValueTask DisposeAsync() { Volatile.Write(ref disposed, 1); return ValueTask.CompletedTask; }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ThrowIfDisposed();
            var prefix = filter.PathPrefix is null ? null : FakeBuildFiles.Normalize(filter.PathPrefix);
            foreach (var id in fake.IdsFrom(filter.FromId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id > filter.ToId) yield break;
                if (fake.Find(id) is not { } file || !FakeBuildFiles.InLocale(file, Locale)) continue;
                var listed = ToFile(file);
                if (prefix is not null && (listed.Path is null || !FakeBuildFiles.Normalize(listed.Path).StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (filter.Formats is { } formats && !formats.Contains(listed.Format)) continue;
                if (filter.Storage is { } storage && !storage.Contains(listed.Storage)) continue;
                if (filter.Encrypted is { } encrypted && listed.Encrypted != encrypted) continue;
                yield return listed;
                await Task.Yield();
            }
        }

        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var found = file.FileDataId is { } id ? fake.Find(id) : file.Path is { } path ? fake.Find(path) : throw new ArgumentException("An empty file reference.", nameof(file));
            if (found is null) return Task.FromResult(new FileStat(FileLookupState.UnknownFile, null));
            if (!FakeBuildFiles.InLocale(found, Locale)) return Task.FromResult(new FileStat(FileLookupState.NotInLocale, null));
            return Task.FromResult(new FileStat(found.NotInEncoding ? FileLookupState.NotInEncoding : FileLookupState.Found, ToFile(found)));
        }

        public async Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) =>
            (await StatAsync(file, cancellationToken).ConfigureAwait(false)).State is FileLookupState.Found or FileLookupState.NotInEncoding;

        private BuildFile ToFile(FakeFile f) => new(f.FileDataId, listNames ? f.Path : null, f.NotInEncoding ? null : f.Size,
            f.ContentKey?.ToLowerInvariant() ?? f.FileDataId.ToString("x32", System.Globalization.CultureInfo.InvariantCulture), f.Format,
            f.NotInEncoding ? FileStorage.NoEncodedCopy : f.Storage, f.Encrypted);
    }
}
