using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5ProjectionTests
{
    [Fact]
    public async Task Keyed_holes_preserve_clear_physical_copy_and_exact_excluded_counts_without_touching_backing_holes()
    {
        var fixture=Build();using var backing=new AsyncBacking(fixture.Bytes);using var input=Project(backing,[Hole(fixture.Starts[1],5,0),Hole(fixture.Starts[1]+5,fixture.Ends[1]-fixture.Starts[1]-5,1)]);
        var sink=new Sink();var done=await Read(input,sink);
        Assert.Equal(new Wdc5Completion(2,1,1,1,1,2,"unencryptedSections",fixture.Bytes.Length),done);
        Assert.Equal(2,sink.Header!.DeclaredRecords);Assert.Equal(3,sink.Header.DeclaredCopyRecords);
        Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));Assert.Equal(["8","99"],sink.Rows[1].Values);
        Assert.True(backing.CanRead);Assert.True(input.CanRead);
        // The parser owns no source cache: a second call rereads the caller's source.
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Bytes.AsSpan(fixture.Starts[0]),123);
        var second=new Sink();await Read(input,second);Assert.Equal("123",second.Rows[0].Values[1]);
    }

    [Fact]
    public async Task Actual_zero_values_and_zero_ID_in_clear_section_are_data_and_interleaved_clear_sections_remain_readable()
    {
        var fixture=Build(interleaved:true,zero:true);using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1])]);
        var sink=new Sink();var done=await Read(input,sink);
        Assert.Equal([0u,30u,8u],sink.Rows.Select(row=>row.Id));Assert.Equal(["0","0"],sink.Rows[0].Values);
        Assert.Equal(2,done.PhysicalRows);Assert.Equal(1,done.CopyRows);Assert.Equal(1,done.ExcludedPhysicalRecords);
    }

    [Fact]
    public async Task Every_clear_body_byte_and_every_padding_byte_is_rejected_before_any_callback()
    {
        var fixture=Build(gap:7);
        foreach(var at in Enumerable.Range(fixture.PrefixEnd,fixture.Ends[0]-fixture.PrefixEnd))
        {
            using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(at,1)]);var sink=new Sink();
            Assert.Equal("DB2_PROJECTION_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink))).Code);
            Assert.Null(sink.Header);Assert.Empty(sink.Rows);
        }
    }

    [Theory]
    [InlineData(0)][InlineData(203)][InlineData(204)][InlineData(284)][InlineData(288)][InlineData(312)][InlineData(316)]
    public async Task Missing_global_header_descriptor_field_storage_and_keyed_ID_prefix_fail_before_callbacks(int at)
    {
        var fixture=Build();using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(at,1)]);var sink=new Sink();
        Assert.Equal("BLTE_RANGE_UNAVAILABLE",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink))).Code);
        Assert.Null(sink.Header);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task One_hole_crossing_keyed_into_clear_or_adjacent_keyed_sections_is_not_admitted()
    {
        foreach(var adjacentKeyed in new[]{false,true})
        {
            var fixture=Build(interleaved:true,lastKeyed:adjacentKeyed);
            using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(fixture.Ends[1]-1,2)]);var sink=new Sink();
            Assert.Equal("DB2_PROJECTION_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink))).Code);Assert.Null(sink.Header);
        }
    }

    [Fact]
    public async Task Partial_source_requires_opt_in_and_cancellation_never_takes_source_ownership()
    {
        var fixture=Build();using var backing=new AsyncBacking(fixture.Bytes);using var input=Project(backing,[Hole(fixture.Starts[1],1)]);var sink=new Sink();
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink,new()))).Code);Assert.Null(sink.Header);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Read(input,sink,token:cancel.Token));Assert.Null(sink.Header);
        cancel.Dispose();using var during=new CancellationTokenSource();sink.AfterHeader=()=>during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Read(input,sink,token:during.Token));Assert.Empty(sink.Rows);Assert.True(backing.CanRead);
    }

    [Fact]
    public async Task Invalid_projection_geometry_from_nonstandard_source_is_not_trusted()
    {
        var fixture=Build();var at=fixture.Starts[1];
        BlteUnavailableRange[][] invalid=[
            [Hole(-1,1)],[Hole(at,0)],[Hole(at,long.MaxValue)],[Hole(fixture.Bytes.Length,1)],
            [Hole(at,2,0),Hole(at+1,1,1)],[Hole(at+1,1,0),Hole(at,1,1)],
            [Hole(at,1,0),Hole(at+1,1,0)],[Hole(at,1,-1)],[Hole(at,1,65_536)],
            Enumerable.Range(0,129).Select(index=>Hole(at,1,index)).ToArray()];
        foreach(var ranges in invalid)
        {
            using var input=new UntrustedProjection(fixture.Bytes,ranges);var sink=new Sink();
            Assert.Equal("DB2_PROJECTION_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink))).Code);Assert.Null(sink.Header);
        }
    }

    [Fact]
    public async Task Cross_section_string_dependency_into_keyed_body_is_never_replaced_with_empty_text()
    {
        var fixture=Build();BinaryPrimitives.WriteUInt32LittleEndian(fixture.Bytes.AsSpan(fixture.Starts[0]),9); // global string coordinate1 belongs to keyed section.
        using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1])]);var sink=new Sink();
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink,definition:Definition(true)))).Code);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Required_Common_auxiliary_bytes_must_be_clear_and_copy_overrides_use_the_target_ID()
    {
        var fixture=Build(common:true);
        for(var at=312;at<320;at++)
        {
            using var missing=Project(new AsyncBacking(fixture.Bytes),[Hole(at,1)]);var rejected=new Sink();
            Assert.Equal("BLTE_RANGE_UNAVAILABLE",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(missing,rejected))).Code);Assert.Null(rejected.Header);
        }
        using var input=Project(new AsyncBacking(fixture.Bytes),[Hole(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1])]);var sink=new Sink();
        await Read(input,sink);Assert.Equal(["7","111"],sink.Rows[0].Values);Assert.Equal(["8","99"],sink.Rows[1].Values);
    }

    [Fact]
    public async Task Clear_source_without_holes_keeps_normal_full_source_policy_and_outer_EOF_checks()
    {
        var fixture=Build();using var input=Project(new AsyncBacking(fixture.Bytes),[]);var sink=new Sink();
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink,new()))).Code);
        var trailing=fixture.Bytes.Concat(new byte[]{0}).ToArray();using var malformed=Project(new AsyncBacking(trailing),[Hole(fixture.Starts[1],1)]);
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(malformed,new Sink()))).Code);
    }

    private static Task<Wdc5Completion> Read(Stream input,Sink sink,Wdc5Limits? limits=null,CancellationToken token=default,DbdDocument? definition=null)
        =>Wdc5TableReader.ReadAsync(input,definition??Definition(),"1.2.3.4",limits??new(AllowPartialEncryptedSections:true),sink,token);
    private static DbdDocument Definition(bool text=false)=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID\n"+(text?"string":"int")+" Value\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nValue"+(text?"":"<u32>")+"\n"));
    private static BlteUnavailableRange Hole(long offset,long length,int ordinal=0)=>new(offset,length,ordinal,"FFFFFFFFFFFFFFFF"); // no universal equality to WDC5 section key.
    private static BlteProjectionStream Project(Stream backing,IReadOnlyList<BlteUnavailableRange> holes)=>new(backing,holes);
    private sealed class Sink:IWdc5Consumer
    {
        public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];public Action? AfterHeader;
        public ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token){Header=header;AfterHeader?.Invoke();return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return ValueTask.CompletedTask;}
    }
    private sealed record Fixture(byte[] Bytes,int PrefixEnd,int[] Starts,int[] Ends);
    private static Fixture Build(bool interleaved=false,bool lastKeyed=false,int gap=0,bool zero=false,bool common=false)
    {
        var keys=interleaved?new ulong[]{0,123,lastKeyed?456ul:0}:new ulong[]{0,123};
        var ids=interleaved?new uint[]{zero?0u:7u,21,30}:new uint[]{zero?0u:7u,21};
        var copies=new (uint Target,uint Source)[][]{[(8,ids[0]),(9,21)],[(22,21)],[]};
        var prefixEnd=204+keys.Length*40+28+(common?8:0)+keys.Count(key=>key!=0)*8;
        var starts=new int[keys.Length];var ends=new int[keys.Length];var offset=prefixEnd+gap;
        for(var index=0;index<keys.Length;index++){starts[index]=offset;offset+=4+1+4+copies[index].Length*8;ends[index]=offset;}
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);var header=new byte[204];"WDC5"u8.CopyTo(header);
        void Put(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at),value);
        Put(4,5);Put(136,(uint)keys.Length);Put(140,1);Put(144,4);Put(148,(uint)keys.Length);Put(152,0x11223344);Put(156,0x01020304);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172),4);Put(176,1);Put(188,24);Put(192,common?8u:0u);Put(200,(uint)keys.Length);writer.Write(header);
        for(var index=0;index<keys.Length;index++){writer.Write(keys[index]);writer.Write((uint)starts[index]);writer.Write(1u);writer.Write(1u);writer.Write(0u);writer.Write(4u);writer.Write(0u);writer.Write(0u);writer.Write((uint)copies[index].Length);}
        writer.Write((short)(common?32:0));writer.Write((ushort)0);writer.Write((ushort)0);writer.Write((ushort)(common?0:32));
        writer.Write(common?8u:0u);writer.Write(common?2u:0u);writer.Write(common?99u:0u);writer.Write(0u);writer.Write(0u);
        if(common){writer.Write(ids[0]);writer.Write(111u);}
        for(var index=0;index<keys.Length;index++)if(keys[index]!=0){writer.Write(1u);writer.Write(ids[index]);}
        writer.Write(new byte[gap]);
        for(var index=0;index<keys.Length;index++){writer.Write(index==0?(zero?0u:99u):555u);writer.Write((byte)0);writer.Write(ids[index]);foreach(var copy in copies[index]){writer.Write(copy.Target);writer.Write(copy.Source);}}
        return new(output.ToArray(),prefixEnd,starts,ends);
    }
    private sealed class AsyncBacking:MemoryStream
    {
        private readonly byte[] bytes;
        public AsyncBacking(byte[] bytes):base(bytes){this.bytes=bytes;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Async reads required.");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Async reads required.");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();var count=(int)Math.Min(target.Length,Length-Position);bytes.AsSpan((int)Position,count).CopyTo(target.Span);Position+=count;return ValueTask.FromResult(count);}
    }
    private sealed class UntrustedProjection(byte[] bytes,IReadOnlyList<BlteUnavailableRange> ranges):MemoryStream(bytes),IBlteProjectionSource
    {public IReadOnlyList<BlteUnavailableRange> UnavailableRanges=>ranges;public IReadOnlyList<BlteReadableRange>? ReadableRanges=>null;}
}
