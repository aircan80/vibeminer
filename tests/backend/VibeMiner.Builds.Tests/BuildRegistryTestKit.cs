using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace VibeMiner.Builds.Tests;

internal static class BuildRegistryTestKit
{
    public static BuildIdentity Identity(int seed = 1, string product = "wow") => BuildIdentity.FromFields(product,
        new Dictionary<string, string>
        {
            ["BuildConfig"] = seed.ToString("x32", CultureInfo.InvariantCulture),
            ["VersionsName"] = "authored-version", ["BuildId"] = seed.ToString(CultureInfo.InvariantCulture),
        });
    public static BuildRef Route(BuildIdentity identity, string region = "us", char cdn = 'c') =>
        new(identity.ProductId, identity.Fields["BuildConfig"], new string(cdn, 32), region);

    public static void Mutate(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public static Dictionary<string, string> HashFiles(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith("-shm", StringComparison.Ordinal) && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith(".lock", StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(directory, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}

internal sealed class RegistryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-build-registry-test-" + Guid.NewGuid().ToString("N"));
    public string Database => System.IO.Path.Combine(Path, "registry.sqlite3");
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
