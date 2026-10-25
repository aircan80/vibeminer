using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Prepares, after Open, the ROOT and install memberships Files lists (D071). One build at a time, at background priority
/// through the shared acquisition pipeline and background CPU and staging permits, so interactive work comes first. It
/// reads install, ROOT and ENCODING, resolves every membership in memory (<see cref="FileCatalogIndex"/>), then reads
/// ROOT again and writes the rows in batches of <see cref="DiscoveryCatalog.FileCatalogBatchRows"/>, each with the durable
/// position, and seals the generation. Its build-sized passes share <see cref="BackgroundMetadataGate"/> with the ROOT
/// scope measurement, so the two never hold their arrays together. A file list that cannot be read ends unsupported with
/// the reason; any other failure is retried, three attempts at most, then left failed for the next Open to retry.
/// Cancellation and shutdown leave it pending: the next run derives the rows again, checks the saved ones against them and
/// continues after them. Saved rows that differ start it again from the beginning.
/// </summary>
public sealed class FileCatalogWorker : IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly AcquisitionResources resources;
    private readonly BackgroundMetadataGate passes;
    private readonly ConfigurationWorkspacePreparer configurations;
    private readonly ResolverArtifacts resolvers;
    private readonly MetadataAcquirer sources;
    private readonly TimeProvider clock;
    private readonly TimeSpan retryDelay;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycle = new();
    private Task? loop;
    private readonly object runningLock = new();
    private (string Product, string Build, CancellationTokenSource Stop)? current;
    /// <summary>Rows per write batch; tests use small batches to stop between them.</summary>
    internal int BatchRows { get; init; } = DiscoveryCatalog.FileCatalogBatchRows;
    /// <summary>Tests only: runs after each committed batch with the rows written so far.</summary>
    internal Func<long, CancellationToken, Task>? AfterBatch { get; init; }
    private static IAcquisitionWorkContext Work => BackgroundWorkContext.Instance;

    public FileCatalogWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client, ISharedAcquisition? shared,
        AcquisitionResources resources, BackgroundMetadataGate passes)
        : this(catalog, objects, client, shared, resources, passes, null) { }

    internal FileCatalogWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client, ISharedAcquisition? shared,
        AcquisitionResources resources, BackgroundMetadataGate passes, TimeProvider? clock, TimeSpan? retryDelay = null)
    {
        this.catalog = catalog; this.objects = objects; this.resources = resources; this.passes = passes;
        this.clock = clock ?? TimeProvider.System; this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        configurations = new(catalog, objects, client, resources: resources);
        resolvers = new(catalog, objects, new MetadataAcquirer(client, objects, shared: shared, resources: resources, work: Work), resources, Work);
        // Install and ROOT are read one after the other while the encoding lookup is held, so they share their own acquirer.
        sources = new(client, objects, shared: shared, resources: resources, work: Work);
        catalog.FileCatalogStopped += StopRequested;
    }

    /// <summary>The user stopped this build's preparation: cancel it now if it is the one running.</summary>
    private void StopRequested(string product, string build)
    {
        lock (runningLock)
            if (current is { } job && job.Product == product && job.Build == build) job.Stop.Cancel();
    }

    /// <summary>Free bytes on the volume of the file catalog folder (the longest mount point that contains it).</summary>
    internal Func<long>? FreeBytesForTest { get; init; }
    private long FreeBytes() => FreeBytesForTest?.Invoke() ?? FreeSpace(catalog.FileCatalogDirectory);
    internal static long FreeSpace(string path)
    {
        var full = Path.GetFullPath(path);
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch (IOException) { return false; } }).ToArray();
        var root = VolumeOf(full, drives.Select(d => d.RootDirectory.FullName));
        // No volume found is a refusal, never unlimited space.
        if (root is null) throw new DiscoveryException("STORAGE_UNAVAILABLE", "The free disk space of the data folder could not be read.", true) { Cause = $"No mounted volume contains {full}." };
        return drives.First(d => d.RootDirectory.FullName == root).AvailableFreeSpace;
    }

    /// <summary>The longest mount point that contains the path on whole path segments: /home contains /home/a, not /home2.</summary>
    internal static string? VolumeOf(string path, IEnumerable<string> roots)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static bool Separator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        return roots.Where(root => path.Equals(root.TrimEnd('/', '\\'), comparison) || path.StartsWith(root, comparison) && (root.Length > 0 && Separator(root[^1]) || path.Length > root.Length && Separator(path[root.Length])))
            .OrderByDescending(root => root.Length).FirstOrDefault();
    }

    /// <summary>Starts the loop. It first removes abandoned rows, then resumes any preparation left pending by an earlier run.</summary>
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
        var fresh = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (fresh) { await catalog.ResetFileCatalogStagesAsync(stop.Token); fresh = false; }
                var leftovers = await CleanupAsync(stop.Token);
                var outcome = await PrepareNextAsync(stop.Token);
                // A retry waits its delay, but a newly opened build, a resume or a pause change ends the wait early.
                // Files cleanup could not delete are tried again the same way: after the delay, or at the next wake-up.
                if (outcome == FileCatalogOutcome.Retry || outcome == FileCatalogOutcome.Idle && leftovers) await WaitForRetryAsync();
                else if (outcome == FileCatalogOutcome.Idle) await catalog.WaitForFileCatalogAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception failure)
            {
                EngineLog.Write($"File catalog worker fault ({DiscoveryCatalog.Cause(failure)}); it looks again after the retry delay.");
                // A catalog fault never ends the loop; wait before looking again.
                try { await Task.Delay(retryDelay, clock, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }
    }

    /// <summary>
    /// The retry delay or the next wake-up, whichever comes first. The other is cancelled and awaited before the loop goes on,
    /// so no wait outlives its turn: a leftover one would take the one-slot wake-up meant for the next wait, and an Open or
    /// Resume after the last attempt would then sit queued until some later wake-up.
    /// </summary>
    private async Task WaitForRetryAsync()
    {
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var delay = Task.Delay(retryDelay, clock, turn.Token);
        var wake = catalog.WaitForFileCatalogAsync(turn.Token);
        await Task.WhenAny(delay, wake);
        await turn.CancelAsync();
        try { await Task.WhenAll(delay, wake); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        stop.Token.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// One cleanup pass, one bounded slice per catalog operation so interactive SQL interleaves. A file that cannot be
    /// deleted is skipped for this pass, never stopping the worker; true when such leftovers remain, so the loop looks
    /// again after the retry delay instead of waiting for the next Open (D085).
    /// </summary>
    internal async Task<bool> CleanupAsync(CancellationToken token)
    {
        var skipped = new HashSet<long>();
        while (await catalog.CleanupFileCatalogsAsync(token, skipped)) { }
        return skipped.Count > 0;
    }

    internal enum FileCatalogOutcome { Idle, Ready, Unsupported, Failed, Retry, Restarted, Superseded, Stopped, Paused, Yielded }

    private sealed class Superseded : Exception;
    private sealed class SavedRowsDiffer : Exception;
    /// <summary>The build's Download all was paused, or another build was opened since: this one waits with its rows kept.</summary>
    private sealed class Deferred(FileCatalogTurn turn) : Exception { internal FileCatalogTurn Turn { get; } = turn; }
    private static Exception Turned(FileCatalogTurn turn) => turn == FileCatalogTurn.Superseded ? new Superseded() : new Deferred(turn);

    /// <summary>
    /// Prepares the pending build opened most recently, if any, to one terminal, retry, restart or deferred outcome. Every
    /// fault other than cancellation, a user stop or a deferral counts as an attempt against that build, so one that keeps
    /// failing ends failed; a refusal retrying cannot fix (STORAGE_FULL) ends failed at once.
    /// </summary>
    internal async Task<FileCatalogOutcome> PrepareNextAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var next = await catalog.ClaimNextFileCatalogAsync(token);
            if (next.Work is not { } work) return FileCatalogOutcome.Idle;
            using var job = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (runningLock) current = (work.ProductId, work.BuildIdentity, job);
            try
            {
                return await PrepareAsync(work, job.Token) ? FileCatalogOutcome.Ready : FileCatalogOutcome.Superseded;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Shut down, not failed: it stays pending with its saved rows. The stage says it is no longer running; a
                // process that ends without getting here is reset when the next run starts.
                try { await catalog.SetFileCatalogStageAsync(work, "queued", CancellationToken.None); }
                catch (Exception) { /* The catalog is closing; the next run's start resets the stage. */ }
                throw;
            }
            // The user stopped it: the row is already stopped, with its written rows kept for Resume or the next Open.
            catch (OperationCanceledException) when (job.IsCancellationRequested) { return FileCatalogOutcome.Stopped; }
            catch (Superseded) { return FileCatalogOutcome.Superseded; }
            catch (Deferred deferred)
            {
                await catalog.SetFileCatalogStageAsync(work, "queued", token);
                return deferred.Turn == FileCatalogTurn.Paused ? FileCatalogOutcome.Paused : FileCatalogOutcome.Yielded;
            }
            catch (SavedRowsDiffer)
            {
                return await catalog.RestartFileCatalogAsync(work, token) ? FileCatalogOutcome.Restarted : FileCatalogOutcome.Superseded;
            }
            catch (Exception failure)
            {
                var code = Code(failure);
                var cause = DiscoveryCatalog.Cause(failure);
                EngineLog.Write($"File catalog of build {work.BuildIdentity[..12]} failed: {code} ({cause ?? "no further cause"}).");
                var (kind, message, retryable) = Unreadable(code)
                    ? ("unsupported", "This build's file list could not be read. Trying again reads the same bytes, so it won't help.", false)
                    : code == "STORAGE_FULL" ? ("final", "There is not enough free disk space to save this build's file list.", false)
                    : ("attempt", "The file list could not be prepared.", true);
                return Settle(await catalog.FailFileCatalogAsync(work, kind, new(code, message, retryable, cause), token));
            }
            finally { lock (runningLock) current = null; }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The typed code of a failure. A full disk is STORAGE_FULL whichever layer saw it; another local file that cannot be
    /// written or read is a storage problem, named as Open names it.
    /// </summary>
    internal static string Code(Exception failure)
    {
        if (failure is DiscoveryException typed) return typed.Code;
        if (failure is IOException io && (io.HResult & 0xFFFF) is 28 or 39 or 112) return "STORAGE_FULL";
        return failure is IOException or UnauthorizedAccessException ? "STORAGE_UNAVAILABLE" : "FILE_CATALOG_UNAVAILABLE";
    }

    private static FileCatalogOutcome Settle(string? state) => state switch
    {
        null => FileCatalogOutcome.Superseded,
        "pending" => FileCatalogOutcome.Retry,
        "unsupported" => FileCatalogOutcome.Unsupported,
        _ => FileCatalogOutcome.Failed,
    };

    /// <summary>The file list itself cannot be read or is outside this version's bounds; trying again would read the same bytes.</summary>
    private static bool Unreadable(string code) => code is "METADATA_MISSING" or "METADATA_UNSUPPORTED" or "METADATA_INVALID" or "METADATA_LIMIT" or
        "METADATA_UNRESOLVED" or "METADATA_SIZE_CONFLICT" or "CONTENT_UNRESOLVED" or "CONTENT_ROOT_UNSUPPORTED" or "RESOLVER_LIMIT" or
        "ROOT_UNSUPPORTED" or "ROOT_INVALID" or "ROOT_LIMIT" or "ROOT_SCOPE_INVALID" or "IN_UNSUPPORTED" or "IN_INVALID" or "IN_LIMIT" or
        "EN_UNSUPPORTED" or "EN_INVALID" or "EN_LIMIT" or "FILE_CATALOG_LIMIT" or "INVENTORY_LIMIT" or "INVENTORY_SIZE_CONFLICT";

    private async Task Stage(FileCatalogWork work, string stage, CancellationToken token)
    {
        var turn = await catalog.SetFileCatalogStageAsync(work, stage, token);
        if (turn != FileCatalogTurn.Run) throw Turned(turn);
    }

    private async Task<bool> PrepareAsync(FileCatalogWork work, CancellationToken token)
    {
        static Task Quiet(string _) => Task.CompletedTask;
        await Stage(work, "acquiring", token);
        var inputs = await configurations.PrepareInputsAsync(new(work.ProductId, work.BuildIdentity, work.SourceRegion), Quiet, token);
        var source = new EncodedSourceContext(work.ProductId, work.BuildIdentity, work.SourceRegion,
            inputs.Configurations.Single(receipt => receipt.Role == "cdnConfig").Sha256, inputs.Context.Reference);
        var encoding = DownloadDeclarations.ReadEncoding(inputs.BuildConfiguration);
        var installDeclaration = DownloadDeclarations.ReadInstall(inputs.BuildConfiguration);
        var metadata = new List<MetadataReceipt>(3);
        FileCatalogIndex index;
        VerifiedMetadata? root = null; IDisposable? grant = null;
        try
        {
            await using (var resolver = await resolvers.OpenEncodingAsync(inputs.Route, encoding, Quiet, token, source))
            {
                metadata.Add(resolver.Receipt);
                MetadataExpectation rootExpected; MetadataExpectation? installExpected = null;
                using (await resources.EnterCpuAsync(Work, token))
                {
                    rootExpected = await SelectedFileAcquirer.ResolveAsync(resolver.Lookup, "root", SelectedFileAcquirer.RootKey(inputs.BuildConfiguration), token);
                    if (installDeclaration is not null) installExpected = installDeclaration.DirectExpectation ?? await ResolveAsync(resolver.Lookup, installDeclaration, token);
                }
                if (rootExpected.DecodedBytes > VerifiedObjectStore.MaxObjectBytes || installExpected?.DecodedBytes > VerifiedObjectStore.MaxObjectBytes)
                    throw new DiscoveryException("ROOT_LIMIT", "This build's file list exceeds the supported object size.");
                IReadOnlyList<FileCatalogIndex.InstallRow> install = [];
                if (installExpected is not null)
                {
                    await using var installSource = await sources.AcquireAsync(inputs.Route, installExpected, Quiet, token,
                        await catalog.FindMetadataAsync(installExpected.EncodingKey, token), source);
                    await catalog.RecordMetadataAsync(installSource.Receipt, token);
                    var pass = new FileCatalogIndex.InstallPass(token);
                    await using (var decoded = installSource.OpenDecoded())
                    using (await resources.EnterCpuAsync(Work, token))
                        await InstallInventoryReader.ReadAsync(decoded, new(MaxDecodedBytes: installExpected.DecodedBytes), pass, token);
                    var installReceipt = installSource.Receipt with { ParserRevision = "install-v1" };
                    await catalog.RecordMetadataAsync(installReceipt, token);
                    metadata.Add(installReceipt); install = pass.Rows;
                }
                root = await sources.AcquireAsync(inputs.Route, rootExpected, Quiet, token, await catalog.FindMetadataAsync(rootExpected.EncodingKey, token), source);
                await catalog.RecordMetadataAsync(root.Receipt, token);
                await Stage(work, "waiting", token);
                // The build-sized arrays live from here to the seal; the ROOT scope measurement waits meanwhile.
                grant = await passes.EnterAsync(false, token);
                await Stage(work, "reading", token);
                // Every CAS proof happens before the CPU permit is taken: never hold CPU while opening CAS objects.
                await using (var rootDecoded = root.OpenDecoded())
                await using (var encodingDecoded = await objects.OpenVerifiedAsync(resolver.DecodedSha256, resolver.DecodedBytes, resources, Work, token))
                using (await resources.EnterCpuAsync(Work, token))
                    index = await FileCatalogIndex.BuildAsync(rootDecoded, long.Parse(root.Receipt.DecodedBytes, CultureInfo.InvariantCulture), install,
                        encodingDecoded, resolver.DecodedBytes, token);
                // As the legacy catalog does (#20), a content with several sized EKeys uses a stored one when there is one.
                var stored = new HashSet<string>(StringComparer.Ordinal);
                foreach (var slice in index.AlternativeKeys.Chunk(256)) stored.UnionWith(await catalog.StoredFileCatalogKeysAsync(slice, token));
                index.PreferStored(stored);
                var rootReceipt = root.Receipt with { ParserRevision = "root-v1" };
                await catalog.RecordMetadataAsync(rootReceipt, token);
                metadata.Insert(1, rootReceipt);
            }
            // The encoding lookup is released; writing needs only ROOT and the index.
            return await WriteAsync(work, root, index, metadata, token);
        }
        finally
        {
            grant?.Dispose();
            if (root is not null) await root.DisposeAsync();
        }
    }

    private static async Task<MetadataExpectation> ResolveAsync(EncodingFileLookup lookup, MetadataDeclaration declaration, CancellationToken token)
    {
        var content = await lookup.FindContentAsync(declaration.ContentKey, token) ??
            throw new DiscoveryException("METADATA_UNRESOLVED", "The declared install manifest content is absent from encoding metadata.");
        if (declaration.DecodedBytes is { } declared && declared != content.DecodedBytes)
            throw new DiscoveryException("METADATA_SIZE_CONFLICT", "Install metadata decoded lengths disagree.");
        foreach (var key in content.EncodingKeys.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (declaration.EncodingKey is { } required && key != required) continue;
            if (await lookup.FindEncodingAsync(key, token) is not { } selected) continue;
            if (declaration.EncodedBytes is { } encoded && encoded != selected.EncodedBytes)
                throw new DiscoveryException("METADATA_SIZE_CONFLICT", "Install metadata encoded lengths disagree.");
            return new("install", content.ContentKey, selected.EncodingKey, selected.EncodedBytes, content.DecodedBytes);
        }
        throw new DiscoveryException("METADATA_UNRESOLVED", "The install manifest has no complete encoded representation.");
    }

    private async Task<bool> WriteAsync(FileCatalogWork work, VerifiedMetadata root, FileCatalogIndex index, List<MetadataReceipt> metadata, CancellationToken token)
    {
        var total = checked(index.RootMembers + index.Install.Count);
        var identity = JsonSourceIdentity(metadata);
        var begun = await catalog.BeginFileCatalogGenerationAsync(work, identity, total, FreeBytes(), token);
        if (begun.Turn != FileCatalogTurn.Run) throw Turned(begun.Turn);
        var writer = new RowWriter(catalog, resources, work, begun.Inventory, begun.Written, BatchRows, AfterBatch, FreeBytes, token);
        try
        {
            await writer.EnterAsync();
            await using (var decoded = root.OpenDecoded())
                await RootInventoryReader.ReadAsync(decoded, new(MaxDecodedBytes: long.Parse(root.Receipt.DecodedBytes, CultureInfo.InvariantCulture)),
                    new RootRows(writer, index), token);
            if (writer.Id != index.RootMembers) throw new DiscoveryException("FILE_CATALOG_CHANGED", "The build file list changed between its two reads.");
            var groups = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in index.Install)
            {
                var id = writer.Id + 1;
                var group = groups.TryGetValue(row.Entry.Name, out var first) ? first : groups[row.Entry.Name] = id;
                var (ekey, encoded, decoded) = index.Resolve(row.Entry.ContentKey);
                if (decoded is { } size && size != row.Entry.DecodedBytes)
                    throw new DiscoveryException("INVENTORY_SIZE_CONFLICT", "Install and encoding decoded lengths disagree.");
                await writer.EmitAsync(new(id, "install", row.Entry.Name, row.Entry.ContentKey, ekey, encoded, decoded, row.Entry.DecodedBytes,
                    null, null, null, null, row.Ordinal.ToString(CultureInfo.InvariantCulture), group));
            }
            await writer.FlushAsync();
        }
        finally { writer.Exit(); }
        if (writer.Id != total) throw new DiscoveryException("FILE_CATALOG_CHANGED", "The build file list changed between its two reads.");
        await Stage(work, "sealing", token);
        var stats = new FileCatalogStats(index.RootFiles, index.InstallFiles, index.RootMembers, index.Install.Count, index.DistinctContent,
            writer.Unresolved, writer.StoredRoot + writer.StoredInstall, writer.StoredRoot, index.Variants);
        return await catalog.SealFileCatalogAsync(work, begun.Inventory, stats, metadata, token);
    }

    /// <summary>The exact sources the rows are derived from; saved rows are continued only for the same ones.</summary>
    private static string JsonSourceIdentity(IReadOnlyList<MetadataReceipt> metadata) => System.Text.Json.JsonSerializer.Serialize(new
    {
        revision = "file-catalog-v1",
        sources = metadata.Select(receipt => new { receipt.Role, receipt.EncodingKey, receipt.Sha256, receipt.EncodedBytes, receipt.DecodedBytes }).ToArray(),
    });

    /// <summary>Second pass over ROOT: each entry becomes one resolved membership row, in stream order.</summary>
    private sealed class RootRows(RowWriter writer, FileCatalogIndex index) : IRootConsumer
    {
        public async ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken)
        {
            var (ekey, encoded, decoded) = index.Resolve(entry.ContentKey);
            await writer.EmitAsync(new(writer.Id + 1, "root", null, entry.ContentKey, ekey, encoded, decoded, null, entry.FileDataId, entry.LocaleFlags,
                entry.ContentFlags, entry.NameHash?.ToString(CultureInfo.InvariantCulture), FileCatalogIndex.RootRaw(blockOrdinal, entryOrdinal, entry.RawContentFlags), null));
        }
    }

    /// <summary>
    /// Rows up to the saved position are checked against the saved ones; later rows are written in batches. Rows are
    /// derived under a background CPU permit, which is released around every catalog operation.
    /// </summary>
    private sealed class RowWriter(DiscoveryCatalog catalog, AcquisitionResources resources, FileCatalogWork work, long inventory, long saved, int batchRows,
        Func<long, CancellationToken, Task>? afterBatch, Func<long> freeBytes, CancellationToken token)
    {
        private readonly List<FileCatalogRow> batch = new(batchRows);
        private readonly Queue<(FileCatalogRow Row, bool Stored)> savedRows = new();
        private IDisposable? cpu;
        private long batches;
        internal long Id { get; private set; }
        internal long Unresolved { get; private set; }
        internal long StoredRoot { get; private set; }
        internal long StoredInstall { get; private set; }

        internal async Task EnterAsync() => cpu = await resources.EnterCpuAsync(Work, token);
        internal void Exit() { cpu?.Dispose(); cpu = null; }

        internal async ValueTask EmitAsync(FileCatalogRow row)
        {
            token.ThrowIfCancellationRequested();
            if (row.Id != Id + 1) throw new InvalidOperationException("File catalog rows must be emitted in order.");
            Id = row.Id;
            if (row.EncodingKey is null) Unresolved++;
            if (row.Id <= saved)
            {
                if (savedRows.Count == 0)
                {
                    Exit();
                    foreach (var value in await catalog.ReadFileCatalogRowsAsync(inventory, row.Id - 1, (int)Math.Min(batchRows, saved - row.Id + 1), token))
                        savedRows.Enqueue(value);
                    await EnterAsync();
                }
                if (!savedRows.TryDequeue(out var stored) || stored.Row != row) throw new SavedRowsDiffer();
                if (stored.Stored) { if (row.Family == "root") StoredRoot++; else StoredInstall++; }
                return;
            }
            batch.Add(row);
            if (batch.Count == batchRows) await FlushAsync();
        }

        internal async Task FlushAsync()
        {
            if (batch.Count == 0) return;
            Exit();
            // The disk is checked before every 64th batch: a volume that filled meanwhile stops cleanly before SQLite fails.
            if (++batches % 64 == 0 && freeBytes() < DiscoveryCatalog.FileCatalogReserveBytes)
                throw new DiscoveryException("STORAGE_FULL", "There is not enough free disk space to save this build's file list.") { Cause = $"Less than {DiscoveryCatalog.FileCatalogReserveBytes} bytes free while writing." };
            var stored = await catalog.AddFileCatalogRowsAsync(work, inventory, batch[0].Id - 1, batch, token);
            if (stored.Turn != FileCatalogTurn.Run) throw Turned(stored.Turn);
            StoredRoot += stored.Root; StoredInstall += stored.Install;
            batch.Clear();
            if (afterBatch is not null) await afterBatch(Id, token);
            await EnterAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        catalog.FileCatalogStopped -= StopRequested;
        lock (lifecycle) { stop.Cancel(); running = loop; }
        if (running is not null) await running;
        await gate.WaitAsync(); gate.Release();
        configurations.Dispose(); stop.Dispose();
    }
}
