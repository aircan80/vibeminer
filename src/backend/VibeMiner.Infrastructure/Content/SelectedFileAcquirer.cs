using System.Globalization;
using System.Diagnostics;
using System.Text.RegularExpressions;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

public sealed class SelectedContentLease : IAsyncDisposable
{
    private readonly FileStream decoded;
    private readonly Stream exposed;
    private readonly Action release;
    private readonly object gate = new();
    private Task? disposal;
    internal SelectedContentLease(ContentReceipt receipt, SelectedFileAssociation association,
        FileStream decoded, Action release, Stream? exposed = null)
    { Receipt = receipt; Association = association; this.decoded = decoded; this.release = release; this.exposed = exposed ?? decoded; }
    public ContentReceipt Receipt { get; }
    public SelectedFileAssociation Association { get; }
    public Stream Decoded => exposed;
    public ValueTask DisposeAsync() { lock (gate) return new(disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        try
        {
            try { if (!ReferenceEquals(exposed, decoded)) await exposed.DisposeAsync(); }
            finally { await decoded.DisposeAsync(); }
        }
        finally { try { File.Delete(decoded.Name); } finally { release(); } }
    }
}

/// <summary>
/// One bounded selected-file pipeline: exact configurations, native EN, complete targeted ROOT
/// scan, explicit locale association, official source location, verified CAS and BLTE decode.
/// Unrelated research catalogues and whole-build transfers are not prerequisites.
/// </summary>
public sealed class SelectedFileAcquirer : IDisposable
{
    public const string VerifierRevision = "content-blte-v1";
    public const string ProjectionVerifierRevision = "content-blte-projection-v1";
    public const string ProjectionPolicy = "blte-clear-chunks-v1";
    public const string VariantPolicy = "enus-consistent-root-variants-v1";
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly ConfigurationWorkspacePreparer configurations;
    private readonly MetadataAcquirer metadata;
    private readonly ResolverArtifacts resolvers;
    private readonly OfficialSourceTransport transport;
    private readonly ArchivedContentLocator archives;
    private int active;
    private readonly ISharedAcquisition? shared;
    private readonly AcquisitionResources? resources;

    public SelectedFileAcquirer(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
        ISharedAcquisition? shared = null, AcquisitionResources? resources = null)
    {
        this.catalog = catalog; this.objects = objects; this.shared = shared; this.resources = resources;
        configurations = new(catalog, objects, client, resources: resources); metadata = new(client, objects, shared: shared, resources: resources);
        resolvers = new(catalog, objects, metadata, resources); transport = new(client, resources);
        archives = new(catalog, objects, transport, resources: resources);
    }

    public async Task<SelectedContentLease> AcquireAsync(WorkspaceRequest request, uint fileDataId,
        string locale, Func<string, Task> progress, CancellationToken token)
    {
        WorkspaceService.Validate(request); ArgumentNullException.ThrowIfNull(progress);
        if (locale != "enUS") throw Error("CONTENT_LOCALE_UNSUPPORTED", "Selected table loading currently requires explicit enUS research locale.");
        if (fileDataId == 0) throw Error("INVALID_REQUEST", "A nonzero selected FileDataID is required.");
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw Error("CONTENT_BUSY", "Another selected file is still being prepared or read.", true);
        var transferred = false;
        IDisposable? staging = null;
        FileStream? decoded = null;
        Stream? exposed = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(10)); var ct = lifetime.Token;
        try
        {
            await progress("Checking exact build source");
            var inputs = await configurations.PrepareInputsAsync(request, progress, ct);
            var routes = Routes(inputs.Context, inputs.Route);
            var sourceContext = new EncodedSourceContext(request.ProductId, request.BuildIdentity, request.SourceRegion,
                inputs.Configurations.Single(value => value.Role == "cdnConfig").Sha256, inputs.Context.Reference);
            var encodingExpected = DownloadDeclarations.ReadEncoding(inputs.BuildConfiguration);
            var rootKey = RootKey(inputs.BuildConfiguration);
            MetadataReceipt encodingSource, rootSource;
            IReadOnlyList<RootTargetMatch> selected;
            MetadataExpectation expected;
            await progress("Preparing native encoding lookup");
            await using (var lookup = await EncodingAsync(routes, encodingExpected, progress, ct, sourceContext))
            {
                encodingSource = lookup.Receipt;
                var rootExpected = await ResolveAsync(lookup.Lookup, "root", rootKey, ct);
                var retainedRoot = await catalog.FindMetadataAsync(rootExpected.EncodingKey, ct);
                await using (var root = await MetadataAsync(routes, rootExpected, retainedRoot, progress, ct, sourceContext))
                {
                    await progress("Finding selected FileDataID and locale in root metadata");
                    await using var source = root.OpenDecoded();
                    var lastProgress = Stopwatch.GetTimestamp();
                    var matches = await RootTargetLookup.ReadAsync(source, [fileDataId], new(),
                        async (done, total, cancellation) =>
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (done == 0 || done == total || Stopwatch.GetElapsedTime(lastProgress).TotalMilliseconds >= 100)
                            {
                                await progress(InventoryProgress.Format("Finding selected FileDataID", done, total, "entries"));
                                lastProgress = Stopwatch.GetTimestamp();
                            }
                        }, ct);
                    rootSource = root.Receipt with { ParserRevision = "root-v1" };
                    await catalog.RecordMetadataAsync(rootSource, ct);
                    selected = matches.Where(match => (match.Entry.LocaleFlags & 2) != 0).ToArray();
                    if (selected.Count == 0)
                        throw Error("CONTENT_VARIANT_MISSING", "The selected FileDataID has no published enUS-eligible membership in this build.");
                    if (selected.Count > 256)
                        throw Error("CONTENT_VARIANT_LIMIT", "Selected FileDataID memberships exceed the retained association bound.");
                    var keys = selected.Select(match => match.Entry.ContentKey).Distinct(StringComparer.Ordinal).ToArray();
                    if (keys.Length != 1)
                        throw Error("CONTENT_VARIANT_AMBIGUOUS", "The selected FileDataID has competing enUS-eligible content variants.");
                    expected = await ResolveAsync(lookup.Lookup, "selected", keys[0], ct);
                }
            }
            if (expected.DecodedBytes > 256L << 20)
                throw Error("CONTENT_LIMIT", "The selected table exceeds the admitted decoded byte limit.");
            var association = new SelectedFileAssociation(N(fileDataId), locale, VariantPolicy, encodingSource,
                rootSource, Array.AsReadOnly(selected.Select(match => new SelectedRootMembership(
                    N(match.BlockOrdinal), N(match.EntryOrdinal), match.Entry.ContentKey,
                    N(match.Entry.LocaleFlags), N(match.Entry.ContentFlags), N(match.Entry.RawContentFlags.First),
                    N(match.Entry.RawContentFlags.Second), N(match.Entry.RawContentFlags.Third),
                    match.Entry.NameHash is { } hash ? hash.ToString(CultureInfo.InvariantCulture) : null)).ToArray()));
            var previous = await catalog.FindContentAsync(expected.EncodingKey, ct);
            ContentReceipt receipt;
            EncodedReceipt? physical = null;
            if (shared is not null)
                physical = await shared.AcquireAsync(new(expected.EncodingKey, expected.EncodedBytes), sourceContext,
                    AcquisitionPriority.Interactive, state => progress(state.Completed is { } done && state.Total is { } total
                        ? InventoryProgress.Format(state.Stage, done, total, state.Unit) : state.Stage), ct);
            if (resources is not null)
                staging = await resources.ReserveStagingAsync(expected.DecodedBytes +
                    (physical is null && previous is null ? expected.EncodedBytes : 0), null, ct);
            decoded = await objects.CreateStagingFileAsync(ct);
            if (previous is not null)
            {
                ValidatePrevious(previous, expected);
                await progress("Verifying retained selected file");
                await using var encoded = resources is null
                    ? await objects.OpenVerifiedAsync(physical?.Sha256 ?? previous.Sha256, expected.EncodedBytes, ct)
                    : await objects.OpenVerifiedAsync(physical?.Sha256 ?? previous.Sha256, expected.EncodedBytes, resources, null, ct);
                using var cpu = resources is null ? null : await resources.EnterCpuAsync(null, ct);
                if (previous.Projection is { } savedProjection)
                {
                    var actual = await DecodeProjectionAsync(encoded, decoded, expected, ct);
                    if (actual.IdentitySha256 != savedProjection.IdentitySha256)
                        throw Error("CONTENT_PROJECTION_INTEGRITY", "Retained available chunks differ from their original authenticated projection.");
                }
                else
                    await BlteDecoder.DecodeAsync(encoded, decoded, expected.EncodingKey, expected.ContentKey,
                        expected.EncodedBytes, expected.DecodedBytes, ct);
                receipt = previous with { VerifiedAt = DateTimeOffset.UtcNow.ToString("O") };
            }
            else if (physical is not null)
            {
                ContentProjection? sharedProjection = null;
                await using var encoded = resources is null
                    ? await objects.OpenVerifiedAsync(physical.Sha256, expected.EncodedBytes, ct)
                    : await objects.OpenVerifiedAsync(physical.Sha256, expected.EncodedBytes, resources, null, ct);
                using (resources is null ? null : await resources.EnterCpuAsync(null, ct))
                {
                    try
                    {
                        await BlteDecoder.DecodeAsync(encoded, decoded, expected.EncodingKey, expected.ContentKey,
                            expected.EncodedBytes, expected.DecodedBytes, ct);
                    }
                    catch (DiscoveryException failure) when (failure.Code == "BLTE_ENCRYPTED")
                    {
                        encoded.Position = 0; decoded.SetLength(0); decoded.Position = 0;
                        sharedProjection = await DecodeProjectionAsync(encoded, decoded, expected, ct);
                    }
                }
                var now = DateTimeOffset.UtcNow.ToString("O");
                receipt = new(expected.ContentKey, expected.EncodingKey, physical.Sha256,
                    N(expected.EncodedBytes), N(expected.DecodedBytes), physical.SourceUrl, physical.RetrievedAt, now,
                    sharedProjection is null ? VerifierRevision : ProjectionVerifierRevision, physical.Location, sharedProjection);
                await catalog.RecordContentAsync(receipt, ct);
            }
            else
            {
                var cdnReceipt = inputs.Configurations.Single(value => value.Role == "cdnConfig");
                var cdnBytes = await objects.ReadAsync(cdnReceipt.Sha256, cdnReceipt.ByteLength, ct);
                var cdn = TactConfigurationParser.Parse(cdnBytes, ct);
                receipt = await AcquireNewAsync(routes, cdn, cdnReceipt.Sha256, expected, decoded, progress, ct);
            }
            await decoded.FlushAsync(ct); decoded.Position = 0;
            ct.ThrowIfCancellationRequested();
            if (receipt.Projection is { } projection)
                exposed = new BlteProjectionStream(decoded,
                    projection.UnavailableRanges.Select(range => new BlteUnavailableRange(Number(range.Offset),
                        Number(range.ByteLength), range.ChunkOrdinal, range.KeyName)).ToArray(), leaveOpen: true,
                    readableRanges: projection.ReadableRanges.Select(range => new BlteReadableRange(Number(range.Offset),
                        Number(range.ByteLength), range.Sha256)).ToArray());
            var lease = new SelectedContentLease(receipt, association, decoded, () => { staging?.Dispose(); Volatile.Write(ref active, 0); }, exposed);
            transferred = true; return lease;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Error("CONTENT_TIMEOUT", "Selected-file preparation exceeded its bounded time budget.", true); }
        finally
        {
            if (!transferred)
            {
                try
                {
                    if (decoded is not null)
                    {
                        var path = decoded.Name;
                        try
                        {
                            try { if (exposed is not null) await exposed.DisposeAsync(); }
                            finally { await decoded.DisposeAsync(); }
                        }
                        finally { File.Delete(path); }
                    }
                }
                finally { staging?.Dispose(); Volatile.Write(ref active, 0); }
            }
        }
    }

    private async Task<ContentReceipt> AcquireNewAsync(IReadOnlyList<Uri> routes, TactConfiguration cdn, string cdnSha,
        MetadataExpectation expected, Stream decoded, Func<string, Task> progress, CancellationToken token)
    {
        string? path = null;
        try
        {
            ContentLocation? location = null;
            var url = ArchivedContentLocator.Url(routes[0], expected.EncodingKey);
            string retrievedAt;
            ContentProjection? projection = null;
            await using (var encoded = await objects.CreateStagingFileAsync(token))
            {
                path = encoded.Name;
                await progress("Downloading selected file");
                for (var index = 0; index < routes.Count; index++)
                {
                    var route = routes[index]; url = ArchivedContentLocator.Url(route, expected.EncodingKey);
                    encoded.SetLength(0); encoded.Position = 0;
                    try
                    {
                        try
                        {
                            await transport.DownloadLooseAsync(url, encoded, expected.EncodedBytes, "selected file", progress, token);
                            location = new("loose", "0", N(expected.EncodedBytes), null);
                        }
                        catch (DiscoveryException failure) when (failure.Code == "CONTENT_LOOSE_MISSING")
                        {
                            encoded.SetLength(0); encoded.Position = 0;
                            var located = await archives.LocateAsync(route, cdn, cdnSha, expected.EncodingKey,
                                expected.EncodedBytes, progress, token);
                            url = ArchivedContentLocator.Url(route, located.ArchiveKey);
                            var sourceBytes = await transport.DownloadRangeAsync(url, encoded, null, located.Offset,
                                located.EncodedBytes, progress, token);
                            location = new("archive", N(located.Offset), N(sourceBytes), located.ArchiveKey);
                        }
                        break;
                    }
                    catch (DiscoveryException failure) when (index + 1 < routes.Count && RetryRoute(failure))
                    { await progress("Trying another advertised official CDN route"); }
                }
                if (location is null) throw Error("CONTENT_UNAVAILABLE", "No admitted official route supplied the selected file.", true);
                retrievedAt = DateTimeOffset.UtcNow.ToString("O");
                await encoded.FlushAsync(token); encoded.Position = 0;
                await progress("Verifying and decoding selected file");
                try
                {
                    await BlteDecoder.DecodeAsync(encoded, decoded, expected.EncodingKey, expected.ContentKey,
                        expected.EncodedBytes, expected.DecodedBytes, token);
                }
                catch (DiscoveryException failure) when (failure.Code == "BLTE_ENCRYPTED")
                {
                    await progress("Verifying available chunks and encrypted ranges");
                    token.ThrowIfCancellationRequested();
                    encoded.Position = 0; decoded.SetLength(0); decoded.Position = 0;
                    projection = await DecodeProjectionAsync(encoded, decoded, expected, token);
                }
            }
            var sha = resources is null
                ? await objects.PublishFileAsync(path, expected.EncodedBytes, token)
                : await objects.PublishFileAsync(path, expected.EncodedBytes, resources, null, token);
            path = null;
            var now = DateTimeOffset.UtcNow.ToString("O");
            var receipt = new ContentReceipt(expected.ContentKey, expected.EncodingKey, sha,
                N(expected.EncodedBytes), N(expected.DecodedBytes), url.AbsoluteUri, retrievedAt, now,
                projection is null ? VerifierRevision : ProjectionVerifierRevision, location, projection);
            await catalog.RecordContentAsync(receipt, token);
            return receipt;
        }
        finally { if (path is not null && File.Exists(path)) File.Delete(path); }
    }

    private static async Task<ContentProjection> DecodeProjectionAsync(Stream encoded, Stream decoded,
        MetadataExpectation expected, CancellationToken token)
    {
        var actual = await BlteDecoder.DecodeAvailableAsync(encoded, decoded, expected.EncodingKey,
            expected.ContentKey, expected.EncodedBytes, expected.DecodedBytes, token);
        if (actual.ContentKeyVerified || actual.UnavailableRanges.Count == 0 ||
            actual.ReadableRanges.Count + actual.UnavailableRanges.Count > 128)
            throw Error("CONTENT_PROJECTION_INVALID", "Available-chunk decoding did not establish a supported partial projection.");
        var projection = new ContentProjection(ProjectionPolicy, "",
            Array.AsReadOnly(actual.ReadableRanges.Select(range => new ContentReadableRange(N(range.Offset),
                N(range.ByteLength), range.Sha256)).ToArray()),
            Array.AsReadOnly(actual.UnavailableRanges.Select(range => new ContentUnavailableRange(N(range.Offset),
                N(range.ByteLength), range.ChunkOrdinal, range.KeyName)).ToArray()));
        return projection with { IdentitySha256 = ContentProjectionIdentity.Compute(expected.ContentKey,
            expected.EncodingKey, N(expected.DecodedBytes), projection) };
    }

    private async Task<EncodingLookupLease> EncodingAsync(IReadOnlyList<Uri> routes, MetadataExpectation expected,
        Func<string, Task> progress, CancellationToken token, EncodedSourceContext? source = null)
    {
        for (var index = 0; index < routes.Count; index++)
        {
            try { return await resolvers.OpenEncodingAsync(routes[index], expected, progress, token, source); }
            catch (DiscoveryException failure) when (index + 1 < routes.Count && RetryRoute(failure))
            { await progress("Trying another advertised official metadata route"); }
        }
        throw Error("METADATA_UNAVAILABLE", "No admitted official route supplied encoding metadata.", true);
    }
    private async Task<VerifiedMetadata> MetadataAsync(IReadOnlyList<Uri> routes, MetadataExpectation expected,
        MetadataReceipt? retained, Func<string, Task> progress, CancellationToken token, EncodedSourceContext? source = null)
    {
        for (var index = 0; index < routes.Count; index++)
        {
            try { return await metadata.AcquireAsync(routes[index], expected, progress, token, retained, source); }
            catch (DiscoveryException failure) when (index + 1 < routes.Count && RetryRoute(failure))
            { await progress("Trying another advertised official metadata route"); }
        }
        throw Error("METADATA_UNAVAILABLE", "No admitted official route supplied root metadata.", true);
    }
    internal static bool RetryRoute(DiscoveryException failure) => failure.Code is
        "METADATA_UNAVAILABLE" or "CONTENT_UNAVAILABLE" or "NETWORK_UNAVAILABLE" or "NETWORK_IO" or "CONTENT_TIMEOUT" or "METADATA_TIMEOUT" or
        "CONTENT_SOURCE_MISSING" or "CONTENT_LOCATOR_INCOMPLETE" or "CONTENT_NOT_LOCATED";
    internal static IReadOnlyList<Uri> Routes(CapturedOpeningContext context, Uri first)
    {
        var fields = MetadataParser.Parse(context.Cdns.Bytes).Rows[context.Reference.CdnsRowOrdinal].Fields;
        var result = new List<Uri> { first };
        foreach (var server in fields["Servers"].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count == 4) break;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
                uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Query.Length > 0 && !Regex.IsMatch(uri.Query, "^\\?(?:maxhosts=[0-9]{1,3}|fallback=[01])(?:&(?:maxhosts=[0-9]{1,3}|fallback=[01]))*$", RegexOptions.CultureInvariant)) continue;
            var route = new Uri($"https://{uri.Host}/{fields["Path"]}/");
            try { OfficialSourceTransport.ValidateUri(ArchivedContentLocator.Url(route, new string('1', 32))); }
            catch (DiscoveryException) { continue; }
            if (!result.Contains(route)) result.Add(route);
        }
        return result;
    }

    internal static async Task<MetadataExpectation> ResolveAsync(EncodingFileLookup lookup, string role,
        string contentKey, CancellationToken token)
    {
        var content = await lookup.FindContentAsync(contentKey, token)
            ?? throw Error("CONTENT_UNRESOLVED", "Selected content has no mapping in this exact build's encoding metadata.");
        foreach (var key in content.EncodingKeys.Order(StringComparer.Ordinal))
        {
            var encoded = await lookup.FindEncodingAsync(key, token);
            if (encoded is not null)
                return new(role, content.ContentKey, encoded.EncodingKey, encoded.EncodedBytes, content.DecodedBytes);
        }
        throw Error("CONTENT_UNRESOLVED", "Selected content has no complete encoded representation in this build.");
    }
    internal static string RootKey(TactConfiguration configuration)
    {
        if (!configuration.Fields.TryGetValue("root", out var values) || values.Count != 1 ||
            values[0].Length != 32 || values[0].Any(ch => !char.IsAsciiHexDigit(ch)) || values[0].All(ch => ch == '0'))
            throw Error("CONTENT_ROOT_UNSUPPORTED", "This build has no supported exact ROOT content declaration.");
        return values[0].ToLowerInvariant();
    }
    private static void ValidatePrevious(ContentReceipt previous, MetadataExpectation expected)
    {
        if (previous.ContentKey != expected.ContentKey || previous.EncodingKey != expected.EncodingKey ||
            previous.EncodedBytes != N(expected.EncodedBytes) || previous.DecodedBytes != N(expected.DecodedBytes) ||
            previous.VerifierRevision != VerifierRevision && previous.VerifierRevision != ProjectionVerifierRevision ||
            previous.VerifierRevision == VerifierRevision && previous.Projection is not null ||
            previous.VerifierRevision == ProjectionVerifierRevision && previous.Projection is null)
            throw Error("CONTENT_IDENTITY_CONFLICT", "A retained selected-file receipt differs from its exact current source mapping.");
        if (previous.Projection is { } projection && projection.IdentitySha256 != ContentProjectionIdentity.Compute(
            previous.ContentKey, previous.EncodingKey, previous.DecodedBytes, projection))
            throw Error("CONTENT_PROJECTION_INTEGRITY", "A retained selected-file projection has a conflicting proof identity.");
    }
    private static long Number(string value) => long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
    public void Dispose() => configurations.Dispose();
}
