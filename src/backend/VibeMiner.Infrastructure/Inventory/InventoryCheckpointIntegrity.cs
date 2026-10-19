using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private sealed record CheckpointDigestTable(string Name, string[] Keys, string[] Columns, string? Family = null);
    // Source checkpoints bind only immutable source columns. Resolution checkpoints bind final
    // mappings separately, so later verified work cannot invalidate an earlier source checkpoint.
    private static CheckpointDigestTable[] CheckpointDigestTables(string kind)
    {
        if (kind == "encoding-content") return
        [
            new("en_content", ["ckey"], ["ckey", "bytes"]),
            new("en_candidates", ["ckey", "ordinal"], ["ckey", "ordinal", "ekey"])
        ];
        if (kind == "encoding") return [new("en_objects", ["ekey"], ["ekey", "bytes"])];
        if (kind.EndsWith("-resolved", StringComparison.Ordinal))
            return [new("inventory_origins", ["id"], ["id", "ekey", "encoded", "decoded"], kind[..^9])];
        return
        [
            new("inventory_origins", ["id"], ["id", "family", "name", "fdid", "ckey", "reference", "width",
                "declared_encoded", "declared_decoded", "locale", "flags", "name_hash", "raw"], kind),
            new("inventory_tags", ["ordinal"], ["ordinal", "name", "type", "bits"], kind)
        ];
    }

    private async Task<string> ComputeCheckpointDigestAsync(long generation, string kind, CancellationToken token,
        Func<string, Task>? progress = null)
    {
        ValidateCheckpointKind(kind);
        token.ThrowIfCancellationRequested();
        var tables = CheckpointDigestTables(kind);
        var expectedRows = await Schedule(() => tables.Sum(table => Convert.ToInt64(Scalar(
            $"SELECT COUNT(*) FROM {table.Name} WHERE g=$g{(table.Family is null ? "" : " AND family=$f")}", null,
            ("$g", generation), ("$f", table.Family)))), token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long charged = 0, completed = 0;
        void Frame(ReadOnlySpan<byte> value)
        {
            if (value.Length > (16L << 30) - charged) throw CheckpointError("Checkpoint integrity work exceeds its byte bound.");
            charged += value.Length;
            hash.AppendData(value);
        }
        void Number(long value)
        { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); Frame(bytes); }
        void Text(string value)
        { var bytes = Encoding.UTF8.GetBytes(value); Number(bytes.Length); Frame(bytes); }
        Text(InventoryCheckpointRevision); Number(generation); Text(kind);
        foreach (var table in tables)
        {
            Text(table.Name); Text(table.Family ?? ""); Number(table.Columns.Length);
            foreach (var column in table.Columns) Text(column);
            object[]? after = null;
            long tableRows = 0;
            while (true)
            {
                var read = await Schedule(() =>
                {
                    var fields = table.Keys.Concat(table.Columns.SelectMany(column =>
                        new[] { $"typeof({column})", $"length(CAST({column} AS BLOB))", $"CAST({column} AS BLOB)" }));
                    var cursor = after is null ? "" : table.Keys.Length == 1 ? $" AND {table.Keys[0]}>$k0" :
                        $" AND ({string.Join(',', table.Keys)})>({string.Join(',', table.Keys.Select((_, i) => "$k" + i))})";
                    using var command = Command($"SELECT {string.Join(',', fields)} FROM {table.Name} WHERE g=$g" +
                        (table.Family is null ? "" : " AND family=$f") + cursor + $" ORDER BY {string.Join(',', table.Keys)} LIMIT 128", null,
                        ("$g", generation));
                    if (table.Family is not null) command.Parameters.AddWithValue("$f", table.Family);
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
                            if (type is not ("null" or "integer" or "text" or "blob"))
                                throw CheckpointError("A checkpoint row has an unsupported stored value type.");
                            Text(type);
                            if (type == "null") { Number(0); continue; }
                            var length = reader.GetInt64(field + 1);
                            if (length > (2L << 20)) throw CheckpointError("A checkpoint field exceeds its admitted byte bound.");
                            Number(length);
                            var bytes = reader.GetFieldValue<byte[]>(field + 2);
                            if (bytes.LongLength != length) throw CheckpointError("A checkpoint field could not be read completely.");
                            for (var offset = 0; offset < bytes.Length;)
                            {
                                token.ThrowIfCancellationRequested();
                                var slice = Math.Min(65_536, bytes.Length - offset);
                                Frame(bytes.AsSpan(offset, slice)); offset += slice;
                            }
                        }
                        after = new object[table.Keys.Length];
                        for (var key = 0; key < after.Length; key++)
                        {
                            if (reader.IsDBNull(key)) throw CheckpointError("A checkpoint row ordering key is missing.");
                            after[key] = reader.GetValue(key);
                        }
                        count++; tableRows++;
                    }
                    return count;
                }, token);
                completed += read;
                if (progress is not null && (completed % 16384 < 128 || completed == expectedRows))
                    await progress(InventoryProgress.Format("Verifying " + kind + " checkpoint", completed, expectedRows, "records"));
                if (read < 128) break;
            }
            Frame([0x45]); Number(tableRows);
        }
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
