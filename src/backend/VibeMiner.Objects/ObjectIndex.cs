using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

/// <summary>An index row. <see cref="Sane"/> is false when its values can't name an entry (a damaged index); reads then say Corrupt.</summary>
internal sealed record IndexRow(int Pack, long Offset, long Length, byte[] Sha256)
{
    public bool Sane => Pack > 0 && Offset >= PackFormat.PackHeaderBytes && Length is > 0 and <= PackFormat.MaxPayloadBytes && Sha256.Length == 32;
}

internal enum PackState { Active = 0, Sealed = 1, Compacting = 2, Retired = 3 }

/// <summary>Row counts and digests of the objects and history tables, kept in the CRC-checked store row.</summary>
internal readonly record struct StoreTotals(long Objects, long ObjectDigest, long History, long HistoryDigest);

/// <summary>
/// objects.sqlite3, schema version 1 (user_version). A cache of the packs: Scan rebuilds every row except last_read.
/// WAL, synchronous=NORMAL for batches: a batch commit lost to power loss is found again by tail recovery, because the active pack
/// was fsynced first. Listing or sealing a pack and switching a compaction commit with FULL (ObjectStore.Durably).
/// </summary>
internal static class ObjectIndex
{
    public const int SchemaVersion = 1;
    public const string FileName = "objects.sqlite3";

    private const string Schema = """
        CREATE TABLE store(id INTEGER PRIMARY KEY CHECK(id = 1), store_id BLOB NOT NULL CHECK(length(store_id) = 16),
            format INTEGER NOT NULL, created_at INTEGER NOT NULL, next_seq INTEGER NOT NULL,
            object_count INTEGER NOT NULL, object_digest INTEGER NOT NULL, history_count INTEGER NOT NULL, history_digest INTEGER NOT NULL,
            row_check INTEGER NOT NULL);
        CREATE TABLE packs(seq INTEGER PRIMARY KEY, state INTEGER NOT NULL, committed_length INTEGER NOT NULL, live_bytes INTEGER NOT NULL,
            row_check INTEGER NOT NULL);
        CREATE TABLE objects(k BLOB PRIMARY KEY, pack INTEGER NOT NULL, offset INTEGER NOT NULL, length INTEGER NOT NULL,
            sha256 BLOB NOT NULL, last_read INTEGER) WITHOUT ROWID;
        CREATE TABLE history(record BLOB PRIMARY KEY, observed_at INTEGER NOT NULL, product TEXT NOT NULL, region TEXT NOT NULL,
            kind INTEGER NOT NULL, document BLOB NOT NULL, url TEXT NOT NULL) WITHOUT ROWID;
        CREATE INDEX history_order ON history(product, region, kind, observed_at, record);
        CREATE INDEX history_time ON history(observed_at, record);
        """;

    public static SqliteConnection Open(string path, bool readOnly)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5,
        }.ToString());
        c.Open();
        // The rows recovery trusts most (where a pack's committed bytes end, the next sequence number) carry a CRC of their own:
        // SQLite has no page checksums, so a flipped bit there would otherwise pass every check and steer recovery wrong.
        c.CreateFunction("vm_pack_check", (long seq, long state, long committed) => PackCheck(seq, state, committed), isDeterministic: true);
        c.CreateFunction("vm_store_check", (byte[] id, long format, long created, long next, long oc, long od, long hc, long hd) =>
            StoreCheck(id, format, created, next, new(oc, od, hc, hd)), isDeterministic: true);
        // Order-independent digests of every row, so open can tell a dropped, added or altered row from the totals in the store row.
        c.CreateAggregate("vm_object_digest", 0L, (long acc, byte[] k, long pack, long offset, long length, byte[] sha) => acc ^ ObjectDigest(k, pack, offset, length, sha), isDeterministic: true);
        c.CreateAggregate("vm_history_digest", 0L, (long acc, byte[] record, long observed, string product, string region, long kind, byte[] document, string url) =>
            acc ^ HistoryDigest(record, observed, product, region, kind, document, url), isDeterministic: true);
        Exec(c, readOnly ? "PRAGMA busy_timeout=5000; PRAGMA cache_size=-65536;"
            : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA temp_store=FILE; PRAGMA cache_size=-65536;");
        return c;
    }

    /// <summary>The schema version of an existing index, read without writing anything; null when the file is missing.</summary>
    public static int? ReadVersion(string path)
    {
        if (!File.Exists(path)) return null;
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Create(SqliteConnection c, byte[] storeId, long createdMs, long nextSeq)
    {
        using var tx = c.BeginTransaction();
        Exec(c, Schema, tx);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO store VALUES(1, $id, $f, $t, $n, 0, 0, 0, 0, vm_store_check($id, $f, $t, $n, 0, 0, 0, 0))";
            cmd.Parameters.AddWithValue("$id", storeId); cmd.Parameters.AddWithValue("$f", (int)PackFormat.Version);
            cmd.Parameters.AddWithValue("$t", createdMs); cmd.Parameters.AddWithValue("$n", nextSeq);
            cmd.ExecuteNonQuery();
        }
        Exec(c, $"PRAGMA user_version={SchemaVersion}", tx);
        tx.Commit();
    }

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    public static IndexRow? Lookup(SqliteCommand prepared, byte[] key)
    {
        prepared.Parameters[0].Value = key;
        using var r = prepared.ExecuteReader();
        if (!r.Read()) return null;
        // Values of the wrong type or range mean a damaged row: report it as one that names no entry, never throw.
        var pack = r.GetValue(0) is long p && p is > 0 and <= int.MaxValue ? (int)p : 0;
        return new IndexRow(pack, r.GetValue(1) as long? ?? -1, r.GetValue(2) as long? ?? -1, r.GetValue(3) as byte[] ?? []);
    }

    public static long PackCheck(long seq, long state, long committed)
    {
        Span<byte> b = stackalloc byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b, seq);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b[8..], state);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b[16..], committed);
        return PackFormat.Crc32C(b);
    }

    public static long StoreCheck(byte[] id, long format, long created, long next, StoreTotals totals)
    {
        var b = new byte[(id?.Length ?? 0) + 56];
        id?.CopyTo(b, 0);
        var at = id?.Length ?? 0;
        foreach (var v in new[] { format, created, next, totals.Objects, totals.ObjectDigest, totals.History, totals.HistoryDigest })
        { System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), v); at += 8; }
        return PackFormat.Crc32C(b);
    }

    /// <summary>A 64-bit value per row, mixed (splitmix64) so XOR over rows doesn't cancel the way plain CRCs, being linear, would.</summary>
    private static long Mix(ReadOnlySpan<byte> b)
    {
        // A reseeded CRC of the same bytes only differs by a constant (CRC is affine), so the second half reads them reversed.
        Span<byte> reversed = b.Length <= 512 ? stackalloc byte[b.Length] : new byte[b.Length];
        b.CopyTo(reversed); reversed.Reverse();
        var x = ((ulong)PackFormat.Crc32C(b) << 32) | PackFormat.Crc32C(reversed);
        x += 0x9E3779B97F4A7C15; x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9; x = (x ^ (x >> 27)) * 0x94D049BB133111EB;
        return (long)(x ^ (x >> 31));
    }

    public static long ObjectDigest(byte[] k, long pack, long offset, long length, byte[] sha)
    {
        Span<byte> b = stackalloc byte[(k?.Length ?? 0) + 24 + (sha?.Length ?? 0)];
        k.AsSpan().CopyTo(b); var at = k?.Length ?? 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b[at..], pack);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b[(at + 8)..], offset);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b[(at + 16)..], length);
        sha.AsSpan().CopyTo(b[(at + 24)..]);
        return Mix(b);
    }

    public static long HistoryDigest(byte[] record, long observed, string product, string region, long kind, byte[] document, string url)
    {
        var text = System.Text.Encoding.UTF8.GetBytes($"{product}\n{region}\n{url}");
        var b = new byte[(record?.Length ?? 0) + (document?.Length ?? 0) + 16 + text.Length];
        record.AsSpan().CopyTo(b); var at = record?.Length ?? 0;
        document.AsSpan().CopyTo(b.AsSpan(at)); at += document?.Length ?? 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), observed);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at + 8), kind);
        text.CopyTo(b, at + 16);
        return Mix(b);
    }

    /// <summary>Totals over objects and history (count and digest), computed from the rows themselves.</summary>
    public static StoreTotals Measure(SqliteConnection c, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT (SELECT count(*) FROM objects), (SELECT vm_object_digest(k, pack, offset, length, sha256) FROM objects), " +
            "(SELECT count(*) FROM history), (SELECT vm_history_digest(record, observed_at, product, region, kind, document, url) FROM history)";
        using var r = cmd.ExecuteReader(); r.Read();
        return new(r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3));
    }

    /// <summary>
    /// As <see cref="Measure"/> for the objects table, split by key range over parallel read-only connections: at 30M rows one
    /// connection takes about 8.6 s here, so open-time checking spreads it over the CPUs.
    /// </summary>
    private static (long count, long digest) MeasureObjectsParallel(string path)
    {
        byte[]? lo, hi;
        using (var c = Open(path, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT min(k), max(k) FROM objects";
            using var r = cmd.ExecuteReader(); r.Read();
            lo = r.GetValue(0) as byte[]; hi = r.GetValue(1) as byte[];
        }
        if (lo is null || hi is null) return (0, 0);
        static UInt128 Prefix(byte[] k) { Span<byte> b = stackalloc byte[8]; k.AsSpan(0, Math.Min(8, k.Length)).CopyTo(b); return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(b); }
        var (a, z) = (Prefix(lo), Prefix(hi));
        var slices = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        var bounds = Enumerable.Range(0, slices + 1).Select(i => i == slices ? (byte[]?)null : BoundOf(a + (z - a) * (UInt128)i / (UInt128)slices)).ToArray();
        static byte[] BoundOf(UInt128 v) { var b = new byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); return b; }
        var parts = new (long count, long digest)[slices];
        Parallel.For(0, slices, new ParallelOptions { MaxDegreeOfParallelism = slices }, i =>
        {
            using var c = Open(path, readOnly: true);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*), vm_object_digest(k, pack, offset, length, sha256) FROM objects WHERE k >= $a" + (bounds[i + 1] is null ? "" : " AND k < $b");
            cmd.Parameters.AddWithValue("$a", i == 0 ? Array.Empty<byte>() : bounds[i]!);
            if (bounds[i + 1] is { } b) cmd.Parameters.AddWithValue("$b", b);
            using var r = cmd.ExecuteReader(); r.Read();
            parts[i] = (r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1));
        });
        return (parts.Sum(p => p.count), parts.Aggregate(0L, (x, p) => x ^ p.digest));
    }

    private static readonly (string table, string[] columns)[] Expected =
    [
        ("store", ["id", "store_id", "format", "created_at", "next_seq", "object_count", "object_digest", "history_count", "history_digest", "row_check"]),
        ("packs", ["seq", "state", "committed_length", "live_bytes", "row_check"]),
        ("objects", ["k", "pack", "offset", "length", "sha256", "last_read"]),
        ("history", ["record", "observed_at", "product", "region", "kind", "document", "url"]),
    ];

    /// <summary>
    /// Opens an existing index read-only and checks it before anything trusts it: SQLite's quick_check, the exact tables and columns,
    /// one store row and every pack row with sane values and matching row checks, at most one active pack. Returns the store id and
    /// null, or a problem; it never throws for a damaged file.
    /// </summary>
    public static (byte[]? storeId, string? problem) Validate(string path)
    {
        try
        {
            using var c = Open(path, readOnly: true);
            using (var cmd = c.CreateCommand())
            {
                // integrity_check, not quick_check: it also finds rows out of key order, which hide objects from lookups (QA on #153).
                // At 30M rows it takes about 3.6 s here, quick_check 3.0 s: the objects table has no secondary index to cross-check.
                cmd.CommandText = "PRAGMA integrity_check";
                var first = cmd.ExecuteScalar() as string;
                if (first != "ok") return (null, $"the index fails SQLite's check ({first})");
            }
            foreach (var (table, columns) in Expected)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info({table})";
                var names = new List<string>();
                using (var r = cmd.ExecuteReader()) while (r.Read()) names.Add(r.GetValue(1) as string ?? "");
                if (!names.SequenceEqual(columns)) return (null, $"the index's {table} table doesn't have the expected columns");
            }
            byte[]? id = null;
            StoreTotals recorded = default;
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT id, store_id, format, created_at, next_seq, object_count, object_digest, history_count, history_digest, row_check FROM store";
                using var r = cmd.ExecuteReader();
                var rows = 0;
                while (r.Read())
                {
                    rows++;
                    if (r.GetValue(0) is not long one || one != 1 || r.GetValue(1) is not byte[] { Length: 16 } sid || r.GetValue(2) is not long format ||
                        format != PackFormat.Version || r.GetValue(3) is not long created || r.GetValue(4) is not long next || next < 1 ||
                        r.GetValue(5) is not long oc || r.GetValue(6) is not long od || r.GetValue(7) is not long hc || r.GetValue(8) is not long hd ||
                        r.GetValue(9) is not long check || check != StoreCheck(sid, format, created, next, new(oc, od, hc, hd)))
                        return (null, "the index's store row is damaged");
                    id = sid; recorded = new(oc, od, hc, hd);
                }
                if (rows != 1) return (null, "the index doesn't have exactly one store row");
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT seq, state, committed_length, live_bytes, row_check FROM packs";
                using var r = cmd.ExecuteReader();
                var active = 0;
                while (r.Read())
                {
                    if (r.GetValue(0) is not long seq || seq < 1 || r.GetValue(1) is not long state || state is < 0 or > 3 ||
                        r.GetValue(2) is not long committed || committed < PackFormat.PackHeaderBytes || r.GetValue(3) is not long ||
                        r.GetValue(4) is not long check || check != PackCheck(seq, state, committed))
                        return (null, "a pack row in the index is damaged");
                    if (state == (long)PackState.Active) active++;
                }
                if (active > 1) return (null, "the index lists more than one active pack");
            }
            // Rows lost, added or altered in a structurally sound file: only the totals over the rows themselves can tell.
            var (count, digest) = MeasureObjectsParallel(path);
            if (count != recorded.Objects || digest != recorded.ObjectDigest) return (null, $"the index's object rows don't match its totals ({count} rows, {recorded.Objects} recorded)");
            var measured = Measure(c);
            if (measured.History != recorded.History || measured.HistoryDigest != recorded.HistoryDigest) return (null, "the index's history rows don't match its totals");
            return (id, null);
        }
        catch (Exception e) when (e is SqliteException or InvalidCastException or InvalidOperationException or FormatException or OverflowException or ArgumentException or AggregateException)
        {
            return (null, $"the index can't be read ({e.Message})");
        }
    }

    public static SqliteCommand LookupCommand(SqliteConnection c, SqliteTransaction? tx = null)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT pack, offset, length, sha256 FROM objects WHERE k = $k";
        cmd.Parameters.Add("$k", SqliteType.Blob);
        cmd.Prepare();
        return cmd;
    }
}

/// <summary>A bounded pool of read connections. Point lookups run inline on the caller: microseconds, no queue.</summary>
internal sealed class IndexReaders : IDisposable
{
    private sealed class Reader(SqliteConnection connection)
    {
        public readonly SqliteConnection Connection = connection;
        public readonly SqliteCommand Lookup = ObjectIndex.LookupCommand(connection);
    }

    private readonly string path;
    private readonly int size;
    private readonly SemaphoreSlim slots;
    private readonly System.Collections.Concurrent.ConcurrentBag<Reader> idle = [];
    private int retired;

    public IndexReaders(string path, int size)
    {
        this.path = path; this.size = size; slots = new SemaphoreSlim(size, size);
    }

    /// <summary>Runs one bounded read on a pooled connection.</summary>
    public async ValueTask<T> Use<T, TState>(TState state, Func<SqliteConnection, SqliteCommand, TState, T> read, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref retired) != 0) throw new ObjectStoreException(ObjectStoreCodes.Closed, "The object store is closed.");
            if (!idle.TryTake(out var r)) r = new Reader(ObjectIndex.Open(path, readOnly: true));
            try
            {
                var value = read(r.Connection, r.Lookup, state);
                idle.Add(r);
                return value;
            }
            // InvalidOperationException: a NULL where the schema promises a value ("The data is NULL at ordinal N").
            catch (Exception e) when (e is SqliteException or InvalidCastException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
            {
                r.Lookup.Dispose(); r.Connection.Dispose();
                throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index can't be read; a Scan rebuilds it from the packs.", e);
            }
        }
        finally { slots.Release(); }
    }

    public void Dispose()
    {
        Volatile.Write(ref retired, 1);
        while (idle.TryTake(out var r)) { r.Lookup.Dispose(); r.Connection.Dispose(); }
    }
}
