using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class BlteDecoderTests
{
    private sealed record Fixture(byte[] Encoded, byte[] Content, string EKey, string CKey);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DecodesAndVerifiesRawZlibAndSemanticEncodingKeys(bool chunked, bool zlib)
    {
        var fixture = Create(chunked, (zlib, Encoding.UTF8.GetBytes("original authored fixture: hello")));
        using var source = new AsyncOnlyInput(fixture.Encoded);
        using var target = new AsyncOnlyOutput();
        var proof = await BlteDecoder.DecodeAsync(source, target, fixture.EKey.ToUpperInvariant(), fixture.CKey,
            fixture.Encoded.Length, fixture.Content.Length);
        Assert.Equal(fixture.Content, target.ToArray());
        Assert.Equal(fixture.EKey, proof.EncodingKey);
        Assert.Equal(fixture.CKey, proof.ContentKey);
        Assert.Equal(fixture.Encoded.Length, proof.EncodedLength);
        Assert.Equal(fixture.Content.Length, proof.DecodedLength);
        Assert.Equal(1, proof.ChunkCount);
        if (chunked) Assert.NotEqual(Md5(fixture.Encoded), proof.EncodingKey);
        Assert.True(source.CanRead);
        Assert.True(target.CanWrite);
    }

    [Fact]
    public async Task VerifiesZlibWithManyInflaterInputAndOutputBuffers()
    {
        // Authored deterministic noise exercises input read-ahead across many 8KiB inflater buffers.
        var content = new byte[250_000];
        uint state = 12345;
        for (var i = 0; i < content.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            content[i] = (byte)state;
        }
        var fixture = Create(true, (true, content));
        Assert.True(fixture.Encoded.Length > 200_000);
        using var target = new MemoryStream();
        await Decode(fixture, target);
        Assert.Equal(content, target.ToArray());
        var block = Block(true, content);
        foreach (var tailLength in new[] { 1, 8191, 8192, 16385 })
            await AssertCode("BLTE_INVALID", FromBlocks(true, content, [.. block, .. new byte[tailLength]]));
    }

    [Fact]
    public async Task HandlesMixedChunksAndEmptyChunksWithoutCombiningCompressionMembers()
    {
        var fixture = Create(true, (false, []), (true, []), (false, "first"u8.ToArray()), (true, "second"u8.ToArray()));
        using var output = new MemoryStream();
        var proof = await Decode(fixture, output);
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
        var fixture = Create(chunked, (zlib, "truncation fixture"u8.ToArray()));
        for (var length = 0; length < fixture.Encoded.Length; length++)
        {
            using var input = new MemoryStream(fixture.Encoded[..length]);
            using var output = new MemoryStream();
            var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(input, output,
                fixture.EKey, fixture.CKey, fixture.Encoded.Length, fixture.Content.Length));
            Assert.Equal("BLTE_INVALID", error.Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsReframedZlibTruncationAndTrailingCompressedBytes(bool chunked)
    {
        var content = "zlib member exactness"u8.ToArray();
        var block = Block(true, content);
        for (var size = 1; size < block.Length; size++)
            await AssertCode("BLTE_INVALID", FromBlocks(chunked, content, block[..size]));
        foreach (var tailLength in new[] { 1, 2, 7, 8191, 8192, 16385 })
            await AssertCode("BLTE_INVALID", FromBlocks(chunked, content, [.. block, .. new byte[tailLength]]));
        await AssertCode("BLTE_INVALID", FromBlocks(chunked, content, [.. block, .. Block(true, content)[1..]]));
        var corruptAdler = block.ToArray();
        corruptAdler[^1] ^= 1;
        await AssertCode("BLTE_INVALID", FromBlocks(chunked, content, corruptAdler));
    }

    [Fact]
    public async Task ChecksIndependentEncodingChunkAndDecodedHashes()
    {
        var fixture = Create(true, (false, "integrity"u8.ToArray()));
        await AssertCode("BLTE_INTEGRITY", fixture with { EKey = new string('0', 32) });
        await AssertCode("BLTE_INTEGRITY", fixture with { CKey = new string('0', 32) });
        var changedBody = fixture.Encoded.ToArray(); changedBody[^1] ^= 1;
        await AssertCode("BLTE_INTEGRITY", fixture with { Encoded = changedBody });
        var headerless = Create(false, (false, "headerless"u8.ToArray()));
        await AssertCode("BLTE_INTEGRITY", headerless with { EKey = new string('0', 32) });
    }

    [Fact]
    public async Task RejectsUnknownEncryptedAndUnsupportedTableModes()
    {
        await AssertCode("BLTE_ENCRYPTED", FromBlocks(false, [], [(byte)'E']));
        await AssertCode("BLTE_UNSUPPORTED", FromBlocks(false, [], [(byte)'F']));
        var fixture = Create(true, (false, []));
        fixture.Encoded[8] = 14;
        await AssertCode("BLTE_UNSUPPORTED", fixture);
    }

    [Fact]
    public async Task RejectsIncorrectDeclaredLengthsAndExternalTrailingBytes()
    {
        var fixture = Create(true, (false, "length"u8.ToArray()));
        await AssertCode("BLTE_INVALID", fixture with { Content = [.. fixture.Content, 0] });
        using var output = new MemoryStream();
        using var input = new MemoryStream([.. fixture.Encoded, 0]);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(input, output,
            fixture.EKey, fixture.CKey, fixture.Encoded.Length, fixture.Content.Length));
        Assert.Equal("BLTE_INVALID", error.Code);
        await AssertCode("BLTE_INVALID", Create(false, (true, "length"u8.ToArray())) with { Content = [] });
    }

    [Fact]
    public async Task RejectsMalformedTableCountsUnsignedLengthsAndTotals()
    {
        await AssertCode("BLTE_INVALID", FromBlocks(true, []));
        var fixture = Create(true, (false, "table"u8.ToArray()));
        var zeroEncoded = fixture.Encoded.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(zeroEncoded.AsSpan(12, 4), 0);
        await AssertCode("BLTE_INVALID", fixture with { Encoded = zeroEncoded });
        var hugeDecoded = fixture.Encoded.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(hugeDecoded.AsSpan(16, 4), uint.MaxValue);
        await AssertCode("BLTE_LIMIT", fixture with { Encoded = hugeDecoded });
        var hugeEncoded = fixture.Encoded.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(hugeEncoded.AsSpan(12, 4), uint.MaxValue);
        await AssertCode("BLTE_LIMIT", fixture with { Encoded = hugeEncoded });
        var hugeCount = fixture.Encoded.ToArray(); hugeCount[9] = 255;
        await AssertCode("BLTE_LIMIT", fixture with { Encoded = hugeCount });
        var badHeader = fixture.Encoded.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(badHeader.AsSpan(4, 4), 13);
        await AssertCode("BLTE_INVALID", fixture with { Encoded = badHeader });
    }

    [Fact]
    public async Task BoundsSizesCountsAndWorkingMemoryBeforePayloadAllocation()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(input, output,
            new string('0', 32), new string('0', 32), BlteDecoder.MaxEncodedBytes + 1, 0));
        Assert.Equal("BLTE_LIMIT", error.Code);
        Assert.Equal(0, input.Position);
        error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(input, output,
            new string('0', 32), new string('0', 32), 9, BlteDecoder.MaxDecodedBytes + 1));
        Assert.Equal("BLTE_LIMIT", error.Code);
        byte[] hugeHeader = [.. "BLTE"u8, 255, 255, 255, 255];
        using var headerInput = new MemoryStream(hugeHeader);
        error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(headerInput, output,
            new string('0', 32), new string('0', 32), 100, 0));
        Assert.Equal("BLTE_LIMIT", error.Code);
        using var compressedInput = new MemoryStream([.. "BLTE"u8, 0, 0, 0, 0, (byte)'Z']);
        error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteDecoder.DecodeAsync(compressedInput, output,
            new string('0', 32), new string('0', 32), BlteDecoder.MaxCompressedChunkBytes + 10L, 0));
        Assert.Equal("BLTE_LIMIT", error.Code);
    }

    [Fact]
    public async Task ObservesCancellationBeforeReadingAndBetweenOutputSlices()
    {
        var fixture = Create(false, (true, new byte[200_000]));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decode(fixture, output, canceled.Token));
        using var during = new CancellationTokenSource();
        using var cancelOutput = new CancelingOutput(during);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decode(fixture, cancelOutput, during.Token));
        Assert.InRange(cancelOutput.Length, 1, 65_536);
    }

    private static async Task AssertCode(string code, Fixture fixture)
    {
        using var output = new MemoryStream();
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() => Decode(fixture, output))).Code);
    }
    private static async Task<BlteVerification> Decode(Fixture fixture, Stream output, CancellationToken token = default)
    {
        using var input = new MemoryStream(fixture.Encoded);
        return await BlteDecoder.DecodeAsync(input, output, fixture.EKey, fixture.CKey, fixture.Encoded.Length, fixture.Content.Length, token);
    }
    private static Fixture Create(bool chunked, params (bool Zlib, byte[] Content)[] chunks) =>
        FromBlocks(chunked, chunks.SelectMany(c => c.Content).ToArray(), chunks.Select(c => Block(c.Zlib, c.Content)).ToArray());
    private static Fixture FromBlocks(bool chunked, byte[] content, params byte[][] blocks)
    {
        using var encoded = new MemoryStream();
        encoded.Write("BLTE"u8);
        var headerLength = chunked ? 12 + blocks.Length * 24 : 0;
        WriteU32(encoded, (uint)headerLength);
        if (chunked)
        {
            encoded.WriteByte(15); encoded.WriteByte((byte)(blocks.Length >> 16));
            encoded.WriteByte((byte)(blocks.Length >> 8)); encoded.WriteByte((byte)blocks.Length);
            foreach (var block in blocks)
            {
                WriteU32(encoded, (uint)block.Length);
                // Tests with multiple chunks use valid blocks and derive each decoded length independently.
                using var raw = new MemoryStream(block, 1, block.Length - 1);
                using var plain = new MemoryStream();
                if (blocks.Length == 1) WriteU32(encoded, (uint)content.Length);
                else
                {
                    if (block[0] == 'Z') { using var zlib = new ZLibStream(raw, CompressionMode.Decompress); zlib.CopyTo(plain); }
                    else raw.CopyTo(plain);
                    WriteU32(encoded, (uint)plain.Length);
                }
                encoded.Write(MD5.HashData(block));
            }
        }
        foreach (var block in blocks) encoded.Write(block);
        var bytes = encoded.ToArray();
        return new(bytes, content, Md5(headerLength == 0 ? bytes : bytes[..headerLength]), Md5(content));
    }
    private static byte[] Block(bool zlib, byte[] content)
    {
        using var block = new MemoryStream(); block.WriteByte((byte)(zlib ? 'Z' : 'N'));
        // RFC 1950 header, final empty fixed-Huffman DEFLATE block, and Adler32(empty)=1.
        if (zlib && content.Length == 0) block.Write([0x78, 0x9c, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        else if (zlib) { using var compressor = new ZLibStream(block, CompressionLevel.SmallestSize, leaveOpen: true); compressor.Write(content); }
        else block.Write(content);
        return block.ToArray();
    }
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static void WriteU32(Stream target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); target.Write(bytes);
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
