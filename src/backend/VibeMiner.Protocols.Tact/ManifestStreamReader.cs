using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

// Shared manifest framing policy, independent of acquisition, storage and payload integrity.
internal static class ManifestStreamReader
{
    public static void ValidateLimits(long decoded, int minimumHeader, long entries, int tags,
        int names, int bitmap, long aggregateTags)
    {
        if (decoded < minimumHeader || entries < 0 || tags < 0 || names < 0 || bitmap < 0 || aggregateTags < 0)
            throw new ArgumentOutOfRangeException(nameof(decoded), "Manifest limits must admit a header and have nonnegative finite bounds.");
    }

    public static int AdmitLayout(string family, long consumed, long decodedLimit, long entries, long entryLimit,
        int tags, int tagLimit, long entryWidth, int bitmapLimit, long aggregateTagLimit, bool nonemptyTag)
    {
        if (entries > entryLimit || tags > tagLimit) throw Limit(family, "Manifest entry or tag count exceeds policy.");
        var bitmap = (entries + 7) / 8;
        if (tags > 0 && bitmap > bitmapLimit) throw Limit(family, "Manifest tag bitmap exceeds policy.");
        try
        {
            var minimumTags = checked(tags * (bitmap + (nonemptyTag ? 4 : 3)));
            var minimumBody = checked(entries * entryWidth + minimumTags);
            if (minimumBody > decodedLimit - consumed || minimumTags > aggregateTagLimit)
                throw Limit(family, "Declared manifest geometry or tag work exceeds policy.");
        }
        catch (OverflowException) { throw Limit(family, "Manifest geometry exceeds supported arithmetic."); }
        return checked((int)bitmap);
    }

    // Charge the complete serialized tag (UTF-8 name, NUL, type and packed bitmap). Preserve
    // padding and source ordinals; expansion into entry/tag relationships is not parser work.
    public static async ValueTask<(string Name, ushort Type, byte[] Membership, long Work)> TagAsync(
        AsyncInventoryReader reader, string family, int nameLimit, int bitmapBytes, long remainingWork, bool allowEmpty)
    {
        var minimumFraming = 3L + bitmapBytes;
        if (remainingWork < minimumFraming + (allowEmpty ? 0 : 1))
            throw Limit(family, "Manifest aggregate tag work exceeds policy.");
        var start = reader.ConsumedBytes;
        var effectiveNameLimit = (int)Math.Min(nameLimit, remainingWork - minimumFraming);
        var name = await reader.CStringAsync(effectiveNameLimit, allowEmpty);
        var type = await reader.UInt16BigEndianAsync();
        var membership = await reader.BytesAsync(bitmapBytes);
        return (name, type, membership, reader.ConsumedBytes - start);
    }

    public static DiscoveryException Unsupported(string family) => new(family + "_UNSUPPORTED", "Unsupported manifest signature, version or key width.");
    public static DiscoveryException Limit(string family, string message) => new(family + "_LIMIT", message);
    public static DiscoveryException Invalid(string family, string message) => new(family + "_INVALID", message);
}
