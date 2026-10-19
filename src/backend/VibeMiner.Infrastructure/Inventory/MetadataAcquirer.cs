using System.Globalization;
using System.Text.RegularExpressions;
using VibeMiner.Domain;
using VibeMiner.Application;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>A temporary verified decoded source. Grammar rows and database publication remain caller-owned.</summary>
public sealed class VerifiedMetadata : IAsyncDisposable
{
    private string? decodedPath;
    private Action? release;
    internal VerifiedMetadata(MetadataReceipt receipt, string path, Action releaseOwnership)
    { Receipt = receipt; decodedPath = path; release = releaseOwnership; }
    public MetadataReceipt Receipt { get; }
    public FileStream OpenDecoded()
    {
        var path = decodedPath ?? throw new ObjectDisposedException(nameof(VerifiedMetadata));
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }
    public ValueTask DisposeAsync()
    {
        var path = Interlocked.Exchange(ref decodedPath, null);
        try { if (path is not null) File.Delete(path); }
        finally { Interlocked.Exchange(ref release, null)?.Invoke(); }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Sequential bounded loose-metadata acquisition. The injected HTTP handler must disable redirects.
/// One decoded lease at a time bounds temporary storage; callers dispose decoded readers before it.
/// Global preparation budgets and source grammar/database publication are separate caller duties.
/// </summary>
public sealed class MetadataAcquirer(HttpClient client, VerifiedObjectStore objects, TimeProvider? timeProvider = null,
    ISharedAcquisition? shared = null, AcquisitionResources? resources = null)
{
    public const string SourceRevision = "metadata-blte-source-v1";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim active = new(1, 1);

    public async Task<VerifiedMetadata> AcquireAsync(Uri origin, MetadataExpectation expectation,
        Func<string, Task> progress, CancellationToken cancellationToken, MetadataReceipt? retained = null,
        EncodedSourceContext? source = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(progress);
        ValidateOrigin(origin);
        ValidateExpectation(expectation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await active.WaitAsync(0, cancellationToken))
            throw Error("METADATA_BUSY", "A metadata source is already being acquired or parsed.");
        string? encodedPath = null, decodedPath = null;
        var transferredOwnership = false;
        IDisposable? staging = null;
        try
        {
            var url = new Uri(origin, $"data/{expectation.EncodingKey[..2]}/{expectation.EncodingKey[2..4]}/{expectation.EncodingKey}");
            var retrievedAt = clock.GetUtcNow().ToString("O");
            string sha;
            EncodedReceipt? physical = null;
            if (shared is not null && source is not null)
                physical = await shared.AcquireAsync(new(expectation.EncodingKey, expectation.EncodedBytes), source,
                    AcquisitionPriority.Interactive, state => progress(state.Completed is { } done && state.Total is { } total
                        ? InventoryProgress.Format(state.Stage, done, total, state.Unit) : state.Stage), cancellationToken);
            if (resources is not null)
                staging = await resources.ReserveStagingAsync(expectation.DecodedBytes +
                    (physical is null && retained is null ? expectation.EncodedBytes : 0), null, cancellationToken);
            await using (var decoded = await objects.CreateStagingFileAsync(cancellationToken))
            {
                decodedPath = decoded.Name;
                if (physical is not null)
                {
                    if (retained is not null) ValidateReceipt(retained, expectation);
                    if (physical.EncodingKey != expectation.EncodingKey || physical.EncodedBytes != expectation.EncodedBytes ||
                        retained is not null && physical.Sha256 != retained.Sha256)
                        throw Error("METADATA_IDENTITY_CONFLICT", "Shared metadata bytes conflict with the exact expected or retained encoded identity.");
                    await progress($"Verifying {expectation.Role} metadata");
                    await using var encoded = resources is null
                        ? await objects.OpenVerifiedAsync(physical.Sha256, expectation.EncodedBytes, cancellationToken)
                        : await objects.OpenVerifiedAsync(physical.Sha256, expectation.EncodedBytes, resources, null, cancellationToken);
                    using var cpu = resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken);
                    await BlteDecoder.DecodeAsync(encoded, decoded, expectation.EncodingKey, expectation.ContentKey,
                        expectation.EncodedBytes, expectation.DecodedBytes, cancellationToken);
                    sha = physical.Sha256; retrievedAt = physical.RetrievedAt;
                }
                else if (retained is not null)
                {
                    ValidateReceipt(retained, expectation);
                    await progress($"Verifying saved {expectation.Role} metadata");
                    await using var encoded = resources is null
                        ? await objects.OpenVerifiedAsync(retained.Sha256, expectation.EncodedBytes, cancellationToken)
                        : await objects.OpenVerifiedAsync(retained.Sha256, expectation.EncodedBytes, resources, null, cancellationToken);
                    using var cpu = resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken);
                    await BlteDecoder.DecodeAsync(encoded, decoded, expectation.EncodingKey, expectation.ContentKey,
                        expectation.EncodedBytes, expectation.DecodedBytes, cancellationToken);
                    sha = retained.Sha256;
                    retrievedAt = retained.RetrievedAt;
                }
                else
                {
                    await progress($"Downloading {expectation.Role} metadata");
                    await using (var encoded = await objects.CreateStagingFileAsync(cancellationToken))
                    {
                        encodedPath = encoded.Name;
                        await DownloadAsync(url, encoded, expectation.EncodedBytes, expectation.Role, progress, cancellationToken);
                        retrievedAt = clock.GetUtcNow().ToString("O");
                        encoded.Position = 0;
                        await progress($"Verifying {expectation.Role} metadata");
                        using var cpu = resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken);
                        await BlteDecoder.DecodeAsync(encoded, decoded, expectation.EncodingKey, expectation.ContentKey,
                            expectation.EncodedBytes, expectation.DecodedBytes, cancellationToken);
                    }
                    sha = resources is null
                        ? await objects.PublishFileAsync(encodedPath, expectation.EncodedBytes, cancellationToken)
                        : await objects.PublishFileAsync(encodedPath, expectation.EncodedBytes, resources, null, cancellationToken);
                    encodedPath = null; // Successful publication consumes the app-owned staging file.
                }
                await decoded.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var receipt = new MetadataReceipt(expectation.Role, expectation.ContentKey, expectation.EncodingKey, sha,
                expectation.EncodedBytes.ToString(CultureInfo.InvariantCulture), expectation.DecodedBytes.ToString(CultureInfo.InvariantCulture),
                retained?.SourceUrl ?? physical?.SourceUrl ?? url.AbsoluteUri,
                retained?.RetrievedAt ?? retrievedAt, clock.GetUtcNow().ToString("O"), SourceRevision,
                // Preserve one original retrieval history, including a legacy absent location.
                // The shared encoded receipt separately retains any later physical acquisition.
                retained is null ? physical?.Location : retained.Location);
            var result = new VerifiedMetadata(receipt, decodedPath, () => { staging?.Dispose(); active.Release(); });
            transferredOwnership = true;
            return result;
        }
        finally
        {
            try { if (encodedPath is not null && File.Exists(encodedPath)) File.Delete(encodedPath); }
            finally
            {
                if (!transferredOwnership)
                {
                    try { if (decodedPath is not null && File.Exists(decodedPath)) File.Delete(decodedPath); }
                    finally { staging?.Dispose(); active.Release(); }
                }
            }
        }
    }

    private Task DownloadAsync(Uri url, Stream destination, long expectedBytes, string role,
        Func<string, Task> progress, CancellationToken cancellationToken) =>
        new OfficialSourceTransport(client, resources).DownloadLooseAsync(url, destination, expectedBytes, role,
            progress, cancellationToken, metadata: true);

    private static void ValidateExpectation(MetadataExpectation expected)
    {
        if (expected.Role is not ("encoding" or "root" or "install" or "download" or "size") ||
            !CanonicalKey(expected.ContentKey, 32) || !CanonicalKey(expected.EncodingKey, 32) ||
            expected.ContentKey.All(ch => ch == '0') || expected.EncodingKey.All(ch => ch == '0') ||
            expected.EncodedBytes < 9 || expected.DecodedBytes < 1)
            throw Error("METADATA_INVALID", "A complete nonzero exact metadata expectation is required.");
        if (expected.EncodedBytes > BlteDecoder.MaxEncodedBytes || expected.DecodedBytes > BlteDecoder.MaxDecodedBytes)
            throw Error("METADATA_LIMIT", "Metadata source exceeds the admitted byte limits.");
    }

    private static void ValidateOrigin(Uri origin)
    {
        if (!OfficialUri(origin) || origin.Query.Length != 0 ||
            !Regex.IsMatch(origin.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/$", RegexOptions.CultureInvariant))
            throw Error("SOURCE_UNSUPPORTED", "Only an admitted official HTTPS CDN origin can acquire metadata.");
    }
    private static bool OfficialUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        (uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" ||
            Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant));
    private static bool CanonicalKey(string value, int characters) => value is not null && value.Length == characters &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateReceipt(MetadataReceipt receipt, MetadataExpectation expected)
    {
        var location = receipt.Location;
        var sourceKey = location?.Kind == "archive" ? location.ArchiveKey : expected.EncodingKey;
        if (sourceKey is null || !CanonicalKey(sourceKey, 32) ||
            location is not null && (location.Kind is not ("loose" or "archive") ||
                !long.TryParse(location.Offset, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0 ||
                !long.TryParse(location.SourceBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var sourceBytes) ||
                sourceBytes < expected.EncodedBytes || offset > sourceBytes - expected.EncodedBytes ||
                location.Kind == "loose" && (location.ArchiveKey is not null || offset != 0 || sourceBytes != expected.EncodedBytes)) ||
            receipt.Role != expected.Role || receipt.ContentKey != expected.ContentKey || receipt.EncodingKey != expected.EncodingKey ||
            receipt.EncodedBytes != expected.EncodedBytes.ToString(CultureInfo.InvariantCulture) ||
            receipt.DecodedBytes != expected.DecodedBytes.ToString(CultureInfo.InvariantCulture) || !CanonicalKey(receipt.Sha256, 64) ||
            receipt.ParserRevision != SourceRevision && receipt.ParserRevision != expected.Role + "-v1" ||
            !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var url) || !OfficialUri(url) || url.Query.Length != 0 ||
            !Regex.IsMatch(url.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + sourceKey[..2] + "/" +
                sourceKey[2..4] + "/" + sourceKey + "$", RegexOptions.CultureInvariant) ||
            !UtcTime(receipt.RetrievedAt) || !UtcTime(receipt.VerifiedAt))
            throw Error("METADATA_RECEIPT_INVALID", "A retained metadata receipt differs from its exact source or supported proof.");
    }
    private static bool UtcTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) && parsed.Offset == TimeSpan.Zero;
    private static DiscoveryException Error(string code, string message, bool retryable = false) => new(code, message, retryable);
}
