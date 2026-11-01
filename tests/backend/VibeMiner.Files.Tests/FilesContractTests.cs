using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
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

    /// <summary>A file system holding <paramref name="build"/> with exactly these locales and files, and this whole-read limit.</summary>
    protected abstract IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files, long maxReadBytes = 256L << 20,
        TimeProvider? time = null);
    /// <summary>A file system for which opening <paramref name="build"/> fails with a layer 3 state and code.</summary>
    protected abstract IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code);
    /// <summary>Makes one file's encoded copy stored, as a download would, on the file system <see cref="Create"/> returned last.</summary>
    protected abstract void MarkStored(BuildRef build, uint fileDataId);
    /// <summary>
    /// Called once a stream of the file is open: a side that can't script a failure on stream reads up front (the real one,
    /// whose open already reads layer 2) scripts it now.
    /// </summary>
    protected abstract void StreamOpened(uint fileDataId);
    /// <summary>
    /// On the file system <see cref="Create"/> returned last: turns the community names on with exactly these names (the
    /// listfile side, apart from <see cref="FakeFile.Path"/>, the ROOT name hash side), or off when null.
    /// </summary>
    protected abstract Task UseNamesAsync(IReadOnlyDictionary<uint, string>? names);
    /// <summary>On the file system <see cref="Create"/> returned last: how many times the file's encoded copy was fetched.</summary>
    protected abstract int FetchesOf(uint fileDataId);
    /// <summary>On the file system <see cref="Create"/> returned last: the priority of every request that asked to fetch the file.</summary>
    protected abstract IReadOnlyList<EnsurePriority> RequestsOf(uint fileDataId);
    /// <summary>On the file system <see cref="Create"/> returned last: holds every fetch of the file until released.</summary>
    protected abstract FetchHold Hold(uint fileDataId);

    /// <summary>A held fetch. <see cref="Seen"/> completes once a request at that priority asked for the file.</summary>
    protected sealed class FetchHold
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource interactive = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource background = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => release.Task;
        public void Release() => release.TrySetResult();
        public void Saw(EnsurePriority priority) => (priority == EnsurePriority.Interactive ? interactive : background).TrySetResult();
        public Task Seen(EnsurePriority priority) => (priority == EnsurePriority.Interactive ? interactive : background).Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static readonly IReadOnlyList<string> Locales = ["enUS", "deDE"];

    private static readonly byte[] Content100 = BlteBuilder.Bytes(1000, 100), Content300 = BlteBuilder.Bytes(3000, 300), Content400 = BlteBuilder.Bytes(4000, 400);
    private static readonly string KeyOf300 = FilesFixture.ContentKeyOf(Content300).ToUpperInvariant();

    /// <summary>
    /// 100 every locale, stored; 150 deDE only; 200 enUS only, encrypted; 250 every locale, not in ENCODING; 300 every locale,
    /// stored and encrypted, with a given (uppercase) content key; 400 and 500 every locale, not stored. 100, 300 and 400 have content.
    /// </summary>
    private static IReadOnlyList<FakeFile> Files() =>
    [
        new(100, "Interface/Icons/One.blp", Storage: FileStorage.Stored, Content: Content100),
        new(150, "world/de/two.m2", 1500, Locales: ["deDE"]),
        new(200, "world/en/three.m2", 2000, Encrypted: true, Locales: ["enUS"]),
        new(250, "sound/missing.ogg", 2500, NotInEncoding: true),
        new(300, "sound/four.ogg", Storage: FileStorage.Stored, Encrypted: true, ContentKey: KeyOf300, Content: Content300),
        new(400, "sound/five.ogg", Content: Content400),
        new(500, "sound/six.ogg", 5000),
    ];

    private static readonly uint[] EnUsIds = [100, 200, 250, 300, 400, 500];

    private async Task<IBuildFiles> OpenAsync(string locale = "enUS", IReadOnlyList<FakeFile>? files = null, long maxReadBytes = 256L << 20,
        TimeProvider? time = null)
    {
        var result = await Create(FilesFixture.Build, Locales, files ?? Files(), maxReadBytes, time).ForAsync(FilesFixture.Build, locale, Ct);
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
        Assert.Equal(new BuildFile(100, null, FilePathSource.None, 1000, FilesFixture.ContentKeyOf(Content100), FileFormat.Unknown, FileFormatSource.None, FileStorage.Stored, false), byId[100]);
        Assert.Equal(new BuildFile(200, null, FilePathSource.None, 2000, FilesFixture.ContentKeyOf(200), FileFormat.Unknown, FileFormatSource.None, FileStorage.NotStored, true), byId[200]);
        Assert.Equal(new BuildFile(300, null, FilePathSource.None, 3000, KeyOf300.ToLowerInvariant(), FileFormat.Unknown, FileFormatSource.None, FileStorage.Stored, true), byId[300]);
        Assert.Equal(new BuildFile(400, null, FilePathSource.None, 4000, FilesFixture.ContentKeyOf(Content400), FileFormat.Unknown, FileFormatSource.None, FileStorage.NotStored, false), byId[400]);
        Assert.All(byId.Values, f => Assert.Matches("^[0-9a-f]{32}$", f.ContentKey));
    }

    [Fact]
    public async Task NotInEncodingListsWithNoEncodedCopyAndNoSize()
    {
        await using var files = await OpenAsync();
        var file = Assert.Single(await ListAsync(files, FileFilter.All with { FromId = 250, ToId = 250 }));
        Assert.Equal(new BuildFile(250, null, FilePathSource.None, null, FilesFixture.ContentKeyOf(250), FileFormat.Unknown, FileFormatSource.None, FileStorage.NoEncodedCopy, false), file);
    }

    // Formats

    // Distinct content per file: the real side shares one stored copy between files of identical content.
    private static byte[] Blp(int seed) => [.. "BLP2"u8, .. BlteBuilder.Bytes(296, seed)];
    private static readonly byte[] Md20Content = [.. "MD20"u8, .. BlteBuilder.Bytes(296, 8)];

    /// <summary>
    /// 10 stored BLP; 11 stored, no magic; 12 not stored, BLP; 13 stored BLP whose first chunk is encrypted with an unknown key;
    /// 14 stored BLP with a hole from byte 10; 15 stored BLP, corrupt; 16 stored, empty; 17 stored MD20.
    /// </summary>
    private static IReadOnlyList<FakeFile> FormatFiles() =>
    [
        new(10, "textures/a.blp", Storage: FileStorage.Stored, Content: Blp(10)),
        new(11, "textures/b.blp", Storage: FileStorage.Stored, Content: Content100),
        new(12, "textures/c.blp", Content: Blp(12)),
        new(13, "textures/d.blp", Storage: FileStorage.Stored, Content: Blp(13), Holes: [new(0, 128, KeyA)]),
        new(14, "textures/e.blp", Storage: FileStorage.Stored, Content: Blp(14), Holes: [new(10, 100, KeyA)]),
        new(15, "textures/f.blp", Storage: FileStorage.Stored, Content: Blp(15), ReadFailure: new(FileReadState.Corrupt, "BLTE_INVALID")),
        new(16, "textures/g.blp", Storage: FileStorage.Stored, Content: []),
        new(17, "creature/a/a.m2", Storage: FileStorage.Stored, Content: Md20Content),
    ];

    private static async Task<Dictionary<uint, (FileFormat, FileFormatSource)>> FormatsAsync(IBuildFiles files, FileFilter? filter = null) =>
        (await ListAsync(files, filter ?? FileFilter.All)).ToDictionary(f => f.FileDataId, f => (f.Format, f.FormatSource));

    [Fact]
    public async Task StoredFilesGetTheirFormatFromTheirFirstBytes()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        var formats = await FormatsAsync(files);
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), formats[10]);
        Assert.Equal((FileFormat.M2Legacy, FileFormatSource.Magic), formats[17]);
        // Bytes before a hole that starts past 0 still count.
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), formats[14]);
        // No names are listed yet, so bytes that prove nothing leave the format unknown.
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), formats[11]);
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), formats[16]);
        foreach (var id in new uint[] { 10, 11, 14, 16, 17 })
        {
            var stat = (await files.StatAsync(id, Ct)).File!;
            Assert.Equal(formats[id], (stat.Format, stat.FormatSource));
        }
    }

    [Fact]
    public async Task UnreadableFirstBytesAreNoEvidenceAndNeverFailTheListing()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        var formats = await FormatsAsync(files);
        Assert.Equal(8, formats.Count);
        // An encrypted first chunk without its key, and a corrupt copy: the magic isn't read.
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), formats[13]);
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), formats[15]);
        var stat = await files.StatAsync(15, Ct);
        Assert.Equal((FileLookupState.Found, FileFormat.Unknown, FileFormatSource.None), (stat.State, stat.File!.Format, stat.File.FormatSource));
    }

    [Fact]
    public async Task FormatDetectionNeverFetches()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (await FormatsAsync(files))[12]);
        var stat = (await files.StatAsync(12, Ct)).File!;
        Assert.Equal((FileStorage.NotStored, FileFormat.Unknown, FileFormatSource.None), (stat.Storage, stat.Format, stat.FormatSource));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(12, Ct)).File!.Storage);
    }

    [Fact]
    public async Task ANotStoredFileGetsItsMagicOnceStored()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        Assert.Equal(FileFormatSource.None, (await files.StatAsync(12, Ct)).File!.FormatSource);
        MarkStored(FilesFixture.Build, 12);
        var stat = (await files.StatAsync(12, Ct)).File!;
        Assert.Equal((FileStorage.Stored, FileFormat.Blp, FileFormatSource.Magic), (stat.Storage, stat.Format, stat.FormatSource));
    }

    [Fact]
    public async Task AWholeReadGivesTheFormat()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        Assert.Equal(FileReadState.Complete, (await files.GetAsync(12, Ct)).State);
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (await FormatsAsync(files))[12]);
    }

    [Fact]
    public async Task FiltersByDetectedFormat()
    {
        await using var files = await OpenAsync(files: FormatFiles());
        Assert.Equal((uint[])[10u, 14], await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Blp } }));
        Assert.Equal((uint[])[11u, 12, 13, 15, 16], await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Unknown } }));
        Assert.Equal((uint[])[10u, 14, 17], await IdsAsync(files,
            FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Blp, FileFormat.M2Legacy } }));
        Assert.Equal((uint[])[14u], await IdsAsync(files,
            FileFilter.All with { FromId = 11, ToId = 16, Formats = new HashSet<FileFormat> { FileFormat.Blp } }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Ogg } }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat>() }));
        // The other criteria still hold.
        Assert.Equal((uint[])[11u, 13, 15, 16], await IdsAsync(files, FileFilter.All with
        {
            Formats = new HashSet<FileFormat> { FileFormat.Unknown }, Storage = new HashSet<FileStorage> { FileStorage.Stored },
        }));
    }

    // StatAsync and ExistsAsync

    [Fact]
    public async Task StatsByIdAndPathMatchTheListing()
    {
        await using var files = await OpenAsync();
        var listed = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId);
        foreach (var (id, path) in new[] { (100u, "Interface/Icons/One.blp"), (200u, "world/en/three.m2"), (300u, "sound/four.ogg"), (400u, "sound/five.ogg") })
        {
            Assert.Equal(new FileStat(FileLookupState.Found, listed[id]), await files.StatAsync(id, Ct));
            Assert.True(await files.ExistsAsync(id, Ct));
            foreach (var asked in new[] { path, path.ToUpperInvariant(), path.ToLowerInvariant().Replace('/', '\\') })
            {
                // ROOT's name hash matched and the listfile doesn't name it: the asked path, as given, labels it.
                var stat = await files.StatAsync(FileRef.ForPath(asked), Ct);
                Assert.Equal(FileLookupState.Found, stat.State);
                Assert.Equal((asked, FilePathSource.RootHash), (stat.File!.Path, stat.File.PathSource));
                Assert.Equal(listed[id] with { Format = stat.File.Format, FormatSource = stat.File.FormatSource },
                    stat.File with { Path = null, PathSource = FilePathSource.None });
                Assert.True(await files.ExistsAsync(FileRef.ForPath(asked), Ct));
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
        var expected = new BuildFile(250, null, FilePathSource.None, null, FilesFixture.ContentKeyOf(250), FileFormat.Unknown, FileFormatSource.None, FileStorage.NoEncodedCopy, false);
        Assert.Equal(new FileStat(FileLookupState.NotInEncoding, expected), await files.StatAsync(250, Ct));
        Assert.True(await files.ExistsAsync(250, Ct));
        var byPath = FileRef.ForPath("Sound\\Missing.OGG");
        Assert.Equal(new FileStat(FileLookupState.NotInEncoding, expected with
        {
            Path = "Sound\\Missing.OGG", PathSource = FilePathSource.RootHash, Format = FileFormat.Ogg, FormatSource = FileFormatSource.Extension,
        }), await files.StatAsync(byPath, Ct));
        Assert.True(await files.ExistsAsync(byPath, Ct));
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

    // Names: FakeFile.Path is ROOT's name hash, UseNamesAsync the listfile.

    private static readonly byte[] Content2 = BlteBuilder.Bytes(64, 2), Content3 = BlteBuilder.Bytes(64, 3), Content5 = BlteBuilder.Bytes(64, 5);

    /// <summary>
    /// 1 and 5 have ROOT names; 3 is listfile-named with 5's ROOT name; 4 is deDE only and shares its listfile name with 6,
    /// which the build lacks; 8 and 9 share a name; 10 (enUS) and 11 (deDE) share a name; 7 is named but not in the build;
    /// 12 has no name at all.
    /// </summary>
    private static IReadOnlyList<FakeFile> NamedFiles() =>
    [
        new(1, "root/a.blp", 100),
        new(2, Content: Content2),
        new(3, Content: Content3),
        new(4, Size: 400, Locales: ["deDE"]),
        new(5, "root/b.blp", Content: Content5),
        new(8, Size: 800),
        new(9, Size: 900),
        new(10, Size: 1000, Locales: ["enUS"]),
        new(11, Size: 1100, Locales: ["deDE"]),
        new(12, Size: 1200),
    ];

    private static readonly IReadOnlyDictionary<uint, string> Listfile = new Dictionary<uint, string>
    {
        [1] = "LF/A.blp", [2] = "lf/Two.BLP", [3] = "Root/B.BLP", [4] = "lf/dup.m2", [6] = "lf/dup.m2", [7] = "lf/gone.blp",
        [8] = "lf/twice.ogg", [9] = "LF\\TWICE.ogg", [10] = "lf/locale.lua", [11] = "lf/locale.lua",
    };

    private async Task<IBuildFiles> OpenNamedAsync(string locale = "enUS", bool names = true)
    {
        var fs = Create(FilesFixture.Build, Locales, NamedFiles());
        if (names) await UseNamesAsync(Listfile);
        var result = await fs.ForAsync(FilesFixture.Build, locale, Ct);
        Assert.Equal(FilesOpenState.Open, result.State);
        return result.Files!;
    }

    [Fact]
    public async Task ListedPathsAreListfileNamesWhenNamesAreOn()
    {
        await using var files = await OpenNamedAsync();
        var listed = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId, f => (f.Path, f.PathSource));
        Assert.Equal((uint[])[1u, 2, 3, 5, 8, 9, 10, 12], listed.Keys.ToArray());
        Assert.Equal(("LF/A.blp", FilePathSource.Listfile), listed[1]);
        Assert.Equal(("Root/B.BLP", FilePathSource.Listfile), listed[3]);
        // A ROOT name is never listed.
        Assert.Equal((null, FilePathSource.None), listed[5]);
        Assert.Equal((null, FilePathSource.None), listed[12]);
        var stat = (await files.StatAsync(9, Ct)).File!;
        Assert.Equal(("LF\\TWICE.ogg", FilePathSource.Listfile), (stat.Path, stat.PathSource));
    }

    [Fact]
    public async Task TurningNamesOffTakesEffectOnTheNextCall()
    {
        await using var files = await OpenNamedAsync();
        Assert.Equal("lf/Two.BLP", (await files.StatAsync(2, Ct)).File!.Path);
        await UseNamesAsync(null);
        Assert.All(await ListAsync(files, FileFilter.All), f => Assert.Equal((null, FilePathSource.None), (f.Path, f.PathSource)));
        Assert.Equal((null, FilePathSource.None), ((await files.StatAsync(2, Ct)).File!.Path, (await files.StatAsync(2, Ct)).File!.PathSource));
        Assert.Equal(FileLookupState.UnknownFile, (await files.StatAsync(FileRef.ForPath("lf/two.blp"), Ct)).State);
        Assert.False(await files.ExistsAsync(FileRef.ForPath("lf/two.blp"), Ct));
        // ROOT's name hash doesn't need names.
        Assert.Equal(1u, (await files.StatAsync(FileRef.ForPath("root/a.blp"), Ct)).File!.FileDataId);
        await UseNamesAsync(Listfile);
        Assert.Equal("lf/Two.BLP", (await files.StatAsync(2, Ct)).File!.Path);
    }

    [Fact]
    public async Task NamesStartOff()
    {
        await using var files = await OpenNamedAsync(names: false);
        Assert.All(await ListAsync(files, FileFilter.All), f => Assert.Null(f.Path));
        Assert.Equal(FileLookupState.UnknownFile, (await files.StatAsync(FileRef.ForPath("lf/two.blp"), Ct)).State);
    }

    [Fact]
    public async Task LooksUpByARootHashPath()
    {
        await using var files = await OpenNamedAsync();
        // The listfile names 1 differently: its name is the label.
        var stat = await files.StatAsync(FileRef.ForPath("ROOT\\A.BLP"), Ct);
        Assert.Equal((FileLookupState.Found, 1u, "LF/A.blp", FilePathSource.Listfile),
            (stat.State, stat.File!.FileDataId, stat.File.Path, stat.File.PathSource));
        Assert.True(await files.ExistsAsync(FileRef.ForPath("root/a.blp"), Ct));
    }

    [Fact]
    public async Task LooksUpByAListfileOnlyPath()
    {
        await using var files = await OpenNamedAsync();
        var path = FileRef.ForPath("LF\\two.blp");
        var stat = await files.StatAsync(path, Ct);
        Assert.Equal((FileLookupState.Found, 2u, "lf/Two.BLP", FilePathSource.Listfile), (stat.State, stat.File!.FileDataId, stat.File.Path, stat.File.PathSource));
        Assert.True(await files.ExistsAsync(path, Ct));
        AssertComplete(await files.GetAsync(path, Ct), Content2, 0, 64);
        AssertComplete(await files.ReadRangeAsync(path, 10, 20, Ct), Content2, 10, 20);
        var open = await files.OpenReadAsync(path, Ct);
        await using var stream = open.Stream!;
        Assert.Equal((FileReadState.Complete, 64L), (open.State, stream.Length));
        // Named, but this build has no such file.
        Assert.Equal(new FileStat(FileLookupState.UnknownFile, null), await files.StatAsync(FileRef.ForPath("lf/gone.blp"), Ct));
        Assert.False(await files.ExistsAsync(FileRef.ForPath("lf/gone.blp"), Ct));
        Assert.Equal(FileReadState.UnknownFile, (await files.GetAsync(FileRef.ForPath("lf/gone.blp"), Ct)).State);
    }

    [Fact]
    public async Task RootWinsWhenItAndTheListfileDisagree()
    {
        await using var files = await OpenNamedAsync();
        var path = FileRef.ForPath("Root/B.BLP");
        var stat = await files.StatAsync(path, Ct);
        // 5 has no listfile name, so the asked path labels it.
        Assert.Equal((5u, "Root/B.BLP", FilePathSource.RootHash), (stat.File!.FileDataId, stat.File.Path, stat.File.PathSource));
        AssertComplete(await files.GetAsync(path, Ct), Content5, 0, 64);
        AssertComplete(await files.ReadRangeAsync(path, 0, 8, Ct), Content5, 0, 8);
    }

    [Fact]
    public async Task ARepeatedNamePicksTheHighestFileInTheLocale()
    {
        await using (var en = await OpenNamedAsync("enUS"))
        {
            Assert.Equal(9u, (await en.StatAsync(FileRef.ForPath("lf/twice.ogg"), Ct)).File!.FileDataId);
            // 11 is higher but deDE only.
            Assert.Equal(10u, (await en.StatAsync(FileRef.ForPath("lf/locale.lua"), Ct)).File!.FileDataId);
            // Only 4 is in the build, and not in this locale; 6 isn't in the build.
            Assert.Equal(new FileStat(FileLookupState.NotInLocale, null), await en.StatAsync(FileRef.ForPath("lf/dup.m2"), Ct));
            Assert.False(await en.ExistsAsync(FileRef.ForPath("lf/dup.m2"), Ct));
            Assert.Equal(FileReadState.NotInLocale, (await en.GetAsync(FileRef.ForPath("lf/dup.m2"), Ct)).State);
        }
        await using var de = await OpenNamedAsync("deDE");
        Assert.Equal(11u, (await de.StatAsync(FileRef.ForPath("lf/locale.lua"), Ct)).File!.FileDataId);
        Assert.Equal(4u, (await de.StatAsync(FileRef.ForPath("LF/DUP.M2"), Ct)).File!.FileDataId);
    }

    [Fact]
    public async Task PathPrefixMatchesListfileNames()
    {
        await using var files = await OpenNamedAsync();
        Assert.Equal((uint[])[1u, 2, 8, 9, 10], await IdsAsync(files, FileFilter.All with { PathPrefix = "lf/" }));
        Assert.Equal((uint[])[1u, 2, 8, 9, 10], await IdsAsync(files, FileFilter.All with { PathPrefix = "Lf\\" }));
        Assert.Equal((uint[])[8u, 9], await IdsAsync(files, FileFilter.All with { PathPrefix = "LF/twice" }));
        // ROOT names don't count: 5's ROOT name starts root/, but only 3 has a listfile name there.
        Assert.Equal((uint[])[3u], await IdsAsync(files, FileFilter.All with { PathPrefix = "root/" }));
        // Every named file; 12 has no name.
        Assert.Equal((uint[])[1u, 2, 3, 8, 9, 10], await IdsAsync(files, FileFilter.All with { PathPrefix = "" }));
        Assert.Equal((uint[])[9u, 10], await IdsAsync(files, FileFilter.All with { PathPrefix = "lf/", FromId = 9 }));
        Assert.Equal((uint[])[2u, 8], await IdsAsync(files, FileFilter.All with { PathPrefix = "lf/", FromId = 2, ToId = 8, Encrypted = false }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { PathPrefix = "lf/x" }));
        await UseNamesAsync(null);
        Assert.Empty(await IdsAsync(files, FileFilter.All with { PathPrefix = "lf/" }));
        Assert.Empty(await IdsAsync(files, FileFilter.All with { PathPrefix = "" }));
    }

    [Fact]
    public async Task ANamedFileNotStoredGetsItsExtensionsFormat()
    {
        await using var files = await OpenNamedAsync();
        var listed = (await ListAsync(files, FileFilter.All)).ToDictionary(f => f.FileDataId);
        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), (listed[2].Format, listed[2].FormatSource));
        Assert.Equal((FileFormat.Ogg, FileFormatSource.Extension), (listed[8].Format, listed[8].FormatSource));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (listed[12].Format, listed[12].FormatSource));
        Assert.Equal((uint[])[1u, 2, 3], await IdsAsync(files, FileFilter.All with { Formats = new HashSet<FileFormat> { FileFormat.Blp } }));
        var stat = (await files.StatAsync(2, Ct)).File!;
        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), (stat.Format, stat.FormatSource));
    }

    // GetAsync, ReadRangeAsync and OpenReadAsync

    private const string KeyA = "1122334455667788", KeyB = "0102030405060708";
    private static readonly byte[] Plain = BlteBuilder.Bytes(1000, 10), Stored = BlteBuilder.Bytes(1000, 11), Streamed = BlteBuilder.Bytes(700, 12),
        WithHoles = BlteBuilder.Bytes(1000, 20), AllHoles = BlteBuilder.Bytes(400, 21), Big = BlteBuilder.Bytes(2000, 60);
    private static readonly FileHole[] HolesOf20 = [new(200, 300, KeyA), new(500, 100, KeyB), new(800, 200, KeyA)];
    private static readonly FileHole[] HolesOf21 = [new(0, 250, KeyA), new(250, 150, KeyB)];

    /// <summary>The scripted failures, by FileDataID 40 to 46, and whether the code is the same on both sides.</summary>
    public static TheoryData<uint, FileReadState, string, bool> Failures => new()
    {
        { 40, FileReadState.Missing, "CDN_NOT_FOUND", true },
        { 41, FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK", true },
        { 42, FileReadState.Unavailable, "CDN_TIMEOUT", true },
        { 43, FileReadState.StoreFailed, "OBJECT_STORE_DISK_FULL", true },
        { 44, FileReadState.Corrupt, "FAKE_CORRUPT", false },
        { 45, FileReadState.Unsupported, "FAKE_UNSUPPORTED", false },
        { 46, FileReadState.WrongKey, "FAKE_WRONG_KEY", false },
    };

    /// <summary>
    /// Every file has its own content: equal bytes are one encoded copy on the real side, stored or failing together.
    /// 10 not stored and 11 stored, 1000 bytes; 12 not stored, 700 bytes; 20 with holes of A, B and A (Partial); 21 all holes
    /// (Protected); 30 no content (NeedsNetwork); 40 to 46 scripted failures; 50 empty; 150 deDE only; 250 not in ENCODING.
    /// </summary>
    private static IReadOnlyList<FakeFile> ReadFiles() =>
    [
        new(10, Content: Plain),
        new(11, Storage: FileStorage.Stored, Content: Stored),
        new(12, Content: Streamed),
        new(20, Content: WithHoles, Holes: HolesOf20),
        new(21, Content: AllHoles, Holes: HolesOf21),
        new(30, Size: 500),
        .. Failures.Select(f => new FakeFile((uint)f[0], Content: BlteBuilder.Bytes(600, (int)(uint)f[0]),
            ReadFailure: new((FileReadState)f[1], (string)f[2]))),
        new(50, Content: []),
        new(150, Content: BlteBuilder.Bytes(100, 150), Locales: ["deDE"]),
        new(250, Size: 2500, NotInEncoding: true),
    ];

    private Task<IBuildFiles> OpenReadsAsync(long maxReadBytes = 256L << 20) => OpenAsync(files: ReadFiles(), maxReadBytes: maxReadBytes);

    private static byte[] Slice(byte[] bytes, long offset, long length) => bytes.AsSpan((int)offset, (int)length).ToArray();

    /// <summary>
    /// The read's shape rules: segments and holes ascending, never empty, covering the range exactly; segments maximal, holes of
    /// one key never adjacent; the state follows from them; segment bytes are the content's.
    /// </summary>
    private static void AssertShape(FileRead read, byte[] content)
    {
        var pieces = read.Segments.Select(x => (x.Offset, (long)x.Bytes.Length, Key: (string?)null))
            .Concat(read.Holes.Select(h => (h.Offset, h.Length, Key: (string?)h.KeyName))).OrderBy(p => p.Offset).ToArray();
        var at = read.Offset;
        for (var i = 0; i < pieces.Length; i++)
        {
            Assert.Equal(at, pieces[i].Offset);
            Assert.True(pieces[i].Item2 > 0, "A segment or hole is never empty.");
            if (i > 0) Assert.False(pieces[i - 1].Key == pieces[i].Key, "Segments are maximal and so are holes of one key.");
            at += pieces[i].Item2;
        }
        Assert.Equal(read.Offset + read.Length, at);
        Assert.Equal(read.Segments.OrderBy(x => x.Offset).Select(x => x.Offset), read.Segments.Select(x => x.Offset));
        Assert.Equal(read.Holes.OrderBy(h => h.Offset).Select(h => h.Offset), read.Holes.Select(h => h.Offset));
        foreach (var segment in read.Segments) Assert.Equal(Slice(content, segment.Offset, segment.Bytes.Length), segment.Bytes.ToArray());
        var expected = read.Holes.Count == 0 ? FileReadState.Complete : read.Segments.Count == 0 ? FileReadState.Protected : FileReadState.Partial;
        Assert.Equal(expected, read.State);
        Assert.Null(read.Code);
    }

    private static void AssertComplete(FileRead read, byte[] content, long offset, long length)
    {
        Assert.Equal((FileReadState.Complete, offset, length), (read.State, read.Offset, read.Length));
        AssertShape(read, content);
        Assert.Equal(Slice(content, offset, length), read.Bytes!.Value.ToArray());
        Assert.Equal(length == 0 ? 0 : 1, read.Segments.Count);
        Assert.Empty(read.Holes);
    }

    private static void AssertFailed(FileRead read, FileReadState state, long offset)
    {
        Assert.Equal((state, offset, 0L), (read.State, read.Offset, read.Length));
        Assert.Empty(read.Segments);
        Assert.Empty(read.Holes);
        Assert.Null(read.Bytes);
    }

    [Fact]
    public async Task GetReturnsTheWholeFile()
    {
        await using var files = await OpenReadsAsync();
        AssertComplete(await files.GetAsync(10, Ct), Plain, 0, 1000);
        AssertComplete(await files.GetAsync(11, Ct), Stored, 0, 1000);
        AssertComplete(await files.GetAsync(12, Ct), Streamed, 0, 700);
        AssertComplete(await files.GetAsync(50, Ct), [], 0, 0);
    }

    [Fact]
    public async Task GetByPathReadsTheSameFile()
    {
        await using var files = await OpenAsync();
        AssertComplete(await files.GetAsync(FileRef.ForPath("SOUND\\FOUR.OGG"), Ct), Content300, 0, 3000);
        AssertComplete(await files.GetAsync(100, Ct), Content100, 0, 1000);
    }

    [Fact]
    public async Task RangesInsideAndAcrossChunks()
    {
        await using var files = await OpenReadsAsync();
        const int C = FilesFixture.ChunkBytes;
        foreach (var (offset, length) in new[] { (10L, 20), (C, C), (C - 1, 2), (100L, 300), (0L, 1000), (1L, 998), (999L, 1) })
            AssertComplete(await files.ReadRangeAsync(10, offset, length, Ct), Plain, offset, length);
    }

    [Fact]
    public async Task RangesClampAtTheEnd()
    {
        await using var files = await OpenReadsAsync();
        AssertComplete(await files.ReadRangeAsync(10, 900, 500, Ct), Plain, 900, 100);
        AssertComplete(await files.ReadRangeAsync(10, 0, 1 << 20, Ct), Plain, 0, 1000);
        AssertComplete(await files.ReadRangeAsync(10, 1000, 10, Ct), Plain, 1000, 0);
        AssertComplete(await files.ReadRangeAsync(10, 5000, 10, Ct), Plain, 1000, 0);
        AssertComplete(await files.ReadRangeAsync(10, long.MaxValue, 10, Ct), Plain, 1000, 0);
        AssertComplete(await files.ReadRangeAsync(10, 300, 0, Ct), Plain, 300, 0);
    }

    [Fact]
    public async Task AnEmptyRangeFetchesNothing()
    {
        await using var files = await OpenReadsAsync();
        // No fetch: not stored after, and a file with no copy or a scripted failure still reads Complete.
        AssertComplete(await files.ReadRangeAsync(10, 1000, 10, Ct), Plain, 1000, 0);
        AssertComplete(await files.ReadRangeAsync(10, 10, 0, Ct), Plain, 10, 0);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(10, Ct)).File!.Storage);
        Assert.Equal((FileReadState.Complete, 500L, 0L), await StateAt(files.ReadRangeAsync(30, 600, 10, Ct)));
        Assert.Equal((FileReadState.Complete, 600L, 0L), await StateAt(files.ReadRangeAsync(40, 600, 10, Ct)));
        Assert.Equal((FileReadState.Complete, 20L, 0L), await StateAt(files.ReadRangeAsync(44, 20, 0, Ct)));
    }

    private static async Task<(FileReadState, long, long)> StateAt(Task<FileRead> read)
    {
        var r = await read;
        return (r.State, r.Offset, r.Length);
    }

    [Fact]
    public async Task HolesAreNamedClippedAndMerged()
    {
        await using var files = await OpenReadsAsync();
        var whole = await files.GetAsync(20, Ct);
        Assert.Equal((FileReadState.Partial, 0L, 1000L), (whole.State, whole.Offset, whole.Length));
        AssertShape(whole, WithHoles);
        Assert.Null(whole.Bytes);
        Assert.Equal(HolesOf20, whole.Holes);
        Assert.Equal([0L, 600L], whole.Segments.Select(x => x.Offset));

        var across = await files.ReadRangeAsync(20, 250, 400, Ct);
        AssertShape(across, WithHoles);
        Assert.Equal([new FileHole(250, 250, KeyA), new FileHole(500, 100, KeyB)], across.Holes);
        Assert.Equal(600L, Assert.Single(across.Segments).Offset);
        Assert.Equal(FileReadState.Partial, across.State);

        var start = await files.ReadRangeAsync(20, 150, 100, Ct);
        AssertShape(start, WithHoles);
        Assert.Equal([new FileHole(200, 50, KeyA)], start.Holes);
        Assert.Equal((150L, 50), (start.Segments[0].Offset, start.Segments[0].Bytes.Length));

        var inside = await files.ReadRangeAsync(20, 300, 250, Ct);
        Assert.Equal(FileReadState.Protected, inside.State);
        AssertShape(inside, WithHoles);
        Assert.Equal([new FileHole(300, 200, KeyA), new FileHole(500, 50, KeyB)], inside.Holes);

        var end = await files.ReadRangeAsync(20, 900, 500, Ct);
        Assert.Equal((FileReadState.Protected, 900L, 100L), (end.State, end.Offset, end.Length));
        Assert.Equal([new FileHole(900, 100, KeyA)], end.Holes);

        AssertComplete(await files.ReadRangeAsync(20, 620, 100, Ct), WithHoles, 620, 100);
    }

    [Fact]
    public async Task AFileOfHolesIsProtected()
    {
        await using var files = await OpenReadsAsync();
        var read = await files.GetAsync(21, Ct);
        Assert.Equal((FileReadState.Protected, 0L, 400L), (read.State, read.Offset, read.Length));
        AssertShape(read, AllHoles);
        Assert.Equal(HolesOf21, read.Holes);
        Assert.Null(read.Bytes);
        var range = await files.ReadRangeAsync(21, 200, 100, Ct);
        Assert.Equal([new FileHole(200, 50, KeyA), new FileHole(250, 50, KeyB)], range.Holes);
        Assert.Equal(FileReadState.Protected, range.State);
    }

    [Fact]
    public async Task LookupStatesCarryTheAskedOffset()
    {
        await using var files = await OpenReadsAsync();
        foreach (var (file, state) in new[] { (FileRef.Id(999), FileReadState.UnknownFile), (FileRef.ForPath("no/such/file"), FileReadState.UnknownFile),
            (FileRef.Id(150), FileReadState.NotInLocale), (FileRef.Id(250), FileReadState.NotInEncoding) })
        {
            AssertFailed(await files.GetAsync(file, Ct), state, 0);
            AssertFailed(await files.ReadRangeAsync(file, 77, 10, Ct), state, 77);
            AssertFailed(await files.ReadRangeAsync(file, 5000, 0, Ct), state, 5000);
            var open = await files.OpenReadAsync(file, Ct);
            Assert.Equal((state, null), (open.State, open.Stream));
        }
    }

    [Fact]
    public async Task AFileWithNoCopyNeedsNetwork()
    {
        await using var files = await OpenReadsAsync();
        var get = await files.GetAsync(30, Ct);
        AssertFailed(get, FileReadState.NeedsNetwork, 0);
        Assert.Equal("OBJECT_ACCESS_NEEDS_NETWORK", get.Code);
        var range = await files.ReadRangeAsync(30, 40, 10, Ct);
        AssertFailed(range, FileReadState.NeedsNetwork, 40);
        Assert.Equal("OBJECT_ACCESS_NEEDS_NETWORK", range.Code);
        var open = await files.OpenReadAsync(30, Ct);
        Assert.Equal((FileReadState.NeedsNetwork, null, "OBJECT_ACCESS_NEEDS_NETWORK"), (open.State, open.Stream, open.Code));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(30, Ct)).File!.Storage);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task ScriptedFailuresCarryNothing(uint id, FileReadState state, string code, bool sameCode)
    {
        await using var files = await OpenReadsAsync();
        var get = await files.GetAsync(id, Ct);
        AssertFailed(get, state, 0);
        var range = await files.ReadRangeAsync(id, 300, 100, Ct);
        AssertFailed(range, state, 300);
        Assert.NotNull(get.Code);
        Assert.NotNull(range.Code);
        if (sameCode) Assert.Equal((code, code), (get.Code, range.Code));
        // Missing is layer 2's dated 404.
        if (state == FileReadState.Missing) Assert.Equal((true, true), (get.ObservedAt is not null, range.ObservedAt is not null));
        var open = await files.OpenReadAsync(id, Ct);
        if (state == FileReadState.WrongKey)
        {
            // WrongKey shows only when a chunk is decoded: the stream opens, and its reads fail.
            Assert.Equal(FileReadState.Complete, open.State);
            await using var stream = open.Stream!;
            var thrown = await Assert.ThrowsAsync<FileReadException>(() => ReadOnceAsync(stream));
            Assert.Equal(FileReadState.WrongKey, thrown.State);
            Assert.Equal(0, stream.Position);
            return;
        }
        Assert.Equal((state, null), (open.State, open.Stream));
        Assert.NotNull(open.Code);
        if (sameCode) Assert.Equal(code, open.Code);
        if (state == FileReadState.Missing) Assert.NotNull(open.ObservedAt);
    }

    [Fact]
    public async Task ALayer2FailureMidStreamThrowsFromTheRead()
    {
        var content = BlteBuilder.Bytes(600, 47);
        await using var files = await OpenAsync(files: [new(47, Content: content,
            ReadFailure: new(FileReadState.Unavailable, "CDN_TIMEOUT", OnStreamRead: true))]);
        var open = await files.OpenReadAsync(47, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;
        Assert.Empty(stream.Holes);
        StreamOpened(47);
        stream.Position = 200;
        var thrown = await Assert.ThrowsAsync<FileReadException>(() => ReadOnceAsync(stream));
        Assert.Equal((FileReadState.Unavailable, "CDN_TIMEOUT"), (thrown.State, thrown.Code));
        Assert.Equal(200, stream.Position);
        // Still failing, and an empty read or one at the end needs no bytes.
        Assert.Equal(FileReadState.Unavailable, (await Assert.ThrowsAsync<FileReadException>(() => ReadOnceAsync(stream))).State);
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, Ct));
        stream.Position = 600;
        Assert.Equal(0, await ReadOnceAsync(stream));
        AssertFailed(await files.ReadRangeAsync(47, 10, 10, Ct), FileReadState.Unavailable, 10);
    }

    [Fact]
    public async Task AWholeReadOverTheLimitIsTooLarge()
    {
        var limited = new FakeFile[] { new(60, Content: Big), new(61, Size: 5000), new(62, Content: BlteBuilder.Bytes(2000, 62), ReadFailure: new(FileReadState.Unavailable, "CDN_TIMEOUT")) };
        await using var files = await OpenAsync(files: limited, maxReadBytes: 1000);
        foreach (var id in new uint[] { 60, 61, 62 })
        {
            var read = await files.GetAsync(id, Ct);
            AssertFailed(read, FileReadState.TooLarge, 0);
            Assert.Equal("FILES_READ_TOO_LARGE", read.Code);
        }
        AssertComplete(await files.ReadRangeAsync(60, 500, 1000, Ct), Big, 500, 1000);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(60, 0, 1001, Ct));
        var open = await files.OpenReadAsync(60, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;
        Assert.Equal(Big, await ReadToEndAsync(stream, 333));
    }

    [Fact]
    public async Task ReadArgumentsAreChecked()
    {
        await using var files = await OpenReadsAsync(maxReadBytes: 1000);
        await Assert.ThrowsAsync<ArgumentException>(() => files.GetAsync(default, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadRangeAsync(default, 0, 1, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => files.OpenReadAsync(default, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(10, -1, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(10, 0, -1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(10, 0, 1001, Ct));
        // Arguments are checked before the lookup.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(999, -1, 1, Ct));
        AssertComplete(await files.ReadRangeAsync(10, 0, 1000, Ct), Plain, 0, 1000);
    }

    [Fact]
    public async Task ReadsOnADisposedHandleThrow()
    {
        var files = await OpenReadsAsync();
        await files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.GetAsync(10, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.ReadRangeAsync(10, 0, 10, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.ReadRangeAsync(10, 5000, 0, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.OpenReadAsync(10, Ct));
    }

    [Fact]
    public async Task ASuccessfulReadStoresTheFile()
    {
        await using var files = await OpenReadsAsync();
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(10, Ct)).File!.Storage);
        AssertComplete(await files.ReadRangeAsync(10, 0, 10, Ct), Plain, 0, 10);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(10, Ct)).File!.Storage);

        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(12, Ct)).File!.Storage);
        var open = await files.OpenReadAsync(12, Ct);
        await open.Stream!.DisposeAsync();
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(12, Ct)).File!.Storage);

        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(20, Ct)).File!.Storage);
        Assert.Equal(FileReadState.Partial, (await files.GetAsync(20, Ct)).State);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(20, Ct)).File!.Storage);
    }

    // Streams

    /// <summary>Reads to the end in <paramref name="size"/>-byte reads, accepting short ones.</summary>
    private static async Task<byte[]> ReadToEndAsync(Stream stream, int size)
    {
        var all = new List<byte>();
        var buffer = new byte[size];
        int got;
        while ((got = await stream.ReadAsync(buffer, Ct)) > 0) all.AddRange(buffer.AsSpan(0, got));
        return [.. all];
    }

    /// <summary>One 10-byte read, as a task.</summary>
    private static Task<int> ReadOnceAsync(Stream stream) => stream.ReadAsync(new byte[10], Ct).AsTask();

    /// <summary>Reads until a read throws <see cref="FileHoleException"/> (returned) or the end (null), collecting the bytes.</summary>
    private static async Task<(byte[] Bytes, FileHole? Hole)> ReadToHoleAsync(Stream stream, int size)
    {
        var all = new List<byte>();
        var buffer = new byte[size];
        while (true)
        {
            int got;
            try { got = await stream.ReadAsync(buffer, Ct); }
            catch (FileHoleException e) { return ([.. all], e.Hole); }
            if (got == 0) return ([.. all], null);
            all.AddRange(buffer.AsSpan(0, got));
        }
    }

    [Fact]
    public async Task AStreamReadsTheWholeFileInOddReads()
    {
        await using var files = await OpenReadsAsync();
        var open = await files.OpenReadAsync(10, Ct);
        Assert.Equal((FileReadState.Complete, null), (open.State, open.Code));
        await using var stream = open.Stream!;
        Assert.Empty(stream.Holes);
        Assert.Equal((true, true, false, 1000L, 0L), (stream.CanRead, stream.CanSeek, stream.CanWrite, stream.Length, stream.Position));
        Assert.Equal(Plain, await ReadToEndAsync(stream, 7));
        Assert.Equal(1000, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[10], Ct));
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, Ct));
    }

    [Fact]
    public async Task AnEmptyFileStreamsNothing()
    {
        await using var files = await OpenReadsAsync();
        var open = await files.OpenReadAsync(50, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;
        Assert.Equal((0L, 0), (stream.Length, await stream.ReadAsync(new byte[4], Ct)));
        Assert.Empty(stream.Holes);
    }

    [Fact]
    public async Task AStreamSeeksFromEachOrigin()
    {
        await using var files = await OpenReadsAsync();
        await using var stream = (await files.OpenReadAsync(10, Ct)).Stream!;
        Assert.Equal(900, stream.Seek(-100, SeekOrigin.End));
        Assert.Equal(950, stream.Seek(50, SeekOrigin.Current));
        Assert.Equal(Slice(Plain, 950, 50), await ReadToEndAsync(stream, 13));
        Assert.Equal(130, stream.Seek(130, SeekOrigin.Begin));
        var buffer = new byte[5];
        Assert.Equal(5, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(Slice(Plain, 130, 5), buffer);
        Assert.Equal(125, stream.Seek(-10, SeekOrigin.Current));
        Assert.Equal(Slice(Plain, 125, 875), await ReadToEndAsync(stream, 64));
        stream.Position = 3;
        Assert.Equal(3, stream.Position);
        Assert.Equal(Slice(Plain, 3, 997), await ReadToEndAsync(stream, 100));
        // Past the end reads nothing; before the start throws.
        Assert.Equal(1005, stream.Seek(5, SeekOrigin.End));
        Assert.Equal(0, await stream.ReadAsync(new byte[4], Ct));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-1001, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Equal(1005, stream.Position);
    }

    [Fact]
    public async Task AStreamStopsAtHolesAndThrowsInsideThem()
    {
        await using var files = await OpenReadsAsync();
        var open = await files.OpenReadAsync(20, Ct);
        Assert.Equal(FileReadState.Partial, open.State);
        await using var stream = open.Stream!;
        Assert.Equal(HolesOf20, stream.Holes);
        Assert.Equal(1000, stream.Length);

        stream.Position = 150;
        var (bytes, hole) = await ReadToHoleAsync(stream, 100);
        Assert.Equal(Slice(WithHoles, 150, 50), bytes);
        Assert.Equal(HolesOf20[0], hole);
        Assert.Equal(200, stream.Position);

        // Inside a hole: the whole merged hole, and the position doesn't move.
        stream.Position = 350;
        var inside = await Assert.ThrowsAsync<FileHoleException>(() => ReadOnceAsync(stream));
        Assert.Equal(HolesOf20[0], inside.Hole);
        Assert.Equal(350, stream.Position);
        stream.Position = 599;
        Assert.Equal(HolesOf20[1], (await Assert.ThrowsAsync<FileHoleException>(() => ReadOnceAsync(stream))).Hole);

        stream.Position = 600;
        (bytes, hole) = await ReadToHoleAsync(stream, 33);
        Assert.Equal(Slice(WithHoles, 600, 200), bytes);
        Assert.Equal(HolesOf20[2], hole);

        // The stream stays usable after a hole.
        stream.Position = 0;
        (bytes, hole) = await ReadToHoleAsync(stream, 1000);
        Assert.Equal(Slice(WithHoles, 0, 200), bytes);
        Assert.Equal(HolesOf20[0], hole);
    }

    [Fact]
    public async Task AStreamOfHolesIsProtected()
    {
        await using var files = await OpenReadsAsync();
        var open = await files.OpenReadAsync(21, Ct);
        Assert.Equal(FileReadState.Protected, open.State);
        await using var stream = open.Stream!;
        Assert.Equal(HolesOf21, stream.Holes);
        Assert.Equal(HolesOf21[0], (await Assert.ThrowsAsync<FileHoleException>(() => ReadOnceAsync(stream))).Hole);
        stream.Position = 399;
        Assert.Equal(HolesOf21[1], (await Assert.ThrowsAsync<FileHoleException>(() => ReadOnceAsync(stream))).Hole);
        stream.Position = 400;
        Assert.Equal(0, await stream.ReadAsync(new byte[10], Ct));
    }

    [Fact]
    public async Task AStreamIsAsyncOnlyAndReadOnly()
    {
        await using var files = await OpenReadsAsync();
        await using var stream = (await files.OpenReadAsync(10, Ct)).Stream!;
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[4], 0, 4));
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[4].AsSpan()));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(5));
        Assert.Equal(0, stream.Position);
        var buffer = new byte[10];
        Assert.Equal(10, await stream.ReadAsync(buffer, 0, 10, Ct));
        Assert.Equal(Slice(Plain, 0, 10), buffer);
    }

    [Fact]
    public async Task ADisposedStreamThrowsButKeepsItsHoles()
    {
        await using var files = await OpenReadsAsync();
        var stream = (await files.OpenReadAsync(20, Ct)).Stream!;
        Assert.Equal(10, await stream.ReadAsync(new byte[10], Ct));
        await stream.DisposeAsync();
        Assert.Equal((false, false, false), (stream.CanRead, stream.CanSeek, stream.CanWrite));
        Assert.Equal(HolesOf20, stream.Holes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => ReadOnceAsync(stream));
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        // Disposing twice is fine.
        stream.Dispose();
    }

    [Fact]
    public async Task AStreamOutlivesItsHandle()
    {
        var files = await OpenReadsAsync();
        await using var plain = (await files.OpenReadAsync(12, Ct)).Stream!;
        await using var holed = (await files.OpenReadAsync(20, Ct)).Stream!;
        await files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.OpenReadAsync(12, Ct));
        Assert.Equal(Streamed, await ReadToEndAsync(plain, 99));
        holed.Position = 600;
        var (bytes, hole) = await ReadToHoleAsync(holed, 50);
        Assert.Equal(Slice(WithHoles, 600, 200), bytes);
        Assert.Equal(HolesOf20[2], hole);
    }

    [Fact]
    public async Task StreamsOfTheSameFileAreIndependent()
    {
        await using var files = await OpenReadsAsync();
        await using var a = (await files.OpenReadAsync(11, Ct)).Stream!;
        await using var b = (await files.OpenReadAsync(FileRef.Id(11), Ct)).Stream!;
        a.Position = 500;
        Assert.Equal(Stored, await ReadToEndAsync(b, 256));
        Assert.Equal(Slice(Stored, 500, 500), await ReadToEndAsync(a, 77));
    }

    // EnsureAsync

    private static EnsureSelection Many(params FileRef[] files) => EnsureSelection.Many(files);

    private static (FileEnsureState, string?)[] StatesOf(EnsureResult result) => [.. result.Files.Select(f => (f.State, f.Code))];

    private static void AssertBounded(EnsureProgress p)
    {
        Assert.InRange(p.FilesDone, 0, p.FilesTotal);
        Assert.InRange(p.BytesDone, 0, p.BytesTotal);
        Assert.True(p.CdnBytes >= 0);
    }

    /// <summary>Records every report as it is delivered.</summary>
    private sealed class Reports : IProgress<EnsureProgress>
    {
        private readonly List<EnsureProgress> seen = [];
        public IReadOnlyList<EnsureProgress> Seen { get { lock (seen) return seen.ToArray(); } }
        public void Report(EnsureProgress value) { lock (seen) seen.Add(value); }
    }

    /// <summary>A clock that stands still, or moves a second on every read.</summary>
    private sealed class StepClock(bool moving) : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => moving ? Interlocked.Add(ref now, TimeSpan.TicksPerSecond) : Interlocked.Read(ref now);
    }

    [Fact]
    public async Task EnsuresOneFile()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(EnsureSelection.One(400), EnsurePriority.Interactive, null, Ct);
        Assert.Equal(EnsureOutcome.Completed, result.Outcome);
        Assert.Equal(new FileEnsured(400, 400, FileEnsureState.Stored, null), Assert.Single(result.Files));
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(400, Ct)).File!.Storage);
        Assert.Equal(1, FetchesOf(400));
        Assert.Equal((1, 1, true), (result.Final.FilesDone, result.Final.FilesTotal, result.Final.Listed));
        Assert.Equal(result.Final.BytesTotal, result.Final.BytesDone);
        Assert.True(result.Final.BytesTotal > 0);

        // Again: already stored, and no new fetch.
        var again = await files.EnsureAsync(EnsureSelection.One(400), EnsurePriority.Interactive, null, Ct);
        Assert.Equal((EnsureOutcome.Completed, FileEnsureState.AlreadyStored), (again.Outcome, Assert.Single(again.Files).State));
        Assert.Equal(1, FetchesOf(400));
    }

    [Fact]
    public async Task EnsuresManyInSelectionOrderWithEveryLookupState()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(Many(500, 400, 999, 150, 250, 100, 200, FileRef.ForPath("no/such/file.blp"), 300), EnsurePriority.Background, null, Ct);
        Assert.Equal(EnsureOutcome.Incomplete, result.Outcome);
        Assert.Equal((FileRef[])[500, 400, 999, 150, 250, 100, 200, FileRef.ForPath("no/such/file.blp"), 300], result.Files.Select(f => f.File));
        Assert.Equal((uint?[])[500, 400, null, 150, 250, 100, 200, null, 300], result.Files.Select(f => f.FileDataId));
        Assert.Equal([
            (FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (FileEnsureState.Stored, null), (FileEnsureState.UnknownFile, null),
            (FileEnsureState.NotInLocale, null), (FileEnsureState.NotInEncoding, null), (FileEnsureState.AlreadyStored, null),
            (FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (FileEnsureState.UnknownFile, null), (FileEnsureState.AlreadyStored, null),
        ], StatesOf(result));
        Assert.Equal((9, 9, true), (result.Final.FilesDone, result.Final.FilesTotal, result.Final.Listed));
        AssertBounded(result.Final);
        Assert.True(result.Final.BytesDone < result.Final.BytesTotal);
    }

    [Fact]
    public async Task DuplicateRefsGiveOneEntry()
    {
        await using var files = await OpenAsync();
        // By id twice and by ROOT path: one file. An unknown path twice: one entry.
        var result = await files.EnsureAsync(Many(400, FileRef.ForPath("sound/five.ogg"), 400, FileRef.ForPath("x/y.ogg"), FileRef.ForPath("x/y.ogg")),
            EnsurePriority.Interactive, null, Ct);
        Assert.Equal([new FileEnsured(400, 400, FileEnsureState.Stored, null), new FileEnsured(FileRef.ForPath("x/y.ogg"), null, FileEnsureState.UnknownFile, null)],
            result.Files);
        Assert.Equal(1, FetchesOf(400));
        Assert.Equal(2, result.Final.FilesTotal);
    }

    [Fact]
    public async Task UnknownPathsDedupeByFoldedCaseAndSlashes()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(Many(FileRef.ForPath("x/y.ogg"), FileRef.ForPath("X\\Y.OGG"), 999, 999, FileRef.ForPath("x/z.ogg")),
            EnsurePriority.Interactive, null, Ct);
        Assert.Equal([FileRef.ForPath("x/y.ogg"), 999, FileRef.ForPath("x/z.ogg")], result.Files.Select(f => f.File));
        Assert.All(result.Files, f => Assert.Equal(FileEnsureState.UnknownFile, f.State));
    }

    [Fact]
    public async Task AnEmptySelectionCompletes()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(EnsureSelection.Many([]), EnsurePriority.Background, null, Ct);
        Assert.Equal((EnsureOutcome.Completed, new EnsureProgress(0, 0, 0, 0, 0, true)), (result.Outcome, result.Final));
        Assert.Empty(result.Files);
        Assert.Throws<ArgumentException>(() => EnsureSelection.One(default));
    }

    [Fact]
    public async Task EnsuresMatchingInFileDataIdOrder()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All with { FromId = 200, ToId = 450 }), EnsurePriority.Background, null, Ct);
        Assert.Equal((uint?[])[200, 250, 300, 400], result.Files.Select(f => f.FileDataId));
        Assert.Equal((FileRef[])[200, 250, 300, 400], result.Files.Select(f => f.File));
        Assert.Equal([(FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (FileEnsureState.NotInEncoding, null), (FileEnsureState.AlreadyStored, null),
            (FileEnsureState.Stored, null)], StatesOf(result));
        Assert.Equal(EnsureOutcome.Incomplete, result.Outcome);
        Assert.True(result.Final.Listed);

        // The filter's other criteria hold: only what isn't stored now.
        var rest = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.NotStored } }),
            EnsurePriority.Background, null, Ct);
        Assert.Equal((uint?[])[200, 500], rest.Files.Select(f => f.FileDataId));
        Assert.Equal(1, FetchesOf(400));
    }

    [Fact]
    public async Task EnsureMatchingFollowsTheLocale()
    {
        await using var files = await OpenAsync("deDE");
        var result = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All with { ToId = 200 }), EnsurePriority.Background, null, Ct);
        Assert.Equal((uint?[])[100, 150], result.Files.Select(f => f.FileDataId));
        Assert.Equal([(FileEnsureState.AlreadyStored, null), (FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK")], StatesOf(result));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>1 Missing, 2 Unavailable, 3 StoreFailed, 4 NeedsNetwork (scripted), 5 fetches; each with its own content.</summary>
    private static IReadOnlyList<FakeFile> FailingFiles() =>
    [
        new(1, Content: BlteBuilder.Bytes(300, 1), ReadFailure: new(FileReadState.Missing, "CDN_NOT_FOUND", ObservedAt: At)),
        new(2, Content: BlteBuilder.Bytes(300, 2), ReadFailure: new(FileReadState.Unavailable, "CDN_OFFLINE")),
        new(3, Content: BlteBuilder.Bytes(300, 3), ReadFailure: new(FileReadState.StoreFailed, "OBJECT_STORE_DISK_FULL")),
        new(4, Content: BlteBuilder.Bytes(300, 4), ReadFailure: new(FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK")),
        new(5, Content: BlteBuilder.Bytes(300, 5)),
    ];

    [Fact]
    public async Task Layer2FailuresMapPerFile()
    {
        await using var files = await OpenAsync(files: FailingFiles());
        var result = await files.EnsureAsync(Many(1, 2, 3, 4, 5), EnsurePriority.Background, null, Ct);
        Assert.Equal(EnsureOutcome.Incomplete, result.Outcome);
        Assert.Equal([(FileEnsureState.Missing, "CDN_NOT_FOUND"), (FileEnsureState.Unavailable, "CDN_OFFLINE"), (FileEnsureState.StoreFailed, "OBJECT_STORE_DISK_FULL"),
            (FileEnsureState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (FileEnsureState.Stored, null)], StatesOf(result));
        Assert.Equal(5, result.Final.FilesDone);
        Assert.InRange(result.Final.BytesDone, 1, result.Final.BytesTotal - 1);
        foreach (var id in (uint[])[1, 2, 3, 4]) Assert.Equal(FileStorage.NotStored, (await files.StatAsync(id, Ct)).File!.Storage);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(5, Ct)).File!.Storage);
    }

    [Fact]
    public async Task AlreadyStoredFilesCostNoRequests()
    {
        await using var files = await OpenAsync();
        var result = await files.EnsureAsync(Many(100, 300), EnsurePriority.Interactive, null, Ct);
        Assert.Equal(EnsureOutcome.Completed, result.Outcome);
        Assert.All(result.Files, f => Assert.Equal(FileEnsureState.AlreadyStored, f.State));
        var matching = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All with { Storage = new HashSet<FileStorage> { FileStorage.Stored } }),
            EnsurePriority.Background, null, Ct);
        Assert.Equal((uint?[])[100, 300], matching.Files.Select(f => f.FileDataId));
        Assert.Equal(EnsureOutcome.Completed, matching.Outcome);
        Assert.Empty(RequestsOf(100));
        Assert.Empty(RequestsOf(300));
        Assert.Equal((0, 0), (FetchesOf(100), FetchesOf(300)));
        Assert.Equal(result.Final.BytesTotal, result.Final.BytesDone);
    }

    [Fact]
    public async Task AnInteractiveEnsureJoinsAHeldBackgroundOne()
    {
        await using var files = await OpenAsync();
        var hold = Hold(400);
        var background = files.EnsureAsync(EnsureSelection.One(400), EnsurePriority.Background, null, Ct);
        await hold.Seen(EnsurePriority.Background);
        var interactive = files.EnsureAsync(Many(400, 100), EnsurePriority.Interactive, null, Ct);
        await hold.Seen(EnsurePriority.Interactive);
        Assert.False(background.IsCompleted);
        Assert.False(interactive.IsCompleted);
        hold.Release();

        var (b, i) = (await background, await interactive);
        Assert.Equal((EnsureOutcome.Completed, FileEnsureState.Stored), (b.Outcome, Assert.Single(b.Files).State));
        Assert.Equal(EnsureOutcome.Completed, i.Outcome);
        Assert.Equal([(FileEnsureState.Stored, null), (FileEnsureState.AlreadyStored, null)], StatesOf(i));
        Assert.Equal(1, FetchesOf(400));
        Assert.Contains(EnsurePriority.Interactive, RequestsOf(400));
        Assert.Contains(EnsurePriority.Background, RequestsOf(400));
    }

    [Fact]
    public async Task ProgressIsCoalescedAndEndsWithTheFinal()
    {
        await using var files = await OpenAsync(time: new StepClock(moving: false));
        var reports = new Reports();
        var result = await files.EnsureAsync(Many(500, 400, 100, 200, 300, 250), EnsurePriority.Background, reports, Ct);
        var seen = reports.Seen;
        // The clock stands still: the first report, then only the final.
        Assert.InRange(seen.Count, 1, 2);
        Assert.Equal(result.Final, seen[^1]);
        Assert.All(seen, AssertBounded);
        Assert.Equal((6, 6, true), (result.Final.FilesDone, result.Final.FilesTotal, result.Final.Listed));
    }

    [Fact]
    public async Task ProgressIsBoundedAndNeverGoesBack()
    {
        await using var files = await OpenAsync(time: new StepClock(moving: true));
        var reports = new Reports();
        var result = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), EnsurePriority.Background, reports, Ct);
        var seen = reports.Seen;
        // How many arrive depends on how fast the drain runs (a newer report replaces one not yet delivered), not on the clock.
        Assert.NotEmpty(seen);
        Assert.Equal(result.Final, seen[^1]);
        Assert.All(seen, AssertBounded);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].FilesDone >= seen[i - 1].FilesDone && seen[i].FilesTotal >= seen[i - 1].FilesTotal);
            Assert.True(seen[i].BytesDone >= seen[i - 1].BytesDone && seen[i].BytesTotal >= seen[i - 1].BytesTotal);
            Assert.True(seen[i].CdnBytes >= seen[i - 1].CdnBytes);
            Assert.True(seen[i].Listed || !seen[i - 1].Listed);
        }
        Assert.Equal((6, 6, true), (result.Final.FilesDone, result.Final.FilesTotal, result.Final.Listed));
    }

    /// <summary>1, 2 and 3 not stored, 4 stored; each with its own content.</summary>
    private static IReadOnlyList<FakeFile> CancelFiles() =>
    [
        new(1, Content: BlteBuilder.Bytes(300, 11)),
        new(2, Content: BlteBuilder.Bytes(300, 12)),
        new(3, Content: BlteBuilder.Bytes(300, 13)),
        new(4, Storage: FileStorage.Stored, Content: BlteBuilder.Bytes(300, 14)),
    ];

    [Fact]
    public async Task CancelKeepsStoredFilesAndMarksTheRestCanceled()
    {
        await using var files = await OpenAsync(files: CancelFiles());
        var hold = Hold(2);
        using var cts = new CancellationTokenSource();
        var reports = new Reports();
        var running = files.EnsureAsync(Many(1, 2, 3, 4), EnsurePriority.Background, reports, cts.Token);
        await hold.Seen(EnsurePriority.Background);
        await cts.CancelAsync();

        var result = await running;
        Assert.Equal(EnsureOutcome.Canceled, result.Outcome);
        Assert.Equal([(FileEnsureState.Stored, null), (FileEnsureState.Canceled, null), (FileEnsureState.Canceled, null), (FileEnsureState.AlreadyStored, null)],
            StatesOf(result));
        Assert.Equal((2, 4), (result.Final.FilesDone, result.Final.FilesTotal));
        Assert.Equal(result.Final, reports.Seen[^1]);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(1, Ct)).File!.Storage);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(2, Ct)).File!.Storage);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(3, Ct)).File!.Storage);
        Assert.Equal(0, FetchesOf(3));
    }

    [Fact]
    public async Task CancelBeforeTheStartCancelsEverything()
    {
        await using var files = await OpenAsync(files: CancelFiles());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await files.EnsureAsync(Many(1, 2), EnsurePriority.Interactive, null, cts.Token);
        Assert.Equal(EnsureOutcome.Canceled, result.Outcome);
        Assert.Equal([new FileEnsured(1, 1, FileEnsureState.Canceled, null), new FileEnsured(2, 2, FileEnsureState.Canceled, null)], result.Files);
        Assert.Equal((0, 2), (result.Final.FilesDone, result.Final.FilesTotal));
        Assert.Equal((0, 0), (FetchesOf(1), FetchesOf(2)));
    }

    [Fact]
    public async Task EnsureOnADisposedHandleThrows()
    {
        var files = await OpenAsync();
        await files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.EnsureAsync(EnsureSelection.One(400), EnsurePriority.Interactive, null, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), EnsurePriority.Background, null, Ct));
    }

    [Fact]
    public async Task EnsureChecksItsArguments()
    {
        await using var files = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentNullException>(() => files.EnsureAsync(null!, EnsurePriority.Interactive, null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.EnsureAsync(EnsureSelection.One(400), (EnsurePriority)7, null, Ct));
    }
}

public sealed class InMemoryFilesContractTests : FilesContractTests
{
    private InMemoryFileSystem? last;

    protected override IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files, long maxReadBytes = 256L << 20,
        TimeProvider? time = null) =>
        last = new InMemoryFileSystem(maxReadBytes) { Time = time ?? TimeProvider.System }.Add(build, locales, files);

    protected override IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code) =>
        new InMemoryFileSystem().Fail(build, state, code);

    protected override void MarkStored(BuildRef build, uint fileDataId) => last!.SetStorage(build, fileDataId, FileStorage.Stored);

    protected override void StreamOpened(uint fileDataId) { }

    protected override Task UseNamesAsync(IReadOnlyDictionary<uint, string>? names)
    {
        if (names is null) last!.Names.State = VibeMiner.Files.Names.FileNamesState.Off;
        else last!.Names.Use(names);
        return Task.CompletedTask;
    }

    protected override int FetchesOf(uint fileDataId) => last!.EnsureFetches.Count(id => id == fileDataId);

    protected override IReadOnlyList<EnsurePriority> RequestsOf(uint fileDataId) =>
        [.. last!.EnsureRequests.Where(r => r.FileDataId == fileDataId).Select(r => r.Priority)];

    protected override FetchHold Hold(uint fileDataId)
    {
        var hold = new FetchHold();
        var fs = last!;
        fs.OnRequest = (id, priority) => { if (id == fileDataId) hold.Saw(priority); };
        fs.BeforeFetch = async (id, _, ct) => { if (id == fileDataId) await hold.Released.WaitAsync(ct); };
        return hold;
    }
}

public sealed class RealFilesContractTests : FilesContractTests
{
    private RealFiles? last;
    private RealNames? names;

    protected override IFileSystem Create(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files, long maxReadBytes = 256L << 20,
        TimeProvider? time = null)
    {
        names = new RealNames();
        last = FilesFixture.Real(build, locales, files, new FileSystemOptions { MaxReadBytes = maxReadBytes, Names = names, Time = time ?? TimeProvider.System });
        return last.Files;
    }

    protected override IFileSystem CreateFailing(BuildRef build, FilesOpenState state, string code)
    {
        var builds = new L3.InMemoryBuilds().Fail(build, Enum.Parse<BuildOpenState>(state.ToString()), BuildPart.Root, code);
        return new FileSystem(builds, new VibeMiner.ObjectAccess.Testing.InMemoryObjectSource());
    }

    protected override void MarkStored(BuildRef build, uint fileDataId) => last!.MarkStored(fileDataId);

    protected override void StreamOpened(uint fileDataId) => last!.FailStreamReads(fileDataId);

    protected override async Task UseNamesAsync(IReadOnlyDictionary<uint, string>? use)
    {
        if (use is null) names!.On = false;
        else await names!.UseAsync(last!.Files, last.Objects, use);
    }

    protected override int FetchesOf(uint fileDataId)
    {
        var key = last!.KeyOf(fileDataId);
        return last.Objects.Fetched.Count(k => k == key);
    }

    protected override IReadOnlyList<EnsurePriority> RequestsOf(uint fileDataId)
    {
        var key = last!.KeyOf(fileDataId);
        return [.. last.Objects.Asked.Where(a => a.Key == key).Select(a => a.Priority == FetchPriority.Interactive ? EnsurePriority.Interactive : EnsurePriority.Background)];
    }

    protected override FetchHold Hold(uint fileDataId)
    {
        var hold = new FetchHold();
        var (key, objects) = (last!.KeyOf(fileDataId), last.Objects);
        objects.BeforeAnswer = async (asked, ct) =>
        {
            if (asked != key) return;
            // Layer 2's fake records the request before it waits here.
            foreach (var p in RequestsOf(fileDataId)) hold.Saw(p);
            await hold.Released.WaitAsync(ct);
        };
        return hold;
    }
}
