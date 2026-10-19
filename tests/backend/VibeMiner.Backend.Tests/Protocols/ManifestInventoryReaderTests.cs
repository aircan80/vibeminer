using System.Text;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ManifestInventoryReaderTests
{
    [Fact]
    public async Task InstallPreservesNamesDecodedUnsignedSizesAndOriginalTagOrdinals()
    {
        var sink = new Capture();
        var bytes = Install();
        using var stream = new AsyncOnlyStream(bytes, 3);
        var result = await InstallInventoryReader.ReadAsync(stream, new(), sink);
        Assert.Equal(bytes.Length, result.ConsumedBytes);
        Assert.Equal(9, result.Entries);
        Assert.Equal(["IN-header", "IN-tag", .. Enumerable.Repeat("IN-entry", 9)], sink.Order);
        Assert.Equal("é/file8", sink.InstallEntries[8].Name);
        Assert.Equal(uint.MaxValue, sink.InstallEntries[8].DecodedBytes);
        Assert.Equal(Key(1), sink.InstallEntries[0].ContentKey);
        Assert.Equal(0xbeef, sink.InstallTags[0].RawType);
        Assert.Equal(new byte[] { 0x81, 0xff }, sink.InstallTags[0].Membership.ToArray());
        Assert.Equal(9, sink.InstallTags[0].EntryCount); // padding bits are retained, not extra entries
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 1, 2)]
    [InlineData(2, 0, 0)]
    [InlineData(3, 255, 2)]
    [InlineData(3, 1, 255)]
    [InlineData(3, 0, 0)]
    public async Task DownloadPreservesFullKeysUnsigned40SizesRawPriorityFlagsAndVersion3(byte version, byte checksum, byte flags)
    {
        var sink = new Capture();
        var bytes = Download(version, checksum, flags);
        using var stream = new AsyncOnlyStream(bytes, 2);
        var result = await DownloadInventoryReader.ReadAsync(stream, new(), sink);
        Assert.Equal(version, result.Version);
        Assert.Equal(bytes.Length, result.ConsumedBytes);
        Assert.Equal(checksum, sink.DownloadHeader!.ChecksumPresentRaw);
        Assert.Equal(flags, sink.DownloadHeader.FlagBytes);
        Assert.Equal(version == 3 ? (byte?)0xff : null, sink.DownloadHeader.BasePriorityRaw);
        Assert.Equal(version == 3 ? new byte[] { 3, 0x80, 9 } : [], sink.DownloadHeader.UnknownVersion3Bytes.ToArray());
        Assert.Equal(0x123456789aL, sink.DownloadEntries[0].EncodedBytes);
        Assert.Equal(255, sink.DownloadEntries[0].PriorityRaw);
        Assert.Equal(checksum == 0 ? null : (uint?)0xfedcba98, sink.DownloadEntries[0].Checksum);
        Assert.Equal(Enumerable.Range(0, flags).Select(x => (byte)(x + 1)), sink.DownloadEntries[0].Flags.ToArray());
        Assert.Equal(Enumerable.Range(0, flags).Select(x => (byte)(x + 2)), sink.DownloadEntries[1].Flags.ToArray());
        Assert.Equal(sink.DownloadEntries[0].EncodingKey, sink.DownloadEntries[1].EncodingKey);
        Assert.Equal(["DL-header", "DL-entry", "DL-entry", "DL-tag"], sink.Order);
        Assert.Equal(new byte[] { 0xbf }, sink.DownloadTags[0].Membership.ToArray());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public async Task DownloadSizePreservesDuplicatePrefixesAndUnsignedTotal(byte width)
    {
        var sink = new Capture();
        var bytes = DownloadSize(width);
        using var stream = new AsyncOnlyStream(bytes, 1);
        var result = await DownloadSizeInventoryReader.ReadAsync(stream, new(), sink);
        Assert.Equal(2L * uint.MaxValue, result.MeasuredEncodedBytes);
        Assert.Equal(bytes.Length, result.ConsumedBytes);
        Assert.Equal(["DS-header", "DS-tag", "DS-entry", "DS-entry"], sink.Order);
        Assert.Equal(width * 2, sink.SizeEntries[0].EncodedKey.Hex.Length);
        Assert.Equal(sink.SizeEntries[0].EncodedKey, sink.SizeEntries[1].EncodedKey);
        Assert.Equal(width == 16 ? Key(7) : null, sink.SizeEntries[0].EncodedKey.FullEncodingKey);
        Assert.Equal(new byte[] { 0x7f }, sink.SizeTags[0].Membership.ToArray());
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task EveryTruncationAndTrailingDataFailsWithoutCompletion(string family)
    {
        var bytes = Fixture(family);
        for (var length = 0; length < bytes.Length; length++)
        {
            var failure = await Assert.ThrowsAsync<DiscoveryException>(() => Parse(family, bytes[..length]));
            Assert.Equal(family + "_INVALID", failure.Code);
        }
        Assert.Equal(family + "_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse(family, [.. bytes, 0]))).Code);
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task UnsupportedSignaturesVersionsAndWidthsNeverFallBack(string family)
    {
        foreach (var offset in new[] { 0, 2, 3 })
        {
            var bytes = Fixture(family); bytes[offset] = 0;
            Assert.Equal(family + "_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse(family, bytes))).Code);
        }
    }

    [Fact]
    public async Task InstallRejectsEmptyInvalidUtf8AndUnterminatedNames()
    {
        var bytes = Install();
        bytes[10] = 0;
        Assert.Equal("IN_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse("IN", bytes))).Code);
        bytes = Install(); bytes[10] = 0xff;
        Assert.Equal("IN_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse("IN", bytes))).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => Parse("IN", Install()[..12]));
    }

    [Theory]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task DownloadFamiliesRejectInvalidUtf8TagNames(string family)
    {
        var bytes = Fixture(family);
        var tag = family == "DS" ? 15 : bytes.Length - 5;
        bytes[tag] = 0xff;
        Assert.Equal(family + "_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse(family, bytes))).Code);
    }

    [Fact]
    public async Task DeclaredDownloadSizeSumMismatchFailsAfterProvisionalRows()
    {
        var bytes = DownloadSize(9); bytes[14]--;
        var sink = new Capture();
        Assert.Equal("DS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => Parse("DS", bytes, sink))).Code);
        Assert.Equal(2, sink.SizeEntries.Count);
    }

    [Fact]
    public async Task InstallBoundsCountsNamesTagWorkAndMinimumGeometryBeforeHeader()
    {
        var bytes = Install();
        foreach (var limits in new[] {
            new InstallLimits(MaxEntries: 8), new InstallLimits(MaxTags: 0),
            new InstallLimits(MaxBitmapBytes: 1), new InstallLimits(MaxAggregateTagBytes: 4),
            new InstallLimits(MaxDecodedBytes: 20) })
        {
            var sink = new Capture();
            Assert.Equal("IN_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => InstallInventoryReader.ReadAsync(new MemoryStream(bytes), limits, sink))).Code);
            Assert.Empty(sink.Order);
        }
        foreach (var limits in new[] { new InstallLimits(MaxNameBytes: 1), new InstallLimits(MaxAggregateNameBytes: 3), new InstallLimits(MaxAggregateTagBytes: 6) })
            Assert.Equal("IN_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => InstallInventoryReader.ReadAsync(new MemoryStream(bytes), limits, new Capture()))).Code);
    }

    [Fact]
    public async Task DownloadBoundsCountsFlagsBitmapTagWorkAndMinimumGeometry()
    {
        var bytes = Download(3, 1, 2);
        foreach (var limits in new[] {
            new DownloadLimits(MaxEntries: 1), new DownloadLimits(MaxTags: 0), new DownloadLimits(MaxFlagBytes: 1),
            new DownloadLimits(MaxBitmapBytes: 0), new DownloadLimits(MaxAggregateTagBytes: 3), new DownloadLimits(MaxDecodedBytes: 20) })
        {
            var sink = new Capture();
            Assert.Equal("DL_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => DownloadInventoryReader.ReadAsync(new MemoryStream(bytes), limits, sink))).Code);
            Assert.Empty(sink.Order);
        }
        Assert.Equal("DL_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => DownloadInventoryReader.ReadAsync(new MemoryStream(bytes), new(MaxNameBytes: 0), new Capture()))).Code);
    }

    [Fact]
    public async Task DownloadSizeBoundsCountsBitmapNamesWorkAndMinimumGeometry()
    {
        var bytes = DownloadSize(9);
        foreach (var limits in new[] {
            new DownloadSizeLimits(MaxEntries: 1), new DownloadSizeLimits(MaxTags: 0),
            new DownloadSizeLimits(MaxBitmapBytes: 0), new DownloadSizeLimits(MaxAggregateTagBytes: 3), new DownloadSizeLimits(MaxDecodedBytes: 20) })
        {
            var sink = new Capture();
            Assert.Equal("DS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => DownloadSizeInventoryReader.ReadAsync(new MemoryStream(bytes), limits, sink))).Code);
            Assert.Empty(sink.Order);
        }
        Assert.Equal("DS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => DownloadSizeInventoryReader.ReadAsync(new MemoryStream(bytes), new(MaxNameBytes: 0), new Capture()))).Code);
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task CancellationAfterCallbackStopsEvenWhenInputBuffered(string family)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new Capture { OnEntry = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Parse(family, Fixture(family), sink, cancellation.Token));
        Assert.Equal(1, sink.InstallEntries.Count + sink.DownloadEntries.Count + sink.SizeEntries.Count);
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task SinkBackpressureAndFailuresAreObservedWithoutCompletion(string family)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("authored consumer failure");
        var sink = new Capture { EntryGate = async () => { entered.TrySetResult(); await release.Task; throw expected; } };
        var reading = Parse(family, Fixture(family), sink);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reading.IsCompleted);
        Assert.Equal(1, sink.InstallEntries.Count + sink.DownloadEntries.Count + sink.SizeEntries.Count);
        release.SetResult();
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => reading));
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task EofIoFailurePropagatesAfterProvisionalRowsAndStreamStaysOpen(string family)
    {
        var sink = new Capture();
        using var stream = new AsyncOnlyStream(Fixture(family), 7) { FailAtEof = true };
        await Assert.ThrowsAsync<IOException>(() => ParseStream(family, stream, sink));
        Assert.True(sink.Order.Count > 1);
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task ExactDecodedByteCeilingAdmitsEofButRefusesOneMoreBodyByte(string family)
    {
        var bytes = Fixture(family);
        async Task Read(byte[] body, long ceiling)
        {
            var stream = new AsyncOnlyStream(body, 1);
            if (family == "IN") await InstallInventoryReader.ReadAsync(stream, new(MaxDecodedBytes: ceiling), new Capture());
            else if (family == "DL") await DownloadInventoryReader.ReadAsync(stream, new(MaxDecodedBytes: ceiling), new Capture());
            else await DownloadSizeInventoryReader.ReadAsync(stream, new(MaxDecodedBytes: ceiling), new Capture());
        }
        await Read(bytes, bytes.Length);
        Assert.Equal(family + "_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => Read([.. bytes, 9], bytes.Length))).Code);
        Assert.Equal(family + "_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => Read(bytes, bytes.Length - 1))).Code);
    }

    [Fact]
    public async Task EmptyManifestsRemainValidAndDownloadTagsMayHaveEmptyNames()
    {
        await Parse("IN", [(byte)'I', (byte)'N', 1, 16, 0, 0, 0, 0, 0, 0]);
        await Parse("DL", [(byte)'D', (byte)'L', 1, 16, 0, 0, 0, 0, 0, 0, 0]);
        await Parse("DS", [(byte)'D', (byte)'S', 1, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        foreach (var family in new[] { "DL", "DS" })
        {
            var sink = new Capture();
            var bytes = Fixture(family).ToList();
            bytes.RemoveAt(family == "DS" ? 15 : bytes.Count - 5);
            await Parse(family, bytes.ToArray(), sink);
            Assert.Equal("", family == "DL" ? sink.DownloadTags[0].Name : sink.SizeTags[0].Name);
        }
    }

    [Theory]
    [InlineData("IN")]
    [InlineData("DL")]
    [InlineData("DS")]
    public async Task CancellationDuringRefillAndBeforeAdmissionProducesNoCallbacks(string family)
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var sink = new Capture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Parse(family, Fixture(family), sink, canceled.Token));
        Assert.Empty(sink.Order);
        using var duringRead = new CancellationTokenSource();
        using var stream = new AsyncOnlyStream(Fixture(family), 1) { BeforeRead = () => duringRead.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParseStream(family, stream, sink, duringRead.Token));
        Assert.Empty(sink.Order);
    }

    [Fact]
    public async Task MultipleTagMembershipBuffersOwnTheirBytesAfterFurtherReads()
    {
        var bytes = Download(1, 0, 0).ToList();
        bytes[10] = 2;
        bytes.AddRange([(byte)'u', 0, 0, 8, 0x40]);
        var sink = new Capture();
        await Parse("DL", bytes.ToArray(), sink);
        Assert.Equal(new byte[] { 0xbf }, sink.DownloadTags[0].Membership.ToArray());
        Assert.Equal(new byte[] { 0x40 }, sink.DownloadTags[1].Membership.ToArray());
        Assert.Equal(7, sink.DownloadTags[0].RawType);
        Assert.Equal(8, sink.DownloadTags[1].RawType);
    }

    private static Task Parse(string family, byte[] bytes, Capture? sink = null, CancellationToken token = default) =>
        ParseStream(family, new AsyncOnlyStream(bytes, 3), sink ?? new Capture(), token);
    private static async Task ParseStream(string family, Stream stream, Capture sink, CancellationToken token = default)
    {
        if (family == "IN") await InstallInventoryReader.ReadAsync(stream, new(), sink, token);
        else if (family == "DL") await DownloadInventoryReader.ReadAsync(stream, new(), sink, token);
        else await DownloadSizeInventoryReader.ReadAsync(stream, new(), sink, token);
    }
    private static byte[] Fixture(string family) => family switch { "IN" => Install(), "DL" => Download(3, 1, 2), _ => DownloadSize(9) };
    private static string Key(byte value) => Convert.ToHexStringLower(Enumerable.Repeat(value, 16).ToArray());
    private static void Number(List<byte> output, long value, int width)
    { for (var shift = (width - 1) * 8; shift >= 0; shift -= 8) output.Add((byte)(value >> shift)); }
    private static void Name(List<byte> output, string text) { output.AddRange(Encoding.UTF8.GetBytes(text)); output.Add(0); }
    private static byte[] Install()
    {
        var output = new List<byte> { (byte)'I', (byte)'N', 1, 16 };
        Number(output, 1, 2); Number(output, 9, 4);
        Name(output, "tag"); Number(output, 0xbeef, 2); output.AddRange([0x81, 0xff]);
        for (var i = 0; i < 9; i++) { Name(output, $"é/file{i}"); output.AddRange(Enumerable.Repeat((byte)(i + 1), 16)); Number(output, i == 8 ? uint.MaxValue : i, 4); }
        return output.ToArray();
    }
    private static byte[] Download(byte version, byte checksum, byte flags)
    {
        var output = new List<byte> { (byte)'D', (byte)'L', version, 16, checksum };
        Number(output, 2, 4); Number(output, 1, 2);
        if (version >= 2) output.Add(flags);
        if (version == 3) output.AddRange([0xff, 3, 0x80, 9]);
        for (var i = 0; i < 2; i++)
        {
            output.AddRange(Enumerable.Repeat((byte)7, 16)); Number(output, 0x123456789a, 5); output.Add((byte)(255 - i));
            if (checksum != 0) Number(output, 0xfedcba98, 4);
            if (version >= 2) output.AddRange(Enumerable.Range(0, flags).Select(x => (byte)(x + i + 1)));
        }
        Name(output, "t"); Number(output, 7, 2); output.Add(0xbf);
        return output.ToArray();
    }
    private static byte[] DownloadSize(byte width)
    {
        var output = new List<byte> { (byte)'D', (byte)'S', 1, width };
        Number(output, 2, 4); Number(output, 1, 2); Number(output, 2L * uint.MaxValue, 5);
        Name(output, "t"); Number(output, 7, 2); output.Add(0x7f);
        for (var i = 0; i < 2; i++) { output.AddRange(Enumerable.Repeat((byte)7, width)); Number(output, uint.MaxValue, 4); }
        return output.ToArray();
    }

    private sealed class Capture : IInstallConsumer, IDownloadConsumer, IDownloadSizeConsumer
    {
        public List<string> Order { get; } = [];
        public List<InstallEntry> InstallEntries { get; } = [];
        public List<InstallTag> InstallTags { get; } = [];
        public List<DownloadEntry> DownloadEntries { get; } = [];
        public List<DownloadTag> DownloadTags { get; } = [];
        public List<DownloadSizeEntry> SizeEntries { get; } = [];
        public List<DownloadSizeTag> SizeTags { get; } = [];
        public DownloadHeader? DownloadHeader { get; private set; }
        public Action? OnEntry { get; init; }
        public Func<Task>? EntryGate { get; init; }
        public ValueTask HeaderAsync(InstallHeader header, CancellationToken token) { Order.Add("IN-header"); return ValueTask.CompletedTask; }
        public ValueTask HeaderAsync(DownloadHeader header, CancellationToken token) { Order.Add("DL-header"); DownloadHeader = header; return ValueTask.CompletedTask; }
        public ValueTask HeaderAsync(DownloadSizeHeader header, CancellationToken token) { Order.Add("DS-header"); return ValueTask.CompletedTask; }
        public ValueTask TagAsync(int ordinal, InstallTag tag, CancellationToken token) { Assert.Equal(InstallTags.Count, ordinal); Order.Add("IN-tag"); InstallTags.Add(tag); return ValueTask.CompletedTask; }
        public ValueTask TagAsync(int ordinal, DownloadTag tag, CancellationToken token) { Assert.Equal(DownloadTags.Count, ordinal); Order.Add("DL-tag"); DownloadTags.Add(tag); return ValueTask.CompletedTask; }
        public ValueTask TagAsync(int ordinal, DownloadSizeTag tag, CancellationToken token) { Assert.Equal(SizeTags.Count, ordinal); Order.Add("DS-tag"); SizeTags.Add(tag); return ValueTask.CompletedTask; }
        public ValueTask EntryAsync(long ordinal, InstallEntry entry, CancellationToken token) { Assert.Equal(InstallEntries.Count, ordinal); Order.Add("IN-entry"); InstallEntries.Add(entry); return AfterEntry(); }
        public ValueTask EntryAsync(long ordinal, DownloadEntry entry, CancellationToken token) { Assert.Equal(DownloadEntries.Count, ordinal); Order.Add("DL-entry"); DownloadEntries.Add(entry); return AfterEntry(); }
        public ValueTask EntryAsync(long ordinal, DownloadSizeEntry entry, CancellationToken token) { Assert.Equal(SizeEntries.Count, ordinal); Order.Add("DS-entry"); SizeEntries.Add(entry); return AfterEntry(); }
        private ValueTask AfterEntry() { OnEntry?.Invoke(); return EntryGate is null ? ValueTask.CompletedTask : new(EntryGate()); }
    }

    private sealed class AsyncOnlyStream(byte[] bytes, int fragment) : Stream
    {
        private int position;
        private bool disposed;
        public bool FailAtEof { get; init; }
        public Action? BeforeRead { get; init; }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O forbidden");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (position == bytes.Length && FailAtEof) throw new IOException("Authored late I/O failure");
            var count = Math.Min(Math.Min(fragment, buffer.Length), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer); position += count;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
