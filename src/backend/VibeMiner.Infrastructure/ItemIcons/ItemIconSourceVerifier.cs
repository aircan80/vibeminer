using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Physical encoded proof alone is insufficient for a full decoded content receipt.</summary>
internal static class ItemIconSourceVerifier
{
    internal static Task<ContentReceipt> VerifyAsync(VerifiedObjectStore objects, AcquisitionResources resources,
        MetadataExpectation expected, EncodedReceipt physical, CancellationToken token) =>
        VerifyAsync(objects, resources, null, expected, physical, token);

    internal static async Task<ContentReceipt> VerifyAsync(VerifiedObjectStore objects, AcquisitionResources resources,
        IAcquisitionWorkContext? workContext, MetadataExpectation expected, EncodedReceipt physical, CancellationToken token)
    {
        if (expected.EncodedBytes is < 9 or > IconArtifacts.MaxSourceBytes || expected.DecodedBytes is < 148 or > IconArtifacts.MaxSourceBytes)
            throw new DiscoveryException("ASSET_LIMIT", "This optional icon exceeds the bounded source/decode allowance.");
        if (physical.EncodingKey != expected.EncodingKey || physical.EncodedBytes != expected.EncodedBytes || physical.VerifierRevision != "blte-encoded-v1")
            throw new DiscoveryException("ITEM_ICON_SOURCE_INVALID", "The acquired representation differs from its selected icon expectation.");
        using var staging = await resources.ReserveStagingAsync(expected.DecodedBytes, workContext, token);
        string? path = null;
        try
        {
            await using var encoded = await objects.OpenVerifiedAsync(physical.Sha256, expected.EncodedBytes, resources, workContext, token);
            await using var decoded = await objects.CreateStagingFileAsync(token); path = decoded.Name;
            // Existing finite decoder unit may perform file I/O; it never enters CAS/row/resource callbacks.
            using (await resources.EnterCpuAsync(workContext, token))
                await BlteDecoder.DecodeAsync(encoded, decoded, expected.EncodingKey, expected.ContentKey,
                    expected.EncodedBytes, expected.DecodedBytes, token);
            await decoded.FlushAsync(token); token.ThrowIfCancellationRequested();
            return new(expected.ContentKey, expected.EncodingKey, physical.Sha256,
                expected.EncodedBytes.ToString(CultureInfo.InvariantCulture), expected.DecodedBytes.ToString(CultureInfo.InvariantCulture),
                physical.SourceUrl, physical.RetrievedAt, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                SelectedFileAcquirer.VerifierRevision, physical.Location);
        }
        finally { if (path is not null) File.Delete(path); }
    }
}
