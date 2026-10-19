using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>A lightweight exact-source feature handle. Idle handles pin proofs, never a worker.
/// Disposing one handle withdraws only its subscriptions and preserves all other consumers.</summary>
public sealed class AssetResolutionSession : IAsyncDisposable
{
    public const string VariantPolicy = "all-root-variants-consistent-asset-v1";
    public const string EncodingSelectionPolicy = "lexicographic-complete-encoding-v1";
    public const int MaxCachedIds = 1024;
    public const int MaxCachedMemberships = 32_768;
    public const long MaxCacheBytes = 16L << 20;
    private readonly AssetResolutionRegistry registry;
    private readonly AssetResolutionRegistry.Feature feature;
    private readonly bool ownsRegistry;

    internal AssetResolutionSession(AssetResolutionRegistry registry, AssetResolutionRegistry.Feature feature,
        EncodedSourceContext sourceContext)
    { this.registry = registry; this.feature = feature; SourceContext = sourceContext; }

    // Compatibility adapter for authored direct-source tests. Production construction belongs
    // exclusively to AssetResolutionSessions and its one shared registry.
    internal AssetResolutionSession(EncodingLookupLease encoding, FileStream root,
        AssetSourceAssociation association, EncodedSourceContext sourceContext, AcquisitionResources resources,
        CancellationToken ownerShutdown, Action release)
    {
        registry = new(ownerShutdown); ownsRegistry = true;
        feature = registry.Attach(new(encoding, root, association, sourceContext, resources, ownerShutdown, release));
        SourceContext = sourceContext;
    }
    public EncodedSourceContext SourceContext { get; }
    public Task<AssetResolutionBatch> ResolveAsync(IReadOnlyCollection<uint> requested,
        Func<string, Task> progress, CancellationToken token = default) => registry.ResolveAsync(feature, requested, progress, token);
    public async ValueTask DisposeAsync()
    {
        await registry.DisposeFeatureAsync(feature);
        if (ownsRegistry) await registry.DisposeAsync();
    }
}
