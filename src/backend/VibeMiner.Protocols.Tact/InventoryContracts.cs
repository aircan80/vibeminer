namespace VibeMiner.Protocols.Tact;

// Shared contracts only. Readers consume caller-owned decoded streams; every awaited callback is
// provisional through complete grammar/EOF AND separate source/BLTE proof. Consumers must stage
// rows. Record collections/memory must own their bytes and remain valid after callbacks return;
// parsers must never expose reused page/scratch buffers. No contract implies SQL, paths or fetching.

public enum EncodingTable { Content, Encoding }

public sealed record EncodingLimits(
    long MaxDecodedBytes = 1L << 30,
    long MaxRecords = 50_000_000,
    long MaxCandidateReferences = 100_000_000,
    int MaxPages = 1_000_000,
    int MaxIndexBytes = 32 << 20,
    int MaxPageBytes = 64 << 10,
    int MaxRecordsPerPage = 65_536,
    int MaxKeysPerContent = 255,
    int MaxSpecificationStrings = 1_000_000,
    int MaxSpecificationBytes = 32 << 20,
    int MaxTrailerBytes = 1 << 20);

public sealed record EncodingHeader(
    byte Version, byte ContentKeyBytes, byte EncodingKeyBytes,
    int ContentPageBytes, int EncodingPageBytes,
    long ContentPageCount, long EncodingPageCount, long SpecificationBytes,
    long MinimumDecodedBytes);

public sealed record EncodingContentEntry(
    string ContentKey, long DecodedBytes, IReadOnlyList<string> EncodingKeys);

public sealed record EncodingEncodedEntry(string EncodingKey, long EncodedBytes, uint SpecificationIndex);

public sealed record EncodingCompletion(
    long ContentPages, long EncodingPages, long ContentRecords, long EncodingRecords,
    long CandidateReferences, int SpecificationStrings, int TrailerBytes, long ConsumedBytes);

public interface IEncodingConsumer
{
    ValueTask HeaderAsync(EncodingHeader header, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask SpecificationAsync(int ordinal, string specification, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask ContentAsync(int pageOrdinal, long ordinal, EncodingContentEntry entry, CancellationToken cancellationToken);
    ValueTask EncodingAsync(int pageOrdinal, long ordinal, EncodingEncodedEntry entry, CancellationToken cancellationToken);
    ValueTask PageCompletedAsync(EncodingTable table, int pageOrdinal, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask TrailerAsync(string trailer, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

// Bitmaps preserve original MSB-first membership and unused padding. They represent EntryCount
// source ordinals, not unique logical files, and must not be expanded into unbounded join rows.
public sealed record InstallLimits(
    long MaxDecodedBytes = 16L << 20,
    long MaxEntries = 500_000,
    int MaxTags = 4096,
    int MaxNameBytes = 4096,
    int MaxBitmapBytes = 2 << 20,
    long MaxAggregateTagBytes = 512L << 20,
    long MaxAggregateNameBytes = 16L << 20);
public sealed record InstallHeader(byte Version, byte ContentKeyBytes, long EntryCount, ushort TagCount);
public sealed record InstallEntry(string Name, string ContentKey, long DecodedBytes);
public sealed record InstallTag(string Name, ushort RawType, long EntryCount, ReadOnlyMemory<byte> Membership);
public sealed record InstallCompletion(long Entries, int Tags, long ConsumedBytes);

public interface IInstallConsumer
{
    ValueTask HeaderAsync(InstallHeader header, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask TagAsync(int ordinal, InstallTag tag, CancellationToken cancellationToken);
    ValueTask EntryAsync(long ordinal, InstallEntry entry, CancellationToken cancellationToken);
}

public sealed record DownloadLimits(
    long MaxDecodedBytes = 1L << 30,
    long MaxEntries = 10_000_000,
    int MaxTags = 4096,
    int MaxNameBytes = 16_384,
    int MaxBitmapBytes = 2 << 20,
    long MaxAggregateTagBytes = 512L << 20,
    int MaxFlagBytes = 255);
public sealed record DownloadHeader(
    byte Version, byte EncodingKeyBytes, byte ChecksumPresentRaw,
    long EntryCount, ushort TagCount, byte FlagBytes,
    byte? BasePriorityRaw, ReadOnlyMemory<byte> UnknownVersion3Bytes);
public sealed record DownloadEntry(
    string EncodingKey, long EncodedBytes, byte PriorityRaw, uint? Checksum, ReadOnlyMemory<byte> Flags);
public sealed record DownloadTag(string Name, ushort RawType, long EntryCount, ReadOnlyMemory<byte> Membership);
public sealed record DownloadCompletion(byte Version, long Entries, int Tags, long ConsumedBytes);

public interface IDownloadConsumer
{
    ValueTask HeaderAsync(DownloadHeader header, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask EntryAsync(long ordinal, DownloadEntry entry, CancellationToken cancellationToken);
    ValueTask TagAsync(int ordinal, DownloadTag tag, CancellationToken cancellationToken);
}

public sealed record DownloadSizeLimits(
    long MaxDecodedBytes = 1L << 30,
    long MaxEntries = 10_000_000,
    int MaxTags = 4096,
    int MaxNameBytes = 16_384,
    int MaxBitmapBytes = 2 << 20,
    long MaxAggregateTagBytes = 512L << 20);
public sealed record DownloadSizeHeader(
    byte Version, byte EncodingKeyBytes, long EntryCount, ushort TagCount, long DeclaredTotalEncodedBytes);

// A nine-byte reference remains a prefix. FullEncodingKey is available only at full width;
// implementations validate exact hex/width rather than padding or inferring identity.
public sealed record EncodedKeyReference(byte ByteLength, string Hex)
{
    public string? FullEncodingKey => ByteLength == 16 ? Hex : null;
}
public sealed record DownloadSizeEntry(EncodedKeyReference EncodedKey, long EncodedBytes);
public sealed record DownloadSizeTag(string Name, ushort RawType, long EntryCount, ReadOnlyMemory<byte> Membership);
public sealed record DownloadSizeCompletion(
    DownloadSizeHeader Header, long Entries, int Tags, long MeasuredEncodedBytes, long ConsumedBytes);

public interface IDownloadSizeConsumer
{
    ValueTask HeaderAsync(DownloadSizeHeader header, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask TagAsync(int ordinal, DownloadSizeTag tag, CancellationToken cancellationToken);
    ValueTask EntryAsync(long ordinal, DownloadSizeEntry entry, CancellationToken cancellationToken);
}
