using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    Task IRetentionDriverCatalog.RestoreDriverAsync(CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        foreach (var original in ReadOccupiedRetentionJobs(transaction))
        {
            var job = original;
            if (job.Active)
                job = job with { Active = false, Pending = 0, PresenceState = "interrupted", AssessedAt = null,
                    State = job.Mode == RetentionMode.OnDemand ? RetentionState.OnDemand : job.Paused ? RetentionState.Paused : RetentionState.Queued };
            // Failed and Waiting are durable explicit-retry latches, not startup work.
            SaveRetentionJob(job, transaction);
        }
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    Task<RetentionDriverJob?> IRetentionDriverCatalog.ClaimNextRetentionAsync(CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        var jobs = ReadOccupiedRetentionJobs(transaction);
        if (jobs.Any(j => j.Active)) return null;
        var job = jobs.FirstOrDefault(j => j.Mode == RetentionMode.DownloadAll && !j.Paused && j.State == RetentionState.Queued);
        if (job is null) return null;
        ValidateRetentionPlanAssociation(job.Plan, transaction);
        job = job with { Active = true, State = RetentionState.Preparing, Generation = checked(job.Generation + 1),
            Cursor = 0, Present = 0, PresentBytes = 0, Missing = 0, Corrupt = 0, Unresolved = 0, Failed = 0,
            PresenceState = "assessing", AssessedAt = null, Progress = null, Pending = 0, WireBytes = 0, Problem = null };
        Execute("DELETE FROM retention_problems WHERE job_id=$j", transaction, ("$j", job.JobId));
        SaveRetentionJob(job, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new RetentionDriverJob(new(job.JobId, job.Revision, job.Generation), job.Plan, job.Map);
    }, token);

    Task<RetentionDriverIntent?> IRetentionDriverCatalog.ReadRetentionIntentAsync(string jobId, CancellationToken token) => Schedule(() =>
    {
        var job = ReadRetentionJob(jobId, null);
        return job is null ? null : new RetentionDriverIntent(job.Revision, job.Mode, job.Paused);
    }, token);

    internal Task<bool> RegisterRetentionMapAsync(RetentionDriverBinding binding, BulkArchiveLocatorArtifact map, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction(); var job = CurrentRetentionJob(binding, transaction);
        if (job is null) return false;
        ValidateRetentionMap(map, job.Plan.Ledger);
        SaveRetentionJob(job with { Map = map, State = RetentionState.Running }, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    Task<bool> IRetentionDriverCatalog.RegisterRetentionMapAsync(RetentionDriverBinding binding, BulkArchiveLocatorArtifact map, CancellationToken token) =>
        RegisterRetentionMapAsync(binding, map, token);

    Task<bool> IRetentionDriverCatalog.ReportRetentionProgressAsync(RetentionDriverBinding binding, AcquisitionProgress? progress,
        int pending, long wireBytes, CancellationToken token) => Schedule(() =>
    {
        ValidateRetentionProgress(progress);
        if (pending is < 0 or > 32 || wireBytes < 0) throw RetentionIntegrity("Retention progress exceeds its frontier bound.");
        using var transaction = Db.BeginTransaction(); var job = CurrentRetentionJob(binding, transaction);
        if (job is null) return false;
        if (wireBytes < job.WireBytes) throw RetentionIntegrity("Physical transfer bytes cannot move backwards within a pass.");
        SaveRetentionJob(job with { Progress = progress, Pending = pending, WireBytes = wireBytes }, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    Task IRetentionDriverCatalog.SettleRetentionAsync(RetentionDriverBinding binding, RetentionPassOutcome outcome,
        RetentionProblem? problem, CancellationToken token) => Schedule(() =>
    {
        if (!Enum.IsDefined(outcome)) throw RetentionIntegrity("Unknown retention pass outcome.");
        ValidateRetentionProblem(problem);
        using var transaction = Db.BeginTransaction(); var job = ReadRetentionJob(binding.JobId, transaction);
        if (job is null || !job.Active || job.Generation != binding.Generation ||
            outcome != RetentionPassOutcome.IntentChanged && job.Revision != binding.IntentRevision) return false;
        if (outcome == RetentionPassOutcome.IntentChanged && job.Revision <= binding.IntentRevision) return false;
        var state = job.Mode == RetentionMode.OnDemand ? RetentionState.OnDemand : job.Paused ? RetentionState.Paused : RetentionState.Queued;
        var presence = "interrupted"; string? assessed = null;
        if (outcome == RetentionPassOutcome.Completed)
        {
            var coverage = ReadRetentionCoverage(job.Plan.Ledger, transaction);
            if (job.Cursor == job.Plan.Ledger.ObjectCount && job.Pending == 0 && job.Present == job.Plan.Ledger.ObjectCount &&
                job.PresentBytes == job.Plan.Ledger.TotalEncodedBytes && job.Missing == 0 && job.Corrupt == 0 && job.Unresolved == 0 && job.Failed == 0 &&
                coverage.Count == job.Plan.Ledger.ObjectCount && coverage.Bytes == job.Plan.Ledger.TotalEncodedBytes)
            { state = RetentionState.Completed; presence = "assessed"; assessed = DateTimeOffset.UtcNow.ToString("O"); problem = null; }
            else { state = RetentionState.Failed; problem = new("RETENTION_INCOMPLETE", "This pass did not establish every declared object and its current presence.", true); }
        }
        else if (outcome == RetentionPassOutcome.Failed) state = RetentionState.Failed;
        else if (outcome == RetentionPassOutcome.Waiting) state = RetentionState.Waiting;
        // Shutdown preserves user intent. IntentChanged derives only the latest durable mode/paused.
        job = job with { Active = false, State = state, Pending = 0, PresenceState = presence, AssessedAt = assessed,
            Progress = null, Problem = outcome is RetentionPassOutcome.IntentChanged or RetentionPassOutcome.Shutdown ? null : problem };
        SaveRetentionJob(job, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private RetentionJob? CurrentRetentionJob(RetentionDriverBinding binding, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var job = ReadRetentionJob(binding.JobId, transaction);
        return job is not null && job.Active && job.Revision == binding.IntentRevision && job.Generation == binding.Generation &&
            job.Mode == RetentionMode.DownloadAll && !job.Paused ? job : null;
    }
    private static void ValidateRetentionMap(BulkArchiveLocatorArtifact map, RetentionLedger ledger)
    {
        if (!DownloadHash(map.MapSha256, 64) || !DownloadHash(map.ManifestSha256, 64) || map.LocationCount is < 0 or > 10000000 ||
            map.MapBytes != 32 + 32 * map.LocationCount || map.ManifestBytes is < 1 or > (2L << 20) ||
            map.LocatedMembers < 0 || map.LocatedMembers > ledger.ObjectCount || map.LocatedMembers > map.LocationCount)
            throw RetentionIntegrity("A sealed retention locator descriptor exceeds its bounds.");
    }
}
