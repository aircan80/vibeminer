using VibeMiner.Builds;
using VibeMiner.Files.Testing;
using Xunit;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// One behaviour suite for <see cref="IFileSystem"/>: the real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes,
/// and the in-memory fake layers 5 and up test against. Each side arranges the same synthetic build its own way.
/// </summary>
public abstract class FilesContractTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>A file system holding <paramref name="build"/> with exactly these locales and files.</summary>
    protected abstract IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files);
    /// <summary>A file system for which opening <paramref name="build"/> fails with a layer 3 state and code.</summary>
    protected abstract IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code);
    /// <summary>Makes one file's encoded copy stored, as a download would, on the file system <see cref="Create"/> returned last.</summary>
    protected abstract void MarkStored(BuildRef build, uint fileDataId);

    private static readonly IReadOnlyList<string> Locales = ["enUS", "deDE"];

    private static readonly string KeyOf300 = "0123456789ABCDEF0123456789ABCDEF";

    /// <summary>
    /// 100 every locale, stored; 150 deDE only; 200 enUS only, encrypted; 250 every locale, not in ENCODING; 300 every locale,
    /// stored and encrypted, with a given content key; 400 and 500 every locale, not stored.
    /// </summary>
    private static IReadOnlyList<FakeFile> Files() =>
    [
        new(100, "Interface/Icons/One.blp", 1000, FileStorage.Stored),
        new(150, "world/de/two.m2", 1500, Locales: ["deDE"]),
        new(200, "world/en/three.m2", 2000, Encrypted: true, Locales: ["enUS"]),
        new(250, "sound/missing.ogg", 2500, NotInEncoding: true),
        new(300, "sound/four.ogg", 3000, FileStorage.Stored, Encrypted: true, ContentKey: KeyOf300),
        new(400, "sound/five.ogg", 4000),
        new(500, "sound/six.ogg", 5000),
    ];

    private static readonly uint[] EnUsIds = [100, 200, 250, 300, 400, 500];

    private async Task<IBuildFiles> OpenAsync(string locale = "enUS", IReadOnlyList<FakeFile>? files = null)
    {
        var result = await Create(FilesFixture.Build, Locales, files ?? Files()).ForAsync(FilesFixture.Build, locale, Ct);
        Assert.Equal(FilesOpenState.Open, result.State);
        return result.Files!;
    }

    private static async Task<List<BuildFile>> ListAsync(IBuildFiles files, FileFilter filter, CancellationToken? ct = null)
    {
        var list = new List<BuildFile>();
        await foreach (var f in files.ListAsync(filter, ct ?? Ct)) list.Add(f);
        return list;
    }

    private static async Task<uint[]> IdsAsync(IBuildFiles files, FileFilter filter) =>
        [.. (await ListAsync(files, filter)).Select(f => f.FileDataId)];

    // ForAsync

    [Fact]
    public async Task OpensWithBuildAndLocale()
    {
        var result = await Create(FilesFixture.Build, Locales, Files()).ForAsync(FilesFixture.Build, "deDE", Ct);
        Assert.Equal((FilesOpenState.Open, FilesFixture.Build, "deDE"), (result.State, result.Build, result.Locale));
        Assert.Equal((null, null), (result.Code, result.Message));
        await using var files = result.Files!;
        Assert.Equal((FilesFixture.Build, "deDE"), (files.Build, files.Locale));
    }

    [Fact]
    public async Task UnregisteredLocaleThrows()
    {
        var fs = Create(FilesFixture.Build, Locales, Files());
        await Assert.ThrowsAsync<ArgumentException>(() => fs.ForAsync(FilesFixture.Build, "xxXX", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => fs.ForAsync(FilesFixture.Build, "enus", Ct));
    }

    [Fact]
    public async Task LocaleTheBuildDoesNotHave()
    {
        var result = await Create(FilesFixture.Build, Locales, Files()).ForAsync(FilesFixture.Build, "frFR", Ct);
        Assert.Equal((FilesOpenState.LocaleNotInBuild, FilesFixture.Build, "frFR"), (result.State, result.Build, result.Locale));
        Assert.Null(result.Files);
    }

    [Fact]
    public async Task UnknownBuildNeedsNetwork()
    {
        var result = await Create(FilesFixture.Build, Locales, Files()).ForAsync(FilesFixture.Other, "enUS", Ct);
        Assert.Equal((FilesOpenState.NeedsNetwork, FilesFixture.Other, "enUS"), (result.State, result.Build, result.Locale));
        Assert.Null(result.Files);
        Assert.NotNull(result.Code);
    }

    [Fact]
    public async Task FailedOpenKeepsStateAndCode()
    {
        var result = await CreateFailing(FilesFixture.Build, FilesOpenState.Corrupt, "BUILD_ROOT_CORRUPT").ForAsync(FilesFixture.Build, "enUS", Ct);
        Assert.Equal((FilesOpenState.Corrupt, "BUILD_ROOT_CORRUPT", FilesFixture.Build, "enUS"), (result.State, result.Code, result.Build, result.Locale));
        Assert.Null(result.Files);
    }

    // ListAsync

    [Fact]
    public async Task ListsThisLocaleAscending()
    {
        // Added out of order: the listing still ascends.
        var shuffled = Files().Reverse().ToArray();
        await using (var en = await OpenAsync("enUS", shuffled))
            Assert.Equal(EnUsIds, await IdsAsync(en, FileFilter.All));
        await using var de = await OpenAsync("deDE", shuffled);
        Assert.Equal((uint[])[100u, 150, 250, 300, 400, 500], await IdsAsync(de, FileFilter.All));
    }

    [Fact]
    public async Task ListsInclusiveRange()
    {
        await using var files = await OpenAsync();
        Assert.Equal((uint[])[200u, 250, 300], await IdsAsync(files, FileFilter.All with { FromId = 200, ToId = 300 }));
        Assert.Equal((uint[])[250u, 300], await IdsAsync(files, FileFilter.All with { FromId = 201, ToId = 399 }));
        Assert.Equal((uint[])[300u], await IdsAsync(files, FileFilter.All with { FromId = 300, ToId = 300 }));
        Assert.Equal((uint[])[500u], await IdsAsync(files, FileFilter.All with { FromId = 500 }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { FromId = 501 }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { FromId = uint.MaxValue }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { FromId = 400, ToId = 300 }));
    }

    [Fact]
    public async Task PagesWithoutGapsOrRepeats()
    {
        await using var files = await OpenAsync();
        var all = await ListAsync(files, FileFilter.All);
        var paged = new List<BuildFile>();
        var from = 0u;
        while (true)
        {
            var page = new List<BuildFile>();
            await foreach (var f in files.ListAsync(FileFilter.All with { FromId = from }, Ct))
            {
                page.Add(f);
                if (page.Count == 3) break;
            }
            paged.AddRange(page);
            if (page.Count < 3) break;
            from = page[^1].FileDataId + 1;
        }
        Assert.Equal(all, paged);
        Assert.Equal(EnUsIds, paged.Select(f => f.FileDataId));
    }

    [Fact]
    public async Task FiltersByStorage()
    {
        await using var files = await OpenAsync();
        Assert.Equal((uint[])[100u, 300], await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.Stored } }));
        Assert.Equal((uint[])[200u, 400, 500], await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.NotStored } }));
        Assert.Equal((uint[])[250u], await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.NoEncodedCopy } }));
        Assert.Equal((uint[])[100u, 250, 300], await IdsAsync(files,
            FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.Stored, FileStorage.NoEncodedCopy } }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage>() }));
    }

    [Fact]
    public async Task FiltersByEncrypted()
    {
        await using var files = await OpenAsync();
        Assert.Equal((uint[])[200u, 300], await IdsAsync(files, FileFilter.All with { Encrypted = true }));
        Assert.Equal((uint[])[100u, 250, 400, 500], await IdsAsync(files, FileFilter.All with { Encrypted = false }));
    }

    [Fact]
    public async Task FiltersByFormat()
    {
        await using var files = await OpenAsync();
        Assert.Equal(EnUsIds, await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Unknown } }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat>() }));
    }

    [Fact]
    public async Task CombinesFilters()
    {
        await using var files = await OpenAsync();
        Assert.Equal((uint[])[300u], await IdsAsync(files, FileFilter.All with
        {
            FromId = 150, ToId = 450, Encrypted = true, Storage = new HashSet<FileStorage> { FileStorage.Stored },
        }));
    }

    [Fact]
    public async Task PathPrefixMatchesNothingWithoutNames()
    {
        await using var files = await OpenAsync();
        Assert.Empty(await IdsAsync(files, FileFilter.All with { PathPrefix = "sound/" }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { PathPrefix = "" }));
    }

    [Fact]
    public async Task ListingHonoursCancellation()
    {
        await using var files = await OpenAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ListAsync(files, FileFilter.All, cts.Token));
    }

    [Fact]
    public async Task ListsFields()
    {
        await using var files = await OpenAsync();
        var byId = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId);
        Assert.Equal(new BuildFile(100, null, 1000, FilesFixture.ContentKeyOf(100), FileFormat.Unknown, FileStorage.Stored, false), byId[100]);
        Assert.Equal(new BuildFile(200, null, 2000, FilesFixture.ContentKeyOf(200), FileFormat.Unknown, FileStorage.NotStored, true), byId[200]);
        Assert.Equal(new BuildFile(300, null, 3000, KeyOf300.ToLowerInvariant(), FileFormat.Unknown, FileStorage.Stored, true), byId[300]);
        Assert.Equal(new BuildFile(400, null, 4000, FilesFixture.ContentKeyOf(400), FileFormat.Unknown, FileStorage.NotStored, false), byId[400]);
        Assert.All(byId.Values, f => Assert.Matches("^[0-9a-f]{32}$", f.ContentKey));
    }

    [Fact]
    public async Task NotInEncodingListsWithNoEncodedCopyAndNoSize()
    {
        await using var files = await OpenAsync();
        var file = Assert.Single(await ListAsync(files, FileFilter.All with { FromId = 250, ToId = 250 }));
        Assert.Equal(new BuildFile(250, null, null, FilesFixture.ContentKeyOf(250), FileFormat.Unknown, FileStorage.NoEncodedCopy, false), file);
    }

    // StatAsync and ExistsAsync

    [Fact]
    public async Task StatsByIdAndPathMatchTheListing()
    {
        await using var files = await OpenAsync();
        var listed = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId);
        foreach (var (id, path) in new[] { (100u, "Interface/Icons/One.blp"), (200u, "world/en/three.m2"), (300u, "sound/four.ogg"), (400u, "sound/five.ogg") })
        {
            foreach (var file in new[] { FileRef.Id(id), FileRef.ForPath(path), FileRef.ForPath(path.ToUpperInvariant()),
                FileRef.ForPath(path.ToLowerInvariant().Replace('/', '\\')) })
            {
                Assert.Equal(new FileStat(FileLookupState.Found, listed[id]), await files.StatAsync(file, Ct));
                Assert.True(await files.ExistsAsync(file, Ct));
            }
        }
    }

    [Fact]
    public async Task StatsUnknownFile()
    {
        await using var files = await OpenAsync();
        foreach (var file in new[] { FileRef.Id(999), FileRef.Id(0), FileRef.ForPath("no/such/file.blp") })
        {
            Assert.Equal(new FileStat(FileLookupState.UnknownFile, null), await files.StatAsync(file, Ct));
            Assert.False(await files.ExistsAsync(file, Ct));
        }
    }

    [Fact]
    public async Task StatsNotInLocale()
    {
        await using var en = await OpenAsync("enUS");
        foreach (var file in new[] { FileRef.Id(150), FileRef.ForPath("WORLD\\DE\\TWO.M2") })
        {
            Assert.Equal(new FileStat(FileLookupState.NotInLocale, null), await en.StatAsync(file, Ct));
            Assert.False(await en.ExistsAsync(file, Ct));
        }
        await using var de = await OpenAsync("deDE");
        Assert.Equal(new FileStat(FileLookupState.NotInLocale, null), await de.StatAsync(200, Ct));
        Assert.Equal(FileLookupState.Found, (await de.StatAsync(150, Ct)).State);
    }

    [Fact]
    public async Task StatsNotInEncodingWithFile()
    {
        await using var files = await OpenAsync();
        var expected = new BuildFile(250, null, null, FilesFixture.ContentKeyOf(250), FileFormat.Unknown, FileStorage.NoEncodedCopy, false);
        foreach (var file in new[] { FileRef.Id(250), FileRef.ForPath("Sound\\Missing.OGG") })
        {
            Assert.Equal(new FileStat(FileLookupState.NotInEncoding, expected), await files.StatAsync(file, Ct));
            Assert.True(await files.ExistsAsync(file, Ct));
        }
    }

    [Fact]
    public async Task DefaultFileRefThrows()
    {
        await using var files = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => files.StatAsync(default, Ct));
    }

    [Fact]
    public async Task StoredStateIsNotCached()
    {
        await using var files = await OpenAsync();
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(400, Ct)).File!.Storage);
        Assert.Equal((uint[])[200u, 400, 500], await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.NotStored } }));
        MarkStored(FilesFixture.Build, 400);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(400, Ct)).File!.Storage);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(FileRef.ForPath("sound/five.ogg"), Ct)).File!.Storage);
        Assert.Equal((uint[])[200u, 500], await IdsAsync(files, FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.NotStored } }));
    }

    [Fact]
    public async Task DisposedHandleThrows()
    {
        var files = await OpenAsync();
        await files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ListAsync(files, FileFilter.All));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.StatAsync(100, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.StatAsync(FileRef.ForPath("sound/five.ogg"), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.ExistsAsync(100, Ct));
    }

    [Fact]
    public async Task ConcurrentStatsOnOneHandle()
    {
        await using var files = await OpenAsync();
        var listed = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId);
        uint[] asked = [100, 150, 200, 250, 300, 400, 500, 999];
        var stats = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => files.StatAsync(asked[i % asked.Length], Ct)));
        for (var i = 0; i < stats.Length; i++)
        {
            var id = asked[i % asked.Length];
            var expected = id switch
            {
                150 => new FileStat(FileLookupState.NotInLocale, null),
                999 => new FileStat(FileLookupState.UnknownFile, null),
                250 => new FileStat(FileLookupState.NotInEncoding, listed[id]),
                _ => new FileStat(FileLookupState.Found, listed[id]),
            };
            Assert.Equal(expected, stats[i]);
        }
    }
}

public sealed class InMemoryFilesContractTests : FilesContractTests
{
    private InMemoryFileSystem? last;

    protected override IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files) =>
        last = new InMemoryFileSystem(listNames: false).Add(build, locales, files);

    protected override IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code) =>
        new InMemoryFileSystem(listNames: false).Fail(build, state, code);

    protected override void MarkStored(BuildRef build, uint fileDataId) => last!.SetStorage(build, fileDataId, FileStorage.Stored);
}

public sealed class RealFilesContractTests : FilesContractTests
{
    private VibeMiner.ObjectAccess.Testing.InMemoryObjectSource? objects;

    protected override IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files)
    {
        var (fs, _, source) = FilesFixture.Real(build, locales, files);
        objects = source;
        return fs;
    }

    protected override IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code)
    {
        var builds = new L3.InMemoryBuilds().Fail(build, Enum.Parse<BuildOpenState>(state.ToString()), BuildPart.Root, code);
        return new FileSystem(builds, new VibeMiner.ObjectAccess.Testing.InMemoryObjectSource());
    }

    protected override void MarkStored(BuildRef build, uint fileDataId) => objects!.AddStored(FilesFixture.ObjectKeyOf(fileDataId), [1, 2, 3]);
}
