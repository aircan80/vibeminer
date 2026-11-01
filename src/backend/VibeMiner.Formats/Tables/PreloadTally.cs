using VibeMiner.Files;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// The counters, failed tables and coalesced progress of one table preload (D360), shared by the real handle and the fake. Thread
/// safe. Every figure only goes up and the stage only forward, whatever order layer 4's reports arrive in. Progress is offered at
/// most once per interval (the first offer, the ensure's final figures and the stage change always go) into one pending slot that
/// a newer offer overwrites, and delivered in order on a pool thread, never under the lock, as layer 4's ensure does: at most one
/// report in flight and one pending. The final report always goes, last, and <see cref="FinishAsync"/> returns once it has been
/// delivered.
/// </summary>
internal sealed class PreloadTally(IProgress<TablePreloadProgress>? progress, TimeProvider time, TimeSpan interval) : IProgress<EnsureProgress>
{
    private readonly Lock gate = new();
    private readonly List<TableEntry> failedTables = [];
    private TablePreloadProgress current = new(TablePreloadStage.Downloading, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private bool delivered, finished, draining;
    private long lastDelivered;
    private TablePreloadProgress? pending;
    private TaskCompletionSource? drained;

    public TablePreloadProgress Current { get { lock (gate) return current; } }

    /// <summary>The tables a definition may read, and how many of them the generated store already holds.</summary>
    public void Tables(int total, int alreadyDone)
    {
        lock (gate) current = current with { TablesTotal = Math.Max(current.TablesTotal, total), TablesDone = Math.Max(current.TablesDone, alreadyDone) };
        Offer();
    }

    /// <summary>Layer 4's ensure figures while Downloading; merged field by field so none goes down.</summary>
    public void Report(EnsureProgress value)
    {
        lock (gate)
        {
            if (current.Stage != TablePreloadStage.Downloading) return;
            current = Merge(current, value, current.FilesFailed);
        }
        Offer();
    }

    /// <summary>The ensure's final figures and its failed files.</summary>
    public void Downloaded(EnsureProgress final, long failed)
    {
        lock (gate) current = Merge(current, final, Math.Max(current.FilesFailed, failed));
        Offer(force: true);
    }

    /// <summary>Downloads are over: the stage change is always offered, whatever the interval.</summary>
    public void Preparing()
    {
        lock (gate) current = current with { Stage = TablePreloadStage.Preparing };
        Offer(force: true);
    }

    /// <summary>A table's end state: done, and failed (kept for the result) unless it is Ready, Partial or Encrypted.</summary>
    public void Table(TableEntry entry, bool countDone = true)
    {
        lock (gate)
        {
            var failed = !IsPrepared(entry.State);
            if (failed) failedTables.Add(entry);
            current = current with
            {
                TablesDone = countDone ? Math.Min(current.TablesTotal, current.TablesDone + 1) : current.TablesDone,
                TablesFailed = failed ? current.TablesFailed + 1 : current.TablesFailed,
            };
        }
        Offer();
    }

    public static bool IsPrepared(TableState state) => state is TableState.Ready or TableState.Partial or TableState.Encrypted;

    private static TablePreloadProgress Merge(TablePreloadProgress at, EnsureProgress value, long failed) => at with
    {
        FilesDone = Math.Max(at.FilesDone, value.FilesDone), FilesTotal = Math.Max(at.FilesTotal, value.FilesTotal), FilesFailed = failed,
        BytesDone = Math.Max(at.BytesDone, value.BytesDone), BytesTotal = Math.Max(at.BytesTotal, value.BytesTotal),
        CdnBytes = Math.Max(at.CdnBytes, value.CdnBytes),
    };

    /// <summary>Offers the current figures unless one was offered less than the interval ago and <paramref name="force"/> is false.</summary>
    public void Offer(bool force = false)
    {
        lock (gate)
        {
            if (progress is null || finished) return;
            var now = time.GetTimestamp();
            if (!force && delivered && time.GetElapsedTime(lastDelivered, now) < interval) return;
            delivered = true; lastDelivered = now;
            Queue(current);
        }
    }

    /// <summary>
    /// The result, after the final report (equal to <see cref="TablePreloadResult.Final"/>) has been delivered. Completed and
    /// Incomplete are decided here: Completed only when no file and no table failed and every table is done.
    /// </summary>
    public async Task<TablePreloadResult> FinishAsync(TablePreloadOutcome? outcome, FilesOpenState? openState = null, string? code = null, string? message = null)
    {
        TablePreloadResult result;
        Task wait;
        lock (gate)
        {
            finished = true;
            var final = current;
            var decided = outcome ?? (final.FilesFailed == 0 && final.FilesDone == final.FilesTotal && final.TablesFailed == 0
                && final.TablesDone == final.TablesTotal ? TablePreloadOutcome.Completed : TablePreloadOutcome.Incomplete);
            result = new(decided, final, failedTables.OrderBy(entry => entry.FileDataId).ToArray(), openState, code, message);
            if (progress is not null) Queue(final);
            wait = draining ? (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
        }
        await wait.ConfigureAwait(false);
        return result;
    }

    // Under gate. Overwrites a report not yet taken by the drain.
    private void Queue(TablePreloadProgress snapshot)
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
            TablePreloadProgress next;
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
            catch { /* a subscriber's failure never stops the preload */ }
        }
    }
}
