using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsValidationTests
{
    internal static readonly ItemsContext Context = new("wow", new('a', 64), "us", "enUS");
    internal static readonly ItemEffectsSelection Selection = new(Context, new('b', 64), "0");
    internal static readonly string Index = new('c', 64);

    [Fact]
    public void EdgeSnapshotReadsCountOnceAndNeverEnumeratesCaller()
    {
        var entries = new HostileEdges(2, i => new(i.ToString()));
        var snapshot = ItemEffectsValidation.Snapshot(new ItemEffectDetailsRequest(Selection, Index, entries));
        Assert.Equal(1, entries.CountReads); Assert.Equal(2, entries.Reads);
        Assert.Equal(new[] { "0", "1" }, snapshot.Edges.Select(e => e.IndexOrdinal));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(17)] [InlineData(int.MaxValue)]
    public void HostileCountIsRefusedBeforeIndexerOrAllocation(int count)
    {
        var edges = new HostileEdges(count, _ => throw new InvalidOperationException());
        Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() =>
            ItemEffectsValidation.Snapshot(new ItemEffectDetailsRequest(Selection, Index, edges))).Code);
        Assert.Equal(1, edges.CountReads); Assert.Equal(0, edges.Reads);
    }

    [Fact]
    public void VanishedNullAndDuplicateEdgesAreTypedRefusals()
    {
        foreach (var edges in new IReadOnlyList<ItemEffectsEdgeKey>[] {
            new HostileEdges(2, i => i == 0 ? new("0") : throw new IndexOutOfRangeException()),
            new HostileEdges(1, _ => null!), new[] { new ItemEffectsEdgeKey("0"), new ItemEffectsEdgeKey("0") } })
            Assert.Equal("INVALID_REQUEST", Assert.Throws<DiscoveryException>(() =>
                ItemEffectsValidation.Snapshot(new ItemEffectDetailsRequest(Selection, Index, edges))).Code);
    }

    [Theory]
    [InlineData("0")] [InlineData("2147483648")] [InlineData("4294967295")]
    public void UnsignedItemDomainIsPreservedWithoutSignedWrap(string id) =>
        ItemEffectsValidation.Validate(Selection with { ItemId = id });

    [Theory]
    [InlineData("-1")] [InlineData("01")] [InlineData("4294967296")] [InlineData("1\n")]
    public void NoncanonicalItemIdsAreRejected(string id) => Assert.Throws<DiscoveryException>(() =>
        ItemEffectsValidation.Validate(Selection with { ItemId = id }));

    [Fact]
    public void UnknownNamePolicyAndOversizedNamesFailBeforeEdgeAccess()
    {
        var edges = new HostileEdges(9, _ => throw new InvalidOperationException());
        Assert.Throws<DiscoveryException>(() => ItemEffectsValidation.Snapshot(new ItemEffectNamesRequest(
            Selection, Index, new('d', 64), edges, "unknown")));
        Assert.Equal(0, edges.CountReads);
        Assert.Throws<DiscoveryException>(() => ItemEffectsValidation.Snapshot(new ItemEffectNamesRequest(
            Selection, Index, new('d', 64), edges)));
        Assert.Equal(0, edges.Reads);
    }

    [Fact]
    public void CursorBindingCannotBeReusedForAnotherPageLimitOrSelection()
    {
        var request = new ItemEffectsPageRequest(Selection, Index);
        var after = Cursor(request, "1");
        ItemEffectsValidation.Validate(request with { After = after });
        foreach (var edited in new[] { request with { Limit = 8 }, request with { Selection = Selection with { ItemId = "1" } } })
            Assert.Equal("ITEM_EFFECTS_CURSOR_INVALID", Assert.Throws<DiscoveryException>(() => ItemEffectsValidation.Validate(edited with { After = after })).Code);
    }

    internal static string Cursor(ItemEffectsPageRequest request, string after)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var binding = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { selection = request.Selection, bridgeIndexArtifactId = request.BridgeIndexArtifactId, revision = ItemEffectsValidation.IndexRevision, orderPolicy = ItemEffectsValidation.OrderPolicy, limit = request.Limit }, json)));
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { binding, after }, json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal sealed class HostileEdges(int count, Func<int, ItemEffectsEdgeKey> read) : IReadOnlyList<ItemEffectsEdgeKey>
    {
        public int CountReads, Reads;
        public int Count { get { if (++CountReads != 1) throw new InvalidOperationException("Count reread"); return count; } }
        public ItemEffectsEdgeKey this[int index] { get { Reads++; return read(index); } }
        public IEnumerator<ItemEffectsEdgeKey> GetEnumerator() => throw new InvalidOperationException("Enumeration forbidden");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
