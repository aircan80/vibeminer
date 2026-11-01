using System.Text;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The browse session's layer 4: the names switch from settings-v1.json and the shared files cache with its leases.</summary>
public sealed class FilesSessionTests : IDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly BuildRef A = new("wow", new string('a', 32), new string('1', 32), "us");
    private static readonly BuildRef B = new("wow", new string('b', 32), new string('2', 32), "us");

    private readonly string data = Directory.CreateTempSubdirectory("vm-files-session-").FullName;
    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new();
    private readonly CountingFileSystem files;
    private readonly BrowseSession session;
    private int made;
    private IFileNameSwitch? givenSwitch;

    public FilesSessionTests()
    {
        memory.Add(A, ["enUS", "deDE"], [new FakeFile(1, Locales: ["enUS"]), new FakeFile(2, Locales: ["deDE"])]);
        memory.Add(B, ["enUS"], [new FakeFile(3)]);
        files = new CountingFileSystem(memory);
        session = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName))
        {
            FileSystem = (_, _, names) => { Interlocked.Increment(ref made); givenSwitch = names; return files; },
        };
    }

    public void Dispose() => Directory.Delete(data, recursive: true);

    private string SettingsPath => Path.Combine(data, SettingsNameSwitch.FileName);
    private Task<bool> SwitchAsync() => new SettingsNameSwitch(data).NamesEnabledAsync(default);

    // ---------------------------------------------------------------- the names switch

    [Fact]
    public async Task NoSettingsFileIsTheDefaultOn() => Assert.True(await SwitchAsync());

    [Theory]
    [InlineData("""{"version":1,"changedAt":"2026-10-31T12:00:00.0000000+00:00","values":{"communityListfile":true}}""", true)]
    [InlineData("""{"version":1,"values":{"communityBuildHistory":false,"communityListfile":false}}""", false)]
    [InlineData("""{"version":1,"values":{"communityBuildHistory":false}}""", true)]
    [InlineData("""{"version":1}""", true)]
    [InlineData("""{"version":1,"values":{"communityListfile":"yes"}}""", false)]
    [InlineData("""{"version":1,"values":{"communityListfile":true,"communityListfile":true}}""", false)]
    [InlineData("""{"version":1,"values":[]}""", false)]
    [InlineData("""{"version":1,"values":{"communityListfile":false},"values":{}}""", false)]
    [InlineData("""{"version":1,"version":1,"values":{"communityListfile":true}}""", false)]
    [InlineData("""{"version":2,"values":{"communityListfile":true}}""", false)]
    [InlineData("""{"values":{"communityListfile":true}}""", false)]
    [InlineData("""{"version":"1","values":{"communityListfile":true}}""", false)]
    [InlineData("""{"version":1,"values":{"communityListfile":true}""", false)]
    [InlineData("""[]""", false)]
    [InlineData("", false)]
    public async Task TheSwitchReadsTheSavedValueAndFailsClosed(string json, bool expected)
    {
        await File.WriteAllTextAsync(SettingsPath, json);
        Assert.Equal(expected, await SwitchAsync());
    }

    [Fact]
    public async Task InvalidUtf8ReadsAsOff()
    {
        await File.WriteAllBytesAsync(SettingsPath, [.. Encoding.UTF8.GetBytes("{\"version\":1,\"values\":{\"x"), 0xFF, .. Encoding.UTF8.GetBytes("\":1,\"communityListfile\":true}}")]);
        Assert.False(await SwitchAsync());
    }

    [Fact]
    public async Task AnOversizeFileIsNotReadAndReadsAsOff()
    {
        var padding = new string(' ', SettingsNameSwitch.MaxFileBytes);
        await File.WriteAllTextAsync(SettingsPath, """{"version":1,"values":{"communityListfile":true}}""" + padding);
        Assert.False(await SwitchAsync());
        await File.WriteAllTextAsync(SettingsPath, """{"version":1,"values":{"communityListfile":true}}""");
        Assert.True(await SwitchAsync());
    }

    [Fact]
    public async Task AFolderInPlaceOfTheFileReadsAsOff()
    {
        Directory.CreateDirectory(SettingsPath);
        Assert.False(await SwitchAsync());
    }

    [Fact]
    public async Task TheSessionSwitchReadsTheDataFolderAndIsGivenToTheFileSystem()
    {
        await using var lease = await session.OpenFilesAsync(new(A, "enUS"), default);
        Assert.Same(session.NameSwitch, givenSwitch);
        Assert.Equal(SettingsPath, Assert.IsType<SettingsNameSwitch>(session.NameSwitch).FilePath);
        await File.WriteAllTextAsync(SettingsPath, """{"version":1,"values":{"communityListfile":false}}""");
        Assert.False(await session.NameSwitch.NamesEnabledAsync(default));
    }

    [Fact]
    public async Task TheRealFileSystemSeamNeverUpdatesNamesAndAFailedOpenComesThrough()
    {
        await using var real = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName));
        var asked = new CountingSwitch(false);
        real.NameSwitch = asked;
        await using (var lease = await real.OpenFilesAsync(new(A, "enUS"), default))
        {
            // Nothing is stored: the read-only source can't fetch the build config.
            Assert.Equal(FilesOpenState.NeedsNetwork, lease.Result.State);
            Assert.NotNull(lease.Result.Code);
            Assert.Null(lease.Files);
            Assert.Equal(FileNamesState.Off, (await lease.Names.StatusAsync(default)).State);
            Assert.True(asked.Calls > 0);
        }
        Assert.Empty(stores.Source.Observed);
    }

    // ---------------------------------------------------------------- the files cache

    [Fact]
    public async Task OneStoreAndFileSystemServeEveryOpenAndAnOpenTargetIsReused()
    {
        await using (var first = await session.OpenFilesAsync(new(A, "enUS"), default))
        {
            Assert.Equal(FilesOpenState.Open, first.Result.State);
            Assert.NotNull(first.Files);
            await using var second = await session.OpenFilesAsync(new(A, "enUS"), default);
            Assert.Same(first.Files, second.Files);
            Assert.Equal(new FilesTarget(A, "enUS"), second.Target);
        }
        await using (await session.OpenFilesAsync(new(A, "enUS"), default)) Assert.Equal(1, files.Opens);
        await using (await session.OpenFilesAsync(new(A, "deDE"), default)) { }
        await using (await session.OpenFilesAsync(new(B, "enUS"), default)) { }
        Assert.Equal(3, files.Opens);
        Assert.Equal(1, made);
        Assert.Equal([(session.StoreFolder, false)], stores.Opened);
        Assert.Equal(1, stores.Open);
    }

    [Fact]
    public async Task AReplacedHandleIsDisposedOnlyAfterItsLastLease()
    {
        var first = await session.OpenFilesAsync(new(A, "enUS"), default);
        var second = await session.OpenFilesAsync(new(A, "enUS"), default);
        var old = first.Files!;
        await using (var other = await session.OpenFilesAsync(new(B, "enUS"), default))
        {
            Assert.NotSame(old, other.Files);
            Assert.Equal(0, files.Disposed(old));
            await first.DisposeAsync();
            await first.DisposeAsync();
            Assert.Equal(0, files.Disposed(old));
            Assert.Equal(1, await (old.ListAsync(FileFilter.All, default)).CountAsync());
            await second.DisposeAsync();
            Assert.Equal(1, files.Disposed(old));
            Assert.Equal(0, files.Disposed(other.Files!));
        }
        // The current one stays cached with no lease.
        Assert.Equal(1, files.DisposedCount);
        await using (await session.OpenFilesAsync(new(B, "enUS"), default)) Assert.Equal(2, files.Opens);
    }

    [Fact]
    public async Task AFailedOpenComesThroughAndIsNotKept()
    {
        memory.Fail(B, FilesOpenState.Corrupt, "BUILD_ENCODING_CORRUPT");
        await using var current = await session.OpenFilesAsync(new(A, "enUS"), default);
        await using (var failed = await session.OpenFilesAsync(new(B, "enUS"), default))
        {
            Assert.Equal(FilesOpenState.Corrupt, failed.Result.State);
            Assert.Equal("BUILD_ENCODING_CORRUPT", failed.Result.Code);
            Assert.Null(failed.Files);
        }
        await using (var missing = await session.OpenFilesAsync(new(B, "frFR"), default))
            Assert.Equal(FilesOpenState.Corrupt, missing.Result.State);
        await using (var locale = await session.OpenFilesAsync(new(A, "frFR"), default))
            Assert.Equal((FilesOpenState.LocaleNotInBuild, "FILES_LOCALE_NOT_IN_BUILD"), (locale.Result.State, locale.Result.Code));
        Assert.Equal(4, files.Opens);
        // A failure didn't replace the open one, and asking again for the failed build tries again.
        await using (var still = await session.OpenFilesAsync(new(A, "enUS"), default)) Assert.Same(current.Files, still.Files);
        await using (await session.OpenFilesAsync(new(B, "enUS"), default)) { }
        Assert.Equal(5, files.Opens);
        Assert.Equal(0, files.DisposedCount);
    }

    [Fact]
    public async Task AMissingStoreIsTheNamedFailureAndNothingIsKept()
    {
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        var e = await Assert.ThrowsAsync<ObjectStoreException>(() => session.OpenFilesAsync(new(A, "enUS"), default));
        Assert.Equal(ObjectStoreCodes.Missing, e.Code);
        Assert.StartsWith("OBJECT_STORE_MISSING", BrowseSession.Explain(e));
        Assert.Equal(0, made);
        stores.ReadOnlyFailure = null;
        await using var lease = await session.OpenFilesAsync(new(A, "enUS"), default);
        Assert.Equal(FilesOpenState.Open, lease.Result.State);
        Assert.Equal(1, stores.Open);
    }

    [Fact]
    public async Task DisposeClosesTheHandleTheFileSystemAndTheStore()
    {
        var closed = new List<bool>();
        stores.Disposing = writer => closed.Add(writer);
        var lease = await session.OpenFilesAsync(new(A, "enUS"), default);
        var handle = lease.Files!;
        await lease.DisposeAsync();
        Assert.Equal(1, stores.Open);
        await session.DisposeAsync();
        Assert.Equal(1, files.Disposed(handle));
        Assert.Equal(1, files.SelfDisposed);
        Assert.Equal([false], closed);
        Assert.Equal(0, stores.Open);
        await session.DisposeAsync();
        Assert.Equal(1, files.SelfDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenFilesAsync(new(A, "enUS"), default));
    }

    [Fact]
    public async Task DisposeWithALeaseStillHeldClosesWhenItEnds()
    {
        var lease = await session.OpenFilesAsync(new(A, "enUS"), default);
        await session.DisposeAsync();
        Assert.Equal(1, stores.Open);
        Assert.Equal(1, await lease.Files!.ListAsync(FileFilter.All, default).CountAsync());
        await lease.DisposeAsync();
        Assert.Equal(1, files.Disposed(lease.Files!));
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task InvalidateReopensEverythingAndLeasesKeepTheirs()
    {
        var lease = await session.OpenFilesAsync(new(A, "enUS"), default);
        await session.InvalidateFilesAsync();
        Assert.Equal(1, stores.Open);
        Assert.Equal(0, files.Disposed(lease.Files!));
        await using (var fresh = await session.OpenFilesAsync(new(A, "enUS"), default))
        {
            Assert.NotSame(lease.Files, fresh.Files);
            Assert.Equal(2, made);
            Assert.Equal(2, stores.Open);
        }
        await lease.DisposeAsync();
        Assert.Equal(1, files.Disposed(lease.Files!));
        Assert.Equal(1, stores.Open);
        await session.DisposeAsync();
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ACanceledOpenWaitingOnTheStoreLeavesNothingAndTheNextOpenWorks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stores.BeforeOpen = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cancel = new CancellationTokenSource();
        var stale = session.OpenFilesAsync(new(A, "enUS"), cancel.Token);
        await entered.Task.WaitAsync(Guard);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(Guard));
        stores.BeforeOpen = null;
        await using var lease = await session.OpenFilesAsync(new(B, "enUS"), default).WaitAsync(Guard);
        Assert.Equal(FilesOpenState.Open, lease.Result.State);
        Assert.Equal(1, stores.Open);
    }

    [Fact]
    public async Task OpensFromManyThreadsQueueOnOneStoreAndEveryHandleOutlivesItsReplacement()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeOpen = _ => release.Task;
        var opens = Enumerable.Range(0, 8).Select(i => Task.Run(() => session.OpenFilesAsync(new(i % 2 == 0 ? A : B, "enUS"), default))).ToArray();
        await Task.Delay(50);
        release.SetResult();
        var leases = await Task.WhenAll(opens).WaitAsync(Guard);
        Assert.All(leases, l => Assert.Equal(FilesOpenState.Open, l.Result.State));
        Assert.Equal(1, made);
        Assert.Equal(1, stores.Open);
        // Every handle any lease got is still usable until its leases go.
        foreach (var l in leases) Assert.Equal(1, await l.Files!.ListAsync(FileFilter.All, default).CountAsync());
        Assert.Equal(0, files.DisposedCount);
        await Task.WhenAll(leases.Select(l => Task.Run(async () => await l.DisposeAsync())));
        // Only the cached one survives.
        Assert.Equal(files.Opens - 1, files.DisposedCount);
        await session.DisposeAsync();
        Assert.Equal(files.Opens, files.DisposedCount);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ACanceledOpenWhileTheBuildOpensLeavesNoHoldAndTheNextOpenWorks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeOpen = async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cancel = new CancellationTokenSource();
        var stale = session.OpenFilesAsync(new(A, "enUS"), cancel.Token);
        await entered.Task.WaitAsync(Guard);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(Guard));
        files.BeforeOpen = null;
        await using (var lease = await session.OpenFilesAsync(new(A, "enUS"), default).WaitAsync(Guard))
            Assert.Equal(FilesOpenState.Open, lease.Result.State);
        Assert.Equal(1, made);
        await session.DisposeAsync();
        Assert.Equal(0, stores.Open);
        Assert.Equal(1, files.DisposedCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeOrInvalidateDuringAnOpenGivesALeaseThatClosesEverythingWhenItEnds(bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeOpen = _ => { entered.TrySetResult(); return release.Task; };
        var opening = session.OpenFilesAsync(new(A, "enUS"), default);
        await entered.Task.WaitAsync(Guard);
        if (dispose) await session.DisposeAsync(); else await session.InvalidateFilesAsync();
        // The open still holds the store.
        Assert.Equal(1, stores.Open);
        files.BeforeOpen = null;
        release.SetResult();
        var lease = await opening.WaitAsync(Guard);
        Assert.Equal(1, await lease.Files!.ListAsync(FileFilter.All, default).CountAsync());
        await lease.DisposeAsync();
        // Not kept: the cache had dropped that store.
        Assert.Equal(1, files.Disposed(lease.Files!));
        Assert.Equal(0, stores.Open);
        if (dispose)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.OpenFilesAsync(new(A, "enUS"), default));
            return;
        }
        await using var fresh = await session.OpenFilesAsync(new(A, "enUS"), default);
        Assert.NotSame(lease.Files, fresh.Files);
        Assert.Equal(2, made);
    }

    [Fact]
    public async Task AFailedCloseOfTheReplacedHandleStillReturnsTheNewLease()
    {
        var first = await session.OpenFilesAsync(new(A, "enUS"), default);
        await first.DisposeAsync();
        files.DisposeThrows = new IOException("scripted close failure");
        var next = await session.OpenFilesAsync(new(B, "enUS"), default).WaitAsync(Guard);
        Assert.Equal(FilesOpenState.Open, next.Result.State);
        Assert.Equal(1, files.Disposed(first.Files!));
        files.DisposeThrows = null;
        await next.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(1, files.Disposed(next.Files!));
        Assert.Equal(0, stores.Open);
    }

    /// <summary>Wraps the in-memory file system: counts opens and each handle's disposals, and can hold opens.</summary>
    private sealed class CountingFileSystem(IFileSystem inner) : IFileSystem, IAsyncDisposable
    {
        private readonly Dictionary<IBuildFiles, int> disposed = new(ReferenceEqualityComparer.Instance);
        private int opens, self;
        /// <summary>When set, disposing a handle counts it, then throws this.</summary>
        public Exception? DisposeThrows { get; set; }
        public Func<CancellationToken, Task>? BeforeOpen { get; set; }
        public int Opens => Volatile.Read(ref opens);
        public int SelfDisposed => Volatile.Read(ref self);
        public int DisposedCount { get { lock (disposed) return disposed.Values.Count(v => v > 0); } }
        public int Disposed(IBuildFiles handle) { lock (disposed) return disposed.GetValueOrDefault(handle); }
        public IFileNames Names => inner.Names;

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref opens);
            if (BeforeOpen is { } wait) await wait(cancellationToken);
            var result = await inner.ForAsync(build, locale, cancellationToken);
            return result.Files is { } f ? result with { Files = new Counted(this, f) } : result;
        }

        public ValueTask DisposeAsync() { Interlocked.Increment(ref self); return ValueTask.CompletedTask; }

        private void Count(IBuildFiles handle) { lock (disposed) disposed[handle] = disposed.GetValueOrDefault(handle) + 1; }

        private sealed class Counted(CountingFileSystem owner, IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public async ValueTask DisposeAsync()
            {
                owner.Count(this);
                await inner.DisposeAsync();
                if (owner.DisposeThrows is { } e) throw e;
            }
            public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
            public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) => inner.ReadRangeAsync(file, offset, length, cancellationToken);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
                inner.EnsureAsync(selection, priority, progress, cancellationToken);
        }
    }

    private sealed class CountingSwitch(bool on) : IFileNameSwitch
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref calls); return Task.FromResult(on); }
    }
}
