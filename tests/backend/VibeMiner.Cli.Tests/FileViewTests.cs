using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.Files.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The file detail: stat fields, the hex view with its holes and failures, reading further, and export.</summary>
public sealed class FileViewTests : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly BuildRef Build = new("wow", new string('a', 32), new string('1', 32), "us");
    private static readonly FilesTarget Target = new(Build, "enUS");
    private const string Key = "0123456789ABCDEF";

    private readonly string data = Directory.CreateTempSubdirectory("vm-file-view-").FullName;
    private readonly string exports;
    private readonly FakeStores stores = new();
    private readonly InMemoryFileSystem memory = new();
    private readonly FlakyFileSystem files;
    private readonly BrowseSession session;
    private readonly List<Navigator> navigators = [];

    public FileViewTests()
    {
        exports = Directory.CreateDirectory(Path.Combine(data, "out")).FullName;
        files = new FlakyFileSystem(memory);
        session = new BrowseSession(stores, new DataFolder(data, DataFolderSource.Override), Path.Combine(data, ObjectStore.DefaultFolderName))
        {
            FileSystem = (_, _, _) => files,
        };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var nav in navigators) await nav.DisposeAsync().AsTask().WaitAsync(Guard);
        await session.DisposeAsync();
        Directory.Delete(data, recursive: true);
    }

    private void Add(params FakeFile[] fakes) => memory.Add(Build, ["enUS"], fakes);

    private static byte[] Bytes(int length, byte fill = 0x41) => Enumerable.Repeat(fill, length).ToArray();

    /// <summary>The file as the list would give it: a stat through the same file system.</summary>
    private async Task<BuildFile> ListedAsync(uint id)
    {
        await using var lease = await session.OpenFilesAsync(Target, default);
        return (await lease.Files!.StatAsync(FileRef.Id(id), default)).File ?? new BuildFile(id, null, FilePathSource.None, null, new string('0', 32),
            FileFormat.Unknown, FileFormatSource.None, FileStorage.NotStored, false);
    }

    private async Task<(Navigator Nav, FileView View)> OpenAsync(uint id, int? shownLimit = null)
    {
        var listed = await ListedAsync(id);
        var view = shownLimit is { } limit ? new FileView(Target, listed) { ShownLimit = limit } : new FileView(Target, listed);
        var registry = new ViewRegistry().Add(new("file", "the file", () => view));
        var nav = new Navigator(registry, session, a => a());
        navigators.Add(nav);
        Assert.True(nav.Go("file"));
        await nav.Loading.WaitAsync(Guard);
        Assert.Null(nav.Current.Error);
        return (nav, view);
    }

    private static string[] Lines(Navigator nav) => nav.Current.Content!.Text!.Split('\n');

    /// <summary>The lines after the stat block (everything after the first empty line).</summary>
    private static string[] Body(Navigator nav)
    {
        var lines = Lines(nav);
        return lines[(Array.IndexOf(lines, "") + 1)..];
    }

    /// <summary>A status line with a path: whole when it fits 80 columns, else the path cut from the left to fit.</summary>
    private static void AssertLine(string before, string path, string after, string status)
    {
        var whole = before + path + after;
        if (whole.Length <= 80) { Assert.Equal(whole, status); return; }
        Assert.Equal(80, status.Length);
        Assert.StartsWith(before + "...", status, StringComparison.Ordinal);
        Assert.EndsWith(path[^20..] + after, status, StringComparison.Ordinal);
    }

    private static int HexCount(Navigator nav) =>
        Body(nav).Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[0-9a-f]{8}  [0-9a-f]{2} "));

    private static async Task MoreAsync(Navigator nav)
    {
        Assert.True(nav.More());
        await nav.Loading.WaitAsync(Guard);
    }

    // ---------------------------------------------------------------- stat and hex

    [Fact]
    public async Task TheStatComesFreshAndSaysWhereEachFieldCameFrom()
    {
        var content = Bytes(100);
        Add(new FakeFile(100, Content: content, Storage: FileStorage.Stored, Encrypted: true));
        memory.Names.Use(new Dictionary<uint, string> { [100] = "world/maps/thing.m2" });
        var (nav, view) = await OpenAsync(100);

        Assert.Equal("thing.m2", view.Title);
        Assert.Equal(Target, view.Target);
        Assert.Equal(100u, view.File.FileDataId);
        var lines = Lines(nav);
        Assert.Equal("FileDataID   100", lines[0]);
        Assert.Equal("Lookup       found", lines[1]);
        Assert.Equal("Path         world/maps/thing.m2 (community listfile name, not Blizzard data)", lines[2]);
        Assert.Equal("Size         100 bytes decoded (100 B)", lines[3]);
        Assert.Equal($"Content key  {Convert.ToHexStringLower(MD5.HashData(content))}", lines[4]);
        Assert.Equal("Format       M2 (from the name's extension)", lines[5]);
        Assert.Equal("Stored       yes", lines[6]);
        Assert.Equal("Encrypted    yes (ROOT's flag, a hint; reading tells)", lines[7]);
        Assert.Equal("wow aaaaaaaa enUS. Shown 100 B of 100 B.", nav.Current.Content!.Note);
        Assert.All(lines, l => Assert.True(l.Length <= 80, l));
    }

    [Fact]
    public async Task ALongNameIsCutFromTheLeftToEightyColumns()
    {
        var path = "world/" + new string('d', 120) + "/thing.m2";
        Add(new FakeFile(101, Content: Bytes(10), Storage: FileStorage.Stored));
        memory.Names.Use(new Dictionary<uint, string> { [101] = path });
        var (nav, _) = await OpenAsync(101);
        var line = Lines(nav)[2];
        Assert.Equal(80, line.Length);
        Assert.StartsWith("Path         ...", line, StringComparison.Ordinal);
        Assert.EndsWith("dd/thing.m2 (community listfile name, not Blizzard data)", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileWithoutANameIsTitledByIdAndSaysSo()
    {
        Add(new FakeFile(7, Size: 10));
        var (nav, view) = await OpenAsync(7);
        Assert.Equal("7", view.Title);
        Assert.Contains("Path         none (names off or not loaded, or the listfile doesn't name it)", Lines(nav));
        Assert.Contains("Stored       no (browse reads only stored bytes; g fetches the file)", Lines(nav));
    }

    [Fact]
    public async Task HexLinesHaveOffsetsBytesAndAnAsciiColumnAndAShortLastLine()
    {
        byte[] content = [.. "ABCDEFGHIJKLMNOP"u8, 0x00, 0x7f, 0x51, 0x52];
        Add(new FakeFile(1, Content: content, Storage: FileStorage.Stored));
        var (nav, _) = await OpenAsync(1);
        Assert.Equal(
        [
            "00000000  41 42 43 44 45 46 47 48  49 4a 4b 4c 4d 4e 4f 50  |ABCDEFGHIJKLMNOP|",
            "00000010  00 7f 51 52" + new string(' ', 39) + "|..QR|",
            "End of the file.",
        ], Body(nav));
        Assert.False(nav.Current.View.HasMore);
    }

    [Fact]
    public async Task AHoleInTheMiddleIsItsOwnLineAndNeverZeros()
    {
        var content = Bytes(48);
        Array.Fill(content, (byte)0, 16, 16);
        Add(new FakeFile(2, Content: content, Storage: FileStorage.Stored, Holes: [new FileHole(16, 16, Key)]));
        var (nav, _) = await OpenAsync(2);
        var row = "41 41 41 41 41 41 41 41  41 41 41 41 41 41 41 41  |AAAAAAAAAAAAAAAA|";
        Assert.Equal(
        [
            "00000000  " + row,
            $"00000010-0000001f  encrypted, needs TACT key {Key} (16 bytes)",
            "00000020  " + row,
            "End of the file.",
        ], Body(nav));
        Assert.DoesNotContain(" 00", string.Join('\n', Body(nav)));
    }

    [Fact]
    public async Task AWholeFileHoleIsProtectedAndShowsNoBytes()
    {
        Add(new FakeFile(3, Content: Bytes(4096, 0), Storage: FileStorage.Stored, Holes: [new FileHole(0, 4096, Key)]));
        var (nav, _) = await OpenAsync(3);
        Assert.Equal([$"00000000-00000fff  encrypted, needs TACT key {Key} (4,096 bytes)", "End of the file."], Body(nav));
    }

    [Fact]
    public async Task AHoleAcrossWindowsIsOneLine()
    {
        Add(new FakeFile(4, Content: Bytes(8192 + 16), Storage: FileStorage.Stored, Holes: [new FileHole(4000, 200, Key)]));
        var (nav, _) = await OpenAsync(4);
        await MoreAsync(nav);
        var body = Body(nav);
        Assert.Single(body, l => l.Contains("TACT key"));
        Assert.Contains($"00000fa0-00001067  encrypted, needs TACT key {Key} (200 bytes)", body);
        // The bytes after the hole run on from 0x1068, across the window boundary at 0x2000 once it is read.
        Assert.Contains(body, l => l.StartsWith("00001068  ", StringComparison.Ordinal));
        Assert.Contains(body, l => l.StartsWith("00001ff8  ", StringComparison.Ordinal));
        await MoreAsync(nav);
        body = Body(nav);
        Assert.Contains(body, l => l.StartsWith("00002008  41 41 41 41 41 41 41 41" + new string(' ', 25) + "  |AAAAAAAA|", StringComparison.Ordinal));
        Assert.Equal("End of the file.", body[^1]);
    }

    [Fact]
    public async Task AnEmptyFileSaysSo()
    {
        Add(new FakeFile(5, Content: [], Storage: FileStorage.Stored));
        var (nav, _) = await OpenAsync(5);
        Assert.Equal(["The file is empty (0 bytes)."], Body(nav));
        Assert.False(nav.Current.View.HasMore);
    }

    [Fact]
    public async Task FilesNotFoundSaySoInsteadOfBytes()
    {
        Add(new FakeFile(6, Locales: ["deDE"]), new FakeFile(8, NotInEncoding: true));
        var (unknown, _) = await OpenAsync(12345);
        Assert.Contains("Lookup       unknown file: this build doesn't list FileDataID 12345.", Lines(unknown));
        var (locale, _) = await OpenAsync(6);
        Assert.Contains("Lookup       not in locale: the build lists it, but not for enUS.", Lines(locale));
        var (encoding, _) = await OpenAsync(8);
        Assert.Contains("Lookup       not in encoding", Lines(encoding));
        Assert.Equal(["No bytes: ENCODING has no encoded copy of this file."], Body(encoding));
        foreach (var nav in new[] { unknown, locale, encoding }) Assert.False(nav.Current.View.HasMore);
    }

    [Fact]
    public async Task ANotStoredFileReadsAsNeedsNetworkAndNothingIsFetched()
    {
        Add(new FakeFile(10, Size: 5000));
        var (nav, _) = await OpenAsync(10);
        Assert.Equal(["00000000  not stored (OBJECT_ACCESS_NEEDS_NETWORK); g fetches it"], Body(nav));
        Assert.False(nav.Current.View.HasMore);
        Assert.Equal(FileStorage.NotStored, (await ListedAsync(10)).Storage);
        Assert.Empty(stores.Source.Observed);
    }

    [Theory]
    [InlineData(FileReadState.Corrupt, "BLTE_CHUNK_CHECKSUM", "Corrupt (BLTE_CHUNK_CHECKSUM): bad chunk")]
    [InlineData(FileReadState.Unsupported, "BLTE_MODE", "Unsupported (BLTE_MODE): bad chunk")]
    [InlineData(FileReadState.WrongKey, "BLTE_WRONG_KEY", "WrongKey: a known TACT key didn't decrypt it (BLTE_WRONG_KEY)")]
    [InlineData(FileReadState.StoreFailed, "OBJECT_STORE_IO", "StoreFailed (OBJECT_STORE_IO): bad chunk")]
    [InlineData(FileReadState.Unavailable, "CDN_DOWN", "Unavailable (CDN_DOWN): bad chunk")]
    [InlineData(FileReadState.Missing, "CDN_404", "Missing: the CDN answered 404 at 2026-10-31 12:00 UTC (CDN_404)")]
    public async Task EachFailedReadIsOneLineWithItsCode(FileReadState state, string code, string expected)
    {
        Add(new FakeFile(11, Content: Bytes(10), ReadFailure: new FakeReadFailure(state, code, "bad chunk",
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero))));
        var (nav, _) = await OpenAsync(11);
        Assert.Equal([$"00000000  {expected}"], Body(nav));
        Assert.False(nav.Current.View.HasMore);
    }

    [Fact]
    public async Task ALongFailureMessageIsCutToEightyColumnsAndNoMessageLeavesNoColon()
    {
        Add(new FakeFile(12, Content: Bytes(10), ReadFailure: new FakeReadFailure(FileReadState.Corrupt, "BLTE_BAD", new string('m', 200))));
        var (nav, _) = await OpenAsync(12);
        var line = Assert.Single(Body(nav));
        Assert.Equal(80, line.Length);
        Assert.StartsWith("00000000  Corrupt (BLTE_BAD): mmm", line, StringComparison.Ordinal);
        Assert.EndsWith("...", line, StringComparison.Ordinal);

        Assert.Equal("Unavailable (CDN_DOWN)", FileView.ReadFailure(FileRead.Failed(FileReadState.Unavailable, 0, "CDN_DOWN", null)));
        Assert.Equal("Unavailable", FileView.ReadFailure(FileRead.Failed(FileReadState.Unavailable, 0, null, null)));
    }

    // ---------------------------------------------------------------- reading further

    [Fact]
    public async Task MoreAddsTheNextWindowAndReadsOnlyIt()
    {
        Add(new FakeFile(20, Content: Bytes(10000), Storage: FileStorage.Stored));
        var (nav, view) = await OpenAsync(20);
        Assert.Equal(256, HexCount(nav));
        Assert.Equal("Down or n reads the next 4 KiB.", Body(nav)[^1]);
        Assert.True(view.HasMore);
        nav.Current.Selected = 30;

        await MoreAsync(nav);
        Assert.Equal(512, HexCount(nav));
        Assert.Equal(30, nav.Current.Selected);

        // n does the same as Down at the end.
        Assert.True(nav.TryAction('n'));
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("End of the file.", Body(nav)[^1]);
        Assert.StartsWith("00002700  ", Body(nav)[^2], StringComparison.Ordinal);
        Assert.False(view.HasMore);
        Assert.False(nav.More());
        Assert.True(nav.TryAction('n'));
        Assert.Equal("Nothing more to read here.", nav.Message);
        Assert.Equal([1, 1, 1], new long[] { 0, 4096, 8192 }.Select(files.Reads));
        Assert.Contains("Down on the last line", nav.Help());
    }

    [Fact]
    public async Task TheHexStopsAtTheCapWithALineSayingSo()
    {
        Add(new FakeFile(21, Content: Bytes(5 * 4096), Storage: FileStorage.Stored));
        var (nav, view) = await OpenAsync(21, shownLimit: 2 * 4096);
        await MoreAsync(nav);
        Assert.False(view.HasMore);
        Assert.Equal("Hex stops at 8.0 KiB; e exports the whole file.", Body(nav)[^1]);
        Assert.False(nav.More());
        Assert.Equal(0, files.Reads(8192));
        Assert.Equal(FileView.MaxShown, new FileView(Target, await ListedAsync(21)).ShownLimit);
    }

    [Fact]
    public async Task AFailedWindowIsNotKeptAndTheNextLoadRetriesIt()
    {
        Add(new FakeFile(22, Content: Bytes(8192), Storage: FileStorage.Stored));
        files.FailAt = 4096;
        var (nav, view) = await OpenAsync(22);
        await MoreAsync(nav);
        Assert.Equal("00001000  StoreFailed (OBJECT_STORE_IO): flaky", Body(nav)[^1]);
        Assert.False(view.HasMore);

        files.FailAt = null;
        Assert.True(nav.TryAction('n'));
        await nav.Loading.WaitAsync(Guard);
        Assert.Equal("End of the file.", Body(nav)[^1]);
        Assert.Equal(512, HexCount(nav));
        Assert.Equal(1, files.Reads(0));
        Assert.Equal(2, files.Reads(4096));
    }

    // ---------------------------------------------------------------- export

    private async Task<string> ExportAsync(Navigator nav, string path)
    {
        Assert.True(nav.TryAction('e'));
        Assert.Equal("export to:", nav.Asking!.Question);
        nav.Answer(path);
        await nav.Tasks.Current.WaitAsync(Guard);
        await nav.Loading.WaitAsync(Guard);
        return nav.Tasks.Status;
    }

    [Fact]
    public async Task ExportWritesACompleteFileThroughATempFile()
    {
        var content = Bytes(1234, 0x5a);
        Add(new FakeFile(30, Content: content, Storage: FileStorage.Stored));
        memory.Names.Use(new Dictionary<uint, string> { [30] = "interface/icons/x.blp" });
        var (nav, _) = await OpenAsync(30);
        Assert.True(nav.TryAction('e'));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "x.blp"), nav.Asking!.Initial);
        nav.CancelAsk();

        var target = Path.Combine(exports, "x.blp");
        var status = await ExportAsync(nav, target);
        AssertLine($"export: wrote 1,234 bytes to ", target, "", status);
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        Assert.Equal([target], Directory.GetFiles(exports));
    }

    [Fact]
    public async Task TheInitialNameIsTheIdWithTheFormatsExtension()
    {
        Add(new FakeFile(31, Content: [.. "OggS"u8, 0, 1, 2, 3], Storage: FileStorage.Stored), new FakeFile(32, Size: 3));
        var (ogg, _) = await OpenAsync(31);
        Assert.Contains("Format       Ogg (from the bytes)", Lines(ogg));
        Assert.True(ogg.TryAction('e'));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "31.ogg"), ogg.Asking!.Initial);
        var (bare, _) = await OpenAsync(32);
        Assert.True(bare.TryAction('e'));
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "32"), bare.Asking!.Initial);
        Assert.Equal("m2", FileView.Extension(FileFormat.M2Chunked));
        Assert.Null(FileView.Extension(FileFormat.Unknown));
    }

    [Fact]
    public async Task ExportNeverOverwritesAnExistingFile()
    {
        Add(new FakeFile(33, Content: Bytes(10), Storage: FileStorage.Stored));
        var (nav, _) = await OpenAsync(33);
        var target = Path.Combine(exports, "taken");
        await File.WriteAllTextAsync(target, "old");
        AssertLine("export: ", target, " exists; nothing written.", await ExportAsync(nav, target));
        Assert.Equal("old", await File.ReadAllTextAsync(target));
        Assert.Equal([target], Directory.GetFiles(exports));
    }

    [Fact]
    public async Task ExportToAMissingFolderSaysSo()
    {
        Add(new FakeFile(34, Content: Bytes(10), Storage: FileStorage.Stored));
        var (nav, _) = await OpenAsync(34);
        var folder = Path.Combine(exports, "nope");
        Assert.StartsWith("export: no folder ", await ExportAsync(nav, Path.Combine(folder, "f")), StringComparison.Ordinal);
        Assert.EndsWith("; nothing written.", nav.Tasks.Status, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ExportRefusesPartialAndProtectedFiles()
    {
        Add(new FakeFile(35, Content: Bytes(64), Storage: FileStorage.Stored, Holes: [new FileHole(0, 16, Key), new FileHole(32, 16, "FEDCBA9876543210")]),
            new FakeFile(36, Content: Bytes(64), Storage: FileStorage.Stored, Holes: [new FileHole(0, 64, Key)]));
        var (partial, _) = await OpenAsync(35);
        Assert.Equal("export: 2 encrypted ranges need TACT keys (never zero-filled); nothing written.",
            await ExportAsync(partial, Path.Combine(exports, "p")));
        var (whole, _) = await OpenAsync(36);
        Assert.Equal("export: 1 encrypted range needs a TACT key (never zero-filled); nothing written.",
            await ExportAsync(whole, Path.Combine(exports, "w")));
        Assert.Empty(Directory.GetFileSystemEntries(exports));
    }

    [Fact]
    public async Task ExportOfANotStoredFileWritesNothing()
    {
        Add(new FakeFile(37, Size: 100));
        var (nav, _) = await OpenAsync(37);
        Assert.Equal("export: not stored; nothing fetched or written (OBJECT_ACCESS_NEEDS_NETWORK).",
            await ExportAsync(nav, Path.Combine(exports, "n")));
        Assert.Empty(Directory.GetFileSystemEntries(exports));
    }

    [Fact]
    public async Task ExportOfAnotherFailureIsOneLineWithTheCode()
    {
        Add(new FakeFile(38, Content: Bytes(10), ReadFailure: new FakeReadFailure(FileReadState.Corrupt, "BLTE_BAD")));
        var (nav, _) = await OpenAsync(38);
        Assert.Equal("export: Corrupt (BLTE_BAD); nothing written.", await ExportAsync(nav, Path.Combine(exports, "c")));
        Assert.Empty(Directory.GetFileSystemEntries(exports));
    }

    [Fact]
    public async Task CancelDeletesTheTempFile()
    {
        Add(new FakeFile(39, Content: Bytes(100), Storage: FileStorage.Stored));
        var (nav, view) = await OpenAsync(39);
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.AfterTempWritten = async (temp, ct) => { entered.TrySetResult(temp); await Task.Delay(Timeout.Infinite, ct); };
        Assert.True(nav.TryAction('e'));
        nav.Answer(Path.Combine(exports, "x"));
        var temp = await entered.Task.WaitAsync(Guard);
        Assert.True(File.Exists(temp));
        Assert.Equal(exports, Path.GetDirectoryName(temp));

        Assert.True(nav.Tasks.Cancel());
        await nav.Tasks.Current.WaitAsync(Guard);
        Assert.Equal("export canceled.", nav.Tasks.Status);
        Assert.Empty(Directory.GetFileSystemEntries(exports));
    }

    /// <summary>Wraps the in-memory file system: counts range reads by offset and can fail the one at <see cref="FailAt"/>.</summary>
    private sealed class FlakyFileSystem(IFileSystem inner) : IFileSystem
    {
        private readonly Dictionary<long, int> reads = [];
        public long? FailAt { get; set; }
        public IFileNames Names => inner.Names;
        public int Reads(long offset) { lock (reads) return reads.GetValueOrDefault(offset); }

        public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var result = await inner.ForAsync(build, locale, cancellationToken);
            return result.Files is { } f ? result with { Files = new Handle(this, f) } : result;
        }

        private sealed class Handle(FlakyFileSystem owner, IBuildFiles inner) : IBuildFiles
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
            public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
            public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
            public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
            public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) => inner.OpenReadAsync(file, cancellationToken);
            public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
                inner.EnsureAsync(selection, priority, progress, cancellationToken);

            public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken)
            {
                lock (owner.reads) owner.reads[offset] = owner.reads.GetValueOrDefault(offset) + 1;
                return owner.FailAt == offset
                    ? Task.FromResult(FileRead.Failed(FileReadState.StoreFailed, offset, "OBJECT_STORE_IO", "flaky"))
                    : inner.ReadRangeAsync(file, offset, length, cancellationToken);
            }
        }
    }
}
