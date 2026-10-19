using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class ItemEffectsBridgeArtifactTests
{
    [Fact]
    public async Task SortedRangePreservesSignedUnsignedBoundariesCopiesAndEveryDuplicateOccurrence()
    {
        Db2Row[] rows = [Row(0, int.MaxValue, 0), Row(1, -1, int.MinValue), Row(2, 0, -1), Row(2, 0, -1, 0),
            Row(0x80000000, 0, 10), Row(uint.MaxValue, int.MinValue, int.MaxValue)];
        await using var h = await Fixture.Create(rows); await h.Build(); await using var session = await h.Open();
        var expected = rows.Select((row, ordinal) => (row, ordinal)).OrderBy(v => int.Parse(v.row.Values[2])).ThenBy(v => v.row.Id).ThenBy(v => v.ordinal).ToArray();
        for (uint i = 0; i < expected.Length; i++)
        {
            var tuple = await session.ReadTupleAsync(i, int.Parse(expected[i].row.Values[2]), default);
            Assert.Equal(expected[i].row.Id, tuple.BridgeId); Assert.Equal((ulong)expected[i].ordinal, tuple.SourceOrdinal);
            Assert.Equal(expected[i].row.CopySourceId, tuple.CopySourceId); Assert.Equal(expected[i].row.IsCopy, tuple.IsCopy);
            Assert.Equal(int.Parse(expected[i].row.Values[1]), tuple.ItemEffectId); Assert.Equal(i, tuple.IndexOrdinal);
        }
        Assert.Equal(new ItemEffectsBridgeRange(2,5), await session.FindRangeAsync(0, default));
        Assert.Equal(new ItemEffectsBridgeRange(1,1), await session.FindRangeAsync(-2, default));
        Assert.InRange(session.LastLowerProbes, 1, 3); Assert.InRange(session.LastUpperProbes, 1, 3);
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => session.ReadTupleAsync(0, 0, default))).Code);
        var page = await session.ReadPageAsync(h.Request(0), h.Binding.Source, default);
        Assert.Equal("3", page.ObservedAssociations); Assert.Equal(new[]{"2","3","4"},page.Edges.Select(e=>e.Bridge.SourceOrdinal));
        Assert.Equal(new[]{"2","3","4"},page.Edges.Select(e=>e.Key.IndexOrdinal)); Assert.Equal("0",page.Edges[1].Bridge.CopySourceId);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(65536)]
    public async Task EmptySingletonAndMaximumCountAreBoundedAndSearchable(int count)
    {
        await using var h=await Fixture.Create(Enumerable.Range(0,count).Select(i=>Row((uint)i,7,i)).ToArray());
        var calls=0; h.FeedHook=(_,_,_)=>{calls++;return Task.CompletedTask;}; await h.Build(); await using var session=await h.Open();
        Assert.Equal((count+49)/50+(count==0?1:0),calls); Assert.Equal(new ItemEffectsBridgeRange(0,(uint)count),await session.FindRangeAsync(7,default));
        Assert.InRange(session.LastLowerProbes,0,17); Assert.InRange(session.LastUpperProbes,0,17);
        Assert.True(h.Artifact.ByteLength<=4<<20); Assert.True(h.CpuCalls>0);
    }
    [Fact]
    public async Task MaximumPlusOneFailsBeforeReadingOrWritingAndLeavesStageOwned()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]); var b=Fixture.WithCount(h.Binding,65537,0);
        var called=false; await using var stage=h.Stage();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.BuildAsync(b,stage,(_,_,_)=>{called=true;throw new Exception();},default));
        Assert.Equal("ITEM_EFFECTS_LIMIT",error.Code); Assert.False(called); Assert.Equal(0,stage.Length); await stage.WriteAsync(new byte[]{1});
    }
    [Fact]
    public async Task OriginalBindingIsOwnedBeforeFirstAwaitAndSourceAdmissionRefreshDoesNotChangeDataIdentity()
    {
        await using var h=await Fixture.Create([Row(1,4,2)]); var columns=(TableDataColumn[])h.Binding.Source.Columns;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.FeedHook=async (_,_,_)=>{entered.TrySetResult();await release.Task;}; var build=h.Build();await entered.Task;
        columns[0]=columns[0] with{Name="bad"};release.SetResult();await build;
        Assert.Equal("ID",h.Artifact.Binding.Source.Columns[0].Name);
        var old=h.Artifact.Binding; var fresh=old with{Source=old.Source with{PreparedAt="later",Source=old.Source.Source with{VerifiedAt="new-verification"}}};
        Assert.Equal(h.Artifact.ContentKey,ItemEffectsBridgePolicy.ContentKey(fresh,"1"));
        Assert.True(ItemEffectsBridgePolicy.SameCurrentSource(old.Source,fresh.Source));
        Assert.False(ItemEffectsBridgePolicy.SameCurrentSource(old.Source,fresh.Source with{Source=fresh.Source.Source with{RetrievedAt="different-original"}}));
        Assert.False(ItemEffectsBridgePolicy.SameCurrentSource(old.Source,fresh.Source with{Source=fresh.Source.Source with{SourceUrl="https://other.example/data"}}));
        Assert.False(ItemEffectsBridgePolicy.SameAuthority(old.Source,fresh.Source));
    }
    [Theory]
    [InlineData("empty")] [InlineData("oversize")] [InlineData("total")] [InlineData("cursor")] [InlineData("signed")] [InlineData("id")] [InlineData("copy")] [InlineData("order")]
    public async Task MalformedFeedCannotPublishAnIndex(string kind)
    {
        await using var h=await Fixture.Create([Row(1,0,0),Row(2,0,1)]);await using var stage=h.Stage();
        var page=new TableArtifactPage(h.Rows,2,null);
        page=kind switch
        {
            "empty"=>page with{Rows=[]},"oversize"=>page with{Rows=Enumerable.Repeat(h.Rows[0],51).ToArray()},
            "total"=>page with{TotalCount=3},"cursor"=>page with{NextCursor=0},
            "signed"=>page with{Rows=[h.Rows[0] with{Values=["1","+1","0"]},h.Rows[1]]},
            "id"=>page with{Rows=[h.Rows[0] with{Values=["9","0","0"]},h.Rows[1]]},
            "copy"=>page with{Rows=[h.Rows[0] with{IsCopy=true},h.Rows[1]]},
            "order"=>page with{Rows=[h.Rows[1],h.Rows[0]]},_=>page
        };
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.BuildAsync(h.Binding,stage,(_,_,_)=>Task.FromResult(page),default))).Code);
        Assert.Equal(0,stage.Length); Assert.True(stage.CanWrite);
    }
    [Theory]
    [InlineData("magic")] [InlineData("version")] [InlineData("count")] [InlineData("metadataLength")] [InlineData("stride")]
    [InlineData("tupleLength")] [InlineData("reserved")] [InlineData("metadata")] [InlineData("truncated")] [InlineData("trailing")]
    [InlineData("flags")] [InlineData("copyCount")] [InlineData("copySource")] [InlineData("ordinalDuplicate")] [InlineData("ordinalRange")] [InlineData("tupleReserved")] [InlineData("order")]
    public async Task MalformedPhysicalIndexIsRejectedAndInputIsConsumed(string kind)
    {
        await using var h=await Fixture.Create([Row(1,0,1),Row(2,0,2)]);await h.Build();var bytes=await File.ReadAllBytesAsync(h.Path);var tuples=64+(int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
        switch(kind)
        {
            case "magic":bytes[0]^=1;break;case "version":bytes[8]=2;break;case "count":bytes[12]=3;break;
            case "metadataLength":BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16),uint.MaxValue);break;
            case "stride":bytes[20]=31;break;case "tupleLength":bytes[24]^=1;break;case "reserved":bytes[63]=1;break;
            case "metadata":bytes[65]^=1;break;case "truncated":bytes=bytes[..^1];break;case "trailing":bytes=[..bytes,0];break;
            case "flags":bytes[tuples+12]=2;break;case "copyCount":bytes[tuples+12]=1;break;case "copySource":bytes[tuples+16]=1;break;
            case "ordinalDuplicate":BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(tuples+32+20),0);break;
            case "ordinalRange":BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(tuples+20),2);break;
            case "tupleReserved":bytes[tuples+28]=1;break;case "order":BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(tuples+4),3);break;
        }
        await File.WriteAllBytesAsync(h.Path,bytes);var input=h.Input();
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.OpenAsync(h.Artifact,input,default))).Code);
        Assert.True(input.SafeFileHandle.IsClosed);
    }
    [Fact]
    public async Task ProfileAndFullOriginalMetadataAreValidatedBeforeNormalization()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await using var stage=h.Stage();
        var changed=h.Binding with{Rows=h.Binding.Rows with{Binding=h.Binding.Rows.Binding with{SchemaSelectionSha256=new('f',64)}}};
        changed=changed with{Rows=changed.Rows with{ContentKey=TableRowArtifacts.ContentKey(changed.Rows.Binding)},Source=changed.Source with{ArtifactId=TableRowArtifacts.ContentKey(changed.Rows.Binding)}};
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.BuildAsync(changed,stage,h.Feed,default))).Code);
        var over=h.Binding with{Source=h.Binding.Source with{PreparedAt=new string('x',131073)}};
        Assert.Equal("ITEM_EFFECTS_LIMIT",Assert.Throws<DiscoveryException>(()=>ItemEffectsBridgePolicy.MetadataBytes(over,"1")).Code);
        Assert.Equal("ITEM_EFFECTS_LIMIT",Assert.Throws<DiscoveryException>(()=>ItemEffectsBridgePolicy.NormalizeBinding(over)).Code);
        Assert.Equal("ITEM_EFFECTS_LIMIT",Assert.Throws<DiscoveryException>(()=>ItemEffectsBridgePolicy.SameCurrentSource(over.Source,h.Binding.Source)).Code);
    }

    internal static Db2Row Row(uint id,int parent,int effect,uint? copy=null)=>new(id,copy.HasValue,copy,[id.ToString(System.Globalization.CultureInfo.InvariantCulture),effect.ToString(System.Globalization.CultureInfo.InvariantCulture),parent.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
    internal sealed class Fixture:IAsyncDisposable
    {
        internal Db2Row[] Rows=[];internal ItemEffectsBridgeBinding Binding=null!;internal ItemEffectsBridgeArtifact Artifact=null!;
        internal readonly string DirectoryPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"vibeminer-effects-index-"+Guid.NewGuid().ToString("N"));
        internal string Path=>System.IO.Path.Combine(DirectoryPath,"stage");internal ItemEffectsBridgeArtifacts Engine=null!;internal int CpuCalls;internal bool InCpu;
        internal Func<long,int,CancellationToken,Task>? FeedHook;
        internal static async Task<Fixture> Create(Db2Row[] rows,int maxPage=(512<<10)-1)
        {
            var h=new Fixture{Rows=rows};Directory.CreateDirectory(h.DirectoryPath);
            h.Engine=new(async (action,token)=>{token.ThrowIfCancellationRequested();Assert.False(h.InCpu);h.InCpu=true;h.CpuCalls++;try{action();}finally{h.InCpu=false;}await Task.Yield();},maxPage);
            var provider=await TablesCatalog.Provider.Value;var definition=provider.Definitions[ItemEffectsBridgePolicy.Table];
            var context=new ItemsContext("wow",new('a',64),"us","enUS");const string time="2026-10-19T00:00:00Z", version="12.1.5.70448";
            var source=new ContentReceipt(new('b',32),new('c',32),new('d',64),"128","64","https://cdn.example/data",time,time,"content-blte-v1",new("loose","0","128",null));
            MetadataReceipt Meta(string role)=>new(role,new('e',32),new('f',32),new('a',64),"128","64","https://cdn.example/"+role,time,time,MetadataAcquirer.SourceRevision);
            var association=new SelectedFileAssociation(ItemEffectsBridgePolicy.FileDataId,"enUS","enus-consistent-root-variants-v1",Meta("encoding"),Meta("root"),[new("0","0",source.ContentKey,"2","0","0","0","0",null)]);
            var rb=new TableArtifactBinding(source.Sha256,new('e',64),definition.Sha256,BundledTableDefinitions.Revision,"enUS",TableDataStore.DecoderRevision,TableRowArtifacts.SchemaSelectionKey(definition.Document,version));
            Db2Column[] columns=[new("ID","integer",true),new("ItemEffectID","integer",false),new("ItemID","integer",false)];
            var header=new Wdc5TableHeader(5,ItemEffectsBridgePolicy.Table.ToUpperInvariant(),ItemEffectsBridgePolicy.Layout,rows.Length-rows.Count(r=>r.IsCopy),rows.Count(r=>r.IsCopy),4,columns);
            var completion=new Wdc5Completion(header.DeclaredRecords,header.DeclaredRecords,header.DeclaredCopyRecords,0,0,0,"complete",64);
            var artifact=new TableRowsArtifact(TableRowArtifacts.ContentKey(rb),new('f',64),128,rb,header,completion);
            var snapshot=new TableDataSnapshot(context.ProductId,context.BuildIdentity,context.SourceRegion,ItemEffectsBridgePolicy.Table,"enUS",artifact.ContentKey,time,ItemEffectsBridgePolicy.Layout,definition.Sha256,BundledTableDefinitions.Revision,TableDataStore.DecoderRevision,source,association,
                columns.Select(c=>TableDataStore.ProjectColumn(c,definition.Document)).ToArray(),new("complete",header.DeclaredRecords.ToString(),completion.PhysicalRows.ToString(),completion.CopyRows.ToString(),"0","0",completion.CopyRows.ToString(),"0","0"),rows.Length.ToString());
            h.Binding=new(context,version,new("1","us",new('b',64),0,new('c',64),0),[new("buildConfig",new('a',32),new('b',64),20,"https://cdn.example/config",time,time,1),new("cdnConfig",new('c',32),new('d',64),20,"https://cdn.example/config",time,time,1)],snapshot,artifact,
                new(new(ItemEffectsBridgePolicy.Table,0),new(ItemEffectsBridgePolicy.Table,1),new(ItemEffectsBridgePolicy.Table,2)));
            return h;
        }
        internal static ItemEffectsBridgeBinding WithCount(ItemEffectsBridgeBinding b,int physical,int copies)
        {
            var c=b.Rows.Completion with{DeclaredRecords=physical,PhysicalRows=physical,CopyRows=copies};var r=b.Rows with{Header=b.Rows.Header with{DeclaredRecords=physical,DeclaredCopyRecords=copies},Completion=c};
            return b with{Rows=r,Source=b.Source with{RowCount=(physical+copies).ToString(),Coverage=new("complete",physical.ToString(),physical.ToString(),copies.ToString(),"0","0",copies.ToString(),"0","0")}};
        }
        internal FileStream Stage()=>new(Path,FileMode.Create,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.Asynchronous);
        internal FileStream Input()=>new(Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.RandomAccess);
        internal async Task<TableArtifactPage> Feed(long after,int limit,CancellationToken token)
        {
            Assert.False(InCpu);Assert.Equal(50,limit);if(FeedHook is not null)await FeedHook(after,limit,token);
            var take=(int)Math.Min(limit,Rows.Length-after);return new(Rows.Skip((int)after).Take(take).ToArray(),Rows.Length,after+take<Rows.Length?after+take:null);
        }
        internal async Task Build()
        {
            await using var stage=Stage();var build=await Engine.BuildAsync(Binding,stage,Feed,default);Assert.True(stage.CanWrite);
            // Authored test input stands in for a caller-verified CAS boundary; production does not duplicate this SHA pass.
            var sha=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path)));Artifact=new(build.ContentKey,sha,build.ByteLength,build.Binding,build.RowCount);
        }
        internal Task<ItemEffectsBridgeArtifacts.Session> Open()=>Engine.OpenAsync(Artifact,Input(),default);
        internal ItemEffectsPageRequest Request(int parent,int limit=16,string? after=null)=>new(new(Binding.Context,new('1',64),parent.ToString()),Artifact.ContentKey,limit,after);
        public ValueTask DisposeAsync(){Directory.Delete(DirectoryPath,true);return ValueTask.CompletedTask;}
    }
}
