using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// D082: whether two metadata receipts name the same verified copy of one object. Role, keys,
/// sizes, bytes and the original retrieval (URL, time, location) are identity. ParserRevision
/// only names the step that recorded a copy, and VerifiedAt is the latest check; neither is.
/// metadata_objects keeps whichever writer's copy came first, so features compare identity only.
/// </summary>
internal static class MetadataReceiptIdentity
{
    internal static bool Same(MetadataReceipt a, MetadataReceipt b)
    {
        // DownloadCatalog.MetadataSourceLocation established legacy null as this exact loose
        // locator. Read that representation without rewriting either original receipt.
        if (!ComparableLocation(a, out var first) || !ComparableLocation(b, out var second)) return false;
        return a.Role == b.Role && a.ContentKey == b.ContentKey && a.EncodingKey == b.EncodingKey && a.Sha256 == b.Sha256 &&
            a.EncodedBytes == b.EncodedBytes && a.DecodedBytes == b.DecodedBytes && a.SourceUrl == b.SourceUrl &&
            a.RetrievedAt == b.RetrievedAt && first == second;
    }

    /// <summary>A feature's admitted copy differs from the build's saved original: not a corrupt projection.</summary>
    /// <param name="reader">Names what was read, e.g. "The Creature table".</param>
    internal static DiscoveryException Mismatch(string code, string reader, MetadataReceipt admitted) =>
        new(code, $"{reader} was read with a {(admitted.Role == "root" ? "ROOT" : admitted.Role == "encoding" ? "ENCODING" : "metadata")} copy that differs from the one saved for this build.");

    private static bool ComparableLocation(MetadataReceipt receipt, out ContentLocation? location)
    {
        location = receipt.Location;
        if (location is not null) return true;
        if (receipt.EncodingKey is not { Length: 32 } || !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var uri)) return false;
        try { OfficialSourceTransport.ValidateUri(uri); }
        catch (DiscoveryException) { return false; }
        var key = receipt.EncodingKey;
        if (!uri.AbsolutePath.EndsWith($"/data/{key[..2]}/{key[2..4]}/{key}", StringComparison.Ordinal)) return false;
        location = new("loose", "0", receipt.EncodedBytes, null); return true;
    }
}
