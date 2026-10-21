using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One build's ROOT scope measurement waiting to run, fenced by its generation.</summary>
internal sealed record RootScopeWork(string ProductId, string BuildIdentity, string SourceRegion, DownloadLedgerArtifact Manifest,
    MetadataReceipt ManifestSource, long Generation, int Attempts);

/// <summary>The next measurement to run, if any, and how many unreadable saved rows were settled as failed on the way.</summary>
internal sealed record RootScopeNext(RootScopeWork? Work, int Settled);

/// <summary>
/// What a build's admitted manifest ledger stands for (D066). Open admits the manifest; the file list ROOT adds is measured
/// afterwards by <see cref="RootScopeWorker"/>. Until then the scope is <c>pending</c>; it ends <c>measured</c> (the ledger
/// with every ROOT-listed file), <c>manifestOnly</c> (ROOT cannot be read, with the reason) or, after three failed
/// attempts, <c>failed</c>, which the next Open of the build retries.
/// </summary>
internal sealed record RootScopeState(string State, string Ledger, string Scope, long Unresolvable, RetentionProblem? Problem);

public sealed partial class DiscoveryCatalog
{
    internal const int MaxRootScopeAttempts = 3;
    private readonly Channel<bool> rootScopeSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private const string RootScopeTable = """
        CREATE TABLE root_scopes(product TEXT NOT NULL,build_id TEXT NOT NULL,manifest TEXT NOT NULL,state TEXT NOT NULL,
            generation INTEGER NOT NULL,attempts INTEGER NOT NULL,ledger TEXT,unresolved INTEGER NOT NULL,problem TEXT,updated_at TEXT NOT NULL,
            PRIMARY KEY(product,build_id,manifest),FOREIGN KEY(manifest) REFERENCES download_artifacts(sha256),
            FOREIGN KEY(ledger) REFERENCES download_artifacts(sha256));
        """;

    internal async Task WaitForRootScopeAsync(CancellationToken token)
    {
        while (!rootScopeSignal.Reader.TryRead(out _))
            if (!await rootScopeSignal.Reader.WaitToReadAsync(token)) throw new OperationCanceledException(token);
    }

    /// <summary>
    /// Open of a manifest-scope build: a new measurement for a newly admitted manifest, or a retry of one that failed.
    /// A measured scope keeps its ledger and its figure is reset if it became unavailable; only when the measured ledger
    /// itself could not be read is the scope measured again, which republishes it. A manifest-only scope stays. The figure
    /// of whichever ledger now stands for the build is kept.
    /// </summary>
    private void EnsureRootScope(string product, string build, DownloadLedgerArtifact manifest, string now, SqliteTransaction transaction)
    {
        (string, object?)[] key = [("$p", product), ("$b", build), ("$m", manifest.Sha256), ("$t", now)];
        Execute("INSERT OR IGNORE INTO root_scopes VALUES($p,$b,$m,'pending',1,0,NULL,0,NULL,$t)", transaction, key);
        Execute("UPDATE root_scopes SET state='pending',generation=generation+1,attempts=0,problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b AND manifest=$m AND state='failed'",
            transaction, key);
        var root = ReadRootScope(product, build, manifest.Sha256, transaction);
        if (root.State == "measured")
        {
            var ledger = ReadDownloadArtifact(root.Ledger, transaction) ?? throw StoredIntegrity();
            if (!LedgerUnreadable(ledger.Sha256, transaction))
            {
                EnsureStoredCoverage(ledger, now, transaction);
                return;
            }
            ResetStoredCoverage(ledger.Sha256, StoredSequence(transaction), now, null, transaction);
            Execute("UPDATE root_scopes SET state='pending',generation=generation+1,attempts=0,ledger=NULL,unresolved=0,problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b AND manifest=$m",
                transaction, key);
        }
        EnsureStoredCoverage(manifest, now, transaction);
        rootScopeSignal.Writer.TryWrite(true);
    }

    /// <summary>The stored-figure worker found this ledger's own bytes missing or invalid, not merely a failed count.</summary>
    private bool LedgerUnreadable(string ledger, SqliteTransaction transaction)
    {
        if (Scalar("SELECT problem FROM stored_coverage WHERE ledger=$l AND problem IS NOT NULL AND length(CAST(problem AS BLOB))<=2048", transaction, ("$l", ledger)) is not string text)
            return false;
        try { return Deserialize<RetentionProblem>(text).Code is "LOCAL_OBJECT_MISSING" or "LOCAL_OBJECT_CORRUPT" or "DOWNLOAD_LEDGER_INTEGRITY" or "DOWNLOAD_LEDGER_INVALID"; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>
    /// After a measurement, the manifest's own figure is kept only while some admission still stands on the manifest itself.
    /// </summary>
    private void RetireManifestCoverage(string manifest, SqliteTransaction transaction)
    {
        var needed = Scalar("""
            SELECT 1 FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id
                LEFT JOIN root_scopes r ON r.product=a.product AND r.build_id=a.build_id AND r.manifest=d.artifact_sha256
                WHERE d.artifact_sha256=$m AND (r.state IS NULL OR r.state<>'measured')
            UNION ALL SELECT 1 FROM root_scopes WHERE ledger=$m AND state='measured' LIMIT 1
            """, transaction, ("$m", manifest));
        if (needed is not null) return;
        Execute("DELETE FROM stored_pages WHERE ledger=$l", transaction, ("$l", manifest));
        Execute("DELETE FROM stored_coverage WHERE ledger=$l", transaction, ("$l", manifest));
    }

    internal async Task<RootScopeWork?> ReadNextRootScopeAsync(CancellationToken token) => (await ClaimNextRootScopeAsync(token)).Work;

    /// <summary>
    /// The oldest pending measurement with the region and manifest source of the build's newest admission of it. A pending
    /// row whose saved inputs cannot be read is settled as failed (the next Open retries it) rather than read again, so it
    /// never holds back later builds.
    /// </summary>
    internal Task<RootScopeNext> ClaimNextRootScopeAsync(CancellationToken token) => Schedule(() =>
    {
        var settled = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var row = new RootScopeRow();
            try { return new RootScopeNext(ReadNextRootScope(row), settled); }
            catch (Exception failure) when (failure is not OperationCanceledException && row.Product is not null)
            {
                var problem = new RetentionProblem(failure is DiscoveryException typed ? typed.Code : "ROOT_SCOPE_INTEGRITY",
                    "This build's saved file-list measurement could not be read. Opening the build again retries.", true);
                // Exactly the row just read; it leaves pending, so the loop moves on to the next one.
                if (Changed("UPDATE root_scopes SET state='failed',attempts=$a,problem=$problem,updated_at=$t WHERE product=$p AND build_id=$b AND manifest=$m AND state='pending' AND generation=$g",
                    null, ("$a", MaxRootScopeAttempts), ("$problem", Serialize(problem)), ("$t", DateTimeOffset.UtcNow.ToString("O")),
                    ("$p", row.Product), ("$b", row.Build), ("$m", row.Manifest), ("$g", row.Generation)) != 1) throw;
                settled++;
            }
        }
    }, token);

    private sealed class RootScopeRow { internal string? Product, Build, Manifest; internal long Generation; }
    private RootScopeWork? ReadNextRootScope(RootScopeRow row)
    {
        using var command = Command("""
            SELECT r.product,r.build_id,r.manifest,r.generation,r.attempts,
                (SELECT a.region FROM workspace_admissions a JOIN download_admissions d ON d.admission_id=a.id
                    WHERE a.build_id=r.build_id AND a.product=r.product AND d.artifact_sha256=r.manifest ORDER BY a.id DESC LIMIT 1),
                (SELECT d.metadata FROM workspace_admissions a JOIN download_admissions d ON d.admission_id=a.id
                    WHERE a.build_id=r.build_id AND a.product=r.product AND d.artifact_sha256=r.manifest AND length(d.metadata)<=32768 ORDER BY a.id DESC LIMIT 1)
            FROM root_scopes r WHERE r.state='pending' ORDER BY r.updated_at,r.build_id LIMIT 1
            """);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var (p, b, m, g) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3));
        (row.Product, row.Build, row.Manifest, row.Generation) = (p, b, m, g);
        if (reader.IsDBNull(5) || reader.IsDBNull(6)) throw StoredIntegrity();
        var manifest = ReadDownloadArtifact(m, null) ?? throw StoredIntegrity();
        var source = Deserialize<MetadataReceipt[]>(reader.GetString(6)).SingleOrDefault(receipt => receipt.Role == "download") ?? throw StoredIntegrity();
        return new RootScopeWork(p, b, reader.GetString(5), manifest, source, g, reader.GetInt32(4));
    }

    /// <summary>Publishes the measured scope. False when the measurement was superseded and nothing was written.</summary>
    internal Task<bool> CompleteRootScopeAsync(RootScopeWork work, DownloadLedgerArtifact ledger, DownloadScopeBinding binding, CancellationToken token) => Schedule(() =>
    {
        ValidateScopeBinding(binding);
        if (binding.Scope != DownloadRootScope) throw StoredIntegrity();
        using var transaction = Db.BeginTransaction();
        if (!Fenced(work, transaction)) return false;
        var bound = Scalar("""
            SELECT 1 FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r AND root_sha256=$root
                AND encoding_sha256=$en AND artifact_sha256=$a AND scope=$scope AND root_unresolved=$u
            """, transaction, ("$s", work.ManifestSource.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision), ("$root", binding.RootSha256),
            ("$en", binding.EncodingSha256), ("$a", ledger.Sha256), ("$scope", binding.Scope), ("$u", binding.UnresolvableRootContent));
        if (bound is null || ReadDownloadArtifact(ledger.Sha256, transaction) != ledger) throw StoredIntegrity();
        var now = DateTimeOffset.UtcNow.ToString("O");
        Execute("UPDATE root_scopes SET state='measured',ledger=$l,unresolved=$u,problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b AND manifest=$m", transaction,
            ("$l", ledger.Sha256), ("$u", binding.UnresolvableRootContent), ("$t", now), ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$m", work.Manifest.Sha256));
        EnsureStoredCoverage(ledger, now, transaction);
        if (ledger.Sha256 != work.Manifest.Sha256) RetireManifestCoverage(work.Manifest.Sha256, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>
    /// Records an unreadable file list (terminal, manifest only) or a failed attempt, which becomes terminal after three.
    /// Returns the new state (pending while attempts remain), or null when the measurement was superseded.
    /// </summary>
    internal Task<string?> FailRootScopeAsync(RootScopeWork work, bool unreadable, RetentionProblem problem, CancellationToken token) => Schedule<string?>(() =>
    {
        ValidateRetentionProblem(problem);
        using var transaction = Db.BeginTransaction();
        if (!Fenced(work, transaction)) return null;
        var attempts = work.Attempts + (unreadable ? 0 : 1);
        var state = unreadable ? "manifestOnly" : attempts >= MaxRootScopeAttempts ? "failed" : "pending";
        Execute("UPDATE root_scopes SET state=$s,attempts=$a,problem=$problem,updated_at=$t WHERE product=$p AND build_id=$b AND manifest=$m", transaction,
            ("$s", state), ("$a", attempts), ("$problem", Serialize(problem)), ("$t", DateTimeOffset.UtcNow.ToString("O")),
            ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$m", work.Manifest.Sha256));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return state;
    }, token);

    private bool Fenced(RootScopeWork work, SqliteTransaction transaction) => Scalar(
        "SELECT 1 FROM root_scopes WHERE product=$p AND build_id=$b AND manifest=$m AND state='pending' AND generation=$g AND attempts=$a", transaction,
        ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$m", work.Manifest.Sha256), ("$g", work.Generation), ("$a", work.Attempts)) is not null;

    /// <summary>The ledger that stands for a build's admitted manifest now: the measured one, or the manifest itself.</summary>
    private RootScopeState ReadRootScope(string product, string build, string manifest, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT state,ledger,unresolved,problem,length(CAST(problem AS BLOB)) FROM root_scopes WHERE product=$p AND build_id=$b AND manifest=$m",
            transaction, ("$p", product), ("$b", build), ("$m", manifest));
        using var reader = command.ExecuteReader();
        // An admission without a measurement row (none is written for a ledger already carrying ROOT) stands for itself.
        if (!reader.Read()) return new("manifestOnly", manifest, DownloadScope, 0, null);
        var state = reader.GetString(0);
        RetentionProblem? problem = null;
        if (!reader.IsDBNull(3))
        {
            if (reader.GetInt64(4) > 2048) throw StoredIntegrity();
            problem = Deserialize<RetentionProblem>(reader.GetString(3)); ValidateRetentionProblem(problem);
        }
        if (state == "measured")
        {
            if (reader.IsDBNull(1) || !DownloadHash(reader.GetString(1), 64) || reader.GetInt64(2) < 0) throw StoredIntegrity();
            return new(state, reader.GetString(1), DownloadRootScope, reader.GetInt64(2), null);
        }
        if (state is not ("pending" or "manifestOnly" or "failed")) throw StoredIntegrity();
        return new(state, manifest, DownloadScope, 0, problem);
    }
    internal Task<string> ReadRootScopeStateForTestAsync(string product, string build, string manifest, CancellationToken token) =>
        Schedule(() => ReadRootScope(product, build, manifest, null).State, token);
}
