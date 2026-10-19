using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record RetentionDriverBinding(string JobId, long IntentRevision, long Generation);
internal sealed record RetentionDriverJob(RetentionDriverBinding Binding, RetentionPlan Plan,
    BulkArchiveLocatorArtifact? Map = null);
internal sealed record RetentionDriverIntent(long Revision, RetentionMode Mode, bool Paused);
internal enum RetentionPresence { Present, Missing, Corrupt, Unknown }
internal enum RetentionPassOutcome { Completed, Failed, Waiting, IntentChanged, Shutdown }

// Ordinal is zero based. A historical receipt is independent of current stat presence.
// Catalog verifies the actual committed receipt; this flag is never publication authority.
internal sealed record RetentionMemberObservation(long Ordinal, string EncodingKey, long EncodedBytes,
    RetentionPresence Presence, bool HasReceipt, RetentionProblem? Problem = null);

internal interface IRetentionDriverCatalog
{
    Task RestoreDriverAsync(CancellationToken token);
    Task<RetentionDriverJob?> ClaimNextRetentionAsync(CancellationToken token);
    Task<RetentionDriverIntent?> ReadRetentionIntentAsync(string jobId, CancellationToken token);
    Task<bool> RegisterRetentionMapAsync(RetentionDriverBinding binding, BulkArchiveLocatorArtifact map, CancellationToken token);
    // At most 64 final observations in strictly contiguous ordinal order; nextOrdinal is
    // the next index to visit, never a one-based DownloadLedgerRow.Id interpreted as an index.
    Task<bool> ApplyRetentionCreditAsync(RetentionDriverBinding binding,
        IReadOnlyList<RetentionMemberObservation> observations, long nextOrdinal, CancellationToken token);
    Task<bool> ReportRetentionProgressAsync(RetentionDriverBinding binding, AcquisitionProgress? progress,
        int pending, long wireBytes, CancellationToken token);
    // IntentChanged permits a newer intent revision only for the same still-active generation.
    // Every settlement runs after actual subscriptions and sessions have been cleaned up.
    Task SettleRetentionAsync(RetentionDriverBinding binding, RetentionPassOutcome outcome,
        RetentionProblem? problem, CancellationToken token);
}

internal interface IRetentionSessionFactory
{
    Task<IRetentionSession> OpenAsync(RetentionDriverJob job, Func<AcquisitionProgress, Task> progress, CancellationToken token);
}

internal interface IRetentionSession : IAsyncDisposable
{
    Task<IReadOnlyList<DownloadLedgerRow>> ReadAsync(long nextOrdinal, int limit, CancellationToken token);
    Task<IReadOnlyList<RetentionMemberObservation>> ObserveAsync(IReadOnlyList<DownloadLedgerRow> members, CancellationToken token);
    Task<IAcquisitionSubscription> AttachAsync(DownloadLedgerRow member, Func<AcquisitionProgress, Task> progress,
        CancellationToken token);
}
