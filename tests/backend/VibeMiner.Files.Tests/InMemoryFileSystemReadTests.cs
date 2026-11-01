using VibeMiner.Builds;
using VibeMiner.Files.Testing;
using Xunit;

namespace VibeMiner.Files.Tests;

/// <summary>
/// The fake's own read rules: what <see cref="InMemoryFileSystem.Add"/> accepts, content setting the size, the order its
/// scripted failures apply in, reads storing the file, and its read limit. The general read behaviour both sides share goes in
/// <see cref="FilesContractTests"/>.
/// </summary>
public sealed class InMemoryFileSystemReadTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const string Key = "0123456789ABCDEF";
    private const string OtherKey = "FEDCBA9876543210";
    private static readonly IReadOnlyList<string> EnUs = ["enUS"];

    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 1))];

    private static async Task<IBuildFiles> OpenAsync(InMemoryFileSystem fs) =>
        (await fs.ForAsync(FilesFixture.Build, "enUS", Ct)).Files!;

    private static InMemoryFileSystem With(FakeFile file, long maxReadBytes = 256L << 20) =>
        new InMemoryFileSystem(maxReadBytes: maxReadBytes).Add(FilesFixture.Build, EnUs, [file]);

    public static TheoryData<IReadOnlyList<FileHole>> BadHoles => new()
    {
        new[] { new FileHole(0, 0, Key) },
        new[] { new FileHole(-1, 4, Key) },
        new[] { new FileHole(60, 8, Key) },
        new[] { new FileHole(8, 8, Key), new FileHole(0, 4, OtherKey) },
        new[] { new FileHole(0, 8, Key), new FileHole(4, 8, OtherKey) },
        new[] { new FileHole(0, 8, Key), new FileHole(8, 8, Key) },
        new[] { new FileHole(0, 8, "0123456789abcdef") },
        new[] { new FileHole(0, 8, "0123456789ABCDE") },
        new[] { new FileHole(0, 8, "0123456789ABCDEG") },
    };

    [Theory]
    [MemberData(nameof(BadHoles))]
    public void AddRejectsHolesThatAreEmptyOutsideUnorderedOverlappingNotMaximalOrBadlyNamed(IReadOnlyList<FileHole> holes) =>
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Content: Bytes(64), Holes: holes)));

    [Fact]
    public void AddRejectsHolesWithoutContent() =>
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Holes: [new FileHole(0, 4, Key)])));

    [Fact]
    public async Task AddAcceptsAdjacentHolesOfDifferentKeysAndAHoleEndingAtTheEnd()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(16), Holes: [new(0, 8, Key), new(8, 8, OtherKey)])));
        var read = await files.GetAsync(100, Ct);
        Assert.Equal(FileReadState.Protected, read.State);
        Assert.Equal([new(0, 8, Key), new(8, 8, OtherKey)], read.Holes);
    }

    [Theory]
    [InlineData(FileReadState.Complete)]
    [InlineData(FileReadState.Partial)]
    [InlineData(FileReadState.Protected)]
    [InlineData(FileReadState.TooLarge)]
    [InlineData(FileReadState.UnknownFile)]
    [InlineData(FileReadState.NotInLocale)]
    [InlineData(FileReadState.NotInEncoding)]
    public void AddRejectsAScriptedFailureThatIsntAReadFailure(FileReadState state) =>
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, ReadFailure: new(state, "CODE"))));

    [Fact]
    public async Task AScriptedFailureWinsOverContent()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.Corrupt, "FAKE_CORRUPT"))));
        var read = await files.ReadRangeAsync(100, 2, 4, Ct);
        Assert.Equal((FileReadState.Corrupt, "FAKE_CORRUPT", 2L), (read.State, read.Code, read.Offset));
        var open = await files.OpenReadAsync(100, Ct);
        Assert.Equal((FileReadState.Corrupt, null), (open.State, open.Stream));
    }

    [Fact]
    public async Task TooLargeComesFromTheListedSizeBeforeAScriptedFailureOrAFetch()
    {
        var files = await OpenAsync(With(new FakeFile(100, Size: 32, ReadFailure: new(FileReadState.Corrupt, "FAKE_CORRUPT")), maxReadBytes: 16));
        Assert.Equal(FileReadState.TooLarge, (await files.GetAsync(100, Ct)).State);
        Assert.Equal(FileReadState.Corrupt, (await files.ReadRangeAsync(100, 0, 16, Ct)).State);
        Assert.Equal(FileReadState.Corrupt, (await files.OpenReadAsync(100, Ct)).State);
    }

    [Theory]
    [InlineData(32L, 4)]
    [InlineData(40L, 4)]
    [InlineData(4L, 0)]
    public async Task AnEmptyClampedRangeIsCompleteAtOnceWithoutContentOrFetch(long offset, int length)
    {
        var files = await OpenAsync(With(new FakeFile(100, Size: 32, ReadFailure: new(FileReadState.Corrupt, "FAKE_CORRUPT"))));
        var read = await files.ReadRangeAsync(100, offset, length, Ct);
        Assert.Equal((FileReadState.Complete, Math.Min(offset, 32), 0L, 0, 0), (read.State, read.Offset, read.Length, read.Segments.Count, read.Holes.Count));
        var none = await OpenAsync(With(new FakeFile(100, Size: 32)));
        Assert.Equal(FileReadState.Complete, (await none.ReadRangeAsync(100, offset, length, Ct)).State);
        Assert.Equal(FileStorage.NotStored, (await none.StatAsync(100, Ct)).File!.Storage);
    }

    [Fact]
    public async Task AnEmptyFileReadsWholeWithoutAFetch()
    {
        var files = await OpenAsync(With(new FakeFile(100, Size: 0)));
        var read = await files.GetAsync(100, Ct);
        Assert.Equal((FileReadState.Complete, 0, 0L), (read.State, read.Bytes!.Value.Length, read.Length));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
        Assert.Equal(FileReadState.NeedsNetwork, (await files.OpenReadAsync(100, Ct)).State);
    }

    [Fact]
    public async Task ContentSetsTheListedAndStatSize()
    {
        var files = await OpenAsync(With(new FakeFile(100, Size: 999, Content: Bytes(40))));
        Assert.Equal(40, (await files.StatAsync(100, Ct)).File!.Size);
        await foreach (var f in files.ListAsync(FileFilter.All, Ct)) Assert.Equal(40, f.Size);
    }

    [Fact]
    public async Task AFileWithoutContentNeedsTheNetworkAndStaysNotStored()
    {
        var files = await OpenAsync(With(new FakeFile(100, Size: 40)));
        var read = await files.GetAsync(100, Ct);
        Assert.Equal((FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (read.State, read.Code));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
    }

    [Fact]
    public async Task AddCopiesContentSoChangingTheCallersArrayChangesNothing()
    {
        var content = Bytes(8);
        var files = await OpenAsync(With(new FakeFile(100, Content: content)));
        content[0] = 0xFF;
        var read = await files.GetAsync(100, Ct);
        Assert.Equal(Bytes(8), read.Bytes!.Value.ToArray());
    }

    [Theory]
    [InlineData("get")]
    [InlineData("range")]
    [InlineData("open")]
    public async Task AReadStoresTheFile(string how)
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(32))));
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
        if (how == "get") await files.GetAsync(100, Ct);
        else if (how == "range") await files.ReadRangeAsync(100, 4, 4, Ct);
        else await using ((await files.OpenReadAsync(100, Ct)).Stream!) { }
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(100, Ct)).File!.Storage);
        var listed = new List<BuildFile>();
        await foreach (var f in files.ListAsync(new FileFilter { Storage = new HashSet<FileStorage> { FileStorage.Stored } }, Ct)) listed.Add(f);
        Assert.Equal([100u], listed.Select(f => f.FileDataId));
    }

    [Fact]
    public async Task AFailedReadLeavesTheFileNotStored()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(32)), maxReadBytes: 16));
        Assert.Equal(FileReadState.TooLarge, (await files.GetAsync(100, Ct)).State);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
    }

    [Fact]
    public async Task MaxReadBytesBoundsWholeReadsAndRangesButNotStreams()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(32)), maxReadBytes: 16));
        var get = await files.GetAsync(100, Ct);
        Assert.Equal((FileReadState.TooLarge, "FILES_READ_TOO_LARGE", 0L), (get.State, get.Code, get.Length));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => files.ReadRangeAsync(100, 0, 17, Ct));
        Assert.Equal(Bytes(32)[16..], (await files.ReadRangeAsync(100, 16, 16, Ct)).Bytes!.Value.ToArray());
        var open = await files.OpenReadAsync(100, Ct);
        await using var stream = open.Stream!;
        Assert.Equal((FileReadState.Complete, 32L), (open.State, stream.Length));
    }

    [Fact]
    public async Task AFileAtExactlyMaxReadBytesReadsWhole()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(16)), maxReadBytes: 16));
        Assert.Equal(Bytes(16), (await files.GetAsync(100, Ct)).Bytes!.Value.ToArray());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData((long)int.MaxValue + 1)]
    public void MaxReadBytesOutOfRangeIsRejected(long max) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryFileSystem(maxReadBytes: max));

    [Fact]
    public void AddRejectsAStoredFileWithoutContent()
    {
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Size: 8, Storage: FileStorage.Stored)));
        _ = With(new FakeFile(100, Storage: FileStorage.Stored, Content: Bytes(8)));
    }

    [Fact]
    public async Task OnlyAFileWithContentCanBeSetStored()
    {
        var fs = new InMemoryFileSystem().Add(FilesFixture.Build, EnUs, [new FakeFile(100, Size: 8), new FakeFile(200, Content: Bytes(8))]);
        Assert.Throws<ArgumentException>(() => fs.SetStorage(FilesFixture.Build, 100, FileStorage.Stored));
        fs.SetStorage(FilesFixture.Build, 200, FileStorage.Stored).SetStorage(FilesFixture.Build, 100, FileStorage.NotStored);
        var files = await OpenAsync(fs);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(200, Ct)).File!.Storage);
        fs.SetStorage(FilesFixture.Build, 200, FileStorage.NotStored);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(200, Ct)).File!.Storage);
    }

    [Fact]
    public async Task ContentKeyIsTheContentsMd5()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(8))));
        Assert.Equal(FilesFixture.ContentKeyOf(Bytes(8)), (await files.StatAsync(100, Ct)).File!.ContentKey);
        var given = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ContentKey: FilesFixture.ContentKeyOf(Bytes(8)).ToUpperInvariant())));
        Assert.Equal(FilesFixture.ContentKeyOf(Bytes(8)), (await given.StatAsync(100, Ct)).File!.ContentKey);
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Content: Bytes(8), ContentKey: new string('9', 32))));
        // Without content, the key is made from the FileDataID.
        var none = await OpenAsync(With(new FakeFile(100, Size: 8)));
        Assert.Equal(FilesFixture.ContentKeyOf(100), (await none.StatAsync(100, Ct)).File!.ContentKey);
    }

    [Theory]
    [InlineData(FileReadState.Missing, false)]
    [InlineData(FileReadState.NeedsNetwork, false)]
    [InlineData(FileReadState.Unavailable, false)]
    [InlineData(FileReadState.StoreFailed, false)]
    [InlineData(FileReadState.Corrupt, true)]
    [InlineData(FileReadState.Unsupported, true)]
    [InlineData(FileReadState.WrongKey, true)]
    public void AStoredFileCantFailWithALayer2State(FileReadState state, bool allowed)
    {
        var stored = new FakeFile(100, Storage: FileStorage.Stored, Content: Bytes(8), ReadFailure: new(state, "CODE"));
        if (allowed) _ = With(stored); else Assert.Throws<ArgumentException>(() => With(stored));
        var fs = With(stored with { Storage = FileStorage.NotStored });
        if (allowed) fs.SetStorage(FilesFixture.Build, 100, FileStorage.Stored);
        else Assert.Throws<ArgumentException>(() => fs.SetStorage(FilesFixture.Build, 100, FileStorage.Stored));
    }

    [Fact]
    public void AFailureOnStreamReadsNeedsContent()
    {
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Size: 8, ReadFailure: new(FileReadState.Unavailable, "CODE", OnStreamRead: true))));
        Assert.Throws<ArgumentException>(() => With(new FakeFile(100, Size: 8, ReadFailure: new(FileReadState.WrongKey, "CODE"))));
    }

    [Fact]
    public async Task ObservedAtIsPassedThroughAndMissingIsDatedByDefault()
    {
        var at = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.Unavailable, "CDN_TIMEOUT", ObservedAt: at))));
        Assert.Equal(at, (await files.GetAsync(100, Ct)).ObservedAt);
        Assert.Equal(at, (await files.OpenReadAsync(100, Ct)).ObservedAt);
        var unavailable = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.Unavailable, "CDN_TIMEOUT"))));
        Assert.Null((await unavailable.ReadRangeAsync(100, 0, 4, Ct)).ObservedAt);
        var missing = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.Missing, "CDN_NOT_FOUND"))));
        Assert.NotNull((await missing.GetAsync(100, Ct)).ObservedAt);
        var onRead = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.StoreFailed, "DISK", ObservedAt: at, OnStreamRead: true))));
        await using var stream = (await onRead.OpenReadAsync(100, Ct)).Stream!;
        var thrown = await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(new byte[4], Ct).AsTask());
        Assert.Equal((FileReadState.StoreFailed, "DISK", (DateTimeOffset?)at), (thrown.State, thrown.Code, thrown.ObservedAt));
    }

    [Fact]
    public async Task AStreamThatOpensWithAFailureOnReadsIsStoredButAFailedReadStoresNothing()
    {
        var files = await OpenAsync(With(new FakeFile(100, Content: Bytes(8), ReadFailure: new(FileReadState.Unavailable, "CODE", OnStreamRead: true))));
        Assert.Equal(FileReadState.Unavailable, (await files.GetAsync(100, Ct)).State);
        Assert.Equal(FileStorage.NotStored, (await files.StatAsync(100, Ct)).File!.Storage);
        await using ((await files.OpenReadAsync(100, Ct)).Stream!) { }
        Assert.Equal(FileStorage.Stored, (await files.StatAsync(100, Ct)).File!.Storage);
    }
}
