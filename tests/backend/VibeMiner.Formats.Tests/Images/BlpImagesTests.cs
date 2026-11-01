using System.Security.Cryptography;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Images;
using VibeMiner.Generated;
using VibeMiner.Generated.Testing;
using Xunit;
using static VibeMiner.Formats.Tests.Images.Blp2DecoderTests;

namespace VibeMiner.Formats.Tests.Images;

/// <summary>Layer 5's BLP entry point over the layer 4 and generated store fakes (D137, D390). Synthetic BLPs only.</summary>
public sealed class BlpImagesTests
{
    private static readonly BuildRef A = new("wow", new string('a', 32), new string('1', 32), "us");
    private static readonly BuildRef B = new("wow", new string('b', 32), new string('2', 32), "us");
    private const string KeyOne = "0123456789ABCDEF";
    private const string KeyTwo = "FEDCBA9876543210";
    private static CancellationToken Ct => CancellationToken.None;

    // Three declared levels of each supported encoding, 4x4 → 1x1.
    public static TheoryData<string> Encodings => ["palette", "bc1", "bc2", "bc3", "bgra"];

    private static byte[] ThreeLevels(string encoding) => encoding switch
    {
        "palette" => Build(1, 8, 0, 4, 4, [[.. Fill(16, 1, 4), .. Fill(16, 0, 255)], [.. Fill(4, 2, 3), .. Fill(4, 17, 1)], [3, 128]]),
        "bc1" => Build(2, 0, 0, 4, 4, [Block(0xF800, 0x001F, 0x1B), Block(0x07E0, 0, 0), Block(0x001F, 0xF800, 0xE4)]),
        "bc2" => Build(2, 8, 1, 4, 4, [[.. Fill(8, 0x21, 3), .. Block(0, 0xFFFF, 0xAA)], [.. Fill(8, 0xFF, 1), .. Block(0xF800, 0, 0)], [.. Fill(8, 7, 1), .. Block(0x8C71, 0, 0)]]),
        "bc3" => Build(2, 8, 7, 4, 4, [[210, 0, .. Fill(6, 0x49, 1), .. Block(0, 0xFFFF, 0x1B)], [10, 20, .. Fill(6, 0x92, 1), .. Block(0xF800, 0, 0)], [50, 50, .. Fill(6, 0, 1), .. Block(0x07E0, 0, 0)]]),
        _ => Build(3, 8, 0, 4, 4, [Fill(64, 5, 7), Fill(16, 9, 3), [1, 2, 3, 4]]),
    };

    private static byte[] Fill(int count, int start, int step) => [.. Enumerable.Range(0, count).Select(i => (byte)(start + i * step))];

    private static async Task<Blp2Image> Direct(byte[] bytes, int level)
    {
        using var stream = new MemoryStream(bytes, false);
        return await Blp2Decoder.DecodeAsync(stream, level, null, Ct);
    }

    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    private static async Task<CountingFiles> Open(InMemoryFileSystem fake, BuildRef build)
    {
        var opened = await fake.ForAsync(build, "enUS", Ct);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return new CountingFiles(opened.Files!);
    }

    private static Task<CountingFiles> One(BuildRef build, FakeFile file) => Open(new InMemoryFileSystem().Add(build, ["enUS"], [file]), build);

    private static FakeFile Stored(byte[] content, uint id = 100, IReadOnlyList<FileHole>? holes = null, IReadOnlyList<string>? decrypted = null) =>
        new(id, Content: content, Storage: FileStorage.Stored, Holes: holes, DecryptedKeys: decrypted);

    private static GeneratedKey KeyOf(string text) => GeneratedKey.Hash(Encoding.UTF8.GetBytes(text));

    private static void SameImage(Blp2Image expected, Blp2Image actual)
    {
        Assert.Equal(expected.Rgba, actual.Rgba);
        Assert.Equal((expected.Width, expected.Height, expected.RowStride, expected.Encoding, expected.AlphaDepth, expected.AlphaEncoding, expected.MipLevel,
            expected.BaseWidth, expected.BaseHeight, expected.MipFlags), (actual.Width, actual.Height, actual.RowStride, actual.Encoding, actual.AlphaDepth,
            actual.AlphaEncoding, actual.MipLevel, actual.BaseWidth, actual.BaseHeight, actual.MipFlags));
        Assert.Equal(expected.Mips, actual.Mips);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Each_encoding_decodes_through_layer_4_at_every_mip_and_a_second_read_comes_from_the_store(string encoding)
    {
        var bytes = ThreeLevels(encoding);
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(bytes));
        for (var level = 0; level < 3; level++)
        {
            var expected = await Direct(bytes, level);
            var result = await images.DecodeAsync(files, 100, level, Ct);
            Assert.Equal(BlpImageState.Ready, result.State);
            Assert.Equal(BlpImageCache.Stored, result.Cache);
            SameImage(expected, result.Image!);
            var opens = files.Opens;
            var again = await images.DecodeAsync(files, 100, level, Ct);
            Assert.Equal(BlpImageCache.Hit, again.Cache);
            SameImage(expected, again.Image!);
            Assert.Equal(opens, files.Opens);
        }
        Assert.Equal(3, store.Count);
        Assert.Equal(0, files.Gets + files.Ranges.Count);
    }

    [Fact]
    public async Task A_cache_hit_reads_no_file_bytes_only_the_stat()
    {
        var bytes = ThreeLevels("bgra");
        var images = new BlpImages(new InMemoryGeneratedStore());
        var files = await One(A, Stored(bytes));
        Assert.Equal(BlpImageCache.Stored, (await images.DecodeAsync(files, 100, 0, Ct)).Cache);
        var counted = await One(A, Stored(bytes));
        var hit = await images.DecodeAsync(counted, 100, 0, Ct);
        Assert.Equal(BlpImageCache.Hit, hit.Cache);
        Assert.Equal((1, 0, 0, 0), (counted.Stats, counted.Opens, counted.Gets, counted.Ranges.Count));
    }

    [Fact]
    public async Task An_unsupported_profile_is_refused_by_name_and_never_cached()
    {
        var bytes = Build(3, 0, 0, 1, 1, [[1, 2, 3, 4]]);
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(bytes));
        var result = await images.DecodeAsync(files, 100, 0, Ct);
        Assert.Equal((BlpImageState.Unsupported, "BLP_UNSUPPORTED", BlpImageCache.None), (result.State, result.Code, result.Cache));
        Assert.Contains("colour encoding 3 with alpha depth 0 and alpha encoding 0", result.Message);
        Assert.Null(result.Image);
        var inspection = await images.InspectAsync(files, 100, Ct);
        Assert.Equal((BlpImageState.Unsupported, "BLP_UNSUPPORTED"), (inspection.State, inspection.Code));
        Assert.Equal(new BlpHeader(3, 0, 0, 0, 1, 1, 1), inspection.Header);
        Assert.Null(inspection.Encoding);
        Assert.Equal(0, store.Puts);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Inspect_reads_only_the_148_byte_header_and_reports_its_facts()
    {
        var bytes = ThreeLevels("bc2");
        var files = await One(A, Stored(bytes));
        var inspection = await new BlpImages(new InMemoryGeneratedStore()).InspectAsync(files, 100, Ct);
        Assert.Equal(BlpImageState.Ready, inspection.State);
        Assert.Equal(new BlpHeader(2, 8, 1, 1, 4, 4, 3), inspection.Header);
        Assert.Equal(Blp2Encoding.Bc2, inspection.Encoding);
        Assert.Equal([(4, 4), (2, 2), (1, 1)], inspection.Mips!.Select(m => (m.Width, m.Height)));
        Assert.All(inspection.Mips!, m => Assert.Equal(Blp2MipStorage.Exact, m.Storage));
        Assert.Equal([(0L, 148)], files.Ranges);
        Assert.Equal((0, 0), (files.Opens, files.Gets));
    }

    [Fact]
    public async Task Inspect_counts_declared_mips_from_the_descriptors_not_the_mip_byte()
    {
        var bytes = ThreeLevels("bgra");
        bytes[11] = 0; // The mip byte now excludes levels after the first; the descriptors still declare three.
        var inspection = await new BlpImages(new InMemoryGeneratedStore()).InspectAsync(await One(A, Stored(bytes)), 100, Ct);
        Assert.Equal((BlpImageState.Invalid, "BLP_INVALID"), (inspection.State, inspection.Code));
        Assert.Equal(3, inspection.Header!.DeclaredMips);
        Assert.Equal(0, inspection.Header.MipFlags);
    }

    [Fact]
    public async Task A_file_shorter_than_its_header_is_invalid_even_in_a_hole()
    {
        var images = new BlpImages(new InMemoryGeneratedStore());
        var files = await One(A, Stored(Build(3, 8, 0, 1, 1, [[1, 2, 3, 4]])[..100], holes: [new(0, 100, KeyOne)]));
        Assert.Equal((BlpImageState.Invalid, "BLP_INVALID"), ((await images.DecodeAsync(files, 100, 0, Ct)).State, (await images.DecodeAsync(files, 100, 0, Ct)).Code));
        Assert.Equal(BlpImageState.Invalid, (await images.InspectAsync(files, 100, Ct)).State);
        Assert.Equal(BlpImageState.Invalid, (await images.DecodeLargestWithinAsync(files, 100, 64, Ct)).State);
    }

    [Fact]
    public async Task A_header_in_a_hole_is_protected_and_names_its_key()
    {
        var bytes = ThreeLevels("bgra");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(bytes, holes: [new(0, 160, KeyOne)]));
        var result = await images.DecodeAsync(files, 100, 2, Ct);
        Assert.Equal(BlpImageState.Protected, result.State);
        Assert.Equal([KeyOne], result.HoleKeys);
        Assert.Null(result.Image);
        var inspection = await images.InspectAsync(files, 100, Ct);
        Assert.Equal(BlpImageState.Protected, inspection.State);
        Assert.Equal([KeyOne], inspection.HoleKeys);
        Assert.Null(inspection.Header);
        Assert.Equal(BlpImageState.Protected, (await images.DecodeLargestWithinAsync(files, 100, 1, Ct)).State);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_palette_in_a_hole_is_protected_for_every_level()
    {
        var bytes = ThreeLevels("palette");
        var images = new BlpImages(new InMemoryGeneratedStore());
        var files = await One(A, Stored(bytes, holes: [new(148 + 400, 64, KeyTwo)]));
        for (var level = 0; level < 3; level++)
        {
            var result = await images.DecodeAsync(files, 100, level, Ct);
            Assert.Equal(BlpImageState.Protected, result.State);
            Assert.Equal([KeyTwo], result.HoleKeys);
        }
    }

    [Fact]
    public async Task A_level_in_a_hole_is_protected_while_a_clear_level_decodes()
    {
        var bytes = ThreeLevels("bgra");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        // Level 0 is bytes 148-211, under one key; levels 1 and 2 are clear.
        var files = await One(A, Stored(bytes, holes: [new(148, 64, KeyOne)]));
        var protectedLevel = await images.DecodeAsync(files, 100, 0, Ct);
        Assert.Equal(BlpImageState.Protected, protectedLevel.State);
        Assert.Equal([KeyOne], protectedLevel.HoleKeys);
        for (var level = 1; level < 3; level++)
        {
            var clear = await images.DecodeAsync(files, 100, level, Ct);
            Assert.Equal(BlpImageState.Ready, clear.State);
            SameImage(await Direct(bytes, level), clear.Image!);
        }
    }

    [Fact]
    public async Task Largest_within_never_steps_down_past_a_protected_level()
    {
        var bytes = ThreeLevels("bgra");
        var images = new BlpImages(new InMemoryGeneratedStore());
        var files = await One(A, Stored(bytes, holes: [new(148, 64, KeyOne)]));
        var refused = await images.DecodeLargestWithinAsync(files, 100, 4, Ct);
        Assert.Equal(BlpImageState.Protected, refused.State);
        Assert.Equal([KeyOne], refused.HoleKeys);
        var smaller = await images.DecodeLargestWithinAsync(files, 100, 3, Ct);
        Assert.Equal((BlpImageState.Ready, 1), (smaller.State, smaller.Image!.MipLevel));
        // The clear build gets the same level for the same request.
        var clear = await images.DecodeLargestWithinAsync(await One(B, Stored(bytes)), 100, 4, Ct);
        Assert.Equal(0, clear.Image!.MipLevel);
    }

    [Fact]
    public async Task Largest_within_files_the_image_under_the_level_the_header_planned()
    {
        var bytes = ThreeLevels("bgra");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(bytes));
        Assert.Equal(1, (await images.DecodeLargestWithinAsync(files, 100, 3, Ct)).Image!.MipLevel);
        Assert.Equal([KeyOf(images.IdentityText(Md5(bytes), 1))], store.Keys);
        // A different side that plans the same level is a hit; a fixed-level decode of that level too.
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeLargestWithinAsync(files, 100, 2, Ct)).Cache);
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeAsync(files, 100, 1, Ct)).Cache);
    }

    [Fact]
    public async Task A_missing_level_or_no_fitting_level_is_mip_unavailable_and_not_cached()
    {
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(Build(3, 8, 0, 4, 2, [new byte[32]])));
        Assert.Equal((BlpImageState.MipUnavailable, "BLP_MIP_UNAVAILABLE"), ((await images.DecodeAsync(files, 100, 1, Ct)).State, (await images.DecodeAsync(files, 100, 1, Ct)).Code));
        Assert.Equal(BlpImageState.MipUnavailable, (await images.DecodeLargestWithinAsync(files, 100, 2, Ct)).State);
        Assert.Equal(BlpImageState.LimitExceeded, (await new BlpImages(store, new Blp2Limits(MaxDimension: 2)).DecodeAsync(files, 100, 0, Ct)).State);
        Assert.Equal(0, store.Puts);
    }

    public static TheoryData<string> FileFailures => ["unknown", "locale", "encoding", "network", "missing", "unavailable", "storefailed", "corrupt", "streamcorrupt", "wrongkey"];

    [Theory]
    [MemberData(nameof(FileFailures))]
    public async Task Each_layer_4_failure_passes_through_with_its_state_and_code(string failure)
    {
        var bytes = ThreeLevels("bc1");
        var observed = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        FakeFile file = failure switch
        {
            "locale" => Stored(bytes) with { Locales = ["deDE"] },
            "encoding" => new(100, NotInEncoding: true),
            "network" => new(100, Size: bytes.Length),
            "missing" => Stored(bytes) with { Storage = FileStorage.NotStored, ReadFailure = new(FileReadState.Missing, "CDN_MISSING", ObservedAt: observed) },
            "unavailable" => Stored(bytes) with { Storage = FileStorage.NotStored, ReadFailure = new(FileReadState.Unavailable, "CDN_NETWORK") },
            "storefailed" => Stored(bytes) with { Storage = FileStorage.NotStored, ReadFailure = new(FileReadState.StoreFailed, "STORE_FULL") },
            "corrupt" => Stored(bytes) with { ReadFailure = new(FileReadState.Corrupt, "BLTE_CORRUPT") },
            "streamcorrupt" => Stored(bytes) with { ReadFailure = new(FileReadState.Corrupt, "BLTE_CORRUPT", OnStreamRead: true) },
            "wrongkey" => Stored(bytes) with { ReadFailure = new(FileReadState.WrongKey, "FILES_WRONG_KEY") },
            _ => Stored(bytes, id: 7),
        };
        var (state, code) = failure switch
        {
            "unknown" => (FileReadState.UnknownFile, (string?)null),
            "locale" => (FileReadState.NotInLocale, null),
            "encoding" => (FileReadState.NotInEncoding, null),
            "network" => (FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"),
            "missing" => (FileReadState.Missing, "CDN_MISSING"),
            "unavailable" => (FileReadState.Unavailable, "CDN_NETWORK"),
            "storefailed" => (FileReadState.StoreFailed, "STORE_FULL"),
            "wrongkey" => (FileReadState.WrongKey, "FILES_WRONG_KEY"),
            _ => (FileReadState.Corrupt, "BLTE_CORRUPT"),
        };
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        // A second file keeps enUS in the build, so a file outside it is NotInLocale rather than a locale the build lacks.
        var files = await Open(new InMemoryFileSystem().Add(A, ["enUS", "deDE"], [file, Stored(bytes, id: 9)]), A);
        var result = await images.DecodeAsync(files, 100, 0, Ct);
        Assert.Equal((BlpImageState.FileFailed, state, code), (result.State, result.FileState, result.Code));
        Assert.Null(result.Image);
        Assert.NotNull(result.Message);
        if (failure == "missing") Assert.Equal(observed, result.ObservedAt);
        Assert.Equal((BlpImageState.FileFailed, state), ((await images.DecodeLargestWithinAsync(files, 100, 4, Ct)).State, (await images.DecodeLargestWithinAsync(files, 100, 4, Ct)).FileState));
        // Range reads fail the same way as stream reads in the fake, so Inspect passes each one through too.
        Assert.Equal((BlpImageState.FileFailed, state), ((await images.InspectAsync(files, 100, Ct)).State, (await images.InspectAsync(files, 100, Ct)).FileState));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task A_clear_image_is_shared_by_another_build_with_the_same_content_key()
    {
        var bytes = ThreeLevels("bc3");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        Assert.Equal(BlpImageCache.Stored, (await images.DecodeAsync(await One(A, Stored(bytes)), 100, 0, Ct)).Cache);
        Assert.Equal([KeyOf(images.IdentityText(Md5(bytes), 0))], store.Keys);
        // Build B lists the same content under another FileDataID and can't read it: the shared entry answers.
        var other = await One(B, new FakeFile(555, Size: bytes.Length, ContentKey: Md5(bytes)));
        var shared = await images.DecodeAsync(other, 555, 0, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.Hit), (shared.State, shared.Cache));
        SameImage(await Direct(bytes, 0), shared.Image!);
        Assert.Equal(0, other.Opens);
        // Other limits are another identity.
        Assert.Equal(BlpImageState.FileFailed, (await new BlpImages(store, new Blp2Limits(MaxDimension: 1024)).DecodeAsync(other, 555, 0, Ct)).State);
    }

    [Fact]
    public async Task An_image_from_a_stream_with_chunk_keys_is_scoped_to_its_build_and_key_statuses()
    {
        var bytes = ThreeLevels("bgra");
        var store = new InMemoryGeneratedStore();
        var status = new TactKeyStatus(new Dictionary<string, string> { [KeyOne] = "fp-one" });
        var images = new BlpImages(store, keys: build => build == A ? status : TactKeyStatus.None);
        // Level 2 lies in clear chunks; the file still has a decrypted chunk and a hole elsewhere.
        var files = await One(A, Stored(bytes, holes: [new(148, 64, KeyTwo)], decrypted: [KeyOne]));
        var first = await images.DecodeAsync(files, 100, 2, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.Stored), (first.State, first.Cache));
        var identity = images.IdentityText(Md5(bytes), 2);
        var scoped = BlpImages.ScopedIdentityText(identity, A, [KeyOne, KeyTwo], status);
        Assert.EndsWith($"\nencoding-in=wow {A.BuildConfig} {A.CdnConfig}\nkey {KeyOne}=fp-one\nkey {KeyTwo}=?", scoped);
        Assert.Equal([KeyOf(scoped)], store.Keys);
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeAsync(files, 100, 2, Ct)).Cache);
        // Not shared: another build that can't read the file finds nothing, and one that can decodes and stores its own.
        Assert.Equal(BlpImageState.FileFailed, (await images.DecodeAsync(await One(B, new FakeFile(100, Size: bytes.Length, ContentKey: Md5(bytes))), 100, 2, Ct)).State);
        Assert.Equal(BlpImageCache.Stored, (await images.DecodeAsync(await One(B, Stored(bytes, decrypted: [KeyOne])), 100, 2, Ct)).Cache);
        // A changed key status is another identity.
        var changed = new BlpImages(store, keys: _ => new TactKeyStatus(new Dictionary<string, string> { [KeyOne] = "fp-new" }));
        Assert.Equal(BlpImageCache.Stored, (await changed.DecodeAsync(files, 100, 2, Ct)).Cache);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public async Task A_shared_entry_wins_over_the_scoped_lookup_for_an_encrypted_copy()
    {
        var bytes = ThreeLevels("bc1");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        await images.DecodeAsync(await One(A, Stored(bytes)), 100, 0, Ct);
        var encrypted = await One(B, Stored(bytes, holes: [new(148, 8, KeyOne)]));
        var result = await images.DecodeAsync(encrypted, 100, 0, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.Hit), (result.State, result.Cache));
        Assert.Equal(0, encrypted.Opens);
    }

    [Fact]
    public async Task A_damaged_or_malformed_entry_is_deleted_and_decoded_again()
    {
        var bytes = ThreeLevels("palette");
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(bytes));
        var key = KeyOf(images.IdentityText(Md5(bytes), 0));
        await images.DecodeAsync(files, 100, 0, Ct);
        store.Damage(key);
        var rebuilt = await images.DecodeAsync(files, 100, 0, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.Stored), (rebuilt.State, rebuilt.Cache));
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeAsync(files, 100, 0, Ct)).Cache);

        var good = (await store.ReadAsync(key, Ct)).Bytes!;
        byte[][] malformed = [[1, 2, 3], [.. good[..^1]], [.. good, 0], [.. good.Select((b, i) => i == 4 ? (byte)2 : b)], [.. good.Select((b, i) => i == 28 ? (byte)1 : b)]];
        foreach (var bad in malformed)
        {
            await store.DeleteAsync([key], Ct);
            await store.PutAsync([new GeneratedPut(key, bad)], Ct);
            var again = await images.DecodeAsync(files, 100, 0, Ct);
            Assert.Equal((BlpImageState.Ready, BlpImageCache.Stored), (again.State, again.Cache));
            SameImage(await Direct(bytes, 0), again.Image!);
            Assert.Equal(good, (await store.ReadAsync(key, Ct)).Bytes);
        }
    }

    [Fact]
    public async Task A_canceled_call_throws_and_stores_nothing()
    {
        var store = new InMemoryGeneratedStore();
        var images = new BlpImages(store);
        var files = await One(A, Stored(ThreeLevels("bgra")));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => images.DecodeAsync(files, 100, 0, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => images.DecodeLargestWithinAsync(files, 100, 4, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => images.InspectAsync(files, 100, canceled.Token));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Arguments_out_of_range_throw()
    {
        var images = new BlpImages(new InMemoryGeneratedStore());
        var files = await One(A, Stored(ThreeLevels("bgra")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => images.DecodeAsync(files, 100, 16, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => images.DecodeAsync(files, 100, -1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => images.DecodeLargestWithinAsync(files, 100, 0, Ct));
    }

    [Fact]
    public async Task An_image_over_the_largest_generated_object_is_returned_but_not_stored()
    {
        // 4096x4096 BC1: 64 MiB of RGBA, so with its header the payload is over GeneratedLimits.MaxObjectBytes.
        var bytes = Build(2, 0, 0, 4096, 4096, [new byte[8 << 20]]);
        var store = new InMemoryGeneratedStore();
        var result = await new BlpImages(store).DecodeAsync(await One(A, Stored(bytes)), 100, 0, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.TooLarge), (result.State, result.Cache));
        Assert.Equal(GeneratedLimits.MaxObjectBytes, result.Image!.Rgba.Length);
        Assert.Equal((0, 0), (store.Puts, store.Count));
    }

    [Fact]
    public async Task A_failed_store_write_still_returns_the_image()
    {
        var bytes = ThreeLevels("bc1");
        var store = new FailingPuts();
        var result = await new BlpImages(store).DecodeAsync(await One(A, Stored(bytes)), 100, 0, Ct);
        Assert.Equal((BlpImageState.Ready, BlpImageCache.StoreFailed), (result.State, result.Cache));
        SameImage(await Direct(bytes, 0), result.Image!);
        Assert.Equal(1, store.Puts);
    }

    [Fact]
    public async Task A_read_failure_while_naming_the_hole_keys_is_passed_through()
    {
        var bytes = ThreeLevels("bgra");
        var files = new FailingAfterHole(await One(A, Stored(bytes, holes: [new(148, 64, KeyOne)])));
        var result = await new BlpImages(new InMemoryGeneratedStore()).DecodeAsync(files, 100, 0, Ct);
        Assert.Equal((BlpImageState.FileFailed, FileReadState.Corrupt, "BLTE_CORRUPT"), (result.State, result.FileState, result.Code));
        Assert.Empty(result.HoleKeys);
    }

    [Fact]
    public async Task Largest_within_reads_the_header_before_the_cache_so_an_unreadable_header_misses_the_shared_entry()
    {
        var bytes = ThreeLevels("bgra");
        var images = new BlpImages(new InMemoryGeneratedStore());
        Assert.Equal(BlpImageCache.Stored, (await images.DecodeAsync(await One(A, Stored(bytes)), 100, 1, Ct)).Cache);
        // Offline and not stored: the fixed level hits, the planned one can't be planned.
        var offline = await One(B, new FakeFile(100, Size: bytes.Length, ContentKey: Md5(bytes)));
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeAsync(offline, 100, 1, Ct)).Cache);
        var planned = await images.DecodeLargestWithinAsync(offline, 100, 3, Ct);
        Assert.Equal((BlpImageState.FileFailed, FileReadState.NeedsNetwork), (planned.State, planned.FileState));
        // The header in a hole: the same.
        var hidden = await One(B, Stored(bytes, holes: [new(0, 160, KeyOne)]));
        Assert.Equal(BlpImageCache.Hit, (await images.DecodeAsync(hidden, 100, 1, Ct)).Cache);
        var refused = await images.DecodeLargestWithinAsync(hidden, 100, 3, Ct);
        Assert.Equal(BlpImageState.Protected, refused.State);
        Assert.Equal([KeyOne], refused.HoleKeys);
    }

    [Fact]
    public async Task A_found_file_without_a_declared_size_is_corrupt_as_layer_4_reads_answer()
    {
        var inspection = await new BlpImages(new InMemoryGeneratedStore()).InspectAsync(await One(A, new FakeFile(100, Size: null)), 100, Ct);
        Assert.Equal((BlpImageState.FileFailed, FileReadState.Corrupt, "FILES_NO_DECLARED_SIZE"), (inspection.State, inspection.FileState, inspection.Code));
    }

    /// <summary>The in-memory store, except every Put throws as a full disk does.</summary>
    private sealed class FailingPuts : IGeneratedStore
    {
        private readonly InMemoryGeneratedStore inner = new();
        public int Puts;
        public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default) => inner.HasAsync(key, cancellationToken);
        public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default) =>
            inner.ReadRangeAsync(key, offset, destination, cancellationToken);
        public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Puts);
            throw new GeneratedStoreException(GeneratedStoreCodes.DiskFull, "The volume is full.");
        }
        public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default) => inner.DeleteAsync(keys, cancellationToken);
        public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken cancellationToken = default) =>
            inner.CompactAsync(minDeadFraction, maxPacks, cancellationToken);
    }

    /// <summary>Layer 4 whose streams fail every read as Corrupt once a read has hit a hole.</summary>
    private sealed class FailingAfterHole(IBuildFiles inner) : IBuildFiles
    {
        public BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
        public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) => inner.ReadRangeAsync(file, offset, length, cancellationToken);
        public async Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken)
        {
            var open = await inner.OpenReadAsync(file, cancellationToken);
            return open.Stream is { } stream ? open with { Stream = new Stream(stream) } : open;
        }
        public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
            inner.EnsureAsync(selection, priority, progress, cancellationToken);

        private sealed class Stream(FileReadStream inner) : FileReadStream
        {
            private bool broken;
            public override IReadOnlyList<FileHole> Holes => inner.Holes;
            public override IReadOnlyList<string> ChunkKeys => inner.ChunkKeys;
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (broken) throw new FileReadException(FileReadState.Corrupt, "BLTE_CORRUPT", "A chunk failed its checksum.");
                try { return await inner.ReadAsync(buffer, cancellationToken); }
                catch (FileHoleException) { broken = true; throw; }
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() { }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
            public override ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>Counts what reaches layer 4.</summary>
    private sealed class CountingFiles(IBuildFiles inner) : IBuildFiles
    {
        private readonly List<(long, int)> ranges = [];
        public int Stats, Gets, Opens;
        public IReadOnlyList<(long Offset, int Length)> Ranges { get { lock (ranges) return [.. ranges]; } }
        public BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) { Interlocked.Increment(ref Stats); return inner.StatAsync(file, cancellationToken); }
        public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) { Interlocked.Increment(ref Gets); return inner.GetAsync(file, cancellationToken); }
        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken)
        {
            lock (ranges) ranges.Add((offset, length));
            return inner.ReadRangeAsync(file, offset, length, cancellationToken);
        }
        public Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken) { Interlocked.Increment(ref Opens); return inner.OpenReadAsync(file, cancellationToken); }
        public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
            inner.EnsureAsync(selection, priority, progress, cancellationToken);
    }
}
