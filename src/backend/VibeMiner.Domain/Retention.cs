namespace VibeMiner.Domain;

public sealed record RetentionTarget(string ProductId, string BuildIdentity,
    string SourceRegion, string LedgerId);
public sealed record RetentionLedger(string Sha256, long ByteLength,
    long ObjectCount, long TotalEncodedBytes, string Revision);
public sealed record RetentionPlan(RetentionTarget Target, OpeningSourceReference SourceReference,
    IReadOnlyList<ConfigurationReceipt> Configurations, MetadataReceipt DownloadSource,
    RetentionLedger Ledger, string Scope, string PolicyRevision);

public enum RetentionAction { DownloadAll, Pause, Resume, OnDemand }
public enum RetentionMode { OnDemand, DownloadAll }
public enum RetentionState
{
    OnDemand, Queued, Preparing, Running, Waiting, Stopping, Paused, Completed, Failed
}

public sealed record RetentionProblem(string Code, string Message, bool Retryable);
public sealed record RetentionDriverFault(string? JobId, RetentionProblem Problem);

// Receipt coverage is historical encoded proof. Presence is a separately dated local
// existence/length observation, not a new cryptographic verification of every payload.
public sealed record RetentionCoverage(long ReceiptObjects, long ReceiptBytes,
    string? ReceiptedAt, string PresenceState, string? PresenceAssessedAt,
    long ObservedObjects, long PresentObjects, long PresentBytes,
    long MissingObjects, long CorruptObjects);

// Revision belongs only to user intent; progress/coverage must not invalidate commands.
public sealed record RetentionSnapshot(RetentionTarget Target, string? JobId,
    long Revision, RetentionMode Mode, bool Paused, RetentionState State,
    string Scope, long ObjectCount, long TotalEncodedBytes,
    RetentionCoverage Coverage, AcquisitionProgress? Progress,
    int PendingObjects, long WireBytesThisSession, long UnresolvedObjects,
    long FailedObjects, int? QueuePosition, RetentionProblem? Problem);

public sealed record RetentionSetRequest(RetentionTarget Target,
    RetentionAction Action, long? ExpectedRevision = null);
