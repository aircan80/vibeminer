using System.Numerics;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>
/// How much of a build's object ledger (every EKey ENCODING's encoded table lists) the profile's own store holds. Bytes are
/// ENCODING's encoded sizes, never stored lengths or transfer bytes. <see cref="Share"/> is the share of bytes.
/// </summary>
public sealed record DownloadedFigure(long StoredObjects, long TotalObjects, long StoredBytes, long TotalBytes)
{
    public double Share => TotalBytes == 0 ? 0 : (double)StoredBytes / TotalBytes;
}

/// <summary>
/// <see cref="BatchKeys"/>: keys per <see cref="IObjectSource.HasKeysAsync"/> call while measuring. <see cref="MaxBuilds"/>:
/// builds tracked at once (the least recently used is dropped). <see cref="QueueCapacity"/>: commits queued for the tally.
/// <see cref="MaxMeasurements"/>: measurements reading the ledger and the index at once; the rest queue.
/// </summary>
public sealed record DownloadedOptions
{
    public int BatchKeys { get; init; } = 4096;
    public int MaxBuilds { get; init; } = 4;
    public int QueueCapacity { get; init; } = StoreCommitFeed.DefaultCapacity;
    public int MaxMeasurements { get; init; } = 2;

    internal void Validate()
    {
        if (BatchKeys < 1 || MaxBuilds < 1 || QueueCapacity < 1 || MaxMeasurements < 1) throw new ArgumentException("Downloaded options need positive limits.");
    }
}

/// <summary>A build whose object ledger layer 3 can read; only builds opened by <see cref="BuildOpener"/> have one.</summary>
internal interface ILedgerSource
{
    ObjectLedger Ledger { get; }
}

/// <summary>
/// A build's object ledger: ENCODING's encoded rows as flat arrays, EKeys strictly ascending, with their encoded sizes.
/// No per-key objects, so a Retail ledger (about 2.9M rows) is two arrays.
/// </summary>
internal sealed class ObjectLedger
{
    private readonly CdnKey[] keys;
    private readonly long[] sizes;

    public ObjectLedger(CdnKey[] keys, long[] sizes)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(sizes);
        if (keys.Length != sizes.Length) throw new ArgumentException("A ledger has one size per key.");
        long total = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0 && keys[i] <= keys[i - 1]) throw new ArgumentException("Ledger keys must be strictly ascending.");
            if (sizes[i] < 0) throw new ArgumentException("Ledger sizes can't be negative.");
            total += sizes[i];
        }
        this.keys = keys; this.sizes = sizes; TotalBytes = total;
    }

    public static ObjectLedger From(EncodingTable encoding)
    {
        var count = checked((int)encoding.EncodedCount);
        var keys = new CdnKey[count];
        var sizes = new long[count];
        var i = 0;
        foreach (var row in encoding.EncodedRows())
        {
            if (i == count) throw new BuildFormatException(BuildFailure.Corrupt, "EN_INTEGRITY", "EN encoded rows differ from the ones counted at open.");
            keys[i] = row.EKey; sizes[i] = row.EncodedSize; i++;
        }
        if (i != count) throw new BuildFormatException(BuildFailure.Corrupt, "EN_INTEGRITY", "EN encoded rows differ from the ones counted at open.");
        return new(keys, sizes);
    }

    public int Count => keys.Length;
    public long TotalBytes { get; }
    public CdnKey Key(int index) => keys[index];
    public long Size(int index) => sizes[index];

    /// <summary>The row of <paramref name="key"/>, or -1.</summary>
    public int IndexOf(CdnKey key)
    {
        var at = Array.BinarySearch(keys, key);
        return at < 0 ? -1 : at;
    }

    /// <summary>The row of an <see cref="ObjectKeyType.Encoded"/> key in namespace 0, or -1 for any other key.</summary>
    public int IndexOf(ObjectKey key)
    {
        if (key.Type != ObjectKeyType.Encoded || key.Namespace != 0) return -1;
        Span<byte> bytes = stackalloc byte[16];
        key.WriteKey(bytes);
        return IndexOf(new CdnKey(bytes));
    }
}

/// <summary>
/// The Downloaded figure of opened builds. The first <see cref="GetAsync"/> for a build instance measures its ledger against
/// the store index in batches; after that one pump keeps every tracked build's tally from this process's store commits, so a
/// shared object moves every build that lists it. Another process's writes show only after <see cref="RefreshAsync"/> or a
/// reopen (a new <see cref="IBuild"/> instance measures again). A commit the queue dropped makes the pump measure every tracked
/// build again. A commit wins over a measurement in flight: a key it touched keeps the commit's answer.
/// Concurrent first calls share one measurement; cancelling one caller cancels only its wait, and a measurement every caller
/// left is cancelled and forgotten, like a failed one, so the next call measures again.
/// </summary>
public sealed class DownloadedFigures : IAsyncDisposable
{
    private readonly IObjectSource source;
    private readonly DownloadedOptions options;
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, Tracked> tracked = [];
    private readonly CancellationTokenSource stop = new();
    private readonly StoreCommitSubscription? subscription;
    private readonly Task pump = Task.CompletedTask;
    private readonly SemaphoreSlim measuring;
    private readonly HashSet<Measurement> live = [];
    private long clock, sequence;
    private bool disposed;

    public DownloadedFigures(IObjectSource source, IStoreCommits? commits = null, DownloadedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        this.options = options ?? new();
        this.options.Validate();
        measuring = new SemaphoreSlim(this.options.MaxMeasurements, this.options.MaxMeasurements);
        if (commits is null) return;
        subscription = commits.Subscribe(this.options.QueueCapacity);
        pump = Task.Run(() => PumpAsync(subscription, stop.Token));
    }

    /// <summary>
    /// Raised after a commit moved a tracked build's figure, and after each measurement; never once disposal has begun.
    /// Handler exceptions are swallowed. Commit changes are raised on the pump, so handlers must be quick: while one runs,
    /// commits queue, and a full queue drops them, which costs a measurement of every tracked build.
    /// </summary>
    public event Action<BuildRef, DownloadedFigure>? Changed;

    /// <summary>The build's figure: measured on the first call for this build instance, then answered from the tally.</summary>
    public Task<DownloadedFigure> GetAsync(IBuild build, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Measurement m;
        Tracked t;
        lock (gate)
        {
            t = Track(build);
            if (t.Measured) return Task.FromResult(t.Figure());
            m = t.Shared ?? Start(t, pinned: false);
            m.Waiters++;
        }
        return WaitAsync(m, cancellationToken);
    }

    /// <summary>Measures the build again from the store index, so another process's writes show.</summary>
    public Task<DownloadedFigure> RefreshAsync(IBuild build, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Measurement m;
        lock (gate)
        {
            var t = Track(build);
            // One still waiting for a slot hasn't read the index yet, so it sees every write made before this call.
            m = Waiting(t) ?? Start(t, pinned: false);
            m.Waiters++;
        }
        return WaitAsync(m, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        stop.Cancel();
        subscription?.Dispose();
        try { await pump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        // Every measurement, including ones of builds already dropped; stop cancelled them all. None starts after disposed.
        Measurement[] running;
        lock (gate)
        {
            running = live.ToArray();
            tracked.Clear();
        }
        foreach (var m in running)
            try { await m.Task.ConfigureAwait(false); } catch { /* its waiters saw it */ }
        stop.Dispose();
        measuring.Dispose();
    }

    /// <summary>Test hook: a measurement found every slot taken and is queued.</summary>
    internal Action? Queued { get; set; }

    /// <summary>Test hook: the pump has handled a missed queue (started the measurements it needed).</summary>
    internal Action? Remeasured { get; set; }

    /// <summary>Test hook: measurements of the tracked <paramref name="build"/> that haven't read the index yet.</summary>
    internal int WaitingCount(BuildRef build)
    {
        lock (gate) return tracked.TryGetValue(build, out var t) ? t.Active.Count(m => m.Touched is null) : 0;
    }

    // Under gate.
    private Tracked Track(IBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (build is not ILedgerSource ledgerSource) throw new ArgumentException("Only a build opened by BuildOpener has an object ledger.", nameof(build));
        if (!tracked.TryGetValue(build.Build, out var t) || !ReferenceEquals(t.Build, build))
        {
            if (t is not null) Drop(t);
            else if (tracked.Count >= options.MaxBuilds) Drop(tracked.Values.MinBy(x => x.LastUse)!);
            tracked[build.Build] = t = new Tracked(build, ledgerSource);
        }
        t.LastUse = ++clock;
        return t;
    }

    // Under gate. Pinned measurements nobody waits for stop with it; ones with waiters finish for them, or stop when they leave.
    private void Drop(Tracked t)
    {
        tracked.Remove(t.Build.Build);
        foreach (var m in t.Active)
            if (m.Pinned && m.Waiters == 0) CancelLater(m);
    }

    // Callbacks run on another thread, so cancelling under the gate never re-enters it; a finished measurement ignores it.
    private static void CancelLater(Measurement m)
    {
        try { _ = m.Cancel.CancelAsync(); } catch (ObjectDisposedException) { }
    }

    // Under gate. A measurement that hasn't read the index yet (still queued for a slot, or building the ledger) and isn't
    // being cancelled. Joining one doesn't keep it alive by itself: a caller counts as a waiter, the pump pins it.
    private static Measurement? Waiting(Tracked t) => t.Active.FirstOrDefault(m => m.Touched is null && !m.Abandoned);

    // Under gate.
    private bool IsTracked(Tracked t) => tracked.TryGetValue(t.Build.Build, out var current) && current == t;

    // Under gate.
    private Measurement Start(Tracked t, bool pinned)
    {
        var m = new Measurement(t, ++sequence, pinned, CancellationTokenSource.CreateLinkedTokenSource(stop.Token));
        t.Active.Add(m);
        live.Add(m);
        t.Shared = m;
        m.Task = Task.Run(() => MeasureAsync(m));
        // Waiters observe their own failures; this keeps a measurement nobody waits for (a re-measure, a left one) observed.
        _ = m.Task.ContinueWith(static x => _ = x.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return m;
    }

    private async Task<DownloadedFigure> WaitAsync(Measurement m, CancellationToken ct)
    {
        try { return await m.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !m.Task.IsCompleted)
        {
            lock (gate)
                // A pinned one runs for the pump only while its build is tracked; a dropped build's has nobody left.
                if (--m.Waiters == 0 && (!m.Pinned || !IsTracked(m.Owner)))
                {
                    m.Abandoned = true;
                    // Forgotten now, not when it stops, so the next call never joins a measurement being cancelled.
                    if (m.Owner.Shared == m) m.Owner.Shared = null;
                    CancelLater(m);
                }
            throw;
        }
    }

    private async Task<DownloadedFigure> MeasureAsync(Measurement m)
    {
        var t = m.Owner;
        var ct = m.Cancel.Token;
        DownloadedFigure figure;
        var admitted = false;
        try
        {
            if (!measuring.Wait(0))
            {
                Queued?.Invoke();
                await measuring.WaitAsync(ct).ConfigureAwait(false);
            }
            admitted = true;
            // Built outside the gate: the first use reads all of ENCODING once.
            var ledger = t.Source.Ledger;
            lock (gate)
            {
                // From here on every commit marks the rows it touches, so no HasKeys answer below can undo one.
                t.Publish(ledger);
                m.Touched = new ulong[Words(ledger.Count)];
            }
            var fresh = new ulong[Words(ledger.Count)];
            var key = new byte[16];
            for (var start = 0; start < ledger.Count; start += options.BatchKeys)
            {
                ct.ThrowIfCancellationRequested();
                var batch = new ObjectKey[Math.Min(options.BatchKeys, ledger.Count - start)];
                for (var i = 0; i < batch.Length; i++)
                {
                    ledger.Key(start + i).WriteTo(key);
                    batch[i] = new ObjectKey(ObjectKeyType.Encoded, key);
                }
                var has = await source.HasKeysAsync(batch, ct).ConfigureAwait(false);
                if (has.Count != batch.Length) throw new InvalidOperationException("The object source answered a different number of keys.");
                for (var i = 0; i < batch.Length; i++)
                    if (has[i]) fresh[(start + i) >> 6] |= 1UL << ((start + i) & 63);
            }
            lock (gate)
            {
                // A newer measurement already applied is more recent than this one; keep it.
                if (m.Sequence > t.Applied)
                {
                    t.Merge(fresh, m.Touched!);
                    t.Applied = m.Sequence;
                    t.Measured = true;
                }
                figure = t.Figure();
            }
        }
        finally
        {
            if (admitted) measuring.Release();
            lock (gate)
            {
                t.Active.Remove(m);
                live.Remove(m);
                if (t.Shared == m) t.Shared = null;
            }
            m.Cancel.Dispose();
        }
        Raise(t.Build.Build, figure);
        return figure;
    }

    private async Task PumpAsync(StoreCommitSubscription s, CancellationToken ct)
    {
        var reader = s.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (reader.TryRead(out var commit)) Apply(commit);
            if (s.TakeMissed())
            {
                lock (gate)
                    if (!disposed)
                        foreach (var t in tracked.Values)
                            if (t.Measured || t.Active.Count > 0)
                            {
                                // One that hasn't read yet will read after the dropped commits; pin it so its callers leaving can't cancel it.
                                if (Waiting(t) is { } waiting) waiting.Pinned = true;
                                else Start(t, pinned: true);
                            }
                Remeasured?.Invoke();
            }
        }
    }

    private void Apply(StoreCommit commit)
    {
        List<(BuildRef, DownloadedFigure)>? moved = null;
        lock (gate)
        {
            foreach (var t in tracked.Values)
            {
                var ledger = t.Ledger;
                if (ledger is null) continue;
                var changed = false;
                foreach (var k in commit.Stored) changed |= t.Mark(ledger.IndexOf(k), true);
                foreach (var k in commit.Removed) changed |= t.Mark(ledger.IndexOf(k), false);
                if (changed && t.Measured) (moved ??= []).Add((t.Build.Build, t.Figure()));
            }
        }
        if (moved is null) return;
        foreach (var (build, figure) in moved) Raise(build, figure);
    }

    private void Raise(BuildRef build, DownloadedFigure figure)
    {
        if (Volatile.Read(ref disposed) || Changed is not { } handlers) return;
        foreach (var handler in handlers.GetInvocationList())
            try { ((Action<BuildRef, DownloadedFigure>)handler)(build, figure); }
            catch { /* a subscriber's failure never stops the tally */ }
    }

    private static int Words(int bits) => (bits + 63) >> 6;

    private sealed class Measurement(Tracked owner, long sequence, bool pinned, CancellationTokenSource cancel)
    {
        public Tracked Owner { get; } = owner;
        public long Sequence { get; } = sequence;
        /// <summary>The pump needs it (a missed queue): it runs whether or not anyone waits. Set under the gate.</summary>
        public bool Pinned { get; set; } = pinned;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task<DownloadedFigure> Task { get; set; } = null!;
        public int Waiters { get; set; }
        /// <summary>Every waiter left; it is being cancelled and nobody joins it.</summary>
        public bool Abandoned { get; set; }
        /// <summary>Ledger rows a commit set while this measurement ran; allocated with the ledger.</summary>
        public ulong[]? Touched { get; set; }
    }

    /// <summary>One build instance's tally. Every field is guarded by the owner's gate.</summary>
    private sealed class Tracked(IBuild build, ILedgerSource source)
    {
        private ObjectLedger? ledger;
        private ulong[] stored = [];
        private long storedObjects, storedBytes;

        public IBuild Build { get; } = build;
        public List<Measurement> Active { get; } = [];
        public Measurement? Shared { get; set; }
        public long LastUse { get; set; }
        public long Applied { get; set; }
        public bool Measured { get; set; }
        public ILedgerSource Source { get; } = source;
        /// <summary>Null until the first measurement has the ledger; commits before then are all older than its HasKeys answers.</summary>
        public ObjectLedger? Ledger => ledger;

        public void Publish(ObjectLedger l)
        {
            if (ledger is not null) return;
            ledger = l;
            stored = new ulong[Words(l.Count)];
        }

        public DownloadedFigure Figure() => new(storedObjects, ledger?.Count ?? 0, storedBytes, ledger?.TotalBytes ?? 0);

        /// <summary>Sets one row from a commit and marks it touched for every measurement in flight. True when the tally moved.</summary>
        public bool Mark(int row, bool present)
        {
            if (row < 0) return false;
            int word = row >> 6; var bit = 1UL << (row & 63);
            foreach (var m in Active) if (m.Touched is { } touched) touched[word] |= bit;
            if (((stored[word] & bit) != 0) == present) return false;
            stored[word] ^= bit;
            var sign = present ? 1 : -1;
            storedObjects += sign;
            storedBytes += sign * ledger!.Size(row);
            return true;
        }

        /// <summary>Takes a measurement's answers for every row no commit touched while it ran.</summary>
        public void Merge(ulong[] fresh, ulong[] touched)
        {
            for (var w = 0; w < stored.Length; w++)
            {
                var next = (stored[w] & touched[w]) | (fresh[w] & ~touched[w]);
                var diff = next ^ stored[w];
                while (diff != 0)
                {
                    var b = BitOperations.TrailingZeroCount(diff);
                    diff &= diff - 1;
                    var row = (w << 6) + b;
                    var sign = (next >> b & 1) != 0 ? 1 : -1;
                    storedObjects += sign;
                    storedBytes += sign * ledger!.Size(row);
                }
                stored[w] = next;
            }
        }
    }
}
