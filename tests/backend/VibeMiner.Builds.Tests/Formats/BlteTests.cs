using System.Buffers.Binary;
using System.Text;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using Xunit;
using static VibeMiner.Builds.Tests.Formats.BlteWriter;

namespace VibeMiner.Builds.Tests.Formats;

public sealed class BlteTests
{
    private static readonly CdnKey Zero = new(new byte[16]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DecodesAndVerifiesRawAndZlib(bool chunked, bool zlib)
    {
        var content = Encoding.UTF8.GetBytes("original authored fixture: hello");
        var chunk = zlib ? Z(content) : N(content);
        var blte = chunked ? Chunked(chunk) : Headerless(chunk);
        using var source = new AsyncOnlyInput(blte.Encoded);
        using var target = new AsyncOnlyOutput();
        var proof = await Blte.DecodeAsync(source, blte.Encoded.Length, target, blte.EKey, blte.CKey, content.Length);
        Assert.Equal(content, target.ToArray());
        Assert.Equal(new BlteDecoded(blte.Encoded.Length, content.Length, 1), proof);
        if (chunked) Assert.NotEqual(Key(blte.Encoded), blte.EKey);
        Assert.True(source.CanRead);
        Assert.True(target.CanWrite);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContentKeyAndDecodedLengthAreOptional(bool chunked, bool zlib)
    {
        var content = "no declared length"u8.ToArray();
        var chunk = zlib ? Z(content) : N(content);
        var blte = chunked ? Chunked(chunk) : Headerless(chunk);
        using var input = new MemoryStream(blte.Encoded);
        using var output = new MemoryStream();
        var proof = await Blte.DecodeAsync(input, blte.Encoded.Length, output, blte.EKey, null, null);
        Assert.Equal(content, output.ToArray());
        Assert.Equal(content.Length, proof.DecodedLength);
    }

    [Fact]
    public async Task VerifiesZlibAcrossManyInflaterBuffers()
    {
        var content = new byte[250_000];
        uint state = 12345;
        for (var i = 0; i < content.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            content[i] = (byte)state;
        }
        var blte = Chunked(Z(content));
        Assert.True(blte.Encoded.Length > 200_000);
        using var target = new MemoryStream();
        await Decode(blte, target);
        Assert.Equal(content, target.ToArray());
        var block = Z(content).Block;
        foreach (var tail in new[] { 1, 8191, 8192, 16385 })
            await AssertCode("BLTE_INVALID", Chunked(new BlteChunk([.. block, .. new byte[tail]], content)));
    }

    [Fact]
    public async Task HandlesMixedAndEmptyChunksAsSeparateMembers()
    {
        var blte = Chunked(N([]), Z([]), N("first"u8.ToArray()), Z("second"u8.ToArray()));
        using var output = new MemoryStream();
        var proof = await Decode(blte, output);
        Assert.Equal("firstsecond", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(4, proof.ChunkCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RejectsEveryPhysicalTruncation(bool chunked, bool zlib)
    {
        var content = "truncation fixture"u8.ToArray();
        var chunk = zlib ? Z(content) : N(content);
        var blte = chunked ? Chunked(chunk) : Headerless(chunk);
        for (var length = 0; length < blte.Encoded.Length; length++)
        {
            using var input = new MemoryStream(blte.Encoded[..length]);
            using var output = new MemoryStream();
            var error = await Assert.ThrowsAsync<BuildFormatException>(() =>
                Blte.DecodeAsync(input, blte.Encoded.Length, output, blte.EKey, blte.CKey, content.Length));
            Assert.Equal("BLTE_INVALID", error.Code);
            Assert.Equal(BuildFailure.Corrupt, error.Failure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsReframedZlibTruncationAndTrailingCompressedBytes(bool chunked)
    {
        var content = "zlib member exactness"u8.ToArray();
        var block = Z(content).Block;
        BlteObject Frame(byte[] b) => chunked ? Chunked(new BlteChunk(b, content)) : Headerless(new BlteChunk(b, content));
        for (var size = 1; size < block.Length; size++) await AssertCode("BLTE_INVALID", Frame(block[..size]));
        foreach (var tail in new[] { 1, 2, 7, 8191, 8192, 16385 }) await AssertCode("BLTE_INVALID", Frame([.. block, .. new byte[tail]]));
        await AssertCode("BLTE_INVALID", Frame([.. block, .. block[1..]]));
        var corruptAdler = block.ToArray();
        corruptAdler[^1] ^= 1;
        await AssertCode("BLTE_INVALID", Frame(corruptAdler));
    }

    [Fact]
    public async Task ChecksEncodingChunkAndContentHashes()
    {
        var blte = Chunked(N("integrity"u8.ToArray()));
        await AssertCode("BLTE_INTEGRITY", blte with { EKey = Zero });
        await AssertCode("BLTE_INTEGRITY", blte with { CKey = Zero });
        var changedBody = blte.Encoded.ToArray(); changedBody[^1] ^= 1;
        await AssertCode("BLTE_INTEGRITY", blte with { Encoded = changedBody });
        await AssertCode("BLTE_INTEGRITY", Headerless(N("headerless"u8.ToArray())) with { EKey = Zero });
    }

    [Fact]
    public async Task EncryptedChunksAreEncryptedWhereverTheySit()
    {
        var first = Chunked(E("secret"u8.ToArray()), N("clear"u8.ToArray()));
        var later = Chunked(N("clear"u8.ToArray()), Z("more"u8.ToArray()), E("secret"u8.ToArray()));
        foreach (var blte in new[] { Headerless(E("secret"u8.ToArray())), first, later })
        {
            var error = await Throws(blte);
            Assert.Equal((BuildFailure.Encrypted, "BLTE_ENCRYPTED"), (error.Failure, error.Code));
        }
    }

    [Fact]
    public async Task FrameAndUnknownModesAreUnsupported()
    {
        var frame = new BlteChunk([(byte)'F', 1, 2, 3], [0]);
        var unknown = new BlteChunk([(byte)'Q', 4], [4]);
        foreach (var blte in new[] { Headerless(frame), Chunked(N([1]), frame), Headerless(unknown), Chunked(unknown) })
        {
            var error = await Throws(blte);
            Assert.Equal((BuildFailure.Unsupported, "BLTE_UNSUPPORTED"), (error.Failure, error.Code));
        }
        var table = WithHeader(Chunked(N([])), b => b[8] = 14);
        Assert.Equal(BuildFailure.Unsupported, (await Throws(table)).Failure);
    }

    [Fact]
    public async Task ACorruptModeByteIsNotReportedAsEncryptedOrUnsupported()
    {
        var blte = Chunked(N("clear"u8.ToArray()));
        var bytes = blte.Encoded.ToArray();
        bytes[36] = (byte)'E';
        await AssertCode("BLTE_INTEGRITY", blte with { Encoded = bytes });
        var headerless = Headerless(N("clear"u8.ToArray()));
        var flipped = headerless.Encoded.ToArray();
        flipped[8] = (byte)'F';
        await AssertCode("BLTE_INTEGRITY", headerless with { Encoded = flipped });
    }

    [Fact]
    public async Task RejectsWrongDeclaredLengthsAndTrailingBytes()
    {
        var blte = Chunked(N("length"u8.ToArray()));
        await AssertCode("BLTE_INVALID", blte with { Content = [.. blte.Content, 0] });
        using var output = new MemoryStream();
        using var input = new MemoryStream([.. blte.Encoded, 0]);
        var error = await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(input, blte.Encoded.Length, output, blte.EKey, blte.CKey, blte.Content.Length));
        Assert.Equal("BLTE_INVALID", error.Code);
        await AssertCode("BLTE_INVALID", Headerless(Z("length"u8.ToArray())) with { Content = [] });
    }

    [Fact]
    public async Task RejectsMalformedTableCountsLengthsAndTotals()
    {
        await AssertCode("BLTE_INVALID", Chunked());
        var blte = Chunked(N("table"u8.ToArray()));
        await AssertCode("BLTE_INVALID", WithHeader(blte, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), 0)));
        await AssertCode("BLTE_LIMIT", WithHeader(blte, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), uint.MaxValue)));
        await AssertCode("BLTE_LIMIT", WithHeader(blte, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(12, 4), uint.MaxValue)));
        await AssertCode("BLTE_LIMIT", WithHeader(blte, b => b[9] = 255));
        await AssertCode("BLTE_INVALID", WithHeader(blte, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4, 4), 13)));
        await AssertCode("BLTE_INTEGRITY", blte with { Encoded = Edit(blte.Encoded, b => b[8] = 14) });
    }

    [Fact]
    public async Task BoundsSizesCountsAndWorkingMemoryBeforePayloadAllocation()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(input, Blte.MaxEncodedBytes + 1, output, Zero, Zero, 0))).Code);
        Assert.Equal(0, input.Position);
        Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(input, 9, output, Zero, Zero, Blte.MaxDecodedBytes + 1))).Code);
        using var headerInput = new MemoryStream([.. "BLTE"u8, 255, 255, 255, 255]);
        Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(headerInput, 100, output, Zero, Zero, 0))).Code);
        using var compressedInput = new MemoryStream([.. "BLTE"u8, 0, 0, 0, 0, (byte)'Z']);
        Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(compressedInput, Blte.MaxCompressedChunkBytes + 10L, output, Zero, Zero, 0))).Code);
    }

    [Fact]
    public async Task StreamOverloadRefusesAMaxDecodedBytesAboveTheCap()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Blte.DecodeAsync(input, 9, output, Zero, Zero, null, Blte.MaxDecodedBytes + 1));
        Assert.Equal(0, input.Position);
    }

    [Fact]
    public async Task InMemoryOverloadRefusesAMaxDecodedBytesAboveTheCap() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Blte.DecodeAsync(ReadOnlyMemory<byte>.Empty, Zero, Zero, null, Blte.MaxDecodedBytes + 1));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MaxDecodedBytesRefusesWithoutADeclaredLength(bool chunked, bool zlib)
    {
        var content = new byte[1000];
        var chunk = zlib ? Z(content) : N(content);
        var blte = chunked ? Chunked(chunk) : Headerless(chunk);
        using var input = new MemoryStream(blte.Encoded);
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(input, blte.Encoded.Length, output, blte.EKey, null, null, maxDecodedBytes: 999));
        Assert.Equal("BLTE_LIMIT", error.Code);
        Assert.True(output.Length <= 999);
    }

    [Fact]
    public async Task InMemoryDecodeReturnsTheBytesAndHonoursItsLimit()
    {
        var content = "metadata object"u8.ToArray();
        var blte = Chunked(Z(content[..4]), N(content[4..]));
        Assert.Equal(content, await Blte.DecodeAsync(blte.Encoded, blte.EKey, blte.CKey, content.Length, 1024));
        Assert.Equal(content, await Blte.DecodeAsync(new ReadOnlyMemory<byte>([0, .. blte.Encoded])[1..], blte.EKey, null, null, 1024));
        foreach (var declared in new long?[] { null, content.Length })
            Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
                Blte.DecodeAsync(blte.Encoded, blte.EKey, blte.CKey, declared, content.Length - 1))).Code);
        var headerless = Headerless(Z(new byte[5000]));
        Assert.Equal("BLTE_LIMIT", (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(headerless.Encoded, headerless.EKey, null, null, 4999))).Code);
        var encrypted = Chunked(N(content), E(content));
        Assert.Equal(BuildFailure.Encrypted, (await Assert.ThrowsAsync<BuildFormatException>(() =>
            Blte.DecodeAsync(encrypted.Encoded, encrypted.EKey, null, null, 1024))).Failure);
    }

    [Fact]
    public async Task ObservesCancellationBeforeReadingAndBetweenOutputSlices()
    {
        var blte = Headerless(Z(new byte[200_000]));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decode(blte, output, canceled.Token));
        using var during = new CancellationTokenSource();
        using var cancelOutput = new CancelingOutput(during);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decode(blte, cancelOutput, during.Token));
        Assert.InRange(cancelOutput.Length, 1, 65_536);
    }

    private static byte[] Edit(byte[] bytes, Action<byte[]> edit) { var copy = bytes.ToArray(); edit(copy); return copy; }

    private static async Task<BuildFormatException> Throws(BlteObject blte)
    {
        using var output = new MemoryStream();
        return await Assert.ThrowsAsync<BuildFormatException>(() => Decode(blte, output));
    }
    private static async Task AssertCode(string code, BlteObject blte) => Assert.Equal(code, (await Throws(blte)).Code);
    private static async Task<BlteDecoded> Decode(BlteObject blte, Stream output, CancellationToken token = default)
    {
        using var input = new MemoryStream(blte.Encoded);
        return await Blte.DecodeAsync(input, blte.Encoded.Length, output, blte.EKey, blte.CKey, blte.Content.Length, cancellationToken: token);
    }

    private sealed class AsyncOnlyInput : MemoryStream
    {
        private readonly byte[] bytes;
        public AsyncOnlyInput(byte[] bytes) : base(bytes) { this.bytes = bytes; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous source I/O");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous source I/O");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(7, Math.Min(buffer.Length, (int)(Length - Position)));
            bytes.AsMemory((int)Position, count).CopyTo(buffer); Position += count;
            return ValueTask.FromResult(count);
        }
    }
    private sealed class AsyncOnlyOutput : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous target I/O");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous target I/O");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var bytes = buffer.ToArray(); base.Write(bytes, 0, bytes.Length); return ValueTask.CompletedTask;
        }
    }
    private sealed class CancelingOutput(CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await base.WriteAsync(buffer, cancellationToken); cancellation.Cancel();
        }
    }
}
