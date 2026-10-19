using System.Buffers.Binary;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class M2SkinReaderTests
{
    [Fact]
    public async Task Modern_zero_header_preserves_raw_fact_with_observed_section60_lookup178_shape()
    {
        var model=await Model();using var source=new AsyncSource(Skin());var skin=await M2SkinReader.ReadAsync(source,model);
        Assert.Equal("m2-skin-native-v2",M2SkinReader.Revision);Assert.Equal(0u,skin.MaximumBones);
        Assert.Equal((ushort)60,Assert.Single(skin.Sections).BoneCount);Assert.Equal(178,model.BoneLookup.Count);
        Assert.Equal(new uint[]{2,0,1},M2StaticGeometry.Create(model,skin).Indices);Assert.Equal(source.Length,source.Position);Assert.True(source.CanRead);
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED",Assert.Throws<DiscoveryException>(()=>M2StaticMaterials.AdmitOpaque(model,skin)).Code);
    }

    [Theory]
    [InlineData(0,60,178)][InlineData(118,60,178)][InlineData(0,256,256)]
    public async Task Zero_header_still_uses_actual_lookup_end_and_independent_finite_palette_cap(ushort start,ushort count,int lookup)
    {
        var model=await Model(lookup:lookup);using var source=new AsyncSource(Skin(boneCount:count,boneStart:start,influences:4));
        var skin=await M2SkinReader.ReadAsync(source,model);Assert.Equal(0u,skin.MaximumBones);Assert.Equal(start,skin.Sections[0].BoneStart);Assert.Equal((ushort)4,skin.Sections[0].BoneInfluences);
    }

    [Theory]
    [InlineData(119,60,2,178,"MODEL_INVALID")][InlineData(0,179,2,178,"MODEL_INVALID")]
    [InlineData(0,257,2,300,"MODEL_LIMIT")][InlineData(0,60,5,178,"MODEL_INVALID")]
    public async Task Zero_header_cannot_waive_lookup_span_palette_capacity_or_influence_bounds(ushort start,ushort count,ushort influences,int lookup,string code)
    {
        var model=await Model(lookup:lookup);await Error(code,Skin(boneCount:count,boneStart:start,influences:influences),model);
    }

    [Theory]
    [InlineData(59u,"MODEL_INVALID")][InlineData(257u,"MODEL_LIMIT")]
    public async Task Positive_header_constraints_remain_unchanged(uint header,string code)=>await Error(code,Skin(header:header),await Model());

    [Theory]
    [InlineData(264u)][InlineData(274u)]
    public async Task Positive_header60_retains_raw_value_and_exact_actual_lookup_bounds(uint version)
    {
        var model=await Model(version);using var input=new AsyncSource(Skin(version,header:60,boneStart:118));var skin=await M2SkinReader.ReadAsync(input,model);
        Assert.Equal(60u,skin.MaximumBones);Assert.Equal((ushort)118,skin.Sections[0].BoneStart);await Error("MODEL_INVALID",Skin(version,header:60,boneStart:119),model);
    }

    [Fact]
    public async Task Zero_header_does_not_expand_legacy264_or_model_container_version_profiles()
    {
        await Error("MODEL_INVALID",Skin(264),await Model(264));
        foreach(var bytes in new[]{ModelBytes(272),ModelBytes(274,wrap:false),ModelBytes(264,wrap:true)})
        {using var input=new AsyncSource(bytes);Assert.Equal("MODEL_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>M2ModelReader.ReadAsync(input))).Code);Assert.True(input.CanRead);}
    }

    [Theory]
    [InlineData("properties")][InlineData("vertex")][InlineData("triangle")][InlineData("section")]
    public async Task Zero_header_keeps_property_count_both_index_levels_and_section_local_geometry_validation(string shape)
    {
        var bytes=Skin();switch(shape)
        {
            case "properties":U32(bytes,20,2);break;
            case "vertex":U16(bytes,56,3);break;
            case "triangle":U16(bytes,62,3);break;
            case "section":U16(bytes,80+4,1);U16(bytes,80+6,2);break;
        }
        await Error("MODEL_INVALID",bytes,await Model());
    }

    [Fact]
    public async Task Async_short_reads_cancellation_at_every_read_and_source_ownership_remain_observed()
    {
        var model=await Model();using var successful=new AsyncSource(Skin());await M2SkinReader.ReadAsync(successful,model);var reads=successful.Reads;Assert.True(reads>4);
        for(var at=1;at<=reads;at++)
        {
            using var cts=new CancellationTokenSource();using var input=new AsyncSource(Skin(),cts,at);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>M2SkinReader.ReadAsync(input,model,token:cts.Token));Assert.True(input.CanRead);
        }
        using var canceled=new CancellationTokenSource();canceled.Cancel();using var untouched=new AsyncSource(Skin());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>M2SkinReader.ReadAsync(untouched,model,token:canceled.Token));Assert.Equal(0,untouched.Reads);Assert.True(untouched.CanRead);
    }

    private static async Task Error(string code,byte[] bytes,M2Model model)
    {using var source=new AsyncSource(bytes);Assert.Equal(code,(await Assert.ThrowsAsync<DiscoveryException>(()=>M2SkinReader.ReadAsync(source,model))).Code);Assert.True(source.CanRead);}
    private static async Task<M2Model> Model(uint version=274,int lookup=178)
    {using var input=new AsyncSource(ModelBytes(version,lookup:lookup));return await M2ModelReader.ReadAsync(input);}

    // Independently authored tiny asymmetric files; only the reviewed 60/178/header0 SHAPE is native-derived.
    // No downloaded geometry, row values, names or asset bytes are fixtures.
    private static byte[] ModelBytes(uint version,int lookup=178,bool? wrap=null)
    {
        const int vertices=304,bones=448,lookupAt=536;var body=new byte[lookupAt+lookup*2];"MD20"u8.CopyTo(body);U32(body,4,version);
        U32(body,44,1);U32(body,48,bones);U16(body,bones+8,ushort.MaxValue);
        U32(body,60,3);U32(body,64,vertices);U32(body,68,1);U32(body,120,(uint)lookup);U32(body,124,lookupAt);
        for(var i=0;i<3;i++){F(body,vertices+i*48,i==1?2:0);F(body,vertices+i*48+4,i==2?3:0);F(body,vertices+i*48+28,1);}
        if(!(wrap??version!=264))return body;
        var bytes=new byte[body.Length+20];"MD21"u8.CopyTo(bytes);U32(bytes,4,(uint)body.Length);body.CopyTo(bytes,8);"SFID"u8.CopyTo(bytes.AsSpan(body.Length+8));U32(bytes,body.Length+12,4);U32(bytes,body.Length+16,900);return bytes;
    }
    private static byte[] Skin(uint version=274,uint header=0,ushort boneCount=60,ushort boneStart=0,ushort influences=2)
    {
        var start=version==264?48:56;var bytes=new byte[start+76];"SKIN"u8.CopyTo(bytes);
        U32(bytes,4,3);U32(bytes,8,(uint)start);U16(bytes,start,2);U16(bytes,start+2,0);U16(bytes,start+4,1);
        U32(bytes,12,3);U32(bytes,16,(uint)start+6);U16(bytes,start+6,0);U16(bytes,start+8,1);U16(bytes,start+10,2);
        U32(bytes,20,3);U32(bytes,24,(uint)start+12);U32(bytes,28,1);U32(bytes,32,(uint)start+24);U32(bytes,44,header);
        U16(bytes,start+24+6,3);U16(bytes,start+24+10,3);U16(bytes,start+24+12,boneCount);U16(bytes,start+24+14,boneStart);U16(bytes,start+24+16,influences);return bytes;
    }
    private static void U32(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    private static void U16(byte[] bytes,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),value);
    private static void F(byte[] bytes,int at,float value)=>BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at),value);
    private sealed class AsyncSource(byte[] bytes,CancellationTokenSource? cancel=null,int cancelAt=int.MaxValue):Stream
    {
        private readonly MemoryStream inner=new(bytes);public int Reads;public override bool CanRead=>inner.CanRead;public override bool CanSeek=>true;public override bool CanWrite=>false;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)
        {if(++Reads==cancelAt)cancel?.Cancel();token.ThrowIfCancellationRequested();return inner.ReadAsync(target[..Math.Min(7,target.Length)],token);}
        public override int Read(byte[] b,int o,int c)=>throw new InvalidOperationException("Synchronous parsing is forbidden.");
        public override long Seek(long at,SeekOrigin from)=>inner.Seek(at,from);public override void SetLength(long value)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
