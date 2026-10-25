using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One build's file catalog preparation waiting to run, fenced by its generation and attempt count.</summary>
internal sealed record FileCatalogWork(string ProductId, string BuildIdentity, string SourceRegion, long Generation, int Attempts,
    long? Inventory, long Opened);

/// <summary>The next preparation to run, if any, and how many unreadable saved rows were settled as failed on the way.</summary>
internal sealed record FileCatalogNext(FileCatalogWork? Work, int Settled);

/// <summary>One ROOT or install membership of a file catalog, fully resolved before it is written (D071).</summary>
internal sealed record FileCatalogRow(long Id, string Family, string? Name, string ContentKey, string? EncodingKey, long? EncodedBytes,
    long? DecodedBytes, long? DeclaredDecoded, long? FileDataId, long? LocaleFlags, long? ContentFlags, string? NameHash, string Raw, long? Group);

/// <summary>Planner statistics computed while the rows were derived; written at seal.</summary>
internal sealed record FileCatalogStats(long RootFiles, long InstallFiles, long RootMembers, long InstallMembers, long UniqueContent,
    long UnresolvedMembers, long StoredMembers, long StoredRootMembers, IReadOnlyList<(uint Locale, uint Flags, long Members)> Variants);

/// <summary>A saved problem: the typed code and message shown, and the underlying cause kept for diagnosis.</summary>
internal sealed record FileCatalogFailure(string Code, string Message, bool Retryable, string? Detail = null);

/// <summary>Whether a running preparation may continue its next step.</summary>
internal enum FileCatalogTurn { Run, Superseded, Paused, Yield }

/// <summary>
/// Files after Open (D071). Opening a build queues its ROOT and install memberships; <see cref="FileCatalogWorker"/> builds
/// them in the background into a generation of profile <see cref="FileCatalogProfile"/>. Its rows live in a SQLite file of
/// their own under <see cref="FileCatalogFolder"/>, attached to the catalog connection while they are written or read, so a
/// retail build's 1.2 GB never counts against the main catalog's bound and removing a catalog is removing one file. The
/// main catalog keeps only <c>file_catalogs</c> (one row per build: <c>pending</c> with its stage, <c>stopped</c> by the
/// user, <c>ready</c>, <c>failed</c> after three attempts or <c>unsupported</c>) and the generation's registry row. Every
/// write is fenced by the row's generation and attempt count, so a result for a preparation that was reset meanwhile is
/// discarded. The build opened most recently goes first; a build whose Download all is paused waits.
/// </summary>
public sealed partial class DiscoveryCatalog
{
    /// <summary>A ROOT/install-only membership catalog: no DL, DS or EN rows, and unresolved memberships kept explicit.</summary>
    public const string FileCatalogProfile = "files-root-install-v1";
    internal const string FileCatalogFolder = "file-catalogs";
    internal const string FileCatalogRevision = "file-catalog-file-v1";
    internal const int MaxFileCatalogAttempts = 3;
    /// <summary>Memberships written per catalog operation. Each batch, its stored marks and the file's own position are one transaction.</summary>
    internal const int FileCatalogBatchRows = 1024;
    /// <summary>One file catalog file stops growing at 4 GiB (1,048,576 pages of 4 KiB); retail needs about 1.2 GB.</summary>
    internal const long FileCatalogMaxPages = 1_048_576;
    /// <summary>File catalog files attached to the catalog connection at once; the least recently used is detached first.</summary>
    internal const int FileCatalogAttachments = 3;
    /// <summary>Activity lists at most this many preparations.</summary>
    internal const int FileCatalogListBound = 32;
    private const int FileCatalogSummaryBytes = 8192;
    internal static readonly string[] FileCatalogStages = ["queued", "acquiring", "waiting", "reading", "writing", "sealing"];

    private readonly Channel<bool> fileCatalogSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly LinkedList<long> attachedCatalogs = new();
    /// <summary>Raised after a user stop or free (D085) commits, with product and build, so a running preparation of that build stops now.</summary>
    internal event Action<string, string>? FileCatalogStopped;

    private void UpgradeFileCatalogs()
    {
        using var tx = Db.BeginTransaction();
        Execute("""
            CREATE TABLE file_catalogs(product TEXT NOT NULL,build_id TEXT NOT NULL,state TEXT NOT NULL,generation INTEGER NOT NULL,
                attempts INTEGER NOT NULL,inventory INTEGER,written INTEGER NOT NULL,total INTEGER,stage TEXT NOT NULL,problem TEXT,
                opened INTEGER NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(product,build_id),FOREIGN KEY(build_id) REFERENCES builds(id),
                FOREIGN KEY(inventory) REFERENCES inventory_generations(id));
            CREATE INDEX file_catalog_queue ON file_catalogs(state,opened);
            PRAGMA user_version=18;
            """, tx);
        tx.Commit();
    }

    internal string FileCatalogDirectory => fileCatalogDirectory;
    internal string FileCatalogPath(long generation) => Path.Combine(fileCatalogDirectory, $"files-{generation.ToString(CultureInfo.InvariantCulture)}.sqlite3");
    private static string FileCatalogSchema(long generation) => "fc" + generation.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Attaches one generation's file to the catalog connection, outside any transaction, and returns its schema name.
    /// Only the worker thread runs this, so the least recently used attachment can be detached safely.
    /// </summary>
    private string AttachFileCatalog(long generation, bool create)
    {
        var schema = FileCatalogSchema(generation);
        var node = attachedCatalogs.Find(generation);
        if (node is not null) { attachedCatalogs.Remove(node); attachedCatalogs.AddFirst(node); return schema; }
        var path = FileCatalogPath(generation);
        if (!create && !File.Exists(path)) throw new DiscoveryException("FILE_CATALOG_MISSING", "The saved file list is missing. Opening the build again prepares it.", true);
        Directory.CreateDirectory(fileCatalogDirectory);
        while (attachedCatalogs.Count >= FileCatalogAttachments) DetachFileCatalog(attachedCatalogs.Last!.Value);
        Execute($"ATTACH DATABASE $path AS {schema}", null, ("$path", path));
        attachedCatalogs.AddFirst(generation);
        Execute($"PRAGMA {schema}.journal_mode=WAL; PRAGMA {schema}.synchronous=FULL; PRAGMA {schema}.max_page_count={FileCatalogMaxPages}; PRAGMA {schema}.cache_size=-16384;");
        return schema;
    }
    private void DetachFileCatalog(long generation)
    {
        if (attachedCatalogs.Find(generation) is not { } node) return;
        attachedCatalogs.Remove(node);
        Execute($"DETACH DATABASE {FileCatalogSchema(generation)}");
    }
    private void DeleteFileCatalogFile(long generation)
    {
        DetachFileCatalog(generation);
        if (!Directory.Exists(fileCatalogDirectory)) return;
        var path = FileCatalogPath(generation);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
    }

    /// <summary>The file's own identity: which generation and build it holds, from which sources, how far written, sealed or not.</summary>
    private sealed record FileCatalogIdentity(long Generation, string Build, string Sources, long Written, bool Sealed);
    private FileCatalogIdentity? ReadFileCatalogIdentity(string schema, SqliteTransaction? transaction)
    {
        if (Scalar($"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name='catalog_identity'", transaction) is null) return null;
        using var command = Command($"SELECT revision,generation,build_id,sources,written,sealed,length(CAST(sources AS BLOB)) FROM {schema}.catalog_identity WHERE id=1", transaction);
        using var r = command.ExecuteReader();
        if (!r.Read() || r.GetString(0) != FileCatalogRevision || r.GetInt64(6) > 4096) return null;
        return new(r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5) == 1);
    }

    private void CreateFileCatalogSchema(string schema, long generation, string build, string sources, SqliteTransaction transaction)
    {
        Execute($"""
            CREATE TABLE {schema}.catalog_identity(id INTEGER PRIMARY KEY CHECK(id=1),revision TEXT NOT NULL,generation INTEGER NOT NULL,
                build_id TEXT NOT NULL,sources TEXT NOT NULL,written INTEGER NOT NULL,sealed INTEGER NOT NULL);
            CREATE TABLE {schema}.inventory_origins(g INTEGER NOT NULL,id INTEGER NOT NULL,family TEXT NOT NULL,name TEXT,fdid TEXT,
                ckey TEXT,reference TEXT,width INTEGER NOT NULL,declared_encoded INTEGER,declared_decoded INTEGER,
                locale TEXT,flags TEXT,name_hash TEXT,raw TEXT NOT NULL,ekey TEXT,encoded INTEGER,decoded INTEGER,
                fdid_n INTEGER,locale_n INTEGER,flags_n INTEGER,grp INTEGER,stored INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(g,id));
            CREATE INDEX {schema}.inventory_content ON inventory_origins(g,ckey,family,fdid_n);
            CREATE INDEX {schema}.inventory_root_files ON inventory_origins(g,fdid_n,locale_n,flags_n,stored) WHERE fdid_n IS NOT NULL;
            CREATE INDEX {schema}.inventory_root_variant_files ON inventory_origins(g,locale_n,flags_n,fdid_n) WHERE fdid_n IS NOT NULL;
            CREATE INDEX {schema}.inventory_stored_files ON inventory_origins(g,family,fdid_n,locale_n,flags_n) WHERE stored=1;
            CREATE INDEX {schema}.inventory_install_files ON inventory_origins(g,grp,id) WHERE family='install';
            CREATE TABLE {schema}.inventory_root_variants(g INTEGER NOT NULL,locale INTEGER NOT NULL,flags INTEGER NOT NULL,members INTEGER NOT NULL,
                PRIMARY KEY(g,locale,flags));
            CREATE TABLE {schema}.inventory_file_stats(g INTEGER PRIMARY KEY,root_files INTEGER NOT NULL,install_files INTEGER NOT NULL,
                root_members INTEGER NOT NULL,variants INTEGER NOT NULL,stored_root_members INTEGER NOT NULL);
            """, transaction);
        Execute($"INSERT INTO {schema}.catalog_identity VALUES(1,$r,$g,$b,$s,0,0)", transaction,
            ("$r", FileCatalogRevision), ("$g", generation), ("$b", build), ("$s", sources));
    }

    private int fileCatalogWaiters;
    /// <summary>Waits for the file catalog wake-up now running. Tests and diagnostics only: the worker keeps at most one.</summary>
    internal int FileCatalogWaiters => Volatile.Read(ref fileCatalogWaiters);

    /// <summary>
    /// Waits for the next wake-up and takes it. The signal has one slot, so a caller must not leave a wait running once it
    /// stops caring: cancel it, or it takes the wake-up the next wait needs.
    /// </summary>
    internal async Task WaitForFileCatalogAsync(CancellationToken token)
    {
        Interlocked.Increment(ref fileCatalogWaiters);
        try
        {
            while (!fileCatalogSignal.Reader.TryRead(out _))
                if (!await fileCatalogSignal.Reader.WaitToReadAsync(token)) throw new OperationCanceledException(token);
        }
        finally { Interlocked.Decrement(ref fileCatalogWaiters); }
    }
    /// <summary>A Download all pause, resume or mode change may let a waiting preparation run, or pause a running one.</summary>
    private void WakeFileCatalogs() => fileCatalogSignal.Writer.TryWrite(true);

    private const string FileCatalogPaused = """
        EXISTS(SELECT 1 FROM retention_jobs j WHERE j.product=f.product AND j.build_id=f.build_id AND j.mode=1
            AND json_extract(j.document,'$.paused')=1)
        """;

    /// <summary>
    /// Inside an Open's admission transaction: queue the build's file catalog unless one is pending or ready, and make it the
    /// most recently opened, so it goes first. A failed, unsupported, stopped or removed (D085) one is queued again; its
    /// staging file, if any, stays for the next attempt to check and continue. A ready one whose file or registry row is gone is queued from
    /// the start.
    /// </summary>
    private void EnsureFileCatalog(string product, string build, string now, SqliteTransaction transaction)
    {
        var opened = Convert.ToInt64(Scalar("SELECT coalesce(max(opened),0)+1 FROM file_catalogs", transaction));
        (string, object?)[] key = [("$p", product), ("$b", build), ("$t", now), ("$o", opened)];
        Execute("INSERT OR IGNORE INTO file_catalogs VALUES($p,$b,'pending',1,0,NULL,0,NULL,'queued',NULL,$o,$t)", transaction, key);
        Execute("UPDATE file_catalogs SET opened=$o WHERE product=$p AND build_id=$b", transaction, key);
        Execute("UPDATE file_catalogs SET state='pending',generation=generation+1,attempts=0,stage='queued',problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b AND state IN ('failed','unsupported','stopped','removed')",
            transaction, key);
        if (Scalar("SELECT inventory FROM file_catalogs WHERE product=$p AND build_id=$b AND state='ready'", transaction, key) is long ready &&
            (Scalar("SELECT 1 FROM inventory_generations WHERE id=$g AND build_id=$b AND state='ready' AND profile=$profile", transaction,
                ("$g", ready), ("$b", build), ("$profile", FileCatalogProfile)) is null || !FileCatalogSealedOnDisk(ready, build)))
        {
            Execute("UPDATE inventory_generations SET state='abandoned' WHERE id=$g AND profile=$profile", transaction, ("$g", ready), ("$profile", FileCatalogProfile));
            Execute("UPDATE file_catalogs SET state='pending',generation=generation+1,attempts=0,inventory=NULL,written=0,total=NULL,stage='queued',problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b",
                transaction, key);
        }
        WakeFileCatalogs();
    }

    /// <summary>
    /// Whether a ready generation's file reads back as that sealed generation of the build. Read on its own read-only
    /// connection, because an Open's admission transaction cannot attach it; any failure to read it counts as not sealed.
    /// </summary>
    private bool FileCatalogSealedOnDisk(long generation, string build)
    {
        var path = FileCatalogPath(generation);
        if (!File.Exists(path)) return false;
        try
        {
            using var file = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            file.Open();
            using var command = file.CreateCommand();
            command.CommandText = "SELECT revision,generation,build_id,sealed FROM catalog_identity WHERE id=1";
            using var r = command.ExecuteReader();
            return r.Read() && r.GetString(0) == FileCatalogRevision && r.GetInt64(1) == generation && r.GetString(2) == build && r.GetInt64(3) == 1;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or InvalidCastException)
        {
            EngineLog.Write($"File catalog {generation} could not be read back on Open and is prepared again ({Cause(failure)}).");
            return false;
        }
    }

    /// <summary>Stages written by a run that ended without settling are stale; a new run starts every pending row queued.</summary>
    internal Task ResetFileCatalogStagesAsync(CancellationToken token) => Schedule(() =>
    {
        Execute("UPDATE file_catalogs SET stage='queued' WHERE state='pending' AND stage<>'queued'"); return true;
    }, token);

    /// <summary>Drops a saved staging generation reference so the next attempt starts from the beginning; cleanup removes its file.</summary>
    private void AbandonFileCatalogGeneration(string product, string build, SqliteTransaction? transaction)
    {
        Execute("UPDATE inventory_generations SET state='abandoned' WHERE id=(SELECT inventory FROM file_catalogs WHERE product=$p AND build_id=$b) AND profile=$profile AND state<>'ready'",
            transaction, ("$p", product), ("$b", build), ("$profile", FileCatalogProfile));
        Execute("UPDATE file_catalogs SET inventory=NULL,written=0,total=NULL WHERE product=$p AND build_id=$b", transaction, ("$p", product), ("$b", build));
    }

    /// <summary>
    /// The pending preparation of the build opened most recently, unless its Download all is paused, with the region of that
    /// build's newest Open. A staging reference that no longer names a staging generation of this build is dropped (the
    /// attempt starts from the beginning); a row that cannot be read at all is settled failed so it never holds back others.
    /// </summary>
    internal Task<FileCatalogNext> ClaimNextFileCatalogAsync(CancellationToken token) => Schedule(() =>
    {
        var settled = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string? product = null, build = null; long generation = 0;
            try
            {
                using var command = Command($"""
                    SELECT f.product,f.build_id,f.generation,f.attempts,f.inventory,f.opened,
                        (SELECT a.region FROM workspace_admissions a WHERE a.product=f.product AND a.build_id=f.build_id ORDER BY a.id DESC LIMIT 1),
                        (SELECT i.state||'|'||i.profile||'|'||i.build_id FROM inventory_generations i WHERE i.id=f.inventory)
                    FROM file_catalogs f INDEXED BY file_catalog_queue WHERE f.state='pending' AND NOT {FileCatalogPaused} ORDER BY f.opened DESC LIMIT 1
                    """);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return new FileCatalogNext(null, settled);
                (product, build, generation) = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
                var attempts = reader.GetInt64(3); var opened = reader.GetInt64(5);
                long? inventory = reader.IsDBNull(4) ? null : reader.GetInt64(4);
                if (reader.IsDBNull(6) || attempts is < 0 or >= MaxFileCatalogAttempts) throw StoredIntegrity();
                var region = reader.GetString(6);
                var valid = inventory is null || !reader.IsDBNull(7) && reader.GetString(7) == $"staging|{FileCatalogProfile}|{build}";
                reader.Close();
                if (!valid) { AbandonFileCatalogGeneration(product, build, null); inventory = null; }
                return new FileCatalogNext(new FileCatalogWork(product, build, region, generation, (int)attempts, inventory, opened), settled);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && product is not null)
            {
                var problem = new FileCatalogFailure(failure is DiscoveryException typed ? typed.Code : "FILE_CATALOG_INTEGRITY",
                    "This build's saved file list preparation could not be read.", true, Cause(failure));
                AbandonFileCatalogGeneration(product, build!, null);
                if (Changed("UPDATE file_catalogs SET state='failed',attempts=$a,problem=$problem,stage='queued',updated_at=$t WHERE product=$p AND build_id=$b AND state='pending' AND generation=$g",
                    null, ("$a", MaxFileCatalogAttempts), ("$problem", Serialize(problem)), ("$t", DateTimeOffset.UtcNow.ToString("O")),
                    ("$p", product), ("$b", build), ("$g", generation)) != 1) throw;
                settled++;
            }
        }
    }, token);

    /// <summary>The underlying cause of a failure, bounded, for the engine log and the problem detail.</summary>
    internal static string? Cause(Exception failure)
    {
        var text = failure is DiscoveryException typed ? typed.Cause ?? typed.Message : $"{failure.GetType().Name}: {failure.Message}";
        return text is null ? null : text.Length <= 400 ? text : text[..400];
    }

    /// <summary>
    /// Whether the preparation may take its next step: still the same pending attempt, its build's Download all is not
    /// paused, and no other pending build was opened more recently (that one goes first; this one keeps its rows).
    /// </summary>
    private FileCatalogTurn FileCatalogTurnOf(FileCatalogWork work, SqliteTransaction? transaction)
    {
        using var command = Command($"""
            SELECT {FileCatalogPaused},
                EXISTS(SELECT 1 FROM file_catalogs o WHERE o.state='pending' AND o.opened>f.opened AND NOT EXISTS(
                    SELECT 1 FROM retention_jobs j WHERE j.product=o.product AND j.build_id=o.build_id AND j.mode=1 AND json_extract(j.document,'$.paused')=1))
            FROM file_catalogs f WHERE f.product=$p AND f.build_id=$b AND f.state='pending' AND f.generation=$g AND f.attempts=$a
            """, transaction, ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$g", work.Generation), ("$a", work.Attempts));
        using var r = command.ExecuteReader();
        if (!r.Read()) return FileCatalogTurn.Superseded;
        return r.GetInt64(0) == 1 ? FileCatalogTurn.Paused : r.GetInt64(1) == 1 ? FileCatalogTurn.Yield : FileCatalogTurn.Run;
    }

    /// <summary>
    /// Records the stage a running preparation reached, for the status read, after checking it may continue. A paused or
    /// overtaken preparation goes back to queued with its rows kept.
    /// </summary>
    internal Task<FileCatalogTurn> SetFileCatalogStageAsync(FileCatalogWork work, string stage, CancellationToken token) => Schedule(() =>
    {
        if (!FileCatalogStages.Contains(stage)) throw new ArgumentException("Unknown file catalog stage.", nameof(stage));
        var turn = stage == "queued" ? FileCatalogTurn.Run : FileCatalogTurnOf(work, null);
        if (turn == FileCatalogTurn.Superseded) return turn;
        Execute("UPDATE file_catalogs SET stage=$s WHERE product=$p AND build_id=$b AND state='pending' AND generation=$g AND attempts=$a", null,
            ("$s", turn == FileCatalogTurn.Run ? stage : "queued"), ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$g", work.Generation), ("$a", work.Attempts));
        return turn;
    }, token);

    /// <summary>
    /// The staging generation the rows go to, with how many its file already holds. A saved file is continued only when it
    /// was derived from the same exact sources and reads back as this generation; otherwise it is abandoned (cleanup removes
    /// it) and a new one starts. Free space is checked first: a file that cannot fit is refused with STORAGE_FULL.
    /// </summary>
    internal Task<(FileCatalogTurn Turn, long Inventory, long Written)> BeginFileCatalogGenerationAsync(FileCatalogWork work, string sources, long total, long freeBytes,
        CancellationToken token) => Schedule(() =>
    {
        if (sources.Length > 4096 || total < 0) throw StoredIntegrity();
        var turn = FileCatalogTurnOf(work, null);
        if (turn != FileCatalogTurn.Run) return (turn, 0L, 0L);
        long? inventory = null; long written = 0;
        if (work.Inventory is { } saved)
        {
            try
            {
                var schema = AttachFileCatalog(saved, false);
                var identity = ReadFileCatalogIdentity(schema, null);
                if (identity is not null && identity.Generation == saved && identity.Build == work.BuildIdentity && identity.Sources == sources && identity.Written <= total &&
                    Convert.ToInt64(Scalar($"SELECT coalesce(max(id),0) FROM {schema}.inventory_origins WHERE g=$g", null, ("$g", saved))) == identity.Written)
                    (inventory, written) = (saved, identity.Written);
            }
            catch (Exception failure) when (failure is SqliteException or DiscoveryException or IOException)
            {
                EngineLog.Write($"File catalog {saved} could not be continued and starts again ({Cause(failure)}).");
            }
            if (inventory is null) { using var drop = Db.BeginTransaction(); AbandonFileCatalogGeneration(work.ProductId, work.BuildIdentity, drop); drop.Commit(); }
        }
        RequireFileCatalogSpace(checked(total - written), freeBytes);
        if (inventory is null)
        {
            using (var transaction = Db.BeginTransaction())
            {
                Execute("INSERT INTO inventory_generations(build_id,region,state,profile,source) VALUES($b,$r,'staging',$profile,$s)", transaction,
                    ("$b", work.BuildIdentity), ("$r", work.SourceRegion), ("$profile", FileCatalogProfile), ("$s", sources));
                inventory = Convert.ToInt64(Scalar("SELECT last_insert_rowid()", transaction));
                transaction.Commit();
            }
            // A leftover file at the new path can only be an orphan of a generation no row names any more.
            DeleteFileCatalogFile(inventory.Value);
            var schema = AttachFileCatalog(inventory.Value, true);
            using var create = Db.BeginTransaction();
            CreateFileCatalogSchema(schema, inventory.Value, work.BuildIdentity, sources, create);
            create.Commit();
        }
        using var tx = Db.BeginTransaction();
        turn = FileCatalogTurnOf(work, tx);
        if (turn != FileCatalogTurn.Run) return (turn, 0L, 0L);
        Execute("UPDATE file_catalogs SET inventory=$g,written=$w,total=$n,stage='writing' WHERE product=$p AND build_id=$b", tx,
            ("$g", inventory), ("$w", written), ("$n", total), ("$p", work.ProductId), ("$b", work.BuildIdentity));
        token.ThrowIfCancellationRequested(); tx.Commit();
        return (FileCatalogTurn.Run, inventory.Value, written);
    }, token);

    /// <summary>About 370 bytes per membership on retail; this allows 512 and keeps 256 MiB free besides.</summary>
    internal const long FileCatalogBytesPerMembership = 512, FileCatalogReserveBytes = 256L << 20;
    private static void RequireFileCatalogSpace(long memberships, long freeBytes)
    {
        if (freeBytes < checked(memberships * FileCatalogBytesPerMembership + FileCatalogReserveBytes))
            throw new DiscoveryException("STORAGE_FULL", "There is not enough free disk space to save this build's file list.")
            { Cause = $"{freeBytes} bytes free; {memberships} memberships need about {memberships * FileCatalogBytesPerMembership + FileCatalogReserveBytes}." };
    }

    /// <summary>Saved rows after <paramref name="after"/>, in ID order, for a resumed preparation to check against what it derives.</summary>
    internal Task<IReadOnlyList<(FileCatalogRow Row, bool Stored)>> ReadFileCatalogRowsAsync(long inventory, long after, int limit, CancellationToken token) =>
        Schedule<IReadOnlyList<(FileCatalogRow, bool)>>(() =>
    {
        if (limit is < 1 or > FileCatalogBatchRows) throw new ArgumentOutOfRangeException(nameof(limit));
        var schema = AttachFileCatalog(inventory, false);
        var rows = new List<(FileCatalogRow, bool)>(limit);
        using var command = Command($"""
            SELECT id,family,name,ckey,ekey,encoded,decoded,declared_decoded,fdid_n,locale_n,flags_n,name_hash,raw,grp,
                length(CAST(name AS BLOB)),length(CAST(raw AS BLOB)),stored
            FROM {schema}.inventory_origins WHERE g=$g AND id>$a ORDER BY id LIMIT $n
            """, null, ("$g", inventory), ("$a", after), ("$n", limit));
        using var r = command.ExecuteReader();
        long? Number(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!r.IsDBNull(14) && r.GetInt64(14) > 4096 || r.GetInt64(15) > 256) throw StoredIntegrity();
            rows.Add((new(r.GetInt64(0), r.GetString(1), Text(2), r.GetString(3), Text(4), Number(5), Number(6), Number(7), Number(8), Number(9),
                Number(10), Text(11), r.GetString(12), Number(13)), r.GetInt64(16) == 1));
        }
        return rows;
    }, token);

    /// <summary>The EKeys among <paramref name="keys"/> stored now under the D065 rule, in slices of 256.</summary>
    internal Task<IReadOnlySet<string>> StoredFileCatalogKeysAsync(IReadOnlyList<(string Key, long Bytes)> keys, CancellationToken token) =>
        Schedule<IReadOnlySet<string>>(() =>
    {
        if (keys.Count > 256) throw new ArgumentOutOfRangeException(nameof(keys));
        using var transaction = Db.BeginTransaction();
        var stored = StoredMembers(keys.Select((key, index) => new StoredMember(index, key.Key, key.Bytes)).ToArray(), transaction, token);
        transaction.Commit();
        return stored;
    }, token);

    /// <summary>
    /// Writes one batch after the file's saved position, with each row's stored mark (the D065 rule, checked now), and moves
    /// that position, in one transaction of the file. Returns the stored ROOT and install memberships of the batch, or the
    /// turn that stopped it (superseded, paused or overtaken) with nothing written.
    /// </summary>
    internal Task<(FileCatalogTurn Turn, int Root, int Install)> AddFileCatalogRowsAsync(FileCatalogWork work, long inventory, long after, IReadOnlyList<FileCatalogRow> rows,
        CancellationToken token) => Schedule(() =>
    {
        if (rows.Count is < 1 or > FileCatalogBatchRows) throw new ArgumentOutOfRangeException(nameof(rows));
        for (var i = 0; i < rows.Count; i++) if (rows[i].Id != after + 1 + i) throw new ArgumentException("File catalog rows must continue the saved position.", nameof(rows));
        var schema = AttachFileCatalog(inventory, false);
        using var transaction = Db.BeginTransaction();
        var turn = FileCatalogTurnOf(work, transaction);
        if (turn != FileCatalogTurn.Run) return (turn, 0, 0);
        if (Scalar("SELECT 1 FROM file_catalogs WHERE product=$p AND build_id=$b AND inventory=$g", transaction, ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$g", inventory)) is null ||
            Scalar($"SELECT 1 FROM {schema}.catalog_identity WHERE id=1 AND generation=$g AND written=$w AND sealed=0", transaction, ("$g", inventory), ("$w", after)) is null)
            throw new DiscoveryException("CHECKPOINT_INTEGRITY", "File list rows must continue from their committed position.");
        var resolved = rows.Where(row => row.EncodingKey is not null).Select(row => (row.EncodingKey!, row.EncodedBytes!.Value)).Distinct()
            .Select((member, index) => new StoredMember(index, member.Item1, member.Item2)).ToArray();
        // In slices of 256, so each JSON key list stays a small short-lived string rather than a large-object allocation.
        var stored = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in resolved.Chunk(256)) stored.UnionWith(StoredMembers(slice, transaction, token));
        using var insert = Command($"INSERT INTO {schema}.inventory_origins(g,id,family,name,ckey,width,declared_decoded,name_hash,raw,ekey,encoded,decoded,fdid_n,locale_n,flags_n,grp,stored) VALUES($g,$i,$f,$n,$c,0,$dd,$h,$raw,$e,$enc,$dec,$dn,$ln,$an,$grp,$s)", transaction);
        int root = 0, install = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var isStored = row.EncodingKey is { } ekey && stored.Contains(ekey);
            if (isStored) { if (row.Family == "root") root++; else install++; }
            Insert(insert, ("$g", inventory), ("$i", row.Id), ("$f", row.Family), ("$n", row.Name), ("$c", row.ContentKey), ("$dd", row.DeclaredDecoded), ("$h", row.NameHash),
                ("$raw", row.Raw), ("$e", row.EncodingKey), ("$enc", row.EncodedBytes), ("$dec", row.DecodedBytes), ("$dn", row.FileDataId), ("$ln", row.LocaleFlags),
                ("$an", row.ContentFlags), ("$grp", row.Group), ("$s", isStored ? 1 : 0));
        }
        Execute($"UPDATE {schema}.catalog_identity SET written=$w WHERE id=1", transaction, ("$w", after + rows.Count));
        // Progress for the status read; the file's own position is the one a restart continues from.
        Execute("UPDATE file_catalogs SET written=$w WHERE product=$p AND build_id=$b", transaction,
            ("$w", after + rows.Count), ("$p", work.ProductId), ("$b", work.BuildIdentity));
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return (FileCatalogTurn.Run, root, install);
    }, token);

    /// <summary>
    /// Seals the generation in two steps, because a transaction over two WAL databases is not atomic: first the file (its
    /// rows end at the expected count, the planner statistics are recorded there and it is marked sealed), then the main
    /// catalog makes it the build's one readable file catalog. A crash between the two leaves a sealed file under a staging
    /// generation, which the next run checks and seals again; never a ready build with an unsealed file. False when the
    /// preparation was superseded.
    /// </summary>
    internal Task<bool> SealFileCatalogAsync(FileCatalogWork work, long inventory, FileCatalogStats stats, IReadOnlyList<MetadataReceipt> metadata, CancellationToken token) => Schedule(() =>
    {
        var members = checked(stats.RootMembers + stats.InstallMembers);
        var schema = AttachFileCatalog(inventory, false);
        using (var file = Db.BeginTransaction())
        {
            // This transaction writes only the attached file; it reads the main catalog for the fence.
            if (FileCatalogTurnOf(work, file) == FileCatalogTurn.Superseded) return false;
            SealFileCatalogFile(work, inventory, schema, members, stats, file, token);
            token.ThrowIfCancellationRequested(); file.Commit();
        }
        using var transaction = Db.BeginTransaction();
        if (FileCatalogTurnOf(work, transaction) == FileCatalogTurn.Superseded) return false;
        if (Scalar($"SELECT 1 FROM {schema}.catalog_identity WHERE id=1 AND generation=$g AND written=$w AND sealed=1", transaction, ("$g", inventory), ("$w", members)) is null)
            throw new DiscoveryException("CHECKPOINT_INTEGRITY", "The file list was not sealed before it was made ready.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        var summary = new FileCatalogSummary(N(inventory), now, N(stats.RootFiles), N(stats.InstallFiles), N(stats.RootMembers), N(stats.InstallMembers),
            N(stats.UniqueContent), N(stats.UnresolvedMembers), N(stats.StoredMembers));
        Execute("UPDATE inventory_generations SET state='ready',summary=$s,metadata=$m WHERE id=$g AND state='staging' AND profile=$profile", transaction,
            ("$g", inventory), ("$s", Serialize(summary)), ("$m", Serialize(metadata)), ("$profile", FileCatalogProfile));
        Execute("UPDATE file_catalogs SET state='ready',stage='queued',problem=NULL,updated_at=$t WHERE product=$p AND build_id=$b", transaction,
            ("$t", now), ("$p", work.ProductId), ("$b", work.BuildIdentity));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>The file's half of the seal: its rows and statistics agree, then it is marked sealed. Sealing again is harmless.</summary>
    private void SealFileCatalogFile(FileCatalogWork work, long inventory, string schema, long members, FileCatalogStats stats, SqliteTransaction transaction, CancellationToken token)
    {
        if (Scalar("SELECT 1 FROM file_catalogs WHERE product=$p AND build_id=$b AND inventory=$g AND total=$w", transaction,
                ("$p", work.ProductId), ("$b", work.BuildIdentity), ("$g", inventory), ("$w", members)) is null ||
            Scalar($"SELECT 1 FROM {schema}.catalog_identity WHERE id=1 AND generation=$g AND written=$w", transaction, ("$g", inventory), ("$w", members)) is null ||
            Convert.ToInt64(Scalar($"SELECT coalesce(max(id),0) FROM {schema}.inventory_origins WHERE g=$g", transaction, ("$g", inventory))) != members ||
            stats.Variants.Sum(pair => pair.Members) != stats.RootMembers)
            throw new DiscoveryException("CHECKPOINT_INTEGRITY", "The file list rows and their statistics do not agree.");
        Execute($"DELETE FROM {schema}.inventory_root_variants; DELETE FROM {schema}.inventory_file_stats;", transaction);
        using (var variant = Command($"INSERT INTO {schema}.inventory_root_variants VALUES($g,$l,$f,$n)", transaction))
            foreach (var (locale, flags, count) in stats.Variants)
            { token.ThrowIfCancellationRequested(); Insert(variant, ("$g", inventory), ("$l", (long)locale), ("$f", (long)flags), ("$n", count)); }
        Execute($"INSERT INTO {schema}.inventory_file_stats(g,root_files,install_files,root_members,variants,stored_root_members) VALUES($g,$rf,$if,$rm,$v,$s)", transaction,
            ("$g", inventory), ("$rf", stats.RootFiles), ("$if", stats.InstallFiles), ("$rm", stats.RootMembers), ("$v", stats.Variants.Count), ("$s", stats.StoredRootMembers));
        Execute($"UPDATE {schema}.catalog_identity SET sealed=1 WHERE id=1", transaction);
    }

    /// <summary>
    /// Records an unreadable file list (terminal, unsupported), a refusal that retrying cannot fix (terminal, failed, such as
    /// STORAGE_FULL) or a failed attempt, which becomes terminal after three. Returns the new state (pending while attempts
    /// remain), or null when the preparation was superseded. Rows already written stay in their file for a later attempt.
    /// </summary>
    internal Task<string?> FailFileCatalogAsync(FileCatalogWork work, string kind, FileCatalogFailure problem, CancellationToken token) => Schedule<string?>(() =>
    {
        if (string.IsNullOrWhiteSpace(problem.Code) || problem.Code.Length > 64 || string.IsNullOrWhiteSpace(problem.Message) || problem.Message.Length > 400 || problem.Detail?.Length > 400)
            throw StoredIntegrity();
        using var transaction = Db.BeginTransaction();
        if (FileCatalogTurnOf(work, transaction) == FileCatalogTurn.Superseded) return null;
        var attempts = work.Attempts + (kind == "attempt" ? 1 : 0);
        var state = kind == "unsupported" ? "unsupported" : kind == "final" || attempts >= MaxFileCatalogAttempts ? "failed" : "pending";
        Execute("UPDATE file_catalogs SET state=$s,attempts=$a,problem=$problem,stage='queued',updated_at=$t WHERE product=$p AND build_id=$b", transaction,
            ("$s", state), ("$a", Math.Min(attempts, MaxFileCatalogAttempts)), ("$problem", Serialize(problem)), ("$t", DateTimeOffset.UtcNow.ToString("O")),
            ("$p", work.ProductId), ("$b", work.BuildIdentity));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return state;
    }, token);

    /// <summary>Saved rows that differ from what the same sources derive now: abandon them and start the next attempt from the beginning.</summary>
    internal Task<bool> RestartFileCatalogAsync(FileCatalogWork work, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        if (FileCatalogTurnOf(work, transaction) == FileCatalogTurn.Superseded) return false;
        AbandonFileCatalogGeneration(work.ProductId, work.BuildIdentity, transaction);
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    /// <summary>
    /// The user's Stop or Resume (D071). Stop leaves the build stopped with its written rows, cancels a running preparation
    /// and is undone by Resume or the next Open; Resume, or Retry of a failed one, queues it again first in line. Resume of
    /// a removed one (D085) prepares it again from the beginning. An unsupported file list is not retried this way: reading
    /// the same bytes again cannot help.
    /// </summary>
    public Task<FileCatalogStatus> SetFileCatalogAsync(FileCatalogSetRequest request, CancellationToken token) => Schedule(() =>
    {
        InventoryService.Validate(request);
        var stop = request.Action == "stop";
        using (var transaction = Db.BeginTransaction())
        {
            var state = (string?)Scalar("SELECT state FROM file_catalogs WHERE product=$p AND build_id=$b", transaction, ("$p", request.ProductId), ("$b", request.BuildIdentity))
                ?? throw new DiscoveryException("FILE_CATALOG_NOT_FOUND", "This build has no file list preparation. Open the build first.");
            var allowed = stop ? state == "pending" : state is "stopped" or "failed" or "removed";
            if (!allowed) throw new DiscoveryException("FILE_CATALOG_STATE", stop ? "This build's file list is not being prepared." : state == "unsupported"
                ? "This build's file list cannot be read; trying again would read the same bytes." : "This build's file list is not stopped, failed or removed.");
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (stop) Execute("UPDATE file_catalogs SET state='stopped',generation=generation+1,stage='queued',updated_at=$t WHERE product=$p AND build_id=$b", transaction,
                ("$t", now), ("$p", request.ProductId), ("$b", request.BuildIdentity));
            else Execute("UPDATE file_catalogs SET state='pending',generation=generation+1,attempts=0,stage='queued',problem=NULL,opened=(SELECT coalesce(max(opened),0)+1 FROM file_catalogs),updated_at=$t WHERE product=$p AND build_id=$b",
                transaction, ("$t", now), ("$p", request.ProductId), ("$b", request.BuildIdentity));
            token.ThrowIfCancellationRequested(); transaction.Commit();
        }
        if (stop) FileCatalogStopped?.Invoke(request.ProductId, request.BuildIdentity);
        WakeFileCatalogs();
        var region = (string?)Scalar("SELECT region FROM workspace_admissions WHERE product=$p AND build_id=$b ORDER BY id DESC LIMIT 1", null, ("$p", request.ProductId), ("$b", request.BuildIdentity))
            ?? throw StoredIntegrity();
        return ReadFileCatalog(new(request.ProductId, request.BuildIdentity, region));
    }, token);

    /// <summary>Generations whose files one cleanup pass may skip because they could not be deleted.</summary>
    internal const int FileCatalogCleanupSkipBound = 256;
    /// <summary>File catalogs remembered as already logged as undeletable; beyond this, further ones are not logged.</summary>
    private const int UndeletableLogBound = 4096;
    // Worker-thread state, touched only inside catalog operations.
    private readonly HashSet<long> undeletableCatalogs = [];
    private int undeletableLogLines;
    /// <summary>Tests and diagnostics only: log lines written for file catalogs cleanup could not delete.</summary>
    internal int UndeletableLogLines => Volatile.Read(ref undeletableLogLines);

    /// <summary>
    /// Removes one file catalog no build stands on (an abandoned or unreferenced staging generation, or a file no
    /// generation row names): its file, then its registry row. False when nothing is left. With <paramref name="skipped"/>,
    /// a generation whose files cannot be deleted (a lock, a permission) keeps its registry row, is added to the set and
    /// is not tried again in this pass, so one stuck file never holds back the others or the worker (D085); without it,
    /// the failure is thrown.
    /// </summary>
    internal Task<bool> CleanupFileCatalogsAsync(CancellationToken token, ISet<long>? skipped = null) => Schedule(() =>
    {
        bool Delete(long id)
        {
            try { DeleteFileCatalogFile(id); undeletableCatalogs.Remove(id); return true; }
            catch (Exception failure) when (skipped is not null && failure is IOException or UnauthorizedAccessException or SqliteException)
            {
                skipped.Add(id);
                // A file that stays stuck is tried every retry delay for as long as the engine runs: one log line per file.
                if (undeletableCatalogs.Count < UndeletableLogBound && undeletableCatalogs.Add(id))
                {
                    undeletableLogLines++;
                    EngineLog.Write($"File catalog {id} could not be deleted yet; cleanup keeps trying without logging it again ({Cause(failure)}).");
                }
                return false;
            }
        }
        if (skipped?.Count >= FileCatalogCleanupSkipBound) return false;
        // Skipped generations are the lowest ones this pass picked, so one more than them names the next to try.
        long? next = null;
        using (var command = Command("""
            SELECT id FROM inventory_generations i WHERE profile=$profile AND state IN ('staging','abandoned')
                AND NOT EXISTS(SELECT 1 FROM file_catalogs f WHERE f.inventory=i.id) ORDER BY id LIMIT $n
            """, null, ("$profile", FileCatalogProfile), ("$n", (skipped?.Count ?? 0) + 1)))
        using (var r = command.ExecuteReader())
            while (r.Read()) if (skipped is null || !skipped.Contains(r.GetInt64(0))) { next = r.GetInt64(0); break; }
        if (next is long g)
        {
            if (Delete(g))
                Execute("DELETE FROM inventory_generations WHERE id=$g AND profile=$profile AND state IN ('staging','abandoned') AND NOT EXISTS(SELECT 1 FROM file_catalogs WHERE inventory=$g)",
                    null, ("$g", g), ("$profile", FileCatalogProfile));
            return true;
        }
        if (!Directory.Exists(fileCatalogDirectory)) return false;
        foreach (var path in Directory.EnumerateFiles(fileCatalogDirectory, "files-*.sqlite3").Take(4096))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || skipped?.Contains(id) == true) continue;
            if (Scalar("SELECT 1 FROM inventory_generations WHERE id=$g AND profile=$profile", null, ("$g", id), ("$profile", FileCatalogProfile)) is not null) continue;
            Delete(id);
            return true;
        }
        return false;
    }, token);

    private static readonly string[] FileCatalogStates = ["pending", "stopped", "ready", "failed", "unsupported", "removed"];

    /// <summary>
    /// The file catalog of one opened build, for Files: one row, at most one bounded summary and the build it waits for,
    /// no transfer or count. The region must be one the build was opened through; the catalog itself is the build's.
    /// </summary>
    public Task<FileCatalogStatus> ReadFileCatalogAsync(FileCatalogRequest request, CancellationToken token) => Schedule(() =>
    {
        InventoryService.Validate(request); return ReadFileCatalog(request);
    }, token);

    /// <summary>Every preparation Downloads & activity shows: pending, stopped or failed, most recently opened first, at most 32.</summary>
    public Task<FileCatalogList> ListFileCatalogsAsync(CancellationToken token) => Schedule(() =>
    {
        var keys = new List<(string Product, string Build, string Region)>();
        using (var command = Command($"""
            SELECT f.product,f.build_id,(SELECT a.region FROM workspace_admissions a WHERE a.product=f.product AND a.build_id=f.build_id ORDER BY a.id DESC LIMIT 1)
            FROM file_catalogs f WHERE f.state IN ('pending','stopped','failed') ORDER BY f.opened DESC LIMIT {FileCatalogListBound}
            """))
        using (var r = command.ExecuteReader())
            while (r.Read()) { token.ThrowIfCancellationRequested(); if (!r.IsDBNull(2)) keys.Add((r.GetString(0), r.GetString(1), r.GetString(2))); }
        return new FileCatalogList(keys.Select(key => ReadFileCatalog(new(key.Product, key.Build, key.Region))).ToArray());
    }, token);

    private FileCatalogStatus ReadFileCatalog(FileCatalogRequest request)
    {
        var version = (string?)Scalar("SELECT json_extract(identity_fields,'$.VersionsName') FROM builds WHERE id=$b AND product=$p", null, ("$b", request.BuildIdentity), ("$p", request.ProductId));
        FileCatalogStatus Status(string state, string? stage = null, string? completed = null, string? total = null, int attempts = 0, FileCatalogSummary? summary = null,
            FileCatalogFailure? problem = null, FileCatalogBuild? waitingFor = null) =>
            new(request.ProductId, request.BuildIdentity, request.SourceRegion, version, state, stage, completed, total, attempts, summary,
                problem is null ? null : new(problem.Code, problem.Message, problem.Retryable, problem.Detail), waitingFor);
        if (Scalar("SELECT 1 FROM workspace_admissions WHERE build_id=$b AND region=$r AND product=$p LIMIT 1", null,
            ("$p", request.ProductId), ("$b", request.BuildIdentity), ("$r", request.SourceRegion)) is null) return Status("notOpened");
        string state, stage; long written, attempts, opened; long? total, inventory; FileCatalogFailure? problem = null; string? summaryText = null; bool paused;
        using (var command = Command($"""
            SELECT f.state,f.stage,f.written,f.total,f.attempts,f.problem,length(CAST(f.problem AS BLOB)),i.summary,length(CAST(i.summary AS BLOB)),i.state,i.profile,f.inventory,
                f.opened,{FileCatalogPaused}
            FROM file_catalogs f LEFT JOIN inventory_generations i ON i.id=f.inventory WHERE f.product=$p AND f.build_id=$b
            """, null, ("$p", request.ProductId), ("$b", request.BuildIdentity)))
        using (var r = command.ExecuteReader())
        {
            if (!r.Read()) return Status("notOpened");
            (state, stage, written, attempts, opened, paused) = (r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(4), r.GetInt64(12), r.GetInt64(13) == 1);
            total = r.IsDBNull(3) ? null : r.GetInt64(3); inventory = r.IsDBNull(11) ? null : r.GetInt64(11);
            if (!r.IsDBNull(5))
            {
                if (r.GetInt64(6) > 2048) throw StoredIntegrity();
                problem = Deserialize<FileCatalogFailure>(r.GetString(5));
            }
            if (state == "ready")
            {
                if (r.IsDBNull(7) || r.GetInt64(8) > FileCatalogSummaryBytes || r.GetString(9) != "ready" || r.GetString(10) != FileCatalogProfile) throw StoredIntegrity();
                summaryText = r.GetString(7);
            }
        }
        if (attempts is < 0 or > MaxFileCatalogAttempts || !FileCatalogStages.Contains(stage) || !FileCatalogStates.Contains(state) ||
            problem is not null && (problem.Code.Length is < 1 or > 64 || problem.Message.Length is < 1 or > 400 || problem.Detail?.Length > 400)) throw StoredIntegrity();
        static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        switch (state)
        {
            case "ready":
                var summary = Deserialize<FileCatalogSummary>(summaryText!);
                if (inventory is null || summary.Generation != N(inventory.Value)) throw StoredIntegrity();
                return Status("ready", summary: summary);
            case "pending":
                if (paused) return Status("pending", "paused", attempts: (int)attempts, problem: problem);
                var writing = stage == "writing" && total is not null;
                if (writing && (written < 0 || written > total)) throw StoredIntegrity();
                return Status("pending", stage, writing ? N(written) : null, writing ? N(total!.Value) : null, (int)attempts, problem: problem,
                    waitingFor: stage == "queued" ? RunningFileCatalog(request) : stage == "waiting" ? MeasuringRootScope() : null);
            case "stopped":
                return Status("stopped", attempts: (int)attempts, problem: problem);
            case "removed":
                // Freed by the user (D085): no rows and no file are left; the next Open or Resume prepares it again.
                if (inventory is not null || problem is not null) throw StoredIntegrity();
                return Status("removed");
            default:
                return Status(state, attempts: (int)attempts, problem: problem ?? throw StoredIntegrity());
        }
    }

    private FileCatalogBuild? NamedBuild(string product, string build) =>
        Scalar("SELECT json_extract(identity_fields,'$.VersionsName') FROM builds WHERE id=$b AND product=$p", null, ("$b", build), ("$p", product)) is string version
            ? new(product, build, version) : null;
    /// <summary>The preparation running now, which a queued one waits for.</summary>
    private FileCatalogBuild? RunningFileCatalog(FileCatalogRequest self)
    {
        using var command = Command("SELECT product,build_id FROM file_catalogs WHERE state='pending' AND stage<>'queued' AND NOT (product=$p AND build_id=$b) LIMIT 1", null,
            ("$p", self.ProductId), ("$b", self.BuildIdentity));
        using var r = command.ExecuteReader();
        return r.Read() ? NamedBuild(r.GetString(0), r.GetString(1)) : null;
    }
    /// <summary>The ROOT scope measurement holding the shared gate, which a waiting preparation waits for.</summary>
    private FileCatalogBuild? MeasuringRootScope()
    {
        using var command = Command("SELECT product,build_id FROM root_scopes WHERE state='pending' ORDER BY updated_at,build_id LIMIT 1");
        using var r = command.ExecuteReader();
        return r.Read() ? NamedBuild(r.GetString(0), r.GetString(1)) : null;
    }

    internal Task<string?> ReadFileCatalogStateForTestAsync(string product, string build, CancellationToken token) =>
        Schedule(() => (string?)Scalar("SELECT state FROM file_catalogs WHERE product=$p AND build_id=$b", null, ("$p", product), ("$b", build)), token);
}
