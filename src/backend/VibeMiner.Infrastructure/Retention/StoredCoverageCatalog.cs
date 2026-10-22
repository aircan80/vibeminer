using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One admitted ledger with pending stored-coverage work: a first scan, or changes after <c>Through</c>.</summary>
internal sealed record StoredWork(DownloadLedgerArtifact Ledger, string? ScanAfter, long Through);
/// <summary>A ledger member (zero-based ordinal) whose stored state is recomputed from current receipts.</summary>
internal sealed record StoredMember(long Ordinal, string EncodingKey, long EncodedBytes);
internal sealed record StoredChangeBatch(IReadOnlyList<string> Keys, long Through);

/// <summary>
/// Running per-ledger stored figure (D065). Every event that can change whether an exact encoded object is stored
/// (a new verified receipt, corruption recorded or cleared, absence found or repaired) appends its EKey to
/// <c>stored_changes</c> in the same transaction. The single <see cref="StoredCoverageWorker"/> applies those changes to
/// every admitted ledger's bit pages, and scans content already on disk once when a ledger is first admitted. Reads only
/// project the committed figure.
/// </summary>
public sealed partial class DiscoveryCatalog : IDownloadSummaryStore
{
    internal const int StoredPageBits = 4096;
    internal const int StoredBatchKeys = 1024;
    internal const int StoredWorkLimit = 64;
    internal const int StoredPruneRows = 8192;
    private const int StoredRegionLimit = 64;
    private const int StoredJobLimit = 64;
    private readonly Channel<bool> storedSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    // Summary revision (D077): this engine run's random epoch plus a count of events that can change a stored figure
    // without the figure already moving (a stored change recorded, a ledger admitted). Kept in memory; the epoch makes a
    // revision from another run never match. Waiters share one signal that is replaced only when someone waits.
    private readonly object summaryGate = new();
    private readonly string summaryEpoch = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    private long summaryChanges;
    private TaskCompletionSource summarySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void UpgradeStoredCoverage()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE stored_changes(seq INTEGER PRIMARY KEY AUTOINCREMENT,ekey TEXT NOT NULL);
            CREATE TABLE object_absence(ekey TEXT PRIMARY KEY,sha256 TEXT NOT NULL,encoded_bytes INTEGER NOT NULL,observed_at TEXT NOT NULL);
            CREATE TABLE stored_coverage(ledger TEXT PRIMARY KEY,object_count INTEGER NOT NULL,total_bytes INTEGER NOT NULL,
                stored_count INTEGER NOT NULL,stored_bytes INTEGER NOT NULL,through_seq INTEGER NOT NULL,scan_after TEXT,
                updated_at TEXT NOT NULL,problem TEXT,FOREIGN KEY(ledger) REFERENCES download_artifacts(sha256));
            CREATE TABLE stored_pages(ledger TEXT NOT NULL,page INTEGER NOT NULL,bits BLOB NOT NULL,
                stored_count INTEGER NOT NULL,stored_bytes INTEGER NOT NULL,PRIMARY KEY(ledger,page),
                FOREIGN KEY(ledger) REFERENCES stored_coverage(ledger)) WITHOUT ROWID;
            CREATE INDEX encoded_objects_sha ON encoded_objects(sha256);
            CREATE INDEX content_objects_sha ON content_objects(sha256);
            """, transaction);
        Execute(RootScopeTable, transaction);
        Execute("PRAGMA user_version=15", transaction);
        transaction.Commit();
    }

    /// <summary>Completes when a stored change may be pending. Coalesced: one wake can stand for many changes.</summary>
    internal async Task WaitForStoredChangeAsync(CancellationToken token)
    {
        while (!storedSignal.Reader.TryRead(out _))
            if (!await storedSignal.Reader.WaitToReadAsync(token)) throw new OperationCanceledException(token);
    }

    /// <summary>The current summary revision. Read it before a summary read: any later change makes it differ.</summary>
    public string SummaryRevision { get { lock (summaryGate) return Revision(); } }
    private string Revision() => $"{summaryEpoch}-{summaryChanges.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Completes with the current revision once it differs from <paramref name="after"/>, or after <paramref name="limit"/>
    /// with the unchanged one. It never reads the catalog. A change inside a transaction that later rolls back still wakes
    /// waiters; their next read simply finds nothing new.
    /// </summary>
    public async Task<string> WaitForSummaryChangeAsync(string after, TimeSpan limit, CancellationToken token)
    {
        Task changed;
        lock (summaryGate)
        {
            if (Revision() is var current && current != after) return current;
            if (summarySignal.Task.IsCompleted) summarySignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed = summarySignal.Task;
        }
        try { await changed.WaitAsync(limit, token); }
        catch (TimeoutException) { }
        return SummaryRevision;
    }

    private void SummaryChanged()
    {
        TaskCompletionSource signal;
        lock (summaryGate) { summaryChanges++; signal = summarySignal; }
        signal.TrySetResult();
    }

    private int Changed(string sql, SqliteTransaction? transaction, params (string, object?)[] parameters)
    { using var command = Command(sql, transaction, parameters); return command.ExecuteNonQuery(); }
    private long StoredSequence(SqliteTransaction? transaction) =>
        Convert.ToInt64(Scalar("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='stored_changes'),0)", transaction), CultureInfo.InvariantCulture);

    // The worker is scheduled on this same single SQL worker, so it reads the change only after this commit.
    private void LogStoredChange(string encodingKey, SqliteTransaction transaction)
    {
        Execute("INSERT INTO stored_changes(ekey) VALUES($k)", transaction, ("$k", encodingKey));
        storedSignal.Writer.TryWrite(true);
        SummaryChanged();
    }
    /// <summary>Corruption is recorded by physical identity; every exact encoded alias of those bytes changes.</summary>
    private void LogStoredChangesForObject(string sha256, SqliteTransaction transaction)
    {
        var keys = new List<string>();
        using (var command = Command("""
            SELECT ekey FROM encoded_objects WHERE sha256=$h
            UNION SELECT ekey FROM content_objects WHERE sha256=$h
            UNION SELECT ekey FROM metadata_objects WHERE json_extract(receipt,'$.sha256')=$h
            LIMIT 64
            """, transaction, ("$h", sha256)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) keys.Add(reader.GetString(0));
        foreach (var key in keys) LogStoredChange(key, transaction);
    }
    /// <summary>A newly verified receipt proves current bytes, so an earlier missing observation no longer applies.</summary>
    private void ClearObjectAbsence(string encodingKey, SqliteTransaction transaction)
    {
        if (Changed("DELETE FROM object_absence WHERE ekey=$k", transaction, ("$k", encodingKey)) > 0) LogStoredChange(encodingKey, transaction);
    }
    private void RecordObjectAbsence(string encodingKey, string sha256, long encodedBytes, string observedAt, SqliteTransaction transaction)
    {
        Execute("""
            INSERT INTO object_absence VALUES($k,$h,$n,$t)
            ON CONFLICT(ekey) DO UPDATE SET sha256=$h,encoded_bytes=$n,observed_at=$t
            """, transaction, ("$k", encodingKey), ("$h", sha256), ("$n", encodedBytes), ("$t", observedAt));
        LogStoredChange(encodingKey, transaction);
    }
    private bool HasObjectAbsence(string encodingKey, SqliteTransaction transaction) =>
        Scalar("SELECT 1 FROM object_absence WHERE ekey=$k", transaction, ("$k", encodingKey)) is not null;

    /// <summary>Registers the ledger at admission. A ledger whose count was given up starts over on a new admission.</summary>
    private void EnsureStoredCoverage(DownloadLedgerArtifact artifact, string now, SqliteTransaction transaction)
    {
        var sequence = StoredSequence(transaction);
        var state = Scalar("SELECT problem IS NOT NULL FROM stored_coverage WHERE ledger=$l", transaction, ("$l", artifact.Sha256));
        if (state is null)
            Execute("INSERT INTO stored_coverage VALUES($l,$n,$b,0,0,$s,'',$t,NULL)", transaction,
                ("$l", artifact.Sha256), ("$n", artifact.ObjectCount), ("$b", artifact.TotalEncodedBytes), ("$s", sequence), ("$t", now));
        else if (Convert.ToInt64(state, CultureInfo.InvariantCulture) == 1) ResetStoredCoverage(artifact.Sha256, sequence, now, null, transaction);
        storedSignal.Writer.TryWrite(true);
        SummaryChanged();
    }
    private void ResetStoredCoverage(string ledger, long sequence, string now, string? problem, SqliteTransaction transaction)
    {
        Execute("DELETE FROM stored_pages WHERE ledger=$l", transaction, ("$l", ledger));
        Execute("UPDATE stored_coverage SET stored_count=0,stored_bytes=0,through_seq=$s,scan_after='',updated_at=$t,problem=$p WHERE ledger=$l", transaction,
            ("$l", ledger), ("$s", sequence), ("$t", now), ("$p", problem));
    }

    /// <summary>
    /// Up to 64 ledgers with pending work, round robin: those after <paramref name="after"/> in ledger order first, then
    /// from the start, so every maintained ledger is reached and pruning is never held back by one that is never visited.
    /// </summary>
    internal Task<IReadOnlyList<StoredWork>> ReadStoredWorkAsync(string? after, CancellationToken token) => Schedule<IReadOnlyList<StoredWork>>(() =>
    {
        if (after is not null && !DownloadHash(after, 64)) throw StoredIntegrity();
        var sequence = StoredSequence(null);
        var result = new List<StoredWork>(); var broken = new List<string>();
        foreach (var (predicate, bound) in new[] { ("c.ledger>$after", after ?? ""), ("c.ledger<=$after", after) })
        {
            if (bound is null || result.Count == StoredWorkLimit) continue;
            using var command = Command($$"""
                SELECT c.ledger,c.scan_after,c.through_seq,a.byte_length,a.object_count,a.total_encoded_bytes,c.object_count,c.total_bytes,
                    length(CAST(c.scan_after AS BLOB)),c.stored_count,c.stored_bytes
                FROM stored_coverage c JOIN download_artifacts a ON a.sha256=c.ledger
                WHERE c.problem IS NULL AND (c.scan_after IS NOT NULL OR c.through_seq<$s) AND {{predicate}}
                ORDER BY c.ledger LIMIT $n
                """, null, ("$s", sequence), ("$after", bound), ("$n", StoredWorkLimit - result.Count));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                var artifact = new DownloadLedgerArtifact(reader.GetString(0), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
                string? scan = reader.IsDBNull(1) ? null : reader.GetString(1);
                var through = reader.GetInt64(2);
                try
                {
                    ValidateDownloadArtifact(artifact);
                    if (reader.GetInt64(6) != artifact.ObjectCount || reader.GetInt64(7) != artifact.TotalEncodedBytes || through < 0 || through > sequence ||
                        scan is not null && reader.GetInt64(8) is not (0 or 32) || scan is { Length: 32 } && !DownloadHash(scan, 32) ||
                        reader.GetInt64(9) is var count && (count < 0 || count > artifact.ObjectCount) ||
                        reader.GetInt64(10) is var bytes && (bytes < 0 || bytes > artifact.TotalEncodedBytes || count == 0 && bytes != 0))
                        throw StoredIntegrity();
                }
                // One inconsistent row is given up on its own; every other ledger keeps its count.
                catch (DiscoveryException) { broken.Add(artifact.Sha256); continue; }
                result.Add(new(artifact, scan, through));
            }
        }
        if (broken.Count > 0)
        {
            using var transaction = Db.BeginTransaction();
            foreach (var ledger in broken)
                ResetStoredCoverage(ledger, sequence, DateTimeOffset.UtcNow.ToString("O"), Serialize(StoredFailure("STORED_COVERAGE_INTEGRITY")), transaction);
            transaction.Commit();
        }
        return result;
    }, token);

    /// <summary>The next sorted distinct retained EKeys after <paramref name="after"/>, across every receipt kind.</summary>
    internal Task<IReadOnlyList<string>> ReadStoredKeysAsync(string after, CancellationToken token) => Schedule<IReadOnlyList<string>>(() =>
    {
        if (after.Length != 0 && !DownloadHash(after, 32)) throw StoredIntegrity();
        // Each branch is bounded by its primary-key order, so the first N of the union are exact.
        using var command = Command("""
            SELECT ekey FROM (
                SELECT ekey FROM (SELECT ekey FROM encoded_objects WHERE ekey>$a ORDER BY ekey LIMIT $n)
                UNION SELECT ekey FROM (SELECT ekey FROM content_objects WHERE ekey>$a ORDER BY ekey LIMIT $n)
                UNION SELECT ekey FROM (SELECT ekey FROM metadata_objects WHERE ekey>$a ORDER BY ekey LIMIT $n))
            ORDER BY ekey LIMIT $n
            """, null, ("$a", after), ("$n", StoredBatchKeys));
        using var reader = command.ExecuteReader(); var keys = new List<string>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested(); var key = reader.GetString(0);
            if (!DownloadHash(key, 32)) throw StoredIntegrity();
            keys.Add(key);
        }
        return keys;
    }, token);

    internal Task<StoredChangeBatch> ReadStoredChangesAsync(long through, CancellationToken token) => Schedule(() =>
    {
        var keys = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var last = through; var rows = 0;
        using (var command = Command("SELECT seq,ekey FROM stored_changes WHERE seq>$t ORDER BY seq LIMIT $n", null, ("$t", through), ("$n", StoredBatchKeys)))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested(); rows++; last = reader.GetInt64(0); var key = reader.GetString(1);
                if (!DownloadHash(key, 32)) throw StoredIntegrity();
                if (seen.Add(key)) keys.Add(key);
            }
        // An exhausted read covers everything committed so far, including a pruned tail.
        if (rows < StoredBatchKeys) last = Math.Max(last, StoredSequence(null));
        return new StoredChangeBatch(keys, last);
    }, token);

    /// <summary>
    /// Recomputes members from current receipts and moves the ledger's checkpoint, fenced by its expected position.
    /// Returns false when the checkpoint moved (a reset or a failure) and nothing was written.
    /// </summary>
    internal Task<bool> ApplyStoredAsync(DownloadLedgerArtifact ledger, string? expectedScanAfter, long expectedThrough,
        IReadOnlyList<StoredMember> members, string? nextScanAfter, long nextThrough, CancellationToken token) => Schedule(() =>
    {
        if (members.Count > StoredBatchKeys || members.Select(m => m.Ordinal).Distinct().Count() != members.Count ||
            members.Any(m => m.Ordinal < 0 || m.Ordinal >= ledger.ObjectCount || !DownloadHash(m.EncodingKey, 32) || m.EncodedBytes < 0) ||
            nextThrough < expectedThrough || nextScanAfter is not null && !DownloadHash(nextScanAfter, 32))
            throw StoredIntegrity();
        using var transaction = Db.BeginTransaction();
        long count, bytes;
        using (var command = Command("SELECT scan_after,through_seq,stored_count,stored_bytes,problem IS NOT NULL,object_count,total_bytes FROM stored_coverage WHERE ledger=$l", transaction, ("$l", ledger.Sha256)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return false;
            var scan = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (reader.GetInt64(4) == 1 || scan != expectedScanAfter || reader.GetInt64(1) != expectedThrough) return false;
            if (reader.GetInt64(5) != ledger.ObjectCount || reader.GetInt64(6) != ledger.TotalEncodedBytes) throw StoredIntegrity();
            count = reader.GetInt64(2); bytes = reader.GetInt64(3);
        }
        if (nextThrough > StoredSequence(transaction)) throw StoredIntegrity();
        var stored = StoredMembers(members, transaction, token);
        var pages = new Dictionary<long, StoredPage>();
        foreach (var member in members.OrderBy(m => m.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var number = member.Ordinal / StoredPageBits;
            if (!pages.TryGetValue(number, out var page)) pages.Add(number, page = ReadStoredPage(ledger, number, transaction));
            var bit = (int)(member.Ordinal % StoredPageBits); var mask = (byte)(1 << (bit % 8)); var set = (page.Bits[bit / 8] & mask) != 0;
            if (stored.Contains(member.EncodingKey) == set) continue;
            if (set) { page.Bits[bit / 8] &= (byte)~mask; page.Count--; page.Bytes -= member.EncodedBytes; count--; bytes -= member.EncodedBytes; }
            else { page.Bits[bit / 8] |= mask; page.Count++; page.Bytes = checked(page.Bytes + member.EncodedBytes); count++; bytes = checked(bytes + member.EncodedBytes); }
        }
        foreach (var (number, page) in pages)
        {
            if (page.Count < 0 || page.Bytes < 0) throw StoredIntegrity();
            if (page.Count == 0) Execute("DELETE FROM stored_pages WHERE ledger=$l AND page=$p", transaction, ("$l", ledger.Sha256), ("$p", number));
            else Execute("""
                INSERT INTO stored_pages VALUES($l,$p,$bits,$n,$b)
                ON CONFLICT(ledger,page) DO UPDATE SET bits=$bits,stored_count=$n,stored_bytes=$b
                """, transaction, ("$l", ledger.Sha256), ("$p", number), ("$bits", page.Bits), ("$n", page.Count), ("$b", page.Bytes));
        }
        if (count < 0 || count > ledger.ObjectCount || bytes < 0 || bytes > ledger.TotalEncodedBytes || count == 0 && bytes != 0 ||
            count == ledger.ObjectCount && bytes != ledger.TotalEncodedBytes)
            throw StoredIntegrity();
        Execute("UPDATE stored_coverage SET stored_count=$n,stored_bytes=$b,scan_after=$a,through_seq=$s,updated_at=$t WHERE ledger=$l", transaction,
            ("$l", ledger.Sha256), ("$n", count), ("$b", bytes), ("$a", nextScanAfter), ("$s", nextThrough), ("$t", DateTimeOffset.UtcNow.ToString("O")));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>
    /// Stored means a verified receipt of the exact length, with no known corruption of those bytes and no missing
    /// observation since. Metadata receipts count too: Open's own metadata (configs, DL, EN) is not a DL member.
    /// </summary>
    private HashSet<string> StoredMembers(IReadOnlyList<StoredMember> members, SqliteTransaction transaction, CancellationToken token)
    {
        var stored = new HashSet<string>(StringComparer.Ordinal);
        if (members.Count == 0) return stored;
        using var command = Command("""
            SELECT m.k FROM (SELECT json_extract(value,'$[0]') k,json_extract(value,'$[1]') n FROM json_each($m)) m
            WHERE NOT EXISTS(SELECT 1 FROM object_absence a WHERE a.ekey=m.k)
              AND (EXISTS(SELECT 1 FROM encoded_objects e WHERE e.ekey=m.k AND e.encoded_bytes=m.n
                      AND NOT EXISTS(SELECT 1 FROM object_invalidity i WHERE i.sha256=e.sha256 AND i.byte_length=m.n))
                OR EXISTS(SELECT 1 FROM content_objects c WHERE c.ekey=m.k AND c.encoded_bytes=m.n
                      AND NOT EXISTS(SELECT 1 FROM object_invalidity i WHERE i.sha256=c.sha256 AND i.byte_length=m.n))
                OR EXISTS(SELECT 1 FROM metadata_objects x WHERE x.ekey=m.k AND x.encoded=m.n
                      AND NOT EXISTS(SELECT 1 FROM object_invalidity i WHERE i.sha256=json_extract(x.receipt,'$.sha256') AND i.byte_length=m.n)))
            """, transaction, ("$m", Serialize(members.Select(m => new object[] { m.EncodingKey, m.EncodedBytes }).ToArray())));
        using var reader = command.ExecuteReader();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); stored.Add(reader.GetString(0)); }
        return stored;
    }

    private sealed class StoredPage(byte[] bits, long count, long bytes)
    { internal byte[] Bits { get; } = bits; internal long Count { get; set; } = count; internal long Bytes { get; set; } = bytes; }
    private StoredPage ReadStoredPage(DownloadLedgerArtifact ledger, long number, SqliteTransaction transaction)
    {
        using var command = Command("SELECT bits,stored_count,stored_bytes,length(bits) FROM stored_pages WHERE ledger=$l AND page=$p", transaction, ("$l", ledger.Sha256), ("$p", number));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(new byte[StoredPageBits / 8], 0, 0);
        if (reader.GetInt64(3) != StoredPageBits / 8) throw StoredIntegrity();
        var page = new StoredPage(reader.GetFieldValue<byte[]>(0), reader.GetInt64(1), reader.GetInt64(2));
        if (page.Count != page.Bits.Sum(b => BitOperations.PopCount((uint)b)) || page.Bytes < 0) throw StoredIntegrity();
        var available = Math.Min(StoredPageBits, ledger.ObjectCount - number * StoredPageBits);
        for (var bit = available; bit < StoredPageBits; bit++)
            if ((page.Bits[bit / 8] & (1 << (int)(bit % 8))) != 0) throw StoredIntegrity();
        return page;
    }

    /// <summary>The ledger itself could not be read. Its figure is no longer maintained until a new admission resets it.</summary>
    internal Task FailStoredCoverageAsync(string ledger, RetentionProblem problem, CancellationToken token) => Schedule(() =>
    {
        ValidateRetentionProblem(problem);
        using var transaction = Db.BeginTransaction();
        if (Scalar("SELECT 1 FROM stored_coverage WHERE ledger=$l", transaction, ("$l", ledger)) is null) return false;
        ResetStoredCoverage(ledger, StoredSequence(transaction), DateTimeOffset.UtcNow.ToString("O"), Serialize(problem), transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>Removes at most 8192 changes every maintained ledger has applied. Returns the number removed.</summary>
    internal Task<int> PruneStoredChangesAsync(CancellationToken token) => Schedule(() =>
    {
        var floor = Scalar("SELECT MIN(through_seq) FROM stored_coverage WHERE problem IS NULL");
        var through = floor is null or DBNull ? StoredSequence(null) : Convert.ToInt64(floor, CultureInfo.InvariantCulture);
        return Changed("DELETE FROM stored_changes WHERE seq IN (SELECT seq FROM stored_changes WHERE seq<=$t ORDER BY seq LIMIT $n)", null,
            ("$t", through), ("$n", StoredPruneRows));
    }, token);

    Task<IReadOnlyList<BuildDownloadSummary>> IDownloadSummaryStore.ReadDownloadSummariesAsync(IReadOnlyList<BuildKey> builds, CancellationToken token) =>
        Schedule<IReadOnlyList<BuildDownloadSummary>>(() =>
    {
        DownloadSummaryService.Validate(builds);
        var result = new List<BuildDownloadSummary>(builds.Count);
        foreach (var build in builds)
        {
            token.ThrowIfCancellationRequested();
            result.Add(ReadDownloadSummary(build, token));
        }
        return result;
    }, token);

    private BuildDownloadSummary ReadDownloadSummary(BuildKey build, CancellationToken token)
    {
        (string, object?)[] key = [("$p", build.ProductId), ("$b", build.BuildIdentity)];
        if (Scalar("SELECT 1 FROM workspace_admissions WHERE build_id=$b AND product=$p LIMIT 1", null, key) is null)
            return new(build.ProductId, build.BuildIdentity, BuildDownloadState.NotOpened, null);
        // The newest measured admission names the ledger. Every region that admitted that same ledger shares its figure.
        if (Scalar("""
            SELECT d.artifact_sha256 FROM workspace_admissions a JOIN download_admissions d ON d.admission_id=a.id
            WHERE a.build_id=$b AND a.product=$p AND a.profile=$profile AND d.artifact_sha256 IS NOT NULL ORDER BY a.id DESC LIMIT 1
            """, null, [.. key, ("$profile", DownloadProfile)]) is not string ledger)
            return new(build.ProductId, build.BuildIdentity, BuildDownloadState.NotMeasured, null);
        var artifact = ReadDownloadArtifact(ledger, null) ?? throw StoredIntegrity();
        var regions = new List<string>();
        using (var command = Command("""
            SELECT DISTINCT a.region FROM workspace_admissions a JOIN download_admissions d ON d.admission_id=a.id
            WHERE a.build_id=$b AND a.product=$p AND a.profile=$profile AND d.artifact_sha256=$l ORDER BY a.region LIMIT $n
            """, null, [.. key, ("$profile", DownloadProfile), ("$l", ledger), ("$n", StoredRegionLimit)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) regions.Add(reader.GetString(0));
        var effective = artifact; var scope = new StoredScope(DownloadScope, 0, "manifestOnly", null);
        try
        {
            // The measured ROOT scope, once known, stands for the admitted manifest (D066).
            var root = ReadRootScope(build.ProductId, build.BuildIdentity, ledger, null);
            var measured = root.Ledger == ledger ? artifact : ReadDownloadArtifact(root.Ledger, null) ?? throw StoredIntegrity();
            (effective, scope) = (measured, new StoredScope(root.Scope, root.Unresolvable, root.State switch
            {
                "pending" => "measuring", "measured" => "measured", _ => "manifestOnly"
            }, root.Problem));
            return ReadMeasuredSummary(build, ledger, effective, regions, scope, token);
        }
        catch (Exception failure) when (failure is DiscoveryException { Code: "STORED_COVERAGE_INTEGRITY" or "RETENTION_STORAGE_INTEGRITY" } or System.Text.Json.JsonException)
        {
            // This build's saved measurement, figure or job state is inconsistent; the other builds in the read are unaffected.
            var code = failure is DiscoveryException typed ? typed.Code : "STORED_COVERAGE_INTEGRITY";
            return new(build.ProductId, build.BuildIdentity, BuildDownloadState.Measured, new(effective.Sha256, scope.Scope, effective.ObjectCount,
                effective.TotalEncodedBytes, 0, 0, StoredCountState.Unavailable, StoredFailure(code), DateTimeOffset.UtcNow.ToString("O"),
                new("notAssessed", null, 0, 0, 0), false, false, regions, scope.Unresolvable, scope.State, scope.Problem));
        }
    }

    private sealed record StoredScope(string Scope, long Unresolvable, string State, RetentionProblem? Problem);
    /// <summary>The figure of <paramref name="artifact"/> (the effective ledger); jobs are keyed by the admitted manifest.</summary>
    private BuildDownloadSummary ReadMeasuredSummary(BuildKey build, string manifest, DownloadLedgerArtifact artifact, List<string> regions, StoredScope scope, CancellationToken token)
    {
        var ledger = artifact.Sha256;
        (string, object?)[] key = [("$p", build.ProductId), ("$b", build.BuildIdentity)];
        long storedCount = 0, storedBytes = 0; var state = StoredCountState.Unavailable; string updatedAt; var pending = false;
        RetentionProblem? problem = new("STORED_COVERAGE_MISSING", "This build's stored figure has not been registered. Open the build again to count it.", true);
        using (var command = Command("""
            SELECT object_count,total_bytes,stored_count,stored_bytes,scan_after,updated_at,problem,length(CAST(problem AS BLOB)),length(CAST(updated_at AS BLOB)),through_seq
            FROM stored_coverage WHERE ledger=$l
            """, null, ("$l", ledger)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetInt64(0) != artifact.ObjectCount || reader.GetInt64(1) != artifact.TotalEncodedBytes || reader.GetInt64(8) > 40) throw StoredIntegrity();
                storedCount = reader.GetInt64(2); storedBytes = reader.GetInt64(3); updatedAt = reader.GetString(5);
                if (storedCount < 0 || storedCount > artifact.ObjectCount || storedBytes < 0 || storedBytes > artifact.TotalEncodedBytes ||
                    storedCount == 0 && storedBytes != 0 || storedCount == artifact.ObjectCount && storedBytes != artifact.TotalEncodedBytes || !DownloadUtc(updatedAt))
                    throw StoredIntegrity();
                // Until every file the build lists is measured, the figure is not the build's.
                if (reader.IsDBNull(6))
                {
                    problem = null; state = reader.IsDBNull(4) && scope.State != "measuring" ? StoredCountState.Current : StoredCountState.Counting;
                    // Recorded changes the worker has not applied yet: the figure may still move.
                    pending = reader.GetInt64(9) < StoredSequence(null);
                }
                else
                {
                    if (reader.GetInt64(7) > 2048) throw StoredIntegrity();
                    problem = Deserialize<RetentionProblem>(reader.GetString(6)); ValidateRetentionProblem(problem);
                }
            }
            else updatedAt = DateTimeOffset.UtcNow.ToString("O");
        }
        var jobs = new List<RetentionJob>();
        using (var command = Command("SELECT job_id,product,build_id,region,ledger,mode,active,queue_order,document,length(document) FROM retention_jobs WHERE product=$p AND build_id=$b AND ledger=$l ORDER BY region LIMIT $n", null,
            [.. key, ("$l", manifest), ("$n", StoredJobLimit)]))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) { token.ThrowIfCancellationRequested(); jobs.Add(ReadRetentionJobRow(reader)); }
        // A check in progress first, then the newest complete check, then an interrupted one.
        var check = jobs.FirstOrDefault(j => j.PresenceState == "assessing")
            ?? jobs.Where(j => j.PresenceState == "assessed").OrderByDescending(j => UtcInstant(j.AssessedAt!)).FirstOrDefault()
            ?? jobs.FirstOrDefault(j => j.PresenceState == "interrupted");
        var presence = check is null ? new BuildPresenceCheck("notAssessed", null, 0, 0, 0)
            : new BuildPresenceCheck(check.PresenceState, check.AssessedAt, check.Present + check.Missing + check.Corrupt, check.Missing, check.Corrupt);
        return new(build.ProductId, build.BuildIdentity, BuildDownloadState.Measured, new(ledger, scope.Scope, artifact.ObjectCount,
            artifact.TotalEncodedBytes, storedCount, storedBytes, state, problem, updatedAt, presence,
            jobs.Any(j => j.Mode == RetentionMode.DownloadAll && !j.Paused),
            jobs.Any(j => j.State is RetentionState.Preparing or RetentionState.Running), regions, scope.Unresolvable, scope.State, scope.Problem, pending));
    }

    internal static RetentionProblem StoredFailure(string code) => new(code,
        "This build's saved stored figure could not be kept up to date, so it is not shown. Open the build again to count it.", true);
    private static DiscoveryException StoredIntegrity() =>
        new("STORED_COVERAGE_INTEGRITY", "A saved stored-content figure is inconsistent with its download ledger.");
}
