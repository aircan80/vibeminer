using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

internal sealed record ResolverArtifact(MetadataReceipt Source, string ParserRevision,
    string DecodedSha256, long DecodedBytes);

/// <summary>
/// Owns one admitted native EN lookup and its immutable byte lease. Disposing the lease releases
/// this service's bounded admission; it never removes verified metadata or derived CAS objects.
/// </summary>
public sealed class EncodingLookupLease : IAsyncDisposable
{
    private FileStream? input;
    private Action? release;
    private readonly object disposalGate = new();
    private Task? disposal;

    internal EncodingLookupLease(MetadataReceipt receipt, EncodingFileLookup lookup,
        FileStream input, Action release)
    { Receipt = receipt; Lookup = lookup; this.input = input; this.release = release; }

    public MetadataReceipt Receipt { get; }
    public EncodingFileLookup Lookup { get; }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        var stream = Interlocked.Exchange(ref input, null);
        try { if (stream is not null) await stream.DisposeAsync(); }
        finally { Interlocked.Exchange(ref release, null)?.Invoke(); }
    }
}

/// <summary>
/// Retains source-bound decoded EN bytes in the shared CAS, without per-entry SQL indexes.
/// Original encoded metadata is fully reverified/decoded on every open initially. A cached
/// artifact also gets complete current SHA-256 and grammar/EOF checks before lookup admission.
/// File publication and the small catalog association remain separate recoverable operations.
/// </summary>
public sealed class ResolverArtifacts(DiscoveryCatalog catalog, VerifiedObjectStore objects,
    MetadataAcquirer acquirer, AcquisitionResources? resources = null)
{
    public const string EncodingRevision = "encoding-native-file-v1";
    private int active;

    public async Task<EncodingLookupLease> OpenEncodingAsync(Uri route, MetadataExpectation expected,
        Func<string, Task> progress, CancellationToken cancellationToken = default, EncodedSourceContext? source = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (expected.Role != "encoding")
            throw new DiscoveryException("RESOLVER_UNSUPPORTED", "Native encoding lookup requires encoding metadata.");
        // Decoded metadata is an ordinary immutable CAS object, not an exemption from its limit.
        if (expected.DecodedBytes > VerifiedObjectStore.MaxObjectBytes)
            throw new DiscoveryException("RESOLVER_LIMIT", "Decoded encoding metadata exceeds the retained artifact limit.");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new DiscoveryException("RESOLVER_BUSY", "An encoding lookup is already in use.", true);

        FileStream? input = null;
        var transferred = false;
        try
        {
            var retained = await catalog.FindMetadataAsync(expected.EncodingKey, cancellationToken);
            MetadataReceipt receipt;
            ResolverArtifact artifact;
            await using (var acquired = await acquirer.AcquireAsync(route, expected, progress,
                cancellationToken, retained, source))
            {
                receipt = acquired.Receipt;
                await catalog.RecordMetadataAsync(receipt, cancellationToken);
                // RecordMetadata preserves an earlier original receipt. Bind derived artifacts
                // to that immutable association, not a newly stamped local verification time.
                var association = await catalog.FindMetadataAsync(expected.EncodingKey, cancellationToken)
                    ?? throw new DiscoveryException("CATALOG_INTEGRITY", "Verified encoding metadata lost its source association.");
                var saved = await catalog.FindResolverArtifactAsync(receipt.Sha256, "encoding",
                    EncodingRevision, cancellationToken);
                if (saved is not null)
                {
                    artifact = saved;
                }
                else
                {
                    await progress("Validating native encoding lookup");
                    string stagedPath;
                    using (resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken))
                    await using (var decoded = acquired.OpenDecoded())
                    {
                        stagedPath = decoded.Name;
                        _ = await EncodingFileLookup.OpenAsync(decoded, new(), cancellationToken);
                    }
                    // Publish consumes this closed app-owned staging file. The source lease's
                    // cleanup tolerates an already consumed path and still releases acquisition.
                    await progress("Saving verified encoding lookup");
                    var sha = resources is null
                        ? await objects.PublishFileAsync(stagedPath, expected.DecodedBytes, cancellationToken)
                        : await objects.PublishFileAsync(stagedPath, expected.DecodedBytes, resources, null, cancellationToken);
                    artifact = new(association, EncodingRevision, sha, expected.DecodedBytes);
                    await catalog.RecordResolverArtifactAsync(artifact, cancellationToken);
                }
            }

            // The temporary source lease has been released before a caller acquires another
            // metadata family. The returned lookup owns only its independent immutable CAS read.
            await progress("Verifying saved encoding lookup");
            EncodingFileLookup lookup;
            input = resources is null
                ? await objects.OpenVerifiedAsync(artifact.DecodedSha256, artifact.DecodedBytes, cancellationToken)
                : await objects.OpenVerifiedAsync(artifact.DecodedSha256, artifact.DecodedBytes, resources, null, cancellationToken);
            using (resources is null ? null : await resources.EnterCpuAsync(null, cancellationToken))
            {
            // A valid local SHA alone proves the selected artifact bytes, not that a damaged
            // association still selects this source's decoded content. Bind them to the CKey
            // authenticated by the freshly verified original BLTE source before native admission.
            if (Convert.ToHexStringLower(await MD5.HashDataAsync(input, cancellationToken)) != receipt.ContentKey)
                throw new DiscoveryException("RESOLVER_SOURCE_INTEGRITY", "Saved encoding lookup differs from its verified decoded source.");
            input.Position = 0;
            lookup = await EncodingFileLookup.OpenAsync(input, new(), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Fresh acquisition proves the bytes above; the lease keeps the artifact's original
            // immutable retrieval history rather than mixing it with a later physical receipt.
            var lease = new EncodingLookupLease(artifact.Source, lookup, input, () => Volatile.Write(ref active, 0));
            transferred = true;
            return lease;
        }
        finally
        {
            if (!transferred)
            {
                try { if (input is not null) await input.DisposeAsync(); }
                finally { Volatile.Write(ref active, 0); }
            }
        }
    }
}
