using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Prepare an exact DL scope without materializing the unrelated research catalog.</summary>
public sealed class DownloadWorkspacePreparer : IWorkspacePreparer, IDisposable
{
    public const string Profile = "home-download-v1";
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly ConfigurationWorkspacePreparer configurations;
    private readonly MetadataAcquirer acquisition;
    private readonly ResolverArtifacts resolvers;
    private readonly DownloadLedger ledger;
    private readonly AcquisitionResources? resources;
    private readonly SemaphoreSlim operation = new(1, 1);
    public DownloadWorkspacePreparer(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
        ISharedAcquisition? shared = null, AcquisitionResources? resources = null)
    {
        this.catalog = catalog; this.objects = objects; this.resources = resources;
        configurations = new(catalog, objects, client, resources: resources);
        acquisition = new(client, objects, shared: shared, resources: resources);
        resolvers = new(catalog, objects, acquisition, resources);
        ledger = resources is null ? new(objects) : new(objects, resources, null);
    }
    public Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token) =>
        PrepareWithProgressAsync(request, value => progress(value.Stage), token);
    public async Task<WorkspaceSnapshot> PrepareWithProgressAsync(WorkspaceRequest request,
        Func<WorkspacePreparationProgress, Task> progress, CancellationToken token)
    {
        if (!await operation.WaitAsync(0, token)) throw new DiscoveryException("OPEN_BUSY", "Another build is still opening.", true);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(20)); var ct = lifetime.Token;
        var index = 1; int? total = null;
        var description = "Verify the small build and CDN configurations for this exact build.";
        async Task Report(string label)
        {
            ct.ThrowIfCancellationRequested();
            await progress(new(label, new(index, total, description)));
            ct.ThrowIfCancellationRequested();
        }
        async Task Stage(int ordinal, string label, string explanation)
        { index = ordinal; description = explanation; await Report(label); }
        try
        {
            await Report("Checking build configurations");
            var inputs = await configurations.PrepareInputsAsync(request, Report, ct);
            var sourceContext = new EncodedSourceContext(request.ProductId, request.BuildIdentity, request.SourceRegion,
                inputs.Configurations.Single(receipt => receipt.Role == "cdnConfig").Sha256, inputs.Context.Reference);
            var declaration = DownloadDeclarations.ReadDownload(inputs.BuildConfiguration);
            if (declaration is null)
            {
                total = 2;
                await Report("Verified build and CDN configurations");
                await Stage(2, "Opening overview with size unavailable",
                    "This build has no declared download manifest; research metadata remains independent of its size.");
                return await catalog.CommitUnavailableDownloadWorkspaceAsync(inputs.Context, inputs.Configurations,
                    new("DOWNLOAD_MANIFEST_ABSENT", "This build does not declare a download manifest. Published download size is unavailable."), ct);
            }
            var direct = declaration.DirectExpectation;
            total = direct is null ? 7 : 5;
            await Report("Verified build and CDN configurations");
            var metadata = new List<MetadataReceipt>(2);
            MetadataExpectation expected;
            if (direct is null)
            {
                await Stage(2, "Preparing encoding metadata lookup",
                    "Verify encoding metadata to locate the download manifest for this exact build.");
                var encoding = DownloadDeclarations.ReadEncoding(inputs.BuildConfiguration);
                await using (var resolver = await resolvers.OpenEncodingAsync(inputs.Route, encoding, Report, ct, sourceContext))
                {
                    metadata.Add(resolver.Receipt);
                    await Stage(3, "Resolving download manifest source",
                        "Look up only the declared manifest content and its exact encoded size in the verified encoding pages.");
                    var content = await resolver.Lookup.FindContentAsync(declaration.ContentKey, ct) ??
                        throw new DiscoveryException("METADATA_UNRESOLVED", "The declared download manifest content is absent from encoding metadata.");
                    if (declaration.DecodedBytes is { } declaredDecoded && declaredDecoded != content.DecodedBytes)
                        throw new DiscoveryException("METADATA_SIZE_CONFLICT", "Download metadata decoded lengths disagree.");
                    EncodingEncodedEntry? selected = null;
                    foreach (var key in content.EncodingKeys.Order(StringComparer.Ordinal))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (declaration.EncodingKey is { } required && key != required) continue;
                        selected = await resolver.Lookup.FindEncodingAsync(key, ct);
                        if (selected is not null) break;
                    }
                    if (selected is null) throw new DiscoveryException("METADATA_UNRESOLVED", "The download manifest has no complete encoded representation.");
                    if (declaration.EncodedBytes is { } declaredEncoded && declaredEncoded != selected.EncodedBytes)
                        throw new DiscoveryException("METADATA_SIZE_CONFLICT", "Download metadata encoded lengths disagree.");
                    expected = new("download", content.ContentKey, selected.EncodingKey, selected.EncodedBytes, content.DecodedBytes);
                }
            }
            else expected = direct;
            var acquisitionStep = total == 5 ? 2 : 4;
            await Stage(acquisitionStep, "Preparing download manifest",
                "Download or reuse and verify the published manifest containing exact encoded object identities and sizes.");
            var saved = await catalog.FindMetadataAsync(expected.EncodingKey, ct);
            DownloadLedgerArtifact artifact;
            await using (var source = await acquisition.AcquireAsync(inputs.Route, expected, Report, ct, saved, sourceContext))
            {
                await catalog.RecordMetadataAsync(source.Receipt, ct);
                await Stage(acquisitionStep + 1, "Preparing compact download ledger",
                    "Verify or build the compact object list, counting each full encoded identity once and checking its size.");
                var previous = await catalog.FindDownloadArtifactAsync(source.Receipt, ct);
                if (previous is not null)
                {
                    await ledger.ValidateAsync(previous, ct, Report);
                    artifact = previous;
                }
                else
                {
                    await using var decoded = source.OpenDecoded();
                    try { artifact = await ledger.BuildAsync(decoded, source.Receipt, Report, ct); }
                    catch (DiscoveryException failure) when (failure.Code == "DL_UNSUPPORTED")
                    {
                        await Stage(acquisitionStep + 2, "Download size unavailable",
                            "The manifest is authentic, but its format is not supported yet. No size or reusable-byte total can be claimed.");
                        await Stage(acquisitionStep + 3, "Opening build overview",
                            "Open the overview with verified configurations and an explicit unavailable-size explanation.");
                        return await catalog.CommitUnavailableDownloadWorkspaceAsync(inputs.Context, inputs.Configurations,
                            new("DOWNLOAD_MANIFEST_UNSUPPORTED", "This build's download manifest format is not supported. Published download size is unavailable."), ct);
                    }
                }
                var receipt = source.Receipt with { ParserRevision = "download-v1" };
                await catalog.RecordMetadataAsync(receipt, ct);
                await catalog.RecordDownloadArtifactAsync(receipt, artifact, ct);
                metadata.Add(receipt);
            }
            await Stage(acquisitionStep + 2, "Checking reusable downloaded objects",
                "Match the manifest's exact identities against retained objects and verify matching bytes before crediting reuse.");
            var candidates = await catalog.ReadRetainedMetadataAsync(ct);
            var byKey = candidates.ToDictionary(row => row.Key, row => row, StringComparer.Ordinal);
            var lengths = byKey.ToDictionary(pair => pair.Key, pair => pair.Value.EncodedBytes, StringComparer.Ordinal);
            var matches = await ledger.AssessAsync(artifact, lengths, ct);
            var verified = new List<string>(matches.Count);
            long checkedDecoded = 0;
            foreach (var key in matches)
            {
                ct.ThrowIfCancellationRequested();
                var receipt = byKey[key];
                var decoded = receipt.DecodedBytes;
                checkedDecoded = checked(checkedDecoded + decoded);
                if (checkedDecoded > 2L * 1024 * 1024 * 1024)
                    throw new DiscoveryException("REUSE_ASSESSMENT_LIMIT", "Retained-byte verification exceeds this operation's work budget.");
                try
                {
                    await using var encoded = resources is null
                        ? await objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, ct)
                        : await objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, resources, null, ct);
                    using var cpu = resources is null ? null : await resources.EnterCpuAsync(null, ct);
                    if (receipt.Projection is { } retainedProjection)
                    {
                        var verifiedProjection = await BlteDecoder.VerifyAvailableAsync(encoded, key, receipt.ContentKey, receipt.EncodedBytes, decoded, ct);
                        var replay = new ContentProjection(retainedProjection.Policy, retainedProjection.IdentitySha256,
                            verifiedProjection.ReadableRanges.Select(range => new ContentReadableRange(NumberText(range.Offset), NumberText(range.ByteLength), range.Sha256)).ToArray(),
                            verifiedProjection.UnavailableRanges.Select(range => new ContentUnavailableRange(NumberText(range.Offset), NumberText(range.ByteLength), range.ChunkOrdinal, range.KeyName)).ToArray());
                        if (verifiedProjection.ContentKeyVerified || ContentProjectionIdentity.Compute(receipt.ContentKey, key, NumberText(decoded), replay) != retainedProjection.IdentitySha256)
                            throw new DiscoveryException("BLTE_PROJECTION_MISMATCH", "Retained encoded bytes do not reproduce their exact partial decoded proof.");
                    }
                    else await BlteDecoder.DecodeAsync(encoded, Stream.Null, key, receipt.ContentKey, receipt.EncodedBytes, decoded, ct);
                    verified.Add(key);
                }
                catch (DiscoveryException failure) when (failure.Code.StartsWith("LOCAL_OBJECT_", StringComparison.Ordinal) || failure.Code.StartsWith("BLTE_", StringComparison.Ordinal))
                { /* Missing or corrupt retained bytes receive no verified credit. Required DL proof was checked above. */ }
            }
            await Stage(acquisitionStep + 3, "Opening build overview",
                "Publish the verified download-manifest size and dated reuse assessment; research data is prepared when requested.");
            var overhead = metadata.Select(row => (row.Sha256, Bytes: Number(row.EncodedBytes)))
                .Concat(inputs.Configurations.Select(row => (row.Sha256, Bytes: (long)row.ByteLength)))
                .Distinct().Sum(row => row.Bytes);
            return await catalog.CommitDownloadWorkspaceAsync(inputs.Context, inputs.Configurations, metadata, artifact, verified, overhead, ct);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new DiscoveryException("OPEN_TIMEOUT", "Download-size preparation exceeded its bounded time budget.", true); }
        catch (IOException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Download-size storage could not complete this operation.", true); }
        catch (UnauthorizedAccessException) { throw new DiscoveryException("STORAGE_UNAVAILABLE", "Download-size storage could not be accessed.", true); }
        finally { operation.Release(); }
    }
    private static string NumberText(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static long Number(string value) => long.Parse(value, CultureInfo.InvariantCulture);
    public void Dispose() { configurations.Dispose(); operation.Dispose(); }
}
