using System.Diagnostics;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>One registry-owned exact-source lease and cache. The global ROOT turn serializes
/// scans/EN lookups; cache-only reads use the cache gate and never acquire traversal capacity.</summary>
internal sealed class AssetResolutionSource : IAsyncDisposable
{
    public const string VariantPolicy = AssetResolutionSession.VariantPolicy;
    public const string EncodingSelectionPolicy = AssetResolutionSession.EncodingSelectionPolicy;
    public const int MaxCachedIds = AssetResolutionSession.MaxCachedIds;
    public const int MaxCachedMemberships = AssetResolutionSession.MaxCachedMemberships;
    public const long MaxCacheBytes = AssetResolutionSession.MaxCacheBytes;
    private const long ResultCharge = 1024, MembershipCharge = 512, MappingCharge = 512;
    private readonly EncodingLookupLease encoding;
    private readonly FileStream root;
    private readonly AssetSourceAssociation association;
    private readonly AcquisitionResources resources;
    private readonly Action release;
    private readonly CancellationTokenSource shutdown;
    private readonly object gate = new();
    private readonly Dictionary<uint, CachedResult> cache = [];
    private readonly LinkedList<uint> recency = [];
    private readonly Dictionary<string, MetadataExpectation?> mappings = new(StringComparer.Ordinal);
    private readonly Queue<string> mappingOrder = [];
    private long cacheBytes;
    private int cachedMemberships;
    private bool disposed;
    private Task? disposal;
    private sealed record CachedResult(AssetResolutionResult Result, LinkedListNode<uint> Node, long Charge);

    internal AssetResolutionSource(EncodingLookupLease encoding, FileStream root,
        AssetSourceAssociation association, EncodedSourceContext sourceContext, AcquisitionResources resources,
        CancellationToken ownerShutdown, Action release)
    {
        this.encoding = encoding; this.root = root; this.association = association;
        this.resources = resources; this.release = release;
        shutdown = CancellationTokenSource.CreateLinkedTokenSource(ownerShutdown);
        SourceContext = sourceContext;
    }

    public EncodedSourceContext SourceContext { get; }

    internal bool TryCached(uint[] ids, CancellationToken token, out AssetResolutionBatch? result)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (ids.Any(id => !cache.ContainsKey(id))) { result = null; return false; }
            var results = ids.Select(id => cache[id].Result).ToArray();
            foreach (var item in results) CacheResult(item);
            result = new(Array.AsReadOnly(results), false); return true;
        }
    }

    internal Task<AssetResolutionBatch> ResolveAsync(uint[] ids, Func<string, Task> progress, CancellationToken token) =>
        ResolveCoreAsync(ids, progress, token);

    private async Task<AssetResolutionBatch> ResolveCoreAsync(uint[] ids, Func<string, Task> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint[] missing;
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); missing = ids.Where(id => !cache.ContainsKey(id)).ToArray(); }
        var fresh = new Dictionary<uint, AssetResolutionResult>();
        var freshMappings = new Dictionary<string, MetadataExpectation?>(StringComparer.Ordinal);
        async Task<MetadataExpectation?> ExpectAsync(string key)
        {
            if (freshMappings.TryGetValue(key, out var expected) || mappings.TryGetValue(key, out expected)) return expected;
            try { expected = await SelectedFileAcquirer.ResolveAsync(encoding.Lookup, "asset", key, token); }
            catch (DiscoveryException failure) when (failure.Code == "CONTENT_UNRESOLVED") { expected = null; }
            freshMappings.Add(key, expected); return expected;
        }
        if (missing.Length > 0)
        {
            await progress("Finding requested asset memberships");
            RootBatchLookupResult batch;
            using (await resources.EnterCpuAsync(null, token))
            {
                token.ThrowIfCancellationRequested(); root.Position = 0;
                var lastProgress = Stopwatch.GetTimestamp();
                batch = await RootBatchLookup.ReadAsync(root, missing, new(),
                    async (done, total, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (done == 0 || done == total || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 100)
                        {
                            await progress(InventoryProgress.Format("Finding asset FileDataIDs", done, total, "entries"));
                            lastProgress = Stopwatch.GetTimestamp();
                        }
                    }, token);
                foreach (var target in batch.Targets)
                {
                    token.ThrowIfCancellationRequested();
                    var source = association with { Memberships = target.Memberships };
                    if (target.Memberships.Count == 0)
                    { fresh.Add(target.FileDataId, new(target.FileDataId, AssetResolutionStatus.MissingMembership, null, source)); continue; }
                    var keys = target.Memberships.Select(match => match.Entry.ContentKey).Distinct(StringComparer.Ordinal).ToArray();
                    if (keys.Length != 1)
                    {
                        // Status stays ambiguous. A D095 standard-resolution choice is attached for geometry textures to opt into.
                        var choice = RootTextureVariants.ChooseStandard(target.Memberships);
                        var variant = choice is null ? null : new AssetTextureVariant(choice, await ExpectAsync(choice.ContentKey));
                        fresh.Add(target.FileDataId, new(target.FileDataId, AssetResolutionStatus.AmbiguousVariants, null, source, variant)); continue;
                    }
                    var expected = await ExpectAsync(keys[0]);
                    fresh.Add(target.FileDataId, new(target.FileDataId,
                        expected is null ? AssetResolutionStatus.EncodingUnresolved : AssetResolutionStatus.Ready, expected, source));
                }
            }
        }
        token.ThrowIfCancellationRequested();
        AssetResolutionResult[] results;
        lock (gate) results = ids.Select(id => fresh.GetValueOrDefault(id) ?? cache[id].Result).ToArray();
        // No await/cancellation callback/observer runs within this bounded cache commit. All new
        // positive and negative outcomes first passed complete EOF and all EN lookups above.
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (key, value) in freshMappings) CacheMapping(key, value);
            foreach (var result in results) CacheResult(result);
        }
        return new(Array.AsReadOnly(results), missing.Length > 0);
    }

    private void CacheMapping(string key, MetadataExpectation? expected)
    {
        if (mappings.ContainsKey(key)) return;
        while (mappings.Count >= MaxCachedIds || cacheBytes + MappingCharge > MaxCacheBytes)
        {
            if (mappingOrder.TryDequeue(out var oldest)) { mappings.Remove(oldest); cacheBytes -= MappingCharge; }
            else if (recency.First is { } first) Evict(first.Value);
            else return;
        }
        mappings.Add(key, expected); mappingOrder.Enqueue(key); cacheBytes += MappingCharge;
    }

    private void CacheResult(AssetResolutionResult result)
    {
        if (cache.TryGetValue(result.FileDataId, out var existing))
        { recency.Remove(existing.Node); recency.AddLast(existing.Node); return; }
        var memberships = result.Association.Memberships.Count;
        var charge = checked(ResultCharge + MembershipCharge * memberships);
        if (charge > MaxCacheBytes) return;
        while (cache.Count >= MaxCachedIds || cachedMemberships + memberships > MaxCachedMemberships || cacheBytes + charge > MaxCacheBytes)
        {
            if (recency.First is { } first) Evict(first.Value);
            else if (mappingOrder.TryDequeue(out var oldest)) { mappings.Remove(oldest); cacheBytes -= MappingCharge; }
            else return;
        }
        var node = recency.AddLast(result.FileDataId);
        cache.Add(result.FileDataId, new(result, node, charge));
        cacheBytes += charge; cachedMemberships += memberships;
    }

    private void Evict(uint id)
    {
        var item = cache[id]; cache.Remove(id); recency.Remove(item.Node);
        cacheBytes -= item.Charge; cachedMemberships -= item.Result.Association.Memberships.Count;
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            disposed = true;
            return new(disposal ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        await AssetCancellation.RequestAsync(shutdown);
        try { await root.DisposeAsync(); }
        finally
        {
            try { await encoding.DisposeAsync(); }
            finally
            {
                cache.Clear(); recency.Clear(); mappings.Clear(); mappingOrder.Clear();
                shutdown.Dispose(); release();
            }
        }
    }
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
}
