using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class RootBatchLookupTests
{
    private sealed record Row(uint Id, byte KeyByte = 0x11, ulong? Name = null);
    private sealed record Block(Row[] Rows, uint Locale = 2, uint First = RootInventoryReader.NoNames,
        uint Second = 0, byte Third = 0);

    [Fact]
    public async Task ResolvesOneCompletePassWithCanonicalTargetsAndAllUnfilteredVariantProofs()
    {
        var bytes = Fixture(new([new(5, 0x11, 99), new(9, 0x22, 100)], 2, 0, 4, 3),
            new([new(5, 0x33), new(uint.MaxValue)], 0, RootInventoryReader.NoNames, 8, 9));
        using var input = new AsyncInput(bytes, 7);
        var result = await RootBatchLookup.ReadAsync(input, new uint[] { uint.MaxValue, 9, 5, 1, 5 }, new());
        Assert.Equal(new uint[] { 1, 5, 9, uint.MaxValue }, result.Targets.Select(target => target.FileDataId));
        Assert.Empty(result.Targets[0].Memberships);
        var matches = result.Targets[1].Memberships;
        Assert.Equal(2, matches.Count);
        Assert.Equal((0L, 0L), (matches[0].BlockOrdinal, matches[0].EntryOrdinal));
        Assert.Equal((1L, 0L), (matches[1].BlockOrdinal, matches[1].EntryOrdinal));
        Assert.Equal(new string('1', 32), matches[0].Entry.ContentKey);
        Assert.Equal(new string('3', 32), matches[1].Entry.ContentKey);
        Assert.Equal(99UL, matches[0].Entry.NameHash);
        Assert.Null(matches[1].Entry.NameHash);
        Assert.Equal(0U, matches[1].Entry.LocaleFlags);
        Assert.Equal(new RootRawContentFlags(RootInventoryReader.NoNames, 8, 9), matches[1].Entry.RawContentFlags);
        Assert.Equal(4, result.Completion.MembershipCount);
        Assert.Equal(2, result.Completion.NamedMembershipCount);
        Assert.Equal(bytes.Length, result.Completion.ConsumedBytes);
        Assert.Equal(bytes.Length, input.BytesRead);
        Assert.Equal(1, input.EofReads);
        Assert.True(input.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrMissingTargetsStillRequireCompleteValidRoot(bool emptyRequest)
    {
        var requested = emptyRequest ? Array.Empty<uint>() : new uint[] { 6 };
        var bytes = Fixture(new Block([new(5)]));
        using var input = new AsyncInput(bytes);
        var result = await RootBatchLookup.ReadAsync(input, requested, new());
        Assert.All(result.Targets, target => Assert.Empty(target.Memberships));
        Assert.Equal(bytes.Length, input.BytesRead);
        await AssertCode("ROOT_INVALID", [.. bytes, 0], requested);
    }

    [Fact]
    public async Task RejectsZeroAndTooManyInputIdsBeforeReading()
    {
        foreach (var requested in new[] { new uint[] { 1, 0 }, new uint[129] })
        {
            using var input = new AsyncInput(Fixture());
            var error = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(input, requested, new()));
            Assert.Equal(requested.Length == 129 ? "ROOT_LIMIT" : "ROOT_INVALID", error.Code);
            Assert.Equal(0, input.BytesRead);
            Assert.True(input.CanRead);
        }
    }

    [Fact]
    public async Task Accepts128TargetsAndDoesNotRetainUnrequestedRows()
    {
        var rows = Enumerable.Range(1, 1_000).Select(id => new Row((uint)id)).ToArray();
        var targets = Enumerable.Range(1, 128).Select(id => (uint)id).Reverse().ToArray();
        var result = await RootBatchLookup.ReadAsync(new AsyncInput(Fixture(new Block(rows))), targets, new());
        Assert.Equal(128, result.Targets.Count);
        Assert.All(result.Targets, target => Assert.Equal(target.FileDataId, Assert.Single(target.Memberships).Entry.FileDataId));
        Assert.Equal(1_000, result.Completion.MembershipCount);
    }

    [Fact]
    public async Task AcceptsPerTargetBoundAndRejectsNextRawVariantWithoutFiltering()
    {
        var blocks = Enumerable.Repeat(new Block([new(5)], 0), RootBatchLookup.MaxMatchesPerTarget).ToArray();
        var result = await RootBatchLookup.ReadAsync(new AsyncInput(Fixture(blocks)), new uint[] { 5 }, new());
        Assert.Equal(4_096, Assert.Single(result.Targets).Memberships.Count);
        await AssertCode("ROOT_LIMIT", Fixture([.. blocks, new([new(5)], 4)]), [5]);
    }

    [Fact]
    public async Task AcceptsAggregateBoundAndRejectsNextMatchAcrossDistinctTargets()
    {
        var rows = Enumerable.Range(1, 8).Select(id => new Row((uint)id)).ToArray();
        var blocks = Enumerable.Repeat(new Block(rows, 0), 4_096).ToArray();
        var targets = Enumerable.Range(1, 9).Select(id => (uint)id).ToArray();
        var result = await RootBatchLookup.ReadAsync(new AsyncInput(Fixture(blocks)), targets, new());
        Assert.Equal(32_768, result.Targets.Sum(target => target.Memberships.Count));
        await AssertCode("ROOT_LIMIT", Fixture([.. blocks, new([new(9)], 4)]), targets);
    }

    [Fact]
    public async Task EveryTruncationFailsEvenWhenRequestedTargetAppearedEarly()
    {
        var bytes = Fixture(new([new(5)]), new([new(9, 0x33, 10)], 2, 0));
        for (var length = 0; length < bytes.Length; length++)
            await AssertCode("ROOT_INVALID", bytes[..length], [5]);
    }

    [Fact]
    public async Task LateCorruptionAndUnsupportedRootNeverBecomeObservedAbsence()
    {
        var bytes = Fixture(new([new(5)]), new([new(9, 0x33, 10)], 2, 0));
        bytes[12] = 1; // Declared membership total no longer covers the late block.
        await AssertCode("ROOT_INVALID", bytes, [5, 10]);
        bytes = Fixture(); bytes[8] = 3;
        await AssertCode("ROOT_UNSUPPORTED", bytes, [10]);
        bytes = Fixture(); bytes[0] = 0;
        await AssertCode("ROOT_UNSUPPORTED", bytes, [10]);
    }

    [Fact]
    public async Task ProgressUsesWholeSourceAndHasNoTerminalEventBeforeEofProof()
    {
        var rows = Enumerable.Range(1, 8_200).Select(id => new Row((uint)id)).ToArray();
        var bytes = Fixture(new Block(rows));
        var progress = new List<(long, long)>();
        ValueTask Report(long done, long total, CancellationToken token)
        { progress.Add((done, total)); return ValueTask.CompletedTask; }
        var result = await RootBatchLookup.ReadAsync(new AsyncInput(bytes), new uint[] { 1 }, new(), Report);
        Assert.Single(Assert.Single(result.Targets).Memberships);
        Assert.Equal([(0L, 8200L), (4096L, 8200L), (8192L, 8200L), (8200L, 8200L)], progress);
        progress.Clear();
        await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootBatchLookup.ReadAsync(new AsyncInput([.. bytes, 0]), new uint[] { 1 }, new(), Report));
        Assert.DoesNotContain((8200L, 8200L), progress);
    }

    [Fact]
    public async Task CancellationBeforeReadDuringPassAndAtTerminalProgressReturnsNoResult()
    {
        var bytes = Fixture(new Block(Enumerable.Range(1, 5_000).Select(id => new Row((uint)id)).ToArray()));
        using var before = new CancellationTokenSource(); before.Cancel();
        using var input = new AsyncInput(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootBatchLookup.ReadAsync(input, new uint[] { 1 }, new(), token: before.Token));
        Assert.Equal(0, input.BytesRead);
        foreach (var cancelAfter in new long[] { 4096, 5000 })
        {
            using var during = new CancellationTokenSource();
            ValueTask Report(long done, long total, CancellationToken token)
            { if (done == cancelAfter) during.Cancel(); return ValueTask.CompletedTask; }
            using var stream = new AsyncInput(bytes);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RootBatchLookup.ReadAsync(stream, new uint[] { 1 }, new(), Report, during.Token));
            Assert.True(stream.CanRead);
        }
    }

    [Fact]
    public async Task HonorsExistingRootLimitsAndPreservesIoFailureAndOwnership()
    {
        var bytes = Fixture(new Block([new(5)]));
        using var limited = new AsyncInput(bytes);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootBatchLookup.ReadAsync(limited, new uint[] { 5 }, new(MaxRecords: 0)));
        Assert.Equal("ROOT_LIMIT", error.Code);
        using var failing = new AsyncInput(bytes, fail: true);
        await Assert.ThrowsAsync<IOException>(() => RootBatchLookup.ReadAsync(failing, new uint[] { 5 }, new()));
        Assert.True(failing.CanRead);
    }

    private static async Task AssertCode(string expected, byte[] bytes, uint[] requested)
    {
        using var input = new AsyncInput(bytes, 7);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(input, requested, new()));
        Assert.Equal(expected, error.Code);
        Assert.True(input.CanRead);
    }

    private static byte[] Fixture(params Block[] blocks)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8); writer.Write(24U); writer.Write(2U);
        writer.Write((uint)blocks.Sum(block => block.Rows.Length));
        writer.Write((uint)blocks.Where(block => (block.First & RootInventoryReader.NoNames) == 0).Sum(block => block.Rows.Length));
        writer.Write(0U);
        foreach (var block in blocks)
        {
            writer.Write((uint)block.Rows.Length); writer.Write(block.Locale); writer.Write(block.First); writer.Write(block.Second); writer.Write(block.Third);
            ulong next = 0;
            foreach (var row in block.Rows) { writer.Write(checked((uint)(row.Id - next))); next = (ulong)row.Id + 1; }
            foreach (var row in block.Rows) writer.Write(Enumerable.Repeat(row.KeyByte, 16).ToArray());
            if ((block.First & RootInventoryReader.NoNames) == 0)
                foreach (var row in block.Rows) writer.Write(row.Name ?? 0);
        }
        return output.ToArray();
    }

    private sealed class AsyncInput(byte[] bytes, int shortRead = 65_536, bool fail = false) : Stream
    {
        private bool disposed;
        public int BytesRead { get; private set; }
        public int EofReads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fail) throw new IOException("Authored stream failure.");
            var count = Math.Min(shortRead, Math.Min(buffer.Length, bytes.Length - BytesRead));
            bytes.AsMemory(BytesRead, count).CopyTo(buffer); BytesRead += count;
            if (count == 0) EofReads++;
            return ValueTask.FromResult(count);
        }
        protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        public override bool CanRead => !disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O prohibited.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous I/O prohibited.");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
