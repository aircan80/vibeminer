using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class BlteEncodedVerifierTests
{
    // Original authored vectors; fixed MD5 values make the verifier independent of fixture construction.
    private static readonly byte[] Golden = Convert.FromHexString(
        "424c54450000006c0f00000400000003000000021556a8a2b19b9d0164d706444cd77d06" +
        "00000009000000006fb87e52f90ab0d490d72e21bc290f86000000042a05f2001eacdc05" +
        "266d577024892067633d649900000002ffffffff5943c7969a083f38448b531862eee38c" +
        "4e68695a789c030000000001450102033fff");
    private const string GoldenKey = "1665999875288301a5c925d7f3e666dd";

    [Fact]
    public async Task AuthenticatesGoldenMixedRawZlibEncryptedAndFutureModesWithoutDecoding()
    {
        using var stream = new AsyncInput(Golden, shortRead: 7);
        var proof = await BlteEncodedVerifier.VerifyAsync(stream, GoldenKey.ToUpperInvariant(), Golden.Length);
        Assert.Equal(new BlteEncodedVerification(GoldenKey, Golden.Length, 4, 5_000_000_001L, true), proof);
        Assert.True(stream.CanRead);
        Assert.NotEqual(Md5(Golden), proof.EncodingKey);
    }

    [Fact]
    public async Task AuthenticatesHeaderlessGoldenIncludingItsEightBytePrefix()
    {
        byte[] bytes = Convert.FromHexString("424c5445000000004e6869");
        using var stream = new AsyncInput(bytes);
        var proof = await BlteEncodedVerifier.VerifyAsync(stream, "b58e2878cc5ec3ac04f4e470be866dd6", bytes.Length);
        Assert.Equal(new BlteEncodedVerification("b58e2878cc5ec3ac04f4e470be866dd6", 11, 1, null, false), proof);
        await AssertCode("BLTE_INTEGRITY", bytes, Md5(bytes[8..]));
    }

    [Theory]
    [InlineData(false, (byte)'N')]
    [InlineData(false, (byte)'Z')]
    [InlineData(false, (byte)'E')]
    [InlineData(false, (byte)'?')]
    [InlineData(true, (byte)'N')]
    [InlineData(true, (byte)'Z')]
    [InlineData(true, (byte)'E')]
    [InlineData(true, (byte)'?')]
    public async Task RetainsOpaqueModeBytesWithoutDecodedSizeOrCompressionAssertions(bool headered, byte mode)
    {
        // Deliberately not valid zlib/encryption framing: only encoded authentication is claimed.
        var fixture = Create(headered, ([mode, 255, 0, 19], uint.MaxValue));
        var proof = await Verify(fixture.Bytes, fixture.Key);
        Assert.Equal(headered ? (long?)uint.MaxValue : null, proof.DeclaredDecodedLength);
        Assert.Equal(headered, proof.Headered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsEveryPhysicalTruncationAndLeavesCallerStreamOpen(bool headered)
    {
        var fixture = Create(headered, ("Ntruncation"u8.ToArray(), 10));
        for (var length = 0; length < fixture.Bytes.Length; length++)
        {
            using var stream = new AsyncInput(fixture.Bytes[..length]);
            var error = await Assert.ThrowsAsync<DiscoveryException>(() =>
                BlteEncodedVerifier.VerifyAsync(stream, fixture.Key, fixture.Bytes.Length));
            Assert.Equal("BLTE_INVALID", error.Code);
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task DetectsEveryChunkCorruptionIncludingModeBytesAndIndependentHeaderKey()
    {
        await AssertCode("BLTE_INTEGRITY", Golden, new string('0', 32));
        for (var offset = 108; offset < Golden.Length; offset++)
        {
            var corrupt = Golden.ToArray(); corrupt[offset] ^= 1;
            await AssertCode("BLTE_INTEGRITY", corrupt, GoldenKey);
        }
        var changedDescriptor = Golden.ToArray(); changedDescriptor[20] ^= 1;
        await AssertCode("BLTE_INTEGRITY", changedDescriptor, GoldenKey);
        // Re-authenticating a corrupt checksum table cannot make its payload authenticate.
        await AssertCode("BLTE_INTEGRITY", changedDescriptor, Md5(changedDescriptor[..108]));
        byte[] headerless = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', 1];
        var expected = Md5(headerless); headerless[^1] ^= 1;
        await AssertCode("BLTE_INTEGRITY", headerless, expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsExternalTrailingBytesAndIncorrectDeclaredEncodedLengths(bool headered)
    {
        var fixture = Create(headered, ("Nlength"u8.ToArray(), 6));
        await AssertCode("BLTE_INVALID", [.. fixture.Bytes, 0], fixture.Key, fixture.Bytes.Length);
        await AssertCode("BLTE_INVALID", fixture.Bytes, fixture.Key, fixture.Bytes.Length + 1);
        await AssertCode("BLTE_INVALID", fixture.Bytes, fixture.Key, fixture.Bytes.Length - 1);
    }

    [Fact]
    public async Task RejectsMalformedTableGeometryAndZeroLengthChunks()
    {
        foreach (var headerLength in new uint[] { 1, 8, 11, 13, 35, 37, 100 })
        {
            var bytes = Create(true, ([(byte)'N'], 0)).Bytes;
            PutU32(bytes, 4, headerLength);
            await AssertCode("BLTE_INVALID", bytes, new string('0', 32));
        }
        var zeroCount = Create(true).Bytes;
        await AssertCode("BLTE_INVALID", zeroCount, Md5(zeroCount));
        var invalid = Create(true, ([(byte)'N'], 0)).Bytes;
        PutU32(invalid, 12, 0);
        await AssertCode("BLTE_INVALID", invalid, Md5(invalid[..36]));
        invalid = Create(true, ([(byte)'N'], 0)).Bytes;
        invalid[11] = 2;
        await AssertCode("BLTE_INVALID", invalid, new string('0', 32));
    }

    [Fact]
    public async Task UsesUnsignedLongTotalsRatherThanOverflowingOrApplyingDecodedAllocationLimits()
    {
        var fixture = Create(true, ([(byte)'E'], uint.MaxValue), ([(byte)'?'], uint.MaxValue), ([(byte)'Z'], uint.MaxValue));
        var proof = await Verify(fixture.Bytes, fixture.Key);
        Assert.Equal(12_884_901_885L, proof.DeclaredDecodedLength);
        PutU32(fixture.Bytes, 12, uint.MaxValue);
        await AssertCode("BLTE_LIMIT", fixture.Bytes, new string('0', 32));
    }

    [Fact]
    public async Task RejectsNonBlteAndUnknownTableFramingExplicitly()
    {
        await AssertCode("BLTE_UNSUPPORTED", "not a BLTE object"u8.ToArray(), new string('0', 32));
        var bytes = Create(true, ([(byte)'N'], 0)).Bytes; bytes[8] = 14;
        await AssertCode("BLTE_UNSUPPORTED", bytes, Md5(bytes[..36]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234")]
    [InlineData("0000000000000000000000000000000g")]
    public async Task RejectsNonFullMd5KeysBeforeReading(string? key)
    {
        using var stream = new AsyncInput(Golden);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteEncodedVerifier.VerifyAsync(stream, key!, Golden.Length));
        Assert.Equal("BLTE_INVALID", error.Code);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(-1L, "BLTE_INVALID")]
    [InlineData(0L, "BLTE_INVALID")]
    [InlineData(8L, "BLTE_INVALID")]
    [InlineData(536870913L, "BLTE_LIMIT")]
    [InlineData(long.MaxValue, "BLTE_LIMIT")]
    public async Task BoundsObjectLengthBeforeReading(long length, string code)
    {
        using var stream = new AsyncInput(Golden);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteEncodedVerifier.VerifyAsync(stream, GoldenKey, length));
        Assert.Equal(code, error.Code);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task BoundsHeadersAndChunkCountsBeforeAllocatingOrReadingDescriptors()
    {
        byte[] prefix = [.. "BLTE"u8, 255, 255, 255, 255];
        await AssertCode("BLTE_LIMIT", prefix, GoldenKey, 100);
        byte[] excessiveCount = [.. "BLTE"u8, 0, 0, 0, 36, 15, 1, 0, 1];
        using var stream = new AsyncInput(excessiveCount);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteEncodedVerifier.VerifyAsync(stream, GoldenKey, 100));
        Assert.Equal("BLTE_LIMIT", error.Code);
        Assert.Equal(12, stream.BytesRead);
    }

    [Fact]
    public async Task AcceptsMaximumChunkCountWithBoundedReadSlices()
    {
        var chunks = Enumerable.Range(0, 65_536).Select(_ => (new[] { (byte)'?' }, 0U)).ToArray();
        var fixture = Create(true, chunks);
        using var stream = new AsyncInput(fixture.Bytes);
        var proof = await BlteEncodedVerifier.VerifyAsync(stream, fixture.Key, fixture.Bytes.Length);
        Assert.Equal(65_536, proof.ChunkCount);
        Assert.InRange(stream.MaxReadRequest, 1, 65_536);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamsLargeChunksWithoutModeSpecificBufferLimits(bool headered)
    {
        var block = new byte[17 * 1024 * 1024 + 1]; block[0] = (byte)'Z'; block[^1] = 42;
        var fixture = Create(headered, (block, uint.MaxValue));
        using var stream = new AsyncInput(fixture.Bytes);
        var proof = await BlteEncodedVerifier.VerifyAsync(stream, fixture.Key, fixture.Bytes.Length);
        Assert.Equal(fixture.Bytes.Length, proof.EncodedLength);
        Assert.InRange(stream.MaxReadRequest, 1, 65_536);
        Assert.Equal(fixture.Bytes.Length, stream.BytesRead);
    }

    [Fact]
    public async Task ObservesCancellationBeforeReadingAndWithinHeaderPayloadAndEofReads()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var untouched = new AsyncInput(Golden);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BlteEncodedVerifier.VerifyAsync(untouched, GoldenKey, Golden.Length, canceled.Token));
        Assert.Equal(0, untouched.BytesRead);
        var large = Create(true, (new byte[200_000], 0));
        foreach (var afterBytes in new[] { 8, 12, 36, 36 + 65_536, large.Bytes.Length })
        {
            using var during = new CancellationTokenSource();
            using var stream = new AsyncInput(large.Bytes, cancel: during, cancelAfter: afterBytes);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BlteEncodedVerifier.VerifyAsync(stream, large.Key, large.Bytes.Length, during.Token));
            Assert.InRange(stream.BytesRead, afterBytes, afterBytes + 65_535);
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task ObservesCancellationAcrossDescriptorSlicesAndOnFinalEof()
    {
        var fixture = Create(true, Enumerable.Range(0, 4_000).Select(_ => (new[] { (byte)'?' }, 0U)).ToArray());
        using var headerCancel = new CancellationTokenSource();
        using var header = new AsyncInput(fixture.Bytes, cancel: headerCancel, cancelAfter: 12 + 65_536);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BlteEncodedVerifier.VerifyAsync(header, fixture.Key, fixture.Bytes.Length, headerCancel.Token));
        Assert.Equal(12 + 65_536, header.BytesRead);

        using var eofCancel = new CancellationTokenSource();
        using var eof = new AsyncInput(Golden, cancel: eofCancel, cancelOnEof: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BlteEncodedVerifier.VerifyAsync(eof, GoldenKey, Golden.Length, eofCancel.Token));
        Assert.Equal(Golden.Length, eof.BytesRead);
    }

    [Fact]
    public async Task PreservesIoFailuresAndStreamOwnership()
    {
        using var stream = new AsyncInput(Golden, failAfter: 108);
        await Assert.ThrowsAsync<IOException>(() => BlteEncodedVerifier.VerifyAsync(stream, GoldenKey, Golden.Length));
        Assert.True(stream.CanRead);
    }

    private static async Task<BlteEncodedVerification> Verify(byte[] bytes, string key)
    {
        using var stream = new AsyncInput(bytes);
        return await BlteEncodedVerifier.VerifyAsync(stream, key, bytes.Length);
    }

    private static async Task AssertCode(string code, byte[] bytes, string key, long? length = null)
    {
        using var stream = new AsyncInput(bytes);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => BlteEncodedVerifier.VerifyAsync(stream, key, length ?? bytes.Length));
        Assert.Equal(code, error.Code);
    }

    private static (byte[] Bytes, string Key) Create(bool headered, params (byte[] Bytes, uint DeclaredDecoded)[] chunks)
    {
        var headerLength = headered ? 12 + 24 * chunks.Length : 8;
        var bytes = new byte[headerLength + chunks.Sum(chunk => chunk.Bytes.Length)];
        "BLTE"u8.CopyTo(bytes); PutU32(bytes, 4, headered ? (uint)headerLength : 0);
        if (headered)
        {
            bytes[8] = 15; bytes[9] = (byte)(chunks.Length >> 16); bytes[10] = (byte)(chunks.Length >> 8); bytes[11] = (byte)chunks.Length;
            for (var index = 0; index < chunks.Length; index++)
            {
                var at = 12 + index * 24;
                PutU32(bytes, at, (uint)chunks[index].Bytes.Length); PutU32(bytes, at + 4, chunks[index].DeclaredDecoded);
                MD5.HashData(chunks[index].Bytes).CopyTo(bytes, at + 8);
            }
        }
        var offset = headerLength;
        foreach (var chunk in chunks) { chunk.Bytes.CopyTo(bytes, offset); offset += chunk.Bytes.Length; }
        return (bytes, Md5(headered ? bytes[..headerLength] : bytes));
    }

    private static void PutU32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value);
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));

    // Nonseekable async-only input also records requested buffers, independent of physical short reads.
    private sealed class AsyncInput(byte[] bytes, int shortRead = int.MaxValue,
        CancellationTokenSource? cancel = null, int cancelAfter = int.MaxValue, int failAfter = int.MaxValue,
        bool cancelOnEof = false) : Stream
    {
        private bool disposed;
        public int BytesRead { get; private set; }
        public int MaxReadRequest { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BytesRead >= failAfter) throw new IOException("Authored read failure.");
            MaxReadRequest = Math.Max(MaxReadRequest, buffer.Length);
            var count = Math.Min(shortRead, Math.Min(buffer.Length, bytes.Length - BytesRead));
            bytes.AsMemory(BytesRead, count).CopyTo(buffer); BytesRead += count;
            if (BytesRead >= cancelAfter) cancel?.Cancel();
            if (count == 0 && cancelOnEof) cancel?.Cancel();
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O is prohibited.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous I/O is prohibited.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
