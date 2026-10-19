namespace VibeMiner.Formats.Wow;

/// <summary>
/// Original streaming reader for explicit TSFM v1/v2 ROOT. Entries retain every source membership;
/// no locale/platform filtering or filename inference is performed. Caller streams stay open.
/// All awaited callbacks are provisional through successful EOF/count validation and separate
/// source authentication. This reader neither publishes data nor establishes whole-build size.
/// </summary>
public static class RootInventoryReader
{
    public const uint NoNames = 0x10000000;

    public static async Task<RootCompletion> ReadAsync(Stream decoded, RootLimits limits,
        IRootConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(consumer);
        if (limits.MaxDecodedBytes < 20 || limits.MaxRecords < 0 || limits.MaxBlocks < 0 ||
            limits.MaxEntriesPerBlock < 0 || limits.MaxBlockWorkingBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "ROOT limits must be finite nonnegative bounds and allow a header.");
        cancellationToken.ThrowIfCancellationRequested();
        var input = new RootStreamReader(decoded, limits.MaxDecodedBytes, cancellationToken);
        var magic = new byte[4];
        await input.ReadExactlyAsync(magic);
        if (!magic.AsSpan().SequenceEqual("TSFM"u8))
            throw input.Fail("ROOT_UNSUPPORTED", "ROOT signature or legacy layout is unsupported.");
        var headerBytes = await input.ReadUInt32Async();
        var version = await input.ReadUInt32Async();
        var total = await input.ReadUInt32Async();
        var named = await input.ReadUInt32Async();
        if (version is not (1 or 2) || headerBytes is not (20 or 24) || version == 2 && headerBytes != 24)
            throw input.Fail("ROOT_UNSUPPORTED", "ROOT version/header layout is unsupported.");
        if (headerBytes == 24 && await input.ReadUInt32Async() != 0)
            throw input.Fail("ROOT_UNSUPPORTED", "ROOT header extension is unsupported.");
        if (total > limits.MaxRecords) throw input.Fail("ROOT_LIMIT", "ROOT declared membership count exceeds the limit.");
        if (named > total) throw input.Fail("ROOT_INVALID", "ROOT named membership count exceeds its total.");
        await consumer.HeaderAsync(new((int)headerBytes, (int)version, total, named), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        long observed = 0, observedNamed = 0, blocks = 0, peak = 0;
        while (await input.ReadOptionalUInt32Async() is { } count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (blocks >= limits.MaxBlocks) throw input.Fail("ROOT_LIMIT", "ROOT block count exceeds the limit.");
            uint locale, first, second;
            byte third;
            if (version == 1)
            {
                first = await input.ReadUInt32Async();
                locale = await input.ReadUInt32Async();
                second = 0;
                third = 0;
            }
            else
            {
                locale = await input.ReadUInt32Async();
                first = await input.ReadUInt32Async();
                second = await input.ReadUInt32Async();
                third = await input.ReadByteAsync();
            }
            input.Context = $"version={version}, block={blocks}, rawFlags={first}/{second}/{third}";
            if (count > limits.MaxEntriesPerBlock || count > limits.MaxRecords - observed)
                throw input.Fail("ROOT_LIMIT", "ROOT block membership count exceeds the limit.");
            if (count > total - observed)
                throw input.Fail("ROOT_INVALID", "ROOT block membership count exceeds the declared total.");
            var workingBytes = count * 20L; // uint IDs plus packed 16-byte keys, excluding fixed I/O buffer.
            if (workingBytes > limits.MaxBlockWorkingBytes || count > Array.MaxLength / 16)
                throw input.Fail("ROOT_LIMIT", "ROOT block working buffers exceed the limit.");
            var flags = first | second | ((uint)third << 17);
            var hasNames = (flags & NoNames) == 0;
            input.RequireBudget(count * (hasNames ? 28L : 20L));
            if (hasNames && count > named - observedNamed)
                throw input.Fail("ROOT_INVALID", "ROOT named block exceeds the declared named count.");
            cancellationToken.ThrowIfCancellationRequested();
            var entries = (int)count;
            var ids = new uint[entries];
            var keys = new byte[entries * 16];
            peak = Math.Max(peak, workingBytes);
            ulong nextId = 0;
            for (var i = 0; i < entries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = nextId + await input.ReadUInt32Async();
                if (id > uint.MaxValue)
                    throw input.Fail("ROOT_INVALID", "ROOT FileDataID delta overflows unsigned32.");
                ids[i] = (uint)id;
                nextId = id + 1;
            }
            await input.ReadExactlyAsync(keys);
            var rawFlags = new RootRawContentFlags(first, second, third);
            for (var i = 0; i < entries; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong? nameHash = hasNames ? await input.ReadUInt64Async() : null;
                var entry = new RootEntry(ids[i], Convert.ToHexStringLower(keys.AsSpan(i * 16, 16)),
                    locale, flags, rawFlags, nameHash);
                await consumer.EntryAsync(blocks, i, entry, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            observed += count;
            if (hasNames) observedNamed += count;
            await consumer.BlockCompletedAsync(blocks, count, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            blocks++;
        }
        if (observed != total || observedNamed != named)
            throw input.Fail("ROOT_INVALID", "ROOT declared and observed membership counts disagree.");
        cancellationToken.ThrowIfCancellationRequested();
        return new((int)headerBytes, (int)version, observed, observedNamed, blocks, input.Position, peak);
    }
}
