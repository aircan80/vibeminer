using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects.Testing;

/// <summary>Authored integration faults around the real durable source-publication acknowledgement.</summary>
public static class InventorySourceFaults
{
    public static ObjectStoreOptions AfterPublicationFailure(ObjectStoreOptions options, Func<bool> fail, bool failReadback = false)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(fail); var readback = 0;
        return options with { Fault = point =>
        {
            if (point == StorePoint.InventorySourceSnapshot && Interlocked.Exchange(ref readback, 0) == 1)
                throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "Authored proof readback failure.");
            if (point != StorePoint.InventoryPublished || !fail()) return;
            if (failReadback) Volatile.Write(ref readback, 1);
            throw new IOException("Authored acknowledgement loss after committed inventory publication.");
        } };
    }

    /// <summary>
    /// Test-only interleaving: author another complete matching generation in one SQLite transaction between a real commit
    /// and its failed acknowledgement. No payload rows or physical claims are created. This proves exact-generation readback.
    /// </summary>
    public static long PublishMatchingReplacement(string directory, string owner, LibraryOwnerScope scope, string producer,
        LibraryObjectRole role, LibraryInventorySourceDescriptor descriptor)
    {
        var claim = new LibraryClaim(owner, scope, producer, role);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "objects.sqlite3"), Pooling = false }.ToString()); connection.Open();
        using var tx = connection.BeginTransaction();
        using var command = LibraryInventoryMetadata.Command(connection, tx, claim, """
            INSERT INTO library_inventory_generation(owner,scope,producer,role,state,key_count,key_digest)
            SELECT g.owner,g.scope,g.producer,g.role,1,g.key_count,g.key_digest FROM library_inventory_generation g
            JOIN library_inventory_identity i ON i.active=g.id
            WHERE i.owner=$o AND i.scope=$s AND i.producer=$p AND i.role=$r;
            SELECT last_insert_rowid();
            """);
        var generation = (long)command.ExecuteScalar()!;
        command.CommandText = """
            INSERT INTO library_inventory_key(generation,k)
            SELECT $new,k FROM library_inventory_key WHERE generation=(SELECT active FROM library_inventory_identity WHERE owner=$o AND scope=$s AND producer=$p AND role=$r);
            UPDATE library_inventory_identity SET latest=$new,active=$new WHERE owner=$o AND scope=$s AND producer=$p AND role=$r;
            """;
        command.Parameters.AddWithValue("$new", generation); command.ExecuteNonQuery();
        LibraryInventorySourceMetadata.Insert(connection, tx, claim, generation, descriptor); tx.Commit(); return generation;
    }
}
