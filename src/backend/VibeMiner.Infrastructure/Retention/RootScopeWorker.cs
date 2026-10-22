using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Measures, after Open, every file a build's ROOT lists outside its download manifest (D066). One build at a time, at
/// background priority through the shared acquisition pipeline and background CPU and staging permits, so interactive
/// work comes first. It reads ENCODING and ROOT, finds the uncovered files (at most 6,000,000 distinct ROOT content keys),
/// publishes the manifest ledger extended with their encoded objects and records the measured scope. A file list that
/// cannot be read ends manifest-only with the reason; any other failure is retried, three attempts at most, then left
/// failed for the next Open to retry. Cancellation and shutdown leave the measurement pending, so a restart resumes it;
/// a result for a measurement that was reset in between is discarded.
/// </summary>
public sealed class RootScopeWorker : IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly AcquisitionResources resources;
    private readonly ConfigurationWorkspacePreparer configurations;
    private readonly ResolverArtifacts resolvers;
    private readonly MetadataAcquirer roots;
    private readonly DownloadLedger ledgers;
    private readonly Action settled;
    private readonly BackgroundMetadataGate passes;
    private readonly TimeProvider clock;
    private readonly TimeSpan retryDelay;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycle = new();
    private Task? loop;

    public RootScopeWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client, ISharedAcquisition? shared,
        AcquisitionResources resources, Action settled, BackgroundMetadataGate? passes = null)
        : this(catalog, objects, client, shared, resources, settled, null, passes: passes) { }

    internal RootScopeWorker(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client, ISharedAcquisition? shared,
        AcquisitionResources resources, Action settled, TimeProvider? clock, TimeSpan? retryDelay = null, BackgroundMetadataGate? passes = null)
    {
        this.catalog = catalog; this.objects = objects; this.resources = resources; this.settled = settled; this.passes = passes ?? new();
        this.clock = clock ?? TimeProvider.System; this.retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
        var work = BackgroundWorkContext.Instance;
        configurations = new(catalog, objects, client, resources: resources);
        resolvers = new(catalog, objects, new MetadataAcquirer(client, objects, shared: shared, resources: resources, work: work), resources, work);
        // ROOT is read while the encoding lookup is held, so it has its own one-at-a-time acquirer.
        roots = new(client, objects, shared: shared, resources: resources, work: work);
        ledgers = new(objects, resources, work);
    }

    /// <summary>Starts the loop. It first resumes any measurement left pending by an earlier run.</summary>
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
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var outcome = await MeasureNextAsync(stop.Token);
                if (outcome == RootScopeOutcome.Retry) await Task.Delay(retryDelay, clock, stop.Token);
                else if (outcome == RootScopeOutcome.Idle) await catalog.WaitForRootScopeAsync(stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception)
            {
                // A catalog fault never ends the loop; wait before looking again.
                try { await Task.Delay(retryDelay, clock, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }
    }

    internal enum RootScopeOutcome { Idle, Measured, ManifestOnly, Failed, Retry, Superseded }

    /// <summary>
    /// Measures the oldest pending build, if any, to one terminal or retry outcome. Every fault other than this worker's own
    /// cancellation counts as an attempt against that build, so a build that keeps failing ends failed instead of being
    /// read again forever. Every terminal outcome wakes the retention driver: a queued Download all waits for it.
    /// </summary>
    internal async Task<RootScopeOutcome> MeasureNextAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var next = await catalog.ClaimNextRootScopeAsync(token);
            if (next.Settled > 0) settled();
            if (next.Work is not { } work) return RootScopeOutcome.Idle;
            try
            {
                var (ledger, binding) = await MeasureAsync(work, token);
                if (!await catalog.CompleteRootScopeAsync(work, ledger, binding, token)) return RootScopeOutcome.Superseded;
                settled();
                return RootScopeOutcome.Measured;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (DiscoveryException failure) when (Unreadable(failure.Code))
            {
                return Settle(await catalog.FailRootScopeAsync(work, true, new(failure.Code,
                    "This build's file list could not be read, so the figure covers its download manifest only.", false), token));
            }
            catch (Exception failure)
            {
                var code = failure is DiscoveryException typed ? typed.Code : "ROOT_SCOPE_UNAVAILABLE";
                return Settle(await catalog.FailRootScopeAsync(work, false, new(code,
                    "Every file the build lists could not be measured yet. Opening the build again retries.", true), token));
            }
        }
        finally { gate.Release(); }
    }

    private RootScopeOutcome Settle(string? state)
    {
        if (state is null) return RootScopeOutcome.Superseded;
        if (state == "pending") return RootScopeOutcome.Retry;
        settled();
        return state == "manifestOnly" ? RootScopeOutcome.ManifestOnly : RootScopeOutcome.Failed;
    }

    private static bool Unreadable(string code) => code is "METADATA_MISSING" or "METADATA_UNSUPPORTED" or "CONTENT_UNRESOLVED" or
        "CONTENT_ROOT_UNSUPPORTED" or "RESOLVER_LIMIT" or "ROOT_UNSUPPORTED" or "ROOT_INVALID" or "ROOT_LIMIT" or "ROOT_SCOPE_LIMIT" or "ROOT_SCOPE_INVALID";

    private async Task<(DownloadLedgerArtifact Ledger, DownloadScopeBinding Binding)> MeasureAsync(RootScopeWork work, CancellationToken token)
    {
        static Task Quiet(string _) => Task.CompletedTask;
        var inputs = await configurations.PrepareInputsAsync(new(work.ProductId, work.BuildIdentity, work.SourceRegion), Quiet, token);
        var source = new EncodedSourceContext(work.ProductId, work.BuildIdentity, work.SourceRegion,
            inputs.Configurations.Single(receipt => receipt.Role == "cdnConfig").Sha256, inputs.Context.Reference);
        var encoding = DownloadDeclarations.ReadEncoding(inputs.BuildConfiguration);
        await using var resolver = await resolvers.OpenEncodingAsync(inputs.Route, encoding, Quiet, token, source);
        MetadataExpectation rootExpected;
        using (await resources.EnterCpuAsync(BackgroundWorkContext.Instance, token))
            rootExpected = await SelectedFileAcquirer.ResolveAsync(resolver.Lookup, "root", SelectedFileAcquirer.RootKey(inputs.BuildConfiguration), token);
        if (rootExpected.DecodedBytes > VerifiedObjectStore.MaxObjectBytes)
            throw new DiscoveryException("ROOT_LIMIT", "This build's file list exceeds the supported object size.");
        await using var root = await roots.AcquireAsync(inputs.Route, rootExpected, Quiet, token, await catalog.FindMetadataAsync(rootExpected.EncodingKey, token), source);
        await catalog.RecordMetadataAsync(root.Receipt, token);
        RootScope scope;
        // Its key arrays never coexist with the file catalog's (D071); a waiting measurement goes first.
        using var grant = await passes.EnterAsync(true, token);
        // Every CAS proof happens before the CPU permit is taken: never hold CPU while opening CAS objects.
        await using (var manifest = await ledgers.OpenReadSessionAsync(work.Manifest, token, resources, BackgroundWorkContext.Instance))
        await using (var rootDecoded = root.OpenDecoded())
        await using (var encodingDecoded = await objects.OpenVerifiedAsync(resolver.DecodedSha256, resolver.DecodedBytes, resources, BackgroundWorkContext.Instance, token))
        using (await resources.EnterCpuAsync(BackgroundWorkContext.Instance, token))
            scope = await RootScopes.ComputeAsync(rootDecoded, long.Parse(root.Receipt.DecodedBytes, System.Globalization.CultureInfo.InvariantCulture),
                encodingDecoded, resolver.DecodedBytes, _ => Task.FromResult(manifest.OpenOrderedCursor()), Quiet, token);
        await catalog.RecordMetadataAsync(root.Receipt with { ParserRevision = "root-v1" }, token);
        // Proving the manifest ledger again inside the merge is the extended ledger's only input besides the gap.
        var ledger = await ledgers.ExtendAsync(work.Manifest, scope.Objects, token);
        var binding = new DownloadScopeBinding(root.Receipt.Sha256, resolver.Receipt.Sha256, DiscoveryCatalog.DownloadRootScope, scope.UnresolvableContent);
        await catalog.RecordDownloadArtifactAsync(work.ManifestSource with { ParserRevision = "download-v1" }, ledger, token, binding);
        return (ledger, binding);
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (lifecycle) { stop.Cancel(); running = loop; }
        if (running is not null) await running;
        await gate.WaitAsync(); gate.Release();
        configurations.Dispose(); stop.Dispose();
    }
}

/// <summary>Background priority for engine-owned work that no screen waits on.</summary>
internal sealed class BackgroundWorkContext : IAcquisitionWorkContext
{
    internal static readonly BackgroundWorkContext Instance = new();
    public AcquisitionWorkSnapshot Snapshot { get; } = new(1, AcquisitionPriority.Background, []);
    public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
    public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
}
