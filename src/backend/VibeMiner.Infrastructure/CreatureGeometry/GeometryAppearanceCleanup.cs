namespace VibeMiner.Infrastructure;

/// <summary>
/// Removes retained Creature appearance rows no current key reaches, and the VMA1 files no remaining row names (D103). It runs
/// once after start, in batches of at most <see cref="BatchRows"/> rows per catalog operation, and never deletes a file while an
/// appearance stream or publication holds it (<see cref="GeometryAppearanceFileUses"/>). A file goes before its row, so a crash
/// in between leaves a superseded row whose file is already gone; the next run removes the row. A file in use, or one that can't
/// be deleted, keeps its row and is tried again after <see cref="RetryDelay"/>, at most <see cref="MaximumRetries"/> times.
/// </summary>
public sealed class GeometryAppearanceCleanup : IAsyncDisposable
{
    internal const int BatchRows = 16, MaximumRetries = 8;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly GeometryAppearanceFileUses uses;
    private readonly TimeProvider clock;
    private readonly TimeSpan retryDelay;
    private readonly CancellationTokenSource stop = new();
    private readonly object lifecycle = new();
    private Task? loop;

    public GeometryAppearanceCleanup(DiscoveryCatalog catalog, VerifiedObjectStore objects, GeometryAppearanceFileUses uses)
        : this(catalog, objects, uses, null) { }

    internal GeometryAppearanceCleanup(DiscoveryCatalog catalog, VerifiedObjectStore objects, GeometryAppearanceFileUses uses,
        TimeProvider? clock, TimeSpan? retryDelay = null)
    {
        this.catalog = catalog; this.objects = objects; this.uses = uses;
        this.clock = clock ?? TimeProvider.System; this.retryDelay = retryDelay ?? RetryDelay;
    }

    /// <summary>Test seam: runs after a file is deleted and before its row is, so a crash there can be simulated.</summary>
    internal Func<string, Task>? AfterFileDeleted { get; init; }
    /// <summary>Test seam: runs after a row's file was found unnamed and before its delete window opens.</summary>
    internal Func<string, Task>? BeforeDelete { get; init; }
    /// <summary>Test seam: observes each batch of superseded rows read.</summary>
    internal Action<int>? BatchRead { get; init; }

    /// <summary>The last pass's totals, for logs and tests.</summary>
    internal GeometryAppearanceCleanupResult? Last { get; private set; }

    public void Start()
    {
        lock (lifecycle) { ObjectDisposedException.ThrowIf(stop.IsCancellationRequested, this); loop ??= RunAsync(); }
    }

    /// <summary>The running loop, or a completed task before <see cref="Start"/>.</summary>
    internal Task Completion { get { lock (lifecycle) return loop ?? Task.CompletedTask; } }

    private async Task RunAsync()
    {
        await Task.Yield();
        for (var attempt = 0; !stop.IsCancellationRequested; attempt++)
        {
            try
            {
                var result = await PassAsync(stop.Token); Last = result;
                if (result.Rows + result.Files > 0 || result.Kept > 0)
                    EngineLog.Write($"geometry appearance cleanup: removed {result.Rows} rows and {result.Files} files ({result.Bytes} bytes); kept {result.Kept} in use or undeletable");
                if (result.Kept == 0 || attempt + 1 >= MaximumRetries) return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // A catalog or store fault never escapes; the pass is tried again later and nothing half-done is served.
                EngineLog.Write("geometry appearance cleanup failed: " + (error is VibeMiner.Domain.DiscoveryException d ? d.Code + " " + d.Cause : error.GetType().Name));
                if (attempt + 1 >= MaximumRetries) return;
            }
            try { await Task.Delay(retryDelay, clock, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }
    }

    /// <summary>One pass over every superseded row, in rowid order. Each catalog operation reads or changes at most
    /// <see cref="BatchRows"/> rows.</summary>
    internal async Task<GeometryAppearanceCleanupResult> PassAsync(CancellationToken token)
    {
        // Rows from before D103 first name their files, so the reference check below sees every file a row still uses.
        while (await catalog.NameGeometryAppearanceFilesAsync(BatchRows, token) > 0) await Task.Yield();
        int rows = 0, files = 0, kept = 0, skipped = 0; long bytes = 0, after = 0;
        while (true)
        {
            var batch = await catalog.ReadGeometryAppearanceGarbageAsync(after, BatchRows, token);
            BatchRead?.Invoke(batch.Count);
            if (batch.Count == 0) break;
            foreach (var row in batch)
            {
                token.ThrowIfCancellationRequested(); after = row.Rowid;
                // A malformed recorded file is that row's fault alone: it is kept and logged, and the pass goes on.
                if (row.Malformed) { skipped++; EngineLog.Write("geometry appearance cleanup: row " + row.Key[..Math.Min(16, row.Key.Length)] + " has a malformed key or recorded file; it is kept"); continue; }
                if (row.Sha256 is { } sha && !await catalog.IsGeometryAppearanceFileNamedAsync(sha, row.Key, token))
                {
                    if (BeforeDelete is { } before) await before(sha);
                    if (!uses.TryBeginDelete(sha)) { kept++; continue; }
                    try
                    {
                        // Checked again inside the delete window: no use can start and no row naming it can commit meanwhile.
                        if (!await catalog.IsGeometryAppearanceFileNamedAsync(sha, row.Key, token))
                        {
                            var deletion = await objects.DeleteDerivedAsync(sha, row.ByteLength!.Value, token);
                            if (deletion == ObjectDeletion.Kept) { kept++; continue; }
                            if (deletion == ObjectDeletion.Deleted) { files++; bytes += row.ByteLength.Value; }
                            if (AfterFileDeleted is { } hook) await hook(sha);
                        }
                    }
                    finally { uses.EndDelete(sha); }
                }
                if (await catalog.DeleteGeometryAppearanceAsync(row.Key, token)) rows++;
            }
            await Task.Yield();
        }
        return new(rows, files, bytes, kept, skipped);
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (lifecycle) { stop.Cancel(); running = loop; }
        if (running is not null) await running;
        stop.Dispose();
    }
}

internal sealed record GeometryAppearanceCleanupResult(int Rows, int Files, long Bytes, int Kept, int Skipped = 0);
