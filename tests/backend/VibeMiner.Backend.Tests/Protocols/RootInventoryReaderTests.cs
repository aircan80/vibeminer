using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class RootInventoryReaderTests
{
    [Theory]
    [InlineData(1, 20)]
    [InlineData(1, 24)]
    [InlineData(2, 24)]
    public async Task PreservesNamedAndUnnamedVariantsUnsignedIdsAndOriginalOrdinals(int version, int headerBytes)
    {
        var data = Build(version, headerBytes,
            new Block([9, 3], 0x80000001, 0x40000002, version == 2 ? 0x20u : 0u, 0,
                [0, ulong.MaxValue]),
            new Block([9, uint.MaxValue - 10], 0x80000002, RootInventoryReader.NoNames, 0, 0, null));
        var consumer = new Collector();
        var input = new AsyncOnlyStream(data, 3);
        var result = await RootInventoryReader.ReadAsync(input, new(), consumer);

        Assert.Equal(new RootHeader(headerBytes, version, 4, 2), consumer.Header);
        Assert.Equal([9u, 13u, 9u, uint.MaxValue], consumer.Rows.Select(x => x.Entry.FileDataId));
        Assert.Equal([(0L, 0L), (0L, 1L), (1L, 0L), (1L, 1L)],
            consumer.Rows.Select(x => (x.Block, x.Ordinal)));
        Assert.Equal(new string('1', 32), consumer.Rows[0].Entry.ContentKey);
        Assert.Equal(new string('2', 32), consumer.Rows[1].Entry.ContentKey);
        Assert.Equal(0UL, consumer.Rows[0].Entry.NameHash);
        Assert.Equal(ulong.MaxValue, consumer.Rows[1].Entry.NameHash);
        Assert.Null(consumer.Rows[2].Entry.NameHash);
        Assert.Equal(0x80000001u, consumer.Rows[0].Entry.LocaleFlags);
        Assert.Equal(new RootRawContentFlags(0x40000002, version == 2 ? 0x20u : 0u, 0),
            consumer.Rows[0].Entry.RawContentFlags);
        Assert.Equal(0x40000002u | (version == 2 ? 0x20u : 0u), consumer.Rows[0].Entry.ContentFlags);
        Assert.Equal([(0L, 2L), (1L, 2L)], consumer.CompletedBlocks);
        Assert.Equal(new RootCompletion(headerBytes, version, 4, 2, 2, data.Length, 40), result);
        Assert.False(input.WasDisposed);
    }

    [Fact]
    public async Task VersionTwoRetainsAllRawFlagsAndDerivesNoNamesFromTheirOr()
    {
        var data = Build(2, 24, new Block([4], 1, 0x80000000, RootInventoryReader.NoNames | 0x40, 0xff, null));
        var rows = new Collector();
        await RootInventoryReader.ReadAsync(new AsyncOnlyStream(data, 1), new(), rows);
        var row = Assert.Single(rows.Rows).Entry;
        Assert.Equal(new RootRawContentFlags(0x80000000, RootInventoryReader.NoNames | 0x40, 0xff), row.RawContentFlags);
        Assert.Equal(0x80000000u | RootInventoryReader.NoNames | 0x40u | (0xffu << 17), row.ContentFlags);
        Assert.Null(row.NameHash);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(1, 24)]
    [InlineData(2, 24)]
    public async Task EveryTruncationOfAuthoredMultiBlockSourceFails(int version, int headerBytes)
    {
        var data = Build(version, headerBytes, new Block([5, 0], 1, 0, 0, 0, [8, 9]),
            new Block([7], 2, RootInventoryReader.NoNames, 0, 0, null));
        for (var length = 0; length < data.Length; length++)
        {
            var error = await Assert.ThrowsAsync<DiscoveryException>(() =>
                RootInventoryReader.ReadAsync(new AsyncOnlyStream(data[..length], 7), new(), new Collector()));
            Assert.Equal("ROOT_INVALID", error.Code);
        }
    }

    [Fact]
    public async Task EmptyRootsAndEmptyBlocksHaveHonestCounts()
    {
        var empty = Build(1, 20);
        var result = await RootInventoryReader.ReadAsync(new AsyncOnlyStream(empty),
            new(MaxRecords: 0, MaxBlocks: 0, MaxEntriesPerBlock: 0, MaxBlockWorkingBytes: 0), new Collector());
        Assert.Equal(new RootCompletion(20, 1, 0, 0, 0, 20, 0), result);
        var rows = new Collector();
        result = await RootInventoryReader.ReadAsync(new AsyncOnlyStream(Build(1, 20,
            new Block([], 0, RootInventoryReader.NoNames, 0, 0, null))), new(), rows);
        Assert.Empty(rows.Rows);
        Assert.Equal((0L, 0L), Assert.Single(rows.CompletedBlocks));
        Assert.Equal(1, result.BlockCount);
    }

    [Fact]
    public async Task RejectsUnsignedDeltaOverflowInsteadOfWrappingOrDroppingRows()
    {
        var data = Build(1, 20, new Block([uint.MaxValue, 0], 1, RootInventoryReader.NoNames, 0, 0, null));
        var rows = new Collector();
        await Invalid(data, "ROOT_INVALID", rows);
        Assert.Empty(rows.Rows); // All ID deltas are validated before the block's row callbacks.
    }

    [Fact]
    public async Task RejectsCountAndNamedFramingMismatches()
    {
        var valid = Build(1, 20, new Block([1], 1, 0, 0, 0, [3]));
        foreach (var (offset, value) in new[] { (12, 0u), (12, 2u), (16, 0u), (16, 2u) })
        {
            var bytes = (byte[])valid.Clone();
            Put32(bytes, offset, value);
            await Invalid(bytes, "ROOT_INVALID");
        }
        var unnamed = Build(1, 20, new Block([1], 1, RootInventoryReader.NoNames, 0, 0, null));
        Put32(unnamed, 16, 1);
        await Invalid(unnamed, "ROOT_INVALID");
    }

    [Fact]
    public async Task RejectsUnknownSignatureVersionHeaderAndNonzeroExtension()
    {
        var bytes = Build(2, 24);
        foreach (var (offset, value) in new[] { (0, 0u), (8, 3u), (4, 28u), (4, 20u), (20, 1u) })
        {
            var changed = (byte[])bytes.Clone();
            Put32(changed, offset, value);
            await Invalid(changed, "ROOT_UNSUPPORTED");
        }
    }

    [Fact]
    public async Task LimitsRefuseCountsAndBlockMemoryBeforeRowsOrLargeAllocation()
    {
        var data = Build(1, 20, new Block([1, 2], 1, 0, 0, 0, [3, 4]));
        var limits = new RootLimits[]
        {
            new(MaxDecodedBytes: data.Length - 1), new(MaxDecodedBytes: 20), new(MaxRecords: 1),
            new(MaxBlocks: 0), new(MaxEntriesPerBlock: 1), new(MaxBlockWorkingBytes: 39)
        };
        foreach (var limit in limits)
        {
            var rows = new Collector();
            Assert.Equal("ROOT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                RootInventoryReader.ReadAsync(new AsyncOnlyStream(data, 1), limit, rows))).Code);
            Assert.Empty(rows.Rows);
        }
        var huge = Build(1, 20, new Block([], 1, RootInventoryReader.NoNames, 0, 0, null));
        Put32(huge, 12, uint.MaxValue);
        Put32(huge, 20, uint.MaxValue);
        Assert.Equal("ROOT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootInventoryReader.ReadAsync(new AsyncOnlyStream(huge),
                new(MaxRecords: uint.MaxValue, MaxEntriesPerBlock: int.MaxValue), new Collector()))).Code);
    }

    [Fact]
    public async Task RejectsInvalidLimitsAndChecksCancellationBeforeReading()
    {
        var invalid = new RootLimits[] { new(MaxDecodedBytes: 19), new(MaxRecords: -1), new(MaxBlocks: -1),
            new(MaxEntriesPerBlock: -1), new(MaxBlockWorkingBytes: -1) };
        foreach (var limit in invalid)
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                RootInventoryReader.ReadAsync(new AsyncOnlyStream([]), limit, new Collector()));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stream = new AsyncOnlyStream(Build(1, 20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootInventoryReader.ReadAsync(stream, new(), new Collector(), canceled.Token));
        Assert.Equal(0, stream.ReadCalls);
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task ExactByteLimitAllowsEofButRejectsOneMoreByte()
    {
        var data = Build(1, 20, new Block([4], 1, RootInventoryReader.NoNames, 0, 0, null));
        var result = await RootInventoryReader.ReadAsync(new AsyncOnlyStream(data),
            new(MaxDecodedBytes: data.Length), new Collector());
        Assert.Equal(data.Length, result.ConsumedBytes);
        Assert.Equal("ROOT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootInventoryReader.ReadAsync(new AsyncOnlyStream([.. data, 0]),
                new(MaxDecodedBytes: data.Length), new Collector()))).Code);
    }

    [Fact]
    public async Task LateTrailerAndIoFailuresCannotTurnProvisionalRowsIntoCompletion()
    {
        var data = Build(1, 20, new Block([4], 1, 0, 0, 0, [3]));
        var rows = new Collector();
        await Invalid([.. data, 0], "ROOT_INVALID", rows);
        Assert.Single(rows.Rows);
        Assert.Single(rows.CompletedBlocks);
        var io = new IOException("authored EOF failure");
        rows = new Collector();
        var source = new AsyncOnlyStream(data) { AtEof = () => throw io };
        Assert.Same(io, await Assert.ThrowsAsync<IOException>(() =>
            RootInventoryReader.ReadAsync(source, new(), rows)));
        Assert.Single(rows.Rows);
        Assert.False(source.WasDisposed);
    }

    [Fact]
    public async Task SinkExceptionsPropagateAndCallbacksApplyBackpressure()
    {
        var data = Build(1, 20, new Block([1, 0], 1, RootInventoryReader.NoNames, 0, 0, null));
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new Collector { OnEntry = async (_, _) => { reached.TrySetResult(); await release.Task; } };
        var task = RootInventoryReader.ReadAsync(new AsyncOnlyStream(data), new(), consumer);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        Assert.Single(consumer.Rows);
        Assert.Empty(consumer.CompletedBlocks);
        release.SetResult();
        Assert.Equal(2, (await task).MembershipCount);

        var failure = new InvalidOperationException("authored sink rejection");
        consumer = new Collector { OnEntry = (_, _) => throw failure };
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RootInventoryReader.ReadAsync(new AsyncOnlyStream(data), new(), consumer)));
        Assert.Single(consumer.Rows);
        Assert.Empty(consumer.CompletedBlocks);
    }

    [Fact]
    public async Task CancellationAfterBufferedRowAndDuringRefillRetiresWithoutCompletion()
    {
        var data = Build(1, 20, new Block([1, 0], 1, RootInventoryReader.NoNames, 0, 0, null));
        using var canceled = new CancellationTokenSource();
        var consumer = new Collector { OnEntry = (_, _) => { canceled.Cancel(); return ValueTask.CompletedTask; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootInventoryReader.ReadAsync(new AsyncOnlyStream(data), new(), consumer, canceled.Token));
        Assert.Single(consumer.Rows);
        Assert.Empty(consumer.CompletedBlocks);
        using var canceledRead = new CancellationTokenSource();
        var stream = new AsyncOnlyStream(data, 1) { OnRead = n => { if (n == 25) canceledRead.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootInventoryReader.ReadAsync(stream, new(), new Collector(), canceledRead.Token));
        Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task MultiRefillBlockEmitsOwnedRowsWithoutRetainingWholeInventory()
    {
        const int entries = 12_000;
        var data = Build(2, 24, new Block(new uint[entries], 0x1234, RootInventoryReader.NoNames, 0, 0, null));
        long observed = 0;
        RootEntry? first = null;
        var consumer = new CountingConsumer(entry =>
        {
            first ??= entry;
            Assert.Equal((uint)observed++, entry.FileDataId);
            Assert.Equal(0x1234u, entry.LocaleFlags);
        });
        var result = await RootInventoryReader.ReadAsync(new AsyncOnlyStream(data, 12_347), new(), consumer);
        Assert.Equal(entries, observed);
        Assert.Equal(entries * 20, result.PeakBlockWorkingBytes);
        Assert.Equal(data.Length, result.ConsumedBytes);
        Assert.Equal(new string('1', 32), first!.ContentKey);
        Assert.Equal(0u, first.FileDataId);
    }

    [Fact]
    public async Task HeaderAndBlockCallbacksAreAwaitedAndTheirFailuresPropagate()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("authored block flush failure");
        var consumer = new GatedConsumer(reached, release, failure);
        var data = Build(1, 20, new Block([1], 1, RootInventoryReader.NoNames, 0, 0, null));
        var task = RootInventoryReader.ReadAsync(new AsyncOnlyStream(data), new(), consumer);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        Assert.Equal(0, consumer.Rows);
        release.SetResult();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => task));
        Assert.Equal(1, consumer.Rows);
    }

    private static async Task Invalid(byte[] data, string code, Collector? rows = null) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootInventoryReader.ReadAsync(new AsyncOnlyStream(data, 5), new(), rows ?? new Collector()))).Code);

    private sealed record Block(uint[] Deltas, uint Locale, uint First, uint Second, byte Third, ulong[]? Names);

    // Authored columnar fixture generator. No downloaded payload or parser output is used.
    private static byte[] Build(int version, int headerBytes, params Block[] blocks)
    {
        using var target = new MemoryStream();
        using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8);
        writer.Write((uint)headerBytes);
        writer.Write((uint)version);
        writer.Write((uint)blocks.Sum(b => b.Deltas.Length));
        writer.Write((uint)blocks.Where(b => b.Names is not null).Sum(b => b.Deltas.Length));
        if (headerBytes == 24) writer.Write(0u);
        foreach (var block in blocks)
        {
            writer.Write((uint)block.Deltas.Length);
            if (version == 1) { writer.Write(block.First); writer.Write(block.Locale); }
            else { writer.Write(block.Locale); writer.Write(block.First); writer.Write(block.Second); writer.Write(block.Third); }
            foreach (var delta in block.Deltas) writer.Write(delta);
            for (var i = 0; i < block.Deltas.Length; i++) writer.Write(Enumerable.Repeat((byte)((i + 1) * 0x11), 16).ToArray());
            if (block.Names is not null) foreach (var name in block.Names) writer.Write(name);
        }
        return target.ToArray();
    }

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);

    private sealed class Collector : IRootConsumer
    {
        public RootHeader? Header { get; private set; }
        public List<(long Block, long Ordinal, RootEntry Entry)> Rows { get; } = [];
        public List<(long Block, long Entries)> CompletedBlocks { get; } = [];
        public Func<RootEntry, CancellationToken, ValueTask>? OnEntry { get; init; }
        public ValueTask HeaderAsync(RootHeader header, CancellationToken cancellationToken) { Header = header; return ValueTask.CompletedTask; }
        public async ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken)
        {
            Rows.Add((blockOrdinal, entryOrdinal, entry));
            if (OnEntry is not null) await OnEntry(entry, cancellationToken);
        }
        public ValueTask BlockCompletedAsync(long blockOrdinal, long entries, CancellationToken cancellationToken)
        { CompletedBlocks.Add((blockOrdinal, entries)); return ValueTask.CompletedTask; }
    }

    private sealed class CountingConsumer(Action<RootEntry> count) : IRootConsumer
    {
        public ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken)
        { count(entry); return ValueTask.CompletedTask; }
    }

    private sealed class GatedConsumer(TaskCompletionSource reached, TaskCompletionSource release,
        Exception blockFailure) : IRootConsumer
    {
        public int Rows { get; private set; }
        public async ValueTask HeaderAsync(RootHeader header, CancellationToken cancellationToken)
        { reached.SetResult(); await release.Task; }
        public ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken)
        { Rows++; return ValueTask.CompletedTask; }
        public ValueTask BlockCompletedAsync(long blockOrdinal, long entries, CancellationToken cancellationToken) =>
            throw blockFailure;
    }

    private sealed class AsyncOnlyStream(byte[] bytes, int fragment = 65_536) : Stream
    {
        private int at;
        public bool WasDisposed { get; private set; }
        public int ReadCalls { get; private set; }
        public Action<int>? OnRead { get; init; }
        public Action? AtEof { get; init; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield(); // Force actual asynchronous completion and exercise all refill continuations.
            OnRead?.Invoke(++ReadCalls);
            cancellationToken.ThrowIfCancellationRequested();
            if (at == bytes.Length) { AtEof?.Invoke(); return 0; }
            var count = Math.Min(Math.Min(fragment, buffer.Length), bytes.Length - at);
            bytes.AsMemory(at, count).CopyTo(buffer);
            at += count;
            return count;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous reads are forbidden.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
