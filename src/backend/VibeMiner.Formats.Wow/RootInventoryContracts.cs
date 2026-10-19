namespace VibeMiner.Formats.Wow;

// Caller-owned decoded stream contracts. Awaited rows remain provisional through complete
// grammar/EOF and separate source proof. The consumer stages rows with bounded backpressure.
public sealed record RootLimits(
    long MaxDecodedBytes = 512L << 20,
    long MaxRecords = 50_000_000,
    int MaxBlocks = 1_000_000,
    int MaxEntriesPerBlock = 2_000_000,
    long MaxBlockWorkingBytes = 64L << 20);

public sealed record RootHeader(int HeaderBytes, int Version, long MembershipCount, long NamedMembershipCount);
public sealed record RootRawContentFlags(uint First, uint Second, byte Third);
public sealed record RootEntry(
    uint FileDataId, string ContentKey, uint LocaleFlags, uint ContentFlags,
    RootRawContentFlags RawContentFlags, ulong? NameHash);
public sealed record RootCompletion(
    int HeaderBytes, int Version, long MembershipCount, long NamedMembershipCount,
    long BlockCount, long ConsumedBytes, long PeakBlockWorkingBytes);

public interface IRootConsumer
{
    ValueTask HeaderAsync(RootHeader header, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken);
    ValueTask BlockCompletedAsync(long blockOrdinal, long entries, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

