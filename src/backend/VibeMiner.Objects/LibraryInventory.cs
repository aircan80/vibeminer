using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

internal sealed class LibraryInventory(ObjectStore store, LibraryClaim claim) : ILibraryInventory
{
    public Task<LibraryInventoryGeneration> BeginAsync(CancellationToken ct = default) => store.BeginInventoryAsync(claim, ct);

    public async Task AppendAsync(LibraryInventoryGeneration generation, IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        try
        {
            ArgumentNullException.ThrowIfNull(keys);
            ct.ThrowIfCancellationRequested();
            if (keys.Count > 1024) throw new ObjectStoreException(ObjectStoreCodes.Limit, "An inventory append is at most 1024 keys.");
            // Copy the bounded batch before admission; subsequent caller changes cannot bypass validation.
            var copy = keys.ToArray();
            foreach (var key in copy) ValidateKey(key);
            await store.AppendInventoryAsync(claim, generation, copy, ct);
        }
        catch (Exception e) when (e is OperationCanceledException or ArgumentException || e is ObjectStoreException { Code: ObjectStoreCodes.Limit })
        {
            await store.PoisonInventoryAsync(claim, generation);
            throw;
        }
    }

    public async Task PublishAsync(LibraryInventoryGeneration generation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        try { await store.PublishInventoryAsync(claim, generation, ct); }
        catch (OperationCanceledException)
        {
            await store.PoisonInventoryAsync(claim, generation);
            throw;
        }
    }

    public async Task PublishSourceAsync(LibraryInventoryGeneration generation, LibraryInventorySourceDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation); ArgumentNullException.ThrowIfNull(descriptor);
        try { await store.PublishInventoryAsync(claim, generation, ct, descriptor); }
        catch (OperationCanceledException) { await store.PoisonInventoryAsync(claim, generation); throw; }
    }
    public ValueTask<long?> MatchSourceAsync(LibraryInventorySourceDescriptor descriptor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return store.MatchInventorySourceAsync(claim, descriptor, ct);
    }

    public Task AbandonAsync(LibraryInventoryGeneration generation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return store.AbandonInventoryAsync(claim, generation, ct);
    }

    public ValueTask<LibraryInventoryLookup> LookupAsync(ObjectKey key, CancellationToken ct = default)
    {
        ValidateKey(key);
        return store.LookupInventoryAsync(claim, key, ct);
    }

    public ValueTask<LibraryInventoryMeasurement> MeasureAsync(CancellationToken ct = default) => store.MeasureInventoryAsync(claim, ct);

    public ValueTask<LibraryInventoryPage> ReadPageAsync(int limit = 1024, LibraryInventoryCursor? after = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        return store.ReadInventoryPageAsync(claim, limit, after, ct);
    }

    internal static void ValidateKey(ObjectKey key)
    {
        if (key.Namespace != 0 || !ObjectFormats.Known(ObjectStoreKind.Library, key.Type))
            throw new ArgumentException("An expected key must have a known library key type and namespace zero.", nameof(key));
    }
}

public sealed partial class ObjectStore
{
    private string InventoryBinding(LibraryClaim claim) => string.Join('\0', Convert.ToHexString(storeId), claim.Owner,
        ((int)claim.Scope).ToString(System.Globalization.CultureInfo.InvariantCulture), claim.Producer,
        ((int)claim.Role).ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static ObjectStoreException InventoryStale() => new(ObjectStoreCodes.InventoryStale, "The inventory generation is stale, abandoned, published or belongs to another identity.");
    private static ObjectStoreException InventoryCursorStale() => new(ObjectStoreCodes.InventoryCursorStale, "The inventory cursor no longer names this identity's active generation.");

    internal Task<LibraryInventoryGeneration> BeginInventoryAsync(LibraryClaim claim, CancellationToken ct) => MaintainAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        LibraryInventoryMetadata.Require(wconn!);
        long id = 0;
        Durably(tx =>
        {
            using var cmd = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
                INSERT INTO library_inventory_generation(owner,scope,producer,role,state) VALUES($o,$s,$p,$r,0);
                SELECT last_insert_rowid();
                """);
            id = (long)cmd.ExecuteScalar()!;
            using var identity = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
                INSERT INTO library_inventory_identity(owner,scope,producer,role,latest,active) VALUES($o,$s,$p,$r,$g,NULL)
                ON CONFLICT(owner,scope,producer,role) DO UPDATE SET latest=excluded.latest;
                """);
            identity.Parameters.AddWithValue("$g", id); identity.ExecuteNonQuery();
        });
        return Task.FromResult(new LibraryInventoryGeneration(id) { Binding = InventoryBinding(claim) });
    }, ct);

    private void CheckInventoryToken(LibraryClaim claim, LibraryInventoryGeneration generation)
    {
        if (generation.Binding != InventoryBinding(claim) || generation.Value <= 0) throw InventoryStale();
    }

    private void RequireInventoryStage(LibraryClaim claim, LibraryInventoryGeneration generation, SqliteTransaction tx)
    {
        CheckInventoryToken(claim, generation);
        using var cmd = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
            SELECT EXISTS(SELECT 1 FROM library_inventory_generation g JOIN library_inventory_identity i
            ON i.owner=g.owner AND i.scope=g.scope AND i.producer=g.producer AND i.role=g.role
            WHERE g.owner=$o AND g.scope=$s AND g.producer=$p AND g.role=$r AND g.id=$g AND g.state=0 AND i.latest=g.id)
            """);
        cmd.Parameters.AddWithValue("$g", generation.Value);
        if ((long)cmd.ExecuteScalar()! != 1) throw InventoryStale();
    }

    internal Task AppendInventoryAsync(LibraryClaim claim, LibraryInventoryGeneration generation, ObjectKey[] keys, CancellationToken ct) => MaintainAsync(() =>
    {
        LibraryInventoryMetadata.Require(wconn!);
        try
        {
            ct.ThrowIfCancellationRequested();
            Durably(tx =>
            {
                RequireInventoryStage(claim, generation, tx);
                long count, digest;
                using (var totals = wconn!.CreateCommand())
                {
                    totals.Transaction = tx;
                    totals.CommandText = "SELECT key_count,key_digest FROM library_inventory_generation WHERE id=$g";
                    totals.Parameters.AddWithValue("$g", generation.Value);
                    using var row = totals.ExecuteReader();
                    if (!row.Read()) throw InventoryStale();
                    count = row.GetInt64(0); digest = row.GetInt64(1);
                }
                using var cmd = wconn!.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO library_inventory_key(generation,k) VALUES($g,$k)";
                cmd.Parameters.AddWithValue("$g", generation.Value);
                var parameter = cmd.Parameters.Add("$k", SqliteType.Blob);
                foreach (var key in keys)
                {
                    ct.ThrowIfCancellationRequested();
                    var encoded = key.ToIndexKey(); parameter.Value = encoded;
                    if (cmd.ExecuteNonQuery() == 1)
                    {
                        count = checked(count + 1);
                        digest ^= ObjectIndex.InventoryKeyDigest(encoded);
                    }
                }
                using var update = wconn.CreateCommand(); update.Transaction = tx;
                update.CommandText = "UPDATE library_inventory_generation SET key_count=$n,key_digest=$d WHERE id=$g";
                update.Parameters.AddWithValue("$n", count); update.Parameters.AddWithValue("$d", digest);
                update.Parameters.AddWithValue("$g", generation.Value); update.ExecuteNonQuery();
            });
        }
        catch (OperationCanceledException) { PoisonInventoryStage(claim, generation); throw; }
        At(StorePoint.InventoryStaged);
        return Task.FromResult(true);
    }, ct);

    internal Task PublishInventoryAsync(LibraryClaim claim, LibraryInventoryGeneration generation, CancellationToken ct, LibraryInventorySourceDescriptor? descriptor = null) => MaintainAsync(() =>
    {
        LibraryInventoryMetadata.Require(wconn!);
        try
        {
            At(StorePoint.InventoryBeforePublish);
            ct.ThrowIfCancellationRequested();
            Durably(tx =>
            {
                RequireInventoryStage(claim, generation, tx);
                if (descriptor is not null)
                {
                    LibraryInventorySourceMetadata.Insert(wconn!, tx, claim, generation.Value, descriptor);
                    At(StorePoint.InventorySourceInsidePublish);
                }
                using var cmd = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
                    UPDATE library_inventory_generation SET state=1 WHERE id=$g;
                    UPDATE library_inventory_identity SET active=$g WHERE owner=$o AND scope=$s AND producer=$p AND role=$r;
                    """);
                cmd.Parameters.AddWithValue("$g", generation.Value); cmd.ExecuteNonQuery();
                At(StorePoint.InventoryInsidePublish);
            });
        }
        catch (OperationCanceledException) { PoisonInventoryStage(claim, generation); throw; }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.InventorySourceMismatch) { PoisonInventoryStage(claim, generation); throw; }
        At(StorePoint.InventoryPublished);
        return Task.FromResult(true);
    }, ct);

    private void PoisonInventoryStage(LibraryClaim claim, LibraryInventoryGeneration generation)
    {
        if (generation.Binding != InventoryBinding(claim)) return;
        Durably(tx =>
        {
            using var cmd = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
                UPDATE library_inventory_generation SET state=2 WHERE id=$g AND owner=$o AND scope=$s AND producer=$p AND role=$r AND state=0
                """);
            cmd.Parameters.AddWithValue("$g", generation.Value); cmd.ExecuteNonQuery();
        });
    }

    internal Task AbandonInventoryAsync(LibraryClaim claim, LibraryInventoryGeneration generation, CancellationToken ct) => MaintainAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        LibraryInventoryMetadata.Require(wconn!);
        CheckInventoryToken(claim, generation);
        Durably(tx =>
        {
            using var cmd = LibraryInventoryMetadata.Command(wconn!, tx, claim, """
                UPDATE library_inventory_generation SET state=2 WHERE id=$g AND owner=$o AND scope=$s AND producer=$p AND role=$r AND state IN (0,2)
                """);
            cmd.Parameters.AddWithValue("$g", generation.Value);
            if (cmd.ExecuteNonQuery() != 1) throw InventoryStale();
        });
        return Task.FromResult(true);
    }, ct);

    internal async Task PoisonInventoryAsync(LibraryClaim claim, LibraryInventoryGeneration generation)
    {
        // Cancellation invalidates a token, rather than allowing a caller to publish a prefix in a later retry.
        // A foreign token cannot affect this or another identity. Closure/fault already prevents further publication.
        if (generation.Binding != InventoryBinding(claim)) return;
        try
        {
            await MaintainAsync(() =>
            {
                LibraryInventoryMetadata.Require(wconn!);
                PoisonInventoryStage(claim, generation);
                return Task.FromResult(true);
            }, CancellationToken.None);
        }
        catch (ObjectStoreException e) when (e.Code is ObjectStoreCodes.Closed or ObjectStoreCodes.WriteFailed or ObjectStoreCodes.DiskFull) { }
    }

    internal ValueTask<LibraryInventoryLookup> LookupInventoryAsync(LibraryClaim claim, ObjectKey key, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use((claim, key), (c, _, state) => InventoryRead(c, ct, tx =>
        {
            var generation = LibraryInventoryMetadata.Active(c, tx, state.claim);
            if (generation is null) return new LibraryInventoryLookup(null, false);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM library_inventory_key WHERE generation=$g AND k=$k)";
            cmd.Parameters.AddWithValue("$g", generation); cmd.Parameters.AddWithValue("$k", state.key.ToIndexKey());
            return new LibraryInventoryLookup(generation, (long)cmd.ExecuteScalar()! == 1);
        }), ct);
    }

    internal ValueTask<LibraryInventoryPage> ReadInventoryPageAsync(LibraryClaim claim, int limit, LibraryInventoryCursor? after, CancellationToken ct)
    {
        ThrowIfClosed();
        var binding = InventoryBinding(claim);
        return readers.Use((claim, limit, after, binding), (c, _, state) => InventoryRead(c, ct, tx =>
        {
            var generation = LibraryInventoryMetadata.Active(c, tx, state.claim);
            if (state.after is { } cursor && (cursor.Binding != state.binding || generation != cursor.Generation)) throw InventoryCursorStale();
            if (generation is null) return new LibraryInventoryPage(null, [], null);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT k FROM library_inventory_key WHERE generation=$g" +
                (state.after is null ? "" : " AND k>$k") + " ORDER BY k LIMIT $n";
            cmd.Parameters.AddWithValue("$g", generation); cmd.Parameters.AddWithValue("$n", state.limit + 1);
            if (state.after is not null) cmd.Parameters.AddWithValue("$k", state.after.After.ToIndexKey());
            var keys = new List<ObjectKey>(state.limit);
            using var rows = cmd.ExecuteReader();
            while (keys.Count < state.limit && rows.Read()) { ct.ThrowIfCancellationRequested(); keys.Add(ObjectKey.FromIndexKey(rows.GetFieldValue<byte[]>(0))); }
            var more = keys.Count == state.limit && rows.Read();
            return new LibraryInventoryPage(generation, keys,
                more ? new LibraryInventoryCursor(generation.Value, keys[^1]) { Binding = state.binding } : null);
        }), ct);
    }

    internal ValueTask<LibraryInventoryMeasurement> MeasureInventoryAsync(LibraryClaim claim, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use(claim, (c, _, identity) => InventoryRead(c, ct, tx =>
        {
            var generation = LibraryInventoryMetadata.Active(c, tx, identity);
            // Active's SELECT pins both inventory and object-index reads, before any concurrent publication/acquisition.
            At(StorePoint.InventoryMeasureSnapshot);
            ct.ThrowIfCancellationRequested();
            if (generation is null) return new LibraryInventoryMeasurement(null, null, null, null, null);
            long recordedCount;
            using (var metadata = c.CreateCommand())
            {
                metadata.Transaction = tx;
                metadata.CommandText = "SELECT key_count FROM library_inventory_generation WHERE id=$g AND state=1";
                metadata.Parameters.AddWithValue("$g", generation.Value);
                if (metadata.ExecuteScalar() is not long count || count < 0)
                    throw new InvalidOperationException("The active inventory generation or its committed key count is unreadable.");
                recordedCount = count;
            }
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT o.k IS NOT NULL,o.length FROM library_inventory_key k
                LEFT JOIN objects o ON o.k=k.k WHERE k.generation=$g
                """;
            cmd.Parameters.AddWithValue("$g", generation.Value);
            long expected = 0, present = 0, bytes = 0;
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
            {
                At(StorePoint.InventoryMeasureRow);
                ct.ThrowIfCancellationRequested();
                expected = checked(expected + 1);
                if (rows.GetInt64(0) == 0) continue;
                if (rows.GetValue(1) is not long length || length <= 0)
                    throw new InvalidOperationException("An expected object's indexed payload length is unreadable.");
                present = checked(present + 1);
                bytes = checked(bytes + length);
            }
            ct.ThrowIfCancellationRequested();
            if (expected != recordedCount)
                throw new InvalidOperationException("The active inventory's keys do not match its committed key count.");
            return new LibraryInventoryMeasurement(generation, expected, present, checked(expected - present), bytes);
        }), ct);
    }

    private static T InventoryRead<T>(SqliteConnection c, CancellationToken ct, Func<SqliteTransaction, T> read)
    {
        using var interrupt = ct.Register(() => SQLitePCL.raw.sqlite3_interrupt(c.Handle));
        try
        {
            ct.ThrowIfCancellationRequested();
            using var tx = c.BeginTransaction();
            LibraryInventoryMetadata.Require(c, tx);
            return read(tx);
        }
        catch (SqliteException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (Exception e) when (e is SqliteException or ArgumentException or InvalidCastException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable,
                "The authoritative inventory metadata cannot be read; preserve the library for repair.", e);
        }
    }
}
