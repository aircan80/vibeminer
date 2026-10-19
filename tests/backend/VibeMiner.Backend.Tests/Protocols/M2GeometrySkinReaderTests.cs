using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class M2GeometrySkinReaderTests
{
    [Fact]
    public async Task Geometry_only_preserves_all_sections_and_explicitly_uninterpreted_material_metadata()
    {
        var model=await Model();var bytes=Skin(opaqueGarbage:true);using var source=new AsyncSource(bytes);
        var skin=await M2GeometrySkinReader.ReadAsync(source,model);var geometry=M2StaticGeometry.Create(model,skin);
        Assert.Same(model,skin.Model);Assert.Equal(new uint[]{2,0,1},geometry.Indices);Assert.Equal(2,geometry.Sections.Count);
        Assert.Equal(geometry.Sections[0],geometry.Sections[1]); // Deliberate duplicate source section, never filtered by a batch.
        Assert.Equal(M2GeometryScope.UnfilteredDeclaredSections,skin.Scope);
        Assert.Equal(M2GeometryEvaluation.NotEvaluated,skin.MaterialEvaluation);Assert.Equal(M2GeometryEvaluation.NotEvaluated,skin.PoseEvaluation);
        Assert.Equal(M2GeometryEvaluation.NotEvaluated,skin.VisibilityEvaluation);Assert.Equal(M2GeometryEvaluation.NotEvaluated,skin.AppearanceEvaluation);
        Assert.Equal(1u,skin.BatchDeclaration.Count);Assert.Equal(24,skin.BatchDeclaration.Stride);Assert.Equal(1u,skin.ShadowBatchDeclaration.Count);Assert.Equal(12,skin.ShadowBatchDeclaration.Stride);
        Assert.Equal(0u,skin.MaximumBones);Assert.Equal(bytes.Length,skin.SourceBytes);Assert.Equal(source.Length,source.Position);Assert.True(source.CanRead);
        await Error("MODEL_INVALID",()=>Strict(bytes,model));
    }

    [Fact]
    public async Task Empty_coordinate_lookup_is_not_invented_as_UV0_or_an_appearance_draw_set()
    {
        var model=await Model();var bytes=Skin();using var source=new AsyncSource(bytes);var skin=await M2GeometrySkinReader.ReadAsync(source,model);
        Assert.Empty(model.TextureCoordinates);Assert.Equal(3,M2StaticGeometry.Create(model,skin).Indices.Count);
        Assert.Equal("MODEL_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Strict(bytes,model))).Code);
        var other=await Model();Assert.Equal("MODEL_INVALID",Assert.Throws<DiscoveryException>(()=>M2StaticGeometry.Create(other,skin)).Code);
    }

    [Theory]
    [InlineData(36,513u,0u,"MODEL_LIMIT")][InlineData(36,1u,uint.MaxValue,"MODEL_INVALID")]
    [InlineData(48,513u,0u,"MODEL_LIMIT")][InlineData(48,1u,55u,"MODEL_INVALID")]
    public async Task Opaque_batch_and_shadow_descriptors_still_have_complete_bounds(int at,uint count,uint offset,string code)
    {
        var bytes=Skin();U32(bytes,at,count);U32(bytes,at+4,offset);var model=await Model();await GeometryError(code,bytes,model);
    }

    [Theory]
    [InlineData("vertex")][InlineData("triangle")][InlineData("section")][InlineData("properties")]
    [InlineData("palette")][InlineData("influence")][InlineData("257")][InlineData("radius")]
    public async Task Material_opacity_does_not_weaken_any_geometry_validation(string kind)
    {
        var bytes=Skin();var section=(int)Get(bytes,32);switch(kind)
        {
            case "vertex":U16(bytes,56,3);break;
            case "triangle":U16(bytes,62,3);break;
            case "section":U16(bytes,section+4,1);U16(bytes,section+6,2);break;
            case "properties":U32(bytes,20,2);break;
            case "palette":U16(bytes,section+14,119);break;
            case "influence":U16(bytes,section+16,5);break;
            case "257":U16(bytes,section+12,257);break;
            case "radius":F(bytes,section+44,-1);break;
        }
        await GeometryError(kind=="257"?"MODEL_LIMIT":"MODEL_INVALID",bytes,await Model());
    }

    [Theory]
    [InlineData("legacy")][InlineData("skeleton")][InlineData("bones")]
    public async Task New_profile_does_not_admit_legacy_or_external_skeleton_dependencies(string kind)
    {
        var bytes=ModelBytes();if(kind=="legacy")bytes=Body(264);else bytes=bytes.Concat(Chunk(kind=="skeleton"?"SKID":"BFID",Word(800))).ToArray();
        var model=await Model(bytes);using var source=new AsyncSource(Skin());await Error("MODEL_UNSUPPORTED",()=>M2GeometrySkinReader.ReadAsync(source,model));Assert.Equal(0,source.Reads);Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Projection_truncation_source_changes_and_combined_limits_never_publish_geometry()
    {
        var model=await Model();var bytes=Skin();using var backing=new MemoryStream(bytes);using var projection=new BlteProjectionStream(backing,[new(bytes.Length-1,1,0,"0123456789ABCDEF")]);
        await Error("MODEL_UNSUPPORTED",()=>M2GeometrySkinReader.ReadAsync(projection,model));Assert.Equal(0,backing.Position);
        await GeometryError("MODEL_INVALID",bytes[..^1],model);
        using var changed=new AsyncSource(bytes,changeLength:true);await Error("MODEL_INVALID",()=>M2GeometrySkinReader.ReadAsync(changed,model));Assert.True(changed.CanRead);
        using var limited=new AsyncSource(bytes);await Error("MODEL_LIMIT",()=>M2GeometrySkinReader.ReadAsync(limited,model,new(MaxOutputBytes:1)));Assert.True(limited.CanRead);
        using var sourceLimited=new AsyncSource(bytes);await Error("MODEL_LIMIT",()=>M2GeometrySkinReader.ReadAsync(sourceLimited,model,new(MaxSourceBytes:bytes.Length-1)));Assert.Equal(0,sourceLimited.Reads);
    }

    [Fact]
    public async Task Large_directories_are_buffered_without_reading_or_copying_the_material_tail()
    {
        var model=await Model();var bytes=Skin(triangles:300000,batches:512,shadows:512);using var source=new AsyncSource(bytes);
        var skin=await M2GeometrySkinReader.ReadAsync(source,model);Assert.Equal(300000,skin.TriangleLookup.Count);Assert.Equal(512u,skin.BatchDeclaration.Count);
        Assert.InRange(source.MaximumRequest,1,65536);Assert.Equal(0,source.BytesIn((int)skin.BatchDeclaration.Offset,512*24+512*12));
        Assert.Equal(300000,M2StaticGeometry.Create(model,skin).Indices.Count);Assert.True(source.CanRead);
        using var tooTight=new AsyncSource(bytes);await Error("MODEL_LIMIT",()=>M2GeometrySkinReader.ReadAsync(tooTight,model,new(MaxOutputBytes:1<<20)));
    }

    [Fact]
    public async Task Cancellation_at_every_async_read_preserves_source_ownership_and_no_partial_result()
    {
        var model=await Model();var bytes=Skin();using var success=new AsyncSource(bytes);await M2GeometrySkinReader.ReadAsync(success,model);
        for(var at=1;at<=success.Reads;at++)
        {using var cts=new CancellationTokenSource();using var input=new AsyncSource(bytes,cts,at);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>M2GeometrySkinReader.ReadAsync(input,model,token:cts.Token));Assert.True(input.CanRead);}
        using var before=new CancellationTokenSource();before.Cancel();using var untouched=new AsyncSource(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>M2GeometrySkinReader.ReadAsync(untouched,model,token:before.Token));Assert.Equal(0,untouched.Reads);
    }

    private static async Task GeometryError(string code,byte[] bytes,M2Model model)
    {using var source=new AsyncSource(bytes);await Error(code,()=>M2GeometrySkinReader.ReadAsync(source,model));Assert.True(source.CanRead);}
    private static async Task Error(string code,Func<Task> action)=>Assert.Equal(code,(await Assert.ThrowsAsync<DiscoveryException>(action)).Code);
    private static async Task<M2Skin> Strict(byte[] bytes,M2Model model){using var input=new AsyncSource(bytes);return await M2SkinReader.ReadAsync(input,model);}
    private static async Task<M2Model> Model(byte[]? bytes=null){using var input=new AsyncSource(bytes??ModelBytes());return await M2ModelReader.ReadAsync(input);}
    // All bytes are authored. Deliberately asymmetric positions and duplicate sections are source-shape facts, not game payloads.
    private static byte[] ModelBytes()=>Chunk("MD21",Body(274)).Concat(Chunk("SFID",Word(900))).Concat(Chunk("TXID",Word(700))).ToArray();
    private static byte[] Body(uint version)
    {
        using var output=new MemoryStream();output.SetLength(304);output.Position=304;using var writer=new BinaryWriter(output,Encoding.UTF8,true);var descriptors=new List<(int At,int Count,int Offset)>();
        void Add(int at,int count,Action<BinaryWriter> write){descriptors.Add((at,count,(int)output.Position));write(writer);}
        Add(60,3,w=>{for(var i=0;i<3;i++){w.Write(i==1?2f:0f);w.Write(i==2?3f:0f);w.Write(0f);w.Write(0u);w.Write(0u);w.Write(0f);w.Write(0f);w.Write(1f);w.Write(0f);w.Write(0f);w.Write(0f);w.Write(0f);}});
        Add(44,1,w=>{var bone=new byte[88];U16(bone,8,ushort.MaxValue);w.Write(bone);});Add(120,178,w=>w.Write(new byte[356]));
        Add(80,1,w=>w.Write(new byte[16]));Add(112,1,w=>w.Write(0u));Add(128,1,w=>w.Write((ushort)0));Add(144,1,w=>w.Write(ushort.MaxValue));Add(152,1,w=>w.Write(ushort.MaxValue));
        var body=output.ToArray();"MD20"u8.CopyTo(body);U32(body,4,version);U32(body,68,1);foreach(var d in descriptors){U32(body,d.At,(uint)d.Count);U32(body,d.At+4,(uint)d.Offset);}return body;
    }
    private static byte[] Skin(bool opaqueGarbage=false,int triangles=3,int batches=1,int shadows=1)
    {
        var triangleAt=62;var propertiesAt=triangleAt+triangles*2;var sectionsAt=propertiesAt+12;var batchAt=sectionsAt+96;var shadowAt=batchAt+batches*24;var b=new byte[shadowAt+shadows*12];"SKIN"u8.CopyTo(b);
        U32(b,4,3);U32(b,8,56);U16(b,56,2);U16(b,58,0);U16(b,60,1);U32(b,12,(uint)triangles);U32(b,16,(uint)triangleAt);for(var i=0;i<triangles;i++)U16(b,triangleAt+i*2,(ushort)(i%3));
        U32(b,20,3);U32(b,24,(uint)propertiesAt);U32(b,28,2);U32(b,32,(uint)sectionsAt);
        for(var i=0;i<2;i++){var at=sectionsAt+i*48;U16(b,at+6,3);U16(b,at+10,3);U16(b,at+12,60);U16(b,at+16,2);}
        U32(b,36,(uint)batches);U32(b,40,(uint)batchAt);U32(b,48,(uint)shadows);U32(b,52,(uint)shadowAt);
        for(var i=0;i<batches;i++){var at=batchAt+i*24;if(opaqueGarbage)Array.Fill(b,(byte)255,at,24);else{U16(b,at+8,ushort.MaxValue);U16(b,at+14,1);}}
        if(opaqueGarbage)Array.Fill(b,(byte)255,shadowAt,shadows*12);return b;
    }
    private static byte[] Chunk(string id,byte[] payload){var b=new byte[payload.Length+8];Encoding.ASCII.GetBytes(id).CopyTo(b,0);U32(b,4,(uint)payload.Length);payload.CopyTo(b,8);return b;}
    private static byte[] Word(uint value){var b=new byte[4];U32(b,0,value);return b;}
    private static uint Get(byte[] b,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static void U32(byte[] b,int at,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),v);
    private static void U16(byte[] b,int at,ushort v)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),v);
    private static void F(byte[] b,int at,float v)=>BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at),v);
    private sealed class AsyncSource(byte[] bytes,CancellationTokenSource? cancel=null,int cancelAt=int.MaxValue,bool changeLength=false):Stream
    {
        private readonly MemoryStream inner=new(bytes);private readonly List<(long Offset,int Count)> reads=[];public int Reads;public int MaximumRequest;
        public int BytesIn(int at,int count)=>reads.Sum(r=>(int)Math.Max(0,Math.Min(r.Offset+r.Count,at+(long)count)-Math.Max(r.Offset,at)));
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>inner.Length+(changeLength&&Reads>0?1:0);public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)
        {if(++Reads==cancelAt)cancel?.Cancel();token.ThrowIfCancellationRequested();MaximumRequest=Math.Max(MaximumRequest,target.Length);var at=inner.Position;var count=await inner.ReadAsync(target[..Math.Min(997,target.Length)],token);reads.Add((at,count));return count;}
        public override int Read(byte[] b,int o,int c)=>throw new InvalidOperationException("Synchronous reads forbidden");public override long Seek(long n,SeekOrigin o)=>inner.Seek(n,o);public override void SetLength(long n)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
}
