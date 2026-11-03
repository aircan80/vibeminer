using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

internal static class LibraryFootprintTestKit
{
    public static string PhysicalPath(string path)
    {
        if (!OperatingSystem.IsMacOS()) return path;
        if (path.StartsWith("/var/", StringComparison.Ordinal) || path.StartsWith("/tmp/", StringComparison.Ordinal)) return "/private" + path;
        return path;
    }
    public static bool Supported => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64;
    public static string DirectoryPath(TempDirectory temp) => Path.Combine(PhysicalPath(temp.Path), "library");
    public static LibraryFootprintBucket[] Buckets(LibraryFootprintSample sample) => [sample.Packs, sample.Database, sample.DatabaseWal, sample.DatabaseShm, sample.Staging, sample.Other];
    public static void AssertReconciled(LibraryFootprintSample sample)
    {
        Assert.Equal(Buckets(sample).Sum(bucket => bucket.Files), sample.Total.Files);
        Assert.Equal(Buckets(sample).Sum(bucket => bucket.Bytes), sample.Total.Bytes);
        Assert.All(Buckets(sample), bucket => { Assert.True(bucket.Files >= 0); Assert.True(bucket.Bytes >= 0); });
        Assert.True(sample.CompletedAt >= sample.StartedAt);
        Assert.Equal(sample.Problems.Count == 0 && !sample.TruncatedDiagnostics, sample.Complete);
    }
    public static async Task SeedMixedAsync(ObjectStore library)
    {
        var original = Synthetic.Headerless(228000, 2000);
        var generated = new PutObject(LibraryInventoryTestKit.Key(228000), 137, new byte[137], ObjectOrigin.Unknown);
        Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("source", LibraryOwnerScope.Build, "encoding", LibraryObjectRole.Original).PutAsync([original], true)).Outcome);
        Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("source", LibraryOwnerScope.Global, "tables", LibraryObjectRole.Derived).PutAsync([generated], true)).Outcome);
    }
    public static Dictionary<string, (long Length, string? Hash)> CaptureFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(directory, path),
            path => (new FileInfo(path).Length, Path.GetFileName(path) == "writer.lock" ? null : Hash(path)));
    public static string[] Membership(string directory)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "objects.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner,scope,producer,role,hex(k) FROM library_membership ORDER BY owner,scope,producer,role,k";
        using var reader = command.ExecuteReader(); var rows = new List<string>();
        while (reader.Read()) rows.Add($"{reader.GetString(0)}:{reader.GetInt64(1)}:{reader.GetString(2)}:{reader.GetInt64(3)}:{reader.GetString(4)}");
        return rows.ToArray();
    }
    public static void WriteSized(string directory, string relative, int length)
    {
        var path = Path.Combine(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Synthetic.Random(228001 + length, length));
    }
}
