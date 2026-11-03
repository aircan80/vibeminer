using VibeMiner.Objects;

namespace VibeMiner.Objects.Testing;

/// <summary>Independent logical-file manifest for consumers of ILibraryFootprint; never accesses a filesystem.</summary>
public sealed class InMemoryLibraryFootprint : ILibraryFootprint
{
    private sealed record Entry(bool Directory, long Length, LibraryFootprintProblemCode? Problem = null);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal)
    { [""] = new(true, 0), ["packs"] = new(true, 0), ["staging"] = new(true, 0), ["objects.sqlite3"] = new(false, 0) };
    private readonly LibraryFootprintOptions options;
    private readonly HashSet<Work> workers = [];
    private bool closed;
    private Task? closing;
    private sealed class Work(CancellationToken caller)
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        public readonly TaskCompletionSource<LibraryFootprintSample> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Canceling;
        public bool Retiring;
    }
    public InMemoryLibraryFootprint(LibraryFootprintOptions? options = null)
    { this.options = options ?? new(); this.options.Validate(); }

    public void SetFile(string relativePath, long length)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Set(relativePath, new(false, length));
    }
    public void SetDirectory(string relativePath) => Set(relativePath, new(true, 0));
    public void SetProblem(string relativePath, LibraryFootprintProblemCode code) => Set(relativePath, new(false, 0, code));
    public void Remove(string relativePath) { lock (gate) entries.Remove(Path(relativePath)); }
    public void Clear() { lock (gate) entries.Clear(); }
    private static string Path(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length > 8192 || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0') || path.Split('/').Any(p => p is "." or ".."))
            throw new ArgumentException("Expected a bounded relative manifest path.", nameof(path));
        return path.TrimEnd('/');
    }
    private void Set(string path, Entry entry)
    {
        path = Path(path);
        lock (gate)
        {
            if (entries.Count >= 1_000_000 && !entries.ContainsKey(path)) throw new ArgumentException("The fake manifest is full.");
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) entries.TryAdd(path[..slash], new(true, 0));
            entries[path] = entry;
        }
    }

    public ValueTask<LibraryFootprintSample> MeasureAsync(CancellationToken ct = default)
    {
        Work work;
        lock (gate)
        {
            if (closed) throw new ObjectStoreException(LibraryFootprintCodes.Closed, "The fake footprint view is closed.");
            ct.ThrowIfCancellationRequested();
            if (workers.Count >= options.MaxConcurrentMeasurements) throw new ObjectStoreException(LibraryFootprintCodes.Busy, "The fake metadata worker budget is in use.");
            work = new(ct); workers.Add(work);
        }
        _ = Task.Run(async () =>
        {
            LibraryFootprintSample? result = null; Exception? failure = null; var token = work.Stop.Token;
            try { result = Sample(token); } catch (Exception error) { failure = error; }
            Task? canceled; lock (gate) { work.Retiring = true; canceled = work.Canceling; }
            if (canceled is not null) try { await canceled.ConfigureAwait(false); } catch (Exception) { }
            lock (gate)
            {
                work.Stop.Dispose(); workers.Remove(work);
                if (failure is OperationCanceledException) work.Done.TrySetCanceled(token);
                else if (failure is not null) work.Done.TrySetException(failure);
                else work.Done.TrySetResult(result!);
            }
        });
        return new(work.Done.Task);
    }
    private Entry? Find(string path) { lock (gate) return entries.GetValueOrDefault(path); }
    private LibraryFootprintSample Sample(CancellationToken ct)
    {
        var start = options.Time.GetUtcNow();
        var counts = new long[6]; var bytes = new long[6]; long totalCount = 0, totalBytes = 0, examined = 0;
        var problems = new List<LibraryFootprintProblem>(); var complete = true; var truncated = false; var stopped = false;
        void Problem(string path, LibraryFootprintProblemCode code)
        { complete = false; if (problems.Count < options.MaxProblems) problems.Add(new(path.Length <= options.MaxPathLength ? path : "", code)); else truncated = true; }
        void Hook(LibraryFootprintPoint point, string path) { ct.ThrowIfCancellationRequested(); options.Fault?.Invoke(point, path); ct.ThrowIfCancellationRequested(); }
        Hook(LibraryFootprintPoint.MeasurementStarted, ""); Hook(LibraryFootprintPoint.BeforeRootOpen, "");
        var root = Find("");
        if (root is null) Problem("", LibraryFootprintProblemCode.MissingRequiredPath);
        else if (root.Problem is { } rootProblem) Problem("", rootProblem);
        else if (!root.Directory) Problem("", LibraryFootprintProblemCode.UnsupportedEntry);
        else Walk("", 0);
        Hook(LibraryFootprintPoint.MeasurementFinishing, "");
        LibraryFootprintBucket Bucket(int i) => new(counts[i], bytes[i]);
        return new(start, options.Time.GetUtcNow(), Bucket(0), Bucket(1), Bucket(2), Bucket(3), Bucket(4), Bucket(5),
            new(totalCount, totalBytes), examined, complete, problems.AsReadOnly(), truncated);

        void Walk(string parent, int depth)
        {
            // The fake snapshots bounded fixture names, independently of the production descriptor enumerator. Per-entry
            // values are sampled later, so deterministic mutation hooks still expose disappearance and interval semantics.
            string[] children;
            lock (gate) children = entries.Keys.Where(p => p.Length > parent.Length &&
                (parent.Length == 0 ? !p.Contains('/') : p.StartsWith(parent + "/", StringComparison.Ordinal) && !p.AsSpan(parent.Length + 1).Contains('/')))
                .Order(StringComparer.Ordinal).ToArray();
            HashSet<string> required = parent.Length == 0 ? new(["packs", "staging", "objects.sqlite3"], StringComparer.Ordinal) : new(StringComparer.Ordinal);
            foreach (var path in children)
            {
                ct.ThrowIfCancellationRequested();
                if (stopped) break;
                if (examined == options.MaxEntries) { Problem(parent, LibraryFootprintProblemCode.EntryLimit); stopped = true; break; }
                examined++;
                if (path.Length > options.MaxPathLength) { Problem(parent, LibraryFootprintProblemCode.PathLimit); continue; }
                try
                {
                    Hook(LibraryFootprintPoint.BeforeEntryMetadata, path);
                    var entry = Find(path);
                    if (entry is null) { Problem(path, LibraryFootprintProblemCode.Disappeared); continue; }
                    if (entry.Problem is { } bad) { Problem(path, bad); continue; }
                    if (required.Contains(path))
                    {
                        if (path == "objects.sqlite3" ? !entry.Directory : entry.Directory) required.Remove(path);
                        else { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); continue; }
                    }
                    if (entry.Directory)
                    {
                        if (depth >= options.MaxDepth) { Problem(path, LibraryFootprintProblemCode.DepthLimit); continue; }
                        Hook(LibraryFootprintPoint.BeforeDirectoryOpen, path); Walk(path, depth + 1);
                    }
                    else
                    {
                        Hook(LibraryFootprintPoint.BeforeFileLength, path); entry = Find(path);
                        if (entry is null) { Problem(path, LibraryFootprintProblemCode.Disappeared); continue; }
                        if (entry.Problem is { } changed) { Problem(path, changed); continue; }
                        if (entry.Directory) { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); continue; }
                        var length = options.LengthOverride?.Invoke(path, entry.Length) ?? entry.Length;
                        if (length < 0) { Problem(path, LibraryFootprintProblemCode.UnsupportedEntry); continue; }
                        var category = Classify(path);
                        try
                        {
                            var count = checked(counts[category] + 1); var size = checked(bytes[category] + length);
                            var allCount = checked(totalCount + 1); var allBytes = checked(totalBytes + length);
                            counts[category] = count; bytes[category] = size; totalCount = allCount; totalBytes = allBytes;
                        }
                        catch (OverflowException) { Problem(path, LibraryFootprintProblemCode.Overflow); stopped = true; }
                    }
                }
                catch (FileNotFoundException) { Problem(path, LibraryFootprintProblemCode.Disappeared); }
                catch (DirectoryNotFoundException) { Problem(path, LibraryFootprintProblemCode.Disappeared); }
                catch (IOException) { Problem(path, LibraryFootprintProblemCode.Unreadable); }
                catch (UnauthorizedAccessException) { Problem(path, LibraryFootprintProblemCode.Unreadable); }
            }
            if (!stopped) foreach (var path in required) Problem(path, LibraryFootprintProblemCode.MissingRequiredPath);
        }
    }
    private static int Classify(string path)
    {
        if (path is "objects.sqlite3") return 1;
        if (path is "objects.sqlite3-wal") return 2;
        if (path is "objects.sqlite3-shm") return 3;
        var pieces = path.Split('/');
        if (pieces.Length == 2 && pieces[0] == "packs" && pieces[1].Length == 15 && pieces[1].StartsWith("pack-", StringComparison.Ordinal) && pieces[1].EndsWith(".vmp", StringComparison.Ordinal) &&
            pieces[1].AsSpan(5, 6).IndexOfAnyExceptInRange('0', '9') < 0 && pieces[1].AsSpan(5, 6).IndexOfAnyExcept('0') >= 0) return 0;
        return pieces.Length == 2 && pieces[0] == "staging" && pieces[1].EndsWith(".part", StringComparison.Ordinal) ? 4 : 5;
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closing is not null) return new(closing);
            closed = true; var pending = workers.ToArray();
            foreach (var work in pending) if (!work.Retiring) work.Canceling ??= work.Stop.CancelAsync();
            closing = Drain(pending); return new(closing);
        }
    }
    private static async Task Drain(Work[] pending)
    { foreach (var work in pending) try { await work.Done.Task.ConfigureAwait(false); } catch (Exception) { } }
}
