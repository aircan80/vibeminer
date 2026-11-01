using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Formats;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// How the real <see cref="FileSystem"/> finds formats: a local-only read of the first chunk, remembered per handle, bounded
/// per listing. Synthetic BLTE objects only; the build has a CDN context, so a fetch would be visible.
/// </summary>
public sealed class FormatWiringTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const uint EnUsBit = 0x2;
    private static readonly CdnContext Cdn = new(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"));

    private sealed record Setup(InMemoryObjectSource Objects, CountingObjectSource Counted, BuildFiles Files);

    private static FileVariant VariantOf(BlteObject o) =>
        new(EnUsBit, 0, new CdnKey(MD5.HashData(o.Decoded)), o.Decoded.Length, o.EKey, o.Encoded.Length, null);

    private static ObjectKey KeyOf(BlteObject o) => new(ObjectKeyType.Encoded, o.EKey.ToBytes());

    /// <summary>Files 1.. in order; each object stored or only fetchable.</summary>
    private static Task<Setup> OpenAsync(params (BlteObject Object, bool Stored)[] files) => OpenWithAsync(null, files);

    private static async Task<Setup> OpenWithAsync(FileSystemOptions? options, params (BlteObject Object, bool Stored)[] files)
    {
        var builds = new L3.InMemoryBuilds();
        var layer3 = files.Select((f, i) => new L3.FakeFile((uint)(i + 1), null, [VariantOf(f.Object)])).ToArray();
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(["enUS"], layer3.Length), layer3, Cdn));
        var objects = new InMemoryObjectSource();
        foreach (var (o, stored) in files)
            if (stored) objects.AddStored(KeyOf(o), o.Encoded); else objects.Add(KeyOf(o), o.Encoded);
        var counted = new CountingObjectSource(objects);
        var opened = await new FileSystem(builds, counted, options).ForAsync(FilesFixture.Build, "enUS", Ct);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return new(objects, counted, (BuildFiles)opened.Files!);
    }

    private static async Task<List<BuildFile>> ListAsync(IBuildFiles files, FileFilter? filter = null)
    {
        var all = new List<BuildFile>();
        await foreach (var f in files.ListAsync(filter ?? FileFilter.All, Ct)) all.Add(f);
        return all;
    }

    private static byte[] Blp(int length, int seed = 1) => [.. "BLP2"u8, .. BlteBuilder.Bytes(length - 4, seed)];

    /// <summary>Ten 1000-byte chunks, alternating raw and zlib; the file starts with a BLP magic.</summary>
    private static BlteObject TenChunks(int seed = 1)
    {
        var decoded = Blp(10_000, seed);
        return BlteBuilder.Chunked(Enumerable.Range(0, 10).Select(i =>
        {
            var part = decoded.AsSpan(i * 1000, 1000).ToArray();
            return i % 2 == 0 ? BlteChunkSpec.Raw(part) : BlteChunkSpec.Zlib(part);
        }).ToArray());
    }

    [Fact]
    public async Task Detection_decodes_only_the_first_chunk_from_the_store()
    {
        var o = TenChunks();
        var s = await OpenAsync((o, true));

        var file = Assert.Single(await ListAsync(s.Files));

        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (file.Format, file.FormatSource));
        Assert.Equal(1, s.Files.DecodedChunks);
        var layout = o.Parse();
        Assert.Equal([(0L, 8), (8L, o.HeaderLength - 8), (layout.Chunks[0].EncodedOffset, layout.Chunks[0].EncodedSize)], s.Counted.Ranges);
        Assert.All(s.Counted.Requests, r => Assert.Null(r.Cdn));
        Assert.Empty(s.Objects.Fetched);
    }

    [Fact]
    public async Task The_remembered_format_needs_no_second_read()
    {
        var s = await OpenAsync((TenChunks(), true));
        await ListAsync(s.Files);
        var reads = s.Counted.Ranges.Count;

        var stat = (await s.Files.StatAsync(1u, Ct)).File!;
        var again = Assert.Single(await ListAsync(s.Files));

        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (stat.Format, stat.FormatSource));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (again.Format, again.FormatSource));
        Assert.Equal(reads, s.Counted.Ranges.Count);
        Assert.Equal(1, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_whole_read_remembers_the_format_without_another_read()
    {
        var s = await OpenAsync((TenChunks(), false));
        var before = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileStorage.NotStored, FileFormat.Unknown, FileFormatSource.None), (before.Storage, before.Format, before.FormatSource));
        // A file that isn't stored isn't read for its format at all.
        Assert.Empty(s.Counted.Ranges);

        Assert.Equal(FileReadState.Complete, (await s.Files.GetAsync(1u, Ct)).State);
        var reads = s.Counted.Ranges.Count;
        var decoded = s.Files.DecodedChunks;

        var after = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileStorage.Stored, FileFormat.Blp, FileFormatSource.Magic), (after.Storage, after.Format, after.FormatSource));
        Assert.Equal(reads, s.Counted.Ranges.Count);
        Assert.Equal(decoded, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_corrupt_first_chunk_is_no_evidence_and_is_read_again_next_time()
    {
        // A zlib chunk whose payload isn't zlib: its checksum holds, its decode fails.
        var o = BlteBuilder.Chunked([BlteChunkSpec.Arbitrary([(byte)'Z', 1, 2, 3, 4, 5], 1000), BlteChunkSpec.Raw(Blp(1000))]);
        var good = TenChunks(2);
        var s = await OpenAsync((o, true), (good, true));

        var listed = await ListAsync(s.Files);

        Assert.Equal((uint[])[1u, 2], listed.Select(f => f.FileDataId));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (listed[0].Format, listed[0].FormatSource));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (listed[1].Format, listed[1].FormatSource));
        var decoded = s.Files.DecodedChunks;
        var stat = await s.Files.StatAsync(1u, Ct);
        Assert.Equal((FileLookupState.Found, FileFormatSource.None), (stat.State, stat.File!.FormatSource));
        // Not remembered: the store may hold a good copy later.
        Assert.Equal(decoded + 1, s.Files.DecodedChunks);
    }

    [Theory]
    [InlineData(BuildFiles.DetectionChunkBytes + 1)]  // decoded size over the cap
    [InlineData(BuildFiles.DetectionChunkBytes)]      // decoded size at the cap, encoded ('N' + bytes) one over
    public async Task A_first_chunk_over_the_detection_cap_is_not_decoded(int decodedSize)
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw(Blp(decodedSize)));
        var s = await OpenAsync((o, true));

        var file = Assert.Single(await ListAsync(s.Files));

        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (file.Format, file.FormatSource));
        Assert.Equal(0, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_first_chunk_under_the_detection_cap_is_decoded()
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw(Blp(BuildFiles.DetectionChunkBytes - 1)));
        var s = await OpenAsync((o, true));
        var file = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (file.Format, file.FormatSource));
    }

    private const ulong KeyA = 0x1122334455667788;
    private static readonly byte[] SecretA = BlteBuilder.Bytes(16, 41);

    [Fact]
    public async Task A_key_added_after_a_listing_opens_a_first_chunk_hole()
    {
        var keys = new TactKeys();
        var o = BlteBuilder.Chunked([BlteChunkSpec.Encrypted(KeyA, SecretA, [1, 2, 3, 4], BlteChunkSpec.Raw(Blp(500)))]);
        var s = await OpenWithAsync(new FileSystemOptions { Keys = keys }, (o, true));

        var locked = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (locked.Format, locked.FormatSource));
        Assert.Equal(0, s.Files.Remembered);

        keys.Add(KeyA, SecretA);
        var opened = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (opened.Format, opened.FormatSource));
    }

    [Fact]
    public async Task A_short_head_before_a_hole_is_not_remembered_as_unknown()
    {
        var keys = new TactKeys();
        var decoded = Blp(500);
        // "BL" readable, then "P2..." behind an unknown key: two bytes prove nothing, but aren't the whole head.
        var o = BlteBuilder.Chunked([BlteChunkSpec.Raw(decoded[..2]),
            BlteChunkSpec.Encrypted(KeyA, SecretA, [1, 2, 3, 4], BlteChunkSpec.Raw(decoded[2..]))]);
        var s = await OpenWithAsync(new FileSystemOptions { Keys = keys }, (o, true));

        var shortHead = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), (shortHead.Format, shortHead.FormatSource));
        Assert.Equal(0, s.Files.Remembered);

        keys.Add(KeyA, SecretA);
        var whole = Assert.Single(await ListAsync(s.Files));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (whole.Format, whole.FormatSource));
        Assert.Equal(1, s.Files.Remembered);
    }

    [Fact]
    public async Task Nothing_is_remembered_past_the_limit()
    {
        var files = Enumerable.Range(0, 5).Select(i => (BlteBuilder.Chunked([BlteChunkSpec.Raw(Blp(100, i))]), true)).ToArray();
        var s = await OpenAsync(files);
        s.Files.RememberLimit = 2;

        await ListAsync(s.Files);
        Assert.Equal(2, s.Files.Remembered);
        Assert.Equal(5, s.Files.DecodedChunks);

        // The two remembered files need no read; the other three are read again and still not added.
        var again = await ListAsync(s.Files);
        Assert.All(again, f => Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (f.Format, f.FormatSource)));
        Assert.Equal(2, s.Files.Remembered);
        Assert.Equal(8, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task Canceling_mid_listing_throws_and_leaves_no_detection_read_running()
    {
        var files = Enumerable.Range(0, 40).Select(i => (BlteBuilder.Chunked([BlteChunkSpec.Raw(Blp(100, i))]), true)).ToArray();
        var s = await OpenAsync(files);
        s.Objects.BeforeAnswer = (_, token) => Task.Delay(2, token);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in s.Files.ListAsync(FileFilter.All, cts.Token)) await cts.CancelAsync();
        });
        var asked = s.Counted.Ranges.Count;
        await Task.Delay(50);

        Assert.Equal(asked, s.Counted.Ranges.Count);
        Assert.True(asked < 3 * 40, $"{asked} reads: detection ran on past the cancel.");
    }

    [Fact]
    public async Task A_listing_runs_a_bounded_number_of_detection_reads_and_keeps_its_order()
    {
        var files = Enumerable.Range(0, 40).Select(i => (BlteBuilder.Chunked([BlteChunkSpec.Raw(Blp(100, i))]), true)).ToArray();
        var s = await OpenAsync(files);
        int inFlight = 0, most = 0;
        s.Objects.BeforeAnswer = async (_, token) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            while ((seen = Volatile.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
            try { await Task.Delay(2, token); }
            finally { Interlocked.Decrement(ref inFlight); }
        };

        var listed = await ListAsync(s.Files);

        Assert.Equal(Enumerable.Range(1, 40).Select(i => (uint)i), listed.Select(f => f.FileDataId));
        Assert.All(listed, f => Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (f.Format, f.FormatSource)));
        Assert.InRange(most, 2, BuildFiles.DetectionReads);
    }

    [Fact]
    public async Task A_reader_that_stops_early_leaves_no_detection_read_running()
    {
        var files = Enumerable.Range(0, 40).Select(i => (BlteBuilder.Chunked([BlteChunkSpec.Raw(Blp(100, i))]), true)).ToArray();
        var s = await OpenAsync(files);
        s.Objects.BeforeAnswer = (_, token) => Task.Delay(2, token);

        await foreach (var _ in s.Files.ListAsync(FileFilter.All, Ct)) break;
        var asked = s.Counted.Ranges.Count;
        await Task.Delay(50);

        Assert.Equal(asked, s.Counted.Ranges.Count);
        Assert.InRange(asked, 1, 3 * BuildFiles.DetectionReads);
    }

    [Fact]
    public async Task The_fake_falls_back_to_the_listed_name_by_the_same_rules()
    {
        byte[] chunked = [.. "MD21"u8, .. BitConverter.GetBytes(100u), .. "MD20"u8, .. BlteBuilder.Bytes(100, 3)];
        var fs = new InMemoryFileSystem().Add(FilesFixture.Build, ["enUS"],
        [
            new(1, "textures/a.blp", Content: Blp(300)),
            new(2, "sound/b.ogg", Storage: FileStorage.Stored, Content: BlteBuilder.Bytes(300, 4)),
            new(3, "creature/c/c.m2", Storage: FileStorage.Stored, Content: chunked),
            new(4, "creature/d/d.m2", 100),
            new(5, "textures/e.blp", Storage: FileStorage.Stored, Content: Blp(300, 5), ReadFailure: new(FileReadState.Corrupt, "BLTE_INVALID")),
            new(6, "textures/f.blp", Storage: FileStorage.Stored, Content: Blp(300, 6), Holes: [new(0, 10, "1122334455667788")]),
            new(7, "interface/g.lua", NotInEncoding: true),
            new(8, null, Storage: FileStorage.Stored, Content: BlteBuilder.Bytes(300, 8)),
        ]);
        fs.Names.Use(new Dictionary<uint, string>
        {
            [1] = "textures/a.blp", [2] = "sound/b.ogg", [3] = "creature/c/c.m2", [4] = "creature/d/d.m2", [5] = "textures/e.blp",
            [6] = "textures/f.blp", [7] = "interface/g.lua",
        });
        await using var files = (await fs.ForAsync(FilesFixture.Build, "enUS", Ct)).Files!;

        var formats = (await ListAsync(files)).ToDictionary(f => f.FileDataId, f => (f.Format, f.FormatSource));

        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), formats[1]);
        Assert.Equal((FileFormat.Ogg, FileFormatSource.Extension), formats[2]);
        Assert.Equal((FileFormat.M2Chunked, FileFormatSource.Magic), formats[3]);
        Assert.Equal((FileFormat.M2, FileFormatSource.Extension), formats[4]);
        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), formats[5]);
        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), formats[6]);
        Assert.Equal((FileFormat.Lua, FileFormatSource.Extension), formats[7]);
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), formats[8]);

        Assert.Equal(FileReadState.Complete, (await files.GetAsync(1u, Ct)).State);
        var stat = (await files.StatAsync(1u, Ct)).File!;
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), (stat.Format, stat.FormatSource));
    }
}
