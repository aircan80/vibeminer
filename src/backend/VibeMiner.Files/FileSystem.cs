using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Files;

public sealed record FileSystemOptions
{
    /// <summary>Files whose stored state one layer 2 call checks while listing.</summary>
    public int PresenceBatch { get; init; } = 4096;
}

/// <summary>
/// The real <see cref="IFileSystem"/>: opens builds through layer 3 and asks layer 2 which encoded copies are stored. A handle
/// holds the opened <see cref="IBuild"/>, so a file is a ROOT and ENCODING lookup in memory plus a batched index check.
/// </summary>
public sealed class FileSystem : IFileSystem
{
    private readonly IBuilds builds;
    private readonly IObjectSource objects;
    private readonly FileSystemOptions options;

    public FileSystem(IBuilds builds, IObjectSource objects, FileSystemOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builds); ArgumentNullException.ThrowIfNull(objects);
        options ??= new();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PresenceBatch, 1, nameof(options));
        this.builds = builds; this.objects = objects; this.options = options;
    }

    public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        var bit = FileLocales.BitOf(locale);
        var opened = await builds.OpenAsync(build, cancellationToken).ConfigureAwait(false);
        if (opened.State != BuildOpenState.Open || opened.Opened is not { } built)
            return new(build, locale, Map(opened.State), null, opened.Code, opened.Message, opened.ObservedAt);
        if (!built.Metadata.Locales.Contains(locale))
            return new(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", $"The build has no {locale} files.", null);
        return new(build, locale, FilesOpenState.Open, new BuildFiles(built, locale, bit, objects, options.PresenceBatch), null, null, null);
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
}

internal sealed class BuildFiles(IBuild opened, string locale, uint localeBit, IObjectSource objects, int batch) : IBuildFiles
{
    private IBuild? build = opened;

    public BuildRef Build { get; } = opened.Build;
    public string Locale { get; } = locale;

    public ValueTask DisposeAsync() { Volatile.Write(ref build, null); return ValueTask.CompletedTask; }

    private IBuild Opened => Volatile.Read(ref build) ?? throw new ObjectDisposedException(nameof(IBuildFiles), "The file handle is disposed.");

    public async IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var build = Opened;
        cancellationToken.ThrowIfCancellationRequested();
        if (filter.FromId > filter.ToId) yield break;
        // Names come in #173: until then no file is named, so a path prefix matches nothing.
        if (filter.PathPrefix is not null) yield break;
        if (filter.Formats is { } formats && !formats.Contains(FileFormat.Unknown)) yield break;

        var pending = new List<(uint Id, FileVariant Variant)>(batch);
        using var ids = build.FileDataIds(Locale, filter.FromId).GetEnumerator();
        var more = true;
        var walked = 0;
        while (more)
        {
            pending.Clear();
            while (pending.Count < batch && (more = ids.MoveNext()))
            {
                // A narrow filter can walk far between batches; stay cancellable.
                if ((++walked & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var id = ids.Current;
                if (id > filter.ToId) { more = false; break; }
                if (Variants.Pick(build.Resolve(id).Variants, localeBit) is not { } variant) continue;
                if (filter.Encrypted is { } encrypted && IsEncrypted(variant) != encrypted) continue;
                pending.Add((id, variant));
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.Count == 0) continue;

            var storage = await StorageOfAsync(pending, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < pending.Count; i++)
            {
                if (filter.Storage is { } wanted && !wanted.Contains(storage[i])) continue;
                cancellationToken.ThrowIfCancellationRequested();
                yield return ToFile(pending[i].Id, pending[i].Variant, storage[i]);
            }
        }
    }

    public async Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(file);
        if (resolved.State == FileResolutionState.UnknownFile || resolved.FileDataId is not { } fileDataId) return new(FileLookupState.UnknownFile, null);
        if (Variants.Pick(resolved.Variants, localeBit) is not { } variant) return new(FileLookupState.NotInLocale, null);
        var storage = await StorageOfAsync([(fileDataId, variant)], cancellationToken).ConfigureAwait(false);
        return new(variant.EKey is null ? FileLookupState.NotInEncoding : FileLookupState.Found, ToFile(fileDataId, variant, storage[0]));
    }

    public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolved = Resolve(file);
        return Task.FromResult(resolved.State != FileResolutionState.UnknownFile && Variants.Pick(resolved.Variants, localeBit) is not null);
    }

    private FileResolution Resolve(FileRef file) => file.FileDataId is { } id ? Opened.Resolve(id)
        : file.Path is { } path ? Opened.Resolve(path) : throw new ArgumentException("An empty file reference.", nameof(file));

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

    private static BuildFile ToFile(uint id, FileVariant variant, FileStorage storage) =>
        new(id, null, variant.EKey is null ? null : variant.DecodedSize, variant.CKey.Hex, FileFormat.Unknown, storage, IsEncrypted(variant));
}
