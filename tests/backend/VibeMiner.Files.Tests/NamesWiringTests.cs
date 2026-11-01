using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using Xunit;

namespace VibeMiner.Files.Tests;

/// <summary>
/// The real <see cref="FileSystem"/>'s names wiring over the layer 3 and layer 2 fakes: handles use the loaded copy only,
/// never fetch, and pick up a newer copy between listing batches. Authored names only.
/// </summary>
public sealed class NamesWiringTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static (RealFiles Real, RealNames Names) Create(int count, int batch = 4096)
    {
        var names = new RealNames();
        var files = Enumerable.Range(1, count).Select(i => new FakeFile((uint)i, Size: i)).ToArray();
        return (FilesFixture.Real(FilesFixture.Build, ["enUS"], files, new FileSystemOptions { PresenceBatch = batch, Names = names }), names);
    }

    private static async Task<IBuildFiles> OpenAsync(RealFiles real) => (await real.Files.ForAsync(FilesFixture.Build, "enUS", Ct)).Files!;

    private static Dictionary<uint, string> Named(int count, string dir) =>
        Enumerable.Range(1, count).ToDictionary(i => (uint)i, i => $"{dir}/f{i}.blp");

    [Fact]
    public async Task NamesAreOffWithoutASwitch()
    {
        var real = FilesFixture.Real(FilesFixture.Build, ["enUS"], [new FakeFile(1, Size: 1)]);
        Assert.Equal(FileNamesState.Off, (await real.Files.Names.StatusAsync(Ct)).State);
        Assert.Equal(FileNamesState.Off, (await real.Files.Names.UpdateAsync(true, Ct)).State);
        Assert.Empty(real.Objects.Observed);
        await using var files = await OpenAsync(real);
        Assert.Null((await files.StatAsync(1, Ct)).File!.Path);
    }

    [Fact]
    public async Task HandlesNeverFetchNames()
    {
        var (real, names) = Create(6);
        await using var files = await OpenAsync(real);
        await names.UseAsync(real.Files, real.Objects, Named(4, "a"));
        var observed = real.Objects.Observed.Count;

        var listed = new List<BuildFile>();
        await foreach (var f in files.ListAsync(FileFilter.All with { PathPrefix = "A/" }, Ct)) listed.Add(f);
        Assert.Equal((uint[])[1u, 2, 3, 4], listed.Select(f => f.FileDataId).ToArray());
        Assert.Equal("a/f3.blp", (await files.StatAsync(3, Ct)).File!.Path);
        Assert.True(await files.ExistsAsync(FileRef.ForPath("a/f2.blp"), Ct));
        Assert.Equal(FileReadState.NeedsNetwork, (await files.GetAsync(FileRef.ForPath("a/f1.blp"), Ct)).State);

        Assert.Equal(observed, real.Objects.Observed.Count);
    }

    [Fact]
    public async Task ExistsAsksLayer2ForNoObjects()
    {
        var (real, names) = Create(3);
        await names.UseAsync(real.Files, real.Objects, Named(3, "a"));
        await using var files = await OpenAsync(real);
        // The first read loads nothing more: the copy is already in memory.
        _ = await files.ExistsAsync(FileRef.ForPath("a/f1.blp"), Ct);
        var (asked, hasKeys) = (real.Objects.Asked.Count, real.Objects.HasKeysCalls);
        Assert.True(await files.ExistsAsync(FileRef.ForPath("A\\F2.BLP"), Ct));
        Assert.True(await files.ExistsAsync(2, Ct));
        Assert.False(await files.ExistsAsync(FileRef.ForPath("a/f9.blp"), Ct));
        Assert.Equal((asked, hasKeys), (real.Objects.Asked.Count, real.Objects.HasKeysCalls));
    }

    [Fact]
    public async Task NamesTurnedOnAfterTheHandleOpenedApplyOnTheNextCall()
    {
        var (real, names) = Create(2);
        await using var files = await OpenAsync(real);
        Assert.Null((await files.StatAsync(1, Ct)).File!.Path);
        await names.UseAsync(real.Files, real.Objects, Named(2, "a"));
        Assert.Equal("a/f1.blp", (await files.StatAsync(1, Ct)).File!.Path);
    }

    [Fact]
    public async Task ANewerCopyLoadedMidListingNamesTheNextBatches()
    {
        var (real, names) = Create(6, batch: 2);
        await names.UseAsync(real.Files, real.Objects, Named(6, "old"));
        await using var files = await OpenAsync(real);

        var paths = new List<string?>();
        await foreach (var f in files.ListAsync(FileFilter.All, Ct))
        {
            paths.Add(f.Path);
            // Files 1 and 2 are one batch, already named; the next batch reads the names again.
            if (f.FileDataId == 1) await names.UseAsync(real.Files, real.Objects, new Dictionary<uint, string> { [2] = "new/f2.blp", [5] = "new/f5.blp" });
        }

        Assert.Equal(["old/f1.blp", "old/f2.blp", null, null, "new/f5.blp", null], paths);
    }

    [Fact]
    public async Task APrefixListingStopsWhenNamesGoOff()
    {
        var (real, names) = Create(6, batch: 2);
        await names.UseAsync(real.Files, real.Objects, Named(6, "a"));
        await using var files = await OpenAsync(real);

        var ids = new List<uint>();
        await foreach (var f in files.ListAsync(FileFilter.All with { PathPrefix = "a/" }, Ct))
        {
            ids.Add(f.FileDataId);
            names.On = false;
        }

        Assert.Equal((uint[])[1u, 2], ids.ToArray());
    }

    [Fact]
    public async Task APrefixListingRestartsItsNameWalkPastTheIdsItSkipped()
    {
        var (real, names) = Create(10, batch: 2);
        await names.UseAsync(real.Files, real.Objects, new Dictionary<uint, string>
        {
            [1] = "a/1", [2] = "a/2", [3] = "b/3", [4] = "b/4", [5] = "b/5", [6] = "b/6", [7] = "a/7", [8] = "a/8", [9] = "a/9",
        });
        await using var files = await OpenAsync(real);

        var ids = new List<uint>();
        await foreach (var f in files.ListAsync(FileFilter.All with { PathPrefix = "a/" }, Ct))
        {
            ids.Add(f.FileDataId);
            // Batch 2 restarts at 3 and walks 3..5 (4 skipped); batch 3 restarts at 6, never back at the skipped 4.
            if (f.FileDataId == 1)
                await names.UseAsync(real.Files, real.Objects, new Dictionary<uint, string> { [3] = "a/3", [5] = "A/5", [7] = "b/7", [9] = "a/9" });
            if (f.FileDataId == 3)
                await names.UseAsync(real.Files, real.Objects, new Dictionary<uint, string> { [4] = "a/4", [6] = "a/6", [8] = "a\\8" });
        }

        Assert.Equal((uint[])[1u, 2, 3, 5, 6, 8], ids.ToArray());
    }
}
