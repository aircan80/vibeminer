namespace VibeMiner.Objects;

/// <summary>Named store-level failures. Per-object Put refusals and read states are results, not exceptions.</summary>
public static class ObjectStoreCodes
{
    public const string InventoryStale = "OBJECT_LIBRARY_INVENTORY_STALE";
    public const string InventoryCursorStale = "OBJECT_LIBRARY_INVENTORY_CURSOR_STALE";
    public const string InventoryUpgradeRequired = "OBJECT_LIBRARY_INVENTORY_UPGRADE_REQUIRED";
    public const string OwnerRequired = "OBJECT_STORE_OWNER_REQUIRED";
    public const string LibraryOperationUnsupported = "OBJECT_LIBRARY_OPERATION_UNSUPPORTED";
    /// <summary>A pack, entry or index was written by a newer format than this engine knows. Nothing was changed.</summary>
    public const string NewerFormat = "OBJECT_STORE_NEWER_FORMAT";
    /// <summary>A pack belongs to another store (its store id differs). Nothing was changed.</summary>
    public const string ForeignPack = "OBJECT_STORE_FOREIGN_PACK";
    /// <summary>The folder's packs or index are of the other <see cref="ObjectStoreKind"/> than the one asked for. Nothing was changed.</summary>
    public const string KindMismatch = "OBJECT_STORE_KIND_MISMATCH";
    /// <summary>Another writer holds the store.</summary>
    public const string Busy = "OBJECT_STORE_BUSY";
    /// <summary>A write was asked of a read-only store.</summary>
    public const string ReadOnly = "OBJECT_STORE_READ_ONLY";
    /// <summary>A read-only store found no store at the folder.</summary>
    public const string Missing = "OBJECT_STORE_MISSING";
    /// <summary>The index can't be read; Scan rebuilds it from the packs.</summary>
    public const string IndexUnreadable = "OBJECT_STORE_INDEX_UNREADABLE";
    /// <summary>A write to a pack or the index failed; the store stopped writing. Reopen recovers.</summary>
    public const string WriteFailed = "OBJECT_STORE_WRITE_FAILED";
    /// <summary>The volume is full. Nothing past the last commit was kept.</summary>
    public const string DiskFull = "OBJECT_STORE_DISK_FULL";
    /// <summary>The store was closed (or a test crashed it) before the operation finished.</summary>
    public const string Closed = "OBJECT_STORE_CLOSED";
    /// <summary>A call was over a fixed bound (batch size, whole-read size, staging budget).</summary>
    public const string Limit = "OBJECT_STORE_LIMIT";
    /// <summary>Staged bytes failed a chunk check; the staging file keeps only the verified prefix.</summary>
    public const string StagingRejected = "OBJECT_STAGING_REJECTED";
}

/// <summary>Per-object refusal codes from Put and staging. Each means the bytes never reached a pack.</summary>
public static class ObjectRefusalCodes
{
    public const string KeyTypeUnsupported = "OBJECT_KEY_TYPE_UNSUPPORTED";
    public const string NamespaceUnsupported = "OBJECT_NAMESPACE_UNSUPPORTED";
    public const string SizeMismatch = "OBJECT_SIZE_MISMATCH";
    public const string TooLarge = "OBJECT_TOO_LARGE";
    public const string KeyMismatch = "OBJECT_KEY_MISMATCH";
    public const string BlteInvalid = "OBJECT_BLTE_INVALID";
    public const string IndexInvalid = "OBJECT_INDEX_INVALID";
    /// <summary>A well-formed index or BLTE layout this format doesn't admit (another version, page size or width, such as an
    /// archive-group index; a chunk table format other than 0x0F). Unsupported, not corrupt.</summary>
    public const string IndexUnsupported = "OBJECT_INDEX_UNSUPPORTED";
    public const string BlteUnsupported = "OBJECT_BLTE_UNSUPPORTED";
    public const string HistoryInvalid = "OBJECT_HISTORY_INVALID";
    public const string HistoryDocumentMissing = "OBJECT_HISTORY_DOCUMENT_MISSING";
    public const string DeleteRefused = "OBJECT_DELETE_REFUSED";
}

public sealed class ObjectStoreException : Exception
{
    public ObjectStoreException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    public string Code { get; }
}

/// <summary>Thrown by the test fault hook to stop the store as a process kill would: no cleanup, no commit.</summary>
internal sealed class SimulatedCrashException(StorePoint point) : Exception($"Simulated crash at {point}.")
{
    public StorePoint Point { get; } = point;
}

/// <summary>Points the fault hook can stop at. Each is a place where a real process kill can land.</summary>
internal enum StorePoint
{
    InventoryAccountingSnapshot, // all inventory identities resolved in a pinned library-wide snapshot
    InventoryAccountingIndexRow, // one committed index row reached before independent accounting
    InventoryAccountingRow,      // one producer input row reached before library-wide deduplication
    InventoryPairSnapshot,    // both selections resolved in the pair measurement's pinned snapshot
    InventoryPairRow,         // one sorted producer input row reached before pair deduplication
    InventoryUnionSnapshot,   // matching producer identities resolved in a pinned union snapshot
    InventoryUnionRow,        // one sorted producer input row reached before union deduplication
    InventoryMeasureSnapshot, // active generation read in the measurement's pinned snapshot
    InventoryMeasureRow,      // one expected-key/object-index join row reached, before counting it
    InventoryStaged,       // staged keys committed, not active
    InventoryBeforePublish, // before the publication transaction
    InventoryInsidePublish, // active pointer switched, transaction not committed
    InventoryPublished,    // publication transaction committed
    PackFileWritten,      // new pack file written, not yet synced or listed
    PackListed,           // new pack's row committed
    EntryStarted,         // before any byte of an entry
    EntryHalfWritten,     // part of an entry written
    EntryWritten,         // whole entry written, not synced
    BeforePackSync,
    AfterPackSync,        // pack synced, index batch not committed
    InsideIndexCommit,    // index transaction open, rows written, not committed
    AfterIndexCommit,     // committed, callers not yet told
    WriteErrorCutting,    // write error: pending batch dropped, pack not yet cut back, callers not yet told
    WriteErrorCut,        // inside the cut back: an IOException thrown here is the cut failing, and the writer stops
    RecoveryTruncated,    // tail truncated, recovery rows not committed
    CompactTargetListed,
    CompactCopied,        // live entries copied and synced, index not switched
    CompactSwitched,      // index points at the new pack, old file still there
    CompactOldDeleted,    // old file deleted, its row not yet removed
    RebuildBuilt,         // rebuilt index complete, not yet swapped in
    RebuildSwapped,
    StagedBeforePublish,    // verified library staged copy (or duplicate claim) before its final cancellation/publication fence
    StagingAppended,      // staged bytes written to the staging file
    IndexMeasuring,       // full index check: after the store row is pinned, before the measure
}
