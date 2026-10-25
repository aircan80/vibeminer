using System.Buffers.Binary;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Blp2DecoderTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(4)][InlineData(8)]
    public async Task Paletted_alpha_planes_are_low_bit_first_and_independent_of_palette_alpha(byte depth)
    {
        byte[] indices=[1,2,3,4,1,2,3,4,1];
        byte[] alpha=depth switch{0=>[],1=>[0xA5,0x01],4=>[0x10,0x32,0x54,0x76,0x08],_=>[0,1,17,34,68,128,170,254,255]};
        byte[] expectedAlpha=depth switch{0=>[255,255,255,255,255,255,255,255,255],1=>[255,0,255,0,0,255,0,255,255],4=>[0,17,34,51,68,85,102,119,136],_=>alpha};
        using var input=new AsyncInput(Build(1,depth,0,9,1,[[..indices,..alpha]]));var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(Blp2Encoding.Paletted,image.Encoding);Assert.Equal(36,image.RowStride);Assert.Equal(depth,image.AlphaDepth);Assert.True(input.CanRead);
        for(var index=0;index<indices.Length;index++)Assert.Equal(Color(indices[index],expectedAlpha[index]),Pixel(image,index,0));
    }

    [Fact]
    public async Task Palette_rows_keep_top_left_orientation_and_unpremultiplied_RGB_even_when_alpha_is_zero()
    {
        var bytes=Build(1,8,0,2,2,[[1,2,3,4,0,128,255,17]]);using var input=new AsyncInput(bytes);var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(new byte[]{200,10,20,0},Pixel(image,0,0));Assert.Equal(new byte[]{30,220,40,128},Pixel(image,1,0));
        Assert.Equal(new byte[]{50,60,240,255},Pixel(image,0,1));Assert.Equal(new byte[]{250,230,210,17},Pixel(image,1,1));
    }

    [Fact]
    public async Task BC1_four_color_interpolation_and_RGB565_endpoint_expansion_have_exact_pixels()
    {
        var selectors=Selectors(Enumerable.Range(0,16).Select(index=>index%4),2);
        var bytes=Build(2,0,0,4,4,[Block(0xF800,0x001F,(uint)selectors)]);using var input=new AsyncInput(bytes);var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(new byte[]{255,0,0,255},Pixel(image,0,0));Assert.Equal(new byte[]{0,0,255,255},Pixel(image,1,0));
        Assert.Equal(new byte[]{170,0,85,255},Pixel(image,2,0));Assert.Equal(new byte[]{85,0,170,255},Pixel(image,3,0));
        using var endpoint=new AsyncInput(Build(2,0,0,1,1,[Block(0x8C71,0,0)]));var precise=await Blp2Decoder.DecodeAsync(endpoint);
        Assert.Equal(new byte[]{140,142,140,255},Pixel(precise,0,0));
    }

    [Theory]
    [InlineData(0,255)][InlineData(1,0)]
    public async Task BC1_three_color_fourth_pixel_is_black_with_RGB_or_RGBA_alpha_policy(byte depth,byte expectedAlpha)
    {
        using var input=new AsyncInput(Build(2,depth,0,4,1,[Block(0,0xFFFF,0xE4)]));var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(new byte[]{0,0,0,255},Pixel(image,0,0));Assert.Equal(new byte[]{255,255,255,255},Pixel(image,1,0));
        Assert.Equal(new byte[]{127,127,127,255},Pixel(image,2,0));Assert.Equal(new byte[]{0,0,0,expectedAlpha},Pixel(image,3,0));
        using var equal=new AsyncInput(Build(2,depth,0,1,1,[Block(0xF800,0xF800,3)]));var same=await Blp2Decoder.DecodeAsync(equal);
        Assert.Equal(new byte[]{0,0,0,expectedAlpha},Pixel(same,0,0));
    }

    [Theory]
    [InlineData(4)][InlineData(8)]
    public async Task BC2_uses_explicit_alpha_and_forces_four_color_mode_even_with_reversed_endpoints(byte depth)
    {
        var alpha=new byte[8];for(var index=0;index<8;index++)alpha[index]=(byte)((index*2)|((index*2+1)<<4));
        var selectors=(uint)Selectors(Enumerable.Range(0,16).Select(index=>index%2+2),2);
        using var input=new AsyncInput(Build(2,depth,1,4,4,[[..alpha,..Block(0,0xFFFF,selectors)]]));var image=await Blp2Decoder.DecodeAsync(input);
        for(var index=0;index<16;index++){var color=index%2==0?85:170;Assert.Equal(new byte[]{(byte)color,(byte)color,(byte)color,(byte)(index*17)},Pixel(image,index%4,index/4));}
        Assert.Equal(Blp2Encoding.Bc2,image.Encoding);
    }

    [Theory]
    [InlineData(210,0,new byte[]{210,0,180,150,120,90,60,30})]
    [InlineData(10,20,new byte[]{10,20,12,14,16,18,0,255})]
    [InlineData(50,50,new byte[]{50,50,50,50,50,50,0,255})]
    public async Task BC3_both_alpha_branches_and_high_selector_bits_are_exact_and_color_is_always_four_way(byte a0,byte a1,byte[] expected)
    {
        var alpha=new byte[8];alpha[0]=a0;alpha[1]=a1;var selectors=Selectors(Enumerable.Range(0,16).Select(index=>index%8),3);
        for(var index=0;index<6;index++)alpha[2+index]=(byte)(selectors>>(8*index));
        using var input=new AsyncInput(Build(2,8,7,4,4,[[..alpha,..Block(0,0xFFFF,uint.MaxValue)]]));var image=await Blp2Decoder.DecodeAsync(input);
        for(var index=0;index<16;index++)Assert.Equal(new byte[]{170,170,170,expected[index%8]},Pixel(image,index%4,index/4));
        Assert.Equal(Blp2Encoding.Bc3,image.Encoding);
    }

    [Theory]
    [InlineData(5,3)][InlineData(3,5)]
    public async Task Partial_edge_blocks_are_clipped_without_mixing_rows_or_block_order(int width,int height)
    {
        using var input=new AsyncInput(Build(2,0,0,width,height,[[..Block(0xF800,0,0),..Block(0x07E0,0,0)]]));var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(width*height*4,image.Rgba.Length);
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {var green=width==5?x>=4:y>=4;Assert.Equal(green?new byte[]{0,255,0,255}:new byte[]{255,0,0,255},Pixel(image,x,y));}
    }

    [Fact]
    public async Task Raw_BGRA_preserves_stored_straight_alpha_and_row_orientation()
    {
        using var input=new AsyncInput(Build(3,8,0,1,2,[[20,10,200,128,240,60,50,0]]));var image=await Blp2Decoder.DecodeAsync(input);
        Assert.Equal(Blp2Encoding.Bgra8,image.Encoding);Assert.Equal(new byte[]{200,10,20,128},Pixel(image,0,0));Assert.Equal(new byte[]{50,60,240,0},Pixel(image,0,1));
    }

    [Fact]
    public async Task Selected_mip_has_its_own_dimensions_limits_and_reads_without_loading_other_payloads()
    {
        var bytes=Build(1,0,0,4,4,[Enumerable.Repeat((byte)1,16).ToArray(),[2,2,2,2]]);using var input=new AsyncInput(bytes);
        var image=await Blp2Decoder.DecodeAsync(input,1,new(MaxPixels:4,MaxMipBytes:4,MaxRgbaBytes:16));
        Assert.Equal(2,image.Width);Assert.Equal(2,image.Height);Assert.Equal(4,image.BaseWidth);Assert.Equal(4,image.BaseHeight);Assert.Equal(1,image.MipLevel);Assert.Equal(2,image.Mips.Count);
        Assert.Equal(Color(2,255),Pixel(image,1,1));Assert.Equal(148+1024+4,input.TotalRead);
        Assert.Equal("BLP_MIP_UNAVAILABLE",await Code(bytes,mip:2));
    }

    [Fact]
    public async Task Observed_mip_flag_0x11_is_preserved_without_inventing_a_mip_count_from_its_value()
    {
        var bytes=Build(2,1,0,4,4,[Block(0xF800,0,0),Block(0x07E0,0,0)]);bytes[11]=17;
        using var input=new AsyncInput(bytes);var image=await Blp2Decoder.DecodeAsync(input,1);
        Assert.Equal(17,image.MipFlags);Assert.Equal(2,image.Mips.Count);Assert.Equal(2,image.Width);Assert.Equal(2,image.Height);
        Assert.Equal(new byte[]{0,255,0,255},Pixel(image,0,0));
        bytes[11]=19;Assert.Equal("BLP_UNSUPPORTED",await Code(bytes));
        bytes[11]=0;Assert.Equal("BLP_INVALID",await Code(bytes));
        bytes[11]=17;Put(bytes,84,7);Assert.Equal("BLP_INVALID",await Code(bytes,mip:1));
    }

    [Fact]
    public async Task Rectangular_mip_dimensions_clamp_each_axis_independently()
    {
        using var input=new AsyncInput(Build(2,0,0,1,8,[[..Block(0xF800,0,0),..Block(0xF800,0,0)],Block(0x07E0,0,0)]));
        var image=await Blp2Decoder.DecodeAsync(input,1);Assert.Equal(1,image.Width);Assert.Equal(4,image.Height);Assert.Equal(new byte[]{0,255,0,255},Pixel(image,0,3));
    }

    [Fact]
    public async Task Every_truncation_of_palette_and_selected_payload_fails_and_caller_keeps_stream_ownership()
    {
        var bytes=Build(1,4,0,3,1,[[1,2,3,0x21,0x0F]]);
        for(var length=0;length<bytes.Length;length++)Assert.Equal("BLP_INVALID",await Code(bytes[..length]));
        using var input=new AsyncInput(bytes){FailAtEof=true};await Assert.ThrowsAsync<IOException>(()=>Blp2Decoder.DecodeAsync(input));Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Every_present_mip_is_validated_even_when_another_mip_was_requested()
    {
        var bytes=Build(1,0,0,4,4,[new byte[16],new byte[4]]);var changed=bytes.ToArray();Put(changed,84,15);Assert.Equal("BLP_INVALID",await Code(changed,mip:1));
        changed=bytes.ToArray();Put(changed,24,1173);Assert.Equal("BLP_INVALID",await Code(changed));
        changed=bytes.ToArray();Put(changed,20,148);Assert.Equal("BLP_INVALID",await Code(changed));
        changed=bytes.ToArray();Put(changed,24,uint.MaxValue);Assert.Equal("BLP_INVALID",await Code(changed));
        changed=bytes.ToArray();Put(changed,24,0);Assert.Equal("BLP_INVALID",await Code(changed));
        changed=bytes.ToArray();Put(changed,80,1172);Put(changed,144,1);Assert.Equal("BLP_INVALID",await Code(changed));
        changed=bytes.ToArray();changed[11]=0;Assert.Equal("BLP_INVALID",await Code(changed));
        changed=Build(1,0,0,1,1,[[1],[2]]);Assert.Equal("BLP_INVALID",await Code(changed));
    }

    [Theory]
    [InlineData("source")][InlineData("dimension")][InlineData("pixels")][InlineData("mip")][InlineData("rgba")]
    public async Task Allocation_bounds_fail_before_reading_selected_payload(string bound)
    {
        var limits=bound switch{"source"=>new Blp2Limits(MaxSourceBytes:148),"dimension"=>new(MaxDimension:1),"pixels"=>new(MaxPixels:3),"mip"=>new(MaxMipBytes:3),_=>new(MaxRgbaBytes:4)};
        using var input=new AsyncInput(Build(1,0,0,2,2,[[1,2,3,4]]));Assert.Equal("BLP_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Blp2Decoder.DecodeAsync(input,limits:limits))).Code);Assert.InRange(input.TotalRead,0,148);
    }

    [Fact]
    public async Task Unsigned_dimensions_and_checked_geometry_cannot_wrap_into_small_allocations()
    {
        var bytes=Build(1,8,0,1,1,[[1,255]]);Put(bytes,12,uint.MaxValue);Assert.Equal("BLP_LIMIT",await Code(bytes));
        Put(bytes,12,int.MaxValue);Put(bytes,16,int.MaxValue);
        Assert.Equal("BLP_LIMIT",await Code(bytes,limits:new(MaxDimension:int.MaxValue)));
        bytes=Build(1,0,0,1,1,[[1]]);Put(bytes,12,0);Assert.Equal("BLP_INVALID",await Code(bytes));
    }

    [Theory]
    [InlineData(8,4)][InlineData(9,2)][InlineData(10,9)][InlineData(11,2)][InlineData(4,0)]
    public async Task Unsupported_container_and_encoding_combinations_are_explicit(int at,byte value)
    {
        var bytes=Build(2,1,0,1,1,[Block(0xF800,0,0)]);bytes[at]=value;Assert.Equal("BLP_UNSUPPORTED",await Code(bytes));
        bytes=Build(1,0,0,1,1,[[1]]);bytes[3]=(byte)'1';Assert.Equal("BLP_UNSUPPORTED",await Code(bytes));
        bytes=Build(3,0,0,1,1,[[1,2,3,4]]);Assert.Equal("BLP_UNSUPPORTED",await Code(bytes));
    }

    [Fact]
    public async Task Cancellation_before_IO_and_before_pixel_loops_prevents_a_completed_image()
    {
        var bytes=Build(1,0,0,2,2,[[1,2,3,4]]);using var canceled=new CancellationTokenSource();canceled.Cancel();using var input=new AsyncInput(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Blp2Decoder.DecodeAsync(input,token:canceled.Token));Assert.Equal(0,input.TotalRead);
        using var during=new CancellationTokenSource();using var source=new AsyncInput(bytes){AfterRead=position=>{if(position==bytes.Length)during.Cancel();}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Blp2Decoder.DecodeAsync(source,token:during.Token));Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Unused_palette_padding_and_source_gaps_are_not_invented_zero_requirements()
    {
        var bytes=Build(2,0,0,1,1,[Block(0xF800,0,0)]);var padded=new byte[bytes.Length+1024+3];bytes.AsSpan(0,148).CopyTo(padded);padded.AsSpan(148,1024).Fill(0xA5);
        bytes.AsSpan(148).CopyTo(padded.AsSpan(1172));Put(padded,20,1172);padded[^1]=0x77;
        using var input=new AsyncInput(padded);var image=await Blp2Decoder.DecodeAsync(input);Assert.Equal(new byte[]{255,0,0,255},Pixel(image,0,0));
    }

    [Fact]
    public async Task Largest_mip_within_a_side_bound_is_the_first_declared_level_that_fits_and_nothing_is_resized()
    {
        // 4x2 BGRA with declared 4x2, 2x1 and 1x1 levels; each level has its own fill byte.
        var bytes=Build(3,8,0,4,2,[Enumerable.Repeat((byte)5,32).ToArray(),Enumerable.Repeat((byte)7,8).ToArray(),[9,9,9,9]]);
        using(var input=new AsyncInput(bytes)){var image=await Blp2Decoder.DecodeLargestWithinAsync(input,2);
            Assert.Equal((1,2,1,4,2),(image.MipLevel,image.Width,image.Height,image.BaseWidth,image.BaseHeight));Assert.Equal(new byte[]{7,7,7,7},Pixel(image,1,0));}
        using(var input=new AsyncInput(bytes))Assert.Equal(0,(await Blp2Decoder.DecodeLargestWithinAsync(input,1024)).MipLevel);
        using(var input=new AsyncInput(bytes))Assert.Equal(2,(await Blp2Decoder.DecodeLargestWithinAsync(input,1)).MipLevel);
        // Only the base level is declared: no smaller level is invented.
        using(var input=new AsyncInput(Build(3,8,0,4,2,[new byte[32]])))
            Assert.Equal("BLP_MIP_UNAVAILABLE",(await Assert.ThrowsAsync<DiscoveryException>(()=>Blp2Decoder.DecodeLargestWithinAsync(input,2))).Code);
        using(var input=new AsyncInput(bytes))await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>Blp2Decoder.DecodeLargestWithinAsync(input,0));
    }

    private static byte[] Pixel(Blp2Image image,int x,int y)=>image.Rgba.AsSpan(y*image.RowStride+x*4,4).ToArray();
    private static byte[] Color(int index,byte alpha)=>index switch{1=>[200,10,20,alpha],2=>[30,220,40,alpha],3=>[50,60,240,alpha],_=>[250,230,210,alpha]};
    private static ulong Selectors(IEnumerable<int> selectors,int bits){ulong value=0;var index=0;foreach(var selector in selectors)value|=(ulong)selector<<(index++*bits);return value;}
    private static byte[] Block(ushort c0,ushort c1,uint selectors){var bytes=new byte[8];BinaryPrimitives.WriteUInt16LittleEndian(bytes,c0);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),c1);Put(bytes,4,selectors);return bytes;}
    private static byte[] Build(byte encoding,byte depth,byte alphaEncoding,int width,int height,byte[][] mips)
    {
        var metadata=encoding==1?1172:148;var bytes=new byte[metadata+mips.Sum(mip=>mip.Length)];"BLP2"u8.CopyTo(bytes);Put(bytes,4,1);bytes[8]=encoding;bytes[9]=depth;bytes[10]=alphaEncoding;bytes[11]=(byte)(mips.Length>1?1:0);Put(bytes,12,(uint)width);Put(bytes,16,(uint)height);
        if(encoding==1)for(var index=1;index<=4;index++){var rgba=Color(index,13);var at=148+index*4;bytes[at]=rgba[2];bytes[at+1]=rgba[1];bytes[at+2]=rgba[0];bytes[at+3]=rgba[3];}
        var offset=metadata;for(var level=0;level<mips.Length;level++){Put(bytes,20+level*4,(uint)offset);Put(bytes,84+level*4,(uint)mips[level].Length);mips[level].CopyTo(bytes,offset);offset+=mips[level].Length;}return bytes;
    }
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    private static async Task<string> Code(byte[] bytes,int mip=0,Blp2Limits? limits=null)
    {using var source=new AsyncInput(bytes);return (await Assert.ThrowsAsync<DiscoveryException>(()=>Blp2Decoder.DecodeAsync(source,mip,limits))).Code;}
    private sealed class AsyncInput:MemoryStream
    {
        private readonly byte[] bytes;public long TotalRead;public bool FailAtEof;public Action<long>? AfterRead;
        public AsyncInput(byte[] bytes):base(bytes){this.bytes=bytes;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous source IO");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Synchronous source IO");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();if(Position==Length&&FailAtEof)throw new IOException("Authored EOF failure");var count=Math.Min(17,Math.Min(target.Length,(int)(Length-Position)));bytes.AsMemory((int)Position,count).CopyTo(target);Position+=count;TotalRead+=count;AfterRead?.Invoke(Position);return ValueTask.FromResult(count);}
    }
}
