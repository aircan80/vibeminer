using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog : IRetentionStore, IRetentionDriverCatalog
{
    internal const string RetentionPolicyRevision = "retention-encoded-v1";
    private const int RetentionSlots = 9;
    private sealed record RetentionJob(string JobId, RetentionPlan Plan, long Revision = 0,
        RetentionMode Mode = RetentionMode.OnDemand, bool Paused = false,
        RetentionState State = RetentionState.OnDemand, bool Active = false, long QueueOrder = 0,
        long Generation = 0, long Cursor = 0, long Present = 0, long PresentBytes = 0,
        long Missing = 0, long Corrupt = 0, long Unresolved = 0, long Failed = 0,
        string PresenceState = "notAssessed", string? AssessedAt = null,
        AcquisitionProgress? Progress = null, int Pending = 0, long WireBytes = 0,
        RetentionProblem? Problem = null, BulkArchiveLocatorArtifact? Map = null);

    private void UpgradeRetention()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE retention_sequence(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER NOT NULL);
            INSERT INTO retention_sequence VALUES(1,0);
            CREATE TABLE retention_jobs(job_id TEXT PRIMARY KEY,product TEXT NOT NULL,build_id TEXT NOT NULL,
                region TEXT NOT NULL,ledger TEXT NOT NULL,mode INTEGER NOT NULL,active INTEGER NOT NULL,
                queue_order INTEGER NOT NULL,document TEXT NOT NULL,UNIQUE(product,build_id,region,ledger));
            CREATE INDEX retention_occupied ON retention_jobs(queue_order,job_id) WHERE mode=1 OR active=1;
            CREATE TABLE retention_coverage(ledger TEXT PRIMARY KEY,object_count INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL,receipt_count INTEGER NOT NULL,receipt_bytes INTEGER NOT NULL,receipted_at TEXT);
            CREATE TABLE retention_bits(ledger TEXT NOT NULL,page INTEGER NOT NULL,bits BLOB NOT NULL,
                receipt_count INTEGER NOT NULL,receipt_bytes INTEGER NOT NULL,digest TEXT NOT NULL,
                PRIMARY KEY(ledger,page),FOREIGN KEY(ledger) REFERENCES retention_coverage(ledger));
            CREATE TABLE retention_problems(job_id TEXT NOT NULL,generation INTEGER NOT NULL,ordinal INTEGER NOT NULL,
                problem TEXT NOT NULL,PRIMARY KEY(job_id,generation,ordinal),FOREIGN KEY(job_id) REFERENCES retention_jobs(job_id));
            CREATE TABLE retention_native_sources(cdn_sha256 TEXT NOT NULL,archive_key TEXT NOT NULL,receipt TEXT NOT NULL,
                PRIMARY KEY(cdn_sha256,archive_key));
            CREATE TABLE object_invalidity(sha256 TEXT NOT NULL,byte_length INTEGER NOT NULL,reason TEXT NOT NULL,
                observed_at TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(sha256,byte_length));
            CREATE TABLE object_invalidity_sequence(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER NOT NULL);
            INSERT INTO object_invalidity_sequence VALUES(1,0);
            """, transaction);
        Execute("""
            CREATE TABLE items_artifacts(content_key TEXT PRIMARY KEY,descriptor TEXT NOT NULL);
            CREATE TABLE items_admissions(build_id TEXT NOT NULL,region TEXT NOT NULL,source_reference TEXT NOT NULL,
                locale TEXT NOT NULL,content_key TEXT NOT NULL,prepared_at TEXT NOT NULL,
                PRIMARY KEY(build_id,region,source_reference,locale),
                FOREIGN KEY(build_id) REFERENCES builds(id),FOREIGN KEY(content_key) REFERENCES items_artifacts(content_key));
            """, transaction);
        Execute("PRAGMA user_version=9", transaction);
        transaction.Commit();
    }

    Task<RetentionSnapshot> IRetentionStore.ReadAsync(RetentionTarget target, CancellationToken token) => Schedule(() =>
    {
        RetentionService.Validate(target);
        var job = ReadRetentionJob(target, null);
        var plan = job?.Plan ?? ReadRetentionPlan(target, null);
        return RetentionSnapshotOf(job ?? new RetentionJob("", plan), null);
    }, token);

    Task<IReadOnlyList<RetentionSnapshot>> IRetentionStore.ListAsync(CancellationToken token) => Schedule<IReadOnlyList<RetentionSnapshot>>(() =>
    {
        var jobs = ReadOccupiedRetentionJobs(null);
        return jobs.Select(job => RetentionSnapshotOf(job, null)).ToArray();
    }, token);

    Task<RetentionSnapshot> IRetentionStore.SetAsync(RetentionSetRequest request, CancellationToken token) => Schedule(() =>
    {
        RetentionService.Validate(request);
        using var transaction = Db.BeginTransaction();
        var existing = ReadRetentionJob(request.Target, transaction);
        var job = existing ?? new RetentionJob(Guid.NewGuid().ToString("N"), ReadRetentionPlan(request.Target, transaction));
        if (request.ExpectedRevision is not null && request.ExpectedRevision != job.Revision)
            throw new DiscoveryException("RETENTION_STALE", "This download intent changed. Read its current state and try again.", true);
        var changed = request.Action switch
        {
            RetentionAction.DownloadAll => job.Mode != RetentionMode.DownloadAll,
            RetentionAction.Pause => job.Mode != RetentionMode.DownloadAll || !job.Paused,
            RetentionAction.OnDemand => job.Mode != RetentionMode.OnDemand || job.Paused,
            RetentionAction.Resume => job.Mode != RetentionMode.DownloadAll || job.Paused || job.State is RetentionState.Failed or RetentionState.Waiting,
            _ => false
        };
        if (!changed) { transaction.Commit(); return RetentionSnapshotOf(existing is null ? job with { JobId = "" } : job, null); }
        var mode = request.Action == RetentionAction.OnDemand ? RetentionMode.OnDemand : RetentionMode.DownloadAll;
        var paused = request.Action == RetentionAction.Pause;
        var occupied = ReadOccupiedRetentionJobs(transaction);
        if (mode == RetentionMode.DownloadAll && job.Mode != RetentionMode.DownloadAll && !job.Active && occupied.Count >= RetentionSlots)
            throw new DiscoveryException("RETENTION_CAPACITY", "Nine download intents are already active, waiting or paused.", true);
        var order = mode == RetentionMode.DownloadAll && !paused ? NextRetentionOrder(transaction) : job.QueueOrder;
        job = job with { Revision = checked(job.Revision + 1), Mode = mode, Paused = paused,
            State = job.Active ? RetentionState.Stopping : mode == RetentionMode.OnDemand ? RetentionState.OnDemand : paused ? RetentionState.Paused : RetentionState.Queued,
            QueueOrder = order, Problem = null };
        SaveRetentionJob(job, transaction);
        EnsureRetentionCoverage(job.Plan.Ledger, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return RetentionSnapshotOf(job, null);
    }, token);

    private long NextRetentionOrder(SqliteTransaction transaction)
    {
        Execute("UPDATE retention_sequence SET value=value+1 WHERE id=1", transaction);
        return Convert.ToInt64(Scalar("SELECT value FROM retention_sequence WHERE id=1", transaction));
    }
    private RetentionJob? ReadRetentionJob(RetentionTarget target, SqliteTransaction? transaction) =>
        ReadRetentionJobWhere("product=$p AND build_id=$b AND region=$r AND ledger=$l", transaction,
            ("$p", target.ProductId), ("$b", target.BuildIdentity), ("$r", target.SourceRegion), ("$l", target.LedgerId));
    private RetentionJob? ReadRetentionJob(string jobId, SqliteTransaction? transaction) =>
        ReadRetentionJobWhere("job_id=$j", transaction, ("$j", jobId));
    private RetentionJob? ReadRetentionJobWhere(string predicate, SqliteTransaction? transaction, params (string, object?)[] parameters)
    {
        using var command = Command("SELECT job_id,product,build_id,region,ledger,mode,active,queue_order,document,length(document) FROM retention_jobs WHERE " + predicate, transaction, parameters);
        using var reader = command.ExecuteReader(); return reader.Read() ? ReadRetentionJobRow(reader) : null;
    }
    private RetentionJob ReadRetentionJobRow(SqliteDataReader reader)
    {
        if (reader.GetInt64(9) is < 1 or > 65536) throw RetentionIntegrity("A retention checkpoint exceeds its bounded size.");
        RetentionJob job;
        try { job = Deserialize<RetentionJob>(reader.GetString(8)); ValidateRetentionJob(job); }
        catch (Exception ex) when (ex is JsonException or NullReferenceException or ArgumentException or OverflowException) { throw RetentionIntegrity("A retention checkpoint is malformed."); }
        if (reader.GetInt32(6) is not (0 or 1) || job.JobId != reader.GetString(0) || job.Plan.Target != new RetentionTarget(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) ||
            (int)job.Mode != reader.GetInt32(5) || job.Active != (reader.GetInt32(6) == 1) || job.QueueOrder != reader.GetInt64(7))
            throw RetentionIntegrity("A retention checkpoint differs from its indexed identity or intent.");
        return job;
    }
    private List<RetentionJob> ReadOccupiedRetentionJobs(SqliteTransaction? transaction)
    {
        using var command = Command("SELECT job_id,product,build_id,region,ledger,mode,active,queue_order,document,length(document) FROM retention_jobs WHERE mode=1 OR active=1 ORDER BY queue_order,job_id LIMIT 10", transaction);
        using var reader = command.ExecuteReader(); var result = new List<RetentionJob>();
        while (reader.Read()) result.Add(ReadRetentionJobRow(reader));
        if (result.Count > RetentionSlots || result.Count(job => job.Active) > 1) throw RetentionIntegrity("Retention queue capacity or active ownership is inconsistent.");
        return result;
    }
    private void SaveRetentionJob(RetentionJob job, SqliteTransaction transaction)
    {
        ValidateRetentionJob(job);
        Execute("""
            INSERT INTO retention_jobs VALUES($j,$p,$b,$r,$l,$m,$a,$q,$d)
            ON CONFLICT(job_id) DO UPDATE SET mode=$m,active=$a,queue_order=$q,document=$d
            """, transaction, ("$j", job.JobId), ("$p", job.Plan.Target.ProductId), ("$b", job.Plan.Target.BuildIdentity),
            ("$r", job.Plan.Target.SourceRegion), ("$l", job.Plan.Target.LedgerId), ("$m", (int)job.Mode),
            ("$a", job.Active ? 1 : 0), ("$q", job.QueueOrder), ("$d", Serialize(job)));
    }
    private static void ValidateRetentionJob(RetentionJob job)
    {
        ValidateRetentionPlan(job.Plan);
        var ledger = job.Plan.Ledger;
        if (job.JobId.Length is not (0 or 32) || job.Revision < 0 || job.Generation < 0 || job.QueueOrder < 0 ||
            !Enum.IsDefined(job.Mode) || !Enum.IsDefined(job.State) || job.Paused && job.Mode != RetentionMode.DownloadAll ||
            job.Cursor < 0 || job.Cursor > ledger.ObjectCount || job.Present < 0 || job.Present > ledger.ObjectCount || job.Missing < 0 || job.Missing > ledger.ObjectCount ||
            job.Corrupt < 0 || job.Corrupt > ledger.ObjectCount || job.Unresolved < 0 || job.Unresolved > ledger.ObjectCount || job.Failed < 0 || job.Failed > ledger.ObjectCount ||
            job.Present + job.Missing + job.Corrupt + job.Unresolved + job.Failed != job.Cursor || job.PresentBytes < 0 || job.PresentBytes > ledger.TotalEncodedBytes ||
            job.Pending is < 0 or > 32 || job.WireBytes < 0 || job.PresenceState is not ("notAssessed" or "assessing" or "assessed" or "interrupted") ||
            job.AssessedAt is not null && !DownloadUtc(job.AssessedAt) ||
            (job.PresenceState == "assessed") != (job.AssessedAt is not null) ||
            job.Active != (job.State is RetentionState.Preparing or RetentionState.Running or RetentionState.Stopping) ||
            job.State == RetentionState.Completed && (job.Cursor != ledger.ObjectCount || job.Present != ledger.ObjectCount ||
                job.PresentBytes != ledger.TotalEncodedBytes || job.Pending != 0 || job.PresenceState != "assessed") ||
            job.State == RetentionState.Paused && (!job.Paused || job.Mode != RetentionMode.DownloadAll) ||
            job.State == RetentionState.Queued && (job.Paused || job.Mode != RetentionMode.DownloadAll))
            throw RetentionIntegrity("A retention intent or bounded pass checkpoint is inconsistent.");
        ValidateRetentionProblem(job.Problem); ValidateRetentionProgress(job.Progress);
        if (job.Map is not null) ValidateRetentionMap(job.Map, ledger);
    }
    private static void ValidateRetentionPlan(RetentionPlan plan)
    {
        if (plan is null) throw RetentionIntegrity("A pinned retention plan is missing.");
        RetentionService.Validate(plan.Target);
        var ledger = plan.Ledger;
        if (ledger is null || ledger.Revision != DownloadLedger.Revision || ledger.Sha256 != plan.Target.LedgerId && plan.Scope != DownloadRootScope ||
            !KnownDownloadScope(plan.Scope) || plan.PolicyRevision != RetentionPolicyRevision)
            throw RetentionIntegrity("A retention plan has unsupported scope, ledger or policy.");
        ValidateDownloadArtifact(new(ledger.Sha256, ledger.ByteLength, ledger.ObjectCount, ledger.TotalEncodedBytes));
        ValidateDownloadSource(plan.DownloadSource, true);
        if (plan.SourceReference is null || plan.SourceReference.Region != plan.Target.SourceRegion || !CanonicalOrdinal(plan.SourceReference.Generation) ||
            !DownloadHash(plan.SourceReference.VersionsSha256, 64) || !DownloadHash(plan.SourceReference.CdnsSha256, 64) ||
            plan.SourceReference.VersionsRowOrdinal < 0 || plan.SourceReference.CdnsRowOrdinal < 0 ||
            plan.Configurations is null || plan.Configurations.Count != 2 ||
            plan.Configurations.Count(c => c.Role == "buildConfig") != 1 || plan.Configurations.Count(c => c.Role == "cdnConfig") != 1)
            throw RetentionIntegrity("A retention plan lacks its exact source and configuration binding.");
        foreach (var c in plan.Configurations)
            if (!DownloadHash(c.Key, 32) || !DownloadHash(c.Sha256, 64) || c.ByteLength is < 1 or > 1048576 ||
                c.ParsedFieldCount is < 0 or > 4096 || !DownloadUtc(c.RetrievedAt) || !DownloadUtc(c.VerifiedAt))
                throw RetentionIntegrity("A retention plan configuration receipt is malformed.");
    }
    private static void ValidateRetentionProblem(RetentionProblem? problem)
    {
        if (problem is not null && (string.IsNullOrWhiteSpace(problem.Code) || problem.Code.Length > 64 || string.IsNullOrWhiteSpace(problem.Message) || problem.Message.Length > 400))
            throw RetentionIntegrity("A retention problem exceeds its bounded description.");
    }
    private static void ValidateRetentionProgress(AcquisitionProgress? progress)
    {
        if (progress is not null && (string.IsNullOrWhiteSpace(progress.Stage) || progress.Stage.Length > 400 || string.IsNullOrWhiteSpace(progress.Unit) || progress.Unit.Length > 64 ||
            progress.Completed < 0 || progress.Total < 0 || progress.Completed is not null && progress.Total is not null && progress.Completed > progress.Total))
            throw RetentionIntegrity("Retention stage progress is malformed.");
    }
    private static DiscoveryException RetentionIntegrity(string message) => new("RETENTION_STORAGE_INTEGRITY", message);
}
