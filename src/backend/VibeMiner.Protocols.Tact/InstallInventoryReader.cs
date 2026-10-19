namespace VibeMiner.Protocols.Tact;

/// <summary>Original bounded IN-v1 enumeration from a caller-owned decoded stream.</summary>
public static class InstallInventoryReader
{
    public static async Task<InstallCompletion> ReadAsync(Stream input, InstallLimits limits,
        IInstallConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(consumer);
        ManifestStreamReader.ValidateLimits(limits.MaxDecodedBytes, 10, limits.MaxEntries, limits.MaxTags,
            limits.MaxNameBytes, limits.MaxBitmapBytes, limits.MaxAggregateTagBytes);
        if (limits.MaxAggregateNameBytes < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        var reader = new AsyncInventoryReader(input, limits.MaxDecodedBytes, "IN", cancellationToken);
        if (await reader.ByteAsync() != 'I' || await reader.ByteAsync() != 'N')
            throw ManifestStreamReader.Unsupported("IN");
        var version = await reader.ByteAsync();
        var keyWidth = await reader.ByteAsync();
        if (version != 1 || keyWidth != 16) throw ManifestStreamReader.Unsupported("IN");
        var tags = await reader.UInt16BigEndianAsync();
        var entries = await reader.UInt32BigEndianAsync();
        var bitmapBytes = ManifestStreamReader.AdmitLayout("IN", reader.ConsumedBytes, limits.MaxDecodedBytes,
            entries, limits.MaxEntries, tags, limits.MaxTags, 22, limits.MaxBitmapBytes,
            limits.MaxAggregateTagBytes, nonemptyTag: true);
        // Every nonempty entry name costs at least its one-byte spelling and terminator.
        if (entries * 2L > limits.MaxAggregateNameBytes)
            throw ManifestStreamReader.Limit("IN", "Declared entry names exceed aggregate name policy.");
        await consumer.HeaderAsync(new(version, keyWidth, entries, tags), cancellationToken);
        reader.CheckCancellation();
        long tagWork = 0, nameWork = 0;
        for (var ordinal = 0; ordinal < tags; ordinal++)
        {
            var tag = await ManifestStreamReader.TagAsync(reader, "IN", limits.MaxNameBytes, bitmapBytes,
                limits.MaxAggregateTagBytes - tagWork, allowEmpty: false);
            tagWork += tag.Work;
            await consumer.TagAsync(ordinal, new(tag.Name, tag.Type, entries, tag.Membership), cancellationToken);
            reader.CheckCancellation();
        }
        for (long ordinal = 0; ordinal < entries; ordinal++)
        {
            var remaining = limits.MaxAggregateNameBytes - nameWork;
            if (remaining < 2) throw ManifestStreamReader.Limit("IN", "Entry names exceed aggregate name policy.");
            var start = reader.ConsumedBytes;
            var name = await reader.CStringAsync((int)Math.Min(limits.MaxNameBytes, remaining - 1));
            nameWork += reader.ConsumedBytes - start;
            var key = Convert.ToHexStringLower(await reader.BytesAsync(16));
            var decodedBytes = await reader.UInt32BigEndianAsync();
            await consumer.EntryAsync(ordinal, new(name, key, decodedBytes), cancellationToken);
            reader.CheckCancellation();
        }
        await reader.ExpectEofAsync();
        return new(entries, tags, reader.ConsumedBytes);
    }
}
