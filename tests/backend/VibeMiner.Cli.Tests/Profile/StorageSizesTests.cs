using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using VibeMiner.Cdn;
using VibeMiner.Cli.Profile;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests.Profile;

public sealed class StorageSizesTests : IDisposable
{
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private readonly string data = Path.Combine(Path.GetTempPath(), "vibeminer-sizes-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStores stores = new();

    public StorageSizesTests() => Directory.CreateDirectory(data);

    public void Dispose() { try { Directory.Delete(data, recursive: true); } catch (IOException) { } }

    private string Generated => Path.Combine(data, StorageSizes.GeneratedFolder);

    private void WriteFile(string relative, int length)
    {
        var path = Path.Combine(Generated, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    [Fact]
    public async Task SizesAddUpAcrossTheStoreTheGeneratedFolderAndEachBuild()
    {
        WriteFile("a.bin", 10);
        WriteFile("tables/b.bin", 32);
        WriteFile("tables/deep/c.bin", 100);
        var (build, keys) = await OpenBuildAsync(A, [4, 6, 10]);
        stores.Source.AddStored(keys[0], new byte[4]).AddStored(keys[2], new byte[10]);
        await using var figures = new DownloadedFigures(stores.Source);

        var report = await StorageSizes.MeasureAsync(data, stores, [build], figures, default);

        Assert.Equal(data, report.DataFolder);
        Assert.Equal(SizeState.Measured, report.ObjectStore.State);
        Assert.Equal(stores.Stats, report.ObjectStore.Stats);
        Assert.Equal([(Path.Combine(data, ObjectStore.DefaultFolderName), false)], stores.Opened);
        Assert.Equal(0, stores.Open);
        Assert.Equal(new GeneratedSize(SizeState.Measured, 142, 3, null, null), report.Generated);
        var only = Assert.Single(report.Builds);
        Assert.Equal(build.Build, only.Build);
        Assert.Equal(new DownloadedFigure(2, 3, 14, 20), only.Figure);
        Assert.Null(only.Code);
    }

    [Fact]
    public async Task AMissingGeneratedFolderIsAbsentAndAnEmptyOneIsZeroBytes()
    {
        var absent = await StorageSizes.GeneratedAsync(data, default);
        Assert.Equal(SizeState.Missing, absent.State);

        Directory.CreateDirectory(Generated);
        var empty = await StorageSizes.GeneratedAsync(data, default);
        Assert.Equal(new GeneratedSize(SizeState.Measured, 0, 0, null, null), empty);
    }

    [Fact]
    public async Task LinksInsideAreNotFollowedAndALinkedFolderIsNotMeasured()
    {
        var outside = Path.Combine(data, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[1000]);
        WriteFile("own.bin", 7);
        Directory.CreateSymbolicLink(Path.Combine(Generated, "dir-link"), outside);
        File.CreateSymbolicLink(Path.Combine(Generated, "file-link"), Path.Combine(outside, "big.bin"));

        Assert.Equal(new GeneratedSize(SizeState.Measured, 7, 1, null, null), await StorageSizes.GeneratedAsync(data, default));

        Directory.Delete(Generated, recursive: true);
        Directory.CreateSymbolicLink(Generated, outside);
        var linked = await StorageSizes.GeneratedAsync(data, default);
        Assert.Equal(SizeState.Failed, linked.State);
        Assert.Equal(StorageSizes.GeneratedNotAFolder, linked.Code);
        Assert.True(File.Exists(Path.Combine(outside, "big.bin")));
    }

    [Fact]
    public async Task AFileWhereTheGeneratedFolderBelongsIsReportedNotThrown()
    {
        File.WriteAllBytes(Generated, new byte[5]);
        var report = await StorageSizes.GeneratedAsync(data, default);
        Assert.Equal(SizeState.Failed, report.State);
        Assert.Equal(StorageSizes.GeneratedNotAFolder, report.Code);
    }

    [Fact]
    public async Task AMissingStoreIsMissingAndAnotherStoreFailureKeepsItsCode()
    {
        await using var figures = new DownloadedFigures(stores.Source);
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        var missing = await StorageSizes.MeasureAsync(data, stores, [], figures, default);
        Assert.Equal(SizeState.Missing, missing.ObjectStore.State);
        Assert.Null(missing.ObjectStore.Stats);
        Assert.Equal(SizeState.Missing, missing.Generated.State);
        Assert.Empty(missing.Builds);

        stores.ReadOnlyFailure = ObjectStoreCodes.IndexUnreadable;
        var failed = await StorageSizes.ObjectStoreAsync(Path.Combine(data, ObjectStore.DefaultFolderName), stores, default);
        Assert.Equal(new ObjectStoreSize(SizeState.Failed, null, ObjectStoreCodes.IndexUnreadable, "Scripted failure."), failed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task EachBuildGetsItsOwnFigureAndOneFailureDoesNotHideTheOthers()
    {
        var (first, firstKeys) = await OpenBuildAsync(A, [4, 6]);
        var (second, secondKeys) = await OpenBuildAsync(C, [8, 2, 5]);
        stores.Source.AddStored(firstKeys[1], new byte[6]).AddStored(secondKeys[0], new byte[8]).AddStored(secondKeys[1], new byte[2]);
        var unledgered = new NoLedgerBuild(new BuildRef("wow", B, B, "us"));
        await using var figures = new DownloadedFigures(stores.Source);

        var sizes = await StorageSizes.DownloadedAsync([first, unledgered, second], figures, default);

        Assert.Equal([first.Build, unledgered.Build, second.Build], sizes.Select(s => s.Build));
        Assert.Equal(new DownloadedFigure(1, 2, 6, 10), sizes[0].Figure);
        Assert.Null(sizes[1].Figure);
        Assert.Equal(StorageSizes.DownloadedFailed, sizes[1].Code);
        Assert.Equal(new DownloadedFigure(2, 3, 10, 15), sizes[2].Figure);
    }

    [Fact]
    public async Task CancelingThrowsInsteadOfReportingAFailure()
    {
        var (build, _) = await OpenBuildAsync(A, [4]);
        await using var figures = new DownloadedFigures(stores.Source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageSizes.DownloadedAsync([build], figures, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StorageSizes.GeneratedAsync(data, cts.Token));
    }

    /// <summary>A fake build whose ledger is one distinct EKey per size; the keys start with <paramref name="config"/>'s first byte.</summary>
    private static async Task<(IBuild Build, ObjectKey[] Keys)> OpenBuildAsync(string config, int[] sizes)
    {
        var files = new List<FakeFile>();
        var keys = new List<ObjectKey>();
        for (var i = 0; i < sizes.Length; i++)
        {
            var eKey = new byte[16]; eKey[0] = (byte)config[0]; eKey[1] = (byte)(i + 1);
            var cKey = new byte[16]; cKey[0] = 0x80; cKey[1] = (byte)(i + 1);
            files.Add(new((uint)(i + 1), null, [new FileVariant(0x2, 0, new CdnKey(cKey), sizes[i], new CdnKey(eKey), sizes[i], null)]));
            keys.Add(new ObjectKey(ObjectKeyType.Encoded, eKey));
        }
        var metadata = new BuildMetadata("12.0.1.60000", default, default, default, 0, files.Count, ["enUS"]);
        var build = new BuildRef("wow", config, config, "eu");
        var opened = await new InMemoryBuilds().Add(build, new FakeBuild(metadata, files)).OpenAsync(build, default);
        return (opened.Opened!, keys.ToArray());
    }

    /// <summary>A build layer 3 can't measure (not opened by an opener, so no ledger).</summary>
    private sealed class NoLedgerBuild(BuildRef build) : IBuild
    {
        public BuildRef Build => build;
        public BuildMetadata Metadata { get; } = new("12.0.1.60000", default, default, default, 0, 0, ["enUS"]);
        public CdnContext? Cdn => null;
        public FileResolution Resolve(uint fileDataId) => throw new NotSupportedException();
        public FileResolution Resolve(string path) => throw new NotSupportedException();
        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => [];
    }
}
