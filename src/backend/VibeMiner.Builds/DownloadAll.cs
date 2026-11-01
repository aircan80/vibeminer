using System.Numerics;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>
/// <see cref="ScanBatchKeys"/>: keys per <see cref="IObjectSource.HasKeysAsync"/> call while scanning. <see cref="FetchBatch"/>:
/// requests per <see cref="IObjectSource.GetKeysAsync"/> call. <see cref="MaxJobs"/>: builds switched on at once.
/// <see cref="FreeReserveBytes"/>: free space kept beyond what is left to fetch. <see cref="ProgressInterval"/>: least time
/// between two progress reports (stage changes and the final one are always reported). <see cref="Time"/> times them.
/// </summary>
public sealed record DownloadAllOptions
{
    public int ScanBatchKeys { get; init; } = 4096;
    public int FetchBatch { get; init; } = 4096;
    public int MaxJobs { get; init; } = 4;
    public long FreeReserveBytes { get; init; } = 1L << 30;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeProvider Time { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (ScanBatchKeys < 1 || FetchBatch < 1 || MaxJobs < 1 || FreeReserveBytes < 0 || ProgressInterval < TimeSpan.Zero || Time is null)
            throw new ArgumentException("Download all options need positive limits, a non-negative reserve and interval, and a clock.");
    }
}

public enum DownloadAllStage { Scanning, Fetching }

/// <summary>
/// A job's figures over the build's object ledger (ENCODING's encoded rows and their encoded sizes, as the Downloaded figure
/// counts). Stored is what the scan found stored plus what the job fetched since; during Scanning only scanned rows count.
/// <see cref="FailedObjects"/> came back NotFound, Unavailable or Refused. <see cref="CdnBytes"/> is layer 2's transfer
/// bytes (gap bytes included) summed over the job's batches. A job that stops before scanning reports zeros.
/// Within a fetch batch, <see cref="StoredObjects"/> also counts the objects layer 2 has finished so far (its per-call
/// <see cref="ObjectProgress.Done"/>), since layer 2 doesn't say which of them failed until the batch ends; it never goes
/// down, so a batch's failures show in <see cref="FailedObjects"/> only once later objects cover them, and stored plus
/// failed never counts an object twice. <see cref="StoredBytes"/> moves only when a batch ends (layer 2 reports no
/// per-object bytes). Only a job's final snapshot is exact, and it may hold fewer stored objects than the one before it
/// when its last batch had failures.
/// </summary>
public sealed record DownloadAllProgress(BuildRef Build, DownloadAllStage Stage, long StoredObjects, long TotalObjects, long StoredBytes,
    long TotalBytes, long FailedObjects, long CdnBytes);

public enum DownloadAllOutcome
{
    /// <summary>Every ledger object is stored.</summary>
    Completed,
    /// <summary>The pass finished, but some objects came back NotFound, Unavailable or Refused (see the counts).</summary>
    Incomplete,
    /// <summary>Turned off, or its owner was disposed.</summary>
    Canceled,
    /// <summary>Not enough free space before it started (<c>DOWNLOAD_ALL_NOT_ENOUGH_SPACE</c>), or the store ran out of space.</summary>
    DiskFull,
    /// <summary>The store couldn't take the bytes, or free space couldn't be read (<c>DOWNLOAD_ALL_FREE_SPACE_UNKNOWN</c>).</summary>
    StoreFailed,
    /// <summary>No CDN route (<c>OBJECT_ACCESS_NO_CDN_CONTEXT</c>, nothing fetched), the CDN is offline, or the source is local only.</summary>
    NeedsNetwork,
    /// <summary>Something unexpected threw (a broken ledger, a source bug): the code is <c>DOWNLOAD_ALL_FAILED</c> or the ledger's.</summary>
    Failed,
}

/// <summary>A job's one terminal result. <see cref="Final"/> is its last progress snapshot.</summary>
public sealed record DownloadAllResult(BuildRef Build, DownloadAllOutcome Outcome, DownloadAllProgress Final, long NotFound, long Unavailable,
    long Refused, string? Code, string? Message);

/// <summary>
/// Per-build Download all: each job fetches every object of an opened build's ledger through layer 2 at background priority,
/// so interactive work keeps its own streams. A job scans the ledger against the store, checks free space, then asks for the
/// missing objects in archive order. It never deletes anything: turning it off (<see cref="StopAsync"/>) keeps what it stored,
/// and a new job, in this process or the next, starts from what the store holds (layer 1 staging resumes a large object).
/// One job per build; a finished job leaves the table, so <see cref="Start"/> runs it again.
/// </summary>
public sealed class DownloadAllJobs : IAsyncDisposable
{
    /// <summary>Terminal results kept for <see cref="StopAsync"/> after their job left the table.</summary>
    private const int KeptResults = 64;
    private readonly IObjectSource source;
    private readonly Func<CancellationToken, ValueTask<long>> freeBytes;
    private readonly DownloadAllOptions options;
    private readonly Lock gate = new();
    private readonly Dictionary<BuildRef, DownloadAllJob> running = [];
    private readonly Dictionary<BuildRef, DownloadAllResult> results = [];
    private readonly Queue<BuildRef> resultOrder = new();
    private readonly CancellationTokenSource stop = new();
    private bool disposed;

    /// <param name="freeBytes">Free bytes on the volume holding the object store.</param>
    public DownloadAllJobs(IObjectSource source, Func<CancellationToken, ValueTask<long>> freeBytes, DownloadAllOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(freeBytes);
        this.source = source; this.freeBytes = freeBytes;
        this.options = options ?? new();
        this.options.Validate();
    }

    /// <summary>
    /// Switches Download all on for the build, or returns its running job. Throws <see cref="ArgumentException"/> for a build
    /// with no ledger (one not opened by <see cref="BuildOpener"/>) and <see cref="InvalidOperationException"/> past
    /// <see cref="DownloadAllOptions.MaxJobs"/>. <paramref name="progress"/> is called on a thread-pool thread, never under
    /// the job's lock or on layer 2's threads, one call at a time and in order; a slow subscriber only sees fewer, newer
    /// snapshots (stage changes and the final one are always delivered, the final last). Its exceptions are swallowed. The
    /// job's <see cref="DownloadAllJob.Completion"/> completes after the final snapshot was delivered.
    /// </summary>
    public DownloadAllJob Start(IBuild build, IProgress<DownloadAllProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (build is not ILedgerSource ledger) throw new ArgumentException("Only a build opened by BuildOpener has an object ledger.", nameof(build));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running.TryGetValue(build.Build, out var job)) return job;
            if (running.Count >= options.MaxJobs) throw new InvalidOperationException($"Download all runs for at most {options.MaxJobs} builds at once.");
            job = new DownloadAllJob(build, ledger, source, freeBytes, options, progress, CancellationTokenSource.CreateLinkedTokenSource(stop.Token));
            running[build.Build] = job;
            job.Completion = Task.Run(() => job.RunAsync(Retire));
            return job;
        }
    }

    /// <summary>The build's running job, or null.</summary>
    public DownloadAllJob? Find(BuildRef build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (gate) return running.GetValueOrDefault(build);
    }

    /// <summary>
    /// Turns the build's job off: cancels it and returns once it has fully stopped, with its one terminal result (Completed when
    /// its last batch finished first). With no job running, the last job's result, or null when none ran. Stored objects stay.
    /// </summary>
    public async Task<DownloadAllResult?> StopAsync(BuildRef build)
    {
        ArgumentNullException.ThrowIfNull(build);
        DownloadAllJob? job;
        DownloadAllResult? last;
        lock (gate)
        {
            running.TryGetValue(build, out job);
            results.TryGetValue(build, out last);
        }
        if (job is null) return last;
        job.Cancel();
        return await job.Completion.ConfigureAwait(false);
    }

    /// <summary>Stops every job and waits until each has stopped.</summary>
    public async ValueTask DisposeAsync()
    {
        DownloadAllJob[] jobs;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            jobs = running.Values.ToArray();
        }
        stop.Cancel();
        foreach (var job in jobs) await job.Completion.ConfigureAwait(false);
        stop.Dispose();
    }

    // Runs on the job before its Completion completes, so whoever awaited it sees the job gone from the table.
    private void Retire(DownloadAllJob job, DownloadAllResult result)
    {
        lock (gate)
        {
            if (running.TryGetValue(job.Build, out var current) && current == job) running.Remove(job.Build);
            if (!results.ContainsKey(job.Build))
            {
                resultOrder.Enqueue(job.Build);
                if (resultOrder.Count > KeptResults) results.Remove(resultOrder.Dequeue());
            }
            results[job.Build] = result;
        }
    }
}

/// <summary>One build's Download all run. <see cref="Completion"/> never faults; <see cref="Latest"/> is always current.</summary>
public sealed class DownloadAllJob
{
    private const string NoContext = "OBJECT_ACCESS_NO_CDN_CONTEXT";
    /// <summary>Archive position of a row no archive lists: after every archived row.</summary>
    private const int NotArchived = int.MaxValue;
    private readonly IBuild build;
    private readonly ILedgerSource ledgerSource;
    private readonly IObjectSource source;
    private readonly Func<CancellationToken, ValueTask<long>> freeBytes;
    private readonly DownloadAllOptions options;
    private readonly IProgress<DownloadAllProgress>? progress;
    private readonly CancellationTokenSource cancel;
    // Serialises Cancel with the source's Dispose; nothing else takes it.
    private readonly Lock cancelGate = new();
    private bool cancelDisposed;
    // Guards the figures and the outbox below. Never held while the subscriber runs.
    private readonly Lock gate = new();
    private DownloadAllProgress latest;
    private DownloadAllStage stage;
    private long storedObjects, totalObjects, storedBytes, totalBytes, notFound, unavailable, refused, cdnDone, cdnBatch;
    // Objects layer 2 finished in the current batch, and the most StoredObjects ever shown.
    private long batchDone, batchSize, shownStored;
    private int batch;
    private long lastDelivered;
    private bool delivered, finished;
    // Snapshots waiting for the subscriber, oldest first. A Kept one (a stage change or the final) is never replaced; a
    // coalesced one at the end is replaced by a newer one, so it holds at most three Kept plus one.
    private readonly List<(DownloadAllProgress Snapshot, bool Kept)> outbox = [];
    private bool draining;
    private TaskCompletionSource? drained;

    internal DownloadAllJob(IBuild build, ILedgerSource ledgerSource, IObjectSource source, Func<CancellationToken, ValueTask<long>> freeBytes,
        DownloadAllOptions options, IProgress<DownloadAllProgress>? progress, CancellationTokenSource cancel)
    {
        this.build = build; this.ledgerSource = ledgerSource; this.source = source; this.freeBytes = freeBytes;
        this.options = options; this.progress = progress; this.cancel = cancel;
        Build = build.Build;
        latest = new(Build, DownloadAllStage.Scanning, 0, 0, 0, 0, 0, 0);
    }

    public BuildRef Build { get; }
    public Task<DownloadAllResult> Completion { get; internal set; } = null!;
    public DownloadAllProgress Latest { get { lock (gate) return latest; } }

    internal void Cancel()
    {
        lock (cancelGate)
            if (!cancelDisposed) cancel.Cancel();
    }

    internal async Task<DownloadAllResult> RunAsync(Action<DownloadAllJob, DownloadAllResult> retire)
    {
        var ct = cancel.Token;
        DownloadAllResult result;
        try { result = await RunCoreAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { result = Finish(DownloadAllOutcome.Canceled, null, "Download all was turned off."); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.DiskFull) { result = Finish(DownloadAllOutcome.DiskFull, e.Code, e.Message); }
        catch (ObjectStoreException e) { result = Finish(DownloadAllOutcome.StoreFailed, e.Code, e.Message); }
        catch (BuildFormatException e) { result = Finish(DownloadAllOutcome.Failed, e.Code, e.Message); }
        catch (Exception e) { result = Finish(DownloadAllOutcome.Failed, "DOWNLOAD_ALL_FAILED", $"{e.GetType().Name}: {e.Message}"); }
        await DrainedAsync().ConfigureAwait(false);
        retire(this, result);
        lock (cancelGate)
        {
            cancelDisposed = true;
            cancel.Dispose();
        }
        return result;
    }

    private async Task<DownloadAllResult> RunCoreAsync(CancellationToken ct)
    {
        if (build.Cdn is not { } cdn) return Finish(DownloadAllOutcome.NeedsNetwork, NoContext, "The build has no CDN route; nothing was fetched.");
        ct.ThrowIfCancellationRequested();
        var ledger = ledgerSource.Ledger;
        lock (gate) { totalObjects = ledger.Count; totalBytes = ledger.TotalBytes; }
        Publish(force: true);

        var (missing, missingCount, missingBytes) = await ScanAsync(ledger, ct).ConfigureAwait(false);
        if (missingCount == 0) return Finish(DownloadAllOutcome.Completed, null, null);

        long free;
        try { free = await freeBytes(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Finish(DownloadAllOutcome.StoreFailed, "DOWNLOAD_ALL_FREE_SPACE_UNKNOWN", $"Free space couldn't be read: {e.Message}"); }
        var need = missingBytes > long.MaxValue - options.FreeReserveBytes ? long.MaxValue : missingBytes + options.FreeReserveBytes;
        if (free < need)
            return Finish(DownloadAllOutcome.DiskFull, "DOWNLOAD_ALL_NOT_ENOUGH_SPACE",
                $"{missingBytes} bytes are left to fetch and {options.FreeReserveBytes} are kept free, but only {free} are free.");

        var order = Order(ledger, missing, missingCount, ct);
        lock (gate) stage = DownloadAllStage.Fetching;
        Publish(force: true);
        return await FetchAsync(ledger, order, cdn, ct).ConfigureAwait(false);
    }

    /// <summary>The ledger rows the store doesn't hold, as a bitset, with their count and encoded bytes.</summary>
    private async Task<(ulong[] Missing, int Count, long Bytes)> ScanAsync(ObjectLedger ledger, CancellationToken ct)
    {
        var missing = new ulong[(ledger.Count + 63) >> 6];
        int count = 0;
        long bytes = 0;
        var key = new byte[16];
        for (var start = 0; start < ledger.Count; start += options.ScanBatchKeys)
        {
            ct.ThrowIfCancellationRequested();
            var keys = new ObjectKey[Math.Min(options.ScanBatchKeys, ledger.Count - start)];
            for (var i = 0; i < keys.Length; i++)
            {
                ledger.Key(start + i).WriteTo(key);
                keys[i] = new ObjectKey(ObjectKeyType.Encoded, key);
            }
            var has = await source.HasKeysAsync(keys, ct).ConfigureAwait(false);
            if (has.Count != keys.Length) throw new InvalidOperationException("The object source answered a different number of keys.");
            lock (gate)
                for (var i = 0; i < keys.Length; i++)
                {
                    var row = start + i;
                    if (has[i]) { storedObjects++; storedBytes += ledger.Size(row); continue; }
                    missing[row >> 6] |= 1UL << (row & 63);
                    count++; bytes += ledger.Size(row);
                }
            Publish(force: false);
        }
        return (missing, count, bytes);
    }

    /// <summary>
    /// The missing rows in fetch order: by their archive's position in the CDN config's archive list, then offset; rows no
    /// archive lists (file-index, loose, or not indexed) last, in ledger order. One 16-byte <see cref="Pending"/> per missing
    /// row, so a whole Retail ledger (about 2.9M rows) is about 46 MB while the job runs.
    /// </summary>
    private Pending[] Order(ObjectLedger ledger, ulong[] missing, int count, CancellationToken ct)
    {
        var order = new Pending[count];
        var at = 0;
        for (var w = 0; w < missing.Length; w++)
        {
            if ((w & 1023) == 0) ct.ThrowIfCancellationRequested();
            var bits = missing[w];
            while (bits != 0)
            {
                var row = (w << 6) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                order[at++] = ledgerSource.TryLocate(ledger.Key(row), out var location, out var archive)
                    ? new(row, archive, location.Offset) : new(row, NotArchived, 0);
            }
        }
        Array.Sort(order);
        ct.ThrowIfCancellationRequested();
        return order;
    }

    private async Task<DownloadAllResult> FetchAsync(ObjectLedger ledger, Pending[] order, CdnContext cdn, CancellationToken ct)
    {
        var key = new byte[16];
        string? firstCode = null;
        for (var start = 0; start < order.Length; start += options.FetchBatch)
        {
            ct.ThrowIfCancellationRequested();
            var requests = new ObjectRequest[Math.Min(options.FetchBatch, order.Length - start)];
            for (var i = 0; i < requests.Length; i++)
            {
                var p = order[start + i];
                var eKey = ledger.Key(p.Row);
                eKey.WriteTo(key);
                IReadOnlyList<ArchiveLocation>? locations = p.Archive != NotArchived && ledgerSource.TryLocate(eKey, out var location, out _) ? [location] : null;
                requests[i] = new ObjectRequest(new ObjectKey(ObjectKeyType.Encoded, key), ledger.Size(p.Row), locations, Order: 0);
            }
            int current;
            lock (gate) { current = ++batch; cdnBatch = 0; batchDone = 0; batchSize = requests.Length; }
            var results = await source.GetKeysAsync(requests, cdn, FetchPriority.Background, new BatchProgress(this, current), ct).ConfigureAwait(false);
            if (results.Count != requests.Length) throw new InvalidOperationException("The object source answered a different number of requests.");

            (DownloadAllOutcome Outcome, string? Code, string? Message)? stop = null;
            lock (gate)
            {
                cdnDone += cdnBatch; cdnBatch = 0; batchDone = 0; batch++;
                for (var i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    switch (r.State)
                    {
                        case ObjectState.Present: storedObjects++; storedBytes += requests[i].DeclaredLength!.Value; break;
                        case ObjectState.NotFound: notFound++; firstCode ??= r.Code; break;
                        case ObjectState.Unavailable when r.Code is "CDN_OFFLINE" or NoContext: stop ??= (DownloadAllOutcome.NeedsNetwork, r.Code, r.Message); break;
                        case ObjectState.Unavailable: unavailable++; firstCode ??= r.Code; break;
                        case ObjectState.Refused: refused++; firstCode ??= r.Code; break;
                        case ObjectState.NeedsNetwork: stop ??= (DownloadAllOutcome.NeedsNetwork, r.Code, r.Message); break;
                        case ObjectState.StoreFailed when r.Code == ObjectStoreCodes.DiskFull:
                            if (stop?.Outcome != DownloadAllOutcome.DiskFull) stop = (DownloadAllOutcome.DiskFull, r.Code, r.Message);
                            break;
                        default:
                            // A put our own cancellation interrupted is the turn-off, not a store failure.
                            stop ??= ct.IsCancellationRequested ? (DownloadAllOutcome.Canceled, null, "Download all was turned off.") : (DownloadAllOutcome.StoreFailed, r.Code, r.Message);
                            break;
                    }
                }
            }
            if (stop is { } s) return Finish(s.Outcome, s.Code, s.Message);
            Publish(force: false);
        }
        long failed;
        lock (gate) failed = notFound + unavailable + refused;
        return failed == 0 ? Finish(DownloadAllOutcome.Completed, null, null)
            : Finish(DownloadAllOutcome.Incomplete, firstCode, $"{failed} objects couldn't be fetched; running Download all again retries them.");
    }

    // Layer 2 may call it from its own threads. Ignores a batch that has already been counted, and never waits for the subscriber.
    private void OnObjectProgress(int forBatch, ObjectProgress value)
    {
        lock (gate)
        {
            if (forBatch != batch) return;
            var done = Math.Clamp(value.Done, 0, batchSize);
            if (value.CdnBytes <= cdnBatch && done <= batchDone) return;
            cdnBatch = Math.Max(cdnBatch, value.CdnBytes);
            batchDone = Math.Max(batchDone, done);
            Publish(force: false);
        }
    }

    // Lock is re-entrant, so callers already holding the gate may call it. Only queues the snapshot; Drain delivers it.
    private void Publish(bool force)
    {
        lock (gate)
        {
            if (finished) return;
            latest = Snapshot(final: false);
            var now = options.Time.GetTimestamp();
            if (!force && delivered && options.Time.GetElapsedTime(lastDelivered, now) < options.ProgressInterval) return;
            delivered = true; lastDelivered = now;
            Queue(latest, kept: force);
        }
    }

    private DownloadAllResult Finish(DownloadAllOutcome outcome, string? code, string? message)
    {
        lock (gate)
        {
            cdnDone += cdnBatch; cdnBatch = 0; batchDone = 0; batch++;
            latest = Snapshot(final: true);
            finished = true;
            Queue(latest, kept: true);
            return new(Build, outcome, latest, notFound, unavailable, refused, code, message);
        }
    }

    // Under gate. Between batch ends, finished-but-unsorted objects count as stored, and stored never goes down until the final.
    private DownloadAllProgress Snapshot(bool final)
    {
        var failed = notFound + unavailable + refused;
        var finishedObjects = storedObjects + failed + batchDone;
        var stored = storedObjects + batchDone;
        if (!final && stored < shownStored) stored = shownStored;
        shownStored = stored;
        return new(Build, stage, stored, totalObjects, storedBytes, totalBytes, final ? failed : finishedObjects - stored, cdnDone + cdnBatch);
    }

    // Under gate.
    private void Queue(DownloadAllProgress snapshot, bool kept)
    {
        if (progress is null) return;
        if (!kept && outbox.Count > 0 && !outbox[^1].Kept) outbox[^1] = (snapshot, false);
        else outbox.Add((snapshot, kept));
        if (draining) return;
        draining = true;
        ThreadPool.QueueUserWorkItem(static job => job.Drain(), this, preferLocal: false);
    }

    // One drainer at a time, on a pool thread, delivering in queue order outside the gate.
    private void Drain()
    {
        while (true)
        {
            DownloadAllProgress next;
            lock (gate)
            {
                if (outbox.Count == 0)
                {
                    draining = false;
                    drained?.TrySetResult();
                    drained = null;
                    return;
                }
                next = outbox[0].Snapshot;
                outbox.RemoveAt(0);
            }
            try { progress!.Report(next); }
            catch { /* a subscriber's failure never stops the job */ }
        }
    }

    private Task DrainedAsync()
    {
        lock (gate)
            return draining ? (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
    }

    /// <summary>A missing ledger row with its archive position (<see cref="NotArchived"/> when none) and offset: 16 bytes.</summary>
    private readonly record struct Pending(int Row, int Archive, long Offset) : IComparable<Pending>
    {
        public int CompareTo(Pending other)
        {
            var c = Archive.CompareTo(other.Archive);
            if (c == 0) c = Offset.CompareTo(other.Offset);
            return c != 0 ? c : Row.CompareTo(other.Row);
        }
    }

    private sealed class BatchProgress(DownloadAllJob job, int batch) : IProgress<ObjectProgress>
    {
        public void Report(ObjectProgress value) => job.OnObjectProgress(batch, value);
    }
}
