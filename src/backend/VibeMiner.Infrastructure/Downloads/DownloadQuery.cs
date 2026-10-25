using System.Diagnostics;
using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed class DownloadQuery(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    AcquisitionResources? resources = null) : IDownloadCatalog
{
    /// <summary>A measured ledger may add at most this many files to its manifest for a Check (D093).</summary>
    internal const long MaxCheckedObjects = 1_000_000;
    /// <summary>Exact receipt lookups per batch, the receipt catalog's own bound.</summary>
    private const int CheckBatch = 128;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly DownloadLedger ledger = resources is null ? new(objects) : new(objects, resources, null);
    // Background reads of the ledgers a Check walks; page reads above stay interactive.
    private readonly DownloadLedger background = resources is null ? new(objects) : new(objects, resources, BackgroundWorkContext.Instance);
    private readonly SemaphoreSlim checks = new(1, 1);

    public async Task<DownloadPage> ReadDownloadsAsync(DownloadReadRequest request, CancellationToken token)
    {
        DownloadService.Validate(request);
        var saved = await catalog.ReadDownloadAdmissionAsync(request, token);
        await using var manifest = await ledger.OpenPageSessionAsync(saved.Artifact, token);
        var matched = await DownloadLedger.AssessAsync(manifest, saved.VerifiedLengths, token);
        if (matched.Count != saved.VerifiedKeys.Count)
            throw new DiscoveryException("DOWNLOAD_CATALOG_INTEGRITY", "The saved reuse assessment does not match this download manifest.");
        await using var measured = saved.Measured is null ? null : await ledger.OpenPageSessionAsync(saved.Measured, token);
        var listed = measured ?? manifest;
        var after = request.From is not null ? await listed.SearchAsync(request.From, 0, false, token)
            : request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture);
        var rows = await listed.ReadPageAsync(after, request.Limit + 1, token);
        var page = rows.Take(request.Limit).ToArray();
        // A measured page marks the files Open's assessment never covered: members of this ledger the manifest lacks.
        var outside = new HashSet<string>(StringComparer.Ordinal);
        if (measured is not null)
        {
            long lower = 0;
            foreach (var row in page)
            {
                lower = await manifest.SearchAsync(row.EncodingKey, lower, false, token);
                var member = lower < manifest.Count ? (await manifest.ReadPageAsync(lower, 1, token))[0] : null;
                if (member?.EncodingKey != row.EncodingKey) outside.Add(row.EncodingKey);
                else if (member.EncodedBytes != row.EncodedBytes) throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "A measured ledger member differs from its manifest member.");
            }
        }
        var found = saved.Check is { } check && outside.Count > 0
            ? await catalog.ReadDownloadCheckObjectsAsync(saved.Measured!.Sha256, check.Generation, [.. outside], token)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var retained = new HashSet<string>(saved.VerifiedKeys, StringComparer.Ordinal);
        var entries = page.Select(row =>
        {
            var open = !outside.Contains(row.EncodingKey);
            return new DownloadObjectEntry(row.Id.ToString(CultureInfo.InvariantCulture), row.EncodingKey,
                row.EncodedBytes.ToString(CultureInfo.InvariantCulture), open && retained.Contains(row.EncodingKey), open,
                open || saved.Check is null ? null : found.GetValueOrDefault(row.EncodingKey, "notStored"));
        }).ToArray();
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.LedgerId, saved.AssessedAt, entries,
            listed.Count.ToString(CultureInfo.InvariantCulture), rows.Count > request.Limit ? entries[^1].Id : null,
            saved.Artifact.Sha256, saved.Check is { } last ? new DownloadCheckSummary(last.CheckedAt, N(last.Checked), N(last.Verified), N(last.NotStored), N(last.Missing), N(last.Corrupt)) : null);
    }

    /// <summary>
    /// Checks every file a measured ledger adds to its admitted manifest (D093): each one with a local receipt of any kind
    /// has its whole stored object hashed against that receipt. One Check at a time engine-wide, at background priority
    /// (one background CPU permit for the ledger walk, background integrity hashing), cancellable between objects. Nothing
    /// is recorded until it completes; a cancelled or failed Check leaves the previous one as it was.
    /// </summary>
    public async Task<DownloadCheckResult> CheckDownloadsAsync(DownloadCheckRequest request, Func<DownloadCheckProgress, Task> progress, CancellationToken token)
    {
        DownloadService.Validate(request);
        if (!await checks.WaitAsync(0, token))
            throw new DiscoveryException("DOWNLOAD_CHECK_BUSY", "Another check of stored files is running. Try again when it has finished.", true);
        try
        {
            var (saved, generation) = await catalog.BeginDownloadCheckAsync(request, token);
            var measuredArtifact = saved.Measured!;
            try
            {
                var total = measuredArtifact.ObjectCount - saved.Artifact.ObjectCount;
                if (total > MaxCheckedObjects)
                    throw new DiscoveryException("DOWNLOAD_CHECK_LIMIT", "This build lists more files outside its download manifest than one check covers.");
                var counts = await WalkAsync(saved, generation, total, progress, token);
                // A cancel that lands after the last file still records nothing: the completing transaction is not cancellable.
                token.ThrowIfCancellationRequested();
                var record = await catalog.CompleteDownloadCheckAsync(measuredArtifact.Sha256, generation, counts.Checked, counts.Verified, counts.NotStored,
                    counts.Missing, counts.Corrupt, token) ?? throw new DiscoveryException("DOWNLOAD_CHECK_STALE", "Another check replaced this one before it finished.", true);
                return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.LedgerId, saved.Artifact.Sha256, record.CheckedAt,
                    N(record.Checked), N(record.Verified), N(record.NotStored), N(record.Missing), N(record.Corrupt));
            }
            catch
            {
                // Best effort: the next Check removes whatever an unfinished one left, and pages never read it.
                try { await catalog.AbandonDownloadCheckAsync(measuredArtifact.Sha256, generation); }
                catch (Exception cleanup) when (cleanup is DiscoveryException or InvalidOperationException) { }
                throw;
            }
        }
        catch (IOException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Stored files could not be read for this check.", true); }
        catch (UnauthorizedAccessException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Stored files could not be accessed for this check.", true); }
        finally { checks.Release(); }
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed class Counts { internal long Checked, Verified, NotStored, Missing, Corrupt; }

    private async Task<Counts> WalkAsync(StoredDownloadAdmission saved, long generation, long total, Func<DownloadCheckProgress, Task> progress, CancellationToken token)
    {
        var counts = new Counts();
        var clock = Stopwatch.StartNew(); var reported = TimeSpan.MinValue;
        async Task Report(bool force)
        {
            if (!force && clock.Elapsed - reported < ProgressInterval) return;
            reported = clock.Elapsed; await progress(new(counts.Checked, total));
        }
        await Report(true);
        // Both proofs are opened before any CPU permit is taken (ASYNC-AND-JOBS: never hold CPU while entering CAS).
        await using var manifest = await background.OpenReadSessionAsync(saved.Artifact, token);
        await using var measured = await background.OpenReadSessionAsync(saved.Measured!, token);
        var inManifest = manifest.OpenOrderedCursor(); var listed = measured.OpenOrderedCursor();
        var moreManifest = await inManifest.NextAsync(token);
        var batch = new List<LedgerValue>(CheckBatch); var ended = false;
        while (!ended)
        {
            // One slice of the ordered merge under a background CPU permit, released before receipts and hashing.
            using (resources is null ? null : await resources.EnterCpuAsync(BackgroundWorkContext.Instance, token))
            {
                for (var slice = 0; slice < 4096 && batch.Count < CheckBatch; slice++)
                {
                    if (!await listed.NextAsync(token)) { ended = true; break; }
                    var row = listed.Current;
                    while (moreManifest && inManifest.Current.CompareKey(row) < 0) moreManifest = await inManifest.NextAsync(token);
                    if (moreManifest && inManifest.Current.SameKey(row))
                    {
                        if (inManifest.Current.Bytes != row.Bytes) throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "A measured ledger member differs from its manifest member.");
                        continue;
                    }
                    batch.Add(row);
                }
            }
            if (batch.Count == CheckBatch || ended && batch.Count > 0)
            {
                await CheckBatchAsync(saved.Measured!.Sha256, generation, batch, counts, () => Report(false), token);
                batch.Clear();
                await Report(false);
            }
        }
        if (counts.Checked != total) throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "A measured ledger does not add exactly its own members to its manifest.");
        await Report(true);
        return counts;
    }

    private async Task CheckBatchAsync(string ledgerId, long generation, IReadOnlyList<LedgerValue> batch, Counts counts, Func<Task> report, CancellationToken token)
    {
        // An object outside 9 bytes to 512 MiB, or the all-zero key, can never have a receipt: it is not stored here.
        var keyed = batch.Select(value => (Key: value.Key, value.Bytes)).ToArray();
        var requests = keyed.Where(item => item.Bytes is >= 9 and <= VerifiedObjectStore.MaxObjectBytes && item.Key.Any(ch => ch != '0')).ToArray();
        var receipts = await catalog.ReadEncodedReceiptsAsync(requests, token);
        var corrupt = await catalog.ReadKnownCorruptAsync(requests, token);
        var found = new List<DownloadCheckObject>(DiscoveryCatalog.DownloadCheckWriteRows);
        async Task Flush()
        {
            if (found.Count == 0) return;
            if (!await catalog.WriteDownloadCheckAsync(ledgerId, generation, found.ToArray(), token))
                throw new DiscoveryException("DOWNLOAD_CHECK_STALE", "Another check replaced this one before it finished.", true);
            found.Clear();
        }
        foreach (var (key, _) in keyed)
        {
            token.ThrowIfCancellationRequested();
            string state;
            if (!receipts.TryGetValue(key, out var receipt)) state = "notStored";
            else if (corrupt.Contains(key)) state = "corrupt";
            else
            {
                try
                {
                    await using var verified = resources is null ? await objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, token)
                        : await objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, resources, BackgroundWorkContext.Instance, token);
                    state = "verified";
                }
                // The store journals detected corruption itself, so the stored figure drops the object too.
                catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_CORRUPT") { state = "corrupt"; }
                catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_MISSING") { state = "missing"; }
            }
            switch (state)
            {
                case "verified": counts.Verified++; break;
                case "missing": counts.Missing++; break;
                case "corrupt": counts.Corrupt++; break;
                default: counts.NotStored++; break;
            }
            counts.Checked++;
            if (state != "notStored")
            {
                found.Add(new(key, state));
                if (found.Count == DiscoveryCatalog.DownloadCheckWriteRows) await Flush();
            }
            await report();
        }
        await Flush();
    }
}
