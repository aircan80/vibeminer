using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Sums file lengths under the engine data folder. Links are not followed. The walk holds at most
/// <see cref="MaxPendingDirectories"/> queued folders and <see cref="MaxDepth"/> levels; anything past those
/// bounds, and anything that can't be read, is counted as skipped instead of guessed. Directory
/// enumeration has no async API, so the walk leaves the caller first and runs in batches of <see cref="BatchEntries"/> entries and
/// yields and checks cancellation between batches. It takes no acquisition permits and reads no file content.
/// </summary>
public sealed class DataDirectoryMeasurer(string dataDirectory, TimeProvider? clock = null) : IStorageMeasurer
{
    public const int MaxDepth = 32;
    public const int MaxPendingDirectories = 4096;
    public const int BatchEntries = 512;
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
    };
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public string Location { get; } = Path.GetFullPath(dataDirectory);

    public async Task<StorageMeasurement> MeasureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The host runs a request inline until its first await; leave the dispatch loop before the first synchronous batch.
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        var started = time.GetUtcNow();
        long bytes = 0, files = 0, skipped = 0;
        var batch = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((Location, 0));
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            IEnumerator<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(directory.Path).EnumerateFileSystemInfos("*", Options).GetEnumerator(); }
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; continue; }
            using (entries)
            {
                while (true)
                {
                    try { if (!entries.MoveNext()) break; }
                    catch (DirectoryNotFoundException) { break; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; break; }
                    var entry = entries.Current;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { /* Links are not followed or counted. */ }
                    else if (entry is DirectoryInfo child)
                    {
                        if (directory.Depth + 1 > MaxDepth || pending.Count >= MaxPendingDirectories) skipped++;
                        else pending.Push((child.FullName, directory.Depth + 1));
                    }
                    else if (entry is FileInfo file)
                    {
                        try { bytes = checked(bytes + file.Length); files++; }
                        catch (FileNotFoundException) { /* Removed during the walk. */ }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { skipped++; }
                    }
                    if (++batch >= BatchEntries)
                    {
                        batch = 0;
                        token.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(bytes, files, skipped, started, time.GetUtcNow());
    }
}
