using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Engine-owned bounded asset source registry. Feature handles share pinned source
/// proofs, complete native EN/ROOT read leases, cache and one fair global ROOT traversal owner.</summary>
public sealed class AssetResolutionSessions : IAsyncDisposable
{
    public const string RootArtifactRevision = "root-native-file-v1";
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly ConfigurationWorkspacePreparer configurations;
    private readonly HttpClient client;
    private readonly ISharedAcquisition shared;
    private readonly AssetResolutionRegistry registry = new();
    private readonly AcquisitionResources resources;
    private readonly object gate = new();

    private Task? disposal;

    public AssetResolutionSessions(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
        ISharedAcquisition shared, AcquisitionResources resources)
    {
        ArgumentNullException.ThrowIfNull(shared); ArgumentNullException.ThrowIfNull(resources);
        this.catalog = catalog; this.objects = objects; this.resources = resources;
        configurations = new(catalog, objects, client, resources: resources);
        this.client = client; this.shared = shared;
    }

    public Task<AssetResolutionSession> OpenAsync(WorkspaceRequest request, string locale,
        Func<string, Task> progress, CancellationToken token = default)
    {
        WorkspaceService.Validate(request); ArgumentNullException.ThrowIfNull(progress);
        if (locale != "enUS") throw Error("ASSET_LOCALE_UNSUPPORTED", "Asset source sessions currently require the explicit enUS research locale.");
        token.ThrowIfCancellationRequested();
        return registry.OpenAsync(async ct =>
        {
            var pinned = await catalog.ResolveOpeningAsync(request, ct);
            return new AssetSourceContextKey(request, locale, pinned.Reference, pinned.Build.BuildConfig, pinned.Build.CdnConfig);
        }, OpenCoreAsync, progress, token);
    }

    private async Task<AssetResolutionSource> OpenCoreAsync(AssetSourceContextKey key,
        Func<string, Task> progress, CancellationToken ct)
    {
        var request = key.Request; var locale = key.Locale;
        // Two context-owned native lookup admissions use the same physical acquisition and budgets.
        var metadata = new MetadataAcquirer(client, objects, shared: shared, resources: resources);
        var resolvers = new ResolverArtifacts(catalog, objects, metadata, resources);
        EncodingLookupLease? encoding = null; FileStream? root = null;
        var transferred = false;
        try
        {
            var inputs = await configurations.PrepareInputsAsync(request, progress, ct);
            key.RequirePrepared(inputs.Context);
            var routes = SelectedFileAcquirer.Routes(inputs.Context, inputs.Route);
            var source = new EncodedSourceContext(request.ProductId, request.BuildIdentity, request.SourceRegion,
                inputs.Configurations.Single(value => value.Role == "cdnConfig").Sha256, inputs.Context.Reference);
            var encodingExpected = DownloadDeclarations.ReadEncoding(inputs.BuildConfiguration);
            await progress("Preparing shared asset encoding lookup");
            foreach (var route in routes)
            {
                try { encoding = await resolvers.OpenEncodingAsync(route, encodingExpected, progress, ct, source); break; }
                catch (DiscoveryException failure) when (route != routes[^1] && SelectedFileAcquirer.RetryRoute(failure))
                { await progress("Trying another advertised official metadata route"); }
            }
            if (encoding is null) throw Error("METADATA_UNAVAILABLE", "No admitted route supplied asset encoding metadata.", true);
            MetadataExpectation rootExpected;
            using (await resources.EnterCpuAsync(null, ct))
                rootExpected = await SelectedFileAcquirer.ResolveAsync(encoding.Lookup, "root",
                    SelectedFileAcquirer.RootKey(inputs.BuildConfiguration), ct);
            if (rootExpected.DecodedBytes > VerifiedObjectStore.MaxObjectBytes)
                throw Error("ASSET_SOURCE_LIMIT", "Decoded ROOT exceeds the immutable resolver artifact limit.");
            MetadataReceipt rootSource;
            ResolverArtifact artifact;
            var retained = await catalog.FindMetadataAsync(rootExpected.EncodingKey, ct);
            VerifiedMetadata? acquired = null;
            try
            {
                foreach (var route in routes)
                {
                    try { acquired = await metadata.AcquireAsync(route, rootExpected, progress, ct, retained, source); break; }
                    catch (DiscoveryException failure) when (route != routes[^1] && SelectedFileAcquirer.RetryRoute(failure))
                    { await progress("Trying another advertised official metadata route"); }
                }
                if (acquired is null) throw Error("METADATA_UNAVAILABLE", "No admitted route supplied complete ROOT metadata.", true);
                await catalog.RecordMetadataAsync(acquired.Receipt, ct);
                rootSource = await catalog.FindMetadataAsync(rootExpected.EncodingKey, ct)
                    ?? throw Error("CATALOG_INTEGRITY", "Verified ROOT lost its original source association.");
                var saved = await catalog.FindResolverArtifactAsync(rootSource.Sha256, "root", RootArtifactRevision, ct);
                if (saved is null)
                {
                    // Publish verified decoded bytes before registering grammar readiness. A later
                    // grammar failure can leave unassociated immutable bytes, never a ready artifact.
                    string path;
                    await using (var decoded = acquired.OpenDecoded()) path = decoded.Name;
                    var sha = await objects.PublishFileAsync(path, rootExpected.DecodedBytes, resources, null, ct);
                    artifact = new(rootSource, RootArtifactRevision, sha, rootExpected.DecodedBytes);
                }
                else artifact = saved;
            }
            finally
            {
                if (acquired is not null)
                {
                    try { await acquired.DisposeAsync(); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { throw Error("ASSET_SOURCE_CLEANUP", "Asset metadata staging cleanup could not establish ownership retirement.", true); }
                }
            }

            await progress("Validating complete asset ROOT source");
            await registry.RootTurnAsync(key, async turnToken =>
            {
                root = await objects.OpenVerifiedAsync(artifact.DecodedSha256, artifact.DecodedBytes, resources, null, turnToken);
                using var cpu = await resources.EnterCpuAsync(null, turnToken);
                if (artifact.DecodedBytes != rootExpected.DecodedBytes ||
                    Convert.ToHexStringLower(await MD5.HashDataAsync(root, turnToken)) != rootExpected.ContentKey)
                    throw Error("RESOLVER_SOURCE_INTEGRITY", "Saved ROOT bytes differ from their exact verified decoded source.");
                root.Position = 0;
                _ = await RootInventoryReader.ReadAsync(root, new(), new SourceValidator(), turnToken);
                root.Position = 0; return true;
            }, ct);
            rootSource = rootSource with { ParserRevision = "root-v1" };
            await catalog.RecordResolverArtifactAsync(artifact with { Source = rootSource }, ct);
            var association = new AssetSourceAssociation(request, locale, inputs.Context.Reference,
                Array.AsReadOnly(inputs.Configurations.ToArray()), encoding.Receipt, rootSource,
                AssetResolutionSession.VariantPolicy, Array.Empty<RootTargetMatch>());
            ct.ThrowIfCancellationRequested();
            var result = new AssetResolutionSource(encoding, root!, association, source, resources, ct, () => { });
            transferred = true; return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Error("ASSET_SOURCE_CLEANUP", "Asset source I/O failed before cleanup could establish ownership retirement.", true); }
        finally
        {
            if (!transferred)
            {
                try
                {
                    try { if (root is not null) await root.DisposeAsync(); }
                    finally { if (encoding is not null) await encoding.DisposeAsync(); }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { throw Error("ASSET_SOURCE_CLEANUP", "Asset source cleanup could not establish ownership retirement.", true); }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { await registry.DisposeAsync(); }
        finally { configurations.Dispose(); }
    }
    private sealed class SourceValidator : IRootConsumer
    {
        public ValueTask EntryAsync(long block, long ordinal, RootEntry entry, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    }
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
}
