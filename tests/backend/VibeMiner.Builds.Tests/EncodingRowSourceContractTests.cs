using VibeMiner.Builds.Testing;
using VibeMiner.Builds.Formats;
using VibeMiner.Builds.Tests.Formats;
using Xunit;
using static VibeMiner.Builds.Tests.EncodingInventoryTestKit;

namespace VibeMiner.Builds.Tests;

public abstract class EncodingRowSourceContractTests
{
    protected abstract IEncodingRowSource Create(int count);
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3507)]
    public async Task LeasedRowsAreExactUniqueSortedAndBoundedWithStableSourceContext(int count)
    {
        var source = Create(count); await using var lease = source.OpenEncodingRows();
        Assert.Equal(source.Build, lease.Build); Assert.Equal(source.EncodingCKey, lease.EncodingCKey); Assert.Equal(count, lease.EncodedCount);
        VibeMiner.Cdn.CdnKey? after = null; var actual = new List<EncodingRow>(); var pages = 0;
        while (true)
        {
            var page = await lease.ReadPageAsync(after, 1024); Assert.InRange(page.Rows.Count, 0, 1024); actual.AddRange(page.Rows);
            Assert.True(++pages <= 4, "Authored row traversal did not terminate.");
            if (!page.HasMore) break;
            Assert.NotEmpty(page.Rows); after = page.Rows[^1].EKey;
        }
        Assert.Equal(Rows(count), actual);
        var tail = await lease.ReadPageAsync(Key(1_000_000), 10); Assert.Empty(tail.Rows); Assert.False(tail.HasMore);
    }
    [Fact]
    public async Task BoundsCancellationAndLeaseClosureAreExplicitAndNeverCloseTheOpenedSource()
    {
        var source = Create(4); var lease = source.OpenEncodingRows();
        foreach (var limit in new[] { 0, 1025 }) Assert.Equal(EncodingInventoryCodes.Validation,
            (await Assert.ThrowsAsync<EncodingInventoryException>(async () => await lease.ReadPageAsync(null, limit))).Code);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await lease.ReadPageAsync(null, ct: canceled.Token));
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.Equal(EncodingInventoryCodes.Closed, (await Assert.ThrowsAsync<EncodingInventoryException>(async () => await lease.ReadPageAsync(null))).Code);
        await using var sibling = source.OpenEncodingRows(); Assert.Equal(4, (await sibling.ReadPageAsync(null)).Rows.Count);
    }
    [Fact]
    public async Task ConcurrentKeysetReadsHaveIndependentStableResults()
    {
        var source = Create(3507); await using var lease = source.OpenEncodingRows();
        var pages = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i => await lease.ReadPageAsync(Key(i * 100), 100)));
        for (var i = 0; i < pages.Length; i++) Assert.Equal(Rows(100, i * 100 + 1), pages[i].Rows);
    }
}
public sealed class EncodingRowSourceContractOnRealOpenedBuild : EncodingRowSourceContractTests
{
    internal static (IEncodingRowSource Source, byte[] Bytes) RealSource(int count)
    {
        var identity = Identity(); var rows = Rows(count);
        var bytes = EncodingWriter.FromFiles(rows.Select((row, i) => (Key(100_000 + i), 50L + i, row.EKey, row.EncodedSize)));
        var table = EncodingTable.Open(bytes, new(), CancellationToken.None);
        var root = RootTable.Parse(RootWriter.Build(2, 24, []), new(), CancellationToken.None);
        var locations = ArchiveLocations.Build([], null, new(), CancellationToken.None);
        var metadata = new BuildMetadata("authored source", default, Key(900_001), default, 0, 0, ["enUS"]);
        return ((IEncodingRowSource)new OpenedBuild(Route(identity), metadata, null, table, root, locations), bytes);
    }
    protected override IEncodingRowSource Create(int count) => RealSource(count).Source;
    [Fact]
    public async Task PublisherStreamsRealOpenedEncodingAndPreservesPriorGenerationIfALaterPageChanges()
    {
        var store = new InMemoryEncodingInventoryStore(); var (source, bytes) = RealSource(3507);
        await using (var initial = new EncodingInventoryPublisher(store))
        {
            var first = await initial.Start(Identity(), new EncodingSourceBuild(source)).Completion;
            Assert.Equal(EncodingInventoryState.Published, first.State); Assert.Equal(3507, first.Processed);
        }
        var previous = store.ReadActive(Identity().Id)!; var changed = false;
        var options = new EncodingInventoryPublisherOptions { Fault = point =>
        {
            if (point != EncodingInventoryPoint.AfterAppend || changed) return; changed = true; bytes[^1] ^= 0x40;
        } };
        await using var refresh = new EncodingInventoryPublisher(store, options);
        var failed = await refresh.Start(Identity(), new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Failed, failed.State); Assert.Equal("EN_INTEGRITY", failed.Code);
        Assert.Equal(previous.Generation, store.ReadActive(Identity().Id)!.Generation);
        Assert.Equal(Rows(3507).Select(row => row.EKey), store.ReadActive(Identity().Id)!.Keys);
    }
    [Fact]
    public async Task MutatedEncodedPageIsRecheckedAndNeverReturnedAsValidRows()
    {
        var (source, bytes) = RealSource(100); await using var lease = source.OpenEncodingRows(); bytes[^1] ^= 0x40;
        var failure = await Assert.ThrowsAsync<BuildFormatException>(async () => await lease.ReadPageAsync(Key(80)));
        Assert.Equal("EN_INTEGRITY", failure.Code);
    }
}
public sealed class EncodingRowSourceContractOnFake : EncodingRowSourceContractTests
{
    protected override IEncodingRowSource Create(int count) => Source(Identity(), count);
}
