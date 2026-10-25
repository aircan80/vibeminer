using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>The latest completed Check of the files a measured ledger adds to its manifest (D093).</summary>
internal sealed record DownloadCheckRecord(long Generation, string CheckedAt, long Checked, long Verified, long NotStored, long Missing, long Corrupt);

/// <summary>One object a Check found stored (verified), with a receipt but no file (missing), or with bad bytes (corrupt).</summary>
internal sealed record DownloadCheckObject(string EncodingKey, string State);

/// <summary>
/// Saved Checks (D093). One row per measured ledger names its completed generation and the generation a running Check
/// writes. Object rows are kept only for files with a local receipt; a file without one was not stored at the check. A
/// running Check's rows carry its staging generation, so a page never reads them, and a Check that is cancelled, fails or
/// is cut short by a restart leaves the completed one as it was.
/// </summary>
public sealed partial class DiscoveryCatalog
{
    internal static readonly string[] DownloadCheckStates = ["verified", "missing", "corrupt"];
    internal const int DownloadCheckWriteRows = 64;

    private void UpgradeDownloadChecks()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE download_checks(ledger TEXT PRIMARY KEY,manifest TEXT NOT NULL,generation INTEGER NOT NULL,staging INTEGER NOT NULL,
                checked_at TEXT,checked INTEGER NOT NULL,verified INTEGER NOT NULL,not_stored INTEGER NOT NULL,missing INTEGER NOT NULL,
                corrupt INTEGER NOT NULL,FOREIGN KEY(ledger) REFERENCES download_artifacts(sha256),FOREIGN KEY(manifest) REFERENCES download_artifacts(sha256));
            CREATE TABLE download_check_objects(ledger TEXT NOT NULL,generation INTEGER NOT NULL,ekey TEXT NOT NULL,state TEXT NOT NULL,
                PRIMARY KEY(ledger,generation,ekey),FOREIGN KEY(ledger) REFERENCES download_checks(ledger)) WITHOUT ROWID;
            PRAGMA user_version=20;
            """, transaction);
        transaction.Commit();
    }

    private DownloadCheckRecord? ReadDownloadCheck(string ledger, string manifest, SqliteTransaction? transaction)
    {
        using var command = Command("""
            SELECT manifest,generation,checked_at,checked,verified,not_stored,missing,corrupt,length(CAST(checked_at AS BLOB))
            FROM download_checks WHERE ledger=$l
            """, transaction, ("$l", ledger));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(1) == 0) return null;
        if (reader.GetString(0) != manifest || reader.IsDBNull(2) || reader.GetInt64(8) > 40 || !DownloadUtc(reader.GetString(2)))
            throw DownloadIntegrity("A saved check of stored files is inconsistent with its ledger.");
        var record = new DownloadCheckRecord(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            reader.GetInt64(6), reader.GetInt64(7));
        if (new[] { record.Checked, record.Verified, record.NotStored, record.Missing, record.Corrupt }.Any(value => value < 0) ||
            record.Verified + record.NotStored + record.Missing + record.Corrupt != record.Checked)
            throw DownloadIntegrity("A saved check of stored files has inconsistent counts.");
        return record;
    }

    /// <summary>
    /// Starts a Check of the measured ledger the request names: the exact admitted context, and a new staging generation.
    /// Rows a Check left unfinished (a restart in between) are removed first.
    /// </summary>
    internal Task<(StoredDownloadAdmission Saved, long Generation)> BeginDownloadCheckAsync(DownloadCheckRequest request, CancellationToken token) =>
        Schedule(() =>
        {
            DownloadService.Validate(request);
            var saved = ReadDownloadAdmission(request.ProductId, request.BuildIdentity, request.SourceRegion, request.LedgerId, token);
            if (saved.Measured is null)
                throw new DiscoveryException("DOWNLOAD_CHECK_NOT_NEEDED", "Open's assessment covered every object of this download manifest. Only a measured file list adds files to check.");
            using var transaction = Db.BeginTransaction();
            (string, object?)[] key = [("$l", saved.Measured.Sha256), ("$m", saved.Artifact.Sha256)];
            Execute("INSERT OR IGNORE INTO download_checks VALUES($l,$m,0,0,NULL,0,0,0,0,0)", transaction, key);
            if (Scalar("SELECT manifest FROM download_checks WHERE ledger=$l", transaction, key) as string != saved.Artifact.Sha256)
                throw DownloadIntegrity("A saved check of stored files names another manifest.");
            Execute("DELETE FROM download_check_objects WHERE ledger=$l AND generation<>(SELECT generation FROM download_checks WHERE ledger=$l)", transaction, key);
            Execute("UPDATE download_checks SET staging=MAX(generation,staging)+1 WHERE ledger=$l", transaction, key);
            var generation = Convert.ToInt64(Scalar("SELECT staging FROM download_checks WHERE ledger=$l", transaction, key), CultureInfo.InvariantCulture);
            token.ThrowIfCancellationRequested(); transaction.Commit();
            return (saved, generation);
        }, token);

    /// <summary>Saves up to 64 found objects under the running Check's staging generation. False when it was superseded.</summary>
    internal Task<bool> WriteDownloadCheckAsync(string ledger, long generation, IReadOnlyList<DownloadCheckObject> found, CancellationToken token) => Schedule(() =>
    {
        if (found.Count is < 1 or > DownloadCheckWriteRows || found.Any(item => !DownloadHash(item.EncodingKey, 32) || !DownloadCheckStates.Contains(item.State)))
            throw DownloadIntegrity("A check of stored files wrote an invalid batch.");
        using var transaction = Db.BeginTransaction();
        if (!CheckFenced(ledger, generation, transaction)) return false;
        foreach (var item in found)
            Execute("INSERT INTO download_check_objects VALUES($l,$g,$k,$s)", transaction, ("$l", ledger), ("$g", generation), ("$k", item.EncodingKey), ("$s", item.State));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>Makes the running Check the completed one and removes the rows of every other generation.</summary>
    internal Task<DownloadCheckRecord?> CompleteDownloadCheckAsync(string ledger, long generation, long @checked, long verified, long notStored,
        long missing, long corrupt, CancellationToken token) => Schedule(() =>
    {
        if (new[] { @checked, verified, notStored, missing, corrupt }.Any(value => value < 0) || verified + notStored + missing + corrupt != @checked)
            throw DownloadIntegrity("A check of stored files has inconsistent counts.");
        using var transaction = Db.BeginTransaction();
        if (!CheckFenced(ledger, generation, transaction)) return null;
        var written = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM download_check_objects WHERE ledger=$l AND generation=$g", transaction,
            ("$l", ledger), ("$g", generation)), CultureInfo.InvariantCulture);
        if (written != verified + missing + corrupt) throw DownloadIntegrity("A check of stored files lost some of its findings.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute("UPDATE download_checks SET generation=$g,checked_at=$t,checked=$c,verified=$v,not_stored=$n,missing=$x,corrupt=$k WHERE ledger=$l", transaction,
            ("$l", ledger), ("$g", generation), ("$t", now), ("$c", @checked), ("$v", verified), ("$n", notStored), ("$x", missing), ("$k", corrupt));
        Execute("DELETE FROM download_check_objects WHERE ledger=$l AND generation<>$g", transaction, ("$l", ledger), ("$g", generation));
        transaction.Commit();
        return new DownloadCheckRecord(generation, now, @checked, verified, notStored, missing, corrupt);
    }, CancellationToken.None);

    /// <summary>Removes a cancelled or failed Check's rows; the completed Check stays as it was.</summary>
    internal Task AbandonDownloadCheckAsync(string ledger, long generation) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        Execute("DELETE FROM download_check_objects WHERE ledger=$l AND generation=$g AND generation<>(SELECT generation FROM download_checks WHERE ledger=$l)",
            transaction, ("$l", ledger), ("$g", generation));
        transaction.Commit(); return true;
    }, CancellationToken.None);

    private bool CheckFenced(string ledger, long generation, SqliteTransaction transaction) =>
        Scalar("SELECT 1 FROM download_checks WHERE ledger=$l AND staging=$g AND generation<$g", transaction, ("$l", ledger), ("$g", generation)) is not null;

    /// <summary>What the completed Check found for these keys of one page (at most 100); a key without a row was not stored.</summary>
    internal Task<IReadOnlyDictionary<string, string>> ReadDownloadCheckObjectsAsync(string ledger, long generation, IReadOnlyList<string> keys,
        CancellationToken token) => Schedule<IReadOnlyDictionary<string, string>>(() =>
    {
        if (keys.Count > 128 || keys.Any(key => !DownloadHash(key, 32))) throw new DiscoveryException("INVALID_REQUEST", "Read at most 128 exact checked objects.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.Count == 0) return result;
        using var command = Command("""
            SELECT ekey,state FROM download_check_objects WHERE ledger=$l AND generation=$g AND ekey IN (SELECT value FROM json_each($keys))
            """, null, ("$l", ledger), ("$g", generation), ("$keys", Serialize(keys.ToArray())));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var state = reader.GetString(1);
            if (!DownloadCheckStates.Contains(state)) throw DownloadIntegrity("A saved check of stored files has an unknown state.");
            result[reader.GetString(0)] = state;
        }
        return result;
    }, token);
}
