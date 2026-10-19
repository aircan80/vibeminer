using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private readonly object retentionSessionsGate = new();
    private readonly Dictionary<RetentionDriverBinding, RetentionMembershipRegistration> retentionSessions = [];

    internal IAsyncDisposable RegisterRetentionLedgerSession(RetentionDriverBinding binding,
        Func<long, string, long, CancellationToken, Task<bool>> membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        lock (retentionSessionsGate)
        {
            if (retentionSessions.Count >= RetentionSlots || retentionSessions.ContainsKey(binding))
                throw RetentionIntegrity("A retention generation already owns its bounded ledger session.");
            var registration = new RetentionMembershipRegistration(this, binding, membership);
            retentionSessions.Add(binding, registration); return registration;
        }
    }
    private sealed class RetentionMembershipRegistration(DiscoveryCatalog owner, RetentionDriverBinding binding,
        Func<long, string, long, CancellationToken, Task<bool>> membership) : IAsyncDisposable
    {
        private int users; private bool closed;
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Borrow()
        {
            lock (owner.retentionSessionsGate) { if (closed) return false; users++; return true; }
        }
        internal Task<bool> Contains(RetentionMemberObservation observation, CancellationToken token) =>
            membership(observation.Ordinal, observation.EncodingKey, observation.EncodedBytes, token);
        internal void Return()
        {
            lock (owner.retentionSessionsGate) { users--; if (closed && users == 0) { owner.retentionSessions.Remove(binding); drained.TrySetResult(); } }
        }
        public ValueTask DisposeAsync()
        {
            lock (owner.retentionSessionsGate)
            {
                closed = true;
                if (users == 0) { owner.retentionSessions.Remove(binding); drained.TrySetResult(); }
                return new(drained.Task);
            }
        }
    }
    async Task<bool> IRetentionDriverCatalog.ApplyRetentionCreditAsync(RetentionDriverBinding binding,
        IReadOnlyList<RetentionMemberObservation> observations, long nextOrdinal, CancellationToken token)
    {
        if (observations is null || observations.Count is < 1 or > 64 || nextOrdinal < observations.Count)
            throw RetentionIntegrity("A retention credit batch requires at most 64 contiguous final members.");
        var batch = observations.ToArray();
        if (batch.Select(o => o.EncodingKey).Distinct(StringComparer.Ordinal).Count() != batch.Length)
            throw RetentionIntegrity("A sorted unique ledger cannot report duplicate encoded identities in one batch.");
        for (var i = 0; i < batch.Length; i++)
        {
            var observation = batch[i]; ValidateRetentionProblem(observation.Problem);
            if (observation.Ordinal != nextOrdinal - batch.Length + i || !DownloadHash(observation.EncodingKey, 32) ||
                observation.EncodedBytes < 0 || !Enum.IsDefined(observation.Presence))
                throw RetentionIntegrity("A retention credit batch has invalid exact members or noncontiguous ordinals.");
        }
        RetentionMembershipRegistration? session;
        lock (retentionSessionsGate)
        { if (!retentionSessions.TryGetValue(binding, out session) || !session.Borrow()) return false; }
        try
        {
            // Caller-held immutable ledger checks perform asynchronous I/O outside the SQL worker.
            foreach (var member in batch)
            { token.ThrowIfCancellationRequested(); if (!await session.Contains(member, token)) throw RetentionIntegrity("A reported member differs from the validated exact ledger ordinal."); }
            return await Schedule(() => ApplyRetentionBatch(binding, batch, nextOrdinal, token), token);
        }
        finally { session.Return(); }
    }
    private bool ApplyRetentionBatch(RetentionDriverBinding binding, RetentionMemberObservation[] batch, long nextOrdinal, CancellationToken token)
    {
        using var transaction = Db.BeginTransaction(); var job = CurrentRetentionJob(binding, transaction);
        if (job is null) return false;
        if (job.Cursor != nextOrdinal - batch.Length || nextOrdinal > job.Plan.Ledger.ObjectCount)
            throw RetentionIntegrity("A retention batch attempts to skip or replay a pass checkpoint.");
        var required = batch.Where(o => o.EncodedBytes is >= 9 and <= VerifiedObjectStore.MaxObjectBytes)
            .ToDictionary(o => o.EncodingKey, o => o.EncodedBytes, StringComparer.Ordinal);
        var receipts = ReadEncodedReceipts(required, transaction, token);
        var history = ReadRetentionCoverage(job.Plan.Ledger, transaction, validatePages: false);
        var pages = new Dictionary<long, RetentionBitPage>();
        long addedCount = 0, addedBytes = 0;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var problems = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM retention_problems WHERE job_id=$j", transaction, ("$j", job.JobId)));
        foreach (var observation in batch)
        {
            token.ThrowIfCancellationRequested();
            var receipt = receipts.GetValueOrDefault(observation.EncodingKey);
            if (receipt is not null)
            {
                var ordinal = observation.Ordinal; var number = ordinal / 4096;
                if (!pages.TryGetValue(number, out var page)) { page = ReadRetentionBitPage(job.Plan.Ledger, number, transaction); pages.Add(number, page); }
                var bit = (int)(ordinal % 4096); var mask = (byte)(1 << (bit % 8));
                if ((page.Bits[bit / 8] & mask) == 0)
                {
                    page.Bits[bit / 8] |= mask; page.Count++; page.Bytes = checked(page.Bytes + observation.EncodedBytes);
                    addedCount++; addedBytes = checked(addedBytes + observation.EncodedBytes);
                }
            }
            var presence = observation.Presence;
            if (receipt is not null && HasObjectInvalidity(receipt.Sha256, receipt.EncodedBytes, transaction)) presence = RetentionPresence.Corrupt;
            if (presence == RetentionPresence.Present && receipt is null) presence = RetentionPresence.Unknown;
            job = presence switch
            {
                RetentionPresence.Present => job with { Present = job.Present + 1, PresentBytes = checked(job.PresentBytes + observation.EncodedBytes) },
                RetentionPresence.Missing => job with { Missing = job.Missing + 1 },
                RetentionPresence.Corrupt => job with { Corrupt = job.Corrupt + 1 },
                _ when observation.Problem?.Retryable == false => job with { Failed = job.Failed + 1 },
                _ => job with { Unresolved = job.Unresolved + 1 }
            };
            if (presence != RetentionPresence.Present || observation.Problem is not null)
            {
                if (problems >= 10000) throw new DiscoveryException("RETENTION_PROBLEM_LIMIT", "This pass reached its bounded problem limit. Resume to start another assessment.");
                var problem = observation.Problem ?? new RetentionProblem(presence == RetentionPresence.Corrupt ? "LOCAL_OBJECT_CORRUPT" :
                    presence == RetentionPresence.Missing ? "LOCAL_OBJECT_MISSING" : "RETENTION_UNRESOLVED", "This declared object was not established as currently present.", true);
                Execute("INSERT INTO retention_problems VALUES($j,$g,$o,$p)", transaction, ("$j", job.JobId), ("$g", job.Generation), ("$o", observation.Ordinal), ("$p", Serialize(problem)));
                problems++; job = job with { Problem = problem };
            }
        }
        foreach (var (number, page) in pages)
            Execute("""
                INSERT INTO retention_bits VALUES($l,$p,$bits,$n,$b,$d)
                ON CONFLICT(ledger,page) DO UPDATE SET bits=$bits,receipt_count=$n,receipt_bytes=$b,digest=$d
                """, transaction, ("$l", job.Plan.Ledger.Sha256), ("$p", number), ("$bits", page.Bits), ("$n", page.Count), ("$b", page.Bytes),
                ("$d", RetentionPageDigest(job.Plan.Ledger.Sha256, number, page)));
        if (history.Count + addedCount > job.Plan.Ledger.ObjectCount || history.Bytes + addedBytes > job.Plan.Ledger.TotalEncodedBytes)
            throw RetentionIntegrity("Historical receipt credit exceeds the immutable declared scope.");
        Execute("UPDATE retention_coverage SET receipt_count=receipt_count+$n,receipt_bytes=receipt_bytes+$b,receipted_at=CASE WHEN $n>0 THEN $t ELSE receipted_at END WHERE ledger=$l", transaction,
            ("$n", addedCount), ("$b", addedBytes), ("$t", now), ("$l", job.Plan.Ledger.Sha256));
        job = job with { Cursor = nextOrdinal, State = RetentionState.Running };
        if (problems >= 10000) job = job with { Problem = new("RETENTION_PROBLEM_LIMIT", "This pass reached its bounded problem limit. Resume to start another assessment.", false) };
        SaveRetentionJob(job, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit();
        if (problems >= 10000) throw new DiscoveryException("RETENTION_PROBLEM_LIMIT", "This pass reached its bounded problem limit. Resume to start another assessment.");
        return true;
    }
    private sealed class RetentionBitPage(byte[] bits, long count, long bytes)
    { internal byte[] Bits { get; } = bits; internal long Count { get; set; } = count; internal long Bytes { get; set; } = bytes; }
    private void EnsureRetentionCoverage(RetentionLedger ledger, SqliteTransaction transaction)
    {
        Execute("INSERT OR IGNORE INTO retention_coverage VALUES($l,$n,$b,0,0,NULL)", transaction,
            ("$l", ledger.Sha256), ("$n", ledger.ObjectCount), ("$b", ledger.TotalEncodedBytes));
        _ = ReadRetentionCoverage(ledger, transaction);
    }
    private (long Count, long Bytes, string? At) ReadRetentionCoverage(RetentionLedger ledger, SqliteTransaction? transaction, bool validatePages = true)
    {
        long count, bytes; string? at;
        using (var command = Command("SELECT object_count,total_bytes,receipt_count,receipt_bytes,receipted_at FROM retention_coverage WHERE ledger=$l", transaction, ("$l", ledger.Sha256)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return (0, 0, null);
            count = reader.GetInt64(2); bytes = reader.GetInt64(3); at = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (reader.GetInt64(0) != ledger.ObjectCount || reader.GetInt64(1) != ledger.TotalEncodedBytes ||
                count < 0 || count > ledger.ObjectCount || bytes < 0 || bytes > ledger.TotalEncodedBytes || count == 0 && bytes != 0 || count > 0 && (at is null || !DownloadUtc(at)))
                throw RetentionIntegrity("Historical receipt totals differ from their immutable ledger geometry.");
        }
        if (!validatePages) return (count, bytes, at);
        // At most 2442 compact pages for the admitted 10-million-member bound. SQL aggregates
        // validate stored geometry/totals without projecting a complete receipt list on Home.
        using (var command = Command("""
            SELECT COALESCE(SUM(receipt_count),0),COALESCE(SUM(receipt_bytes),0),
              COALESCE(SUM(CASE WHEN page<0 OR page>=$pages OR length(bits)!=512 OR receipt_count<1 OR receipt_count>4096 OR receipt_bytes<0 OR length(digest)!=64 THEN 1 ELSE 0 END),0)
            FROM retention_bits WHERE ledger=$l
            """, transaction, ("$l", ledger.Sha256), ("$pages", (ledger.ObjectCount + 4095) / 4096)))
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetInt64(0) != count || reader.GetInt64(1) != bytes || reader.GetInt64(2) != 0)
                throw RetentionIntegrity("Historical bit-page geometry or aggregates are inconsistent.");
        if (ledger.ObjectCount > 0) _ = ReadRetentionBitPage(ledger, (ledger.ObjectCount - 1) / 4096, transaction);
        return (count, bytes, at);
    }
    private RetentionBitPage ReadRetentionBitPage(RetentionLedger ledger, long number, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT bits,receipt_count,receipt_bytes,digest FROM retention_bits WHERE ledger=$l AND page=$p", transaction, ("$l", ledger.Sha256), ("$p", number));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(new byte[512], 0, 0);
        var page = new RetentionBitPage((byte[])reader[0], reader.GetInt64(1), reader.GetInt64(2));
        if (page.Bits.Length != 512 || page.Count != page.Bits.Sum(b => BitOperations.PopCount((uint)b)) ||
            page.Bytes < 0 || reader.GetString(3) != RetentionPageDigest(ledger.Sha256, number, page))
            throw RetentionIntegrity("A historical receipt bit page failed integrity validation.");
        var available = Math.Min(4096, ledger.ObjectCount - number * 4096);
        for (var bit = available; bit < 4096; bit++)
            if ((page.Bits[bit / 8] & (1 << (int)(bit % 8))) != 0) throw RetentionIntegrity("A historical bit page credits an ordinal outside the declared ledger.");
        return page;
    }
    private static string RetentionPageDigest(string ledger, long number, RetentionBitPage page)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"{ledger}:{N(number)}:{N(page.Count)}:{N(page.Bytes)}:")); hash.AppendData(page.Bits);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
