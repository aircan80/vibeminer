using VibeMiner.Builds;
using VibeMiner.Composition;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Profile;

/// <summary>What one size part found. Missing is absent (no store, no folder), never the same as measured at 0 bytes.</summary>
internal enum SizeState
{
    Measured,
    Missing,
    Failed,
}

/// <summary>The object store: <see cref="Stats"/> when measured, the store's code when it failed.</summary>
internal sealed record ObjectStoreSize(SizeState State, StoreStats? Stats, string? Code, string? Message);

/// <summary>Generated data: the summed file lengths under <see cref="StorageSizes.GeneratedFolder"/>; links are not followed.</summary>
internal sealed record GeneratedSize(SizeState State, long Bytes, long Files, string? Code, string? Message);

/// <summary>One build's Downloaded figure, or why it couldn't be had. One build failing never hides the others.</summary>
internal sealed record BuildDownloadedSize(BuildRef Build, DownloadedFigure? Figure, string? Code, string? Message);

internal sealed record StorageSizesReport(string DataFolder, ObjectStoreSize ObjectStore, GeneratedSize Generated, IReadOnlyList<BuildDownloadedSize> Builds);

/// <summary>
/// The sizes of one data folder: the object store (from its index, through a read-only open), the generated data folder (a
/// walk of its files, off the caller's thread) and each given build's Downloaded share (from the caller's layer 3 figures).
/// Failures are part of the report; only cancellation throws.
/// </summary>
internal static class StorageSizes
{
    /// <summary>The generated store's folder in the data folder (#179); the reset service removes the same one.</summary>
    public const string GeneratedFolder = "generated-v1";

    public const string GeneratedIoError = "GENERATED_IO_ERROR";
    public const string GeneratedAccessDenied = "GENERATED_ACCESS_DENIED";
    /// <summary>The name is there but is a file or a link, not a folder; nothing behind it is measured.</summary>
    public const string GeneratedNotAFolder = "GENERATED_NOT_A_FOLDER";
    public const string StoreIoError = "OBJECT_STORE_IO_ERROR";
    public const string DownloadedFailed = "DOWNLOADED_FAILED";

    public static async Task<StorageSizesReport> MeasureAsync(string dataFolder, IStoreOpener stores, IReadOnlyList<IBuild> builds,
        DownloadedFigures figures, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(figures);
        var store = await ObjectStoreAsync(DataFolderPaths.ObjectStoreFolder(dataFolder), stores, ct).ConfigureAwait(false);
        var generated = await GeneratedAsync(dataFolder, ct).ConfigureAwait(false);
        var downloaded = await DownloadedAsync(builds, figures, ct).ConfigureAwait(false);
        return new(dataFolder, store, generated, downloaded);
    }

    /// <summary>Opens the store read-only for its stats and closes it. A missing store is <see cref="SizeState.Missing"/>.</summary>
    public static async Task<ObjectStoreSize> ObjectStoreAsync(string storeFolder, IStoreOpener stores, CancellationToken ct)
    {
        try
        {
            await using var handle = await stores.OpenReadOnlyAsync(storeFolder, ct).ConfigureAwait(false);
            return await ObjectStoreAsync(handle, ct).ConfigureAwait(false);
        }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Missing) { return new(SizeState.Missing, null, e.Code, e.Message); }
        catch (ObjectStoreException e) { return new(SizeState.Failed, null, e.Code, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(SizeState.Failed, null, StoreIoError, e.Message); }
    }

    /// <summary>Stats from a handle the caller already holds.</summary>
    public static async Task<ObjectStoreSize> ObjectStoreAsync(IStoreHandle handle, CancellationToken ct)
    {
        try { return new(SizeState.Measured, await handle.StatsAsync(ct).ConfigureAwait(false), null, null); }
        catch (ObjectStoreException e) { return new(SizeState.Failed, null, e.Code, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(SizeState.Failed, null, StoreIoError, e.Message); }
    }

    /// <summary>
    /// Walks <see cref="GeneratedFolder"/> on the thread pool (one walk, no fan-out) and sums its regular files. A link inside is
    /// neither followed nor counted; the folder itself being a link or a file is <see cref="GeneratedNotAFolder"/>.
    /// </summary>
    public static Task<GeneratedSize> GeneratedAsync(string dataFolder, CancellationToken ct) =>
        Task.Run(() => Walk(Path.Combine(dataFolder, GeneratedFolder), ct), ct);

    private static GeneratedSize Walk(string root, CancellationToken ct)
    {
        try
        {
            var info = new DirectoryInfo(root);
            if (!info.Exists && !File.Exists(root) && info.LinkTarget is null) return new(SizeState.Missing, 0, 0, null, null);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return new(SizeState.Failed, 0, 0, GeneratedNotAFolder, $"{root} is not a folder.");
            long bytes = 0, files = 0;
            var pending = new Stack<DirectoryInfo>();
            pending.Push(info);
            var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
            while (pending.TryPop(out var dir))
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null) continue;
                    if (entry is DirectoryInfo sub) pending.Push(sub);
                    else if (entry is FileInfo file) { bytes += file.Length; files++; }
                }
            }
            return new(SizeState.Measured, bytes, files, null, null);
        }
        catch (UnauthorizedAccessException e) { return new(SizeState.Failed, 0, 0, GeneratedAccessDenied, e.Message); }
        catch (IOException e) { return new(SizeState.Failed, 0, 0, GeneratedIoError, e.Message); }
    }

    /// <summary>Each build's figure in turn; <paramref name="figures"/> bounds its own measurements.</summary>
    public static async Task<IReadOnlyList<BuildDownloadedSize>> DownloadedAsync(IReadOnlyList<IBuild> builds, DownloadedFigures figures,
        CancellationToken ct)
    {
        var sizes = new List<BuildDownloadedSize>(builds.Count);
        foreach (var build in builds)
        {
            ct.ThrowIfCancellationRequested();
            try { sizes.Add(new(build.Build, await figures.GetAsync(build, ct).ConfigureAwait(false), null, null)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (ObjectStoreException e) { sizes.Add(new(build.Build, null, e.Code, e.Message)); }
            catch (Exception e) { sizes.Add(new(build.Build, null, DownloadedFailed, $"{e.GetType().Name}: {e.Message}")); }
        }
        return sizes;
    }
}
