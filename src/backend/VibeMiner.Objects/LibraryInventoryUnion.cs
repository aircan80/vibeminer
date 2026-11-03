using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

internal sealed class LibraryInventoryUnion(ObjectStore store, string owner, LibraryOwnerScope scope, LibraryObjectRole role) : ILibraryInventoryUnion
{
    public ValueTask<LibraryInventoryUnionMeasurement> MeasureAsync(CancellationToken ct = default) =>
        store.MeasureInventoryUnionAsync(owner, scope, role, ct);
}

public sealed partial class ObjectStore
{
    /// <summary>
    /// Captures one owner/scope/role for published producer-union measurement. Producer discovery and complete-owner coverage
    /// remain the caller's concerns. This view borrows the library lifetime.
    /// </summary>
    public ILibraryInventoryUnion ForInventoryOwner(string owner, LibraryOwnerScope scope, LibraryObjectRole role)
    {
        ThrowIfClosed();
        if (Kind != ObjectStoreKind.Library) throw new InvalidOperationException("Inventory owner views require a mixed library.");
        // Reuse the exact identity validation without registering an inventory or choosing a real producer.
        _ = new LibraryClaim(owner, scope, "inventory-union", role);
        return new LibraryInventoryUnion(this, owner, scope, role);
    }

    private readonly record struct InventoryUnionGeneration(long Id, long KeyCount);

    internal ValueTask<LibraryInventoryUnionMeasurement> MeasureInventoryUnionAsync(string owner, LibraryOwnerScope scope, LibraryObjectRole role, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use((owner, scope, role), (c, _, identity) => InventoryRead(c, ct, tx =>
        {
            var generations = new List<InventoryUnionGeneration>(LibraryInventoryUnionLimits.MaxPublishedProducers);
            long published = 0, unpublished = 0;
            using (var identities = c.CreateCommand())
            {
                identities.Transaction = tx;
                identities.CommandText = """
                    SELECT i.active,g.id,g.state,g.key_count FROM library_inventory_identity i
                    LEFT JOIN library_inventory_generation g ON g.id=i.active AND g.owner=i.owner
                        AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role
                    WHERE i.owner=$o AND i.scope=$s AND i.role=$r
                    """;
                identities.Parameters.AddWithValue("$o", identity.owner);
                identities.Parameters.AddWithValue("$s", (int)identity.scope);
                identities.Parameters.AddWithValue("$r", (int)identity.role);
                using var rows = identities.ExecuteReader();
                while (rows.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var active = LibraryInventoryMetadata.ParseActive(rows.GetValue(0));
                    if (active is null) { unpublished = checked(unpublished + 1); continue; }
                    if (rows.GetValue(1) is not long id || id != active || rows.GetValue(2) is not long state || state != 1 ||
                        rows.GetValue(3) is not long count || count < 0)
                        throw new InvalidOperationException("An active producer inventory or its committed key count is unreadable.");
                    published = checked(published + 1);
                    if (published > LibraryInventoryUnionLimits.MaxPublishedProducers)
                        throw new ObjectStoreException(ObjectStoreCodes.Limit,
                            $"An inventory union supports at most {LibraryInventoryUnionLimits.MaxPublishedProducers} active published producers.");
                    generations.Add(new(id, count));
                }
            }
            // The identity SELECT pins the inventory generations and committed object index before concurrent writer work.
            At(StorePoint.InventoryUnionSnapshot);
            ct.ThrowIfCancellationRequested();
            if (published == 0) return new LibraryInventoryUnionMeasurement(0, unpublished, null, null, null, null);

            // At most 256 sorted PK streams: bounded SQLite statement state and managed heads, no DISTINCT or temporary sort.
            var streams = new List<InventoryUnionCursor>(generations.Count);
            var heads = new PriorityQueue<InventoryUnionCursor, ObjectKey>(generations.Count);
            try
            {
                foreach (var generation in generations)
                {
                    ct.ThrowIfCancellationRequested();
                    var stream = new InventoryUnionCursor(c, tx, generation);
                    streams.Add(stream);
                    if (stream.MoveNext(ct)) heads.Enqueue(stream, stream.Key);
                }
                long expected = 0, present = 0, bytes = 0;
                ObjectKey previous = default; var havePrevious = false;
                while (heads.TryDequeue(out var stream, out var key))
                {
                    At(StorePoint.InventoryUnionRow);
                    ct.ThrowIfCancellationRequested();
                    if (!havePrevious || key != previous)
                    {
                        expected = checked(expected + 1);
                        if (stream.IndexedLength is { } length)
                        {
                            present = checked(present + 1);
                            bytes = checked(bytes + length);
                        }
                        previous = key; havePrevious = true;
                    }
                    if (stream.MoveNext(ct)) heads.Enqueue(stream, stream.Key);
                }
                ct.ThrowIfCancellationRequested();
                return new LibraryInventoryUnionMeasurement(published, unpublished, expected, present, checked(expected - present), bytes);
            }
            finally { foreach (var stream in streams) stream.Dispose(); }
        }), ct);
    }

    private sealed class InventoryUnionCursor : IDisposable
    {
        private readonly SqliteCommand command;
        private readonly SqliteDataReader rows;
        private readonly long recordedCount;
        private long count;
        public ObjectKey Key { get; private set; }
        public long? IndexedLength { get; private set; }

        public InventoryUnionCursor(SqliteConnection c, SqliteTransaction tx, InventoryUnionGeneration generation)
        {
            recordedCount = generation.KeyCount;
            command = c.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                SELECT k.k,o.k IS NOT NULL,o.length,typeof(k.k)='blob' FROM library_inventory_key k
                LEFT JOIN objects o ON o.k=k.k WHERE k.generation=$g ORDER BY k.k
                """;
            command.Parameters.AddWithValue("$g", generation.Id);
            try { rows = command.ExecuteReader(); }
            catch { command.Dispose(); throw; }
        }

        public bool MoveNext(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!rows.Read())
            {
                if (count != recordedCount) throw new InvalidOperationException("A producer inventory's keys do not match its committed key count.");
                return false;
            }
            // Bound key allocation even if metadata changes after open. Exactly 18 bytes encode every accepted ObjectKey.
            if (rows.GetInt64(3) != 1 || rows.GetBytes(0, 0, null, 0, 0) != ObjectKey.EncodedBytes)
                throw new InvalidOperationException("An expected inventory key is malformed.");
            var key = ObjectKey.FromIndexKey(rows.GetFieldValue<byte[]>(0));
            LibraryInventory.ValidateKey(key);
            if (count > 0 && key.CompareTo(Key) <= 0) throw new InvalidOperationException("A producer inventory has duplicate or unordered expected keys.");
            count = checked(count + 1); Key = key;
            if (rows.GetInt64(1) == 0) IndexedLength = null;
            else
            {
                if (rows.GetValue(2) is not long length || length <= 0)
                    throw new InvalidOperationException("An expected object's indexed payload length is unreadable.");
                IndexedLength = length;
            }
            return true;
        }

        public void Dispose() { rows.Dispose(); command.Dispose(); }
    }
}
