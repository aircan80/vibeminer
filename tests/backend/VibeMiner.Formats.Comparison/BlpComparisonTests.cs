using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using Xunit;
using Xunit.Abstractions;
using L = VibeMiner.Formats.Wow;
using N = VibeMiner.Formats.Images;

namespace BlpComparison;

/// <summary>#199: the layer 5 Blp2Decoder must give the same result as the legacy Formats.Wow decoder on every base fixture, under
/// every limit set and call (every mip level, ReadMipsAsync, DecodeLargestWithinAsync), and on every truncation and header-byte
/// mutation of it. A result is the full image (RGBA bytes and every metadata field) or the error: the legacy DiscoveryException
/// and the new BlpFormatException must carry the same code and message; any other exception the same type and message.</summary>
// No intentional differences: the port changes only the namespace and the exception type, so nothing is skipped.
public sealed class BlpComparisonTests(ITestOutputHelper output)
{
    public static TheoryData<string> Names()
    {var data=new TheoryData<string>();foreach(var fixture in BlpFixtures.All)data.Add(fixture.Name);return data;}

    [Fact]
    public void Corpus_has_distinct_fixtures_and_the_same_revision()
    {
        Assert.True(BlpFixtures.All.Length>=60);
        Assert.Equal(BlpFixtures.All.Length,BlpFixtures.All.Select(f=>f.Name).Distinct().Count());
        Assert.Equal(BlpFixtures.All.Length,BlpFixtures.All.Select(f=>Convert.ToHexString(f.Bytes)).Distinct().Count());
        Assert.Equal("blp2-rgba8-v2",N.Blp2Decoder.Revision);Assert.Equal(L.Blp2Decoder.Revision,N.Blp2Decoder.Revision);
    }

    [Fact]
    public void Public_profile_and_mip_flag_predicates_agree_on_every_byte()
    {
        for(var flags=-1;flags<=256;flags++)
        {Assert.Equal(L.Blp2Decoder.IsMipFlags(flags),N.Blp2Decoder.IsMipFlags(flags));Assert.Equal(L.Blp2Decoder.AllowsMipLevels(flags),N.Blp2Decoder.AllowsMipLevels(flags));}
        foreach(var encoding in Enum.GetValues<L.Blp2Encoding>())
            for(var depth=-1;depth<=256;depth++)for(var alpha=-1;alpha<=256;alpha++)
                Assert.Equal(L.Blp2Decoder.IsSupported(encoding,depth,alpha),N.Blp2Decoder.IsSupported((N.Blp2Encoding)(int)encoding,depth,alpha));
        Assert.Equal(Enum.GetNames<L.Blp2Encoding>(),Enum.GetNames<N.Blp2Encoding>());Assert.Equal(Enum.GetNames<L.Blp2MipStorage>(),Enum.GetNames<N.Blp2MipStorage>());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_call_under_every_limit_set_gives_identical_results(string name)
    {
        var fixture=BlpFixtures.All.Single(f=>f.Name==name);var outcomes=new SortedDictionary<string,int>();
        foreach(var limits in Limits)foreach(var call in Calls)
        {var result=await Same(fixture.Bytes,call,limits);var key=result.StartsWith("ok",StringComparison.Ordinal)?"ok":result[..result.IndexOf(" msg=",StringComparison.Ordinal)];outcomes[key]=outcomes.GetValueOrDefault(key)+1;}
        output.WriteLine(string.Join("\n",outcomes.Select(o=>o.Key+": "+o.Value)));
        var outcome=await Same(fixture.Bytes,new Call(CallKind.Decode,fixture.Mip),null);
        Assert.Equal(fixture.Expect,outcome.StartsWith("ok",StringComparison.Ordinal)?"ok":Code(outcome));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_truncation_gives_identical_results(string name)
    {
        var fixture=BlpFixtures.All.Single(f=>f.Name==name);
        for(var length=0;length<fixture.Bytes.Length;length++)
            foreach(var call in ShortCalls(fixture.Mip))await Same(fixture.Bytes[..length],call,null);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_header_byte_mutation_gives_identical_results(string name)
    {
        var fixture=BlpFixtures.All.Single(f=>f.Name==name);
        for(var at=0;at<Math.Min(148,fixture.Bytes.Length);at++)
            foreach(var value in new[]{0,1,0xFF,fixture.Bytes[at]^0x01,fixture.Bytes[at]^0x10,fixture.Bytes[at]^0x80}.Distinct())
            {
                if(value==fixture.Bytes[at])continue;var changed=fixture.Bytes.ToArray();changed[at]=(byte)value;
                foreach(var call in ShortCalls(fixture.Mip))await Same(changed,call,null);
            }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Every_palette_and_payload_byte_flip_gives_identical_pixels(string name)
    {
        var fixture=BlpFixtures.All.Single(f=>f.Name==name);
        // The 128x32 compact fixture's payload is 5 KiB of one repeated block; its first 512 payload bytes cover every level kind.
        for(var at=148;at<Math.Min(fixture.Bytes.Length,1172+512);at++)
        {var changed=fixture.Bytes.ToArray();changed[at]^=0x5A;await Same(changed,new Call(CallKind.Decode,fixture.Mip),null);}
    }

    [Fact]
    public async Task Cancellation_and_argument_refusals_are_identical()
    {
        var bytes=BlpFixtures.All[0].Bytes;using var canceled=new CancellationTokenSource();canceled.Cancel();
        Assert.Equal(await Run(()=>L.Blp2Decoder.DecodeAsync(new Input(bytes),token:canceled.Token)),await Run(()=>N.Blp2Decoder.DecodeAsync(new Input(bytes),token:canceled.Token)));
        Assert.Equal(await Run(()=>L.Blp2Decoder.ReadMipsAsync(new Input(bytes),token:canceled.Token)),await Run(()=>N.Blp2Decoder.ReadMipsAsync(new Input(bytes),token:canceled.Token)));
        Assert.Equal(await Run(()=>L.Blp2Decoder.DecodeAsync(null!)),await Run(()=>N.Blp2Decoder.DecodeAsync(null!)));
        Assert.Equal(await Run(()=>L.Blp2Decoder.DecodeAsync(new Input(bytes){Seekable=false})),await Run(()=>N.Blp2Decoder.DecodeAsync(new Input(bytes){Seekable=false})));
        var eof=await Run(()=>L.Blp2Decoder.DecodeAsync(new Input(bytes){FailAtEof=true}));
        Assert.StartsWith("System.IO.IOException",eof);Assert.Equal(eof,await Run(()=>N.Blp2Decoder.DecodeAsync(new Input(bytes){FailAtEof=true})));
    }

    private enum CallKind{Decode,ReadMips,Largest}
    private sealed record Call(CallKind Kind,int Arg);
    private sealed record LimitSpec(long MaxSourceBytes=64L<<20,int MaxDimension=16_384,long MaxPixels=16_777_216,long MaxMipBytes=64L<<20,long MaxRgbaBytes=64L<<20)
    {
        public L.Blp2Limits Legacy()=>new(MaxSourceBytes,MaxDimension,MaxPixels,MaxMipBytes,MaxRgbaBytes);
        public N.Blp2Limits New()=>new(MaxSourceBytes,MaxDimension,MaxPixels,MaxMipBytes,MaxRgbaBytes);
    }
    // null is the decoders' own default; the rest are the bounds the decoder tests use, plus an invalid limit set.
    private static readonly LimitSpec?[] Limits=[null,new(),new(MaxSourceBytes:148),new(MaxSourceBytes:1172),new(MaxDimension:1),new(MaxDimension:4),new(MaxDimension:int.MaxValue),
        new(MaxPixels:3),new(MaxPixels:4),new(MaxMipBytes:3),new(MaxMipBytes:16),new(MaxRgbaBytes:4),new(MaxRgbaBytes:16),new(MaxPixels:4,MaxMipBytes:4,MaxRgbaBytes:16),new(MaxRgbaBytes:3)];
    private static readonly Call[] Calls=[new(CallKind.ReadMips,0),..Enumerable.Range(-1,18).Select(mip=>new Call(CallKind.Decode,mip)),
        ..new[]{0,1,2,4,8,16,1024}.Select(side=>new Call(CallKind.Largest,side))];
    private static Call[] ShortCalls(int mip)=>[new(CallKind.ReadMips,0),new(CallKind.Decode,0),new(CallKind.Decode,1),new(CallKind.Decode,mip),new(CallKind.Largest,2),new(CallKind.Largest,8)];

    private static async Task<string> Same(byte[] bytes,Call call,LimitSpec? limits)
    {
        var legacy=await Legacy(bytes,call,limits);var current=await New(bytes,call,limits);
        Assert.True(legacy==current,$"{call} {limits}:\nlegacy {legacy}\nnew    {current}");return current;
    }
    private static Task<string> Legacy(byte[] bytes,Call call,LimitSpec? spec)
    {
        var limits=spec?.Legacy();
        return call.Kind switch
        {
            CallKind.ReadMips=>Run(async()=>Canon.Mips((await L.Blp2Decoder.ReadMipsAsync(new Input(bytes),limits)).Select(Canon.Mip))),
            CallKind.Decode=>Run(async()=>Canon.Image(await L.Blp2Decoder.DecodeAsync(new Input(bytes),call.Arg,limits))),
            _=>Run(async()=>Canon.Image(await L.Blp2Decoder.DecodeLargestWithinAsync(new Input(bytes),call.Arg,limits)))
        };
    }
    private static Task<string> New(byte[] bytes,Call call,LimitSpec? spec)
    {
        var limits=spec?.New();
        return call.Kind switch
        {
            CallKind.ReadMips=>Run(async()=>Canon.Mips((await N.Blp2Decoder.ReadMipsAsync(new Input(bytes),limits)).Select(Canon.Mip))),
            CallKind.Decode=>Run(async()=>Canon.Image(await N.Blp2Decoder.DecodeAsync(new Input(bytes),call.Arg,limits))),
            _=>Run(async()=>Canon.Image(await N.Blp2Decoder.DecodeLargestWithinAsync(new Input(bytes),call.Arg,limits)))
        };
    }
    private static async Task<string> Run<T>(Func<Task<T>> action)
    {
        try{var result=await action();return result as string??"ok";}
        catch(DiscoveryException e){return "code="+e.Code+" msg="+JsonSerializer.Serialize(e.Message);}
        catch(N.BlpFormatException e){return "code="+e.Code+" msg="+JsonSerializer.Serialize(e.Message);}
        catch(Exception e){return e.GetType().FullName+" msg="+JsonSerializer.Serialize(e.Message);}
    }
    private static string Code(string outcome)=>outcome.StartsWith("code=",StringComparison.Ordinal)?outcome[5..outcome.IndexOf(' ')]:outcome;

    private static class Canon
    {
        public static string Mips(IEnumerable<string> mips)=>"ok mips=["+string.Join(",",mips)+"]";
        public static string Mip(L.Blp2Mip m)=>$"{m.Level}:{m.Width}x{m.Height}@{m.Offset}+{m.ByteLength}/{m.Storage}";
        public static string Mip(N.Blp2Mip m)=>$"{m.Level}:{m.Width}x{m.Height}@{m.Offset}+{m.ByteLength}/{m.Storage}";
        public static string Image(L.Blp2Image i)=>Image(i.Width,i.Height,i.RowStride,i.Rgba,i.Encoding.ToString(),i.AlphaDepth,i.AlphaEncoding,i.MipLevel,i.BaseWidth,i.BaseHeight,i.MipFlags,i.Mips.Select(Mip));
        public static string Image(N.Blp2Image i)=>Image(i.Width,i.Height,i.RowStride,i.Rgba,i.Encoding.ToString(),i.AlphaDepth,i.AlphaEncoding,i.MipLevel,i.BaseWidth,i.BaseHeight,i.MipFlags,i.Mips.Select(Mip));
        private static string Image(int width,int height,int stride,byte[] rgba,string encoding,byte depth,byte alphaEncoding,int level,int baseWidth,int baseHeight,int flags,IEnumerable<string> mips)
        {
            var text=new StringBuilder("ok ");
            text.Append($"{width}x{height} stride={stride} enc={encoding} depth={depth} alphaEnc={alphaEncoding} mip={level} base={baseWidth}x{baseHeight} flags={flags} ");
            text.Append(Mips(mips)[3..]).Append(" rgba=").Append(Convert.ToHexString(rgba));return text.ToString();
        }
    }

    /// <summary>Async-only seekable input with short reads, like the decoder tests' AsyncInput.</summary>
    private sealed class Input:MemoryStream
    {
        private readonly byte[] bytes;public Input(byte[] bytes):base(bytes){this.bytes=bytes;}
        public bool FailAtEof;public bool Seekable=true;
        public override bool CanSeek=>Seekable&&base.CanSeek;
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous source IO");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Synchronous source IO");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();if(Position==Length&&FailAtEof)throw new IOException("Authored EOF failure");
            var count=Math.Min(17,Math.Min(target.Length,(int)(Length-Position)));bytes.AsMemory((int)Position,count).CopyTo(target);Position+=count;return ValueTask.FromResult(count);
        }
    }
}

/// <summary>A synthetic base fixture: <see cref="Expect"/> is "ok" or the error code both decoders give for mip <see cref="Mip"/>
/// with default limits.</summary>
internal sealed record BlpFixture(string Name,byte[] Bytes,string Expect,int Mip=0)
{
    public override string ToString()=>Name;
}

/// <summary>Every input the BLP2 decoder tests build, made with copies of their builders. Authored values only; no game content.</summary>
internal static class BlpFixtures
{
    public static readonly BlpFixture[] All=Build();

    private static BlpFixture[] Build()
    {
        var list=new List<BlpFixture>();
        // Palette alpha depths 0/1/4/8, and the 2x2 orientation fixture.
        byte[] indices=[1,2,3,4,1,2,3,4,1];
        foreach(var depth in new byte[]{0,1,4,8})
        {byte[] alpha=depth switch{0=>[],1=>[0xA5,0x01],4=>[0x10,0x32,0x54,0x76,0x08],_=>[0,1,17,34,68,128,170,254,255]};list.Add(new("palette-depth-"+depth,Make(1,depth,0,9,1,[[..indices,..alpha]]),"ok"));}
        list.Add(new("palette-2x2-depth8",Make(1,8,0,2,2,[[1,2,3,4,0,128,255,17]]),"ok"));
        // BC1 four-colour and endpoint expansion, three-colour with and without alpha, equal endpoints.
        list.Add(new("bc1-four-colour",Make(2,0,0,4,4,[Block(0xF800,0x001F,(uint)Selectors(Enumerable.Range(0,16).Select(i=>i%4),2))]),"ok"));
        list.Add(new("bc1-endpoint-expansion",Make(2,0,0,1,1,[Block(0x8C71,0,0)]),"ok"));
        foreach(var depth in new byte[]{0,1})
        {
            list.Add(new("bc1-three-colour-depth"+depth,Make(2,depth,0,4,1,[Block(0,0xFFFF,0xE4)]),"ok"));
            list.Add(new("bc1-equal-endpoints-depth"+depth,Make(2,depth,0,1,1,[Block(0xF800,0xF800,3)]),"ok"));
        }
        // BC2 explicit alpha with reversed endpoints.
        foreach(var depth in new byte[]{4,8})
        {
            var alpha=new byte[8];for(var i=0;i<8;i++)alpha[i]=(byte)((i*2)|((i*2+1)<<4));
            list.Add(new("bc2-depth"+depth,Make(2,depth,1,4,4,[[..alpha,..Block(0,0xFFFF,(uint)Selectors(Enumerable.Range(0,16).Select(i=>i%2+2),2))]]),"ok"));
        }
        // BC3 both alpha branches and the equal-endpoint case.
        foreach(var (a0,a1) in new[]{((byte)210,(byte)0),((byte)10,(byte)20),((byte)50,(byte)50)})
        {
            var alpha=new byte[8];alpha[0]=a0;alpha[1]=a1;var selectors=Selectors(Enumerable.Range(0,16).Select(i=>i%8),3);
            for(var i=0;i<6;i++)alpha[2+i]=(byte)(selectors>>(8*i));
            list.Add(new($"bc3-alpha-{a0}-{a1}",Make(2,8,7,4,4,[[..alpha,..Block(0,0xFFFF,uint.MaxValue)]]),"ok"));
        }
        // Partial edge blocks.
        foreach(var (w,h) in new[]{(5,3),(3,5)})list.Add(new($"bc1-edge-{w}x{h}",Make(2,0,0,w,h,[[..Block(0xF800,0,0),..Block(0x07E0,0,0)]]),"ok"));
        // Raw BGRA.
        list.Add(new("bgra-1x2",Make(3,8,0,1,2,[[20,10,200,128,240,60,50,0]]),"ok"));
        // Selected mip with its own limits.
        var twoPalette=Make(1,0,0,4,4,[Enumerable.Repeat((byte)1,16).ToArray(),[2,2,2,2]]);
        list.Add(new("palette-two-mips",twoPalette,"ok",1));
        // Mip flags 0x00/0x01/0x10/0x11 and refused neighbours, on a two-level BC1 and a one-level BC3.
        var two=Make(2,1,0,4,4,[Block(0xF800,0,0),Block(0x07E0,0,0)]);
        foreach(var flags in new[]{0x00,0x01,0x02,0x10,0x11,0x12,0x13,0x20,0x21,0x30,0xFF})
        {var changed=two.ToArray();changed[11]=(byte)flags;list.Add(new($"bc1-two-mips-flags-{flags:x2}",changed,flags is 0x01 or 0x11?"ok":IsFlags(flags)?"BLP_INVALID":"BLP_UNSUPPORTED",1));}
        var twoBadCount=two.ToArray();twoBadCount[11]=0x11;Put(twoBadCount,84,7);list.Add(new("bc1-two-mips-short-count",twoBadCount,"BLP_INVALID"));
        var single=Make(2,8,7,4,4,[[255,255,0,0,0,0,0,0,..Block(0x07E0,0x07E0,0)]]);
        foreach(var flags in new[]{0x00,0x01,0x10,0x11}){var changed=single.ToArray();changed[11]=(byte)flags;list.Add(new($"bc3-single-flags-{flags:x2}",changed,"ok"));}
        // D100 observed profiles and their supported counterparts.
        foreach(var (encoding,depth,alphaEncoding,sameDepth,sameAlpha) in new[]{((byte)1,(byte)0,(byte)8,(byte)0,(byte)0),((byte)2,(byte)72,(byte)7,(byte)8,(byte)7),((byte)3,(byte)1,(byte)8,(byte)8,(byte)0)})
        {
            byte[] payload=encoding switch{1=>[1,2,3,4],2=>[0x10,0x80,0x24,0x92,0x49,0x24,0x92,0x49,..Block(0xF800,0x001F,0x1B1B1B1B)],_=>[20,10,200,255,240,60,50,0,1,2,3,255,9,8,7,0]};
            var (w,h)=encoding==2?(4,4):(2,2);
            list.Add(new($"profile-{encoding}-{depth}-{alphaEncoding}",Make(encoding,depth,alphaEncoding,w,h,[payload]),"ok"));
            list.Add(new($"profile-{encoding}-{sameDepth}-{sameAlpha}-reference",Make(encoding,sameDepth,sameAlpha,w,h,[payload]),"ok"));
        }
        // Unobserved neighbouring profiles.
        foreach(var (encoding,depth,alphaEncoding) in new[]{(1,1,8),(1,8,8),(1,0,7),(2,71,7),(2,72,1),(2,0,7),(2,8,8),(3,0,0),(3,0,8),(3,8,8),(3,1,0)})
            list.Add(new($"refused-profile-{encoding}-{depth}-{alphaEncoding}",Make((byte)encoding,(byte)depth,(byte)alphaEncoding,1,1,[new byte[encoding==2?16:encoding==3?4:2]]),"BLP_UNSUPPORTED"));
        // Rectangular clamping.
        list.Add(new("bc1-1x8-two-mips",Make(2,0,0,1,8,[[..Block(0xF800,0,0),..Block(0xF800,0,0)],Block(0x07E0,0,0)]),"ok",1));
        // Truncation fixture (every prefix is covered by the truncation test).
        list.Add(new("palette-3x1-depth4",Make(1,4,0,3,1,[[1,2,3,0x21,0x0F]]),"ok"));
        // Descriptor structure, length, bounds and overlap.
        var descriptor=Make(1,0,0,4,4,[Enumerable.Repeat((byte)1,16).ToArray(),new byte[4]]);
        foreach(var (name,edits,expect,mip) in new (string,(int At,uint Value)[],string,int)[]
        {
            ("unpaired-offset",[(24,0)],"BLP_INVALID",0),("gap-in-prefix",[(80,1172),(144,1)],"BLP_INVALID",0),
            ("bad-length-0",[(84,15)],"BLP_INVALID",0),("bad-length-1",[(88,5)],"ok",0),("out-of-source-1",[(24,1190)],"ok",0),
            ("offset-max-1",[(24,uint.MaxValue)],"ok",0),("overlaps-metadata",[(20,148)],"BLP_INVALID",0),("overlap",[(24,1180)],"BLP_INVALID",0),
        })
        {var changed=descriptor.ToArray();foreach(var (at,value) in edits)Put(changed,at,value);list.Add(new("descriptor-"+name,changed,expect,mip));}
        var noFlag=descriptor.ToArray();noFlag[11]=0;list.Add(new("descriptor-no-mip-flag",noFlag,"BLP_INVALID"));
        list.Add(new("descriptor-beyond-terminal",Make(1,0,0,1,1,[[1],[2]]),"BLP_INVALID"));
        // Compact tail levels: the 128x32 BC2 and a one-byte-short compact level.
        var alphaFull=Enumerable.Repeat((byte)0xFF,8).ToArray();
        byte[] Blocks(int count,ushort color)=>Enumerable.Range(0,count).SelectMany(_=>(byte[])[..alphaFull,..Block(color,color,0)]).ToArray();
        var compact=Make(2,8,1,128,32,[Blocks(256,0xF800),Blocks(64,0x07E0),Blocks(16,0x001F),Blocks(4,0xFFE0),Blocks(1,0x07FF),Blocks(1,0xF81F),Blocks(1,0xFFFF),Blocks(1,0x0000)]);
        list.Add(new("bc2-128x32-compact-tail",compact,"BLP_UNSUPPORTED",4));
        var shorter=compact.ToArray();Put(shorter,84+4*4,15);list.Add(new("bc2-128x32-unrecognized-tail",shorter,"BLP_INVALID",4));
        foreach(var (encoding,depth,alphaEncoding,w,h,compactLength,exact) in new[]{(2,0,0,16,2,16,32),(2,1,0,8,1,8,16),(2,8,7,16,1,16,64),(2,4,1,1,8,16,32),(2,8,7,2,8,16,32)})
        {
            list.Add(new($"compact-{encoding}-{depth}-{alphaEncoding}-{w}x{h}",Make((byte)encoding,(byte)depth,(byte)alphaEncoding,w,h,[new byte[compactLength]]),"BLP_UNSUPPORTED"));
            list.Add(new($"exact-{encoding}-{depth}-{alphaEncoding}-{w}x{h}",Make((byte)encoding,(byte)depth,(byte)alphaEncoding,w,h,[new byte[exact]]),"ok"));
        }
        foreach(var (encoding,depth,alphaEncoding,w,h,length) in new[]{(2,0,0,4,4,4),(2,8,7,2,2,4),(2,0,0,2,1,1),(1,8,0,4,1,4),(3,8,0,4,1,4)})
            list.Add(new($"unrecognized-{encoding}-{depth}-{alphaEncoding}-{w}x{h}-{length}",Make((byte)encoding,(byte)depth,(byte)alphaEncoding,w,h,[new byte[length]]),"BLP_INVALID"));
        // Allocation bounds run through the limit sets on every fixture; the decoder tests' 2x2 palette is profile-1-0-0-reference. Unsigned dimensions.
        var wide=Make(1,8,0,1,1,[[1,255]]);Put(wide,12,uint.MaxValue);list.Add(new("dimension-uint-max",wide,"BLP_LIMIT"));
        var huge=wide.ToArray();Put(huge,12,int.MaxValue);Put(huge,16,int.MaxValue);list.Add(new("dimension-int-max-square",huge,"BLP_LIMIT"));
        var zero=Make(1,0,0,1,1,[[1]]);Put(zero,12,0);list.Add(new("dimension-zero",zero,"BLP_INVALID"));
        // Unsupported container and encoding bytes.
        foreach(var (at,value) in new[]{(8,4),(9,2),(10,9),(11,2),(4,0)})
        {var changed=Make(2,1,0,1,1,[Block(0xF800,0,0)]);changed[at]=(byte)value;list.Add(new($"unsupported-byte{at}-{value}",changed,"BLP_UNSUPPORTED"));}
        var blp1=Make(1,0,0,1,1,[[1]]);blp1[3]=(byte)'1';list.Add(new("unsupported-blp1",blp1,"BLP_UNSUPPORTED"));
        list.Add(new("unsupported-bgra-depth0",Make(3,0,0,1,1,[[1,2,3,4]]),"BLP_UNSUPPORTED"));
        // Unused palette padding and a trailing gap.
        var plain=Make(2,0,0,1,1,[Block(0xF800,0,0)]);var padded=new byte[plain.Length+1024+3];plain.AsSpan(0,148).CopyTo(padded);padded.AsSpan(148,1024).Fill(0xA5);
        plain.AsSpan(148).CopyTo(padded.AsSpan(1172));Put(padded,20,1172);padded[^1]=0x77;list.Add(new("bc1-padded-gap",padded,"ok"));
        // DecodeLargestWithinAsync: three declared levels, and only the base level.
        list.Add(new("bgra-4x2-three-mips",Make(3,8,0,4,2,[Enumerable.Repeat((byte)5,32).ToArray(),Enumerable.Repeat((byte)7,8).ToArray(),[9,9,9,9]]),"ok",2));
        list.Add(new("bgra-4x2-base-only",Make(3,8,0,4,2,[new byte[32]]),"ok"));
        return list.ToArray();
    }

    private static bool IsFlags(int flags)=>flags is 0x00 or 0x01 or 0x10 or 0x11;
    private static byte[] Color(int index,byte alpha)=>index switch{1=>[200,10,20,alpha],2=>[30,220,40,alpha],3=>[50,60,240,alpha],_=>[250,230,210,alpha]};
    private static ulong Selectors(IEnumerable<int> selectors,int bits){ulong value=0;var index=0;foreach(var selector in selectors)value|=(ulong)selector<<(index++*bits);return value;}
    private static byte[] Block(ushort c0,ushort c1,uint selectors){var bytes=new byte[8];BinaryPrimitives.WriteUInt16LittleEndian(bytes,c0);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2),c1);Put(bytes,4,selectors);return bytes;}
    private static byte[] Make(byte encoding,byte depth,byte alphaEncoding,int width,int height,byte[][] mips)
    {
        var metadata=encoding==1?1172:148;var bytes=new byte[metadata+mips.Sum(mip=>mip.Length)];"BLP2"u8.CopyTo(bytes);Put(bytes,4,1);bytes[8]=encoding;bytes[9]=depth;bytes[10]=alphaEncoding;bytes[11]=(byte)(mips.Length>1?1:0);Put(bytes,12,(uint)width);Put(bytes,16,(uint)height);
        if(encoding==1)for(var index=1;index<=4;index++){var rgba=Color(index,13);var at=148+index*4;bytes[at]=rgba[2];bytes[at+1]=rgba[1];bytes[at+2]=rgba[0];bytes[at+3]=rgba[3];}
        var offset=metadata;for(var level=0;level<mips.Length;level++){Put(bytes,20+level*4,(uint)offset);Put(bytes,84+level*4,(uint)mips[level].Length);mips[level].CopyTo(bytes,offset);offset+=mips[level].Length;}return bytes;
    }
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
}
