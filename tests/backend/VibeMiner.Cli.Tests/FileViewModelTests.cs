using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Objects;
using Xunit;
using static VibeMiner.Cli.Tests.ModelBytes;

namespace VibeMiner.Cli.Tests;

/// <summary>The model section of the file detail: counts, skin profile 0 and referenced files, all from stored bytes.</summary>
public sealed class FileViewModelTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('1', 32), "us");
    private static readonly FilesTarget Target = new(Build, "enUS");

    private readonly string data = Directory.CreateTempSubdirectory("vm-file-model-").FullName;
    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new();
    private readonly BrowseSession session;
    private readonly List<Navigator> navigators = [];

    public FileViewModelTests()
    {
        session = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName))
        {
            FileSystem = (_, _, _, _) => Task.FromResult<IFileSystem>(memory),
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var nav in navigators) await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync();
        Directory.Delete(data, recursive: true);
    }

    private void Add(params FakeFile[] fakes) => memory.Add(Build, ["enUS"], fakes);

    private async Task<Navigator> OpenAsync(uint id, int? referenceLimit = null)
    {
        BuildFile listed;
        await using (var lease = await session.OpenFilesAsync(Target, default))
            listed = (await lease.Files!.StatAsync(FileRef.Id(id), default)).File!;
        var view = referenceLimit is { } limit ? new FileView(Target, listed) { ReferenceLimit = limit } : new FileView(Target, listed);
        var nav = new Navigator(new ViewRegistry().Add(new("file", "the file", () => view)), session, a => a());
        navigators.Add(nav);
        Assert.True(nav.Go("file"));
        await nav.Loading.WaitAsync(Guard);
        Assert.Null(nav.Current.Error);
        return nav;
    }

    private static string[] Lines(Navigator nav) => nav.Current.Content!.Text!.Split('\n');

    /// <summary>The model section: the lines between the stat block and the hex.</summary>
    private static string[] Section(Navigator nav)
    {
        var lines = Lines(nav);
        var start = Array.IndexOf(lines, "") + 1;
        return lines[start..Array.IndexOf(lines, "", start)];
    }

    [Fact]
    public async Task AStoredChunkedModelShowsItsCountsSkinAndReferences()
    {
        var model = Chunked(274, ("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("SKID", Words32(0)),
            ("AFID", Afid((0, 0, 0), (60, 1, 800), (60, 2, 800))));
        Add(new FakeFile(500, Content: model, Storage: FileStorage.Stored),
            new FakeFile(900, Content: Skin(), Storage: FileStorage.Stored),
            new FakeFile(901, Size: 100),
            new FakeFile(700, Size: 100),
            new FakeFile(800, NotInEncoding: true));
        memory.Names.Use(new Dictionary<uint, string> { [500] = "creature/thing/thing.m2", [900] = "thing/thing00.skin" });
        var nav = await OpenAsync(500);

        var section = Section(nav);
        Assert.Equal(
        [
            "Model        version 274, chunked (MD21)",
            "Name         authored",
            "Vertices     4",
            "Textures     2",
            "Materials    1",
            "Bones        0",
            "Skins        1 main, 1 LOD (SFID lists 2, the header 1)",
            "Animations   3 AFID entries, 1 distinct file, 1 inline in the M2 (id 0)",
            "Bone files   0 BFID entries",
            "Skeleton     none (SKID declares no external file)",
            "Physics      none (no PFID)",
            "Bounds       min (0, 0, 0) max (0, 0, 0) radius 0 (declared)",
            "Skin 0       4 vertex lookup, 3 triangle indices, 1 submesh, 1 batch",
            "References   8 declared, by kind",
            "Skin 0        900       found, stored yes, thing/thing00.skin (community name)",
            "LOD skin 0    901       found, stored no, no name",
            "Texture 0     700       found, stored no, no name",
            "Texture 1     0         resolved elsewhere (type 11: creature display)",
            "Skeleton      0         no external file",
            "Anim 0 0.0    0         no external file",
            "Anim 1 60.1   800       not in encoding",
            "Anim 2 60.2   800       not in encoding",
        ], section);
        Assert.All(Lines(nav), l => Assert.True(l.Length <= 80, l));
        Assert.Equal("End of the file.", Lines(nav)[^1]);
        Assert.Empty(stores.Source.Observed);
    }

    [Fact]
    public async Task ANotStoredSkinIsALineNeverEmptyGeometry()
    {
        Add(new FakeFile(501, Content: Chunked(274, ("SFID", Words32(910)), ("TXID", Words32(700, 0))), Storage: FileStorage.Stored),
            new FakeFile(910, Size: 100));
        var nav = await OpenAsync(501);
        var section = Section(nav);
        Assert.Contains("Skins        1 main profile", section);
        Assert.Contains("Skin 0       910 not stored; g on that file fetches it", section);
        Assert.DoesNotContain(section, l => l.Contains("vertex lookup", StringComparison.Ordinal));
        Assert.Contains("Skin 0        910       found, stored no, no name", section);
        Assert.Contains("Texture 0     700       unknown file", section);
        Assert.Empty(stores.Source.Observed);
    }

    [Fact]
    public async Task ATypedTexturesMissingDefaultIsNotAMissingTexture()
    {
        Add(new FakeFile(502, Content: Chunked(274, ("SFID", Words32(0)), ("TXID", Words32(7000, 7777))), Storage: FileStorage.Stored),
            new FakeFile(503, Content: Chunked(274, ("SFID", Words32(0)), ("TXID", Words32(7000, 7778))), Storage: FileStorage.Stored),
            new FakeFile(7778, NotInEncoding: true));
        var section = Section(await OpenAsync(502));
        Assert.Contains("Texture 1     7778      not in encoding (type 11 default; game sets it)", Section(await OpenAsync(503)));
        Assert.Contains("Skin 0       not declared: no file for profile 0 in SFID", section);
        Assert.Contains("Skin 0        0         no file declared", section);
        // Type 0 keeps the plain state; type 11's TXID id is only a default the game replaces.
        Assert.Contains("Texture 0     7000      unknown file", section);
        Assert.Contains("Texture 1     7777      not in this build (type 11 default; game sets it)", section);
    }

    [Fact]
    public async Task AModelThatIsNotStoredIsOneLineAndNoCounts()
    {
        Add(new FakeFile(510, Size: 5000));
        memory.Names.Use(new Dictionary<uint, string> { [510] = "world/thing.m2" });
        var nav = await OpenAsync(510);
        Assert.Equal(["Model        not read: not stored (OBJECT_ACCESS_NEEDS_NETWORK); g fetches it"], Section(nav));
        Assert.Equal("00000000  not stored (OBJECT_ACCESS_NEEDS_NETWORK); g fetches it", Lines(nav)[^1]);
        await using (var lease = await session.OpenFilesAsync(Target, default))
            Assert.Equal(FileStorage.NotStored, (await lease.Files!.StatAsync(FileRef.Id(510), default)).File!.Storage);
        Assert.Empty(stores.Source.Observed);
    }

    [Fact]
    public async Task ARejectedModelShowsItsCodeAndTheHexStillShows()
    {
        Add(new FakeFile(520, Content: Chunked(999, ("SFID", Words32(900))), Storage: FileStorage.Stored));
        var nav = await OpenAsync(520);
        var section = Assert.Single(Section(nav));
        Assert.StartsWith("Model        not read: rejected (MODEL_UNSUPPORTED): M2 version 999", section, StringComparison.Ordinal);
        Assert.True(section.Length <= 80);
        Assert.StartsWith("00000000  4d 44 32 31", Lines(nav)[Array.IndexOf(Lines(nav), section) + 2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyModelSaysItsSkinsAreNotDeclared()
    {
        Add(new FakeFile(530, Content: Body(264), Storage: FileStorage.Stored));
        var nav = await OpenAsync(530);
        Assert.Contains("Format       M2Legacy (from the bytes)", Lines(nav));
        var section = Section(nav);
        Assert.Equal("Model        version 264, legacy (MD20)", section[0]);
        Assert.Contains("Skins        1 profile in the header; legacy MD20 declares no skin FileDataIDs", section);
        Assert.Contains("Skin 0       not declared: legacy model, no SFID; no name is guessed", section);
        Assert.Contains("Skin 0        -         not declared (legacy model, never guessed)", section);
        Assert.Contains("Texture 0     -         no file declared", section);
    }

    [Fact]
    public async Task ReferenceLinesStopAtTheCapAndSayHowManyWereCut()
    {
        var entries = Enumerable.Range(0, 250).Select(i => ((ushort)i, (ushort)0, (uint)(1000 + i))).ToArray();
        Add(new FakeFile(540, Content: Chunked(274, ("SFID", Words32(900, 901)), ("TXID", Words32(700, 0)), ("AFID", Afid(entries))),
            Storage: FileStorage.Stored));
        var nav = await OpenAsync(540);
        var section = Section(nav);
        Assert.Contains("Animations   250 AFID entries, 250 distinct files, 0 inline in the M2 (id 0)", section);
        Assert.Contains("References   254 declared, by kind", section);
        var references = section[(Array.IndexOf(section, "References   254 declared, by kind") + 1)..];
        Assert.Equal(201, references.Length);
        // The label column widens to the longest label, "Anim 199 199.0", so every FileDataID starts in one column.
        Assert.Equal("Skin 0         900       unknown file", references[0]);
        Assert.Equal("Anim 195 195.0 1195      unknown file", references[199]);
        Assert.Equal("Anim 5 5.0     1005      unknown file", references[9]);
        Assert.Equal("54 more not shown (first 200 of 254).", references[^1]);
        Assert.All(Lines(nav), l => Assert.True(l.Length <= 80, l));

        var small = await OpenAsync(540, referenceLimit: 3);
        Assert.Equal("251 more not shown (first 3 of 254).", Section(small)[^1]);
    }

    [Fact]
    public async Task CountsOfOneAreSingular()
    {
        Add(new FakeFile(550, Content: Chunked(274, ("SFID", Words32(900)), ("TXID", Words32(700, 0)), ("BFID", Words32(600)),
            ("AFID", Afid((60, 0, 800)))), Storage: FileStorage.Stored),
            new FakeFile(900, Content: Skin(), Storage: FileStorage.Stored));
        var section = Section(await OpenAsync(550));
        Assert.Contains("Skins        1 main profile", section);
        Assert.Contains("Animations   1 AFID entry, 1 distinct file, 0 inline in the M2 (id 0)", section);
        Assert.Contains("Bone files   1 BFID entry", section);
        Assert.Contains("Skin 0       4 vertex lookup, 3 triangle indices, 1 submesh, 1 batch", section);
        Assert.Contains("References   5 declared, by kind", section);
    }

    [Fact]
    public async Task AReloadAfterAskingForMoreReadsTheModelAgain()
    {
        Add(new FakeFile(560, Content: Chunked(274, ("SFID", Words32(900, 901)), ("TXID", Words32(700, 0))), Storage: FileStorage.Stored),
            new FakeFile(901, Content: Skin()));
        var nav = await OpenAsync(560);
        Assert.Contains("LOD skin 0    901       found, stored no, no name", Section(nav));

        // More asked for, then a reload (as after g ensured a referenced file) before that load ran: the section is read fresh.
        nav.Current.View.More();
        memory.SetStorage(Build, 901, FileStorage.Stored);
        nav.Reload();
        await nav.Loading.WaitAsync(Guard);
        Assert.Contains("LOD skin 0    901       found, stored yes, no name", Section(nav));
    }

    [Theory]
    [InlineData(11u, "creature display")]
    [InlineData(13u, "creature display")]
    [InlineData(15u, "guild emblem/banner")]
    [InlineData(17u, "guild emblem/banner")]
    [InlineData(1u, "character or item data")]
    [InlineData(14u, "character or item data")]
    public void ATextureWithNoFileSaysWhereItsTypeComesFrom(uint type, string from)
    {
        var reference = new VibeMiner.Formats.Models.ModelReference(VibeMiner.Formats.Models.ModelReferenceKind.Texture, 1, 0, null,
            VibeMiner.Formats.Models.ModelReferenceResolution.None, VibeMiner.Formats.Models.ModelReferenceState.ResolvedElsewhere, null, TextureType: type);
        Assert.Equal($"Texture 1     0         resolved elsewhere (type {type}: {from})", FileView.ReferenceLine(reference));
    }
}
