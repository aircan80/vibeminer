using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
namespace VibeMiner.Backend.Tests;

public sealed class CompactInventoryMatchingTests
{
    private static InventoryKey Key(ulong a, ulong b = 0) => new(a, b);
    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public void Map_and_merge_preserve_duplicates_sizes_and_complete_identity(int width)
    {
        CompactEncodedObject[] objects = [new(Key(1, 7), 20), new(Key(2, 8), 0), new(Key(3, 9), 40)];
        CompactSizeRequest[] requests = [new(1, objects[0].Key.Prefix(width), 20), new(2, objects[0].Key.Prefix(width), 20), new(3, objects[2].Key.Prefix(width), 40)];
        var map = CompactInventoryMatching.BuildMap(objects, 3, width, default);
        var actual = new List<(long, CompactEncodedObject)>();
        CompactInventoryMatching.Merge(objects, requests, width, (request, value) => actual.Add((request.Id, value)), default);
        Assert.Equal(new[] { (1L, objects[0]), (2L, objects[0]), (3L, objects[2]) }, actual);
        Assert.All(requests, request => Assert.Equal(actual.Single(row => row.Item1 == request.Id).Item2, CompactInventoryMatching.Resolve(map, request, width)));
        Assert.Equal("01020304050607080900000000000000", InventoryKey.Parse("010203040506070809").ToString());
    }
    [Fact]
    public void A_shared_prefix_is_ambiguous_even_when_sizes_match_but_full_keys_remain_distinct()
    {
        CompactEncodedObject[] objects = [new(Key(1,1),10),new(Key(1,2),10)];
        var request = new CompactSizeRequest(1,Key(1),10);
        Assert.Equal("INVENTORY_PREFIX_UNRESOLVED", Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.Resolve(CompactInventoryMatching.BuildMap(objects,2,9,default),request,9)).Code);
        Assert.Equal("INVENTORY_PREFIX_UNRESOLVED", Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.Merge(objects,[request],9,(_,_)=>{},default)).Code);
        Assert.Equal(objects[1],CompactInventoryMatching.Resolve(CompactInventoryMatching.BuildMap(objects,2,16,default),new(1,objects[1].Key,10),16));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_references_and_size_conflicts_fail_closed(bool conflict)
    {
        CompactEncodedObject[] objects = [new(Key(1),10)];
        var request = new CompactSizeRequest(1,Key(conflict?1UL:2UL),11);
        var code = conflict?"INVENTORY_SIZE_CONFLICT":"INVENTORY_PREFIX_UNRESOLVED";
        Assert.Equal(code,Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.Resolve(CompactInventoryMatching.BuildMap(objects,1,9,default),request,9)).Code);
        Assert.Equal(code,Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.Merge(objects,[request],9,(_,_)=>{},default)).Code);
    }
    [Fact]
    public void Bounded_admission_order_and_cancellation_are_enforced()
    {
        Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.BuildMap([],int.MaxValue,9,default));
        Assert.Throws<DiscoveryException>(() => CompactInventoryMatching.BuildMap([new(Key(1),1)],0,9,default));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompactInventoryMatching.BuildMap([new(Key(1),1)],1,9,ct.Token));
        Assert.Throws<OperationCanceledException>(() => CompactInventoryMatching.Merge([],[],9,(_,_)=>{},ct.Token));
        Assert.Throws<ArgumentException>(() => CompactInventoryMatching.Merge([new(Key(2),2),new(Key(1),1)],[new(1,Key(2),2)],9,(_,_)=>{},default));
        Assert.Throws<ArgumentException>(() => CompactInventoryMatching.Merge([new(Key(1),1),new(Key(2),2)],[new(1,Key(2),2),new(2,Key(1),1)],9,(_,_)=>{},default));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Resumable_merge_keeps_prefix_groups_across_tiny_worker_slices(int work)
    {
        CompactEncodedObject[] objects = [new(Key(1,7),20),new(Key(2,8),30),new(Key(3,9),40)];
        CompactSizeRequest[] requests = [new(7,Key(2),30),new(19,Key(2),30),new(33,Key(3),40)];
        using var cursor = new OrderedInventoryCursor(objects,requests,9);
        var rows = new List<ResolvedEncodedOrigin>();var slices=0;
        while(true)
        {
            var batch=cursor.Read(work,1,default);slices++;rows.AddRange(batch.Rows);
            if(batch.Complete)break;
            Assert.True(slices<100);
        }
        Assert.Equal(new[]{7L,19L,33L},rows.Select(row=>row.Id));
        Assert.Equal(new[]{objects[1],objects[1],objects[2]},rows.Select(row=>row.Object));
        Assert.True(slices>3);
    }
    [Fact]
    public void Resumable_merge_refuses_late_collision_and_allows_only_declared_complete_absent_keys()
    {
        CompactEncodedObject[] objects=[new(Key(1,1),10),new(Key(1,2),10)];
        using(var collision=new OrderedInventoryCursor(objects,[new(1,Key(1),10)],9))
        {
            var error=Assert.Throws<DiscoveryException>(()=>{for(var i=0;i<100;i++)Assert.Empty(collision.Read(1,1,default).Rows);});
            Assert.Equal("INVENTORY_PREFIX_UNRESOLVED",error.Code);
        }
        using var exact=new OrderedInventoryCursor([],[new(1,Key(2,3),10),new(2,Key(2,3),11)],16,allowAbsentExact:true);
        Assert.Single(exact.Read(100,1,default).Rows);
        Assert.Equal("INVENTORY_SIZE_CONFLICT",Assert.Throws<DiscoveryException>(()=>exact.Read(100,1,default)).Code);
        Assert.Throws<ArgumentException>(()=>new OrderedInventoryCursor([],[],9,allowAbsentExact:true));
    }

}
