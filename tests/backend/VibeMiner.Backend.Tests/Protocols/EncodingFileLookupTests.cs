using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class EncodingFileLookupTests
{
    [Fact]
    public async Task NativeDirectoriesResolvePageBoundariesCandidatesAndWideSizesWithoutReadingOtherPages()
    {
        using var stream = new AsyncStream(Fixture(
            [Page(Content(2, 7, 11, 12)), Page(Content(5, (1L << 32) + 9, 15))],
            [Page(Encoded(11, 17), Encoded(12, 18)), Page(Encoded(15, (1L << 32) + 19))]));
        var lookup = await EncodingFileLookup.OpenAsync(stream, new());
        Assert.Equal(2, lookup.Completion.ContentRecords);
        stream.ReadBytes = 0;
        var first = await lookup.FindContentAsync(Key(2).ToUpperInvariant());
        Assert.Equal([Key(11), Key(12)], first!.EncodingKeys);
        Assert.Equal(1024, stream.ReadBytes);
        stream.ReadBytes = 0;
        Assert.Equal((1L << 32) + 9, (await lookup.FindContentAsync(Key(5)))!.DecodedBytes);
        Assert.Equal(1024, stream.ReadBytes);
        stream.ReadBytes = 0;
        Assert.Equal((1L << 32) + 19, (await lookup.FindEncodingAsync(Key(15)))!.EncodedBytes);
        Assert.Equal(1024, stream.ReadBytes);
        Assert.Equal(18, (await lookup.FindEncodingAsync(Key(12)))!.EncodedBytes);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task AbsenceBeforeBetweenAndAfterNativeIntervalsIsExplicit()
    {
        using var stream = new AsyncStream(Fixture([Page(Content(2, 7, 11)), Page(Content(5, 9, 15))],
            [Page(Encoded(11, 17)), Page(Encoded(15, 19))]));
        var lookup = await EncodingFileLookup.OpenAsync(stream, new());
        stream.ReadBytes = 0;
        Assert.Null(await lookup.FindContentAsync(Key(1)));
        Assert.Equal(0, stream.ReadBytes);
        Assert.Null(await lookup.FindContentAsync(Key(3)));
        Assert.Null(await lookup.FindContentAsync(Key(6)));
        Assert.Null(await lookup.FindEncodingAsync(Key(10)));
        Assert.Null(await lookup.FindEncodingAsync(Key(13)));
        Assert.Null(await lookup.FindEncodingAsync(Key(16)));
        using var empty = new MemoryStream(Fixture([], []));
        var emptyLookup = await EncodingFileLookup.OpenAsync(empty, new());
        Assert.Null(await emptyLookup.FindContentAsync(Key(1)));
        Assert.Null(await emptyLookup.FindEncodingAsync(Key(1)));
    }

    [Fact]
    public async Task OpenRequiresCompleteValidatedGrammarIncludingUnqueriedPagesAndFinalEof()
    {
        var valid = Fixture([Page(Content(2, 7, 11))], [Page(Encoded(11, 17)), Page(Encoded(15, 19))]);
        var badPage = valid.ToArray(); badPage[^1] ^= 1;
        Assert.Equal("EN_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream(badPage), new()))).Code);
        Assert.Equal("EN_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream([.. valid, 0]), new()))).Code);
        Assert.Equal("EN_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream(valid[..^1]), new()))).Code);
    }

    [Fact]
    public async Task OpenChecksNativeDirectoryOrderGeometryAndPolicyBeforePublishingLookup()
    {
        var invalidDirectory = Fixture([Page(Content(5, 1, 11)), Page(Content(2, 1, 12))], []);
        Assert.Equal("EN_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream(invalidDirectory), new()))).Code);
        var source = Fixture([Page(Content(2, 7, 11))], []);
        Assert.Equal("EN_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream(source), new(MaxIndexBytes: 31)))).Code);
        var geometry = source.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(geometry.AsSpan(9), uint.MaxValue);
        Assert.Equal("EN_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            EncodingFileLookup.OpenAsync(new MemoryStream(geometry), new()))).Code);
    }

    [Fact]
    public async Task CachedDirectoryProofDetectsSourcePageMutationAfterAdmission()
    {
        var source = Fixture([Page(Content(2, 7, 11))], [Page(Encoded(11, 17))]);
        using var input = new MemoryStream(source, writable: true);
        var lookup = await EncodingFileLookup.OpenAsync(input, new());
        source[22 + 1 + 32 + 100] ^= 1;
        Assert.Equal("EN_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => lookup.FindContentAsync(Key(2)))).Code);
        // A failed lookup releases its single-operation guard.
        source[22 + 1 + 32 + 100] ^= 1;
        Assert.NotNull(await lookup.FindContentAsync(Key(2)));
        input.SetLength(input.Length - 1);
        Assert.Equal("EN_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => lookup.FindEncodingAsync(Key(11)))).Code);
    }

    [Fact]
    public async Task SupportsCallerPositionWithoutTakingStreamOwnership()
    {
        var source = Fixture([Page(Content(2, 7, 11))], [Page(Encoded(11, 17))]);
        using var input = new AsyncStream([0xaa, 0xbb, .. source]); input.Position = 2;
        var lookup = await EncodingFileLookup.OpenAsync(input, new());
        Assert.Equal(source.Length, lookup.Completion.ConsumedBytes);
        Assert.Equal(7, (await lookup.FindContentAsync(Key(2)))!.DecodedBytes);
        Assert.Equal(17, (await lookup.FindEncodingAsync(Key(11)))!.EncodedBytes);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task CancellationAndMalformedKeysDoNotReadOrPoisonTheLookup()
    {
        using var input = new AsyncStream(Fixture([Page(Content(2, 7, 11))], []));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncodingFileLookup.OpenAsync(input, new(), canceled.Token));
        Assert.Equal(0, input.ReadBytes);
        var lookup = await EncodingFileLookup.OpenAsync(input, new());
        input.ReadBytes = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.FindContentAsync(Key(2), canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => lookup.FindContentAsync("123"));
        await Assert.ThrowsAsync<ArgumentException>(() => lookup.FindEncodingAsync(new string('0', 32)));
        Assert.Equal(0, input.ReadBytes);
        Assert.NotNull(await lookup.FindContentAsync(Key(2)));
    }

    [Fact]
    public async Task ConcurrentAccessIsRejectedWithoutQueueingOrMovingTheActiveRead()
    {
        using var input = new AsyncStream(Fixture([Page(Content(2, 7, 11))], []));
        var lookup = await EncodingFileLookup.OpenAsync(input, new());
        input.ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        input.ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = lookup.FindContentAsync(Key(2));
        await input.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lookup.FindContentAsync(Key(2)));
        input.ReadRelease.SetResult();
        Assert.NotNull(await first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancellationInsidePageIoReleasesTheGuardAndPreservesCallerStream()
    {
        using var input = new AsyncStream(Fixture([Page(Content(2, 7, 11))], []));
        var lookup = await EncodingFileLookup.OpenAsync(input, new());
        input.ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        input.ReadRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled = new CancellationTokenSource();
        var work = lookup.FindContentAsync(Key(2), canceled.Token);
        await input.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        input.ReadEntered = input.ReadRelease = null;
        Assert.NotNull(await lookup.FindContentAsync(Key(2)));
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task NonseekableInputIsRejectedBeforeParsing()
    {
        using var input = new NonseekableStream(Fixture([], []));
        await Assert.ThrowsAsync<ArgumentException>(() => EncodingFileLookup.OpenAsync(input, new()));
        Assert.Equal(0, input.Position);
    }

    private static byte[] Fixture(byte[][] contents, byte[][] encodings)
    {
        using var output = new MemoryStream();
        output.Write("EN"u8); output.Write([1, 16, 16]);
        U16(output, 1); U16(output, 1); U32(output, (uint)contents.Length); U32(output, (uint)encodings.Length);
        output.WriteByte(0); U32(output, 1); output.WriteByte(0);
        foreach (var page in contents) { output.Write(page.AsSpan(6, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in contents) output.Write(page);
        foreach (var page in encodings) { output.Write(page.AsSpan(0, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in encodings) output.Write(page);
        return output.ToArray();
    }
    private static byte[] Page(params byte[][] entries)
    {
        var result = new byte[1024]; var at = 0;
        foreach (var entry in entries) { entry.CopyTo(result, at); at += entry.Length; }
        return result;
    }
    private static byte[] Content(uint key, long bytes, params uint[] candidates)
    {
        using var row = new MemoryStream(); row.WriteByte((byte)candidates.Length); U40(row, bytes); row.Write(KeyBytes(key));
        foreach (var candidate in candidates) row.Write(KeyBytes(candidate));
        return row.ToArray();
    }
    private static byte[] Encoded(uint key, long bytes)
    { using var row = new MemoryStream(); row.Write(KeyBytes(key)); U32(row, 0); U40(row, bytes); return row.ToArray(); }
    private static byte[] KeyBytes(uint key)
    { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), key); return bytes; }
    private static string Key(uint key) => Convert.ToHexStringLower(KeyBytes(key));
    private static void U16(Stream stream, ushort value)
    { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void U32(Stream stream, uint value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void U40(Stream stream, long value) { stream.WriteByte((byte)(value >> 32)); U32(stream, (uint)value); }

    private sealed class AsyncStream : MemoryStream
    {
        private readonly byte[] bytes;
        public AsyncStream(byte[] bytes) : base(bytes) { this.bytes = bytes; }
        public long ReadBytes;
        public TaskCompletionSource? ReadEntered, ReadRelease;
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous read");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (ReadEntered is not null)
            { ReadEntered.TrySetResult(); await ReadRelease!.Task.WaitAsync(token); }
            var count = (int)Math.Min(Math.Min(buffer.Length, 37), Length - Position);
            bytes.AsMemory((int)Position, count).CopyTo(buffer); Position += count; ReadBytes += count;
            return count;
        }
    }

    private sealed class NonseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
