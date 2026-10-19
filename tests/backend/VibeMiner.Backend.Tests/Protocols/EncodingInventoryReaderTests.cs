using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class EncodingInventoryReaderTests
{
    private const int PageBytes = 1024;
    private static readonly byte[] Specs = Encoding.ASCII.GetBytes("b:*=z\0");

    [Fact]
    public async Task EnumeratesBothTablesOrderedCandidatesSpecificationsAndScalarCompletion()
    {
        var source = Fixture(
            [Page(Content(1, (1L << 32) + 7, 11, 12)), Page(Content(2, 8, 13))],
            [Page(Encoded(11, (1L << 32) + 99, 0), Encoded(12, 12, 1)), Page(Encoded(13, 13, 0))],
            trailer: "opaque metadata"u8.ToArray());
        var sink = new Sink();
        using var input = new FragmentedAsyncInput(source);
        var complete = await EncodingInventoryReader.ReadAsync(input, new(), sink);
        Assert.Equal(2, complete.ContentPages);
        Assert.Equal(2, complete.EncodingPages);
        Assert.Equal(2, complete.ContentRecords);
        Assert.Equal(3, complete.EncodingRecords);
        Assert.Equal(3, complete.CandidateReferences);
        Assert.Equal(source.Length, complete.ConsumedBytes);
        Assert.Equal(["b:*=z", ""], sink.Specifications);
        Assert.Equal("opaque metadata", sink.Trailer);
        Assert.Equal((1L << 32) + 7, sink.Contents[0].Entry.DecodedBytes);
        Assert.Equal([Key(11), Key(12)], sink.Contents[0].Entry.EncodingKeys);
        Assert.Equal((1L << 32) + 99, sink.Encodings[0].Entry.EncodedBytes);
        Assert.Equal(1u, sink.Encodings[1].Entry.SpecificationIndex);
        Assert.Equal([(EncodingTable.Content, 0), (EncodingTable.Content, 1), (EncodingTable.Encoding, 0), (EncodingTable.Encoding, 1)], sink.CompletedPages);
        Assert.Equal(1, sink.Contents[1].Page);
        Assert.Equal(1, sink.Contents[1].Ordinal);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task AdmitsEmptyTablesWithNoSpecificationsOrTrailer()
    {
        var sink = new Sink();
        var complete = await Read(Fixture([], [], specifications: []), sink);
        Assert.Equal(0, complete.ContentRecords);
        Assert.Equal(0, complete.EncodingRecords);
        Assert.Empty(sink.Specifications);
        Assert.Empty(sink.CompletedPages);
        Assert.Equal("", sink.Trailer);
    }

    [Fact]
    public async Task RejectsEveryMissingByteOfRequiredFraming()
    {
        var full = Fixture([Page(Content(1, 7, 11))], [Page(Encoded(11, 9, 0))]);
        for (var length = 0; length < full.Length; length++)
            Assert.Equal("EN_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Read(full[..length], new Sink()))).Code);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 9)]
    [InlineData(4, 9)]
    [InlineData(17, 1)]
    public async Task RejectsUnsupportedLayouts(int offset, byte value)
    {
        var source = Fixture([], []); source[offset] = value;
        await AssertCode("EN_UNSUPPORTED", source);
    }

    [Fact]
    public async Task RejectsBadMagicAndZeroPageGeometry()
    {
        var source = Fixture([], []); source[0] = (byte)'X';
        await AssertCode("EN_INVALID", source);
        source = Fixture([], []); source[5] = source[6] = 0;
        await AssertCode("EN_INVALID", source);
    }

    [Fact]
    public async Task AuthenticatesPagesBeforeEmittingTheirRows()
    {
        var source = Fixture([Page(Content(1, 7, 11))], []);
        source[22 + Specs.Length + 32 + 100] ^= 1;
        var sink = new Sink();
        await AssertCode("EN_INTEGRITY", source, sink);
        Assert.Empty(sink.Contents);
        Assert.Empty(sink.CompletedPages);
    }

    [Fact]
    public async Task RejectsDuplicateUnorderedAndUnboundIndexKeys()
    {
        await AssertCode("EN_INVALID", Fixture([Page(Content(1, 1, 11)), Page(Content(1, 2, 12))], []));
        await AssertCode("EN_INVALID", Fixture([Page(Content(2, 1, 11)), Page(Content(1, 2, 12))], []));
        var source = Fixture([Page(Content(1, 1, 11))], []);
        KeyBytes(2).CopyTo(source, 22 + Specs.Length);
        await AssertCode("EN_INVALID", source);
        Array.Clear(source, 22 + Specs.Length, 16);
        await AssertCode("EN_INVALID", source);
    }

    [Fact]
    public async Task RejectsRowsCrossingIndexIntervalsAndDuplicateRows()
    {
        await AssertCode("EN_INVALID", Fixture([Page(Content(1, 1, 11), Content(3, 3, 13)), Page(Content(2, 2, 12))], []));
        await AssertCode("EN_INVALID", Fixture([Page(Content(1, 1, 11), Content(1, 2, 12))], []));
        await AssertCode("EN_INVALID", Fixture([], [Page(Encoded(11, 1, 0), Encoded(11, 2, 0))]));
    }

    [Fact]
    public async Task RejectsCrossPageRowsEmptyPagesZeroCandidatesAndNonzeroPadding()
    {
        var crossed = Page(Content(1, 1, 11)); crossed[38] = 255;
        await AssertCode("EN_INVALID", Fixture([crossed], []));
        var padding = Page(Content(1, 1, 11)); padding[^1] = 1;
        await AssertCode("EN_INVALID", Fixture([padding], []));
        var zeroCandidate = Page(Content(1, 1, 0));
        await AssertCode("EN_INVALID", Fixture([zeroCandidate], []));
        var empty = Fixture([new byte[PageBytes]], []);
        KeyBytes(1).CopyTo(empty, 22 + Specs.Length);
        await AssertCode("EN_INVALID", empty);
    }

    [Fact]
    public async Task ValidatesSpecificationFramingCountAndReferences()
    {
        await AssertCode("EN_INVALID", Fixture([], [], specifications: "unterminated"u8.ToArray()));
        await AssertCode("EN_UNSUPPORTED", Fixture([], [], specifications: [0xff, 0]));
        await AssertCode("EN_INVALID", Fixture([], [Page(Encoded(11, 1, 99))]));
        await AssertCode("EN_INVALID", Fixture([], [Page(Encoded(11, 1, 0))], specifications: []));
        await AssertCode("EN_LIMIT", Fixture([], []), limits: new(MaxSpecificationStrings: 1));
        await AssertCode("EN_LIMIT", Fixture([], []), limits: new(MaxSpecificationBytes: 1));
    }

    [Fact]
    public async Task AdmitsOnlyTheLastPagesEncodingFinalMarkerAndZeroPadding()
    {
        var marked = Page(Encoded(11, 1, 0));
        marked.AsSpan(25 + 16, 4).Fill(255);
        await Read(Fixture([], [marked]), new Sink());
        await AssertCode("EN_INVALID", Fixture([], [marked, Page(Encoded(12, 2, 0))]));
        marked[^1] = 1;
        await AssertCode("EN_INVALID", Fixture([], [marked]));
    }

    [Fact]
    public async Task TrailerIsBoundedPrintableAndCanInvalidatePreviouslyCompletedPages()
    {
        var sink = new Sink();
        await AssertCode("EN_UNSUPPORTED", Fixture([Page(Content(1, 1, 11))], [], trailer: [0]), sink);
        Assert.Single(sink.CompletedPages);
        Assert.Null(sink.Trailer);
        await AssertCode("EN_LIMIT", Fixture([], [], trailer: "xx"u8.ToArray()), limits: new(MaxTrailerBytes: 1));
    }

    [Fact]
    public async Task EnforcesHeaderWorkBoundsAndPerPageAggregateCandidateLimits()
    {
        var one = Fixture([Page(Content(1, 1, 11))], []);
        await AssertCode("EN_LIMIT", one, limits: new(MaxDecodedBytes: one.Length - 1));
        await AssertCode("EN_LIMIT", one, limits: new(MaxPages: 0));
        await AssertCode("EN_LIMIT", one, limits: new(MaxIndexBytes: 31));
        await AssertCode("EN_LIMIT", one, limits: new(MaxRecords: 0));
        var two = Fixture([Page(Content(1, 1, 11), Content(2, 2, 12, 13))], []);
        await AssertCode("EN_LIMIT", two, limits: new(MaxKeysPerContent: 1));
        await AssertCode("EN_LIMIT", two, limits: new(MaxRecordsPerPage: 1));
        var sink = new Sink();
        await AssertCode("EN_LIMIT", two, sink, new(MaxCandidateReferences: 2));
        Assert.Single(sink.Contents);
        Assert.Empty(sink.CompletedPages);
        await AssertCode("EN_LIMIT", two, limits: new(MaxRecords: 1));
        var hugePages = Fixture([], []);
        BinaryPrimitives.WriteUInt32BigEndian(hugePages.AsSpan(9), uint.MaxValue);
        await AssertCode("EN_LIMIT", hugePages);
    }

    [Fact]
    public async Task InvalidLimitPolicyFailsBeforeInputIsRead()
    {
        using var input = new FragmentedAsyncInput([]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => EncodingInventoryReader.ReadAsync(input, new(MaxPageBytes: 1), new Sink()));
        Assert.Equal(0, input.Position);
    }

    [Fact]
    public async Task CancellationBeforeInputAndFromRowCallbacksPreventsCompletion()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var input = new FragmentedAsyncInput([]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EncodingInventoryReader.ReadAsync(input, new(), new Sink(), canceled.Token));
        Assert.Equal(0, input.Position);
        using var during = new CancellationTokenSource();
        var sink = new Sink { AfterContent = () => during.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(Fixture([Page(Content(1, 1, 11), Content(2, 2, 12))], []), sink, token: during.Token));
        Assert.Single(sink.Contents);
        Assert.Empty(sink.CompletedPages);
        Assert.Null(sink.Trailer);
    }

    [Fact]
    public async Task ConsumerAwaitsProvideBackpressureAndSinkFailuresPropagateUnchanged()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new Sink { ContentGate = async () => { entered.TrySetResult(); await release.Task; } };
        var work = Read(Fixture([Page(Content(1, 1, 11), Content(2, 2, 12))], []), sink);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(sink.Contents);
        Assert.Empty(sink.CompletedPages);
        Assert.False(work.IsCompleted);
        release.SetResult();
        await work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sink.Contents.Count);
        var failure = new InvalidOperationException("authored sink failure");
        var failedSink = new Sink { AfterContent = () => throw failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => Read(Fixture([Page(Content(1, 1, 11))], []), failedSink)));
        Assert.Empty(failedSink.CompletedPages);
    }

    [Fact]
    public async Task FinalEofIoFailureLeavesAllPageCallbacksProvisional()
    {
        var source = Fixture([Page(Content(1, 1, 11))], []);
        var failure = new IOException("authored final EOF failure");
        using var input = new FragmentedAsyncInput(source, failure);
        var sink = new Sink();
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => EncodingInventoryReader.ReadAsync(input, new(), sink)));
        Assert.Single(sink.CompletedPages);
        Assert.Null(sink.Trailer);
        Assert.True(input.CanRead);
    }

    private static async Task<EncodingCompletion> Read(byte[] source, Sink sink, EncodingLimits? limits = null, CancellationToken token = default)
    {
        using var input = new MemoryStream(source);
        return await EncodingInventoryReader.ReadAsync(input, limits ?? new(), sink, token);
    }
    private static async Task AssertCode(string code, byte[] source, Sink? sink = null, EncodingLimits? limits = null) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() => Read(source, sink ?? new Sink(), limits))).Code);

    private sealed class Sink : IEncodingConsumer
    {
        public readonly List<string> Specifications = [];
        public readonly List<(int Page, long Ordinal, EncodingContentEntry Entry)> Contents = [];
        public readonly List<(int Page, long Ordinal, EncodingEncodedEntry Entry)> Encodings = [];
        public readonly List<(EncodingTable, int)> CompletedPages = [];
        public string? Trailer;
        public Action? AfterContent;
        public Func<Task>? ContentGate;
        public ValueTask SpecificationAsync(int ordinal, string specification, CancellationToken token) { Specifications.Add(specification); return ValueTask.CompletedTask; }
        public async ValueTask ContentAsync(int page, long ordinal, EncodingContentEntry entry, CancellationToken token)
        {
            Contents.Add((page, ordinal, entry)); AfterContent?.Invoke(); if (ContentGate is not null) await ContentGate();
        }
        public ValueTask EncodingAsync(int page, long ordinal, EncodingEncodedEntry entry, CancellationToken token) { Encodings.Add((page, ordinal, entry)); return ValueTask.CompletedTask; }
        public ValueTask PageCompletedAsync(EncodingTable table, int ordinal, CancellationToken token) { CompletedPages.Add((table, ordinal)); return ValueTask.CompletedTask; }
        public ValueTask TrailerAsync(string trailer, CancellationToken token) { Trailer = trailer; return ValueTask.CompletedTask; }
    }

    private static byte[] Fixture(byte[][] contentPages, byte[][] encodingPages, byte[]? specifications = null, byte[]? trailer = null)
    {
        var specs = specifications ?? Specs;
        using var output = new MemoryStream();
        output.Write("EN"u8); output.Write([1, 16, 16]);
        U16(output, 1); U16(output, 1); U32(output, (uint)contentPages.Length); U32(output, (uint)encodingPages.Length);
        output.WriteByte(0); U32(output, (uint)specs.Length); output.Write(specs);
        foreach (var page in contentPages) { output.Write(page.AsSpan(6, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in contentPages) output.Write(page);
        foreach (var page in encodingPages) { output.Write(page.AsSpan(0, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in encodingPages) output.Write(page);
        if (trailer is not null) output.Write(trailer);
        return output.ToArray();
    }
    private static byte[] Page(params byte[][] records)
    {
        var page = new byte[PageBytes]; var offset = 0;
        foreach (var record in records) { record.CopyTo(page, offset); offset += record.Length; }
        return page;
    }
    private static byte[] Content(uint key, long size, params uint[] encodings)
    {
        using var row = new MemoryStream(); row.WriteByte((byte)encodings.Length); U40(row, size); row.Write(KeyBytes(key));
        foreach (var encoding in encodings) row.Write(KeyBytes(encoding));
        return row.ToArray();
    }
    private static byte[] Encoded(uint key, long size, uint spec)
    {
        using var row = new MemoryStream(); row.Write(KeyBytes(key)); U32(row, spec); U40(row, size); return row.ToArray();
    }
    private static byte[] KeyBytes(uint value) { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), value); return bytes; }
    private static string Key(uint value) => Convert.ToHexStringLower(KeyBytes(value));
    private static void U16(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void U32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }
    private static void U40(Stream stream, long value) { stream.WriteByte((byte)(value >> 32)); U32(stream, (uint)value); }

    private sealed class FragmentedAsyncInput : MemoryStream
    {
        private readonly byte[] bytes;
        private readonly Exception? eofFailure;
        public FragmentedAsyncInput(byte[] bytes, Exception? eofFailure = null) : base(bytes) { this.bytes = bytes; this.eofFailure = eofFailure; }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous input I/O");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous input I/O");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position == Length && eofFailure is not null) throw eofFailure;
            var count = Math.Min(7, Math.Min(buffer.Length, (int)(Length - Position)));
            bytes.AsMemory((int)Position, count).CopyTo(buffer); Position += count;
            return ValueTask.FromResult(count);
        }
    }
}
