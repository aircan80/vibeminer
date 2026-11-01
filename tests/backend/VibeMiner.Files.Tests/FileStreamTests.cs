using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// <see cref="IBuildFiles.OpenReadAsync"/> of the real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes (D137), with
/// synthetic BLTE objects and generated keys: open states and holes, chunk-at-a-time reads, seeking, mid-stream failures and the
/// stream's own rules.
/// </summary>
public sealed class FileStreamTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const uint EnUsBit = 0x2, DeDeBit = 0x20;
    private const ulong KeyA = 0x1122334455667788, KeyB = 0x0102030405060708;
    private static readonly byte[] SecretA = BlteBuilder.Bytes(16, 41), SecretB = BlteBuilder.Bytes(16, 42), Iv = [1, 2, 3, 4];
    private static readonly CdnContext Cdn = new(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"));
    private static readonly string NameA = KeyA.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string NameB = KeyB.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

    private static FileVariant VariantOf(BlteObject o, uint locales = EnUsBit, long? decodedSize = -1, CdnKey? cKey = null) =>
        new(locales, 0, cKey ?? new CdnKey(MD5.HashData(o.Decoded)), decodedSize == -1 ? o.Decoded.Length : decodedSize, o.EKey, o.Encoded.Length, null);

    private static ObjectKey KeyOf(BlteObject o) => new(ObjectKeyType.Encoded, o.EKey.ToBytes());

    private sealed record Setup(InMemoryObjectSource Objects, CountingObjectSource Counted, BuildFiles Files);

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
        return new(objects, counted, (BuildFiles)opened.Files!);
    }

    private static Task<Setup> OpenAsync(BlteObject o, FileSystemOptions? options = null) => OpenAsync([VariantOf(o)], [o], options);

    private static async Task<FileReadStream> StreamAsync(Setup s, uint id = 1)
    {
        var open = await s.Files.OpenReadAsync(id, Ct);
        Assert.NotNull(open.Stream);
        return open.Stream;
    }

    /// <summary>Ten chunks of 1000 decoded bytes, alternating raw and zlib.</summary>
    private static BlteObject TenChunks() => BlteBuilder.Chunked(Enumerable.Range(0, 10)
        .Select(i => i % 2 == 0 ? BlteChunkSpec.Raw(BlteBuilder.Bytes(1000, i)) : BlteChunkSpec.Zlib(BlteBuilder.Bytes(1000, i))).ToArray());

    private static BlteChunkSpec Locked(ulong name, byte[] secret, byte[] data) => BlteChunkSpec.Encrypted(name, secret, Iv, BlteChunkSpec.Raw(data));

    /// <summary>Raw, A, A, zlib, B: chunks of 100 bytes.</summary>
    private static BlteObject WithHoles() => BlteBuilder.Chunked([
        BlteChunkSpec.Raw(BlteBuilder.Bytes(100, 1)), Locked(KeyA, SecretA, BlteBuilder.Bytes(100, 2)), Locked(KeyA, SecretA, BlteBuilder.Bytes(100, 3)),
        BlteChunkSpec.Zlib(BlteBuilder.Bytes(100, 4)), Locked(KeyB, SecretB, BlteBuilder.Bytes(100, 5))]);

    private static byte[] Slice(byte[] bytes, long offset, long length) => bytes.AsSpan((int)offset, (int)length).ToArray();

    /// <summary>Reads to the end in reads of <paramref name="size"/> bytes, checking none crosses a 1000-byte chunk.</summary>
    private static async Task<byte[]> ReadAllAsync(Stream stream, int size, int chunkSize = 1000)
    {
        using var all = new MemoryStream();
        var buffer = new byte[size];
        int n;
        while ((n = await stream.ReadAsync(buffer, Ct)) > 0)
        {
            Assert.True((all.Length % chunkSize) + n <= chunkSize, "A read crossed a chunk end.");
            all.Write(buffer, 0, n);
        }
        return all.ToArray();
    }

    [Fact]
    public async Task Opening_reads_the_header_and_one_window_of_envelopes_and_decodes_nothing()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        var open = await s.Files.OpenReadAsync(1u, Ct);

        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;
        Assert.Empty(stream.Holes);
        Assert.Empty(stream.ChunkKeys);
        Assert.Equal(10_000, stream.Length);
        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead && stream.CanSeek && !stream.CanWrite);
        Assert.Equal(0, s.Files.DecodedChunks);
        Assert.Equal([(0L, 8), (8L, o.HeaderLength - 8), (o.HeaderLength, o.Encoded.Length - o.HeaderLength)], s.Counted.Ranges);
    }

    [Fact]
    public async Task Holes_are_listed_at_open_merged_per_key_with_partial_and_protected_states()
    {
        var o = WithHoles();
        var s = await OpenAsync(o);
        var open = await s.Files.OpenReadAsync(1u, Ct);
        Assert.Equal(FileReadState.Partial, open.State);
        Assert.Equal([new FileHole(100, 200, NameA), new FileHole(400, 100, NameB)], open.Stream!.Holes);
        Assert.Equal(0, s.Files.DecodedChunks);

        var two = BlteBuilder.Chunked([Locked(KeyA, SecretA, BlteBuilder.Bytes(10, 1)), Locked(KeyB, SecretB, BlteBuilder.Bytes(10, 2))]);
        var same = BlteBuilder.Chunked([Locked(KeyA, SecretA, BlteBuilder.Bytes(10, 1)), BlteChunkSpec.Raw([]), Locked(KeyA, SecretA, BlteBuilder.Bytes(10, 2))]);
        var p = await OpenAsync([VariantOf(two), VariantOf(same)], [two, same]);
        var protectedTwo = await p.Files.OpenReadAsync(1u, Ct);
        Assert.Equal(FileReadState.Protected, protectedTwo.State);
        Assert.Equal([new FileHole(0, 10, NameA), new FileHole(10, 10, NameB)], protectedTwo.Stream!.Holes);
        var protectedSame = await p.Files.OpenReadAsync(2u, Ct);
        Assert.Equal(FileReadState.Protected, protectedSame.State);
        Assert.Equal([new FileHole(0, 20, NameA)], protectedSame.Stream!.Holes);
    }

    [Fact]
    public async Task Only_chunks_up_to_64_KiB_are_batched_in_1_MiB_windows_and_a_bigger_one_reads_just_its_envelope()
    {
        // 65,535 raw bytes encode to exactly 64 KiB, the biggest chunk still batched.
        BlteChunkSpec Small(int seed) => BlteChunkSpec.Raw(BlteBuilder.Bytes(65_535, seed));
        var o = BlteBuilder.Chunked([BlteChunkSpec.Raw(BlteBuilder.Bytes(100, 1)), BlteChunkSpec.Raw(BlteBuilder.Bytes((1 << 20) + 10, 2)),
            Locked(KeyA, SecretA, BlteBuilder.Bytes(100, 3)), Small(4), Locked(KeyB, SecretB, BlteBuilder.Bytes(70_000, 5)),
            .. Enumerable.Range(10, 17).Select(Small)]);
        var c = o.Parse().Chunks;
        var s = await OpenAsync(o);
        var open = await s.Files.OpenReadAsync(1u, Ct);

        Assert.Equal(FileReadState.Partial, open.State);
        Assert.Equal([new FileHole(c[2].DecodedOffset, 100, NameA), new FileHole(c[4].DecodedOffset, 70_000, NameB)], open.Stream!.Holes);
        Assert.Equal([(c[0].EncodedOffset, c[0].EncodedSize), (c[1].EncodedOffset, 16), (c[2].EncodedOffset, c[2].EncodedSize + c[3].EncodedSize),
            (c[4].EncodedOffset, 16), (c[5].EncodedOffset, 16 * 65_536), (c[21].EncodedOffset, 65_536)], s.Counted.Ranges.Skip(2));
        Assert.Equal(0, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_headerless_file_over_16_MiB_reads_through_the_stream()
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw(BlteBuilder.Bytes((16 << 20) + 10, 3)));
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        Assert.Equal([(0L, 8), (8L, 16)], s.Counted.Ranges);

        stream.Position = (16 << 20) - 5;
        var tail = new byte[100];
        Assert.Equal(15, await stream.ReadAsync(tail, Ct));
        Assert.Equal(Slice(o.Decoded, (16 << 20) - 5, 15), tail[..15]);
        stream.Position = 0;
        var head = new byte[1000];
        await stream.ReadExactlyAsync(head, Ct);
        Assert.Equal(Slice(o.Decoded, 0, 1000), head);
        Assert.Equal(1, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task Reading_the_whole_stream_in_odd_sizes_gives_the_decoded_file()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        foreach (var size in new[] { 1, 333, 999, 1000, 4096 })
        {
            await using var stream = await StreamAsync(s);
            Assert.Equal(o.Decoded, await ReadAllAsync(stream, size));
            Assert.Equal(10_000, stream.Position);
            Assert.Equal(0, await stream.ReadAsync(new byte[10], Ct));
        }
        await using var copied = await StreamAsync(s);
        using var target = new MemoryStream();
        await copied.CopyToAsync(target, Ct);
        Assert.Equal(o.Decoded, target.ToArray());
    }

    [Fact]
    public async Task Seeking_from_each_origin_and_reading_across_chunk_boundaries()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);

        Assert.Equal(2500, stream.Seek(2500, SeekOrigin.Begin));
        var buffer = new byte[1700];
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(Slice(o.Decoded, 2500, 1700), buffer);

        Assert.Equal(3700, stream.Seek(-500, SeekOrigin.Current));
        var first = await stream.ReadAsync(buffer, Ct);
        Assert.Equal(300, first);
        Assert.Equal(Slice(o.Decoded, 3700, 300), buffer[..300]);

        Assert.Equal(9_990, stream.Seek(-10, SeekOrigin.End));
        Assert.Equal(10, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(Slice(o.Decoded, 9_990, 10), buffer[..10]);

        stream.Position = 999;
        await stream.ReadExactlyAsync(buffer.AsMemory(0, 2), Ct);
        Assert.Equal(Slice(o.Decoded, 999, 2), buffer[..2]);

        Assert.Equal(20_000, stream.Seek(10_000, SeekOrigin.End));
        Assert.Equal(0, await stream.ReadAsync(buffer, Ct));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-20_001, SeekOrigin.Current));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
        Assert.Throws<ArgumentException>(() => stream.Seek(0, (SeekOrigin)7));
        Assert.Equal(20_000, stream.Position);
    }

    [Fact]
    public async Task A_read_stops_short_at_a_hole_and_one_inside_it_throws_with_the_whole_hole()
    {
        var o = WithHoles();
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        var buffer = new byte[300];

        stream.Position = 50;
        Assert.Equal(50, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(Slice(o.Decoded, 50, 50), buffer[..50]);
        Assert.Equal(100, stream.Position);

        var atStart = await Assert.ThrowsAsync<FileHoleException>(() => stream.ReadAsync(buffer, Ct).AsTask());
        Assert.Equal(new FileHole(100, 200, NameA), atStart.Hole);
        Assert.Equal(100, stream.Position);
        stream.Position = 299;
        Assert.Equal(new FileHole(100, 200, NameA), (await Assert.ThrowsAsync<FileHoleException>(() => stream.ReadAsync(buffer, 0, 10, Ct))).Hole);

        stream.Position = 300;
        Assert.Equal(100, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(Slice(o.Decoded, 300, 100), buffer[..100]);
        Assert.Equal(new FileHole(400, 100, NameB), (await Assert.ThrowsAsync<FileHoleException>(() => stream.ReadAsync(buffer, Ct).AsTask())).Hole);
        // Holes are known at open: reading them fetches and decodes nothing.
        Assert.Equal(2, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task A_known_key_decrypts_and_a_wrong_one_fails_only_its_chunk()
    {
        var o = WithHoles();
        var s = await OpenAsync([VariantOf(o)], [o], new FileSystemOptions { Keys = new TactKeys().Add(KeyA, SecretA).Add(KeyB, SecretB) });
        var open = await s.Files.OpenReadAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        // A decrypted chunk is no hole, but its key is listed: its bytes depend on it.
        Assert.Equal([NameB, NameA], open.Stream!.ChunkKeys);
        await using (var stream = open.Stream!) Assert.Equal(o.Decoded, await ReadAllAsync(stream, 77, 100));

        var wrong = await OpenAsync([VariantOf(o)], [o], new FileSystemOptions { Keys = new TactKeys().Add(KeyA, SecretB) });
        await using var bad = await StreamAsync(wrong);
        Assert.Equal([new FileHole(400, 100, NameB)], bad.Holes);
        Assert.Equal([NameB, NameA], bad.ChunkKeys);
        bad.Position = 120;
        var failed = await Assert.ThrowsAsync<FileReadException>(() => bad.ReadAsync(new byte[10], Ct).AsTask());
        Assert.Equal((FileReadState.WrongKey, "BLTE_KEY_MISMATCH"), (failed.State, failed.Code));
        bad.Position = 0;
        Assert.Equal(100, await bad.ReadAsync(new byte[200], Ct));
    }

    [Fact]
    public async Task A_key_gone_after_open_throws_a_hole_for_that_chunk()
    {
        var o = WithHoles();
        var keys = new MutableKeys();
        keys.Keys[KeyA] = SecretA;
        var s = await OpenAsync([VariantOf(o)], [o], new FileSystemOptions { Keys = keys });
        await using var stream = await StreamAsync(s);
        Assert.Equal([new FileHole(400, 100, NameB)], stream.Holes);

        keys.Keys.Clear();
        stream.Position = 150;
        Assert.Equal(new FileHole(100, 100, NameA), (await Assert.ThrowsAsync<FileHoleException>(() => stream.ReadAsync(new byte[10], Ct).AsTask())).Hole);
        stream.Position = 300;
        Assert.Equal(100, await stream.ReadAsync(new byte[100], Ct));
    }

    [Theory]
    [InlineData("Unavailable", FileReadState.Unavailable, "CDN_TIMEOUT")]
    [InlineData("Refused", FileReadState.Corrupt, "OBJECT_SIZE_MISMATCH")]
    [InlineData("NeedsNetwork", FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK")]
    public async Task A_layer_2_failure_mid_stream_throws_its_state_and_the_kept_chunk_still_reads(string script, FileReadState state, string code)
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        var buffer = new byte[10];
        await stream.ReadExactlyAsync(buffer, Ct);

        var key = KeyOf(o);
        _ = script switch
        {
            "Unavailable" => s.Objects.Unavailable(key, "CDN_TIMEOUT"),
            "Refused" => s.Objects.Refuse(key, "OBJECT_SIZE_MISMATCH"),
            _ => s.Objects.NeedsNetwork(key),
        };
        stream.Position = 3500;
        var failed = await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(buffer, Ct).AsTask());
        Assert.Equal((state, code), (failed.State, failed.Code));
        Assert.Equal(3500, stream.Position);

        // Layer 2 failed before any decode, so the chunk kept from the first read still serves without a call.
        var calls = s.Counted.Ranges.Count;
        stream.Position = 500;
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(Slice(o.Decoded, 500, 10), buffer);
        Assert.Equal(calls, s.Counted.Ranges.Count);
        stream.Position = 1500;
        Assert.Equal(state, (await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(buffer, Ct).AsTask())).State);
    }

    [Fact]
    public async Task A_corrupt_chunk_mid_stream_throws_corrupt_and_the_rest_still_reads()
    {
        var o = TenChunks();
        var tampered = o.Encoded.ToArray();
        tampered[o.Parse().Chunks[5].EncodedOffset + 3] ^= 1;
        var s = await OpenAsync([VariantOf(o)], [o with { Encoded = tampered }]);
        var open = await s.Files.OpenReadAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;

        stream.Position = 5200;
        var failed = await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(new byte[10], Ct).AsTask());
        Assert.Equal((FileReadState.Corrupt, "BLTE_INTEGRITY"), (failed.State, failed.Code));
        stream.Position = 4000;
        var buffer = new byte[1000];
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(Slice(o.Decoded, 4000, 1000), buffer);
        await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(buffer, Ct).AsTask());
    }

    [Fact]
    public async Task One_chunk_decodes_once_while_reads_stay_in_it()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        var c3 = o.Parse().Chunks[3];
        var opened = s.Counted.Ranges.Count;

        stream.Position = 3000;
        var buffer = new byte[100];
        for (var i = 0; i < 10; i++) await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(1, s.Files.DecodedChunks);
        Assert.Equal([(c3.EncodedOffset, c3.EncodedSize)], s.Counted.Ranges.Skip(opened));

        stream.Position = 3999;
        Assert.Equal(1, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(1, s.Files.DecodedChunks);
        Assert.Equal(100, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(2, s.Files.DecodedChunks);
        stream.Position = 3500;
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(3, s.Files.DecodedChunks);
    }

    [Fact]
    public async Task The_stream_outlives_the_handle_and_ignores_the_whole_file_limit()
    {
        var o = TenChunks();
        var s = await OpenAsync(o, new FileSystemOptions { MaxReadBytes = 4000 });
        Assert.Equal(FileReadState.TooLarge, (await s.Files.GetAsync(1u, Ct)).State);
        await using var stream = await StreamAsync(s);
        await s.Files.DisposeAsync();

        Assert.Equal(o.Decoded, await ReadAllAsync(stream, 1234));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Files.OpenReadAsync(1u, Ct));
    }

    [Fact]
    public async Task A_disposed_stream_refuses_every_member()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        var stream = await StreamAsync(s);
        await stream.ReadExactlyAsync(new byte[10], Ct);
        await stream.DisposeAsync();
        stream.Dispose();

        Assert.False(stream.CanRead || stream.CanSeek || stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.Length);
        Assert.Throws<ObjectDisposedException>(() => stream.Position);
        Assert.Throws<ObjectDisposedException>(() => stream.Position = 0);
        Assert.Throws<ObjectDisposedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.NotNull(stream.Holes); // the hole list is plain data and stays readable
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[10], Ct).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.ReadAsync(new byte[10], 0, 10, Ct));
    }

    [Fact]
    public async Task Sync_reads_and_writes_are_not_supported()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        var opened = s.Counted.Ranges.Count;

        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[10], 0, 10));
        Assert.Throws<NotSupportedException>(() => stream.Read(new byte[10].AsSpan()));
        Assert.Throws<NotSupportedException>(() => stream.ReadByte());
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(5));
        stream.Flush();
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = stream.ReadAsync(new byte[10], 5, 10, Ct); });
        Assert.Equal(opened, s.Counted.Ranges.Count);
        Assert.Equal(0, stream.Position);
    }

    public static TheoryData<string, FileReadState, string> Layer2States => new()
    {
        { "NotFound", FileReadState.Missing, "CDN_NOT_FOUND" },
        { "NeedsNetwork", FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK" },
        { "Unavailable", FileReadState.Unavailable, "CDN_TIMEOUT" },
        { "StoreFailed", FileReadState.StoreFailed, "OBJECT_STORE_DISK_FULL" },
        { "Refused", FileReadState.Corrupt, "OBJECT_SIZE_MISMATCH" },
    };

    [Theory]
    [MemberData(nameof(Layer2States))]
    public async Task A_layer_2_failure_at_open_is_the_open_state_with_its_code_and_time(string script, FileReadState state, string code)
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
        var open = await s.Files.OpenReadAsync(1u, Ct);
        Assert.Equal((state, code, (FileReadStream?)null), (open.State, open.Code, open.Stream));
        if (script == "NotFound") Assert.Equal(at, open.ObservedAt);
        if (script == "NeedsNetwork") Assert.NotNull(open.Message);
    }

    [Fact]
    public async Task Lookup_states_come_before_any_fetch()
    {
        var o = TenChunks();
        var s = await OpenAsync([VariantOf(o, locales: DeDeBit), VariantOf(o) with { EKey = null, EncodedSize = null }, VariantOf(o, decodedSize: null)], [o]);
        foreach (var (file, state) in new (FileRef, FileReadState)[] { (99u, FileReadState.UnknownFile), (FileRef.ForPath("a/b.txt"), FileReadState.UnknownFile),
            (1u, FileReadState.NotInLocale), (2u, FileReadState.NotInEncoding), (3u, FileReadState.Corrupt) })
        {
            var open = await s.Files.OpenReadAsync(file, Ct);
            Assert.Equal((state, (FileReadStream?)null), (open.State, open.Stream));
        }
        Assert.Empty(s.Counted.Ranges);
    }

    [Fact]
    public async Task Bad_bytes_at_open_are_the_open_state()
    {
        var o = TenChunks();
        var tampered = o.Encoded.ToArray();
        tampered[20] ^= 1;
        var badHeader = await OpenAsync([VariantOf(o), VariantOf(o, decodedSize: 10_001)], [o with { Encoded = tampered }]);
        var header = await badHeader.Files.OpenReadAsync(1u, Ct);
        Assert.Equal((FileReadState.Corrupt, "BLTE_INTEGRITY", (FileReadStream?)null), (header.State, header.Code, header.Stream));

        var sizes = await OpenAsync([VariantOf(o, decodedSize: 10_001)], [o]);
        Assert.Equal((FileReadState.Corrupt, "BLTE_INVALID"), ((await sizes.Files.OpenReadAsync(1u, Ct)).State, (await sizes.Files.OpenReadAsync(1u, Ct)).Code));

        // Envelopes are peeked at open: a key name length this code doesn't read is Unsupported, a truncated envelope Corrupt.
        var odd = BlteBuilder.Chunked([BlteChunkSpec.Raw(BlteBuilder.Bytes(10)), BlteChunkSpec.Arbitrary([(byte)'E', 7, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], 10)]);
        var cut = BlteBuilder.Chunked([BlteChunkSpec.Arbitrary([(byte)'E', 8, 1, 2, 3], 10)]);
        var e = await OpenAsync([VariantOf(odd), VariantOf(cut)], [odd, cut]);
        var unsupported = await e.Files.OpenReadAsync(1u, Ct);
        Assert.Equal((FileReadState.Unsupported, "BLTE_UNSUPPORTED", (FileReadStream?)null), (unsupported.State, unsupported.Code, unsupported.Stream));
        var truncated = await e.Files.OpenReadAsync(2u, Ct);
        Assert.Equal((FileReadState.Corrupt, "BLTE_INVALID"), (truncated.State, truncated.Code));
        Assert.Equal(0, e.Files.DecodedChunks);
    }

    [Fact]
    public async Task Headerless_files_open_as_one_chunk_and_one_over_the_limit_is_too_large()
    {
        var z = BlteBuilder.Headerless(BlteChunkSpec.Zlib(BlteBuilder.Bytes(3000, 7)));
        var locked = BlteBuilder.Headerless(Locked(KeyA, SecretA, BlteBuilder.Bytes(300, 8)));
        var s = await OpenAsync([VariantOf(z), VariantOf(locked)], [z, locked], new FileSystemOptions { MaxReadBytes = 4000 });

        await using (var stream = await StreamAsync(s))
        {
            Assert.Equal([(0L, 8), (8L, z.Encoded.Length - 8)], s.Counted.Ranges);
            Assert.Equal(z.Decoded, await ReadAllAsync(stream, 512, 3000));
        }
        var protectedOpen = await s.Files.OpenReadAsync(2u, Ct);
        Assert.Equal(FileReadState.Protected, protectedOpen.State);
        Assert.Equal([new FileHole(0, 300, NameA)], protectedOpen.Stream!.Holes);

        var big = BlteBuilder.Headerless(BlteChunkSpec.Raw(BlteBuilder.Bytes(5000)));
        var tooBig = await OpenAsync(big, new FileSystemOptions { MaxReadBytes = 4000 });
        var open = await tooBig.Files.OpenReadAsync(1u, Ct);
        Assert.Equal((FileReadState.TooLarge, "FILES_CHUNK_TOO_LARGE", (FileReadStream?)null), (open.State, open.Code, open.Stream));
        Assert.Equal([(0L, 8)], tooBig.Counted.Ranges);
    }

    [Fact]
    public async Task An_empty_file_opens_complete_without_a_fetch_but_still_matches_its_content_key()
    {
        var o = BlteBuilder.Headerless(BlteChunkSpec.Raw([]));
        var s = await OpenAsync([VariantOf(o), VariantOf(o, cKey: CdnKey.Parse(new string('9', 32)))], [o]);
        var open = await s.Files.OpenReadAsync(1u, Ct);
        Assert.Equal(FileReadState.Complete, open.State);
        await using var stream = open.Stream!;
        Assert.Equal(0, stream.Length);
        Assert.Empty(stream.Holes);
        Assert.Equal(0, await stream.ReadAsync(new byte[10], Ct));
        Assert.Equal((FileReadState.Corrupt, "BLTE_INTEGRITY"), ((await s.Files.OpenReadAsync(2u, Ct)).State, (await s.Files.OpenReadAsync(2u, Ct)).Code));
        Assert.Empty(s.Counted.Ranges);
    }

    [Fact]
    public async Task Arguments_and_a_disposed_handle_throw()
    {
        var s = await OpenAsync(TenChunks());
        await Assert.ThrowsAsync<ArgumentException>(() => s.Files.OpenReadAsync(default, Ct));
        await s.Files.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => s.Files.OpenReadAsync(default, Ct));
        Assert.Empty(s.Counted.Ranges);
    }

    [Fact]
    public async Task Cancellation_at_open_and_mid_stream_throws()
    {
        var o = TenChunks();
        var s = await OpenAsync(o);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Files.OpenReadAsync(1u, canceled.Token));
        Assert.Empty(s.Counted.Ranges);

        using var duringOpen = new CancellationTokenSource();
        var calls = 0;
        // Cancel on the envelope window, after the prefix and header.
        s.Objects.BeforeAnswer = (_, _) => ++calls == 3 ? duringOpen.CancelAsync() : Task.CompletedTask;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Files.OpenReadAsync(1u, duringOpen.Token));
        Assert.Equal(3, calls);

        s.Objects.BeforeAnswer = null;
        await using var stream = await StreamAsync(s);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[10], canceled.Token).AsTask());
        using var duringRead = new CancellationTokenSource();
        s.Objects.BeforeAnswer = (_, _) => duringRead.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[10], duringRead.Token).AsTask());
        Assert.Equal(0, s.Files.DecodedChunks);

        // A canceled read leaves the stream usable.
        s.Objects.BeforeAnswer = null;
        var buffer = new byte[10];
        await stream.ReadExactlyAsync(buffer, Ct);
        Assert.Equal(Slice(o.Decoded, 0, 10), buffer);
    }

    [Fact]
    public async Task A_missing_object_mid_stream_keeps_its_date()
    {
        var o = TenChunks();
        var at = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var s = await OpenAsync(o);
        await using var stream = await StreamAsync(s);
        s.Objects.Time = new FixedTime(at);
        s.Objects.NotFound(KeyOf(o));

        var failed = await Assert.ThrowsAsync<FileReadException>(() => stream.ReadAsync(new byte[10], Ct).AsTask());
        Assert.Equal((FileReadState.Missing, "CDN_NOT_FOUND", (DateTimeOffset?)at), (failed.State, failed.Code, failed.ObservedAt));
    }

    private sealed class MutableKeys : ITactKeys
    {
        public Dictionary<ulong, byte[]> Keys { get; } = [];
        public ReadOnlyMemory<byte>? Find(ulong keyName) => Keys.TryGetValue(keyName, out var key) ? key : (ReadOnlyMemory<byte>?)null;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
