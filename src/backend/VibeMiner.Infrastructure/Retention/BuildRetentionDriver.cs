using System.Threading.Channels;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One engine-owned feeder. Commands wake it; renderer reads never own its lifetime.</summary>
public sealed partial class BuildRetentionDriver : IRetentionDriver
{
    internal const int MaximumFrontier = 32;
    internal const int MaximumProblems = 10_000;
    internal const string VerifierRevision = "blte-encoded-v1";
    private readonly IRetentionDriverCatalog catalog;
    private readonly IRetentionSessionFactory sessions;
    private readonly TimeProvider clock;
    private readonly object recoveryGate = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly Channel<bool> changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly CancellationTokenSource shutdown = new();
    private Task? worker;
    private PendingSettlement? pendingSettlement;
    private bool claimUncertain;
    private RetentionDriverFault? fault;
    private long intentVersion;
    private int stopping;

    public RetentionDriverFault? ReadFault() => Volatile.Read(ref fault);

    internal BuildRetentionDriver(IRetentionDriverCatalog catalog, IRetentionSessionFactory sessions, TimeProvider? clock = null)
    { this.catalog = catalog; this.sessions = sessions; this.clock = clock ?? TimeProvider.System; }

    public async Task RestoreAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref stopping) != 0) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (worker is not null) return;
            await catalog.RestoreDriverAsync(token);
            worker = RunAsync();
        }
        finally { lifecycle.Release(); }
    }

    public void NotifyIntentChanged()
    {
        lock (recoveryGate)
        {
            Interlocked.Increment(ref intentVersion);
            Volatile.Write(ref fault, null);
            changed.Writer.TryWrite(true);
        }
    }

    public async Task ShutdownAsync(CancellationToken token)
    {
        Interlocked.Exchange(ref stopping, 1); shutdown.Cancel();
        NotifyIntentChanged();
        await lifecycle.WaitAsync(token);
        Task? retiring;
        try
        {
            // A failed shutdown retains its cleaned-up settlement/uncertain claim. A later
            // explicit shutdown retries that same retirement without opening another job.
            if (worker?.IsCompleted == true && (pendingSettlement is not null || claimUncertain)) worker = RunAsync();
            retiring = worker;
        }
        finally { lifecycle.Release(); }
        if (retiring is not null) await retiring.WaitAsync(token);
        if (pendingSettlement is not null || claimUncertain)
        {
            var problem = ReadFault()?.Problem;
            throw new DiscoveryException(problem?.Code ?? "RETENTION_SETTLEMENT_UNAVAILABLE",
                problem?.Message ?? "Retention resources retired, but durable retirement is waiting for storage recovery.", true);
        }
    }

    private async Task RunAsync()
    {
        // Startup restores bounded local state before any job work begins.
        await Task.Yield();
        var attempts = 0; long retryWakeVersion = 0;
        while (true)
        {
            var observedVersion = Volatile.Read(ref intentVersion);
            try
            {
                if (pendingSettlement is { } pending)
                {
                    await SettleAsync(pending);
                    pendingSettlement = null; attempts = 0; Volatile.Write(ref fault, null);
                }
                if (claimUncertain)
                {
                    // Claim may have committed before its return failed. No session has been
                    // opened, so the single driver can reconcile that interrupted observation.
                    await catalog.RestoreDriverAsync(CancellationToken.None);
                    claimUncertain = false;
                }
                if (shutdown.IsCancellationRequested) { Volatile.Write(ref fault, null); return; }
                RetentionDriverJob? job;
                try { job = await catalog.ClaimNextRetentionAsync(shutdown.Token); }
                catch { claimUncertain = true; throw; }
                attempts = 0; Volatile.Write(ref fault, null);
                if (job is null) { await WaitForWakeAsync(observedVersion, shutdown.Token); continue; }
                pendingSettlement = await RunControlledAsync(job);
            }
            catch (OperationCanceledException canceled) when (shutdown.IsCancellationRequested && canceled.CancellationToken == shutdown.Token)
            {
                // A canceled claim can also have won its commit race. Reconcile before exit.
                if (pendingSettlement is null && !claimUncertain) return;
                attempts = 0;
            }
            catch (Exception failure)
            {
                if (attempts++ == 0) retryWakeVersion = observedVersion;
                if (attempts < 3)
                {
                    // Three bounded attempts, then explicit intent/shutdown wake only. These
                    // short retirement retries also run after shutdown canceled job work.
                    await Task.Delay(TimeSpan.FromSeconds(attempts == 1 ? 1 : 4), clock, CancellationToken.None);
                    continue;
                }
                var cause = Problem(failure);
                var code = cause.Code.Length <= 64 ? cause.Code : cause.Code[..64];
                bool woken;
                lock (recoveryGate)
                {
                    woken = !shutdown.IsCancellationRequested && Volatile.Read(ref intentVersion) != retryWakeVersion;
                    if (!woken)
                        Volatile.Write(ref fault, new RetentionDriverFault(pendingSettlement?.Binding.JobId,
                            new("RETENTION_STATE_RETRY", $"Download state could not be saved or restored ({code}). Retry the saved download policy after storage recovers.", true)));
                }
                if (woken) { attempts = 0; continue; }
                if (shutdown.IsCancellationRequested) return;
                try { await WaitForWakeAsync(retryWakeVersion, shutdown.Token); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                attempts = 0;
            }
        }
    }

    private async Task WaitForWakeAsync(long observedVersion, CancellationToken token)
    {
        while (Volatile.Read(ref intentVersion) == observedVersion)
        {
            while (changed.Reader.TryRead(out _)) { }
            if (Volatile.Read(ref intentVersion) != observedVersion) return;
            await changed.Reader.ReadAsync(token);
        }
    }

    private async Task<PendingSettlement> RunControlledAsync(RetentionDriverJob job)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        using var watcherStop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var watcher = WatchIntentAsync(job.Binding, cancellation, watcherStop.Token);
        PassResult result = new(RetentionPassOutcome.Failed, null);
        try { result = await RunPassAsync(job, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { result = new(shutdown.IsCancellationRequested ? RetentionPassOutcome.Shutdown : RetentionPassOutcome.IntentChanged, null); }
        catch (Exception failure)
        {
            var problem = Problem(failure);
            result = new(problem.Retryable ? RetentionPassOutcome.Waiting : RetentionPassOutcome.Failed, problem);
        }
        finally
        {
            watcherStop.Cancel();
            try { await watcher; }
            catch (Exception failure) { var problem = Problem(failure); result = new(RetentionPassOutcome.Failed, problem); }
        }
        if (shutdown.IsCancellationRequested) result = new(RetentionPassOutcome.Shutdown, null);
        return new(job.Binding, result);
    }

    private async Task SettleAsync(PendingSettlement pending)
    {
        // No request/shutdown cancellation interrupts this short durable release after cleanup.
        await catalog.SettleRetentionAsync(pending.Binding, pending.Result.Outcome, pending.Result.Problem, CancellationToken.None);
        // A command may commit after the watcher retires but before the first settlement's
        // revision CAS. If that CAS lost, release this cleaned-up generation using current
        // intent. If it won, the inactive generation makes this fallback a no-op.
        if (pending.Result.Outcome != RetentionPassOutcome.IntentChanged)
            await catalog.SettleRetentionAsync(pending.Binding, RetentionPassOutcome.IntentChanged, null, CancellationToken.None);
    }

    private async Task WatchIntentAsync(RetentionDriverBinding binding, CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var observedVersion = Volatile.Read(ref intentVersion);
                // Check before waiting as a command can commit between claim and watcher admission.
                var intent = await catalog.ReadRetentionIntentAsync(binding.JobId, token);
                if (intent is null || intent.Revision != binding.IntentRevision || intent.Mode != RetentionMode.DownloadAll || intent.Paused)
                { cancellation.Cancel(); return; }
                await WaitForWakeAsync(observedVersion, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { cancellation.Cancel(); throw; }
    }

    private async Task<PassResult> RunPassAsync(RetentionDriverJob job, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token; var state = new ProgressState();
        using var reportStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var reporter = ReportAsync(job.Binding, state, cancellation, reportStop.Token);
        try
        {
            await using var session = await sessions.OpenAsync(job, state.Report, token);
            long next = 0; var problems = 0; var allRetained = true; var retryable = true;
            RetentionProblem? last = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (problems == MaximumProblems)
                    return new(RetentionPassOutcome.Failed, new("RETENTION_PROBLEM_LIMIT", "This pass reached its bounded problem limit. Resume starts a new pass.", true));
                var rows = await session.ReadAsync(next, Math.Min(MaximumFrontier, MaximumProblems - problems), token);
                if (rows.Count == 0)
                {
                    if (next != job.Plan.Ledger.ObjectCount)
                        throw new DiscoveryException("RETENTION_LEDGER_INTEGRITY", "The ledger ended before every admitted member was visited.");
                    return allRetained ? new(RetentionPassOutcome.Completed, null)
                        : new(retryable ? RetentionPassOutcome.Waiting : RetentionPassOutcome.Failed, last);
                }
                ValidateRows(rows, next, job.Plan.Ledger.ObjectCount);
                var before = await session.ObserveAsync(rows, token);
                ValidateObservations(rows, before);
                var attempts = new Task<RetentionProblem?>[rows.Count];
                state.Pending = before.Count(item => !Ready(item));
                for (var i = 0; i < rows.Count; i++)
                    attempts[i] = Ready(before[i]) ? Task.FromResult<RetentionProblem?>(null)
                        : before[i].Problem is { Retryable: false } unsupported ? Task.FromResult<RetentionProblem?>(unsupported)
                        : AcquireMemberAsync(session, rows[i], state, token);
                RetentionProblem?[] failures;
                try { failures = await Task.WhenAll(attempts); }
                finally { state.Pending = 0; }
                token.ThrowIfCancellationRequested();
                var after = await session.ObserveAsync(rows, token);
                ValidateObservations(rows, after);
                var observations = new RetentionMemberObservation[rows.Count];
                for (var i = 0; i < rows.Count; i++)
                {
                    var failure = failures[i] ?? after[i].Problem;
                    if (!Ready(after[i]) && failure is null)
                        failure = new("RETENTION_NOT_PRESENT", "A committed encoded receipt and current local presence were not both established.", true);
                    observations[i] = after[i] with { Problem = failure };
                    if (failure is not null) { problems++; last = failure; retryable &= failure.Retryable; }
                    allRetained &= Ready(observations[i]) && failure is null;
                }
                next += rows.Count;
                if (!await catalog.ApplyRetentionCreditAsync(job.Binding, observations, next, token))
                { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
                state.Latest = new("Assessing retained download objects", "objects", next, job.Plan.Ledger.ObjectCount);
                if (!await catalog.ReportRetentionProgressAsync(job.Binding, state.Latest, 0, state.WireBytes, token))
                    cancellation.Cancel();
                await Task.Yield();
            }
        }
        finally
        {
            reportStop.Cancel();
            await reporter;
        }
    }

    private async Task<RetentionProblem?> AcquireMemberAsync(IRetentionSession session, DownloadLedgerRow row,
        ProgressState progress, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var subscription = await session.AttachAsync(row, progress.Report, token);
                progress.Attach(subscription);
                try
                {
                    var receipt = await subscription.WaitAsync(token);
                    if (receipt.EncodingKey != row.EncodingKey || receipt.EncodedBytes != row.EncodedBytes ||
                        receipt.VerifierRevision != VerifierRevision)
                        throw new DiscoveryException("RETENTION_RECEIPT_CONFLICT", "An acquisition outcome differs from the exact ledger member and proof policy.");
                    return null;
                }
                finally
                {
                    try { await subscription.DisposeAsync(); }
                    finally { progress.Retire(subscription); }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                var problem = Problem(failure);
                if (!problem.Retryable || attempt == 2) return problem;
                await progress.Report(new("Waiting to retry an unavailable encoded source", "attempts", attempt + 1, 3));
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 4), clock, token);
            }
        }
    }

    private async Task ReportAsync(RetentionDriverBinding binding, ProgressState state,
        CancellationTokenSource cancellation, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, token);
                if (!await catalog.ReportRetentionProgressAsync(binding, state.Latest, state.Pending, state.WireBytes, token))
                { cancellation.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { cancellation.Cancel(); throw; }
    }

    private static bool Ready(RetentionMemberObservation item) => item.Presence == RetentionPresence.Present && item.HasReceipt;
    private static void ValidateRows(IReadOnlyList<DownloadLedgerRow> rows, long next, long count)
    {
        if (rows.Count > MaximumFrontier || next > count - rows.Count)
            throw new DiscoveryException("RETENTION_LEDGER_INTEGRITY", "A ledger page exceeds the bounded admitted frontier.");
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Id != next + i + 1)
                throw new DiscoveryException("RETENTION_LEDGER_INTEGRITY", "A ledger page skipped or repeated an exact ordinal.");
    }
    private static void ValidateObservations(IReadOnlyList<DownloadLedgerRow> rows, IReadOnlyList<RetentionMemberObservation> observations)
    {
        if (rows.Count != observations.Count) throw new DiscoveryException("RETENTION_OBSERVATION_INTEGRITY", "A presence batch has inconsistent membership.");
        for (var i = 0; i < rows.Count; i++)
            if (observations[i].Ordinal != rows[i].Id - 1 || observations[i].EncodingKey != rows[i].EncodingKey ||
                observations[i].EncodedBytes != rows[i].EncodedBytes || !Enum.IsDefined(observations[i].Presence))
                throw new DiscoveryException("RETENTION_OBSERVATION_INTEGRITY", "A presence result differs from its exact ledger member.");
    }
    private static RetentionProblem Problem(Exception failure) => failure is DiscoveryException typed
        ? new(typed.Code, typed.Message.Length <= 400 ? typed.Message : typed.Message[..400], typed.Retryable)
        : new("RETENTION_IO", "The retention pass could not finish its bounded local or source operation.", failure is IOException);
    private sealed record PassResult(RetentionPassOutcome Outcome, RetentionProblem? Problem);
    private sealed record PendingSettlement(RetentionDriverBinding Binding, PassResult Result);
    private sealed class ProgressState
    {
        private AcquisitionProgress? latest;
        private int pending;
        private readonly object gate = new();
        private readonly HashSet<IAcquisitionSubscription> transfers = [];
        private long wireBytes;
        internal AcquisitionProgress? Latest { get => Volatile.Read(ref latest); set => Volatile.Write(ref latest, value); }
        internal int Pending { get => Volatile.Read(ref pending); set => Volatile.Write(ref pending, value); }
        internal long WireBytes
        {
            get
            {
                lock (gate)
                {
                    var result = wireBytes;
                    foreach (var subscription in transfers) result = checked(result + subscription.ObservedPayloadBytes);
                    return result;
                }
            }
        }
        internal void Attach(IAcquisitionSubscription subscription) { lock (gate) transfers.Add(subscription); }
        internal void Retire(IAcquisitionSubscription subscription)
        {
            lock (gate)
                if (transfers.Remove(subscription)) wireBytes = checked(wireBytes + subscription.ObservedPayloadBytes);
        }
        internal Task Report(AcquisitionProgress progress) { Latest = progress; return Task.CompletedTask; }
    }
}
