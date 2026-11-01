using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Files;

public enum EnsureSelectionKind { One, Many, Matching }

/// <summary>
/// The files an <see cref="IBuildFiles.EnsureAsync"/> call makes local: one file, a list of files, or every file a
/// <see cref="FileFilter"/> lists (by the same rules as <see cref="IBuildFiles.ListAsync"/>, ascending FileDataID).
/// </summary>
public sealed class EnsureSelection
{
    private EnsureSelection(EnsureSelectionKind kind, IReadOnlyList<FileRef>? files, FileFilter? filter) { Kind = kind; Files = files; Filter = filter; }

    public EnsureSelectionKind Kind { get; }
    /// <summary>The files asked for, in order, for One and Many; null for Matching.</summary>
    public IReadOnlyList<FileRef>? Files { get; }
    /// <summary>The filter for Matching; null otherwise.</summary>
    public FileFilter? Filter { get; }

    public static EnsureSelection One(FileRef file) => new(EnsureSelectionKind.One, [Checked(file)], null);

    /// <summary>A copy of <paramref name="files"/>. A file named twice, by the same ref or two refs to one FileDataID, is ensured once.</summary>
    public static EnsureSelection Many(IReadOnlyList<FileRef> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var copy = new FileRef[files.Count];
        for (var i = 0; i < copy.Length; i++) copy[i] = Checked(files[i]);
        return new(EnsureSelectionKind.Many, copy, null);
    }

    public static EnsureSelection Matching(FileFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new(EnsureSelectionKind.Matching, null, filter);
    }

    private static FileRef Checked(FileRef file) =>
        file.FileDataId is null && file.Path is null ? throw new ArgumentException("An empty file reference.", nameof(file)) : file;
}

/// <summary>Layer 4's fetch priority: Interactive is something a screen waits for, Background is a preload.</summary>
public enum EnsurePriority { Interactive, Background }

/// <summary>
/// How one selected file ended. Stored: it wasn't stored when this call checked and is now, fetched by this call or by a
/// transfer it joined. AlreadyStored: it was stored when checked, so it cost no fetch. The lookup states are
/// <see cref="FileLookupState"/>'s. Missing (layer 2's dated 404), NeedsNetwork, Unavailable and StoreFailed are layer 2's;
/// Refused means layer 2 found the bytes or the declarations wrong. Canceled: not finished when the call was canceled.
/// </summary>
public enum FileEnsureState : byte
{
    Stored,
    AlreadyStored,
    UnknownFile,
    NotInLocale,
    NotInEncoding,
    Missing,
    NeedsNetwork,
    Unavailable,
    Refused,
    StoreFailed,
    Canceled,
}

/// <summary>
/// One selected file's outcome. <see cref="FileDataId"/> is set when the build lists the file (null for UnknownFile).
/// <see cref="Code"/> is layer 2's code for a layer 2 failure, else null. 40 bytes per file on 64-bit.
/// </summary>
public readonly record struct FileEnsured(FileRef File, uint? FileDataId, FileEnsureState State, string? Code);

public enum EnsureOutcome
{
    /// <summary>Every selected file is Stored or AlreadyStored.</summary>
    Completed,
    /// <summary>The call ran to its end, but some file ended in another state.</summary>
    Incomplete,
    /// <summary>Canceled: what was stored stays stored; files not finished are Canceled.</summary>
    Canceled,
}

/// <summary>
/// Files and bytes of one ensure. A file is done once it has a state other than Canceled. Bytes are encoded bytes as ENCODING
/// declares them, counted per file (two files sharing an encoded copy count it twice); done bytes are those of Stored and
/// AlreadyStored files. <see cref="CdnBytes"/> is layer 2's transfer bytes (gap bytes included). For Matching the totals grow
/// while the filter is still being listed; <see cref="Listed"/> turns true once they are final. Done never exceeds total.
/// </summary>
public readonly record struct EnsureProgress(long FilesDone, long FilesTotal, long BytesDone, long BytesTotal, long CdnBytes, bool Listed);

/// <summary>
/// The outcome of <see cref="IBuildFiles.EnsureAsync"/>. <see cref="Files"/> has one entry per distinct selected file: in
/// selection order for One and Many, ascending FileDataID for Matching (a Matching call canceled while listing has only the
/// files listed so far, and <see cref="EnsureProgress.Listed"/> is false).
/// </summary>
public sealed record EnsureResult(EnsureOutcome Outcome, EnsureProgress Final, IReadOnlyList<FileEnsured> Files);

/// <summary>
/// The outcome list, counters and coalesced progress of one ensure, shared by the real handle and the fake. Thread safe.
/// Progress is offered at most once per interval (the first offer always goes) into one pending slot that a newer offer
/// overwrites, and delivered in order on a pool thread, never under the lock: at most one report in flight and one pending, so
/// a slow subscriber sees fewer reports, never a backlog. The final report always goes, last, and <see cref="FinishAsync"/>
/// returns once it has been delivered (after at most the one report already in flight).
/// </summary>
internal sealed class EnsureTally(IProgress<EnsureProgress>? progress, TimeProvider time, TimeSpan interval)
{
    private readonly Lock gate = new();
    private readonly List<FileEnsured> files = [];
    private long filesDone, filesTotal, bytesDone, bytesTotal, cdnDone, cdnCall, failed;
    private int call;
    private bool listed, delivered, finished, draining;
    private long lastDelivered;
    private EnsureProgress? pending;
    private TaskCompletionSource? drained;

    /// <summary>Adds a selected file, not finished yet, and counts it; returns its index.</summary>
    public int Add(FileRef file, uint? fileDataId, long bytes)
    {
        lock (gate)
        {
            files.Add(new(file, fileDataId, FileEnsureState.Canceled, null));
            filesTotal++; bytesTotal += bytes;
            return files.Count - 1;
        }
    }

    /// <summary>Gives a file its state; <paramref name="bytes"/> is what <see cref="Add"/> counted for it.</summary>
    public void Settle(int index, FileEnsureState state, string? code, long bytes)
    {
        if (state == FileEnsureState.Canceled) return;
        lock (gate)
        {
            if (files[index].State != FileEnsureState.Canceled) throw new InvalidOperationException("A file is settled once.");
            files[index] = files[index] with { State = state, Code = code };
            filesDone++;
            if (state is FileEnsureState.Stored or FileEnsureState.AlreadyStored) bytesDone += bytes;
            else failed++;
        }
    }

    public void Listed() { lock (gate) listed = true; }

    /// <summary>Starts counting one layer 2 call's transfer bytes; returns the call's number for <see cref="Cdn"/>.</summary>
    public int BeginCall() { lock (gate) { cdnDone += cdnCall; cdnCall = 0; return ++call; } }

    /// <summary>A layer 2 call's transfer bytes so far; a stale call's report is ignored.</summary>
    public void Cdn(int forCall, long bytes)
    {
        lock (gate)
        {
            if (forCall != call || bytes <= cdnCall) return;
            cdnCall = bytes;
        }
        Offer();
    }

    /// <summary>A key that is equal for two refs to the same unknown file: the FileDataID, or the path with ASCII case and slash direction folded.</summary>
    public static string UnknownKey(FileRef file)
    {
        if (file.FileDataId is { } id) return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.Create(file.Path!.Length + 1, file.Path, static (span, path) =>
        {
            span[0] = '/';
            for (var i = 0; i < path.Length; i++)
            {
                var c = path[i];
                span[i + 1] = c == '\\' ? '/' : char.IsAsciiLetterUpper(c) ? (char)(c | 0x20) : c;
            }
        });
    }

    /// <summary>Offers the current figures unless one was offered less than the interval ago.</summary>
    public void Offer()
    {
        lock (gate)
        {
            if (progress is null || finished) return;
            var now = time.GetTimestamp();
            if (delivered && time.GetElapsedTime(lastDelivered, now) < interval) return;
            delivered = true; lastDelivered = now;
            Queue(Snapshot());
        }
    }

    /// <summary>The result, after the final report (equal to <see cref="EnsureResult.Final"/>) has been delivered.</summary>
    public async Task<EnsureResult> FinishAsync(bool canceled)
    {
        EnsureResult result;
        Task wait;
        lock (gate)
        {
            finished = true;
            var final = Snapshot();
            result = new(canceled ? EnsureOutcome.Canceled : failed == 0 && filesDone == filesTotal ? EnsureOutcome.Completed : EnsureOutcome.Incomplete,
                final, files.ToArray());
            if (progress is not null) Queue(final);
            wait = draining ? (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
        }
        await wait.ConfigureAwait(false);
        return result;
    }

    /// <summary>Stops reporting, for a call that ends by throwing.</summary>
    public void Abandon() { lock (gate) finished = true; }

    // Under gate.
    private EnsureProgress Snapshot() => new(filesDone, filesTotal, bytesDone, bytesTotal, cdnDone + cdnCall, listed);

    // Under gate. Overwrites a report not yet taken by the drain.
    private void Queue(EnsureProgress snapshot)
    {
        pending = snapshot;
        if (draining) return;
        draining = true;
        ThreadPool.QueueUserWorkItem(static tally => tally.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        while (true)
        {
            EnsureProgress next;
            lock (gate)
            {
                if (pending is not { } taken)
                {
                    draining = false;
                    drained?.TrySetResult();
                    drained = null;
                    return;
                }
                next = taken;
                pending = null;
            }
            try { progress!.Report(next); }
            catch { /* a subscriber's failure never stops the ensure */ }
        }
    }
}

internal sealed partial class BuildFiles
{
    /// <summary>A selected file waiting for layer 2: its outcome index, its layer 2 request and its counted bytes.</summary>
    private readonly record struct Pending(int Index, ObjectRequest Request, long Bytes);

    public async Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        var build = Opened;
        var tally = new EnsureTally(progress, options.Time, options.ProgressInterval);
        var fetch = priority == EnsurePriority.Interactive ? FetchPriority.Interactive : FetchPriority.Background;
        var pending = new List<Pending>(Math.Min(batch, 1024));
        try
        {
            try
            {
                if (selection.Kind == EnsureSelectionKind.Matching) await EnsureMatchingAsync(build, selection.Filter!, fetch, tally, pending, cancellationToken).ConfigureAwait(false);
                else await EnsureListedAsync(build, selection.Files!, fetch, tally, pending, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Files layer 2 was working on are settled by what the store holds now; the rest stay Canceled.
                return await tally.FinishAsync(canceled: true).ConfigureAwait(false);
            }
            return await tally.FinishAsync(canceled: false).ConfigureAwait(false);
        }
        catch { tally.Abandon(); throw; }
    }

    /// <summary>One and Many: every ref resolved and deduplicated first (in memory), so the totals are final from the start.</summary>
    private async Task EnsureListedAsync(IBuild build, IReadOnlyList<FileRef> refs, FetchPriority fetch, EnsureTally tally, List<Pending> pending,
        CancellationToken cancellationToken)
    {
        var index = refs.Any(r => r.FileDataId is null) ? await names.CurrentAsync(cancellationToken).ConfigureAwait(false) : null;
        var byId = new HashSet<uint>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        // Outcome index and FileDataID of each file that may need layer 2; the variant is looked up again per batch.
        var toFetch = new List<(int Index, uint Id)>();
        foreach (var file in refs)
        {
            var resolved = Find(build, file, index).Resolution;
            if (resolved.State == FileResolutionState.UnknownFile || resolved.FileDataId is not { } id)
            {
                if (unknown.Add(EnsureTally.UnknownKey(file))) tally.Settle(tally.Add(file, null, 0), FileEnsureState.UnknownFile, null, 0);
                continue;
            }
            if (!byId.Add(id)) continue;
            var variant = Variants.Pick(resolved.Variants, localeBit);
            if (variant is null) { tally.Settle(tally.Add(file, id, 0), FileEnsureState.NotInLocale, null, 0); continue; }
            if (variant.EKey is null) { tally.Settle(tally.Add(file, id, 0), FileEnsureState.NotInEncoding, null, 0); continue; }
            toFetch.Add((tally.Add(file, id, variant.EncodedSize ?? 0), id));
        }
        tally.Listed();
        tally.Offer();
        for (var start = 0; start < toFetch.Count; start += batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending.Clear();
            for (var i = start; i < Math.Min(start + batch, toFetch.Count); i++)
            {
                var variant = Variants.Pick(build.Resolve(toFetch[i].Id).Variants, localeBit)!;
                pending.Add(new(toFetch[i].Index, Variants.RequestOf(variant), variant.EncodedSize ?? 0));
            }
            await EnsureBatchAsync(build, pending, checkStore: true, fetch, tally, cancellationToken).ConfigureAwait(false);
            tally.Offer();
        }
    }

    /// <summary>Matching: the listing's own batched stored state decides what is fetched, a batch of files at a time.</summary>
    private async Task EnsureMatchingAsync(IBuild build, FileFilter filter, FetchPriority fetch, EnsureTally tally, List<Pending> pending,
        CancellationToken cancellationToken)
    {
        await foreach (var (file, variant) in ListEntriesAsync(build, filter, detectFormats: false, cancellationToken).ConfigureAwait(false))
        {
            var bytes = variant.EKey is null ? 0 : variant.EncodedSize ?? 0;
            var at = tally.Add(FileRef.Id(file.FileDataId), file.FileDataId, bytes);
            if (file.Storage == FileStorage.NoEncodedCopy) tally.Settle(at, FileEnsureState.NotInEncoding, null, 0);
            else if (file.Storage == FileStorage.Stored) tally.Settle(at, FileEnsureState.AlreadyStored, null, bytes);
            else pending.Add(new(at, Variants.RequestOf(variant), bytes));
            // Coalesced, so a report per listed file costs a clock read.
            tally.Offer();
            if (pending.Count < batch) continue;
            await EnsureBatchAsync(build, pending, checkStore: false, fetch, tally, cancellationToken).ConfigureAwait(false);
            pending.Clear();
            tally.Offer();
        }
        tally.Listed();
        if (pending.Count > 0) await EnsureBatchAsync(build, pending, checkStore: false, fetch, tally, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One batch: stored state (unless the listing just checked it), then one <c>GetKeysAsync</c> for the distinct keys not
    /// stored. Canceled while layer 2 works, the asked keys are checked again without the token: stored ones are Stored, the
    /// rest stay Canceled, and the cancellation propagates.
    /// </summary>
    private async Task EnsureBatchAsync(IBuild build, List<Pending> pending, bool checkStore, FetchPriority fetch, EnsureTally tally,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requests = new List<ObjectRequest>();
        var position = new Dictionary<ObjectKey, int>();
        foreach (var p in pending)
            if (position.TryAdd(p.Request.Key, requests.Count)) requests.Add(p.Request);
        var missing = requests;
        if (checkStore)
        {
            var present = await objects.HasKeysAsync([.. requests.Select(r => r.Key)], cancellationToken).ConfigureAwait(false);
            if (present.Count != requests.Count) throw new InvalidOperationException("Layer 2 answered a different number of keys than asked.");
            missing = [];
            for (var i = 0; i < requests.Count; i++) if (!present[i]) missing.Add(requests[i]);
            var stored = new HashSet<ObjectKey>(requests.Where((_, i) => present[i]).Select(r => r.Key));
            foreach (var p in pending)
                if (stored.Contains(p.Request.Key)) tally.Settle(p.Index, FileEnsureState.AlreadyStored, null, p.Bytes);
        }
        if (missing.Count == 0) return;

        cancellationToken.ThrowIfCancellationRequested();
        var call = tally.BeginCall();
        IReadOnlyList<ObjectResult> results;
        try
        {
            results = await objects.GetKeysAsync(missing, build.Cdn, fetch, new CdnProgress(tally, call), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var now = await objects.HasKeysAsync([.. missing.Select(r => r.Key)], CancellationToken.None).ConfigureAwait(false);
            if (now.Count != missing.Count) throw new InvalidOperationException("Layer 2 answered a different number of keys than asked.");
            var stored = new HashSet<ObjectKey>(missing.Where((_, i) => now[i]).Select(r => r.Key));
            foreach (var p in pending)
                if (stored.Contains(p.Request.Key)) tally.Settle(p.Index, FileEnsureState.Stored, null, p.Bytes);
            throw;
        }
        if (results.Count != missing.Count) throw new InvalidOperationException("Layer 2 answered a different number of objects than asked.");
        var byKey = new Dictionary<ObjectKey, ObjectResult>(missing.Count);
        for (var i = 0; i < missing.Count; i++) byKey[missing[i].Key] = results[i];
        foreach (var p in pending)
        {
            if (!byKey.TryGetValue(p.Request.Key, out var r)) continue;
            var (state, code) = StateOf(r);
            tally.Settle(p.Index, state, code, p.Bytes);
        }
    }

    /// <summary>A layer 2 answer for a key that wasn't stored when checked.</summary>
    private static (FileEnsureState, string?) StateOf(ObjectResult result) => result.State switch
    {
        ObjectState.Present => (FileEnsureState.Stored, null),
        ObjectState.NotFound => (FileEnsureState.Missing, result.Code),
        ObjectState.NeedsNetwork => (FileEnsureState.NeedsNetwork, result.Code),
        ObjectState.Unavailable => (FileEnsureState.Unavailable, result.Code),
        ObjectState.Refused => (FileEnsureState.Refused, result.Code),
        ObjectState.StoreFailed => (FileEnsureState.StoreFailed, result.Code),
        _ => throw new InvalidOperationException($"Layer 2 answered {result.State}."),
    };

    private sealed class CdnProgress(EnsureTally tally, int call) : IProgress<ObjectProgress>
    {
        public void Report(ObjectProgress value) => tally.Cdn(call, value.CdnBytes);
    }
}
