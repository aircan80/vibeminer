namespace VibeMiner.Objects;

/// <summary>One object to store: its key, the length the source declared for it (EN, index or config) and the bytes as served.</summary>
public sealed record PutObject(ObjectKey Key, long DeclaredLength, ReadOnlyMemory<byte> Bytes, ObjectOrigin Origin = ObjectOrigin.Cdn);

public enum PutOutcome
{
    /// <summary>Verified, appended and committed.</summary>
    Stored,
    /// <summary>The store already had the key; nothing was appended.</summary>
    AlreadyPresent,
    /// <summary>The bytes failed their key type's check, or the call broke a rule; nothing was appended. See the code.</summary>
    Refused,
    /// <summary>Canceled before its bytes were appended; nothing was stored.</summary>
    Canceled,
}

public sealed record PutResult(ObjectKey Key, PutOutcome Outcome, string? Code = null, string? Message = null);

public enum ReadState
{
    /// <summary>The bytes passed every read check and were returned.</summary>
    Present,
    /// <summary>The store has no committed entry for the key.</summary>
    Absent,
    /// <summary>An entry is indexed but its bytes are truncated or don't match their checks. No bytes were returned.</summary>
    Corrupt,
    /// <summary>An entry is indexed but its pack file is gone. No bytes were returned.</summary>
    Lost,
}

/// <summary>Facts from the entry header: payload length, where the bytes came from, and when they were appended.</summary>
public sealed record ObjectInfo(ObjectKey Key, long Length, ObjectOrigin Origin, DateTimeOffset StoredAt);

/// <summary>A whole read. <see cref="Bytes"/> is set only for <see cref="ReadState.Present"/>.</summary>
public sealed record ObjectRead(ReadState State, ObjectInfo? Info = null, byte[]? Bytes = null, string? Problem = null, bool FromSecondary = false);

/// <summary>A byte-range read. The destination holds the range only for <see cref="ReadState.Present"/>.</summary>
public sealed record RangeRead(ReadState State, ObjectInfo? Info = null, string? Problem = null, bool FromSecondary = false);

public enum DeleteOutcome { Deleted, Absent, Refused }
public sealed record DeleteResult(ObjectKey Key, DeleteOutcome Outcome, string? Code = null);

/// <summary><see cref="FullIndexCheck"/>: the open ran the full index check (the last writer didn't close cleanly), not just the cheap one.</summary>
public sealed record OpenReport(int RecoveredEntries, long TruncatedBytes, bool IndexRebuilt, IReadOnlyList<string> Problems, bool FullIndexCheck = false);
/// <summary>
/// A scrub: the full index check (<see cref="IndexProblem"/> is null when it held; <see cref="IndexRebuilt"/> when the writer rebuilt
/// it) and Verify. <see cref="IndexChecked"/> is always true now that the check measures one pinned snapshot (#201); it is kept
/// for callers that still read it.
/// </summary>
public sealed record ScrubReport(string? IndexProblem, bool IndexRebuilt, VerifyReport Verify, bool IndexChecked = true);
public sealed record ScanReport(int Packs, long Entries, long Live, long Duplicates, long Tombstones, long Corrupt, long Unverifiable, IReadOnlyList<string> Problems);
public sealed record VerifyReport(long Checked, long Bytes, long CorruptCount, long LostCount, IReadOnlyList<ObjectKey> Corrupt, IReadOnlyList<ObjectKey> Lost);
/// <summary><see cref="SkippedCorrupt"/>: packs left alone because they hold a corrupt live entry (Read and Verify still report it).</summary>
public sealed record CompactReport(int PacksCompacted, long BytesBefore, long BytesAfter, long Moved, long SkippedCorrupt);
public sealed record ScanProgress(long Entries, long Bytes);

/// <summary>
/// What the index holds as of the last commit, from one read of its store and pack rows. <see cref="Objects"/> counts every object
/// row (history documents and records included); <see cref="PackBytes"/> is the committed length of the live packs and
/// <see cref="LiveBytes"/> the part of it live entries use. Compaction targets and retired packs are left out.
/// </summary>
public sealed record StoreStats(int Packs, long Objects, long HistoryRecords, long PackBytes, long LiveBytes, DateTimeOffset CreatedAt);

/// <summary>
/// What a store holds. <see cref="Cdn"/>: CDN bytes, key types 1..6, checked against their keys. <see cref="Generated"/>: prepared
/// data, key type 7 only, no history log and no staging. The kind is written in every pack header and the index; a folder of one
/// kind never opens as the other.
/// </summary>
public enum ObjectStoreKind { Cdn = 0, Generated = 1 }

/// <summary>Bounds and timing. The defaults are the D123 group-commit rule and pack size.</summary>
public sealed record ObjectStoreOptions
{
    /// <summary>The store's kind; opening a folder of the other kind throws OBJECT_STORE_KIND_MISMATCH.</summary>
    public ObjectStoreKind Kind { get; init; } = ObjectStoreKind.Cdn;
    public long MaxPackBytes { get; init; } = 2L << 30;
    public int CommitObjects { get; init; } = 128;
    public long CommitBytes { get; init; } = 16L << 20;
    public TimeSpan CommitDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public int ReadConnections { get; init; } = 8;
    public int PackHandles { get; init; } = 64;
    public long MaxStagingBytes { get; init; } = 2L << 30;
    public int MaxStagings { get; init; } = 16;
    public TimeSpan StagingLifetime { get; init; } = TimeSpan.FromDays(7);
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Tests only: called at each <see cref="StorePoint"/>; throwing <see cref="SimulatedCrashException"/> stops the store as a kill would.</summary>
    internal Action<StorePoint>? Fault { get; init; }
}

public sealed record CompactOptions(double MinDeadFraction = 0.25, int MaxPacks = 1);
