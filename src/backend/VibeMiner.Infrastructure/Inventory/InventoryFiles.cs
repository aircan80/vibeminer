using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
namespace VibeMiner.Infrastructure;

/// <summary>
/// Files: one row per ROOT FileDataID or install name over a sealed inventory generation (D068).
/// A request stops at the first file boundary after <see cref="DiscoveryCatalog.FilesExaminationBudget"/> membership rows,
/// so a sparse filter returns a short page with scanLimited and a cursor instead of walking the whole catalog. A file has
/// at most <see cref="DiscoveryCatalog.MaxFileMemberships"/> memberships (refused at seal), which bounds the overshoot by
/// <see cref="DiscoveryCatalog.FilesReadSlack"/>. Candidate FileDataIDs
/// come from whichever index the filters make smallest: exact FileDataID, content key, (locale, content flags) variant
/// streams merged in FileDataID order, the stored-membership index, or the FileDataID-ordered scan.
/// </summary>
public sealed partial class DiscoveryCatalog
{
    /// <summary>Membership rows one Files request may read, candidate search and row summaries together.</summary>
    internal int FilesExaminationBudget { get; set; } = 65_536;
    /// <summary>A content-key prefix matching more ROOT/install memberships than this is refused as too broad.</summary>
    internal const int ContentPrefixBound = 16_384;
    /// <summary>Variant streams merged at once; more matching (locale, flags) pairs use the FileDataID-ordered scan.</summary>
    internal const int VariantMergeBound = 256;
    /// <summary>Variant pairs the planner reads per request; a ROOT with more distinct pairs plans without them.</summary>
    internal const int VariantPlanBound = 4096;
    /// <summary>Memberships one Files row may have. Sealing refuses a catalog with a larger ROOT FileDataID or install name.</summary>
    internal const int MaxFileMemberships = 4096;
    /// <summary>
    /// Rows a request can read past the budget before it reaches a file boundary: the rest of one file in the driver, plus
    /// the merge refills that can happen meanwhile (64 rows, at most once per 64 popped rows plus once per stream), plus that
    /// file's summary. A CKey prefix's candidate set (at most <see cref="ContentPrefixBound"/> rows) fits inside it too.
    /// </summary>
    internal const int FilesReadSlack = MaxFileMemberships + (MaxFileMemberships / 64 + VariantMergeBound) * 64;
    /// <summary>Membership rows the last inventory.files and inventory.file requests read; tests check the stated bounds.</summary>
    internal int LastFilesRowsRead { get; private set; }
    internal int LastFileRowsRead { get; private set; }
    private const int FilesFrameBytes = 750_000;

    private void UpgradeInventoryFiles()
    {
        using var tx = Db.BeginTransaction();
        // Derived columns written with each origin: numeric ROOT FileDataID, locale and derived content flags, and the
        // install file group (first membership ID with the same exact name). Every seal checks them against the
        // digest-covered source columns. stored mirrors the sealed inventory_units.retained of the membership's resolved
        // EKey and is rewritten at every seal. A version-16 catalog's existing memberships are backfilled here; their
        // next Open reseals, which records the stored marks and the Files statistics.
        Execute("""
            ALTER TABLE inventory_origins ADD COLUMN fdid_n INTEGER;
            ALTER TABLE inventory_origins ADD COLUMN locale_n INTEGER;
            ALTER TABLE inventory_origins ADD COLUMN flags_n INTEGER;
            ALTER TABLE inventory_origins ADD COLUMN grp INTEGER;
            ALTER TABLE inventory_origins ADD COLUMN stored INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX inventory_install_names ON inventory_origins(g,name,id) WHERE family='install';
            UPDATE inventory_origins SET fdid_n=CAST(fdid AS INTEGER),locale_n=CAST(locale AS INTEGER),flags_n=CAST(flags AS INTEGER) WHERE family='root';
            UPDATE inventory_origins SET grp=(SELECT min(x.id) FROM inventory_origins x INDEXED BY inventory_install_names
                WHERE x.g=inventory_origins.g AND x.family='install' AND x.name=inventory_origins.name) WHERE family='install';
            DROP INDEX inventory_content;
            CREATE INDEX inventory_content ON inventory_origins(g,ckey,family,fdid_n);
            CREATE INDEX inventory_root_files ON inventory_origins(g,fdid_n,locale_n,flags_n,stored) WHERE fdid_n IS NOT NULL;
            CREATE INDEX inventory_root_variant_files ON inventory_origins(g,locale_n,flags_n,fdid_n) WHERE fdid_n IS NOT NULL;
            CREATE INDEX inventory_stored_files ON inventory_origins(g,family,fdid_n,locale_n,flags_n) WHERE stored=1;
            CREATE INDEX inventory_install_files ON inventory_origins(g,grp,id) WHERE family='install';
            CREATE TABLE inventory_root_variants(g INTEGER NOT NULL,locale INTEGER NOT NULL,flags INTEGER NOT NULL,members INTEGER NOT NULL,
                PRIMARY KEY(g,locale,flags));
            CREATE TABLE inventory_file_stats(g INTEGER PRIMARY KEY,root_files INTEGER NOT NULL,install_files INTEGER NOT NULL,
                root_members INTEGER NOT NULL,variants INTEGER NOT NULL,stored_root_members INTEGER NOT NULL);
            PRAGMA user_version=17;
            """, tx);
        tx.Commit();
    }

    /// <summary>Inside the seal transaction: mirror retained EKeys onto memberships and record the planner statistics.</summary>
    private void SealInventoryFiles(long generation, IReadOnlyList<string> verifiedKeys, SqliteTransaction tx, CancellationToken token)
    {
        Execute("UPDATE inventory_origins SET stored=0 WHERE g=$g AND stored=1", tx, ("$g", generation));
        // Every ROOT/install origin's EKey is an EN candidate of its CKey (checked above), so the reverse candidate
        // index finds all memberships of one retained EKey without a build-wide scan.
        using (var mark = Command("UPDATE inventory_origins SET stored=1 WHERE g=$g AND ckey IN (SELECT ckey FROM en_candidates WHERE g=$g AND ekey=$e) AND ekey=$e AND family IN ('root','install')", tx, ("$g", generation), ("$e", "")))
        {
            mark.Prepare();
            foreach (var key in verifiedKeys) { token.ThrowIfCancellationRequested(); mark.Parameters[1].Value = key; mark.ExecuteNonQuery(); }
        }
        RecordFileStats(generation, tx, token);
    }
    /// <summary>Records the Files statistics of a generation whose stored marks are final. Tests and the Files benchmark seed through it.</summary>
    internal Task RecordFileStatsAsync(long generation, CancellationToken token) => Schedule(() =>
    {
        using var tx = Db.BeginTransaction(); RecordFileStats(generation, tx, token); tx.Commit(); return true;
    }, token);
    private void RecordFileStats(long generation, SqliteTransaction tx, CancellationToken token)
    {
        // Rebuilt at every seal: the statistics are derived and outside the digest, so a reopen never plans from saved
        // counts it has not just recomputed from the checked membership columns.
        Execute("DELETE FROM inventory_root_variants WHERE g=$g; DELETE FROM inventory_file_stats WHERE g=$g;", tx, ("$g", generation));
        // Every Files read of one row is bounded by this. Real ROOTs have about a dozen memberships per FileDataID;
        // the format allows one per block (up to a million), so a larger file is refused rather than read unbounded.
        if (Convert.ToInt64(Scalar($"SELECT EXISTS(SELECT 1 FROM inventory_origins INDEXED BY inventory_root_files WHERE g=$g AND fdid_n IS NOT NULL GROUP BY fdid_n HAVING count(*)>{MaxFileMemberships})", tx, ("$g", generation))) != 0 ||
            Convert.ToInt64(Scalar($"SELECT EXISTS(SELECT 1 FROM inventory_origins INDEXED BY inventory_install_files WHERE g=$g AND family='install' GROUP BY grp HAVING count(*)>{MaxFileMemberships})", tx, ("$g", generation))) != 0)
            throw new DiscoveryException("INVENTORY_LIMIT", $"A ROOT FileDataID or install name has more than {MaxFileMemberships:N0} memberships, the bound Files reads per file.");
        token.ThrowIfCancellationRequested();
        Execute("INSERT INTO inventory_root_variants SELECT g,locale_n,flags_n,count(*) FROM inventory_origins INDEXED BY inventory_root_variant_files WHERE g=$g AND fdid_n IS NOT NULL GROUP BY locale_n,flags_n", tx, ("$g", generation));
        token.ThrowIfCancellationRequested();
        Execute("""
            INSERT INTO inventory_file_stats(g,root_files,install_files,root_members,variants,stored_root_members) VALUES($g,
                (SELECT count(DISTINCT fdid_n) FROM inventory_origins INDEXED BY inventory_root_files WHERE g=$g AND fdid_n IS NOT NULL),
                (SELECT count(DISTINCT grp) FROM inventory_origins INDEXED BY inventory_install_files WHERE g=$g AND family='install'),
                (SELECT count(*) FROM inventory_origins INDEXED BY inventory_root_files WHERE g=$g AND fdid_n IS NOT NULL),
                (SELECT count(*) FROM inventory_root_variants WHERE g=$g),0)
            """, tx, ("$g", generation));
        Execute("UPDATE inventory_file_stats SET stored_root_members=(SELECT count(*) FROM inventory_origins INDEXED BY inventory_stored_files WHERE g=$g AND stored=1 AND family='root') WHERE g=$g", tx, ("$g", generation));
        if (Convert.ToInt64(Scalar("SELECT root_members FROM inventory_file_stats WHERE g=$g", tx, ("$g", generation))) !=
            Convert.ToInt64(Scalar("SELECT count(*) FROM inventory_origins INDEXED BY inventory_family WHERE g=$g AND family='root'", tx, ("$g", generation))))
            throw new DiscoveryException("CHECKPOINT_INTEGRITY", "The ROOT memberships and their FileDataID index do not agree.");
    }

    private sealed record FileStats(long RootFiles, long InstallFiles, long RootMembers, long Variants, long StoredRootMembers);
    private sealed class Budget(int rows)
    {
        public int Remaining = rows, Used;
        public bool Exhausted => Remaining <= 0;
        public void Read() { Remaining--; Used++; }
    }
    private long ReadyFilesGeneration(string product, string build, string region, string generation)
    {
        var g = long.Parse(generation, CultureInfo.InvariantCulture);
        var ready = Scalar("SELECT 1 FROM inventory_generations WHERE id=$g AND build_id=$b AND region=$r AND state='ready' AND profile=$p", null,
            ("$g", g), ("$b", build), ("$r", region), ("$p", InventoryWorkspacePreparer.Profile));
        if (ready is null || (string?)Scalar("SELECT product FROM builds WHERE id=$b", null, ("$b", build)) != product)
            throw new DiscoveryException("INVENTORY_NOT_FOUND", "This exact prepared inventory is unavailable. Open the build again.", true);
        return g;
    }
    private FileStats ReadFileStats(long g)
    {
        using var cmd = Command("SELECT root_files,install_files,root_members,variants,stored_root_members FROM inventory_file_stats WHERE g=$g", null, ("$g", g));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new DiscoveryException("CATALOG_INTEGRITY", "The prepared inventory has no Files index.");
        return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    public Task<InventoryFilesPage> ReadInventoryFilesAsync(InventoryFilesRequest request, CancellationToken token) => Schedule(() =>
    {
        var (query, after) = InventoryService.Validate(request);
        var g = ReadyFilesGeneration(request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation);
        var stats = ReadFileStats(g);
        var budget = new Budget(FilesExaminationBudget);
        var entries = new List<InventoryFile>(); var bytes = 0;
        InventoryFilePosition? position = after; var more = false; var limited = false;
        bool Offer(InventoryFile file)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(file, Json).Length + 2;
            if (entries.Count == request.Limit || bytes + size > FilesFrameBytes) { more = true; return false; }
            entries.Add(file); bytes += size; position = InventoryService.Position(file.Key, false); return true;
        }
        if (query.IncludesRoot && after is not { Install: true })
        {
            var root = new RootCandidates(this, g, query, stats, after?.Value ?? -1, budget, token);
            foreach (var fdid in root.Read())
            {
                var file = RootFile(g, fdid, query, budget, token);
                if (file is not null && file.MatchingCount > 0 && !Offer(file)) break;
                // A candidate that failed the full predicate is examined too; resume after it.
                if (!more && file?.MatchingCount is null or 0) position = new InventoryFilePosition(false, fdid);
            }
            if (!more && root.Limited) { limited = true; if (root.ExaminedThrough is { } through && (position is null || !position.Value.Install && position.Value.Value < through)) position = new InventoryFilePosition(false, through); }
            else if (!more) position = new InventoryFilePosition(true, 0);
        }
        if (!more && !limited && query.InstallCanMatch)
        {
            if (budget.Exhausted) limited = true;
            else foreach (var file in InstallFiles(g, query, position is { Install: true } p ? p.Value : 0, budget, token))
            {
                if (file is null) { limited = true; break; }
                if (file.MatchingCount > 0 && !Offer(file)) break;
                if (file.MatchingCount == 0) position = InventoryService.Position(file.Key, false);
            }
        }
        string? total = query.Narrows ? null : (query.Family switch { "root" => stats.RootFiles, "install" => stats.InstallFiles, _ => stats.RootFiles + stats.InstallFiles }).ToString(CultureInfo.InvariantCulture);
        var next = more ? entries[^1].Key : limited ? position?.ToString() : null;
        LastFilesRowsRead = budget.Used;
        return new InventoryFilesPage(request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation, entries, total, next, limited && !more);
    }, token);

    /// <summary>All memberships of one ROOT FileDataID, summarized; null when the FileDataID has none.</summary>
    private InventoryFile? RootFile(long g, long fdid, InventoryFileQuery query, Budget budget, CancellationToken token)
    {
        using var cmd = Command("SELECT ckey,encoded,locale_n,flags_n,stored FROM inventory_origins INDEXED BY inventory_root_files WHERE g=$g AND fdid_n=$f", null, ("$g", g), ("$f", fdid));
        using var r = cmd.ExecuteReader();
        var summary = new FileSummary();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested(); budget.Read();
            var ckey = r.GetString(0); var locale = (uint)r.GetInt64(2); var flags = (uint)r.GetInt64(3); var stored = r.GetInt64(4) == 1;
            summary.Add(ckey, r.IsDBNull(1) ? null : r.GetInt64(1), stored, query.Matches("root", fdid, ckey, locale, flags, stored), locale);
        }
        return summary.Members == 0 ? null : summary.File("root:" + fdid.ToString(CultureInfo.InvariantCulture), "root", fdid.ToString(CultureInfo.InvariantCulture), null);
    }
    /// <summary>Install files in group order after <paramref name="after"/>; yields null once the budget ends a page early.</summary>
    private IEnumerable<InventoryFile?> InstallFiles(long g, InventoryFileQuery query, long after, Budget budget, CancellationToken token)
    {
        using var cmd = Command("SELECT grp,name,ckey,encoded,stored FROM inventory_origins INDEXED BY inventory_install_files WHERE g=$g AND family='install' AND grp>$a ORDER BY grp,id", null, ("$g", g), ("$a", after));
        using var r = cmd.ExecuteReader();
        long group = -1; string? name = null; var summary = new FileSummary();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var current = r.GetInt64(0);
            if (current != group)
            {
                if (group >= 0) { yield return summary.File("install:" + group.ToString(CultureInfo.InvariantCulture), "install", null, name); if (budget.Exhausted) { yield return null; yield break; } }
                group = current; name = r.GetString(1); summary = new FileSummary();
            }
            budget.Read();
            var ckey = r.GetString(2); var stored = r.GetInt64(4) == 1;
            summary.Add(ckey, r.IsDBNull(3) ? null : r.GetInt64(3), stored, query.Matches("install", null, ckey, null, null, stored), null);
        }
        if (group >= 0) yield return summary.File("install:" + group.ToString(CultureInfo.InvariantCulture), "install", null, name);
    }
    private sealed class FileSummary
    {
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);
        public int Members, Matching, Unresolved, Stored; private long? min, max; private uint locale; private bool anyLocale;
        public void Add(string ckey, long? encoded, bool stored, bool matches, uint? localeFlags)
        {
            Members++; if (matches) Matching++; if (stored) Stored++;
            keys.Add(ckey);
            if (encoded is { } bytes) { min = min is null ? bytes : Math.Min(min.Value, bytes); max = max is null ? bytes : Math.Max(max.Value, bytes); }
            else Unresolved++;
            if (localeFlags is { } value) { locale |= value; anyLocale = true; }
        }
        public InventoryFile File(string key, string family, string? fdid, string? name) => new(key, family, fdid, name, Members, Matching, keys.Count,
            min?.ToString(CultureInfo.InvariantCulture), max?.ToString(CultureInfo.InvariantCulture), Unresolved,
            anyLocale ? locale.ToString(CultureInfo.InvariantCulture) : null, Stored);
    }

    /// <summary>Ascending candidate ROOT FileDataIDs from the smallest driving index. Stops at group boundaries when the budget ends.</summary>
    private sealed class RootCandidates(DiscoveryCatalog catalog, long g, InventoryFileQuery query, FileStats stats, long after, Budget budget, CancellationToken token)
    {
        public bool Limited { get; private set; }
        /// <summary>The last FileDataID whose driver rows were all read when the budget ended the search.</summary>
        public long? ExaminedThrough { get; private set; }
        public IEnumerable<long> Read()
        {
            if (query.FileDataId is { } exact) return exact > after ? [exact] : [];
            if (query.ContentKey is { Length: 32 } key) return Ordered("SELECT fdid_n,0,0,0 FROM inventory_origins INDEXED BY inventory_content WHERE g=$g AND ckey=$c AND family='root' AND fdid_n>$a ORDER BY fdid_n", ("$c", key));
            if (query.ContentKey is { } prefix) return Prefix(prefix);
            var total = Math.Max(stats.RootMembers, 1);
            List<(uint Locale, uint Flags, long Members)>? pairs = null; long variantMembers = total;
            if (query.VariantFilter && stats.Variants <= VariantPlanBound)
            {
                pairs = [];
                using var cmd = catalog.Command("SELECT locale,flags,members FROM inventory_root_variants WHERE g=$g", null, ("$g", g));
                using var r = cmd.ExecuteReader();
                while (r.Read()) { var locale = (uint)r.GetInt64(0); var flags = (uint)r.GetInt64(1); if (query.MatchesVariant(locale, flags)) pairs.Add((locale, flags, r.GetInt64(2))); }
                variantMembers = pairs.Sum(pair => pair.Members);
                if (variantMembers == 0) return [];
            }
            var storedMembers = query.StoredOnly ? stats.StoredRootMembers : total;
            if (storedMembers == 0) return [];
            // Drive from the narrowest index that reads only candidate rows. Matches can cluster in one FileDataID range
            // (encrypted files do), so a large match count is no promise that the FileDataID-ordered scan fills a page;
            // that scan is the fallback only when no narrowing index applies.
            var mergeable = pairs is not null && pairs.Count <= VariantMergeBound;
            if (query.StoredOnly && (!mergeable || storedMembers <= variantMembers))
                return Ordered("SELECT fdid_n,locale_n,flags_n,1 FROM inventory_origins INDEXED BY inventory_stored_files WHERE g=$g AND stored=1 AND family='root' AND fdid_n>$a ORDER BY fdid_n");
            if (mergeable) return Merge(pairs!);
            return Ordered("SELECT fdid_n,locale_n,flags_n,stored FROM inventory_origins INDEXED BY inventory_root_files WHERE g=$g AND fdid_n>$a ORDER BY fdid_n");
        }
        private bool Passes(uint locale, uint flags, bool stored) => query.MatchesVariant(locale, flags) && (!query.StoredOnly || stored);
        // One index-ordered stream: rows of one FileDataID are consecutive; a group is a candidate when any row passes.
        private IEnumerable<long> Ordered(string sql, params (string, object?)[] extra)
        {
            using var cmd = catalog.Command(sql, null, [("$g", (object?)g), ("$a", (object?)after), .. extra]);
            using var r = cmd.ExecuteReader();
            long group = -1; var candidate = false;
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var fdid = r.GetInt64(0);
                if (fdid != group)
                {
                    if (group >= 0) { if (candidate) yield return group; if (budget.Exhausted) { Limited = true; ExaminedThrough = group; yield break; } }
                    group = fdid; candidate = false;
                }
                budget.Read();
                if (!candidate && (query.ContentKey is not null || Passes((uint)r.GetInt64(1), (uint)r.GetInt64(2), r.GetInt64(3) == 1))) candidate = true;
            }
            if (group >= 0 && candidate) yield return group;
        }
        private IEnumerable<long> Prefix(string prefix)
        {
            (string, object?) lo = ("$lo", prefix), hi = ("$hi", prefix + "g"), at = ("$g", g);
            var count = Convert.ToInt64(catalog.Scalar($"SELECT count(*) FROM (SELECT 1 FROM inventory_origins INDEXED BY inventory_content WHERE g=$g AND ckey>=$lo AND ckey<$hi LIMIT {ContentPrefixBound + 1})", null, at, lo, hi));
            if (count > ContentPrefixBound)
                throw new DiscoveryException("INVENTORY_FILTER_BROAD", $"More than {ContentPrefixBound:N0} memberships have a content key starting {prefix}. Type more of the key.");
            var ids = new SortedSet<long>();
            using (var cmd = catalog.Command("SELECT fdid_n FROM inventory_origins INDEXED BY inventory_content WHERE g=$g AND ckey>=$lo AND ckey<$hi AND family='root' AND fdid_n>$a", null, at, lo, hi, ("$a", after)))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { token.ThrowIfCancellationRequested(); budget.Read(); ids.Add(r.GetInt64(0)); }
            return Bounded(ids);
        }
        // Stops only after one candidate was offered in this request, so every page makes progress.
        private IEnumerable<long> Bounded(IEnumerable<long> ids)
        {
            var offered = false;
            foreach (var id in ids) { if (offered && budget.Exhausted) { Limited = true; ExaminedThrough = id - 1; yield break; } offered = true; yield return id; }
        }
        // Each matching (locale, flags) pair is a FileDataID-ordered index range; merge them lazily: 8 rows per pair
        // first, then 64 per refill. One FileDataID can repeat inside a pair (two blocks with equal flags), so a refill
        // resumes after (FileDataID, rowid).
        private IEnumerable<long> Merge(List<(uint Locale, uint Flags, long Members)> pairs)
        {
            using var cmd = catalog.Command("SELECT fdid_n,stored,rowid FROM inventory_origins INDEXED BY inventory_root_variant_files WHERE g=$g AND locale_n=$l AND flags_n=$f AND fdid_n>=$a AND (fdid_n,rowid)>($a,$r) ORDER BY fdid_n,rowid LIMIT $n", null,
                ("$g", g), ("$l", 0L), ("$f", 0L), ("$a", 0L), ("$r", 0L), ("$n", 0L));
            cmd.Prepare();
            var buffers = new Queue<(long Fdid, bool Stored, long Row)>[pairs.Count]; var exhausted = new bool[pairs.Count];
            var heads = new PriorityQueue<int, long>();
            bool Fill(int i, long from, long row, int size)
            {
                cmd.Parameters[1].Value = (long)pairs[i].Locale; cmd.Parameters[2].Value = (long)pairs[i].Flags; cmd.Parameters[3].Value = from; cmd.Parameters[4].Value = row; cmd.Parameters[5].Value = (long)size;
                var queue = buffers[i] ??= new(); var read = 0;
                using (var r = cmd.ExecuteReader()) while (r.Read()) { budget.Read(); queue.Enqueue((r.GetInt64(0), r.GetInt64(1) == 1, r.GetInt64(2))); read++; }
                exhausted[i] = read < size; return queue.Count > 0;
            }
            for (var i = 0; i < pairs.Count; i++) { token.ThrowIfCancellationRequested(); if (Fill(i, after, long.MaxValue, 8)) heads.Enqueue(i, buffers[i].Peek().Fdid); }
            long group = -1; var candidate = false;
            while (heads.TryDequeue(out var i, out var fdid))
            {
                token.ThrowIfCancellationRequested();
                if (fdid != group)
                {
                    if (group >= 0) { if (candidate) yield return group; if (budget.Exhausted) { Limited = true; ExaminedThrough = group; yield break; } }
                    group = fdid; candidate = false;
                }
                var (_, stored, row) = buffers[i].Dequeue();
                if (!query.StoredOnly || stored) candidate = true;
                if (buffers[i].Count > 0) heads.Enqueue(i, buffers[i].Peek().Fdid);
                else if (!exhausted[i] && Fill(i, fdid, row, 64)) heads.Enqueue(i, buffers[i].Peek().Fdid);
            }
            if (group >= 0 && candidate) yield return group;
        }
    }

    public Task<InventoryFileDetail> ReadInventoryFileAsync(InventoryFileRequest request, CancellationToken token) => Schedule(() =>
    {
        var (query, file, after) = InventoryService.Validate(request);
        var g = ReadyFilesGeneration(request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation);
        // The file's own rows: at most MaxFileMemberships (checked at seal) through the install-group or FileDataID index.
        var where = file.Install ? "INDEXED BY inventory_install_files WHERE g=$g AND family='install' AND grp=$k" : "INDEXED BY inventory_root_files WHERE g=$g AND fdid_n=$k";
        int total = 0, matching = 0, read = 0; string? name = null;
        using (var count = Command($"SELECT family,ckey,locale_n,flags_n,stored,fdid_n,name FROM inventory_origins {where}", null, ("$g", g), ("$k", file.Value)))
        using (var r = count.ExecuteReader())
            while (r.Read())
            {
                token.ThrowIfCancellationRequested(); read++; total++; name ??= r.IsDBNull(6) ? null : r.GetString(6);
                if (query.Matches(r.GetString(0), r.IsDBNull(5) ? null : r.GetInt64(5), r.GetString(1), r.IsDBNull(2) ? null : (uint)r.GetInt64(2), r.IsDBNull(3) ? null : (uint)r.GetInt64(3), r.GetInt64(4) == 1)) matching++;
            }
        if (total == 0) throw new DiscoveryException("INVENTORY_FILE_NOT_FOUND", "This prepared inventory has no such file.");
        var members = new List<InventoryFileMembership>(); var bytes = 0; var more = false;
        using (var page = Command($"SELECT id,family,ckey,ekey,decoded,encoded,locale_n,flags_n,name_hash,raw,stored,fdid_n FROM inventory_origins {where} AND id>$a ORDER BY id LIMIT $n", null,
            ("$g", g), ("$k", file.Value), ("$a", after), ("$n", request.Limit + 1)))
        using (var r = page.ExecuteReader())
        {
            string? Text(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            string? Number(int i) => r.IsDBNull(i) ? null : r.GetInt64(i).ToString(CultureInfo.InvariantCulture);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested(); read++;
                if (members.Count == request.Limit) { more = true; break; }
                var id = r.GetInt64(0); var family = r.GetString(1); var ckey = r.GetString(2); var stored = r.GetInt64(10) == 1;
                uint? locale = r.IsDBNull(6) ? null : (uint)r.GetInt64(6), flags = r.IsDBNull(7) ? null : (uint)r.GetInt64(7);
                var matches = query.Matches(family, r.IsDBNull(11) ? null : r.GetInt64(11), ckey, locale, flags, stored);
                var raw = r.GetString(9);
                int? block = null, entry = null, ordinal = null; InventoryRawContentFlags? rawFlags = null;
                if (family == "root")
                {
                    using var json = JsonDocument.Parse(raw); var value = json.RootElement; var parts = value.GetProperty("RawContentFlags");
                    block = value.GetProperty("blockOrdinal").GetInt32(); entry = value.GetProperty("entryOrdinal").GetInt32();
                    rawFlags = new(parts.GetProperty("First").GetUInt32().ToString(CultureInfo.InvariantCulture), parts.GetProperty("Second").GetUInt32().ToString(CultureInfo.InvariantCulture), parts.GetProperty("Third").GetByte());
                }
                else ordinal = int.Parse(raw, CultureInfo.InvariantCulture);
                var member = new InventoryFileMembership(id.ToString(CultureInfo.InvariantCulture), family, block, entry, ordinal,
                    locale?.ToString(CultureInfo.InvariantCulture), flags?.ToString(CultureInfo.InvariantCulture), rawFlags, Text(8), ckey, Text(3), Number(4), Number(5), stored, matches);
                var size = JsonSerializer.SerializeToUtf8Bytes(member, Json).Length + 2;
                if (bytes + size > FilesFrameBytes) { more = true; break; }
                members.Add(member); bytes += size;
            }
        }
        LastFileRowsRead = read;
        return new InventoryFileDetail(request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation, file.ToString(),
            file.Install ? "install" : "root", file.Install ? null : file.Value.ToString(CultureInfo.InvariantCulture), name, total, matching, members, more && members.Count > 0 ? members[^1].Id : null);
    }, token);
}
