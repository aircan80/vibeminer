using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class RootTargetLookupTests
{
    [Fact]
    public async Task CollectsOnlyRequestedIdsButPreservesAllVariantsRawFlagsNamesAndOrdinals()
    {
        using var input = new MemoryStream(Fixture(
            new([5, 2], 1, 0, 2, 3, [99, 100]),
            new([5], 4, RootInventoryReader.NoNames, 8, 9, null)));
        var result = await RootTargetLookup.ReadAsync(input, new uint[] { 5, 5, uint.MaxValue }, new());
        Assert.Equal(2, result.Count);
        Assert.Equal((0L, 0L), (result[0].BlockOrdinal, result[0].EntryOrdinal));
        Assert.Equal((1L, 0L), (result[1].BlockOrdinal, result[1].EntryOrdinal));
        Assert.All(result, row => Assert.Equal(5u, row.Entry.FileDataId));
        Assert.Equal(99UL, result[0].Entry.NameHash);
        Assert.Null(result[1].Entry.NameHash);
        Assert.Equal(new RootRawContentFlags(RootInventoryReader.NoNames, 8, 9), result[1].Entry.RawContentFlags);
        Assert.Equal(4u, result[1].Entry.LocaleFlags);
        Assert.True(input.CanRead);
    }

    [Fact]
    public async Task MissingTargetsAndEmptyRequestsStillValidateTheCompleteSource()
    {
        var source = Fixture(new Block([5], 1, RootInventoryReader.NoNames, 0, 0, null));
        Assert.Empty(await RootTargetLookup.ReadAsync(new MemoryStream(source), new uint[] { 9 }, new()));
        Assert.Empty(await RootTargetLookup.ReadAsync(new MemoryStream(source), Array.Empty<uint>(), new()));
        foreach (var requested in new IReadOnlyCollection<uint>[] { new uint[] { 5 }, Array.Empty<uint>() })
            Assert.Equal("ROOT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                RootTargetLookup.ReadAsync(new MemoryStream([.. source, 0]), requested, new()))).Code);
    }

    [Fact]
    public async Task TargetAndVariantBoundsFailExplicitlyInsteadOfTruncatingResults()
    {
        using var input = new MemoryStream(Fixture());
        Assert.Equal("ROOT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootTargetLookup.ReadAsync(input, new uint[RootTargetLookup.MaxTargets + 1], new()))).Code);
        Assert.Equal(0, input.Position);
        var blocks = Enumerable.Repeat(new Block([5], 1, RootInventoryReader.NoNames, 0, 0, null), RootTargetLookup.MaxMatches + 1).ToArray();
        Assert.Equal("ROOT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootTargetLookup.ReadAsync(new MemoryStream(Fixture(blocks)), new uint[] { 5 }, new()))).Code);
    }

    [Fact]
    public async Task ReportsBoundedActualProgressAndCompletesOnlyAfterEofValidation()
    {
        var source = Fixture(new Block(new uint[8200], 1, RootInventoryReader.NoNames, 0, 0, null));
        var progress = new List<(long, long)>();
        ValueTask Report(long done, long total, CancellationToken token)
        { progress.Add((done, total)); return ValueTask.CompletedTask; }
        Assert.Single(await RootTargetLookup.ReadAsync(new MemoryStream(source), new uint[] { 8199 }, new(), Report));
        Assert.Equal([(0L, 8200L), (4096L, 8200L), (8192L, 8200L), (8200L, 8200L)], progress);
        progress.Clear();
        await Assert.ThrowsAsync<DiscoveryException>(() =>
            RootTargetLookup.ReadAsync(new MemoryStream([.. source, 0]), new uint[] { 8199 }, new(), Report));
        Assert.DoesNotContain((8200L, 8200L), progress);
    }

    [Fact]
    public async Task CancellationBeforeInputAndDuringLongScanNeverPublishesPartialTargets()
    {
        var source = Fixture(new Block(new uint[5000], 1, RootInventoryReader.NoNames, 0, 0, null));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using var input = new MemoryStream(source);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootTargetLookup.ReadAsync(input, new uint[] { 1 }, new(), canceled.Token));
        Assert.Equal(0, input.Position);
        using var during = new CancellationTokenSource();
        ValueTask Report(long done, long total, CancellationToken token)
        { if (done == 4096) during.Cancel(); return ValueTask.CompletedTask; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RootTargetLookup.ReadAsync(input, new uint[] { 1 }, new(), Report, during.Token));
        Assert.True(input.CanRead);
    }

    private sealed record Block(uint[] Deltas, uint Locale, uint First, uint Second, byte Third, ulong[]? Names);
    private static byte[] Fixture(params Block[] blocks)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8); writer.Write(24u); writer.Write(2u);
        writer.Write((uint)blocks.Sum(block => block.Deltas.Length));
        writer.Write((uint)blocks.Where(block => block.Names is not null).Sum(block => block.Deltas.Length)); writer.Write(0u);
        foreach (var block in blocks)
        {
            writer.Write((uint)block.Deltas.Length); writer.Write(block.Locale);
            writer.Write(block.First); writer.Write(block.Second); writer.Write(block.Third);
            foreach (var delta in block.Deltas) writer.Write(delta);
            foreach (var _ in block.Deltas) writer.Write(Enumerable.Repeat((byte)0x11, 16).ToArray());
            if (block.Names is not null) foreach (var name in block.Names) writer.Write(name);
        }
        return output.ToArray();
    }
}
