using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    internal Task<IReadOnlySet<string>> ReadKnownCorruptAsync(IReadOnlyList<(string EncodingKey, long EncodedBytes)> requests,
        CancellationToken token)
    {
        var required = ValidateEncodedRequests(requests);
        return Schedule<IReadOnlySet<string>>(() =>
        {
            var receipts = ReadEncodedReceipts(required, null, token); var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var receipt in receipts.Values)
                if (HasObjectInvalidity(receipt.Sha256, receipt.EncodedBytes, null)) result.Add(receipt.EncodingKey);
            return result;
        }, token);
    }

    internal Task RecordEncodedInvalidityAsync(string encodingKey, long encodedBytes, string sha256, string reason,
        CancellationToken token) => Schedule(() =>
    {
        ValidateObjectInvalidity(sha256, encodedBytes, reason);
        using var transaction = Db.BeginTransaction();
        RequireInvalidityAlias(encodingKey, encodedBytes, sha256, transaction, token);
        RecordObjectInvalidity(sha256, encodedBytes, reason, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    // CAS adapters report a mismatch against an app-owned immutable object's expected identity.
    // This records current invalidity; historical receipts and original provenance remain intact.
    public Task RecordObjectInvalidityAsync(string sha256, long byteLength, string reason, CancellationToken token) => Schedule(() =>
    {
        ValidateObjectInvalidity(sha256, byteLength, reason);
        using var transaction = Db.BeginTransaction(); RecordObjectInvalidity(sha256, byteLength, reason, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    // Only current whole SHA + encoded BLTE verification followed by durable receipt publication
    // authorizes this explicit call. Historical receipt conversion/stat checks never invoke it.
    internal Task<long> CaptureObjectIntegrityEpochAsync(CancellationToken token) =>
        Schedule(() => CurrentObjectIntegrityEpoch(null), token);

    internal Task ClearKnownCorruptAsync(string encodingKey, long encodedBytes, string sha256, long verifiedThroughEpoch, CancellationToken token) => Schedule(() =>
    {
        ValidateObjectInvalidity(sha256, encodedBytes, "LOCAL_OBJECT_CORRUPT");
        using var transaction = Db.BeginTransaction(); RequireInvalidityAlias(encodingKey, encodedBytes, sha256, transaction, token);
        if (verifiedThroughEpoch < 0 || verifiedThroughEpoch > CurrentObjectIntegrityEpoch(transaction))
            throw RetentionIntegrity("A current verification cannot clear an unknown future corruption epoch.");
        Execute("DELETE FROM object_invalidity WHERE sha256=$h AND byte_length=$n AND revision<=$v", transaction,
            ("$h", sha256), ("$n", encodedBytes), ("$v", verifiedThroughEpoch));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    private bool HasObjectInvalidity(string sha256, long bytes, Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        using var command = Command("SELECT reason,observed_at,revision FROM object_invalidity WHERE sha256=$h AND byte_length=$n", transaction,
            ("$h", sha256), ("$n", bytes));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        var reason = reader.GetString(0); var observedAt = reader.GetString(1); var revision = reader.GetInt64(2); reader.Close();
        ValidateObjectInvalidity(sha256, bytes, reason);
        if (!DownloadUtc(observedAt) || revision < 1 || revision > CurrentObjectIntegrityEpoch(transaction))
            throw RetentionIntegrity("A corruption journal observation has invalid durable epoch or time.");
        return true;
    }

    private void RequireInvalidityAlias(string encodingKey, long encodedBytes, string sha256,
        Microsoft.Data.Sqlite.SqliteTransaction transaction, CancellationToken token)
    {
        var required = ValidateEncodedRequests([(encodingKey, encodedBytes)]);
        var receipt = ReadEncodedReceipts(required, transaction, token).GetValueOrDefault(encodingKey);
        if (receipt is null || receipt.Sha256 != sha256) throw RetentionIntegrity("A current invalidity observation differs from the exact durable encoded receipt.");
    }
    private long CurrentObjectIntegrityEpoch(Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        var value = Scalar("SELECT value FROM object_invalidity_sequence WHERE id=1", transaction);
        if (value is not long epoch || epoch < 0 || epoch == long.MaxValue) throw RetentionIntegrity("The corruption journal epoch is invalid or exhausted.");
        return epoch;
    }
    private void RecordObjectInvalidity(string sha256, long byteLength, string reason, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var revision = checked(CurrentObjectIntegrityEpoch(transaction) + 1);
        Execute("UPDATE object_invalidity_sequence SET value=$v WHERE id=1", transaction, ("$v", revision));
        Execute("""
            INSERT INTO object_invalidity VALUES($h,$n,$r,$t,$v)
            ON CONFLICT(sha256,byte_length) DO UPDATE SET reason=$r,observed_at=$t,revision=$v
            """, transaction, ("$h", sha256), ("$n", byteLength), ("$r", reason), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$v", revision));
    }
    private static void ValidateObjectInvalidity(string sha256, long byteLength, string reason)
    {
        if (!DownloadHash(sha256, 64) || byteLength is < 1 or > VerifiedObjectStore.MaxObjectBytes ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 64 || reason.Any(ch => ch is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) ||
            reason is "LOCAL_OBJECT_MISSING" or "OBJECT_NOT_FOUND")
            throw RetentionIntegrity("A current corruption observation requires an exact bounded object identity and explicit corruption cause.");
    }
}
