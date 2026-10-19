namespace VibeMiner.Protocols.Tact;

/// <summary>Original bounded DL-v1/v2/v3 enumeration; checksums and flags remain opaque metadata.</summary>
public static class DownloadInventoryReader
{
    public static async Task<DownloadCompletion> ReadAsync(Stream input, DownloadLimits limits,
        IDownloadConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(consumer);
        ManifestStreamReader.ValidateLimits(limits.MaxDecodedBytes, 11, limits.MaxEntries, limits.MaxTags,
            limits.MaxNameBytes, limits.MaxBitmapBytes, limits.MaxAggregateTagBytes);
        if (limits.MaxFlagBytes is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(limits));
        var reader = new AsyncInventoryReader(input, limits.MaxDecodedBytes, "DL", cancellationToken);
        if (await reader.ByteAsync() != 'D' || await reader.ByteAsync() != 'L')
            throw ManifestStreamReader.Unsupported("DL");
        var version = await reader.ByteAsync();
        var keyWidth = await reader.ByteAsync();
        if (version is < 1 or > 3 || keyWidth != 16) throw ManifestStreamReader.Unsupported("DL");
        var checksumPresent = await reader.ByteAsync();
        var entries = await reader.UInt32BigEndianAsync();
        var tags = await reader.UInt16BigEndianAsync();
        var flags = version >= 2 ? await reader.ByteAsync() : (byte)0;
        byte? basePriority = version == 3 ? await reader.ByteAsync() : null;
        var unknown = version == 3 ? await reader.BytesAsync(3) : [];
        if (flags > limits.MaxFlagBytes) throw ManifestStreamReader.Limit("DL", "Download flag width exceeds policy.");
        var bitmapBytes = ManifestStreamReader.AdmitLayout("DL", reader.ConsumedBytes, limits.MaxDecodedBytes,
            entries, limits.MaxEntries, tags, limits.MaxTags, 22L + (checksumPresent != 0 ? 4 : 0) + flags,
            limits.MaxBitmapBytes, limits.MaxAggregateTagBytes, nonemptyTag: false);
        await consumer.HeaderAsync(new(version, keyWidth, checksumPresent, entries, tags, flags, basePriority, unknown), cancellationToken);
        reader.CheckCancellation();
        for (long ordinal = 0; ordinal < entries; ordinal++)
        {
            var key = Convert.ToHexStringLower(await reader.BytesAsync(16));
            var encodedBytes = await reader.UInt40BigEndianAsync();
            var priority = await reader.ByteAsync();
            uint? checksum = checksumPresent != 0 ? await reader.UInt32BigEndianAsync() : null;
            var rawFlags = await reader.BytesAsync(flags);
            await consumer.EntryAsync(ordinal, new(key, encodedBytes, priority, checksum, rawFlags), cancellationToken);
            reader.CheckCancellation();
        }
        long tagWork = 0;
        for (var ordinal = 0; ordinal < tags; ordinal++)
        {
            var tag = await ManifestStreamReader.TagAsync(reader, "DL", limits.MaxNameBytes, bitmapBytes,
                limits.MaxAggregateTagBytes - tagWork, allowEmpty: true);
            tagWork += tag.Work;
            await consumer.TagAsync(ordinal, new(tag.Name, tag.Type, entries, tag.Membership), cancellationToken);
            reader.CheckCancellation();
        }
        await reader.ExpectEofAsync();
        return new(version, entries, tags, reader.ConsumedBytes);
    }
}
