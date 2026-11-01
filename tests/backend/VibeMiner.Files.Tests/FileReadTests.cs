using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Formats;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// Whole-file and range reads of the real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes (D137), with synthetic
/// BLTE objects and generated keys: chunk-scoped decoding, holes, keys, layer 2 states and the size checks.
/// </summary>
public sealed class FileReadTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const uint EnUsBit = 0x2, DeDeBit = 0x20;
    private const ulong KeyA = 0x1122334455667788, KeyB = 0x0102030405060708;
    private static readonly byte[] SecretA = BlteBuilder.Bytes(16, 41), SecretB = BlteBuilder.Bytes(16, 42), Iv = [1, 2, 3, 4];
    private static readonly CdnContext Cdn = new(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"));
    private static readonly string NameA = KeyA.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string NameB = KeyB.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The variant for a built object: real content key, sizes and EKey unless overridden.</summary>
    private static FileVariant VariantOf(BlteObject o, uint locales = EnUsBit, long? decodedSize = -1, long? encodedSize = -1, CdnKey? cKey = null,
        ArchiveLocation? location = null) =>
        new(locales, 0, cKey ?? new CdnKey(MD5.HashData(o.Decoded)), decodedSize == -1 ? o.Decoded.Length : decodedSize, o.EKey,
            encodedSize == -1 ? o.Encoded.Length : encodedSize, location);

    private static ObjectKey KeyOf(BlteObject o) => new(ObjectKeyType.Encoded, o.EKey.ToBytes());

    private sealed class Setup
    {
        public required InMemoryObjectSource Objects { get; init; }
        public required CountingObjectSource Counted { get; init; }
        public required BuildFiles Files { get; init; }
    }

    /// <summary>One build of files 1.. (one variant each) with every given object in layer 2.</summary>
    private static async Task<Setup> OpenAsync(FileVariant[] variants, BlteObject[] stored, FileSystemOptions? options = null,
        Action<InMemoryObjectSource>? script = null)
    {
        var builds = new L3.InMemoryBuilds();
        var files = variants.Select((v, i) => new L3.FakeFile((uint)(i + 1), null, [v])).ToArray();
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(["enUS", "deDE"], files.Length), files, Cdn));
        var objects = new InMemoryObjectSource();
        foreach (var o in stored) objects.Add(KeyOf(o), o.Encoded);
        script?.Invoke(objects);
        var counted = new CountingObjectSource(objects);
        var opened = await new FileSystem(builds, counted, options).ForAsync(FilesFixture.Build, "enUS", Ct);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return new() { Objects = objects, Counted = counted, Files = (BuildFiles)opened.Files! };
    }

    private static Task<Setup> OpenAsync(BlteObject o, FileSystemOptions? options = null) => OpenAsync([VariantOf(o)], [o], options);

    /// <summary>Ten chunks of 1000 decoded bytes, alternating raw and zlib.</summary>
    private static BlteObject TenChunks() => BlteBuilder.Chunked(Enumerable.Range(0, 10)
        .Select(i => i % 2 == 0 ? BlteChunkSpec.Raw(BlteBuilder.Bytes(1000, i)) : BlteChunkSpec.Zlib(BlteBuilder.Bytes(1000, i))).ToArray());

    private static BlteChunkSpec Locked(ulong name, byte[] secret, byte[] data) => BlteChunkSpec.Encrypted(name, secret, Iv, BlteChunkSpec.Raw(data));

    private static byte[] Slice(byte[] bytes, long offset, long length) => bytes.AsSpan((int)offset, (int)length).ToArray();

    [Fact]
    public async Task A_range_inside_two_chunks_decodes_only_them_and_reads_only_the_prefix_header_and_their_bytes()
    {
        var o = TenChunks();
        var layout = o.Parse();
        var s = await OpenAsync(o);

        var read = await s.Files.ReadRangeAsync(1u, 3100, 1800, Ct);

        Assert.Equal(FileReadState.Complete, read.State);
        Assert.Equal((3100L, 1800L), (read.Offset, read.Length));
        Assert.Equal(Slice(o.Decoded, 3100, 1800), read.Bytes!.Value.ToArray());
        Assert.Equal(2, s.Files.DecodedChunks);
        var c3 = layout.Chunks[3];
        Assert.Equal([(0L, 8), (8L, o.HeaderLength - 8), (c3.EncodedOffset, c3.EncodedSize + layout.Chunks[4].EncodedSize)], s.Counted.Ranges);

        // The layout is cached on the handle: a second range reads only its chunk.
        var again = await s.Files.ReadRangeAsync(1u, 3200, 100, Ct);
        Assert.Equal(Slice(o.Decoded, 3200, 100), again.Bytes!.Value.ToArray());
        Assert.Equal(3, s.Files.DecodedChunks);
        Assert.Equal((c3.EncodedOffset, c3.EncodedSize), s.Counted.Ranges[^1]);
        Assert.Equal(4, s.Counted.Ranges.Count);
    }

    [Fact]
    public async Task Every_range_matches_the_decoded_file_across_chunk_boundaries()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        foreach (var (offset, length) in new[] { (0L, 1), (0L, 10_000), (999L, 2), (1000L, 1000), (1L, 9998), (4500L, 3000), (9999L, 1), (5000L, 5000) })
        {
            var read = await s.Files.ReadRangeAsync(1u, offset, length, Ct);
            Assert.Equal(FileReadState.Complete, read.State);
            var segment = Assert.Single(read.Segments);
            Assert.Equal(offset, segment.Offset);
            Assert.Equal(Slice(o.Decoded, offset, length), segment.Bytes.ToArray());
            Assert.Empty(read.Holes);
        }
    }

    [Fact]
    public async Task Get_returns_the_whole_file_checked_against_its_content_key()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, read.State);
        Assert.Equal((0L, 10_000L), (read.Offset, read.Length));
        Assert.Equal(o.Decoded, read.Bytes!.Value.ToArray());
        Assert.Equal(10, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_whole_read_that_misses_its_content_key_is_corrupt()
    {
        var o = TenChunks();
        var s = await OpenAsync([VariantOf(o, cKey: CdnKey.Parse(new string('9', 32)))], [o]);
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Corrupt, read.State);
        Assert.Equal("BLTE_INTEGRITY", read.Code);
        Assert.Empty(read.Segments);

        // A range can't be held to the content key; its chunks prove themselves.
        Assert.Equal(FileReadState.Complete, (await s.Files.ReadRangeAsync(1u, 0, 10_000, Ct)).State);
    }

    [Fact]
    public async Task An_empty_file_reads_complete_without_a_fetch_but_still_matches_its_content_key()
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw([]));
        var s = await OpenAsync([VariantOf(o), VariantOf(o, cKey: CdnKey.Parse(new string('9', 32)))], [o]);
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, read.State);
        Assert.Equal(0, read.Bytes!.Value.Length);
        Assert.Equal(FileReadState.Corrupt, (await s.Files.GetAsync(2u, Ct)).State);
        Assert.Empty(s.Counted.Ranges);
    }

    /// <summary>Raw, A, A, raw, B: chunks of 100 bytes, with keys A and B unknown.</summary>
    private static BlteObject WithHoles() => BlteBuilder.Chunked([
        BlteChunkSpec.Raw(BlteBuilder.Bytes(100, 1)), Locked(KeyA, SecretA, BlteBuilder.Bytes(100, 2)), Locked(KeyA, SecretA, BlteBuilder.Bytes(100, 3)),
        BlteChunkSpec.Zlib(BlteBuilder.Bytes(100, 4)), Locked(KeyB, SecretB, BlteBuilder.Bytes(100, 5))]);

    [Fact]
    public async Task Unknown_keys_leave_named_holes_merged_per_key_and_the_rest_as_segments()
    {
        var o = WithHoles();
        // A partial whole read isn't held to the content key, so a wrong one doesn't matter.
        var s = await OpenAsync([VariantOf(o, cKey: CdnKey.Parse(new string('9', 32)))], [o]);

        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Partial, read.State);
        Assert.Null(read.Bytes);
        Assert.Equal([new FileHole(100, 200, NameA), new FileHole(400, 100, NameB)], read.Holes);
        Assert.Equal([0L, 300L], read.Segments.Select(x => x.Offset));
        Assert.Equal(Slice(o.Decoded, 0, 100), read.Segments[0].Bytes.ToArray());
        Assert.Equal(Slice(o.Decoded, 300, 100), read.Segments[1].Bytes.ToArray());
    }

    [Fact]
    public async Task Ranges_starting_and_ending_inside_holes_clip_them()
    {
        var o = WithHoles();
        var s = await OpenAsync(o);

        var inside = await s.Files.ReadRangeAsync(1u, 150, 100, Ct);
        Assert.Equal(FileReadState.Protected, inside.State);
        Assert.Empty(inside.Segments);
        Assert.Equal([new FileHole(150, 100, NameA)], inside.Holes);

        var across = await s.Files.ReadRangeAsync(1u, 250, 200, Ct);
        Assert.Equal(FileReadState.Partial, across.State);
        Assert.Equal([new FileHole(250, 50, NameA), new FileHole(400, 50, NameB)], across.Holes);
        var segment = Assert.Single(across.Segments);
        Assert.Equal(300, segment.Offset);
        Assert.Equal(Slice(o.Decoded, 300, 100), segment.Bytes.ToArray());

        var start = await s.Files.ReadRangeAsync(1u, 50, 100, Ct);
        Assert.Equal(FileReadState.Partial, start.State);
        Assert.Equal(Slice(o.Decoded, 50, 50), Assert.Single(start.Segments).Bytes.ToArray());
        Assert.Equal([new FileHole(100, 50, NameA)], start.Holes);
    }

    [Fact]
    public async Task Different_keys_side_by_side_stay_separate_holes()
    {
        var o = BlteBuilder.Chunked([Locked(KeyA, SecretA, BlteBuilder.Bytes(10, 1)), Locked(KeyB, SecretB, BlteBuilder.Bytes(10, 2))]);
        var s = await OpenAsync(o);
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Protected, read.State);
        Assert.Equal([new FileHole(0, 10, NameA), new FileHole(10, 10, NameB)], read.Holes);
    }

    [Fact]
    public async Task A_known_key_decrypts_and_a_wrong_one_is_wrong_key()
    {
        var o = WithHoles();
        var keys = new TactKeys().Add(KeyA, SecretA).Add(KeyB, SecretB);
        var s = await OpenAsync([VariantOf(o)], [o], new FileSystemOptions { Keys = keys });
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, read.State);
        Assert.Equal(o.Decoded, read.Bytes!.Value.ToArray());

        var wrong = await OpenAsync([VariantOf(o)], [o], new FileSystemOptions { Keys = new TactKeys().Add(KeyA, SecretB) });
        var failed = await wrong.Files.ReadRangeAsync(1u, 120, 10, Ct);
        Assert.Equal(FileReadState.WrongKey, failed.State);
        Assert.Equal("BLTE_KEY_MISMATCH", failed.Code);
        Assert.Equal((120L, 0L), (failed.Offset, failed.Length));
        // The raw chunk before it still reads.
        Assert.Equal(FileReadState.Complete, (await wrong.Files.ReadRangeAsync(1u, 0, 100, Ct)).State);
    }

    public static TheoryData<string, FileReadState> Layer2States => new()
    {
        { "NotFound", FileReadState.Missing },
        { "NeedsNetwork", FileReadState.NeedsNetwork },
        { "Unavailable", FileReadState.Unavailable },
        { "StoreFailed", FileReadState.StoreFailed },
        { "Refused", FileReadState.Corrupt },
    };

    [Theory]
    [MemberData(nameof(Layer2States))]
    public async Task Layer_2_states_map_to_read_states_with_their_code_and_time(string script, FileReadState expected)
    {
        var o = TenChunks();
        var at = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var s = await OpenAsync([VariantOf(o)], [], script: objects =>
        {
            objects.Time = new FixedTime(at);
            var key = KeyOf(o);
            _ = script switch
            {
                "NotFound" => objects.NotFound(key),
                "NeedsNetwork" => objects.NeedsNetwork(key),
                "Unavailable" => objects.Unavailable(key, "CDN_TIMEOUT"),
                "StoreFailed" => objects.StoreFailed(key),
                _ => objects.Refuse(key, "OBJECT_SIZE_MISMATCH"),
            };
        });
        var expectedCode = script switch
        {
            "NotFound" => "CDN_NOT_FOUND", "NeedsNetwork" => "OBJECT_ACCESS_NEEDS_NETWORK", "Unavailable" => "CDN_TIMEOUT",
            "StoreFailed" => "OBJECT_STORE_DISK_FULL", _ => "OBJECT_SIZE_MISMATCH",
        };

        foreach (var read in new[] { await s.Files.GetAsync(1u, Ct), await s.Files.ReadRangeAsync(1u, 500, 10, Ct) })
        {
            Assert.Equal(expected, read.State);
            Assert.Equal(expectedCode, read.Code);
            Assert.Empty(read.Segments);
            Assert.Equal(0, read.Length);
            if (script == "NotFound") Assert.Equal(at, read.ObservedAt);
            if (script == "NeedsNetwork") Assert.NotNull(read.Message);
        }
        Assert.Equal(500, (await s.Files.ReadRangeAsync(1u, 500, 10, Ct)).Offset);
    }

    [Fact]
    public async Task The_request_carries_the_encoded_size_and_location_at_interactive_priority_on_the_build_route()
    {
        var o = TenChunks();
        var location = new ArchiveLocation(CdnKey.Parse(new string('a', 32)), 4096, o.Encoded.Length);
        var s = await OpenAsync([VariantOf(o, location: location)], [o]);
        await s.Files.ReadRangeAsync(1u, 0, 1, Ct);
        var (request, cdn, priority) = s.Counted.Requests[0];
        Assert.Equal(KeyOf(o), request.Key);
        Assert.Equal(o.Encoded.Length, request.DeclaredLength);
        Assert.Equal([location], request.Locations!);
        Assert.Same(Cdn, cdn);
        Assert.All(s.Counted.Requests, r => Assert.Equal(FetchPriority.Interactive, r.Priority));

        var loose = await OpenAsync([VariantOf(o, encodedSize: null)], [o]);
        Assert.Equal(FileReadState.Complete, (await loose.Files.GetAsync(1u, Ct)).State);
        Assert.Null(loose.Counted.Requests[0].Request.Locations);
        Assert.Null(loose.Counted.Requests[0].Request.DeclaredLength);
    }

    [Fact]
    public async Task A_header_that_misses_its_encoding_key_is_corrupt()
    {
        var o = TenChunks();
        var tampered = o.Encoded.ToArray();
        tampered[20] ^= 1;
        var s = await OpenAsync([VariantOf(o)], [o with { Encoded = tampered }]);
        var read = await s.Files.ReadRangeAsync(1u, 10, 10, Ct);
        Assert.Equal(FileReadState.Corrupt, read.State);
        Assert.Equal("BLTE_INTEGRITY", read.Code);
        Assert.Equal(0, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_chunk_that_misses_its_checksum_is_corrupt()
    {
        var o = TenChunks();
        var tampered = o.Encoded.ToArray();
        tampered[o.Parse().Chunks[5].EncodedOffset + 3] ^= 1;
        var s = await OpenAsync([VariantOf(o)], [o with { Encoded = tampered }]);
        Assert.Equal(FileReadState.Complete, (await s.Files.ReadRangeAsync(1u, 0, 5000, Ct)).State);
        var read = await s.Files.ReadRangeAsync(1u, 4000, 2000, Ct);
        Assert.Equal(FileReadState.Corrupt, read.State);
        Assert.Equal("BLTE_INTEGRITY", read.Code);
        Assert.Equal(4000, read.Offset);
    }

    [Fact]
    public async Task An_unsupported_chunk_mode_is_unsupported()
    {
        var o = BlteBuilder.Chunked([BlteChunkSpec.Raw(BlteBuilder.Bytes(10)), BlteChunkSpec.Arbitrary([(byte)'F', 1, 2, 3], 10)]);
        var s = await OpenAsync(o);
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Unsupported, read.State);
        Assert.Equal("BLTE_UNSUPPORTED", read.Code);
    }

    [Fact]
    public async Task A_declared_size_the_layout_disagrees_with_is_corrupt_even_from_the_cached_layout()
    {
        var o = TenChunks();
        var s = await OpenAsync([VariantOf(o, decodedSize: 10_001), VariantOf(o), VariantOf(o, decodedSize: 9_000)], [o]);
        var first = await s.Files.ReadRangeAsync(1u, 0, 10, Ct);
        Assert.Equal(FileReadState.Corrupt, first.State);
        Assert.Equal("BLTE_INVALID", first.Code);

        Assert.Equal(FileReadState.Complete, (await s.Files.ReadRangeAsync(2u, 0, 10, Ct)).State);
        var cached = await s.Files.ReadRangeAsync(3u, 0, 10, Ct);
        Assert.Equal(FileReadState.Corrupt, cached.State);
        Assert.Equal("BLTE_INVALID", cached.Code);

        var encoded = await OpenAsync([VariantOf(o), VariantOf(o, encodedSize: o.Encoded.Length + 1)], [o]);
        Assert.Equal(FileReadState.Complete, (await encoded.Files.ReadRangeAsync(1u, 0, 10, Ct)).State);
        Assert.Equal(FileReadState.Corrupt, (await encoded.Files.ReadRangeAsync(2u, 0, 10, Ct)).State);
    }

    [Fact]
    public async Task A_whole_read_over_the_limit_is_too_large_before_any_fetch()
    {
        var o = TenChunks();
        var s = await OpenAsync(o, new FileSystemOptions { MaxReadBytes = 4000 });
        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.TooLarge, read.State);
        Assert.Equal("FILES_READ_TOO_LARGE", read.Code);
        Assert.Empty(s.Counted.Ranges);

        Assert.Equal(FileReadState.Complete, (await s.Files.ReadRangeAsync(1u, 6000, 4000, Ct)).State);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Files.ReadRangeAsync(1u, 0, 4001, Ct));
    }

    [Fact]
    public async Task A_headerless_chunk_over_the_limit_is_too_large()
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw(BlteBuilder.Bytes(5000)));
        var s = await OpenAsync(o, new FileSystemOptions { MaxReadBytes = 4000 });
        var read = await s.Files.ReadRangeAsync(1u, 10, 10, Ct);
        Assert.Equal(FileReadState.TooLarge, read.State);
        Assert.Equal("FILES_CHUNK_TOO_LARGE", read.Code);
        Assert.Equal(0, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task Headerless_files_read_whole_and_by_range()
    {
        var z = BlteBuilder.Headerless(BlteChunkSpec.Zlib(BlteBuilder.Bytes(3000, 7)));
        var n = BlteBuilder.Headerless(BlteChunkSpec.Raw(BlteBuilder.Bytes(300, 8)));
        var s = await OpenAsync([VariantOf(z), VariantOf(n)], [z, n]);

        Assert.Equal(z.Decoded, (await s.Files.GetAsync(1u, Ct)).Bytes!.Value.ToArray());
        Assert.Equal([(0L, 8), (8L, z.Encoded.Length - 8)], s.Counted.Ranges);
        var range = await s.Files.ReadRangeAsync(2u, 100, 50, Ct);
        Assert.Equal(Slice(n.Decoded, 100, 50), range.Bytes!.Value.ToArray());

        var tampered = n.Encoded.ToArray();
        tampered[^1] ^= 1;
        var bad = await OpenAsync([VariantOf(n)], [n with { Encoded = tampered }]);
        var corrupt = await bad.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Corrupt, corrupt.State);
        Assert.Equal("BLTE_INTEGRITY", corrupt.Code);
    }

    [Fact]
    public async Task Ranges_clamp_to_the_declared_size_and_an_empty_one_fetches_nothing()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);

        var past = await s.Files.ReadRangeAsync(1u, 20_000, 10, Ct);
        Assert.Equal(new FileRead(FileReadState.Complete, 10_000, 0, past.Segments, past.Holes), past);
        Assert.Empty(past.Segments);
        Assert.Equal(0, past.Bytes!.Value.Length);
        var empty = await s.Files.ReadRangeAsync(1u, 5, 0, Ct);
        Assert.Equal((FileReadState.Complete, 5L, 0L), (empty.State, empty.Offset, empty.Length));
        Assert.Empty(s.Counted.Ranges);

        var tail = await s.Files.ReadRangeAsync(1u, 9_997, 10, Ct);
        Assert.Equal((9_997L, 3L), (tail.Offset, tail.Length));
        Assert.Equal(Slice(o.Decoded, 9_997, 3), tail.Bytes!.Value.ToArray());
    }

    [Fact]
    public async Task Lookup_states_come_before_any_fetch()
    {
        var o = TenChunks();
        var s = await OpenAsync([VariantOf(o, locales: DeDeBit), VariantOf(o) with { EKey = null, EncodedSize = null }], [o]);
        foreach (var (file, state) in new (FileRef, FileReadState)[] { (99u, FileReadState.UnknownFile), (FileRef.ForPath("a/b.txt"), FileReadState.UnknownFile),
            (1u, FileReadState.NotInLocale), (2u, FileReadState.NotInEncoding) })
        {
            Assert.Equal(state, (await s.Files.GetAsync(file, Ct)).State);
            var range = await s.Files.ReadRangeAsync(file, 77, 10, Ct);
            Assert.Equal((state, 77L, 0L), (range.State, range.Offset, range.Length));
        }
        Assert.Empty(s.Counted.Ranges);
    }

    [Fact]
    public async Task Arguments_and_a_disposed_handle_throw()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Files.GetAsync(default, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Files.ReadRangeAsync(default, 0, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Files.ReadRangeAsync(1u, -1, 1, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.Files.ReadRangeAsync(1u, 0, -1, Ct));

        await s.Files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Files.GetAsync(1u, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Files.ReadRangeAsync(default, -1, -1, Ct));
        Assert.Empty(s.Counted.Ranges);
    }

    [Fact]
    public async Task Cancellation_before_and_during_a_read_throws()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Files.GetAsync(1u, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Files.ReadRangeAsync(1u, 0, 10, canceled.Token));
        Assert.Empty(s.Counted.Ranges);

        using var during = new CancellationTokenSource();
        var calls = 0;
        // Cancel on the header read, after the prefix.
        s.Objects.BeforeAnswer = (_, _) => ++calls == 2 ? during.CancelAsync() : Task.CompletedTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Files.ReadRangeAsync(1u, 0, 10, during.Token));
        Assert.Equal(2, calls);
        Assert.Equal(0, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task Encoded_reads_split_at_the_range_cap_into_pieces_and_runs()
    {
        const int MiB = 1 << 20;
        var o = BlteBuilder.Chunked([BlteChunkSpec.Raw(BlteBuilder.Bytes(5 * MiB, 1)), BlteChunkSpec.Raw(BlteBuilder.Bytes(MiB, 2)),
            BlteChunkSpec.Raw(BlteBuilder.Bytes(MiB, 3)), BlteChunkSpec.Raw(BlteBuilder.Bytes(3 * MiB, 4)), BlteChunkSpec.Raw(BlteBuilder.Bytes(5 * MiB, 5))]);
        var c = o.Parse().Chunks;
        const int Cap = FileReader.MaxRangeBytes;
        var s = await OpenAsync(o);

        var read = await s.Files.GetAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, read.State);
        Assert.Equal(o.Decoded, read.Bytes!.Value.ToArray());
        // A chunk over the cap reads in pieces; chunks 1 and 2 fit one call, chunk 3 would push it over.
        Assert.Equal([(0L, 8), (8L, o.HeaderLength - 8),
            (c[0].EncodedOffset, Cap), (c[0].EncodedOffset + Cap, c[0].EncodedSize - Cap),
            (c[1].EncodedOffset, c[1].EncodedSize + c[2].EncodedSize), (c[3].EncodedOffset, c[3].EncodedSize),
            (c[4].EncodedOffset, Cap), (c[4].EncodedOffset + Cap, c[4].EncodedSize - Cap)], s.Counted.Ranges);
        Assert.All(s.Counted.Ranges, r => Assert.InRange(r.Length, 1, Cap));

        var across = await s.Files.ReadRangeAsync(1u, 5 * MiB - 10, 20, Ct);
        Assert.Equal(Slice(o.Decoded, 5 * MiB - 10, 20), across.Bytes!.Value.ToArray());
        Assert.Equal([(c[0].EncodedOffset, Cap), (c[0].EncodedOffset + Cap, c[0].EncodedSize - Cap), (c[1].EncodedOffset, c[1].EncodedSize)],
            s.Counted.Ranges.Skip(8));
    }

    [Fact]
    public async Task Concurrent_ranges_on_one_handle_each_read_their_own_bytes()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        var ranges = Enumerable.Range(0, 40).Select(i => (Offset: (long)(i * 245 % 9_000), Length: 1 + i * 37 % 1_000)).ToArray();
        var reads = await Task.WhenAll(ranges.Select(r => Task.Run(() => s.Files.ReadRangeAsync(1u, r.Offset, r.Length, Ct))).Append(Task.Run(() => s.Files.GetAsync(1u, Ct))));
        for (var i = 0; i < ranges.Length; i++)
        {
            Assert.Equal(FileReadState.Complete, reads[i].State);
            Assert.Equal(Slice(o.Decoded, ranges[i].Offset, ranges[i].Length), reads[i].Bytes!.Value.ToArray());
        }
        Assert.Equal(o.Decoded, reads[^1].Bytes!.Value.ToArray());
    }

    private static (CdnKey Key, BlteLayout Layout) LayoutOf(int chunks, int seed)
    {
        var o = BlteBuilder.Chunked(Enumerable.Range(0, chunks).Select(i => BlteChunkSpec.Raw(BlteBuilder.Bytes(4, seed * 100 + i))).ToArray());
        return (o.EKey, o.Parse());
    }

    [Fact]
    public void The_layout_cache_drops_the_least_recently_used_entry_first()
    {
        var cache = new LayoutCache(maxEntries: 2, maxChunks: 100);
        var (a, b, c) = (LayoutOf(1, 1), LayoutOf(1, 2), LayoutOf(1, 3));
        cache.Add(a.Key, a.Layout);
        cache.Add(b.Key, b.Layout);
        Assert.Same(a.Layout, cache.Find(a.Key));
        cache.Add(c.Key, c.Layout);
        Assert.Null(cache.Find(b.Key));
        Assert.Same(a.Layout, cache.Find(a.Key));
        Assert.Same(c.Layout, cache.Find(c.Key));
    }

    [Fact]
    public void The_layout_cache_bounds_total_chunks_but_always_keeps_the_newest()
    {
        var cache = new LayoutCache(maxEntries: 10, maxChunks: 5);
        var (x, y, z, w) = (LayoutOf(2, 1), LayoutOf(2, 2), LayoutOf(3, 3), LayoutOf(6, 4));
        cache.Add(x.Key, x.Layout);
        cache.Add(y.Key, y.Layout);
        cache.Add(z.Key, z.Layout);
        Assert.Null(cache.Find(x.Key));
        Assert.Same(y.Layout, cache.Find(y.Key));
        Assert.Same(z.Layout, cache.Find(z.Key));

        // One layout over the whole budget still stays, alone.
        cache.Add(w.Key, w.Layout);
        Assert.Same(w.Layout, cache.Find(w.Key));
        Assert.Null(cache.Find(y.Key));
        Assert.Null(cache.Find(z.Key));

        // Adding a key again replaces it without counting it twice.
        cache.Add(y.Key, y.Layout);
        cache.Add(y.Key, y.Layout);
        Assert.Same(y.Layout, cache.Find(y.Key));
        Assert.Null(cache.Find(w.Key));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>An <see cref="IObjectSource"/> that records every range call and passes everything to the one it wraps.</summary>
internal sealed class CountingObjectSource(IObjectSource inner) : IObjectSource
{
    private readonly Lock gate = new();
    private readonly List<(long Offset, int Length)> ranges = [];
    private readonly List<(ObjectRequest Request, CdnContext? Cdn, FetchPriority Priority)> requests = [];

    /// <summary>Every <see cref="GetRangeAsync"/> as (offset, length), in call order.</summary>
    public IReadOnlyList<(long Offset, int Length)> Ranges { get { lock (gate) return ranges.ToArray(); } }
    public IReadOnlyList<(ObjectRequest Request, CdnContext? Cdn, FetchPriority Priority)> Requests { get { lock (gate) return requests.ToArray(); } }

    public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        lock (gate) { ranges.Add((offset, destination.Length)); requests.Add((request, cdn, priority)); }
        return inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);
    }

    public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
        inner.GetKeyAsync(request, cdn, priority, cancellationToken);
    public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) => inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);
    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) => inner.ObserveAsync(request, cancellationToken);
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.CountPresentAsync(keys, cancellationToken);
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.HasKeysAsync(keys, cancellationToken);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) => inner.ReadHistoryAsync(query, cancellationToken);
}
