using VibeMiner.Composition;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Profile;

internal enum ResetTargetKind { Folder, File }

/// <summary>One thing in the data folder a reset wipes: a plain name directly under the data folder.</summary>
internal sealed record ResetTarget(string Name, ResetTargetKind Kind);

internal enum ResetState { Wiped, Absent, Failed, Skipped }

/// <summary>
/// What happened to one target (or one leftover <c>.reset-</c> folder). <see cref="BytesFreed"/> is the sum of file lengths
/// measured before deletion, links not followed; null when measuring failed (the delete still ran). A failure carries the
/// exception type and message, or <see cref="ProfileReset.KindMismatch"/> when the entry on disk isn't the target's kind.
/// </summary>
internal sealed record ResetResult(
    string Name, ResetTargetKind Kind, ResetState State, long? BytesFreed, string? ErrorType = null, string? ErrorMessage = null);

/// <summary>
/// The outcome of a reset. <see cref="Targets"/> is in target order; <see cref="Leftovers"/> are the <c>&lt;name&gt;.reset-*</c>
/// folders an interrupted reset left behind. <see cref="Kept"/> is never touched. <see cref="Cancelled"/> means cancellation
/// arrived mid-reset: the current target finished and the rest are <see cref="ResetState.Skipped"/>.
/// </summary>
internal sealed record ResetReport(
    IReadOnlyList<ResetResult> Targets, IReadOnlyList<ResetResult> Leftovers, IReadOnlyList<string> Kept, bool Cancelled)
{
    public bool Failed => Targets.Any(t => t.State == ResetState.Failed) || Leftovers.Any(t => t.State == ResetState.Failed);
    /// <summary>Bytes freed as far as they were measured; see <see cref="BytesUnknown"/>.</summary>
    public long BytesFreed => Targets.Concat(Leftovers).Sum(t => t.BytesFreed ?? 0);
    public bool BytesUnknown => Targets.Concat(Leftovers).Any(t => t.State == ResetState.Wiped && t.BytesFreed is null);
}

/// <summary>
/// Wipes everything in the data folder that a fresh profile rebuilds, and never touches the layer 1 object store. A
/// folder is renamed to <c>&lt;name&gt;.reset-&lt;guid&gt;</c> first and deleted after, so a crash never leaves a half-deleted
/// live store (a lone SQLite -wal, say); the next reset deletes such leftovers. Links are removed as links, never followed.
/// </summary>
internal sealed class ProfileReset
{
    /// <summary>The generated store (#179) by folder name; this file must not reference that project.</summary>
    public const string GeneratedFolder = "generated-v1";
    /// <summary>Engine settings, matching JsonSettingsStore.FileName and TemporaryFileName in Infrastructure.</summary>
    public const string SettingsFile = "settings-v1.json", SettingsTemporaryFile = "settings-v1.json.tmp";
    public const string ResetMarker = ".reset-";
    /// <summary>The error type of a target whose entry on disk is a file where a folder is expected, or the reverse.</summary>
    public const string KindMismatch = "KindMismatch";

    /// <summary>Test seam: awaited before each target is wiped, after the cancellation check for it.</summary>
    internal Func<ResetTarget, Task>? BeforeTarget { get; init; }
    /// <summary>Test seam: how a folder's bytes are measured before it is deleted.</summary>
    internal Func<DirectoryInfo, long> Measurer { get; init; } = Measure;

    public static IReadOnlyList<ResetTarget> DefaultTargets { get; } =
    [
        new(DataFolderPaths.CatalogFolder, ResetTargetKind.Folder),
        new(GeneratedFolder, ResetTargetKind.Folder),
        new(SettingsFile, ResetTargetKind.File),
        new(SettingsTemporaryFile, ResetTargetKind.File),
    ];

    private readonly string dataFolder;
    private readonly IReadOnlyList<ResetTarget> targets;

    public ProfileReset(string dataFolder, IReadOnlyList<ResetTarget>? targets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);
        if (!Path.IsPathFullyQualified(dataFolder)) throw new ArgumentException("The data folder must be an absolute path.", nameof(dataFolder));
        this.dataFolder = Path.GetFullPath(dataFolder);
        this.targets = [.. targets ?? DefaultTargets];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in this.targets)
        {
            Validate(target);
            if (!seen.Add(target.Name)) throw new ArgumentException($"Reset target '{target.Name}' is listed twice.", nameof(targets));
        }
    }

    public IReadOnlyList<ResetTarget> Targets => targets;

    private static void Validate(ResetTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var name = target.Name;
        string? problem =
            string.IsNullOrWhiteSpace(name) ? "is empty" :
            Path.IsPathRooted(name) ? "is rooted" :
            name.IndexOfAny(['/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) >= 0 ? "contains a separator" :
            name == "." || name.Contains("..", StringComparison.Ordinal) ? "contains '..'" :
            string.Equals(name, ObjectStore.DefaultFolderName, StringComparison.OrdinalIgnoreCase) ? "is the object store" :
            !Enum.IsDefined(target.Kind) ? "has an unknown kind" :
            null;
        if (problem is not null) throw new ArgumentException($"Reset target '{name}' {problem}.", nameof(target));
    }

    /// <summary>
    /// Awaits every stop in order (a stop that throws fails the reset before anything is deleted), then wipes each target.
    /// Cancellation is honoured up to the first deletion; after that the current target finishes and the rest are skipped.
    /// </summary>
    public async Task<ResetReport> ResetAsync(IReadOnlyList<Func<CancellationToken, Task>> stops, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stops);
        ct.ThrowIfCancellationRequested();
        foreach (var stop in stops)
        {
            await stop(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        var results = new List<ResetResult>(targets.Count);
        var leftovers = new List<ResetResult>();
        var cancelled = false;
        foreach (var target in targets)
        {
            if (cancelled || (results.Count > 0 && ct.IsCancellationRequested))
            {
                cancelled = true;
                results.Add(new(target.Name, target.Kind, ResetState.Skipped, 0));
                continue;
            }
            if (BeforeTarget is not null) await BeforeTarget(target).ConfigureAwait(false);
            // Filesystem work is synchronous; one target at a time off the caller's thread, never cancelled half way.
            var (result, removed) = await Task.Run(() => Wipe(target), CancellationToken.None).ConfigureAwait(false);
            leftovers.AddRange(removed);
            results.Add(result);
        }
        return new(results, leftovers, [ObjectStore.DefaultFolderName], cancelled);
    }

    private (ResetResult Result, List<ResetResult> Leftovers) Wipe(ResetTarget target)
    {
        var leftovers = new List<ResetResult>();
        if (target.Kind == ResetTargetKind.Folder) CleanLeftovers(target.Name, leftovers);
        var path = Path.Combine(dataFolder, target.Name);
        try
        {
            var info = Entry(path);
            if (info is null) return (new(target.Name, target.Kind, ResetState.Absent, 0), leftovers);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                RemoveLink(info);
                return (new(target.Name, target.Kind, ResetState.Wiped, 0), leftovers);
            }
            var isFolder = info is DirectoryInfo;
            if (isFolder != (target.Kind == ResetTargetKind.Folder))
            {
                var found = isFolder ? "a folder" : "a file";
                return (new(target.Name, target.Kind, ResetState.Failed, 0, KindMismatch,
                    $"Expected a {target.Kind.ToString().ToLowerInvariant()} but found {found}; nothing was deleted."), leftovers);
            }
            if (isFolder)
            {
                var parked = Path.Combine(dataFolder, target.Name + ResetMarker + Guid.NewGuid().ToString("N"));
                Directory.Move(path, parked);
                // Look again: whatever now sits at the parked name is what gets deleted, and a link is only unlinked.
                var moved = Entry(parked);
                if (moved is null) return (new(target.Name, target.Kind, ResetState.Wiped, 0), leftovers);
                if (moved.LinkTarget is not null || moved.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    RemoveLink(moved);
                    return (new(target.Name, target.Kind, ResetState.Wiped, 0), leftovers);
                }
                if (moved is not DirectoryInfo parkedFolder)
                {
                    var movedLength = ((FileInfo)moved).Length;
                    moved.Delete();
                    return (new(target.Name, target.Kind, ResetState.Wiped, movedLength), leftovers);
                }
                var bytes = TryMeasure(parkedFolder);
                parkedFolder.Delete(recursive: true);
                return (new(target.Name, target.Kind, ResetState.Wiped, bytes), leftovers);
            }
            var length = ((FileInfo)info).Length;
            info.Delete();
            return (new(target.Name, target.Kind, ResetState.Wiped, length), leftovers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (new(target.Name, target.Kind, ResetState.Failed, 0, e.GetType().Name, e.Message), leftovers);
        }
    }

    /// <summary>Deletes <c>&lt;name&gt;.reset-*</c> folders an interrupted reset left in the data folder.</summary>
    private void CleanLeftovers(string name, List<ResetResult> into)
    {
        var prefix = name + ResetMarker;
        IEnumerable<FileSystemInfo> found;
        try
        {
            if (!Directory.Exists(dataFolder)) return;
            found = [.. new DirectoryInfo(dataFolder).EnumerateFileSystemInfos(prefix + "*")
                .Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            into.Add(new(prefix + "*", ResetTargetKind.Folder, ResetState.Failed, 0, e.GetType().Name, e.Message));
            return;
        }
        foreach (var leftover in found)
        {
            try
            {
                if (leftover.LinkTarget is not null || leftover.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    RemoveLink(leftover);
                    into.Add(new(leftover.Name, ResetTargetKind.Folder, ResetState.Wiped, 0));
                }
                else if (leftover is DirectoryInfo directory)
                {
                    var bytes = TryMeasure(directory);
                    directory.Delete(recursive: true);
                    into.Add(new(leftover.Name, ResetTargetKind.Folder, ResetState.Wiped, bytes));
                }
                // A plain file with the marker is not ours; leave it.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                into.Add(new(leftover.Name, ResetTargetKind.Folder, ResetState.Failed, 0, e.GetType().Name, e.Message));
            }
        }
    }

    /// <summary>The entry at <paramref name="path"/> without following a final link, or null when there is none.</summary>
    private static FileSystemInfo? Entry(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null) return file;
        if (Directory.Exists(path)) return new DirectoryInfo(path);
        return file.Exists ? file : null;
    }

    /// <summary>Removes a symlink or reparse point itself; its target is never touched.</summary>
    private static void RemoveLink(FileSystemInfo link)
    {
        if (OperatingSystem.IsWindows() && link.Attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(link.FullName, recursive: false);
        else File.Delete(link.FullName);
    }

    /// <summary>Best effort: a folder that can't be measured is still deleted, with its size unknown.</summary>
    private long? TryMeasure(DirectoryInfo root)
    {
        try { return Measurer(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    /// <summary>Sum of regular file lengths under <paramref name="root"/>; links count as nothing and are not entered.</summary>
    private static long Measure(DirectoryInfo root)
    {
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (entry is DirectoryInfo child) pending.Push(child);
                else if (entry is FileInfo file) total += file.Length;
            }
        }
        return total;
    }
}
