using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

/// <summary>Generic, bounded provenance for one complete expected-key generation. Producer semantics belong to its caller.</summary>
public sealed record LibraryInventorySourceDescriptor
{
    public string Format { get; }
    public string Revision { get; }
    public string Source { get; }
    public long ExpectedKeys { get; }
    public LibraryInventorySourceDescriptor(string format, string revision, string source, long expectedKeys)
    {
        Check(format, 64); Check(revision, 128); Check(source, 1024);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedKeys);
        Format = format; Revision = revision; Source = source; ExpectedKeys = expectedKeys;
    }
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static void Check(string value, int max)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || Utf8.GetByteCount(value) > max)
                throw new ArgumentException("An inventory source field is empty, contains NUL or exceeds its UTF-8 bound.");
        }
        catch (EncoderFallbackException e) { throw new ArgumentException("An inventory source field contains malformed Unicode.", nameof(value), e); }
    }
}

internal static class LibraryInventorySourceMetadata
{
    internal const string Schema = """
        CREATE TABLE library_inventory_source(generation INTEGER PRIMARY KEY REFERENCES library_inventory_generation(id),
            format TEXT NOT NULL,revision TEXT NOT NULL,source TEXT NOT NULL,expected_keys INTEGER NOT NULL,
            row_check BLOB NOT NULL CHECK(length(row_check)=32));
        """;
    internal static void Require(SqliteConnection c, SqliteTransaction? tx = null)
    {
        var version = LibraryInventoryMetadata.Version(c, tx);
        if (version is 1 or 2) throw new ObjectStoreException(ObjectStoreCodes.InventoryUpgradeRequired,
            "Inventory source proof requires the explicit UpgradeLibraryInventorySources metadata upgrade; preserve the library.");
        if (version != 3) throw new ObjectStoreException(version > 3 ? ObjectStoreCodes.NewerFormat : ObjectStoreCodes.IndexUnreadable,
            "The authoritative source-proof format is unsupported; preserve the library.");
    }
    private static byte[] Checksum(long generation, LibraryClaim claim, LibraryInventorySourceDescriptor descriptor)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Field("vibeminer-inventory-source-v1"); Number(generation); Field(claim.Owner); Number((long)claim.Scope); Field(claim.Producer); Number((long)claim.Role);
        Field(descriptor.Format); Field(descriptor.Revision); Field(descriptor.Source); Number(descriptor.ExpectedKeys);
        return hash.GetHashAndReset();
        void Number(long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, value); hash.AppendData(bytes); }
        void Field(string value) { var bytes = Encoding.UTF8.GetBytes(value); Number(bytes.Length); hash.AppendData(bytes); }
    }
    internal static void Insert(SqliteConnection c, SqliteTransaction tx, LibraryClaim claim, long generation, LibraryInventorySourceDescriptor descriptor)
    {
        Require(c, tx);
        using var count = c.CreateCommand(); count.Transaction = tx;
        count.CommandText = "SELECT CASE WHEN typeof(key_count)='integer' THEN key_count ELSE NULL END FROM library_inventory_generation WHERE id=$g";
        count.Parameters.AddWithValue("$g", generation);
        if (count.ExecuteScalar() is not long keys || keys < 0)
            throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The source generation key count is malformed; preserve the library for repair.");
        if (keys != descriptor.ExpectedKeys)
            throw new ObjectStoreException(ObjectStoreCodes.InventorySourceMismatch, "The source descriptor count does not match the complete staged key set.");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO library_inventory_source VALUES($g,$f,$r,$s,$n,$check)";
        cmd.Parameters.AddWithValue("$g", generation); cmd.Parameters.AddWithValue("$f", descriptor.Format);
        cmd.Parameters.AddWithValue("$r", descriptor.Revision); cmd.Parameters.AddWithValue("$s", descriptor.Source);
        cmd.Parameters.AddWithValue("$n", descriptor.ExpectedKeys); cmd.Parameters.AddWithValue("$check", Checksum(generation, claim, descriptor)); cmd.ExecuteNonQuery();
    }
    private const string Columns = """
        p.format,p.revision,p.source,CASE WHEN typeof(p.expected_keys)='integer' THEN p.expected_keys ELSE NULL END,p.row_check,
        typeof(p.format)='text' AND length(CAST(p.format AS BLOB)) BETWEEN 1 AND 64,
        typeof(p.revision)='text' AND length(CAST(p.revision AS BLOB)) BETWEEN 1 AND 128,
        typeof(p.source)='text' AND length(CAST(p.source AS BLOB)) BETWEEN 1 AND 1024,
        typeof(p.row_check)='blob' AND length(p.row_check)=32
        """;
    internal static LibraryInventorySourceDescriptor? Read(SqliteConnection c, SqliteTransaction tx, LibraryClaim claim, long generation, long keyCount)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM library_inventory_source p WHERE p.generation=$g";
        cmd.Parameters.AddWithValue("$g", generation); using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        var descriptor = ReadRow(row, claim, generation, keyCount);
        if (row.Read()) throw new ArgumentException("Duplicate source proof for one generation.");
        return descriptor;
    }
    private static LibraryInventorySourceDescriptor ReadRow(SqliteDataReader row, LibraryClaim claim, long generation, long keyCount)
    {
        for (var i = 5; i <= 8; i++) if (row.GetValue(i) is not long valid || valid != 1) throw new ArgumentException("Malformed source-proof storage class or size.");
        if (row.GetValue(3) is not long count || count < 0 || count != keyCount) throw new ArgumentException("Source-proof count disagrees with its generation.");
        var descriptor = new LibraryInventorySourceDescriptor(row.GetString(0), row.GetString(1), row.GetString(2), count);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(generation, claim, descriptor), row.GetFieldValue<byte[]>(4)))
            throw new ArgumentException("The source-proof checksum failed.");
        return descriptor;
    }
    internal static void Validate(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        using (var shape = c.CreateCommand())
        {
            shape.Transaction = tx;
            shape.CommandText = "SELECT CASE WHEN typeof(sql)='text' AND length(CAST(sql AS BLOB))<=4096 THEN sql ELSE NULL END FROM sqlite_master WHERE type='table' AND name='library_inventory_source'";
            if (shape.ExecuteScalar() is not string ddl || ddl != Schema.Trim().TrimEnd(';')) throw new ArgumentException("The source-proof table does not match its authoritative format.");
            shape.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE tbl_name='library_inventory_source' AND type!='table')";
            if ((long)shape.ExecuteScalar()! != 0) throw new ArgumentException("Unexpected source-proof schema objects.");
        }
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT {Columns},CASE WHEN typeof(p.generation)='integer' THEN p.generation ELSE NULL END,
                g.owner,CASE WHEN typeof(g.scope)='integer' THEN g.scope ELSE NULL END,g.producer,
                CASE WHEN typeof(g.role)='integer' THEN g.role ELSE NULL END,
                CASE WHEN typeof(g.state)='integer' THEN g.state ELSE NULL END,
                CASE WHEN typeof(g.key_count)='integer' THEN g.key_count ELSE NULL END,
                typeof(g.owner)='text' AND length(CAST(g.owner AS BLOB)) BETWEEN 1 AND 512,
                typeof(g.producer)='text' AND length(CAST(g.producer AS BLOB)) BETWEEN 1 AND 128
            FROM library_inventory_source p LEFT JOIN library_inventory_generation g ON g.id=p.generation ORDER BY p.generation
            """;
        using var row = cmd.ExecuteReader(); long previous = 0;
        while (row.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (row.GetValue(9) is not long generation || generation <= previous || row.GetValue(16) is not long ov || ov != 1 ||
                row.GetValue(17) is not long pv || pv != 1 || row.GetValue(11) is not long scope || scope is < 0 or > 1 ||
                row.GetValue(13) is not long role || role is < 0 or > 1 || row.GetValue(14) is not long state || state != 1 ||
                row.GetValue(15) is not long count || count < 0) throw new ArgumentException("Source proof is duplicated, orphaned, unpublished or malformed.");
            previous = generation;
            var claim = new LibraryClaim(row.GetString(10), (LibraryOwnerScope)scope, row.GetString(12), (LibraryObjectRole)role);
            _ = ReadRow(row, claim, generation, count);
        }
    }

}

public sealed partial class ObjectStore
{
    internal ValueTask<long?> MatchInventorySourceAsync(LibraryClaim claim, LibraryInventorySourceDescriptor descriptor, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use(claim, (c, _, identity) => InventoryRead(c, ct, tx =>
        {
            LibraryInventorySourceMetadata.Require(c, tx);
            var generation = LibraryInventoryMetadata.Active(c, tx, identity);
            if (generation is null) return (long?)null;
            using var count = c.CreateCommand(); count.Transaction = tx;
            count.CommandText = "SELECT CASE WHEN typeof(key_count)='integer' THEN key_count ELSE NULL END FROM library_inventory_generation WHERE id=$g AND state=1";
            count.Parameters.AddWithValue("$g", generation.Value);
            if (count.ExecuteScalar() is not long keys || keys < 0) throw new ArgumentException("The active source generation count is malformed.");
            var proof = LibraryInventorySourceMetadata.Read(c, tx, identity, generation.Value, keys);
            At(StorePoint.InventorySourceSnapshot); ct.ThrowIfCancellationRequested();
            return proof == descriptor ? generation : null;
        }), ct);
    }
}
