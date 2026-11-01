using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

/// <summary>
/// Layer 1: a permanent local copy of what the CDN served, in append-only packs with a rebuildable SQLite index (D121-D127).
/// It knows nothing about builds or features. One writer per store (a lock file); any number of readers, in this process or others.
/// The whole surface is Has, Read (whole or a range), Put (a verified batch, or one staged object), Scan, Verify, Compact and Delete,
/// plus the history log, which is Put and a query.
/// </summary>
public sealed partial class ObjectStore : IObjectReader, IObjectWriter, IHistoryLog, IObjectStoreMaintenance, IAsyncDisposable
{
    /// <summary>The store's folder name inside the data folder. A newer incompatible format gets a new name.</summary>
    public const string DefaultFolderName = "cdn-store-v1";
    internal const int MaxBatchObjects = 4096;
    internal const long MaxBatchBytes = 256L << 20;
    internal const long SessionHashLimit = 1L << 20;
    private const int VerifiedCapacity = 1 << 20, TouchCapacity = 1 << 16;
    private const string LockFile = "writer.lock";

    private readonly string directory, packsDirectory, stagingDirectory, indexPath;
    private readonly ObjectStoreOptions options;
    private readonly bool readOnly;
    private readonly PackReaders packReaders;
    private readonly ConcurrentDictionary<(int, long), byte> verified = new();
    private readonly ConcurrentDictionary<ObjectKey, byte> touches = new();
    private IndexReaders readers = null!;
    private byte[] storeId = [];
    private int closed;

    private ObjectStore(string directory, ObjectStoreOptions options, bool readOnly)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("The store folder must be an absolute path.", nameof(directory));
        this.directory = Path.GetFullPath(directory);
        if (!Enum.IsDefined(options.Kind)) throw new ArgumentOutOfRangeException(nameof(options), "Unknown object store kind.");
        this.options = options; this.readOnly = readOnly;
        packsDirectory = Path.Combine(this.directory, "packs");
        stagingDirectory = Path.Combine(this.directory, "staging");
        indexPath = Path.Combine(this.directory, ObjectIndex.FileName);
        packReaders = new PackReaders(packsDirectory, options.PackHandles);
    }

    public string Folder => directory;
    public bool IsReadOnly => readOnly;
    public ObjectStoreKind Kind => options.Kind;
    /// <summary>What opening found and did: entries recovered past the last commit, bytes truncated, problems left alone.</summary>
    public OpenReport OpenReport { get; private set; } = new(0, 0, false, []);

    /// <summary>Opens (or creates) the store as its one writer, recovering anything past the last commit.</summary>
    public static async Task<ObjectStore> OpenAsync(string directory, ObjectStoreOptions? options = null, CancellationToken ct = default)
    {
        var store = new ObjectStore(directory, options ?? new(), readOnly: false);
        try { await store.OpenWriterAsync(ct); return store; }
        catch { store.Abandon(); throw; }
    }

    /// <summary>
    /// Opens an existing store for reading only, such as the shared dev store. It never writes, recovers or records reads,
    /// sees exactly what the writer has committed, and offers no write at all (<see cref="ReadOnlyObjectStore"/>).
    /// </summary>
    public static async Task<ReadOnlyObjectStore> OpenReadOnlyAsync(string directory, ObjectStoreOptions? options = null, CancellationToken ct = default) =>
        new(await OpenReadOnlyCoreAsync(directory, options, ct));

    private static Task<ObjectStore> OpenReadOnlyCoreAsync(string directory, ObjectStoreOptions? options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var store = new ObjectStore(directory, options ?? new(), readOnly: true);
        try
        {
            if (!File.Exists(store.indexPath)) throw new ObjectStoreException(ObjectStoreCodes.Missing, $"No object store at {store.directory}.");
            int version;
            try { version = ObjectIndex.ReadVersion(store.indexPath) ?? 0; }
            catch (Exception e) when (e is SqliteException or OverflowException or InvalidCastException or FormatException) { throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index can't be read.", e); }
            // A flipped user_version on a format 1 index is damage, named like any other; anything else above 1 is a newer index.
            var damagedVersion = ObjectIndex.IsDamagedVersion(store.indexPath, version);
            if (!damagedVersion && version > ObjectIndex.SchemaVersion) throw NewerFormat($"index schema {version}");
            if (!damagedVersion && version < ObjectIndex.SchemaVersion) throw new ObjectStoreException(ObjectStoreCodes.Missing, "The object store was never completely created.");
            store.CheckIndexKind();
            var valid = new List<(int seq, string path)>();
            foreach (var (seq, path) in store.DiskPacks())
            {
                var check = CheckPackHeader(path, seq, null, store.Kind);
                if (check == PackFormat.HeaderCheck.Newer) throw NewerFormat($"pack {seq}");
                if (check == PackFormat.HeaderCheck.Valid) valid.Add((seq, path));
            }
            var packStore = OnePackStore(valid);
            // A read-only store can't rebuild: a damaged index is a named state, for its writer to repair.
            if (damagedVersion) throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, $"The object index can't be used: its schema version reads {version}. Its writer rebuilds it.");
            var (indexStore, problem, clean, _) = ObjectIndex.Validate(store.indexPath, full: false, ct);
            // Unset also while its writer runs (it clears the marker at open): a reader can't tell that from a crash, so it checks fully.
            // A writer that kept committing leaves it unsettled, which is no evidence of damage: the cheap check stands.
            if (problem is null && !clean) (indexStore, problem, _, _) = ObjectIndex.Validate(store.indexPath, full: true, ct, () => store.At(StorePoint.IndexMeasuring));
            if (problem is not null) throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, $"The object index can't be used: {problem}. Its writer rebuilds it.");
            if (packStore is not null && !packStore.AsSpan().SequenceEqual(indexStore))
                throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index names another store than its packs. Its writer rebuilds it.");
            store.storeId = indexStore!;
            store.readers = new IndexReaders(store.indexPath, store.options.ReadConnections);
            return Task.FromResult(store);
        }
        catch { store.Abandon(); throw; }
    }

    // ---------------------------------------------------------------- reads

    /// <summary>Whether a committed entry exists for the key. Index only; no bytes are read.</summary>
    public async ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default) => await LookupAsync(key, ct) is not null;

    /// <summary>
    /// Reads a whole object (at most 64 MiB; use ranges beyond that). Header, length and every block checksum are checked on every read,
    /// and the SHA-256 on the first read in this session of an object up to 1 MiB. A failed check returns Corrupt and no bytes.
    /// </summary>
    public async ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        ThrowIfClosed();
        for (var attempt = 0; ; attempt++)
        {
            var row = await LookupAsync(key, ct);
            if (row is null) return new(ReadState.Absent);
            if (!row.Sane) return new(ReadState.Corrupt, Problem: "the index row is damaged");
            if (row.Length > ObjectFormats.MaxInMemoryBytes)
                throw new ObjectStoreException(ObjectStoreCodes.Limit, "Whole reads are at most 64 MiB; read larger objects by range.");
            var lease = packReaders.Acquire(row.Pack);
            if (lease is null) { if (attempt == 0) continue; return new(ReadState.Lost, Problem: $"pack {row.Pack} is missing"); }
            using (lease.Value)
            {
                // One read of the whole entry: header, payload and block table are contiguous.
                var entry = new byte[PackFormat.EntryBytes(row.Length)];
                var got = await PackFiles.ReadFullyAsync(lease.Value.Handle, entry, row.Offset, ct);
                if (got < PackFormat.EntryHeaderBytes) return new(ReadState.Corrupt, Problem: "entry truncated");
                if (CheckHeader(entry, key, row, Kind, out var e) is { } bad) return new(ReadState.Corrupt, Problem: bad);
                if (got != entry.Length) return new(ReadState.Corrupt, Info(e), Problem: "entry truncated");
                var payload = entry.AsSpan(PackFormat.EntryHeaderBytes, (int)row.Length).ToArray();
                if (CheckBlocks(payload, 0, entry.AsSpan(PackFormat.EntryHeaderBytes + (int)row.Length)) is { } blockProblem) return new(ReadState.Corrupt, Info(e), Problem: blockProblem);
                if (row.Length <= SessionHashLimit && !verified.ContainsKey((row.Pack, row.Offset)))
                {
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), row.Sha256)) return new(ReadState.Corrupt, Info(e), Problem: "SHA-256 mismatch");
                    MarkVerified(row);
                }
                Touch(key);
                return new(ReadState.Present, Info(e), payload);
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="destination"/>.Length bytes from <paramref name="offset"/>. Only the 64 KiB blocks under the range are
    /// read and each is checked against its CRC-32C before any byte is copied; the rest of the object is never read.
    /// </summary>
    public async ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length > ObjectFormats.MaxInMemoryBytes) throw new ObjectStoreException(ObjectStoreCodes.Limit, "A range read is at most 64 MiB.");
        for (var attempt = 0; ; attempt++)
        {
            var row = await LookupAsync(key, ct);
            if (row is null) return new(ReadState.Absent);
            if (!row.Sane) return new(ReadState.Corrupt, Problem: "the index row is damaged");
            var lease = packReaders.Acquire(row.Pack);
            if (lease is null) { if (attempt == 0) continue; return new(ReadState.Lost, Problem: $"pack {row.Pack} is missing"); }
            using (lease.Value)
            {
                var h = lease.Value.Handle;
                var header = new byte[PackFormat.EntryHeaderBytes];
                if (await PackFiles.ReadFullyAsync(h, header, row.Offset, ct) != header.Length) return new(ReadState.Corrupt, Problem: "entry truncated");
                if (CheckHeader(header, key, row, Kind, out var e) is { } bad) return new(ReadState.Corrupt, Problem: bad);
                // Bounds after the header check: the entry agrees with the index row, so this really is the caller's range.
                if (offset + destination.Length > row.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
                if (destination.IsEmpty) return new(ReadState.Present, Info(e));
                var first = (int)(offset >> PackFormat.BlockShift);
                var last = (int)((offset + destination.Length - 1) >> PackFormat.BlockShift);
                var payloadAt = row.Offset + PackFormat.EntryHeaderBytes;
                var table = new byte[(last - first + 1) * 4];
                if (await PackFiles.ReadFullyAsync(h, table, payloadAt + row.Length + first * 4L, ct) != table.Length)
                    return new(ReadState.Corrupt, Info(e), Problem: "entry truncated");
                var start = (long)first << PackFormat.BlockShift;
                var length = (int)(Math.Min(row.Length, (long)(last + 1) << PackFormat.BlockShift) - start);
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    if (await PackFiles.ReadFullyAsync(h, buffer.AsMemory(0, length), payloadAt + start, ct) != length)
                        return new(ReadState.Corrupt, Info(e), Problem: "entry truncated");
                    if (CheckBlocks(buffer.AsSpan(0, length), first, table) is { } blockProblem) return new(ReadState.Corrupt, Info(e), Problem: blockProblem);
                    buffer.AsSpan((int)(offset - start), destination.Length).CopyTo(destination.Span);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                Touch(key);
                return new(ReadState.Present, Info(e));
            }
        }
    }

    /// <summary>The history log, oldest first, filtered and paged. Raw replies are the entries' documents.</summary>
    public async ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query), "A history page is 1 to 10,000 entries.");
        var page = await readers.Use(query, static (c, _, q) => History(c, q), ct);
        // Each page must start after the cursor and run forward; anything else is a damaged index, never a loop for the caller.
        HistoryEntry? previous = query.After;
        foreach (var e in page)
        {
            if (previous is not null && (e.ObservedAt < previous.ObservedAt || e.ObservedAt == previous.ObservedAt &&
                    e.Record.KeyBytes().AsSpan().SequenceCompareTo(previous.Record.KeyBytes()) <= 0))
                throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The history index has rows out of order; a Scan rebuilds it from the packs.");
            previous = e;
        }
        return page;
    }

    /// <summary>Counts and sizes from the index (<see cref="StoreStats"/>): one statement, so one committed state. No pack is read.</summary>
    public async ValueTask<StoreStats> StatsAsync(CancellationToken ct = default)
    {
        ThrowIfClosed();
        return await readers.Use(0, static (c, _, _) =>
        {
            using var cmd = c.CreateCommand();
            var live = $"state IN ({(int)PackState.Active}, {(int)PackState.Sealed})";
            cmd.CommandText = "SELECT s.object_count, s.history_count, s.created_at, " +
                $"(SELECT COUNT(*) FROM packs WHERE {live}), " +
                $"(SELECT COALESCE(SUM(committed_length), 0) FROM packs WHERE {live}), " +
                $"(SELECT COALESCE(SUM(live_bytes), 0) FROM packs WHERE {live}) FROM store s WHERE s.id = 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index has no store row; a Scan rebuilds it from the packs.");
            return new StoreStats(r.GetInt32(3), r.GetInt64(0), r.GetInt64(1), r.GetInt64(4), r.GetInt64(5), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
        }, ct);
    }

    private static List<HistoryEntry> History(SqliteConnection c, HistoryQuery q)
    {
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (q.Product is not null) { where.Add("product = $p"); cmd.Parameters.AddWithValue("$p", q.Product); }
        if (q.Region is not null) { where.Add("region = $r"); cmd.Parameters.AddWithValue("$r", q.Region); }
        if (q.Kind is { } k) { where.Add("kind = $k"); cmd.Parameters.AddWithValue("$k", (int)k); }
        if (q.After is { } a)
        {
            where.Add("(observed_at > $t OR observed_at = $t AND record > $rec)");
            cmd.Parameters.AddWithValue("$t", a.ObservedAt.ToUnixTimeMilliseconds()); cmd.Parameters.AddWithValue("$rec", a.Record.KeyBytes());
        }
        cmd.CommandText = "SELECT kind, product, region, observed_at, url, document, record FROM history" +
            (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY observed_at, record LIMIT $n";
        cmd.Parameters.AddWithValue("$n", q.Limit);
        var list = new List<HistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new((HistoryKind)r.GetInt32(0), r.GetString(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), r.GetString(4),
                new ObjectKey(ObjectKeyType.HistoryDocument, r.GetFieldValue<byte[]>(5)), new ObjectKey(ObjectKeyType.HistoryRecord, r.GetFieldValue<byte[]>(6))));
        return list;
    }

    internal async ValueTask<IndexRow?> LookupAsync(ObjectKey key, CancellationToken ct)
    {
        ThrowIfClosed();
        return await readers.Use(key.ToIndexKey(), static (_, cmd, k) => ObjectIndex.Lookup(cmd, k), ct);
    }

    private static string? CheckHeader(ReadOnlySpan<byte> raw, ObjectKey key, IndexRow row, ObjectStoreKind kind, out EntryHeader e)
    {
        if (PackFormat.ReadEntryHeader(raw, kind, out e) != PackFormat.HeaderCheck.Valid) return "entry header unreadable";
        if (e.Tombstone || e.Key != key || e.Length != row.Length || !e.Sha256.AsSpan().SequenceEqual(row.Sha256)) return "entry header doesn't match the index";
        return null;
    }

    private static string? CheckBlocks(ReadOnlySpan<byte> bytes, int firstBlock, ReadOnlySpan<byte> table)
    {
        for (var b = 0; b * PackFormat.BlockBytes < bytes.Length; b++)
        {
            var start = b * PackFormat.BlockBytes;
            var block = bytes.Slice(start, Math.Min(PackFormat.BlockBytes, bytes.Length - start));
            if (PackFormat.Crc32C(block) != BinaryPrimitives.ReadUInt32LittleEndian(table[(b * 4)..])) return $"block {firstBlock + b} checksum mismatch";
        }
        return null;
    }

    private static ObjectInfo Info(EntryHeader e) => new(e.Key, e.Length, e.Origin, DateTimeOffset.FromUnixTimeMilliseconds(e.StoredAtMs));

    private void MarkVerified(IndexRow row)
    {
        if (verified.Count >= VerifiedCapacity) verified.Clear();
        verified.TryAdd((row.Pack, row.Offset), 0);
    }

    /// <summary>Last-read time is kept per day in the index, best effort: a full touch queue drops new touches until the next flush.</summary>
    private void Touch(ObjectKey key)
    {
        if (!readOnly && touches.Count < TouchCapacity) touches.TryAdd(key, 0);
    }

    // ---------------------------------------------------------------- shared helpers

    private IEnumerable<(int seq, string path)> DiskPacks()
    {
        if (!System.IO.Directory.Exists(packsDirectory)) yield break;
        foreach (var f in System.IO.Directory.EnumerateFiles(packsDirectory))
            if (PackFiles.TryParse(Path.GetFileName(f), out var seq)) yield return (seq, f);
    }

    /// <summary>Reads a pack header without writing anything. A store id or store kind mismatch is refused outright.</summary>
    private static PackFormat.HeaderCheck CheckPackHeader(string path, int seq, byte[]? expectedStore, ObjectStoreKind kind)
    {
        var h = new byte[PackFormat.PackHeaderBytes];
        int n;
        using (var f = PackFiles.OpenRead(path)) n = RandomAccess.Read(f, h, 0);
        if (n < h.Length) return PackFormat.HeaderCheck.Torn;
        var check = PackFormat.ReadPackHeader(h, out var headerSeq, out var id, out var packKind);
        if (check != PackFormat.HeaderCheck.Valid) return check;
        if (headerSeq != seq) return PackFormat.HeaderCheck.Torn;
        if (packKind != kind) throw KindMismatch($"pack {seq}", packKind, kind);
        if (expectedStore is not null && !id.AsSpan().SequenceEqual(expectedStore))
            throw new ObjectStoreException(ObjectStoreCodes.ForeignPack, $"Pack {seq} belongs to another object store.");
        return check;
    }

    private static byte[] ReadPackStoreId(string path)
    {
        var h = new byte[PackFormat.PackHeaderBytes];
        using (var f = PackFiles.OpenRead(path)) RandomAccess.Read(f, h, 0);
        PackFormat.ReadPackHeader(h, out _, out var id, out _);
        return id;
    }

    /// <summary>Refuses an index that records the other store kind, or one this engine doesn't know, before anything is written.</summary>
    private void CheckIndexKind()
    {
        long? recorded;
        try { recorded = ObjectIndex.ReadKind(indexPath); }
        catch (Exception e) when (e is SqliteException or OverflowException or InvalidCastException or FormatException) { return; }  // unreadable: the packs decide
        if (recorded is not { } k) return;
        if (k is < 0 or > (long)ObjectStoreKind.Generated) throw NewerFormat($"index kind {k}");
        if ((ObjectStoreKind)k != Kind) throw KindMismatch("index", (ObjectStoreKind)k, Kind);
    }

    private static ObjectStoreException KindMismatch(string what, ObjectStoreKind found, ObjectStoreKind asked) =>
        new(ObjectStoreCodes.KindMismatch, $"The object store's {what} belongs to a {found} store, not a {asked} store. Nothing was changed.");

    private static byte[] ReadStoreId(SqliteConnection c)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT store_id FROM store WHERE id = 1";
        return cmd.ExecuteScalar() as byte[] ?? throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The object index has no store row.");
    }

    internal static ObjectStoreException NewerFormat(string what) =>
        new(ObjectStoreCodes.NewerFormat, $"The object store's {what} was written by a newer VibeMiner. Nothing was changed; update VibeMiner to use it.");

    internal void At(StorePoint point) => options.Fault?.Invoke(point);

    private void ThrowIfClosed()
    {
        if (Volatile.Read(ref closed) != 0) throw new ObjectStoreException(ObjectStoreCodes.Closed, "The object store is closed.");
    }

    private long NowMs => options.Time.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        if (!readOnly) await CloseWriterAsync();
        readers?.Dispose();
        packReaders.Dispose();
    }

    /// <summary>Releases everything without committing, cleaning or checkpointing: what a killed process leaves behind.</summary>
    private void Abandon()
    {
        Volatile.Write(ref closed, 1);
        AbandonWriter();
        readers?.Dispose();
        packReaders.Dispose();
    }
}

/// <summary>Reads from a profile's own store, then from a read-only second store (the shared dev store) when the first doesn't have the key.</summary>
public sealed class LayeredObjectReader(IObjectReader primary, IObjectReader? secondary) : IObjectReader
{
    public async ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default) =>
        await primary.HasAsync(key, ct) || secondary is not null && await secondary.HasAsync(key, ct);

    public async ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        var r = await primary.ReadAsync(key, ct);
        if (r.State != ReadState.Absent || secondary is null) return r;
        var s = await secondary.ReadAsync(key, ct);
        return s.State == ReadState.Absent ? r : s with { FromSecondary = true };
    }

    public async ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        var r = await primary.ReadRangeAsync(key, offset, destination, ct);
        if (r.State != ReadState.Absent || secondary is null) return r;
        var s = await secondary.ReadRangeAsync(key, offset, destination, ct);
        return s.State == ReadState.Absent ? r : s with { FromSecondary = true };
    }
}
