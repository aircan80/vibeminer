using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The single background maintainer of running stored figures (D065). It applies committed receipt, corruption and
/// absence changes to every admitted ledger, and scans content already on disk once when a ledger is first admitted.
/// It never transfers, never observes files and never runs a presence check. Each round takes at most one bounded slice
/// for each of up to 64 ledgers, round robin, on the shared catalog worker; rounds start at most once a second; at most
/// eight ledgers are held open, and a ledger proved once is reopened without proving it again while its file is unchanged.
/// A fault stays with its ledger: the ledger is given up after three failed rounds in a row, and the others go on.
/// </summary>
public sealed class StoredCoverageWorker : IAsyncDisposable
{
    internal const int MaximumOpenLedgers = 8;
    internal const int MaximumLedgerFailures = 3;
    private const int MaximumPruneSteps = 16;
    private const int MaximumProofs = 4096;
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly AcquisitionResources resources;
    private readonly TimeProvider clock;
    private readonly TimeSpan minimumInterval, idleClose;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, OpenLedger> open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObjectFileIdentity> proofs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);
    private readonly object lifecycle = new();
    private Task? loop;
    private long uses;
    private long? lastRound;
    private string? cursor;

    /// <summary>How many times a ledger was fully proved (SHA-256 and order) since this worker started.</summary>
    internal int LedgerProofs { get; private set; }

    public StoredCoverageWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, AcquisitionResources resources)
        : this(catalog, objects, resources, null) { }

    internal StoredCoverageWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, AcquisitionResources resources,
        TimeProvider? clock, TimeSpan? minimumInterval = null, TimeSpan? idleClose = null)
    {
        this.catalog = catalog; this.objects = objects; this.resources = resources; this.clock = clock ?? TimeProvider.System;
        this.minimumInterval = minimumInterval ?? TimeSpan.FromSeconds(1); this.idleClose = idleClose ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Starts the loop. It first finishes any scan or backlog left by an earlier run.</summary>
    public void Start()
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(stop.IsCancellationRequested, this);
            loop ??= RunAsync();
        }
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        var failed = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (await RoundAsync(stop.Token)) { failed = 0; continue; }
                failed = 0;
                var wake = catalog.WaitForStoredChangeAsync(stop.Token);
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    if (await Task.WhenAny(wake, Task.Delay(idleClose, clock, idle.Token)) != wake) await CloseAsync();
                    idle.Cancel();
                }
                await wake;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // A catalog fault never ends the loop: retry after 1 s and 4 s, then wait for the next change.
                try
                {
                    await CloseAsync();
                    if (++failed <= 2) await Task.Delay(TimeSpan.FromSeconds(failed == 1 ? 1 : 4), clock, stop.Token);
                    else { failed = 0; await catalog.WaitForStoredChangeAsync(stop.Token); }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception) { /* Closing failed; the next round reopens what it needs. */ }
            }
        }
    }

    /// <summary>Rounds until no ledger has pending work, each paced like the loop's, then prunes applied changes.</summary>
    internal async Task DrainAsync(CancellationToken token)
    {
        while (await RoundAsync(token)) { }
    }

    /// <summary>One paced round: at most one slice or change batch per ledger. Returns whether there was work.</summary>
    internal async Task<bool> RoundAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            // Rounds start at most once per interval, whether a wake or remaining work started them.
            if (lastRound is { } previous && clock.GetElapsedTime(previous) < minimumInterval)
                await Task.Delay(minimumInterval - clock.GetElapsedTime(previous), clock, token);
            lastRound = clock.GetTimestamp();
            return await RoundCoreAsync(token);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> RoundCoreAsync(CancellationToken token)
    {
        var work = await catalog.ReadStoredWorkAsync(cursor, token);
        if (work.Count > 0) cursor = work[^1].Ledger.Sha256;
        foreach (var item in work)
        {
            token.ThrowIfCancellationRequested();
            var ledger = item.Ledger.Sha256;
            try
            {
                var held = await OpenAsync(item.Ledger, token);
                if (item.ScanAfter is not null) await ScanAsync(item, held, token);
                else await ApplyChangesAsync(item, held, token);
                failures.Remove(ledger);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (DiscoveryException failure) when (failure.Code is "LOCAL_OBJECT_MISSING" or "LOCAL_OBJECT_CORRUPT" or
                "DOWNLOAD_LEDGER_INTEGRITY" or "DOWNLOAD_LEDGER_INVALID")
            {
                await DropAsync(ledger); failures.Remove(ledger); proofs.Remove(ledger);
                await catalog.FailStoredCoverageAsync(ledger, new(failure.Code,
                    "The saved download ledger could not be read, so this build's stored figure is not kept up to date. Open the build again to count it.", true), token);
            }
            // A catalog that cannot be used at all is the loop's fault, not this ledger's.
            catch (DiscoveryException failure) when (failure.Code is "CATALOG_BUSY" or "STORAGE_FULL" or "STORAGE_FAILURE" or "CATALOG_INTEGRITY") { throw; }
            catch (Exception)
            {
                await DropAsync(ledger);
                var count = failures[ledger] = failures.GetValueOrDefault(ledger) + 1;
                if (count >= MaximumLedgerFailures)
                {
                    failures.Remove(ledger);
                    await catalog.FailStoredCoverageAsync(ledger, DiscoveryCatalog.StoredFailure("STORED_COVERAGE_FAILED"), token);
                }
            }
        }
        for (var step = 0; step < MaximumPruneSteps && await catalog.PruneStoredChangesAsync(token) == DiscoveryCatalog.StoredPruneRows; step++)
            token.ThrowIfCancellationRequested();
        return work.Count > 0;
    }

    private async Task ScanAsync(StoredWork item, OpenLedger ledger, CancellationToken token)
    {
        var keys = await catalog.ReadStoredKeysAsync(item.ScanAfter!, token);
        // Resume where the previous slice ended, or find that place again after a restart.
        var position = ledger.ScanAfter == item.ScanAfter ? ledger.ScanPosition
            : item.ScanAfter!.Length == 0 ? 0 : await ledger.Session.SearchAsync(item.ScanAfter, 0, true, token);
        var members = new List<StoredMember>(); var exhausted = false;
        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            position = await ledger.SeekAsync(key, position, token);
            if (position == ledger.Session.Count) { exhausted = true; break; }
            var row = ledger.Row(position);
            if (row.EncodingKey == key) { members.Add(new(position, key, row.EncodedBytes)); position++; }
        }
        // Past the ledger's last member no later key can be one, so the first count is complete.
        var next = exhausted || keys.Count < DiscoveryCatalog.StoredBatchKeys ? null : keys[^1];
        if (await catalog.ApplyStoredAsync(item.Ledger, item.ScanAfter, item.Through, members, next, item.Through, token))
        { ledger.ScanAfter = next; ledger.ScanPosition = position; }
        else ledger.ScanAfter = null;
    }

    private async Task ApplyChangesAsync(StoredWork item, OpenLedger ledger, CancellationToken token)
    {
        var batch = await catalog.ReadStoredChangesAsync(item.Through, token);
        var members = new List<StoredMember>(); long from = 0;
        foreach (var key in batch.Keys.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var row = await ledger.Session.FindKeyAsync(key, from, token);
            if (row is null) continue;
            members.Add(new(row.Id - 1, key, row.EncodedBytes)); from = row.Id;
        }
        await catalog.ApplyStoredAsync(item.Ledger, null, item.Through, members, null, batch.Through, token);
    }

    private async Task<OpenLedger> OpenAsync(DownloadLedgerArtifact artifact, CancellationToken token)
    {
        if (open.TryGetValue(artifact.Sha256, out var held) && held.Artifact == artifact) { held.Use = ++uses; return held; }
        if (held is not null) await DropAsync(artifact.Sha256);
        while (open.Count >= MaximumOpenLedgers) await DropAsync(open.MinBy(entry => entry.Value.Use).Key);
        DownloadLedgerReadSession? session = null;
        // A ledger this worker already proved is immutable; while its file is the same one, reopening only checks the header.
        if (proofs.TryGetValue(artifact.Sha256, out var proof) &&
            await objects.OpenProvenAsync(artifact.Sha256, artifact.ByteLength, proof, token) is { } file)
        {
            try { session = await DownloadLedger.OpenProvenReadSessionAsync(file, artifact, token); }
            finally { if (session is null) await file.DisposeAsync(); }
        }
        if (session is null)
        {
            // Otherwise prove the whole ledger (SHA-256 and order) under background CPU permits.
            proofs.Remove(artifact.Sha256);
            session = await new DownloadLedger(objects).OpenReadSessionAsync(artifact, token, resources, BackgroundWork.Instance);
            LedgerProofs++;
            if (proofs.Count >= MaximumProofs) proofs.Clear();
            proofs[artifact.Sha256] = session.FileIdentity;
        }
        var ledger = new OpenLedger(artifact, session) { Use = ++uses };
        open.Add(artifact.Sha256, ledger);
        return ledger;
    }
    private async Task DropAsync(string ledger)
    {
        if (open.Remove(ledger, out var held)) await held.Session.DisposeAsync();
    }
    /// <summary>Closes every held ledger; proofs stay. The loop does this after 30 s idle.</summary>
    internal Task CloseLedgersAsync() => CloseAsync();
    private async Task CloseAsync()
    {
        await gate.WaitAsync();
        try { foreach (var ledger in open.Keys.ToArray()) await DropAsync(ledger); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (lifecycle) { stop.Cancel(); running = loop; }
        if (running is not null) await running;
        await CloseAsync();
        stop.Dispose();
    }

    /// <summary>A held ledger plus one cached page of up to 4096 members for ordered scans.</summary>
    private sealed class OpenLedger(DownloadLedgerArtifact artifact, DownloadLedgerReadSession session)
    {
        private IReadOnlyList<DownloadLedgerRow> page = [];
        private long pageStart;
        internal DownloadLedgerArtifact Artifact { get; } = artifact;
        internal DownloadLedgerReadSession Session { get; } = session;
        internal long Use { get; set; }
        internal string? ScanAfter { get; set; }
        internal long ScanPosition { get; set; }
        internal DownloadLedgerRow Row(long index) => page[(int)(index - pageStart)];
        /// <summary>First index at or after <paramref name="from"/> whose key is at least <paramref name="key"/>, with its page loaded.</summary>
        internal async Task<long> SeekAsync(string key, long from, CancellationToken token)
        {
            var end = pageStart + page.Count;
            if (from >= pageStart && from < end && string.CompareOrdinal(page[^1].EncodingKey, key) >= 0)
            {
                // The key falls inside the cached page.
                int lower = (int)(from - pageStart), upper = page.Count - 1;
                while (lower < upper)
                {
                    var middle = lower + (upper - lower) / 2;
                    if (string.CompareOrdinal(page[middle].EncodingKey, key) < 0) lower = middle + 1; else upper = middle;
                }
                return pageStart + lower;
            }
            var index = await Session.SearchAsync(key, from, false, token);
            if (index < Session.Count) { page = await Session.ReadFromAsync(index, token); pageStart = index; }
            return index;
        }
    }

    private sealed class BackgroundWork : IAcquisitionWorkContext
    {
        internal static readonly BackgroundWork Instance = new();
        public AcquisitionWorkSnapshot Snapshot { get; } = new(1, AcquisitionPriority.Background, []);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }
}
