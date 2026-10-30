using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Strict full original verification only. No projection/receipt-shape shortcut.</summary>
internal static class GeometrySourceVerification
{
    // One operation owns at most two streams, one path, staging and source payload.
    // Failed close uncertainty is latched, never automatically retried or refunded.
    internal sealed class Owner(IDisposable? staging)
    {
        private Stream? encoded;
        private FileStream? decoded;
        private string? path;
        private IDisposable? stagingCharge = staging, payload;
        private IAsyncDisposable? handle;
        private Exception? handleFailure;
        private GeometrySourceCleanupException? failure;
        private bool encodedFailed, decodedFailed, deletionFailed;
        internal FileStream Decoded => decoded ?? throw new ObjectDisposedException(nameof(Owner));
        internal void OwnEncoded(Stream stream) { if (encoded is not null) throw new InvalidOperationException(); encoded = stream; }
        internal void OwnDecoded(FileStream stream) { if (decoded is not null) throw new InvalidOperationException(); decoded = stream; path = stream.Name; }
        internal void RetainPayload(IDisposable reservation)
        { if (payload is not null) throw new InvalidOperationException(); payload = reservation; }
        internal GeometrySourceCleanupException RetainHandle(IAsyncDisposable value, Exception cause)
        { if (handle is not null || handleFailure is not null) throw new InvalidOperationException(); handle = value; handleFailure = cause; return Failed(cause); }
        private GeometrySourceCleanupException Failed(Exception cause) => failure ??= new(this, cause);
        internal async ValueTask CloseEncodedAsync()
        {
            if (failure is not null) throw failure;
            if (encoded is null) return;
            try { await encoded.DisposeAsync(); encoded = null; }
            catch (Exception cause) { encodedFailed = true; throw Failed(cause); }
        }
        internal async ValueTask DisposeAsync()
        {
            if (encoded is not null && !encodedFailed)
                try { await encoded.DisposeAsync(); encoded = null; } catch (Exception cause) { encodedFailed = true; Failed(cause); }
            if (decoded is not null && !decodedFailed)
                try { await decoded.DisposeAsync(); decoded = null; } catch (Exception cause) { decodedFailed = true; Failed(cause); }
            if (path is not null && !deletionFailed)
                try { File.Delete(path); path = null; } catch (Exception cause) { deletionFailed = true; Failed(cause); }
            if (failure is not null) throw failure;
            try { stagingCharge?.Dispose(); stagingCharge = null; }
            catch (Exception cause) { throw Failed(cause); }
        }
    }
    internal sealed class Node(Owner owner, GeometryCanonicalSources canonical) : IAsyncDisposable
    {
        internal FileStream Decoded => owner.Decoded;
        internal GeometryCanonicalSources Canonical { get; } = canonical;
        private Task? disposal;
        public ValueTask DisposeAsync() => new(disposal ??= owner.DisposeAsync().AsTask());
    }
    internal static async Task<Node> VerifyAsync(GeometrySourceServices services, AssetResolutionResult result,
        EncodedSourceContext context, IGeometrySelectionFence selection, Func<CreatureGeometryProgress, Task> progress,
        CreatureGeometryDependency dependency, CancellationToken token)
    {
        var expected = result.Expectation ?? throw GeometrySourceCapsuleCodec.Invalid();
        var texture = dependency == CreatureGeometryDependency.Texture;
        RequireSourceSize(expected, dependency);
        var stage = dependency switch { CreatureGeometryDependency.Model => "model", CreatureGeometryDependency.Skin => "skin", CreatureGeometryDependency.Texture => "texture", _ => throw GeometrySourceCapsuleCodec.Invalid() };
        var physical = await services.Shared.AcquireAsync(new(expected.EncodingKey, expected.EncodedBytes), context,
            AcquisitionPriority.Interactive, p => progress(new(stage + "Acquisition", "Acquiring original " + stage + " source", p.Unit, p.Completed, p.Total, dependency)), token);
        await selection.RevalidateAsync(token);
        if (physical is null || physical.EncodingKey != expected.EncodingKey || physical.EncodedBytes != expected.EncodedBytes ||
            physical.VerifierRevision != OfficialEncodedObjectProducer.VerifierRevision) throw GeometrySourceCapsuleCodec.Invalid();
        GeometrySourceCapsuleCodec.Hash(physical.Sha256, 64);
        // The physical producer has retired its own encoded staging before its receipt completes.
        // Reserve only the actual decoded phase, never32MiB across a nested producer wait.
        var staging = await services.Resources.ReserveStagingAsync(expected.DecodedBytes, null, token);
        var owner = new Owner(staging); var transferred = false;
        try
        {
            await progress(new(stage + "Verification", "Verifying complete original " + stage + " source", "bytes", 0, expected.EncodedBytes, dependency));
            var encoded = await services.Objects.OpenVerifiedAsync(physical.Sha256, expected.EncodedBytes, services.Resources, null, token);
            owner.OwnEncoded(encoded);
            {
                var decoded = await services.Objects.CreateStagingFileAsync(token); owner.OwnDecoded(decoded);
                // Explicit existing finite original unit: <=4MiB encoded/8MiB decoded. Its
                // streams are plain caller-owned files; no CAS/row/resource callbacks nest here.
                using (await services.Resources.EnterCpuAsync(null, token))
                    await BlteDecoder.DecodeAsync(encoded, decoded, expected.EncodingKey, expected.ContentKey, expected.EncodedBytes, expected.DecodedBytes, token);
                await decoded.FlushAsync(token);
            }
            await owner.CloseEncodedAsync();
            token.ThrowIfCancellationRequested();
            var candidate = new ContentReceipt(expected.ContentKey, expected.EncodingKey, physical.Sha256,
                expected.EncodedBytes.ToString(CultureInfo.InvariantCulture), expected.DecodedBytes.ToString(CultureInfo.InvariantCulture),
                physical.SourceUrl, physical.RetrievedAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SelectedFileAcquirer.VerifierRevision, physical.Location);
            // This seam bounds the EXISTING row before GetString and records only when absent,
            // in one transaction. Generic RecordContentAsync before it would defeat that bound.
            var canonical = await services.Catalog.ReadGeometryOriginalSourcesAsync(result.Association, expected, candidate, token, texture);
            await selection.RevalidateAsync(token); owner.Decoded.Position = 0;
            transferred = true; return new(owner, canonical);
        }
        finally
        {
            if (!transferred) await owner.DisposeAsync();
        }
    }
    /// <summary>The finite source sizes, checked before any payload demand. A source over them is refused with its
    /// own and the allowed sizes, so a large real file is never mistaken for a corrupt one (#98).</summary>
    internal static void RequireSourceSize(MetadataExpectation expected, CreatureGeometryDependency dependency)
    {
        var texture = dependency == CreatureGeometryDependency.Texture;
        long encodedLimit = texture ? 16 << 20 : 4 << 20, decodedLimit = texture ? 16 << 20 : 8 << 20;
        if (expected.Role != "asset" || expected.EncodedBytes < 9 || expected.DecodedBytes < 1) throw GeometrySourceCapsuleCodec.Limit();
        if (expected.EncodedBytes <= encodedLimit && expected.DecodedBytes <= decodedLimit) return;
        var role = dependency switch { CreatureGeometryDependency.Model => "model", CreatureGeometryDependency.Skin => "SKIN", _ => "texture" };
        throw new DiscoveryException("CREATURE_GEOMETRY_RESOURCE_LIMIT", string.Create(CultureInfo.InvariantCulture,
            $"The {role} file is {Mib(expected.DecodedBytes)} decoded and {Mib(expected.EncodedBytes)} encoded; geometry reads at most {Mib(decodedLimit)} decoded and {Mib(encodedLimit)} encoded."));
    }
    // Rounded up, so a file just over a limit never reads as equal to it.
    private static string Mib(long bytes) => (bytes % (1 << 20) == 0 ? (bytes >> 20).ToString(CultureInfo.InvariantCulture)
        : (Math.Ceiling(bytes / 104857.6) / 10).ToString("0.0", CultureInfo.InvariantCulture)) + " MiB";
    internal static DiscoveryException Cleanup() => new("CREATURE_GEOMETRY_CLEANUP_FAILED", "Geometry source cleanup failed; its reservation remains charged.");
}

// The exact exception is retained by Store's failed group. It keeps the bounded
// operation owner strongly reachable through shutdown; it grants no source authority.
internal sealed class GeometrySourceCleanupException(GeometrySourceVerification.Owner owner, Exception cause)
    : Exception("Geometry source cleanup failed; its owners and reservations remain charged.", cause)
{
    internal void RetainPayload(IDisposable reservation) => owner.RetainPayload(reservation);
    internal void RetainHandle(IAsyncDisposable handle, Exception cause) => owner.RetainHandle(handle, cause);
}
