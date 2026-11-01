using Xunit;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Only where file modes bite: root (and Windows) ignore a read-only folder, so the test would prove nothing.</summary>
public sealed class ReadOnlyMediaFactAttribute : FactAttribute
{
    public ReadOnlyMediaFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "File modes are Unix only.";
        else if (Environment.IsPrivilegedProcess) Skip = "Root writes into a read-only folder, so it can't stand in for read-only media.";
    }
}

/// <summary>A store on read-only media (6.10): the folder and every file in it can't be written.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ObjectStoreReadOnlyMediaTests
{
    private const UnixFileMode ReadOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode ReadOnlyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead;
    private const UnixFileMode Writable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [ReadOnlyMediaFact]
    public async Task ACleanlyClosedStoreOpensFromReadOnlyMediaAndServesEverything()
    {
        using var t = new TempDirectory();
        var dir = Path.Combine(t.Path, "media #1 ?%20 ü");  // SQLite gets an immutable index by URI: these must stay part of the name
        var objects = Enumerable.Range(1, 20).Select(i => Synthetic.Headerless(i, 500 + i * 37)).ToList();
        await using (var writer = await ObjectStore.OpenAsync(dir, Quick)) await writer.PutAsync([.. objects, .. HistoryRecordFormat.Objects(Synthetic.Observation(1))]);
        Assert.False(File.Exists(IndexFile(dir) + "-wal"));
        var before = AllFiles(dir);
        try
        {
            MakeReadOnly(dir);
            await using (var shared = await ObjectStore.OpenReadOnlyAsync(dir))
            {
                foreach (var o in objects) Assert.Equal(o.Bytes.ToArray(), (await shared.ReadAsync(o.Key)).Bytes);
                Assert.Equal(ReadState.Absent, (await shared.ReadAsync(Synthetic.Headerless(99).Key)).State);
                Assert.Single(await shared.ReadHistoryAsync(new HistoryQuery()));
                Assert.Equal((22L, 1L), ((await shared.StatsAsync()).Objects, (await shared.StatsAsync()).HistoryRecords));
                Assert.Equal(22, (await shared.VerifyAsync(full: true)).Checked);
                var scrub = await shared.ScrubAsync(full: true);
                Assert.Null(scrub.IndexProblem);
                Assert.Equal(22, scrub.Verify.Checked);
            }
            Assert.Equal(before, AllFiles(dir));
        }
        finally { MakeWritable(dir); }
    }

    [ReadOnlyMediaFact]
    public async Task AStoreWithAWalButNoShmOnReadOnlyMediaIsRefusedNotReadStale()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var copy = Path.Combine(t.Path, "copy");
        var o = Synthetic.Headerless(1, 900);
        await using (var writer = await ObjectStore.OpenAsync(dir, Quick))
        {
            await writer.PutAsync([o], commitNow: true);
            // A copy taken while the writer runs: its commits are still in the -wal, which immutable=1 would ignore.
            CopyFolder(dir, copy, skip: f => f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("writer.lock", StringComparison.Ordinal));
        }
        Assert.True(new FileInfo(IndexFile(copy) + "-wal").Length > 0);
        try
        {
            MakeReadOnly(copy);
            Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(copy))).Code);
        }
        finally { MakeWritable(copy); }
        // With the folder writable again SQLite builds the -shm, and the copy opens with the writer's commit.
        await using var opened = await ObjectStore.OpenReadOnlyAsync(copy);
        Assert.Equal(o.Bytes.ToArray(), (await opened.ReadAsync(o.Key)).Bytes);
    }

    [ReadOnlyMediaFact]
    public void ACantOpenInAWritableFolderNeverMakesAReaderImmutable()
    {
        // Out of file handles also reads CANTOPEN: only a folder that really can't be written counts as read-only media.
        using var t = new TempDirectory();
        Directory.CreateDirectory(t.Path);
        var index = Path.Combine(t.Path, "objects.sqlite3");
        Assert.False(ObjectIndex.ReadOnlyMedia(index, new Microsoft.Data.Sqlite.SqliteException("x", SQLitePCL.raw.SQLITE_CANTOPEN)));
        try
        {
            MakeReadOnly(t.Path);
            Assert.True(ObjectIndex.ReadOnlyMedia(index, new Microsoft.Data.Sqlite.SqliteException("x", SQLitePCL.raw.SQLITE_CANTOPEN)));
        }
        finally { MakeWritable(t.Path); }
    }

    [ReadOnlyMediaFact]
    public async Task AStoreNotCleanlyClosedOnReadOnlyMediaIsRefused()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var copy = Path.Combine(t.Path, "copy");
        await using (var writer = await ObjectStore.OpenAsync(dir, Quick))
        {
            await writer.PutAsync([Synthetic.Headerless(1, 900)], commitNow: true);
            CopyFolder(dir, copy, skip: f => f.EndsWith("-shm", StringComparison.Ordinal) || f.EndsWith("writer.lock", StringComparison.Ordinal));
        }
        // Fold the copy's -wal into its main file, as a last connection's checkpoint does: clean_close stays 0, and no -wal is left.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={IndexFile(copy)};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; cmd.ExecuteNonQuery();
        }
        Assert.False(File.Exists(IndexFile(copy) + "-wal"));
        try
        {
            MakeReadOnly(copy);
            var e = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenReadOnlyAsync(copy));
            Assert.Equal(ObjectStoreCodes.IndexUnreadable, e.Code);
            Assert.Contains("cleanly closed", e.Message, StringComparison.Ordinal);
        }
        finally { MakeWritable(copy); }
    }

    private static string IndexFile(string dir) => Path.Combine(dir, "objects.sqlite3");

    private static Dictionary<string, string> AllFiles(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    private static void CopyFolder(string from, string to, Func<string, bool> skip)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            if (!skip(f)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
    }

    private static void MakeReadOnly(string dir)
    {
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(f, ReadOnlyFile);
        foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Append(dir)) File.SetUnixFileMode(d, ReadOnlyFolder);
    }

    private static void MakeWritable(string dir)
    {
        File.SetUnixFileMode(dir, Writable);
        foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(d, Writable);
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
