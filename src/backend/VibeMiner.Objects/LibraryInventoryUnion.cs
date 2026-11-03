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

    private readonly record struct InventoryOwnerReference(string Owner, LibraryOwnerScope Scope);
    private readonly record struct InventoryUnionGeneration(long Id, long KeyCount,
        InventoryOwnerReference? Owner = null, LibraryObjectRole Role = default);

    private sealed record InventoryUnionResolution(List<InventoryUnionGeneration> Generations, long Published, long Unpublished)
    {
        public LibraryInventoryUnionMeasurement Measurement(InventoryCoverageCounter totals) => Published == 0
            ? new(0, Unpublished, null, null, null, null)
            : new(Published, Unpublished, totals.Expected, totals.Present, checked(totals.Expected - totals.Present), totals.Bytes);
    }

    private sealed class InventoryCoverageCounter
    {
        public long Expected { get; private set; }
        public long Present { get; private set; }
        public long Bytes { get; private set; }
        public void Add(long? length)
        {
            Expected = checked(Expected + 1);
            if (length is not { } bytes) return;
            Present = checked(Present + 1); Bytes = checked(Bytes + bytes);
        }
        public LibraryInventoryCoverage Coverage() => new(Expected, Present, checked(Expected - Present), Bytes);
    }

    private static InventoryUnionResolution ResolveInventoryUnion(SqliteConnection c, SqliteTransaction tx,
        string owner, LibraryOwnerScope scope, LibraryObjectRole role, CancellationToken ct)
    {
        var generations = new List<InventoryUnionGeneration>(LibraryInventoryUnionLimits.MaxPublishedProducers);
        long published = 0, unpublished = 0;
        using var identities = c.CreateCommand(); identities.Transaction = tx;
        identities.CommandText = """
            SELECT CASE WHEN typeof(i.active)='integer' OR i.active IS NULL THEN i.active ELSE 0 END,g.id,
                CASE WHEN typeof(g.state)='integer' THEN g.state ELSE NULL END,
                CASE WHEN typeof(g.key_count)='integer' THEN g.key_count ELSE NULL END FROM library_inventory_identity i
            LEFT JOIN library_inventory_generation g ON g.id=i.active AND g.owner=i.owner
                AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role
            WHERE i.owner=$o AND i.scope=$s AND i.role=$r
            """;
        identities.Parameters.AddWithValue("$o", owner);
        identities.Parameters.AddWithValue("$s", (int)scope);
        identities.Parameters.AddWithValue("$r", (int)role);
        using var rows = identities.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var generation = ReadActiveInventoryGeneration(rows);
            if (generation is null) { unpublished = checked(unpublished + 1); continue; }
            published = checked(published + 1);
            if (published > LibraryInventoryUnionLimits.MaxPublishedProducers)
                throw new ObjectStoreException(ObjectStoreCodes.Limit,
                    $"An inventory union supports at most {LibraryInventoryUnionLimits.MaxPublishedProducers} active published producers.");
            generations.Add(generation.Value);
        }
        return new(generations, published, unpublished);
    }

    // The first four columns are shared by selected and library-wide identity resolution. The SQL join must bind
    // generation identity exactly; a positive pointer into another identity must never become a plausible empty result.
    private static InventoryUnionGeneration? ReadActiveInventoryGeneration(SqliteDataReader rows)
    {
        var active = LibraryInventoryMetadata.ParseActive(rows.GetValue(0));
        if (active is null) return null;
        if (rows.GetValue(1) is not long id || id != active || rows.GetValue(2) is not long state || state != 1 ||
            rows.GetValue(3) is not long count || count < 0)
            throw new InvalidOperationException("An active producer inventory or its committed key count is unreadable.");
        return new(id, count);
    }

    internal ValueTask<LibraryInventoryUnionMeasurement> MeasureInventoryUnionAsync(string owner, LibraryOwnerScope scope, LibraryObjectRole role, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use((owner, scope, role), (c, _, identity) => InventoryRead(c, ct, tx =>
        {
            var resolved = ResolveInventoryUnion(c, tx, identity.owner, identity.scope, identity.role, ct);
            // The identity SELECT pins both generations and object index before concurrent writer work.
            At(StorePoint.InventoryUnionSnapshot);
            ct.ThrowIfCancellationRequested();
            var totals = new InventoryCoverageCounter();
            MergeInventoryStreams(c, tx, resolved.Generations, null, StorePoint.InventoryUnionRow,
                (sides, length) => totals.Add(length), ct);
            return resolved.Measurement(totals);
        }), ct);
    }

    // A fixed cap bounds both SQL statement state and managed heads. Each SQL cursor follows the expected-key PK, without
    // DISTINCT or temporary sorting; side bits on equal keys support the pair-local intersection without a second traversal.
    private void MergeInventoryStreams(SqliteConnection c, SqliteTransaction tx, List<InventoryUnionGeneration> left,
        List<InventoryUnionGeneration>? right, StorePoint rowPoint, Action<int, long?> accept, CancellationToken ct, Action<InventoryUnionGeneration>? reference = null)
    {
        var capacity = left.Count + (right?.Count ?? 0);
        if (capacity > LibraryInventoryPairLimits.MaxStreamHeads)
            throw new ObjectStoreException(ObjectStoreCodes.Limit, "Too many inventory stream heads.");
        var streams = new List<InventoryUnionCursor>(capacity);
        var heads = new PriorityQueue<InventoryUnionCursor, ObjectKey>(capacity);
        try
        {
            AddStreams(left, 1);
            if (right is not null) AddStreams(right, 2);
            ObjectKey previous = default; var havePrevious = false; var sides = 0; long? length = null;
            while (heads.TryDequeue(out var stream, out var key))
            {
                At(rowPoint);
                ct.ThrowIfCancellationRequested();
                if (havePrevious && key != previous)
                {
                    accept(sides, length);
                    sides = 0; length = null;
                }
                previous = key; havePrevious = true;
                sides |= stream.Side; length = stream.IndexedLength;
                reference?.Invoke(stream.Generation);
                if (stream.MoveNext(ct)) heads.Enqueue(stream, stream.Key);
            }
            if (havePrevious) accept(sides, length);
            ct.ThrowIfCancellationRequested();
        }
        finally { foreach (var stream in streams) stream.Dispose(); }

        void AddStreams(List<InventoryUnionGeneration> generations, int side)
        {
            foreach (var generation in generations)
            {
                ct.ThrowIfCancellationRequested();
                var stream = new InventoryUnionCursor(c, tx, generation, side);
                streams.Add(stream);
                if (stream.MoveNext(ct)) heads.Enqueue(stream, stream.Key);
            }
        }
    }

    private sealed class InventoryUnionCursor : IDisposable
    {
        private readonly SqliteCommand command;
        private readonly SqliteDataReader rows;
        private readonly long recordedCount;
        private long count;
        public InventoryUnionGeneration Generation { get; }
        public int Side { get; }
        public ObjectKey Key { get; private set; }
        public long? IndexedLength { get; private set; }

        public InventoryUnionCursor(SqliteConnection c, SqliteTransaction tx, InventoryUnionGeneration generation, int side)
        {
            Side = side; Generation = generation;
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
