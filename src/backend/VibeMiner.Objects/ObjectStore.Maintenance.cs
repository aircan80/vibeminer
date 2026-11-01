using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

public sealed partial class ObjectStore
{
    // ---------------------------------------------------------------- Scan

    /// <summary>
    /// Rebuilds the index from the packs alone (D126). Entries are replayed in store order (their sequence numbers) across every pack:
    /// the first copy of a key wins, a tombstone removes it. Each entry's SHA-256 and block checksums are checked, then the checks Put
    /// makes (D372); one that fails is left out and reported. It runs in one transaction on the live index, so every reader, in this
    /// process or another, sees the old index until the new one is complete. last_read is kept.
    /// </summary>
    public Task<ScanReport> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default) =>
        MaintainAsync(() => ScanCoreAsync(progress, ct), ct);

    private async Task<ScanReport> ScanCoreAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        {
            // Seal the active pack first, durably, so a Scan that fails leaves no pack marked active without a handle.
            if (active is not null) Durably(tx => SetPackState(tx, activeSeq, PackState.Sealed));
            active?.Dispose(); active = null; activeSeq = 0; activeLength = committedLength = 0;
            var c = wconn!;
            ScanReport report;
            using (var tx = c.BeginTransaction())
            {
                Exec(c, tx, "CREATE TEMP TABLE kept_reads(k BLOB PRIMARY KEY, last_read INTEGER) WITHOUT ROWID");
                Exec(c, tx, "INSERT INTO temp.kept_reads SELECT k, last_read FROM objects WHERE last_read IS NOT NULL");
                var listed = ListedPacks(c, tx);
                Exec(c, tx, "DELETE FROM objects"); Exec(c, tx, "DELETE FROM history"); Exec(c, tx, "DELETE FROM packs");
                (report, _) = await ReplayPacksAsync(c, tx, listed, progress, ct);
                Exec(c, tx, "UPDATE objects SET last_read = (SELECT r.last_read FROM temp.kept_reads r WHERE r.k = objects.k) WHERE k IN (SELECT k FROM temp.kept_reads)");
                Exec(c, tx, "DROP TABLE temp.kept_reads");
                At(StorePoint.RebuildBuilt);
                tx.Commit();
            }
            At(StorePoint.RebuildSwapped);
            wlookup?.Dispose(); wlookup = ObjectIndex.LookupCommand(c);
            LoadStoreRow();
            verified.Clear();
            try { ObjectIndex.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)"); } catch (SqliteException) { /* readers hold the WAL; a later checkpoint gets it */ }
            return report;
        }
    }

    /// <summary>At open, with no usable index: build one beside it from the packs, then move it into place. Nothing else has it open.</summary>
    private async Task RebuildIndexAsync(CancellationToken ct)
    {
        var scratch = new[] { RebuildPath, RebuildPath + "-wal", RebuildPath + "-shm" };
        foreach (var f in scratch) File.Delete(f);
        try
        {
            using var c = ObjectIndex.Open(RebuildPath, readOnly: false);
            ObjectIndex.Create(c, new byte[16], NowMs, 1, Kind);
            using (var tx = c.BeginTransaction())
            {
                var (_, id) = await ReplayPacksAsync(c, tx, null, null, ct);
                Exec(c, tx, "UPDATE store SET store_id = $id, row_check = vm_store_check($id, format, created_at, next_seq, object_count, object_digest, history_count, history_digest, clean_close) WHERE id = 1", ("$id", id ?? System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));
                tx.Commit();
            }
            CarryLastRead(c);
            ObjectIndex.Exec(c, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (ObjectStoreException)
        {
            // A refused rebuild (newer format, foreign pack) leaves nothing behind.
            SqliteConnection.ClearAllPools();
            foreach (var f in scratch) File.Delete(f);
            throw;
        }
        At(StorePoint.RebuildBuilt);
        foreach (var f in new[] { indexPath + "-wal", indexPath + "-shm" }) File.Delete(f);
        File.Move(RebuildPath, indexPath, overwrite: true);
        DirectorySync.Sync(directory);
        At(StorePoint.RebuildSwapped);
    }

    /// <summary>The live index's pack rows (number, committed length), only to report what a Scan replays beyond them; null if unreadable.</summary>
    private static Dictionary<int, long>? ListedPacks(SqliteConnection c, SqliteTransaction tx)
    {
        try
        {
            var listed = new Dictionary<int, long>();
            using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT seq, committed_length FROM packs";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (r.GetValue(0) is long s && s is > 0 and <= int.MaxValue && r.GetValue(1) is long committed) listed[(int)s] = committed;
            return listed;
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// Replays every readable pack. <paramref name="listed"/> (a Scan on a running store) only adds problems naming the packs it
    /// didn't list and the bytes walked past a listed pack's committed length; every entry gets the same checks either way.
    /// </summary>
    private async Task<(ScanReport report, byte[]? storeId)> ReplayPacksAsync(SqliteConnection c, SqliteTransaction tx, Dictionary<int, long>? listed,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var found = new List<string>();
        var walkers = new List<(PackWalker walker, SafeFileHandle handle)>();
        byte[]? id = null;
        long entries = 0, dups = 0, tombs = 0, corrupt = 0, unverifiable = 0, bytes = 0;
        ulong maxSeq = 0;
        try
        {
            foreach (var (seq, path) in DiskPacks().OrderBy(p => p.seq))
            {
                var check = CheckPackHeader(path, seq, null, Kind);
                if (check == PackFormat.HeaderCheck.Newer) throw NewerFormat($"pack {seq}");
                if (check != PackFormat.HeaderCheck.Valid) { found.Add($"pack {seq} has no readable header; skipped"); continue; }
                var packId = ReadPackStoreId(path);
                if (id is null) id = packId;
                else if (!packId.AsSpan().SequenceEqual(id)) throw new ObjectStoreException(ObjectStoreCodes.ForeignPack, $"Pack {seq} belongs to another object store.");
                var h = PackFiles.OpenRead(path);
                walkers.Add((new PackWalker(h, seq, PackFormat.PackHeaderBytes, RandomAccess.GetLength(h), Kind), h));
            }
            var queue = new PriorityQueue<(PackWalker w, SafeFileHandle h), (ulong, int)>();
            var ends = new Dictionary<int, long>();
            var tombBytes = new Dictionary<int, long>();
            async ValueTask Advance(PackWalker w, SafeFileHandle h)
            {
                var status = await w.NextAsync(ct);
                if (status == WalkStatus.Entry) { queue.Enqueue((w, h), (w.Current.Sequence, w.Pack)); return; }
                if (status == WalkStatus.Newer) throw NewerFormat($"entry at {w.Offset} in pack {w.Pack}");
                if (status == WalkStatus.Torn) found.Add($"pack {w.Pack}: unreadable entry at {w.Offset}; the rest of the pack is unreachable");
                ends[w.Pack] = w.Offset;
            }
            foreach (var (w, h) in walkers) await Advance(w, h);
            using var lookup = ObjectIndex.LookupCommand(c, tx);
            while (queue.TryDequeue(out var item, out _))
            {
                ct.ThrowIfCancellationRequested();
                var (w, h) = item;
                var e = w.Current; var at = w.CurrentOffset;
                entries++; bytes += e.EntryBytes; maxSeq = Math.Max(maxSeq, e.Sequence);
                // The writer's checks, key type included: entries it never indexed (past a committed length, in a pack it doesn't
                // list, anywhere when the index is gone) must not get in unproven. A refused one is left out like a corrupt one.
                var (check, reason) = await EntryIO.CheckPayloadAsync(h, at, e, keyType: true, null, ct);
                if (check == PayloadCheck.Corrupt) { corrupt++; if (found.Count < 100) found.Add($"{e.Key} at {at} in pack {w.Pack}: {reason}; left out"); }
                else if (check == PayloadCheck.TypeFailed) { unverifiable++; if (found.Count < 100) found.Add($"{e.Key} at {at} in pack {w.Pack} fails its key check ({reason}); left out"); }
                else if (e.Tombstone)
                {
                    tombs++; tombBytes[w.Pack] = tombBytes.GetValueOrDefault(w.Pack) + PackFormat.EntryHeaderBytes;
                    Exec(c, tx, "DELETE FROM objects WHERE k = $k", ("$k", e.Key.ToIndexKey()));
                    if (e.Key.Type == ObjectKeyType.HistoryRecord) Exec(c, tx, "DELETE FROM history WHERE record = $r", ("$r", e.Key.KeyBytes()));
                }
                else if (ObjectIndex.Lookup(lookup, e.Key.ToIndexKey()) is not null) dups++;
                else
                {
                    HistoryRecordFormat.Parsed? history = null;
                    if (e.Key.Type == ObjectKeyType.HistoryRecord && (history = await EntryIO.ReadHistoryAsync(h, at, e, ct)) is null)
                    { unverifiable++; found.Add($"history record {e.Key} doesn't parse; left out"); }
                    else
                    {
                        Exec(c, tx, "INSERT INTO objects(k, pack, offset, length, sha256) VALUES($k, $p, $o, $l, $s)",
                            ("$k", e.Key.ToIndexKey()), ("$p", w.Pack), ("$o", at), ("$l", e.Length), ("$s", e.Sha256));
                        if (history is { } p)
                            Exec(c, tx, "INSERT OR IGNORE INTO history VALUES($r, $t, $pr, $rg, $kd, $doc, $u)", ("$r", e.Key.KeyBytes()), ("$t", p.ObservedMs),
                                ("$pr", p.Product), ("$rg", p.Region), ("$kd", (int)p.Kind), ("$doc", p.Document.KeyBytes()), ("$u", p.Url));
                    }
                }
                if (entries % 65_536 == 0) progress?.Report(new(entries, bytes));
                await Advance(w, h);
            }
            if (listed is not null)
                foreach (var (w, _) in walkers)
                {
                    var end = ends.GetValueOrDefault(w.Pack, PackFormat.PackHeaderBytes);
                    if (!listed.TryGetValue(w.Pack, out var committed)) found.Add($"pack {w.Pack} had no row in the index; replayed with every check");
                    else if (end > committed) found.Add($"pack {w.Pack}: {end - committed} bytes past its committed length replayed with every check");
                }
            // Every pack sealed at the end of its last whole entry: the writer starts a fresh pack for the next append.
            foreach (var (w, _) in walkers)
                Exec(c, tx, "INSERT INTO packs VALUES($s, $st, $c, 0, vm_pack_check($s, $st, $c))", ("$s", w.Pack), ("$st", (int)PackState.Sealed), ("$c", ends.GetValueOrDefault(w.Pack, PackFormat.PackHeaderBytes)));
            Exec(c, tx, "UPDATE packs SET live_bytes = (SELECT COALESCE(SUM(96 + length + 4 * ((length + 65535) / 65536)), 0) FROM objects WHERE pack = packs.seq)");
            foreach (var (pack, kept) in tombBytes) Exec(c, tx, "UPDATE packs SET live_bytes = live_bytes + $k WHERE seq = $s", ("$k", kept), ("$s", pack));
            var measured = ObjectIndex.Measure(c, tx);
            Exec(c, tx, "UPDATE store SET next_seq = $n, object_count = $oc, object_digest = $od, history_count = $hc, history_digest = $hd, " +
                "row_check = vm_store_check(store_id, format, created_at, $n, $oc, $od, $hc, $hd, clean_close) WHERE id = 1",
                ("$n", (long)maxSeq + 1), ("$oc", measured.Objects), ("$od", measured.ObjectDigest), ("$hc", measured.History), ("$hd", measured.HistoryDigest));
            var live = Count(c, tx, "SELECT count(*) FROM objects");
            // Dangling history (a record whose document no copy holds) is reported; the record stays as stored.
            var dangling = Count(c, tx, "SELECT count(*) FROM history WHERE NOT EXISTS (SELECT 1 FROM objects WHERE k = CAST(X'0500' || history.document AS BLOB))");  // || yields TEXT, which never equals a BLOB key
            if (dangling > 0) found.Add($"{dangling} history records name a document the store doesn't have");
            progress?.Report(new(entries, bytes));
            problems.AddRange(found);
            return (new ScanReport(walkers.Count, entries, live, dups, tombs, corrupt, unverifiable, found), id);
        }
        finally { foreach (var (_, h) in walkers) h.Dispose(); }
    }

    private void CarryLastRead(SqliteConnection c)
    {
        if (!File.Exists(indexPath)) return;
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ATTACH DATABASE $p AS old"; cmd.Parameters.AddWithValue("$p", indexPath);
            cmd.ExecuteNonQuery();
            ObjectIndex.Exec(c, "UPDATE objects SET last_read = (SELECT o.last_read FROM old.objects o WHERE o.k = objects.k) WHERE EXISTS (SELECT 1 FROM old.objects o WHERE o.k = objects.k AND o.last_read IS NOT NULL)");
            ObjectIndex.Exec(c, "DETACH DATABASE old");
        }
        catch (SqliteException) { /* the old index is unreadable: last_read starts empty */ }
    }

    // ---------------------------------------------------------------- Scrub

    /// <summary>
    /// The full check on request (D126), for layer 3 to schedule: integrity_check and the totals over every row, then Verify of every
    /// object. A failing index is rebuilt from the packs in place, as an open after a crash would; the report says what was found.
    /// </summary>
    public async Task<ScrubReport> ScrubAsync(bool full = false, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureWriter();
        // Measure on reader connections in one pinned snapshot, so writes go on meanwhile and none of them passes for damage.
        var (_, problem, _, _) = await Task.Run(() => ObjectIndex.Validate(indexPath, full: true, ct, () => At(StorePoint.IndexMeasuring)), ct);
        var rebuilt = false;
        if (problem is not null)
            (problem, rebuilt) = await MaintainAsync(async () =>
            {
                // Confirmed with the writer held before anything is rebuilt.
                var (_, confirmed, _, _) = ObjectIndex.Validate(indexPath, full: true, ct);
                if (confirmed is null) return (confirmed, false);
                problems.Add($"{confirmed}; rebuilt from the packs");
                await ScanCoreAsync(null, ct);
                return (confirmed, true);
            }, ct);
        return new ScrubReport(problem, rebuilt, await VerifyAsync(full, progress, ct));
    }

    /// <summary>Read-only stores: the same full index check, reported without a rebuild (only the writer can rebuild).</summary>
    internal async Task<ScrubReport> ScrubReadOnlyAsync(bool full, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var (_, problem, _, settled) = await Task.Run(() => ObjectIndex.Validate(indexPath, full: true, ct, () => At(StorePoint.IndexMeasuring), immutableIndex), ct);
        VerifyReport verify;
        try { verify = await VerifyAsync(full, progress, ct); }
        catch (ObjectStoreException e) when (problem is not null && e.Code == ObjectStoreCodes.IndexUnreadable) { verify = new(0, 0, 0, 0, [], []); }
        return new ScrubReport(problem, false, verify, settled);
    }

    // ---------------------------------------------------------------- Verify

    /// <summary>
    /// Scrubs every indexed object: header, SHA-256 and every block, plus Put's checks (namespace, length bound, key type) when
    /// <paramref name="full"/>.
    /// Read-only stores can verify too. Reports at most 1,000 keys of each kind; the counts are exact.
    /// </summary>
    public async Task<VerifyReport> VerifyAsync(bool full = false, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        ThrowIfClosed();
        try { return await VerifyCoreAsync(full, progress, ct); }
        catch (Exception e) when (e is SqliteException or InvalidCastException or InvalidOperationException or FormatException or OverflowException)
        { throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index can't be read; a Scan rebuilds it from the packs.", e); }
    }

    private async Task<VerifyReport> VerifyCoreAsync(bool full, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        long checkedCount = 0, bytes = 0, corruptCount = 0, lostCount = 0;
        var corrupt = new List<ObjectKey>(); var lost = new List<ObjectKey>();
        byte[] last = [];
        using var c = ObjectIndex.Open(indexPath, readOnly: true, immutableIndex);
        while (true)
        {
            var rows = new List<(ObjectKey key, IndexRow row)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT k, pack, offset, length, sha256 FROM objects WHERE k > $k ORDER BY k LIMIT 1024";
                cmd.Parameters.AddWithValue("$k", last);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.GetValue(0) is not byte[] k) { corruptCount++; continue; }
                    // A page that doesn't move past the cursor means rows out of key order: refuse rather than walk in circles.
                    if (k.AsSpan().SequenceCompareTo(last) <= 0) throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index has rows out of key order; a Scan rebuilds it from the packs.");
                    last = k;
                    // A key or row of the wrong shape is a damaged index row: counted corrupt, never thrown.
                    if (k.Length != ObjectKey.EncodedBytes) { corruptCount++; continue; }
                    var pack = r.GetValue(1) is long p && p is > 0 and <= int.MaxValue ? (int)p : 0;
                    rows.Add((ObjectKey.FromIndexKey(k), new IndexRow(pack, r.GetValue(2) as long? ?? -1, r.GetValue(3) as long? ?? -1, r.GetValue(4) as byte[] ?? [])));
                }
            }
            if (rows.Count == 0) break;
            foreach (var (key, first) in rows.OrderBy(x => x.row.Pack).ThenBy(x => x.row.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var state = await VerifyOneAsync(key, first, full, ct);
                // Compaction may have moved the row meanwhile: a failure counts only if the current row fails too.
                if (state != ReadState.Present && await LookupAsync(key, ct) is { } now && (now.Pack != first.Pack || now.Offset != first.Offset))
                    state = await VerifyOneAsync(key, now, full, ct);
                checkedCount++; bytes += first.Length;
                if (state == ReadState.Corrupt) { corruptCount++; if (corrupt.Count < 1000) corrupt.Add(key); }
                else if (state == ReadState.Lost) { lostCount++; if (lost.Count < 1000) lost.Add(key); }
            }
            progress?.Report(new(checkedCount, bytes));
        }
        return new(checkedCount, bytes, corruptCount, lostCount, corrupt, lost);
    }

    private async Task<ReadState> VerifyOneAsync(ObjectKey key, IndexRow row, bool full, CancellationToken ct)
    {
        if (!row.Sane) return ReadState.Corrupt;
        var lease = packReaders.Acquire(row.Pack);
        if (lease is null) return ReadState.Lost;
        using (lease.Value)
        {
            var header = new byte[PackFormat.EntryHeaderBytes];
            if (await PackFiles.ReadFullyAsync(lease.Value.Handle, header, row.Offset, ct) != header.Length) return ReadState.Corrupt;
            if (CheckHeader(header, key, row, Kind, out var e) is not null) return ReadState.Corrupt;
            var (check, _) = await EntryIO.CheckPayloadAsync(lease.Value.Handle, row.Offset, e, full, null, ct);
            if (check != PayloadCheck.Valid) return ReadState.Corrupt;
            MarkVerified(row);
            return ReadState.Present;
        }
    }

    // ---------------------------------------------------------------- Compact

    // Packs found holding a corrupt live entry: not picked again this session, so they don't take every Compact's slots.
    private readonly HashSet<int> corruptPacks = [];

    /// <summary>
    /// Rewrites sealed packs whose dead bytes (deleted or duplicate entries) reach the threshold (D125). Live entries are verified and
    /// copied byte for byte, keeping their sequence numbers; tombstones are kept (and count as kept bytes) so a later Scan can't bring a
    /// deleted object back. A pack holding a corrupt live entry is left as it is and counted in <see cref="CompactReport.SkippedCorrupt"/>.
    /// New pack synced, then the index switched, then the old file deleted: a crash at any point leaves one valid copy indexed.
    /// </summary>
    public Task<CompactReport> CompactAsync(CompactOptions? compact = null, CancellationToken ct = default) =>
        MaintainAsync(async () =>
        {
            compact ??= new();
            var candidates = new List<(int seq, long committed, long live)>();
            using (var cmd = wconn!.CreateCommand())
            {
                cmd.CommandText = "SELECT seq, committed_length, live_bytes FROM packs WHERE state = $s";
                cmd.Parameters.AddWithValue("$s", (int)PackState.Sealed);
                using var r = cmd.ExecuteReader();
                while (r.Read()) candidates.Add((r.GetInt32(0), r.GetInt64(1), r.GetInt64(2)));
            }
            var chosen = candidates.Where(p => !corruptPacks.Contains(p.seq) && p.committed > PackFormat.PackHeaderBytes && p.committed - PackFormat.PackHeaderBytes - p.live > 0 &&
                    (p.committed - PackFormat.PackHeaderBytes - p.live) >= compact.MinDeadFraction * (p.committed - PackFormat.PackHeaderBytes))
                .OrderByDescending(p => p.committed - p.live).Take(compact.MaxPacks).ToList();
            int done = 0, skipped = 0; long before = 0, after = 0, moved = 0;
            foreach (var (seq, committed, _) in chosen)
            {
                ct.ThrowIfCancellationRequested();
                var result = await CompactPackAsync(seq, committed, ct);
                if (result is null) continue;
                if (result.Value.skipped > 0) { skipped++; corruptPacks.Add(seq); continue; }
                done++; before += committed; after += result.Value.length; moved += result.Value.moved;
            }
            return new CompactReport(done, before, after, moved, skipped);
        }, ct);

    private async Task<(long length, long moved, long skipped)?> CompactPackAsync(int seq, long committed, CancellationToken ct)
    {
        var target = TakePackNumber();
        var targetPath = PackPath(target);
        Durably(tx => InsertPack(tx, target, PackState.Compacting, PackFormat.PackHeaderBytes));
        At(StorePoint.CompactTargetListed);
        var moves = new List<(ObjectKey key, long offset, long length, long from, byte[] sha)>();
        long at = PackFormat.PackHeaderBytes, live = 0;
        var ok = true; var corruptFound = false;
        try
        {
        using (var outHandle = PackFiles.OpenWrite(targetPath, FileMode.CreateNew))
        using (var source = PackFiles.OpenRead(PackPath(seq)))
        {
            var header = new byte[PackFormat.PackHeaderBytes];
            PackFormat.WritePackHeader(header, target, storeId, NowMs, Kind);
            await RandomAccess.WriteAsync(outHandle, header, 0, ct);
            var walker = new PackWalker(source, seq, PackFormat.PackHeaderBytes, committed, Kind);
            var raw = new byte[PackFormat.EntryHeaderBytes];
            while (true)
            {
                var status = await walker.NextAsync(ct);
                if (status == WalkStatus.End) break;
                if (status != WalkStatus.Entry) { ok = false; problems.Add($"pack {seq} has an unreadable entry at {walker.Offset}; not compacted"); break; }
                var e = walker.Current; var from = walker.CurrentOffset;
                if (!e.Tombstone)
                {
                    var row = LookupWriter(e.Key);
                    if (row is null || row.Pack != seq || row.Offset != from) continue;  // deleted or a later duplicate: dead
                }
                await PackFiles.ReadFullyAsync(source, raw, from, ct);
                var entryAt = at;
                var (check, _) = await EntryIO.CheckPayloadAsync(source, from, e, keyType: false,
                    async (slice, offset) => await RandomAccess.WriteAsync(outHandle, slice, entryAt + PackFormat.EntryHeaderBytes + offset, ct), ct);
                if (check != PayloadCheck.Valid)
                {
                    // Never turn Corrupt into Absent: the pack stays as it is, so Read and Verify keep reporting the damage.
                    ok = false; corruptFound = true;
                    problems.Add($"pack {seq} holds a corrupt object ({e.Key}); not compacted");
                    break;
                }
                var table = new byte[PackFormat.TableBytes(e.Length)];
                await PackFiles.ReadFullyAsync(source, table, from + PackFormat.EntryHeaderBytes + e.Length, ct);
                await RandomAccess.WriteAsync(outHandle, table, entryAt + PackFormat.EntryHeaderBytes + e.Length, ct);
                await RandomAccess.WriteAsync(outHandle, raw, entryAt, ct);
                if (!e.Tombstone) moves.Add((e.Key, entryAt, e.Length, from, e.Sha256));
                live += e.EntryBytes;
                at += e.EntryBytes;
            }
            if (ok) { RandomAccess.FlushToDisk(outHandle); DirectorySync.Sync(packsDirectory); }
        }
        }
        catch (Exception e) when (e is not SimulatedCrashException)
        {
            // Canceled or failed while copying: nothing points at the target yet, so it goes, file first.
            File.Delete(targetPath);
            ExecWriter("DELETE FROM packs WHERE seq = $s", ("$s", target));
            throw;
        }
        if (!ok)
        {
            File.Delete(targetPath);
            ExecWriter("DELETE FROM packs WHERE seq = $s", ("$s", target));
            return corruptFound ? (committed, 0, 1) : null;
        }
        At(StorePoint.CompactCopied);
        // Durable before the old file goes (D125): otherwise power loss could keep the delete and lose the switch.
        var moved = totals;
        foreach (var (key, offset, length, from, sha) in moves)
            moved = moved with { ObjectDigest = moved.ObjectDigest ^ ObjectIndex.ObjectDigest(key.ToIndexKey(), seq, from, length, sha) ^ ObjectIndex.ObjectDigest(key.ToIndexKey(), target, offset, length, sha) };
        Durably(tx =>
        {
            foreach (var (key, offset, _, _, _) in moves)
                ExecWriter("UPDATE objects SET pack = $p, offset = $o WHERE k = $k", tx, ("$p", target), ("$o", offset), ("$k", key.ToIndexKey()));
            WriteStoreRow(tx, (long)nextSeq, moved);
            ExecWriter("UPDATE packs SET state = $st, committed_length = $c, live_bytes = $l, row_check = vm_pack_check(seq, $st, $c) WHERE seq = $s", tx,
                ("$st", (int)PackState.Sealed), ("$c", at), ("$l", live), ("$s", target));
            SetPackState(tx, seq, PackState.Retired);
        });
        totals = moved;
        At(StorePoint.CompactSwitched);
        packReaders.Invalidate(seq);
        File.Delete(PackPath(seq)); DirectorySync.Sync(packsDirectory);
        At(StorePoint.CompactOldDeleted);
        ExecWriter("DELETE FROM packs WHERE seq = $s", ("$s", seq));
        return (at, moves.Count, 0);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static long Count(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }
}
