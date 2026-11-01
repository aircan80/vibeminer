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
/// it) and Verify. <see cref="IndexChecked"/> is false only for a read-only store whose writer kept committing through every attempt:
/// no stable view, so nothing was concluded.
/// </summary>
public sealed record ScrubReport(string? IndexProblem, bool IndexRebuilt, VerifyReport Verify, bool IndexChecked = true);
public sealed record ScanReport(int Packs, long Entries, long Live, long Duplicates, long Tombstones, long Corrupt, long Unverifiable, IReadOnlyList<string> Problems);
public sealed record VerifyReport(long Checked, long Bytes, long CorruptCount, long LostCount, IReadOnlyList<ObjectKey> Corrupt, IReadOnlyList<ObjectKey> Lost);
/// <summary><see cref="SkippedCorrupt"/>: packs left alone because they hold a corrupt live entry (Read and Verify still report it).</summary>
public sealed record CompactReport(int PacksCompacted, long BytesBefore, long BytesAfter, long Moved, long SkippedCorrupt);
public sealed record ScanProgress(long Entries, long Bytes);

/// <summary>Bounds and timing. The defaults are the D123 group-commit rule and pack size.</summary>
public sealed record ObjectStoreOptions
{
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
