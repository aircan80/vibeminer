namespace VibeMiner.Protocols.Tact;

/// <summary>Original bounded DS-v1 enumeration. Nine-byte keys remain exact prefixes.</summary>
public static class DownloadSizeInventoryReader
{
    public static async Task<DownloadSizeCompletion> ReadAsync(Stream input, DownloadSizeLimits limits,
        IDownloadSizeConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(consumer);
        ManifestStreamReader.ValidateLimits(limits.MaxDecodedBytes, 15, limits.MaxEntries, limits.MaxTags,
            limits.MaxNameBytes, limits.MaxBitmapBytes, limits.MaxAggregateTagBytes);
        var reader = new AsyncInventoryReader(input, limits.MaxDecodedBytes, "DS", cancellationToken);
        if (await reader.ByteAsync() != 'D' || await reader.ByteAsync() != 'S')
            throw ManifestStreamReader.Unsupported("DS");
        var version = await reader.ByteAsync();
        var keyWidth = await reader.ByteAsync();
        if (version != 1 || keyWidth is not (9 or 16)) throw ManifestStreamReader.Unsupported("DS");
        var entries = await reader.UInt32BigEndianAsync();
        var tags = await reader.UInt16BigEndianAsync();
        var declaredBytes = await reader.UInt40BigEndianAsync();
        var bitmapBytes = ManifestStreamReader.AdmitLayout("DS", reader.ConsumedBytes, limits.MaxDecodedBytes,
            entries, limits.MaxEntries, tags, limits.MaxTags, keyWidth + 4L, limits.MaxBitmapBytes,
            limits.MaxAggregateTagBytes, nonemptyTag: false);
        var header = new DownloadSizeHeader(version, keyWidth, entries, tags, declaredBytes);
        await consumer.HeaderAsync(header, cancellationToken);
        reader.CheckCancellation();
        long tagWork = 0;
        for (var ordinal = 0; ordinal < tags; ordinal++)
        {
            var tag = await ManifestStreamReader.TagAsync(reader, "DS", limits.MaxNameBytes, bitmapBytes,
                limits.MaxAggregateTagBytes - tagWork, allowEmpty: true);
            tagWork += tag.Work;
            await consumer.TagAsync(ordinal, new(tag.Name, tag.Type, entries, tag.Membership), cancellationToken);
            reader.CheckCancellation();
        }
        long measuredBytes = 0;
        for (long ordinal = 0; ordinal < entries; ordinal++)
        {
            var key = new EncodedKeyReference(keyWidth, Convert.ToHexStringLower(await reader.BytesAsync(keyWidth)));
            var size = await reader.UInt32BigEndianAsync();
            try { measuredBytes = checked(measuredBytes + size); }
            catch (OverflowException) { throw ManifestStreamReader.Limit("DS", "Download-size sum exceeds supported arithmetic."); }
            await consumer.EntryAsync(ordinal, new(key, size), cancellationToken);
            reader.CheckCancellation();
        }
        await reader.ExpectEofAsync();
        if (measuredBytes != declaredBytes)
            throw ManifestStreamReader.Invalid("DS", "Measured entry-size sum differs from declared encoded total.");
        return new(header, entries, tags, measuredBytes, reader.ConsumedBytes);
    }
}
