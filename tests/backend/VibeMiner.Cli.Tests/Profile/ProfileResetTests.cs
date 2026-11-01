using System.Security.Cryptography;
using VibeMiner.Cli.Profile;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests.Profile;

/// <summary>Reset wipes what a fresh profile rebuilds and never touches the object store's files. Temp folders only.</summary>
public sealed class ProfileResetTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibeminer-reset-test-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string Outside => Path.Combine(root, "outside");
    private static readonly IReadOnlyList<Func<CancellationToken, Task>> NoStops = [];

    public ProfileResetTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Outside);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private string At(params string[] parts) => Path.Combine([Data, .. parts]);

    private static void Write(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    private void FillProfile()
    {
        Write(At(ObjectStore.DefaultFolderName, "index", "store.idx"), 300);
        Write(At(ObjectStore.DefaultFolderName, "packs", "00", "pack-0001"), 5000);
        Write(At(ObjectStore.DefaultFolderName, "lock"), 0);
        Write(At(RealStores.CatalogFolder, RealStores.CatalogFile), 4096);
        Write(At(RealStores.CatalogFolder, RealStores.CatalogFile + "-wal"), 1000);
        Write(At(ProfileReset.GeneratedFolder, "tables", "t.bin"), 700);
        Write(At(ProfileReset.SettingsFile), 50);
        Write(At(ProfileReset.SettingsTemporaryFile), 20);
    }

    private Dictionary<string, string> Hashes(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private ResetResult Result(ResetReport report, string name) => Assert.Single(report.Targets, t => t.Name == name);

    [Fact]
    public async Task WipesTheDefaultTargetsAndKeepsTheObjectStoreByteForByte()
    {
        FillProfile();
        var before = Hashes(At(ObjectStore.DefaultFolderName));

        var report = await new ProfileReset(Data).ResetAsync(NoStops, default);

        Assert.Equal(before, Hashes(At(ObjectStore.DefaultFolderName)));
        Assert.Equal([ObjectStore.DefaultFolderName], report.Kept);
        Assert.False(Directory.Exists(At(RealStores.CatalogFolder)));
        Assert.False(Directory.Exists(At(ProfileReset.GeneratedFolder)));
        Assert.False(File.Exists(At(ProfileReset.SettingsFile)));
        Assert.False(File.Exists(At(ProfileReset.SettingsTemporaryFile)));
        Assert.All(report.Targets, t => Assert.Equal(ResetState.Wiped, t.State));
        Assert.Equal(5096, Result(report, RealStores.CatalogFolder).BytesFreed);
        Assert.Equal(700, Result(report, ProfileReset.GeneratedFolder).BytesFreed);
        Assert.Equal(50, Result(report, ProfileReset.SettingsFile).BytesFreed);
        Assert.Equal(20, Result(report, ProfileReset.SettingsTemporaryFile).BytesFreed);
        Assert.False(report.Failed);
        Assert.False(report.Cancelled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Data, "*" + ProfileReset.ResetMarker + "*"));
        Assert.Equal([ObjectStore.DefaultFolderName], Directory.EnumerateFileSystemEntries(Data).Select(Path.GetFileName));
    }

    [Fact]
    public async Task LegacyFilesAreNotTouched()
    {
        Write(At("discovery-v1.sqlite3"), 10);
        Write(At("objects-v1", "x"), 10);
        await new ProfileReset(Data).ResetAsync(NoStops, default);
        Assert.True(File.Exists(At("discovery-v1.sqlite3")));
        Assert.True(File.Exists(At("objects-v1", "x")));
    }

    [Fact]
    public async Task StopsRunInOrderBeforeAnythingIsDeleted()
    {
        FillProfile();
        var seen = new List<string>();
        Func<CancellationToken, Task> Stop(string name) => async ct =>
        {
            await Task.Yield();
            Assert.True(File.Exists(At(RealStores.CatalogFolder, RealStores.CatalogFile)));
            seen.Add(name);
        };

        await new ProfileReset(Data).ResetAsync([Stop("download-all"), Stop("downloaded"), Stop("scrub")], default);

        Assert.Equal(["download-all", "downloaded", "scrub"], seen);
        Assert.False(Directory.Exists(At(RealStores.CatalogFolder)));
    }

    [Fact]
    public async Task AThrowingStopDeletesNothing()
    {
        FillProfile();
        var ran = false;
        Func<CancellationToken, Task> boom = _ => throw new InvalidOperationException("still running");
        Func<CancellationToken, Task> later = _ => { ran = true; return Task.CompletedTask; };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ProfileReset(Data).ResetAsync([boom, later], default));

        Assert.False(ran);
        Assert.True(File.Exists(At(RealStores.CatalogFolder, RealStores.CatalogFile)));
        Assert.True(File.Exists(At(ProfileReset.GeneratedFolder, "tables", "t.bin")));
        Assert.True(File.Exists(At(ProfileReset.SettingsFile)));
        Assert.True(File.Exists(At(ProfileReset.SettingsTemporaryFile)));
    }

    [Fact]
    public async Task CancellationBeforeTheStartDeletesNothing()
    {
        FillProfile();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProfileReset(Data).ResetAsync(NoStops, cts.Token));
        Assert.True(File.Exists(At(RealStores.CatalogFolder, RealStores.CatalogFile)));
        Assert.True(File.Exists(At(ProfileReset.SettingsFile)));
    }

    [Fact]
    public async Task CancellationDuringTheStopsDeletesNothing()
    {
        FillProfile();
        using var cts = new CancellationTokenSource();
        Func<CancellationToken, Task> stop = _ => cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProfileReset(Data).ResetAsync([stop], cts.Token));
        Assert.True(File.Exists(At(RealStores.CatalogFolder, RealStores.CatalogFile)));
    }

    [Fact]
    public async Task CancellingDuringTheResetFinishesTheCurrentTargetAndSkipsTheRest()
    {
        FillProfile();
        using var cts = new CancellationTokenSource();
        var reset = new ProfileReset(Data)
        {
            // Cancel while the first target is about to be wiped: it still finishes, nothing after it starts.
            BeforeTarget = t => t.Name == RealStores.CatalogFolder ? cts.CancelAsync() : Task.CompletedTask,
        };

        var report = await reset.ResetAsync(NoStops, cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(ResetState.Wiped, Result(report, RealStores.CatalogFolder).State);
        Assert.False(Directory.Exists(At(RealStores.CatalogFolder)));
        foreach (var name in new[] { ProfileReset.GeneratedFolder, ProfileReset.SettingsFile, ProfileReset.SettingsTemporaryFile })
            Assert.Equal(ResetState.Skipped, Result(report, name).State);
        Assert.True(File.Exists(At(ProfileReset.GeneratedFolder, "tables", "t.bin")));
        Assert.True(File.Exists(At(ProfileReset.SettingsFile)));
        Assert.True(File.Exists(At(ProfileReset.SettingsTemporaryFile)));
    }

    [Fact]
    public async Task AnEntryOfTheWrongKindFailsAndIsKept()
    {
        Write(At(RealStores.CatalogFolder), 11);                        // a file where a folder is expected
        Write(At(ProfileReset.SettingsFile, "inside"), 12);             // a folder where a file is expected
        Write(At(ProfileReset.SettingsFile + ProfileReset.ResetMarker + "x", "y"), 1); // not a folder target's leftover

        var report = await new ProfileReset(Data).ResetAsync(NoStops, default);

        foreach (var name in new[] { RealStores.CatalogFolder, ProfileReset.SettingsFile })
        {
            var result = Result(report, name);
            Assert.Equal(ResetState.Failed, result.State);
            Assert.Equal(ProfileReset.KindMismatch, result.ErrorType);
            Assert.Contains("nothing was deleted", result.ErrorMessage);
        }
        Assert.True(File.Exists(At(RealStores.CatalogFolder)));
        Assert.True(File.Exists(At(ProfileReset.SettingsFile, "inside")));
        Assert.True(File.Exists(At(ProfileReset.SettingsFile + ProfileReset.ResetMarker + "x", "y")));
        Assert.Empty(report.Leftovers);
        Assert.True(report.Failed);
    }

    [Fact]
    public async Task AFolderThatCantBeMeasuredIsStillDeletedWithUnknownBytes()
    {
        Write(At(ProfileReset.GeneratedFolder, "a"), 5);
        Write(At(RealStores.CatalogFolder, RealStores.CatalogFile), 6);
        var reset = new ProfileReset(Data)
        {
            Measurer = d => d.Name.StartsWith(ProfileReset.GeneratedFolder, StringComparison.Ordinal)
                ? throw new UnauthorizedAccessException("no listing") : 6,
        };

        var report = await reset.ResetAsync(NoStops, default);

        var result = Result(report, ProfileReset.GeneratedFolder);
        Assert.Equal(ResetState.Wiped, result.State);
        Assert.Null(result.BytesFreed);
        Assert.False(Directory.Exists(At(ProfileReset.GeneratedFolder)));
        Assert.Equal(6, Result(report, RealStores.CatalogFolder).BytesFreed);
        Assert.True(report.BytesUnknown);
        Assert.False(report.Failed);
    }

    [Fact]
    public async Task LeftoverResetFoldersAreDeletedToo()
    {
        Write(At(RealStores.CatalogFolder + ProfileReset.ResetMarker + "abc", RealStores.CatalogFile + "-wal"), 123);
        Write(At(ProfileReset.GeneratedFolder + ProfileReset.ResetMarker + "def", "x"), 7);

        var report = await new ProfileReset(Data).ResetAsync(NoStops, default);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Data));
        Assert.Equal(130, report.Leftovers.Sum(l => l.BytesFreed));
        Assert.All(report.Leftovers, l => Assert.Equal(ResetState.Wiped, l.State));
        Assert.Equal(2, report.Leftovers.Count);
    }

    [Fact]
    public async Task ALinkInsideTheDataFolderIsRemovedNeverFollowed()
    {
        var keep = Path.Combine(Outside, "keep.bin");
        Write(keep, 99);
        Write(Path.Combine(Outside, "dir", "deep.bin"), 99);
        var outsideFile = Path.Combine(Outside, "settings.json");
        Write(outsideFile, 10);
        Directory.CreateDirectory(At(RealStores.CatalogFolder));
        File.CreateSymbolicLink(At(RealStores.CatalogFolder, "escape"), Outside);
        File.CreateSymbolicLink(At(RealStores.CatalogFolder, "escape-file"), keep);
        Directory.CreateSymbolicLink(At(ProfileReset.GeneratedFolder), Outside);
        File.CreateSymbolicLink(At(ProfileReset.SettingsFile), outsideFile);

        var report = await new ProfileReset(Data).ResetAsync(NoStops, default);

        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(Path.Combine(Outside, "dir", "deep.bin")));
        Assert.True(File.Exists(outsideFile));
        Assert.All(report.Targets.Take(3), t => Assert.Equal(ResetState.Wiped, t.State));
        Assert.Equal(0, Result(report, RealStores.CatalogFolder).BytesFreed);
        Assert.Equal(0, Result(report, ProfileReset.GeneratedFolder).BytesFreed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Data));
    }

    [Fact]
    public async Task MissingTargetsAreAbsent()
    {
        Write(At(ProfileReset.SettingsFile), 5);
        var report = await new ProfileReset(Data).ResetAsync(NoStops, default);
        Assert.Equal(ResetState.Absent, Result(report, RealStores.CatalogFolder).State);
        Assert.Equal(ResetState.Absent, Result(report, ProfileReset.GeneratedFolder).State);
        Assert.Equal(ResetState.Absent, Result(report, ProfileReset.SettingsTemporaryFile).State);
        Assert.Equal(ResetState.Wiped, Result(report, ProfileReset.SettingsFile).State);
        Assert.False(report.Failed);

        var none = await new ProfileReset(Path.Combine(root, "nowhere")).ResetAsync(NoStops, default);
        Assert.All(none.Targets, t => Assert.Equal(ResetState.Absent, t.State));
    }

    [Fact]
    public async Task OneFailedTargetDoesNotStopTheOthers()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return; // POSIX permissions; root ignores them
        Write(At("locked", "inner", "f"), 3);
        Write(At(ProfileReset.SettingsFile), 5);
        // Moving "locked" needs write access to the data folder; deleting its contents needs write access to "inner".
        File.SetUnixFileMode(At("locked", "inner"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var report = await new ProfileReset(Data, [new("locked", ResetTargetKind.Folder), new(ProfileReset.SettingsFile, ResetTargetKind.File)])
                .ResetAsync(NoStops, default);
            var failed = Assert.Single(report.Targets, t => t.State == ResetState.Failed);
            Assert.Equal("locked", failed.Name);
            Assert.NotNull(failed.ErrorType);
            Assert.Equal(ResetState.Wiped, Result(report, ProfileReset.SettingsFile).State);
            Assert.True(report.Failed);
        }
        finally
        {
            foreach (var dir in Directory.EnumerateDirectories(Data, "locked*"))
                File.SetUnixFileMode(Path.Combine(dir, "inner"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task PluggableTargetsAreWiped()
    {
        Write(At("new-store-v1", "a"), 4);
        var report = await new ProfileReset(Data, [new("new-store-v1", ResetTargetKind.Folder)]).ResetAsync(NoStops, default);
        Assert.Equal(ResetState.Wiped, Assert.Single(report.Targets).State);
        Assert.False(Directory.Exists(At("new-store-v1")));
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(ObjectStore.DefaultFolderName)]
    [InlineData("CDN-STORE-V1")]
    public void BadTargetsAreRefused(string name)
    {
        Assert.Throws<ArgumentException>(() => new ProfileReset(Data, [new(name, ResetTargetKind.Folder)]));
    }

    [Fact]
    public void DuplicateTargetsAndARelativeDataFolderAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ProfileReset(Data, [new("x", ResetTargetKind.File), new("x", ResetTargetKind.Folder)]));
        Assert.Throws<ArgumentException>(() => new ProfileReset("relative/data"));
    }
}
