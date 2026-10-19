using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One serialized visible batch and one pinned source session; all acquisition stays shared.</summary>
public sealed class ItemIconsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, ItemsStore items,
    AssetResolutionSessions sessions, ISharedAcquisition shared, AcquisitionResources resources, IconArtifacts artifacts)
    : IItemIconsStore, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private AssetResolutionSession? session;
    private ItemsContext? sessionContext;
    private OpeningSourceReference? sessionReference;
    private TaskCompletionSource? preparation;
    private readonly Dictionary<long, TaskCompletionSource> openingReads = [];
    private readonly HashSet<ReadLease> reads = [];
    private long nextRead;
    private bool closed;
    private Task? disposal;

    public async Task<ItemIconBatch> PrepareBatchAsync(ItemIconBatchRequest request, Func<ItemIconProgress, Task> progress, CancellationToken token)
    {
        ItemIconsService.Validate(request); ArgumentNullException.ThrowIfNull(progress); token.ThrowIfCancellationRequested();
        TaskCompletionSource retired;
        lock (gate)
        {
            RequireOpen(); if (preparation is not null) throw Busy();
            preparation = retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(10));
        try { return await PrepareCoreAsync(request, progress, lifetime.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested)
        { throw new DiscoveryException("ITEM_ICONS_TIMEOUT", "Visible icon preparation exceeded its bounded time budget.", true); }
        finally { lock (gate) { preparation = null; retired.TrySetResult(); } }
    }

    private async Task<ItemIconBatch> PrepareCoreAsync(ItemIconBatchRequest request, Func<ItemIconProgress, Task> progress, CancellationToken token)
    {
        var authorized = await items.AuthorizeIconsAsync(request.Context, request.ArtifactId, request.ItemIds, token);
        var opening = await catalog.ResolveOpeningAsync(ItemIconAdmissionPolicy.Workspace(request.Context), token);
        var outcomes = new Dictionary<uint, ItemIconOutcome>();
        var resolving = new List<uint>(); var resolved = new List<AssetResolutionResult>();
        var sourceBudget = new ItemIconBatchSources();
        void AddResolved(AssetResolutionResult result)
        {
            if (sourceBudget.TryAdmit(result)) resolved.Add(result);
            else outcomes.Add(result.FileDataId, Failure(result.FileDataId,
                new("ITEM_ICON_ASSOCIATION_LIMIT", "The complete visible icon source associations exceed their bounded allowance.")));
        }
        foreach (var id in authorized.Items.Where(value => value.Admitted && value.IconFileDataId is not null)
            .Select(value => uint.Parse(value.IconFileDataId!, CultureInfo.InvariantCulture)).Where(value => value != 0).Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var saved = await catalog.FindItemIconAsync(opening, authorized.Snapshot, id, token);
                if (saved is null) { resolving.Add(id); continue; }
                try
                {
                    await using var probe = await artifacts.OpenVerifiedAsync(saved.Artifact, resources, null, token);
                    outcomes.Add(id, Ready(id, saved.Artifact));
                }
                catch (DiscoveryException error) when (error.Code == "LOCAL_OBJECT_MISSING")
                { AddResolved(new(id, AssetResolutionStatus.Ready, saved.Admission.Expectation, saved.Admission.Association)); }
            }
            catch (DiscoveryException error) when (!Stale(error)) { outcomes.Add(id, Failure(id, error)); }
        }
        if (resolving.Count > 0)
        {
            try
            {
                if (sessionContext != request.Context || sessionReference != opening.Reference || session is null)
                {
                    if (session is not null) await session.DisposeAsync(); session = null;
                    sessionContext = null; sessionReference = null;
                    session = await sessions.OpenAsync(ItemIconAdmissionPolicy.Workspace(request.Context), request.Context.ResearchLocale,
                        stage => progress(new(stage, "stage")), token);
                    sessionContext = request.Context; sessionReference = opening.Reference;
                }
                foreach (var result in (await session.ResolveAsync(resolving, stage => progress(new(stage, "stage")), token)).Results) AddResolved(result);
            }
            catch (DiscoveryException error) when (!Stale(error))
            { foreach (var id in resolving) outcomes.Add(id, Failure(id, error)); }
        }
        foreach (var value in resolved.Where(value => value.Status != AssetResolutionStatus.Ready))
            outcomes.Add(value.FileDataId, new("", N(value.FileDataId), value.Status switch
            {
                AssetResolutionStatus.MissingMembership => ItemIconStatus.MissingMembership,
                AssetResolutionStatus.AmbiguousVariants => ItemIconStatus.AmbiguousVariants,
                _ => ItemIconStatus.EncodingUnresolved
            }));
        var groups = resolved.Where(value => value.Status == AssetResolutionStatus.Ready)
            .GroupBy(value => value.Expectation!).ToArray();
        var completed = 0;
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var values = group.ToArray();
            try
            {
                var expected = group.Key;
                if (expected.EncodedBytes is < 9 or > IconArtifacts.MaxSourceBytes || expected.DecodedBytes is < 148 or > IconArtifacts.MaxSourceBytes)
                    throw new DiscoveryException("ASSET_LIMIT", "This optional icon exceeds the bounded source/decode allowance.");
                var retained = await ReusePngAsync(expected, token);
                ContentReceipt source; IconPngArtifact png;
                if (retained is not null) (source, png) = retained.Value;
                else
                {
                    var association = values[0].Association;
                    var sourceContext = new EncodedSourceContext(request.Context.ProductId, request.Context.BuildIdentity,
                        request.Context.SourceRegion, association.Configurations.Single(value => value.Role == "cdnConfig").Sha256, association.SourceReference);
                    var physical = await shared.AcquireAsync(new(expected.EncodingKey, expected.EncodedBytes), sourceContext,
                        AcquisitionPriority.Interactive, value => progress(new(value.Stage, value.Unit, value.Completed, value.Total)), token);
                    await progress(new("Verifying complete icon source", "icons", completed, groups.Length));
                    source = await ItemIconSourceVerifier.VerifyAsync(objects, resources, expected, physical, token);
                    await catalog.RecordContentAsync(source, token);
                    // Keep the first original receipt; local revalidation is not a new acquisition.
                    var original = await catalog.FindContentAsync(source.EncodingKey, token);
                    if (original is { Projection: null, VerifierRevision: SelectedFileAcquirer.VerifierRevision }) source = original;
                    using var staging = await resources.ReserveStagingAsync(IconArtifacts.MaxSourceBytes + IconArtifacts.MaxPngBytes + (4L << 20), null, token);
                    png = await artifacts.BuildAsync(source, 0, resources, null,
                        stage => progress(new(stage, "icons", completed, groups.Length)), token);
                }
                foreach (var value in values)
                {
                    var saved = new StoredItemIcon(new(request.Context, request.ArtifactId, value.FileDataId, value.Expectation!,
                        value.Association, source, png.RequestKey), png);
                    await catalog.CommitItemIconAsync(opening, authorized.Snapshot, saved, token);
                    outcomes.Add(value.FileDataId, Ready(value.FileDataId, png));
                }
            }
            catch (DiscoveryException error) when (!Stale(error))
            { foreach (var value in values) if (!outcomes.ContainsKey(value.FileDataId)) outcomes.Add(value.FileDataId, Failure(value.FileDataId, error)); }
            completed++; await progress(new("Prepared visible icon sources", "icons", completed, groups.Length));
        }
        token.ThrowIfCancellationRequested();
        // Revalidate capability after source work; a late result cannot authorize a replaced feature.
        await items.AuthorizeIconsAsync(request.Context, request.ArtifactId, [], token);
        return new(request.Context, request.ArtifactId, authorized.Items.Select(value => !value.Admitted
            ? new ItemIconOutcome(value.ItemId, null, ItemIconStatus.MissingItem)
            : value.IconFileDataId is null ? new(value.ItemId, null, ItemIconStatus.FieldUnavailable)
            : value.IconFileDataId == "0" ? new(value.ItemId, "0", ItemIconStatus.NoIcon)
            : outcomes[uint.Parse(value.IconFileDataId, CultureInfo.InvariantCulture)] with { ItemId = value.ItemId }).ToArray());
    }

    private async Task<(ContentReceipt Source, IconPngArtifact Png)?> ReusePngAsync(MetadataExpectation expected, CancellationToken token)
    {
        var source = await catalog.FindContentAsync(expected.EncodingKey, token);
        if (source is null || source.Projection is not null || source.VerifierRevision != SelectedFileAcquirer.VerifierRevision) return null;
        if (source.ContentKey != expected.ContentKey || source.EncodedBytes != N(expected.EncodedBytes) || source.DecodedBytes != N(expected.DecodedBytes))
            throw new DiscoveryException("ITEM_ICON_SOURCE_INVALID", "A retained full source differs from the selected icon mapping.");
        var png = await catalog.FindIconPngAsync(IconArtifacts.ComputeKey(source), token);
        if (png is null) return null;
        try
        {
            await using var probe = await artifacts.OpenVerifiedAsync(png, resources, null, token);
            return (source, png);
        }
        catch (DiscoveryException error) when (error.Code == "LOCAL_OBJECT_MISSING") { return null; }
    }

    public async Task<IItemIconReadLease> OpenAsync(ItemIconReadRequest request, CancellationToken token)
    {
        ItemIconsService.Validate(request); token.ThrowIfCancellationRequested(); long id; TaskCompletionSource retired;
        lock (gate)
        {
            RequireOpen(); if (openingReads.Count + reads.Count >= 4) throw Busy();
            id = ++nextRead; openingReads.Add(id, retired = new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        Stream? stream = null;
        try
        {
            var ct = lifetime.Token;
            var authorized = await items.AuthorizeIconsAsync(request.Context, request.ArtifactId, [request.ItemId], ct);
            var item = authorized.Items[0];
            if (!item.Admitted || item.IconFileDataId is null or "0") throw Unknown();
            var opening = await catalog.ResolveOpeningAsync(ItemIconAdmissionPolicy.Workspace(request.Context), ct);
            var saved = await catalog.FindItemIconAsync(opening, authorized.Snapshot, uint.Parse(item.IconFileDataId, CultureInfo.InvariantCulture), ct);
            if (saved is null || saved.Artifact.ArtifactId != request.ImageArtifactId) throw Unknown();
            stream = await artifacts.OpenVerifiedAsync(saved.Artifact, resources, null, ct);
            ct.ThrowIfCancellationRequested();
            var lease = new ReadLease(stream, saved.Artifact, Release);
            lock (gate) { RequireOpen(); reads.Add(lease); stream = null; }
            return lease;
        }
        finally
        {
            try { if (stream is not null) await stream.DisposeAsync(); }
            finally { lock (gate) { openingReads.Remove(id); retired.TrySetResult(); } }
        }
    }
    private void Release(ReadLease lease) { lock (gate) reads.Remove(lease); }
    private sealed class ReadLease(Stream stream, IconPngArtifact artifact, Action<ReadLease> release) : IItemIconReadLease
    {
        private int disposed;
        public Stream Content => stream;
        public string ArtifactId => artifact.ArtifactId;
        public string Sha256 => artifact.Sha256;
        public long ByteLength => artifact.ByteLength;
        public int Width => artifact.Proof.Width;
        public int Height => artifact.Proof.Height;
        public async ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref disposed, 1) == 0) try { await stream.DisposeAsync(); } finally { release(this); } }
    }
    private static ItemIconOutcome Ready(uint id, IconPngArtifact png) => new("", N(id), ItemIconStatus.Ready,
        new(png.ArtifactId, png.Proof.Width, png.Proof.Height, N(png.ByteLength)));
    internal static ItemIconOutcome Failure(uint id, DiscoveryException error) => new("", N(id), Classify(error.Code), null,
        new(error.Code, SafeMessage(Classify(error.Code)), error.Retryable));
    internal static ItemIconStatus Classify(string code) => code switch
    {
        "BLTE_ENCRYPTED" => ItemIconStatus.Protected,
        "ASSET_LIMIT" or "ASSET_SOURCE_UNSUPPORTED" or "BLP2_UNSUPPORTED" or "BLP_UNSUPPORTED" => ItemIconStatus.Unsupported,
        _ when code.Contains("UNSUPPORTED", StringComparison.Ordinal) || code.EndsWith("_LIMIT", StringComparison.Ordinal) => ItemIconStatus.Unsupported,
        _ when code.Contains("INTEGRITY", StringComparison.Ordinal) || code.Contains("CORRUPT", StringComparison.Ordinal) || code.Contains("CONFLICT", StringComparison.Ordinal) ||
            code.EndsWith("_INVALID", StringComparison.Ordinal) || code.StartsWith("BLP2_", StringComparison.Ordinal) ||
            code.StartsWith("BLTE_", StringComparison.Ordinal) => ItemIconStatus.Corrupt,
        _ => ItemIconStatus.Unavailable
    };
    private static string SafeMessage(ItemIconStatus status) => status switch
    {
        ItemIconStatus.Protected => "This optional icon requires protected source content.",
        ItemIconStatus.Unsupported => "This optional icon exceeds the supported format or resource limits.",
        ItemIconStatus.Corrupt => "This optional icon failed source or image integrity validation.",
        _ => "This optional icon source is currently unavailable."
    };
    private static bool Stale(DiscoveryException error) => error.Code.StartsWith("ITEMS_", StringComparison.Ordinal) || error.Code == "ENGINE_STOPPING";
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Unknown() => new("ITEM_ICON_UNKNOWN", "This exact current item does not authorize the requested image artifact.");
    private static DiscoveryException Busy() => new("ITEM_ICONS_BUSY", "The bounded icon preparation or read capacity is occupied.", true);
    private void RequireOpen() { if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true); }
    public ValueTask DisposeAsync()
    { lock (gate) { closed = true; return new(disposal ??= DisposeCoreAsync()); } }
    private async Task DisposeCoreAsync()
    {
        try { await shutdown.CancelAsync(); }
        catch (AggregateException) { /* Still retire all accepted operations and leases. */ }
        Task[] retiring;
        lock (gate) retiring = openingReads.Values.Select(value => value.Task).Concat(preparation is null ? [] : new[] { preparation.Task }).ToArray();
        await Task.WhenAll(retiring);
        ReadLease[] held; lock (gate) held = reads.ToArray();
        foreach (var read in held) await read.DisposeAsync();
        if (session is not null) await session.DisposeAsync();
        shutdown.Dispose();
    }
}
