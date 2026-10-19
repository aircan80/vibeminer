using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private sealed record DigestTable(string Name, string[] Keys, string[] Columns);
    private static readonly DigestTable[] DigestTables =
    [
        new("en_content", ["ckey"], ["ckey", "bytes"]),
        new("en_candidates", ["ckey", "ordinal"], ["ckey", "ordinal", "ekey"]),
        new("en_objects", ["ekey"], ["ekey", "bytes"]),
        new("inventory_origins", ["id"], ["id", "family", "name", "fdid", "ckey", "reference", "width", "declared_encoded", "declared_decoded", "locale", "flags", "name_hash", "raw", "ekey", "encoded", "decoded"]),
        new("inventory_tags", ["family", "ordinal"], ["family", "ordinal", "name", "type", "bits"]),
        new("inventory_logical", ["ckey"], ["ckey", "bytes"]),
        new("inventory_units", ["ekey"], ["ekey", "bytes"])
    ];
    private const long InventoryDigestMaxBytes = 16L << 30;
    private const long InventoryDigestMaxValueBytes = 2L << 20;

    // Hash directly on the existing database worker, then yield its queue after each128 rows.
    // No open reader/transaction crosses a worker slice and no catalogue collection is retained.
    internal async Task<string> ComputeInventoryDigestAsync(long generation, CancellationToken token, Func<string, Task>? progress = null)
    {
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
        token.ThrowIfCancellationRequested();
        var expectedRows = await Schedule(() => DigestTables.Sum(table => Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM {table.Name} WHERE g=$g", null, ("$g", generation)))), token);
        long completedRows = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long chargedBytes = 0;
        void Frame(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > InventoryDigestMaxBytes - chargedBytes)
                throw new DiscoveryException("INVENTORY_LIMIT", "Inventory integrity verification exceeds its byte budget.");
            chargedBytes += bytes.Length; hash.AppendData(bytes);
        }
        void Number(long number)
        {
            Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, number); Frame(bytes);
        }
        void Text(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); Number(bytes.Length); Frame(bytes);
        }
        Text("vibeminer-inventory-digest-v1"); Number(generation);
        foreach (var table in DigestTables)
        {
            token.ThrowIfCancellationRequested();
            Frame([0x54]); Text(table.Name); Number(table.Columns.Length);
            foreach (var column in table.Columns) Text(column);
            object[]? after = null;
            long rows = 0;
            while (true)
            {
                var read = await Schedule(() =>
                {
                    var projection = string.Join(',', table.Keys.Concat(table.Columns.SelectMany(column =>
                        new[] { $"typeof({column})", $"length(CAST({column} AS BLOB))", $"CAST({column} AS BLOB)" })));
                    var predicate = after is null ? "" : table.Keys.Length == 1
                        ? $" AND {table.Keys[0]}>$k0"
                        : $" AND ({string.Join(',', table.Keys)})>({string.Join(',', table.Keys.Select((_, i) => "$k" + i))})";
                    using var command = Command($"SELECT {projection} FROM {table.Name} WHERE g=$g{predicate} ORDER BY {string.Join(',', table.Keys)} LIMIT 128", null, ("$g", generation));
                    if (after is not null)
                        for (var key = 0; key < after.Length; key++) command.Parameters.AddWithValue("$k" + key, after[key]);
                    using var reader = command.ExecuteReader();
                    var count = 0;
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested(); Frame([0x52]);
                        for (var column = 0; column < table.Columns.Length; column++)
                        {
                            token.ThrowIfCancellationRequested(); Frame([0x43]);
                            var field = table.Keys.Length + column * 3;
                            var type = reader.GetString(field);
                            // Immutable inventory fields use only these SQLite storage classes.
                            // Unexpected REAL values must fail closed rather than round into text.
                            if (type is not ("null" or "integer" or "text" or "blob"))
                                throw new DiscoveryException("CATALOG_INTEGRITY", "Inventory contains an unsupported stored value type.");
                            Text(type);
                            if (type == "null") { Number(0); continue; }
                            var length = reader.GetInt64(field + 1);
                            if (length > InventoryDigestMaxValueBytes)
                                throw new DiscoveryException("CATALOG_INTEGRITY", "Inventory field exceeds its admitted integrity bound.");
                            Number(length);
                            // SQLite materializes one value; bound its byte length before the
                            // provider creates that single owned array (at most one tag bitmap).
                            var value = reader.GetFieldValue<byte[]>(field + 2);
                            if (value.LongLength != length) throw new DiscoveryException("CATALOG_INTEGRITY", "Inventory field cannot be read completely.");
                            for (long position = 0; position < length;)
                            {
                                token.ThrowIfCancellationRequested();
                                var countBytes = (int)Math.Min(65_536, length - position);
                                Frame(value.AsSpan((int)position, countBytes)); position += countBytes;
                            }
                        }
                        after = new object[table.Keys.Length];
                        for (var key = 0; key < after.Length; key++)
                        {
                            if (reader.IsDBNull(key)) throw new DiscoveryException("CATALOG_INTEGRITY", "Inventory ordering key is absent.");
                            after[key] = reader.GetValue(key);
                        }
                        count++; rows++;
                    }
                    return count;
                }, token);
                completedRows += read;
                if (progress is not null && (completedRows % 16384 < 128 || completedRows == expectedRows))
                    await progress(InventoryProgress.Format("Verifying local catalog", completedRows, expectedRows, "records"));
                if (read < 128) break;
            }
            Frame([0x45]); Number(rows);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // Called only after startup/single-producer ownership has fenced every prior staging owner.
    // Each child delete is its own short worker operation; never cascade-delete millions of rows.
    internal async Task CleanupStagingInventoriesAsync(CancellationToken token, Func<string, Task>? progress = null)
    {
        var children = DigestTables.Select(table => table.Name).Concat(new[] { "inventory_available", "inventory_merge_results", "inventory_merge_state", "inventory_resolution_cursor" }).ToArray();
        var tables = await Schedule(() => children.Where(table =>
            Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n", null, ("$n", table)) is not null).ToArray(), token);
        while (true)
        {
            var generation = await Schedule(() => Scalar("SELECT id FROM inventory_generations i WHERE state IN ('staging','abandoned') AND NOT EXISTS(SELECT 1 FROM inventory_checkpoints c WHERE c.g=i.id) ORDER BY id LIMIT 1"), token);
            if (generation is null) return;
            var id = Convert.ToInt64(generation);
            long total = 0, completed = 0;
            foreach (var table in tables)
                total = checked(total + await Schedule(() => Convert.ToInt64(Scalar($"SELECT count(*) FROM {table} WHERE g=$g", null, ("$g", id))), token));
            if (progress is not null) await progress(InventoryProgress.Format("Cleaning up unfinished catalog data", 0, total, "rows"));
            var lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var table in tables)
            {
                while (true)
                {
                    var removed = await Schedule(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var command = Command($"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE g=$g LIMIT 8192) AND EXISTS(SELECT 1 FROM inventory_generations WHERE id=$g AND state IN ('staging','abandoned')) AND NOT EXISTS(SELECT 1 FROM inventory_checkpoints WHERE g=$g)", null, ("$g", id));
                        return command.ExecuteNonQuery();
                    }, token);
                    completed += removed;
                    if (progress is not null && (completed == total || System.Diagnostics.Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 500))
                    {
                        await progress(InventoryProgress.Format("Cleaning up unfinished catalog data", completed, total, "rows"));
                        lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    if (removed < 8192) break;
                }
            }
            var deleted = await Schedule(() =>
            {
                token.ThrowIfCancellationRequested();
                var empty = string.Join(" AND ", tables.Select(table => $"NOT EXISTS(SELECT 1 FROM {table} WHERE g=$g)"));
                using var command = Command($"DELETE FROM inventory_generations WHERE id=$g AND state IN ('staging','abandoned') AND NOT EXISTS(SELECT 1 FROM inventory_checkpoints WHERE g=$g){(empty.Length == 0 ? "" : " AND " + empty)}", null, ("$g", id));
                return command.ExecuteNonQuery();
            }, token);
            if (deleted != 1) throw new DiscoveryException("CATALOG_INTEGRITY", "Abandoned inventory cleanup lost its ownership fence.");
        }
    }
}
