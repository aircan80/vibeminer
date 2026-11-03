namespace VibeMiner.Objects;

public sealed partial class ObjectStore
{
    private readonly object footprintGate = new();
    private int footprintWorkers;
    private LibraryFootprintNative.Metadata? footprintRoot;
    private LibraryFootprintProblemCode? footprintRootProblem;

    // Capture physical directory identity during the already-explicit open. Failure affects only the new measurement;
    // opening an existing store does not acquire a new platform/confinement requirement.
    private void CaptureFootprintRoot()
    {
        if (Kind != ObjectStoreKind.Library) return;
        if (!LibraryFootprintNative.Supported) { footprintRootProblem = LibraryFootprintProblemCode.UnsupportedPlatform; return; }
        if (directory.Length > 4096) { footprintRootProblem = LibraryFootprintProblemCode.PathLimit; return; }
        try { using var root = LibraryFootprintNative.Directory.OpenRoot(directory, CancellationToken.None); footprintRoot = root.Identity; }
        catch (IOException error) { footprintRootProblem = LibraryFootprintNative.ErrorCode(LibraryFootprintNative.ErrorNumber(error), required: true); }
        catch (UnauthorizedAccessException) { footprintRootProblem = LibraryFootprintProblemCode.Unreadable; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { footprintRootProblem = LibraryFootprintProblemCode.UnsupportedPlatform; }
    }

    /// <summary>
    /// Borrows this explicit mixed library for metadata-only file-length sampling; it does not scan a profile or open payloads.
    /// Linux/macOS x64/arm64 use descriptor-relative no-follow traversal. Other platforms return an incomplete unsupported sample.
    /// </summary>
    public ILibraryFootprint ForFootprint(LibraryFootprintOptions? options = null)
    {
        ThrowIfClosed();
        if (Kind != ObjectStoreKind.Library) throw new InvalidOperationException("File footprint measurement requires a mixed library.");
        var selected = options ?? new(); selected.Validate();
        return new LibraryFootprint(this, directory, footprintRoot, footprintRootProblem, selected);
    }

    internal void AdmitFootprint()
    {
        lock (footprintGate)
        {
            ThrowIfClosed();
            if (footprintWorkers >= 2) throw new ObjectStoreException(LibraryFootprintCodes.Busy, "The library metadata worker budget is in use.");
            footprintWorkers++;
        }
    }
    internal void ReleaseFootprint() { lock (footprintGate) footprintWorkers--; }
}

internal sealed class LibraryFootprint(ObjectStore store, string directory, LibraryFootprintNative.Metadata? rootIdentity,
    LibraryFootprintProblemCode? rootProblem, LibraryFootprintOptions options) : ILibraryFootprint
{
    private readonly object gate = new();
    private readonly HashSet<Work> accepted = [];
    private bool closed;
    private Task? closing;
    private sealed class Work(CancellationToken caller)
    {
        internal readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        internal readonly TaskCompletionSource<LibraryFootprintSample> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Canceling;
        internal bool Retiring;
    }

    public ValueTask<LibraryFootprintSample> MeasureAsync(CancellationToken ct = default)
    {
        Work work;
        lock (gate)
        {
            if (closed) throw new ObjectStoreException(LibraryFootprintCodes.Closed, "The library footprint view is closed.");
            ct.ThrowIfCancellationRequested();
            if (accepted.Count >= options.MaxConcurrentMeasurements)
                throw new ObjectStoreException(LibraryFootprintCodes.Busy, "The footprint view metadata worker budget is in use.");
            store.AdmitFootprint();
            try { work = new(ct); accepted.Add(work); }
            catch { store.ReleaseFootprint(); throw; }
        }
        // There is no waiting queue: at most two accepted workers across all views of this library reach Task.Run.
        _ = Task.Run(async () =>
        {
            LibraryFootprintSample? sample = null; Exception? failure = null; var token = work.Stop.Token;
            try { token.ThrowIfCancellationRequested(); sample = new LibraryFootprintWalker(directory, rootIdentity, rootProblem, options, token).Measure(); }
            catch (Exception error) { failure = error; }
            Task? canceling; lock (gate) { work.Retiring = true; canceling = work.Canceling; }
            if (canceling is not null) { try { await canceling.ConfigureAwait(false); } catch (Exception) { } }
            lock (gate)
            {
                work.Stop.Dispose(); accepted.Remove(work); store.ReleaseFootprint();
                if (failure is OperationCanceledException) work.Done.TrySetCanceled(token);
                else if (failure is not null) work.Done.TrySetException(failure);
                else work.Done.TrySetResult(sample!);
            }
        });
        return new(work.Done.Task);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closing is not null) return new(closing);
            closed = true;
            var works = accepted.ToArray();
            foreach (var work in works) if (!work.Retiring) work.Canceling ??= work.Stop.CancelAsync();
            closing = DrainAsync(works);
            return new(closing);
        }
    }
    private static async Task DrainAsync(Work[] works)
    {
        foreach (var work in works)
            try { await work.Done.Task.ConfigureAwait(false); } catch (Exception) { }
    }
}

internal sealed class LibraryFootprintWalker(string directory, LibraryFootprintNative.Metadata? expectedRoot,
    LibraryFootprintProblemCode? rootProblem, LibraryFootprintOptions options, CancellationToken ct)
{
    private readonly long[] files = new long[6], lengths = new long[6];
    private long totalFiles, totalBytes, examined;
    private bool complete = true, truncated, halted;
    private readonly List<LibraryFootprintProblem> problems = [];
    private readonly HashSet<string> required = new(StringComparer.Ordinal) { "packs", "staging", "objects.sqlite3" };

    internal LibraryFootprintSample Measure()
    {
        var started = options.Time.GetUtcNow();
        Hook(LibraryFootprintPoint.MeasurementStarted, "");
        if (!LibraryFootprintNative.Supported) Problem("", LibraryFootprintProblemCode.UnsupportedPlatform);
        else if (rootProblem is { } unavailable) Problem("", unavailable);
        else if (directory.Length > 4096) Problem("", LibraryFootprintProblemCode.PathLimit);
        else
        {
            try
            {
                Hook(LibraryFootprintPoint.BeforeRootOpen, "");
                using var root = LibraryFootprintNative.Directory.OpenRoot(directory, ct);
                if (expectedRoot is not { } identity || !root.Identity.SameIdentity(identity)) Problem("", LibraryFootprintProblemCode.ChangedDirectory);
                else
                {
                    Walk(root, "", 0);
                    if (!halted) foreach (var missing in required) Problem(missing, LibraryFootprintProblemCode.MissingRequiredPath);
                    // Resolve the captured name again without following any component. Renaming/replacing the root cannot
                    // redirect the pinned traversal, and also cannot silently yield a complete sample of its replacement.
                    using var current = LibraryFootprintNative.Directory.OpenRoot(directory, ct);
                    if (!current.Identity.SameIdentity(identity)) Problem("", LibraryFootprintProblemCode.ChangedDirectory);
                }
            }
            catch (IOException error) { IoProblem("", error, required: true); }
            catch (UnauthorizedAccessException) { Problem("", LibraryFootprintProblemCode.Unreadable); }
        }
        Hook(LibraryFootprintPoint.MeasurementFinishing, "");
        ct.ThrowIfCancellationRequested();
        var completed = options.Time.GetUtcNow();
        LibraryFootprintBucket Bucket(int index) => new(files[index], lengths[index]);
        return new(started, completed, Bucket(0), Bucket(1), Bucket(2), Bucket(3), Bucket(4), Bucket(5),
            new(totalFiles, totalBytes), examined, complete, problems.AsReadOnly(), truncated);
    }

    private void Walk(LibraryFootprintNative.Directory parent, string relative, int depth)
    {
        while (!halted)
        {
            ct.ThrowIfCancellationRequested(); string? name;
            try { name = parent.Next(); }
            catch (IOException error) { IoProblem(relative, error); return; }
            catch (System.Text.DecoderFallbackException) { Problem(relative, LibraryFootprintProblemCode.UnsupportedEntry); return; }
            if (name is null) return;
            if (name is "." or "..") continue;
            if (examined >= options.MaxEntries) { Problem(relative, LibraryFootprintProblemCode.EntryLimit); halted = true; return; }
            examined++;
            var path = relative.Length == 0 ? name : relative + "/" + name;
            if (path.Length > options.MaxPathLength) { Problem(relative, LibraryFootprintProblemCode.PathLimit); continue; }
            try
            {
                Hook(LibraryFootprintPoint.BeforeEntryMetadata, path);
                var entry = parent.Get(name);
                if (entry.IsLink) { Problem(path, LibraryFootprintProblemCode.Link); continue; }
                if (relative.Length == 0 && required.Contains(name))
                {
                    if (name == "objects.sqlite3" ? entry.IsRegular : entry.IsDirectory) required.Remove(name);
                    else { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); continue; }
                }
                if (entry.IsDirectory)
                {
                    if (depth >= options.MaxDepth) { Problem(path, LibraryFootprintProblemCode.DepthLimit); continue; }
                    Hook(LibraryFootprintPoint.BeforeDirectoryOpen, path);
                    using var child = parent.OpenChild(name);
                    if (!child.Identity.SameIdentity(entry) || child.Identity.Device != parent.Identity.Device)
                    { Problem(path, LibraryFootprintProblemCode.ChangedDirectory); continue; }
                    Walk(child, path, depth + 1);
                    var after = parent.Get(name);
                    if (after.IsLink) Problem(path, LibraryFootprintProblemCode.Link);
                    else if (!after.SameIdentity(entry)) Problem(path, LibraryFootprintProblemCode.ChangedDirectory);
                }
                else if (entry.IsRegular)
                {
                    Hook(LibraryFootprintPoint.BeforeFileLength, path);
                    // Re-stat after the hook: disappearance/link replacement is a missing observation, not a stale length.
                    var current = parent.Get(name);
                    if (current.IsLink) { Problem(path, LibraryFootprintProblemCode.Link); continue; }
                    if (!current.IsRegular) { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); continue; }
                    var length = options.LengthOverride?.Invoke(path, current.Length) ?? current.Length;
                    Add(path, length);
                }
                else Problem(path, LibraryFootprintProblemCode.UnsupportedEntry);
            }
            catch (IOException error) { IoProblem(path, error); }
            catch (UnauthorizedAccessException) { Problem(path, LibraryFootprintProblemCode.Unreadable); }
        }
    }

    private void Add(string path, long bytes)
    {
        if (bytes < 0) { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); return; }
        var category = Category(path);
        try
        {
            // Calculate every checked value first, so an overflowing entry leaves reconciled prefix totals intact.
            var count = checked(files[category] + 1); var length = checked(lengths[category] + bytes);
            var allCount = checked(totalFiles + 1); var allBytes = checked(totalBytes + bytes);
            files[category] = count; lengths[category] = length; totalFiles = allCount; totalBytes = allBytes;
        }
        catch (OverflowException) { Problem(path, LibraryFootprintProblemCode.Overflow); halted = true; }
    }
    private static int Category(string path)
    {
        if (path == "objects.sqlite3") return 1;
        if (path == "objects.sqlite3-wal") return 2;
        if (path == "objects.sqlite3-shm") return 3;
        if (path.StartsWith("packs/", StringComparison.Ordinal) && PackFiles.TryParse(path[6..], out _)) return 0;
        if (path.StartsWith("staging/", StringComparison.Ordinal) && !path.AsSpan(8).Contains('/') && path.Length >= 13 && path.EndsWith(".part", StringComparison.Ordinal)) return 4;
        return 5;
    }
    private void Hook(LibraryFootprintPoint point, string path) { ct.ThrowIfCancellationRequested(); options.Fault?.Invoke(point, path); ct.ThrowIfCancellationRequested(); }
    private void IoProblem(string path, IOException error, bool required = false) =>
        Problem(path, LibraryFootprintNative.ErrorNumber(error) == 116 ? LibraryFootprintProblemCode.ChangedDirectory : LibraryFootprintNative.ErrorCode(LibraryFootprintNative.ErrorNumber(error), required));
    private void Problem(string path, LibraryFootprintProblemCode code)
    {
        complete = false;
        if (problems.Count < options.MaxProblems) problems.Add(new(path.Length <= options.MaxPathLength ? path : "", code));
        else truncated = true;
    }
}
