using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using VibeMiner.Objects;

namespace VibeMiner.Primitives.Tests;

/// <summary>Authored expected keys only: constructing an inventory never requires game bytes or an acquired payload.</summary>
internal static class LibraryInventoryTestKit
{
    public static string DirectoryPath(TempDirectory temp) => Path.Combine(temp.Path, "library");

    public static ObjectKey Key(int value, ObjectKeyType type = ObjectKeyType.Generated)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), value);
        return new(type, bytes);
    }

    public static ObjectKey[] Keys(int start, int count) => Enumerable.Range(start, count)
        .Select(i => Key(i, i % 2 == 0 ? ObjectKeyType.Encoded : ObjectKeyType.Generated)).ToArray();

    public static void MutateIndex(string folder, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "objects.sqlite3"), Pooling = false, ForeignKeys = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
