using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class IconArtifactsTests
{
    [Fact]
    public async Task Builds_source_bound_PNG_and_reopens_after_restart_with_identical_straight_alpha_pixels()
    {
        var directory=Path.Combine(Path.GetTempPath(),"vm-icons-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        IconPngArtifact saved;
        try
        {
            await using(var objects=new VerifiedObjectStore(directory))
            {
                var source=await PublishSource(objects,Blp());var assets=new IconArtifacts(objects);saved=await assets.BuildAsync(source);
                Assert.Equal(IconArtifacts.ComputeKey(source),saved.RequestKey);Assert.Equal(new byte[]{200,10,20,0,30,220,40,128,50,60,240,255,250,230,210,17},await Pixels(assets,saved));
                Assert.Equal(saved,await assets.BuildAsync(source));Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory,"staging-v1")));
                Assert.Equal(2,Directory.EnumerateFiles(Path.Combine(directory,"objects-v1"),"*",SearchOption.AllDirectories).Count(path=>Path.GetFileName(path)!=".owner"));
            }
            await using(var objects=new VerifiedObjectStore(directory))
            {var assets=new IconArtifacts(objects);await using var image=await assets.OpenVerifiedAsync(saved);var bytes=await ReadAll(image);Assert.Equal(saved.Sha256,Hash(bytes));Assert.Equal(saved.ByteLength,bytes.Length);}
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public async Task Stable_request_key_excludes_dates_routes_but_binds_immutable_source_and_selected_mip()
    {
        await using var h=new Harness();var source=await PublishSource(h.Objects,Blp(mips:true));
        var changed=source with{VerifiedAt="2030-01-01T00:00:00.0000000Z",RetrievedAt="2029-01-01T00:00:00.0000000Z",SourceUrl="https://us.cdn.blizzard.com/another-retained-official-route"};
        Assert.Equal(IconArtifacts.ComputeKey(source),IconArtifacts.ComputeKey(changed));
        Assert.NotEqual(IconArtifacts.ComputeKey(source),IconArtifacts.ComputeKey(source with{EncodingKey=new('a',32)}));
        Assert.NotEqual(IconArtifacts.ComputeKey(source),IconArtifacts.ComputeKey(source with{Sha256=new('b',64)}));
        var first=await h.Assets.BuildAsync(source);var next=await h.Assets.BuildAsync(source,1);
        Assert.NotEqual(first.RequestKey,next.RequestKey);Assert.NotEqual(first.ArtifactId,next.ArtifactId);Assert.Equal(new byte[]{30,220,40,255},await Pixels(h.Assets,next));
    }

    [Fact]
    public async Task Full_source_flags_do_not_bypass_encoded_SHA_BLTE_EKey_or_CKey_verification()
    {
        await using var h=new Harness();var source=await PublishSource(h.Objects,Blp());
        Assert.Equal("BLTE_INTEGRITY",await Code(()=>h.Assets.BuildAsync(source with{ContentKey=new('0',32)})));
        Assert.Equal("BLTE_INTEGRITY",await Code(()=>h.Assets.BuildAsync(source with{EncodingKey=new('0',32)})));
        var path=ObjectPath(h.Directory,source.Sha256);var bytes=await File.ReadAllBytesAsync(path);bytes[^1]^=1;await File.WriteAllBytesAsync(path,bytes);
        Assert.Equal("LOCAL_OBJECT_CORRUPT",await Code(()=>h.Assets.BuildAsync(source)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Directory,"staging-v1")));
    }

    [Fact]
    public async Task Projection_or_encrypted_source_is_not_relabelled_as_a_complete_icon()
    {
        await using var h=new Harness();var source=await PublishSource(h.Objects,Blp());
        var projection=new ContentProjection("blte-clear-chunks-v1",new('1',64),[],[new("0","148",0,"0000000000000001")]);
        Assert.Equal("ASSET_SOURCE_UNSUPPORTED",await Code(()=>h.Assets.BuildAsync(source with{Projection=projection})));
        Assert.Equal("ASSET_SOURCE_UNSUPPORTED",await Code(()=>h.Assets.BuildAsync(source with{VerifierRevision="content-blte-projection-v1"})));
        var encrypted=await PublishBytes(h.Objects,[.."BLTE"u8,0,0,0,0,(byte)'E',8,1,2,3,4,5,6,7,8,4,0,0,0,0,(byte)'S',99]);
        var fake=source with{Sha256=encrypted.Sha,EncodedBytes=encrypted.Length.ToString(),EncodingKey=Md5(encrypted.Bytes)};
        Assert.Equal("BLTE_ENCRYPTED",await Code(()=>h.Assets.BuildAsync(fake)));Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Directory,"staging-v1")));
    }

    [Theory]
    [InlineData("leading-zero")][InlineData("uppercase")][InlineData("source-bytes")][InlineData("decoded-bytes")][InlineData("mip")]
    public async Task Canonical_source_and_budget_admission_precede_staging(string kind)
    {
        await using var h=new Harness();var source=await PublishSource(h.Objects,Blp());
        source=kind switch{"leading-zero"=>source with{EncodedBytes="0"+source.EncodedBytes},"uppercase"=>source with{ContentKey=source.ContentKey.ToUpperInvariant()},"source-bytes"=>source with{EncodedBytes=(IconArtifacts.MaxSourceBytes+1).ToString()},"decoded-bytes"=>source with{DecodedBytes=(IconArtifacts.MaxSourceBytes+1).ToString()},_=>source};
        var code=await Code(()=>h.Assets.BuildAsync(source,kind=="mip"?16:0));Assert.Equal(kind.EndsWith("bytes",StringComparison.Ordinal)?"ASSET_LIMIT":"ASSET_INVALID",code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Directory,"staging-v1")));
    }

    [Fact]
    public async Task Cancellation_and_progress_failure_clean_staging_and_release_bounded_build_admission()
    {
        await using var h=new Harness();var source=await PublishSource(h.Objects,Blp());
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var building=h.Assets.BuildAsync(source,0,async stage=>{if(stage=="Verifying icon source"){entered.TrySetResult();await release.Task;}});
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("ASSET_BUSY",await Code(()=>h.Assets.BuildAsync(source)));release.SetResult();await building;
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>h.Assets.BuildAsync(source,0,stage=>{if(stage=="Encoding verified icon PNG")cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        var expected=new IOException("authored progress failure");Assert.Same(expected,await Assert.ThrowsAsync<IOException>(()=>h.Assets.BuildAsync(source,0,stage=>stage=="Publishing verified icon PNG"?Task.FromException(expected):Task.CompletedTask)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Directory,"staging-v1")));
        await h.Assets.BuildAsync(source);
    }

    [Fact]
    public async Task Read_leases_are_bounded_and_disposal_releases_capacity_without_writable_paths()
    {
        await using var h=new Harness();var artifact=await h.Assets.BuildAsync(await PublishSource(h.Objects,Blp()));var leases=new List<Stream>();
        try
        {
            for(var index=0;index<4;index++)leases.Add(await h.Assets.OpenVerifiedAsync(artifact));
            Assert.Equal("ASSET_BUSY",await Code(async()=>{await using var extra=await h.Assets.OpenVerifiedAsync(artifact);}));
            Assert.False(leases[0].CanWrite);Assert.Throws<NotSupportedException>(()=>leases[0].WriteByte(1));
            await leases[0].DisposeAsync();await using var replacement=await h.Assets.OpenVerifiedAsync(artifact);
        }
        finally {foreach(var lease in leases)await lease.DisposeAsync();}
        await using var again=await h.Assets.OpenVerifiedAsync(artifact);
    }

    [Fact]
    public async Task Descriptor_and_embedded_source_pixel_proof_substitution_are_rejected()
    {
        await using var h=new Harness();var artifact=await h.Assets.BuildAsync(await PublishSource(h.Objects,Blp()));
        Assert.Equal("ASSET_INVALID",await Code(async()=>{await using var ignored=await h.Assets.OpenVerifiedAsync(artifact with{Proof=artifact.Proof with{Width=1}});}));
        var source=artifact.Proof.Source with{ContentKey=new('a',32)};
        var proof=artifact.Proof with{Source=source,RequestKey=IconArtifacts.ComputeKey(ToReceipt(source))};
        var substituted=artifact with{RequestKey=proof.RequestKey,ArtifactId=IconArtifacts.ProofId(proof),Proof=proof};
        Assert.Equal("ASSET_PNG_INTEGRITY",await Code(async()=>{await using var ignored=await h.Assets.OpenVerifiedAsync(substituted);}));
        var changed=await Encode(artifact.Proof,new byte[16]);var stored=await PublishBytes(h.Objects,changed);
        var wrongPixels=artifact with{Sha256=stored.Sha,ByteLength=stored.Length};
        Assert.Equal("ASSET_PNG_INTEGRITY",await Code(async()=>{await using var ignored=await h.Assets.OpenVerifiedAsync(wrongPixels);}));
    }

    [Theory]
    [InlineData("crc")][InlineData("truncated-zlib")][InlineData("trailing-zlib")][InlineData("dimensions")][InlineData("trailing-file")][InlineData("oversized-chunk")]
    public async Task Reopen_rejects_malformed_CAS_content_even_when_its_local_SHA_was_updated(string kind)
    {
        await using var h=new Harness();var artifact=await h.Assets.BuildAsync(await PublishSource(h.Objects,Blp()));
        await using var file=await h.Assets.OpenVerifiedAsync(artifact);var bytes=await ReadAll(file);await file.DisposeAsync();
        var chunks=Chunks(bytes);var idat=chunks.Single(chunk=>chunk.Type=="IDAT");
        var malformed=kind switch
        {
            "crc"=>Change(bytes,idat.Start+8),
            "truncated-zlib"=>Rewrite(chunks.Select(chunk=>chunk.Type=="IDAT"?(chunk.Type,chunk.Bytes[..^1]):(chunk.Type,chunk.Bytes))),
            "trailing-zlib"=>Rewrite(chunks.Select(chunk=>chunk.Type=="IDAT"?(chunk.Type,chunk.Bytes.Concat(new byte[]{0}).ToArray()):(chunk.Type,chunk.Bytes))),
            "dimensions"=>Rewrite(chunks.Select(chunk=>chunk.Type=="IHDR"?(chunk.Type,Change(chunk.Bytes,3)):(chunk.Type,chunk.Bytes))),
            "trailing-file"=>[..bytes,0],
            _=>ChangeWord(bytes,idat.Start,uint.MaxValue)
        };
        var stored=await PublishBytes(h.Objects,malformed);var corrupted=artifact with{Sha256=stored.Sha,ByteLength=stored.Length};
        var code=await Code(async()=>{await using var ignored=await h.Assets.OpenVerifiedAsync(corrupted);});Assert.StartsWith("ASSET_PNG_",code);
    }

    [Fact]
    public async Task PNG_encoder_streams_bounded_chunks_and_preserves_RGBA_rows_and_standard_CRC()
    {
        var rgba=new byte[512*512*4];uint state=12345;for(var index=0;index<rgba.Length;index++){state^=state<<13;state^=state>>17;state^=state<<5;rgba[index]=(byte)state;}
        using var target=new AsyncOutput();await PngRgbaEncoder.WriteAsync(target,512,512,rgba,"authored proof"u8.ToArray(),default);var bytes=target.ToArray();
        Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},bytes[..8]);Assert.Equal(new byte[]{0,0,0,0,73,69,78,68,174,66,96,130},bytes[^12..]);
        var chunks=Chunks(bytes);Assert.Equal("IHDR",chunks[0].Type);Assert.Equal("vmPR",chunks[1].Type);Assert.Equal("IEND",chunks[^1].Type);
        Assert.True(chunks.Count(chunk=>chunk.Type=="IDAT")>1);Assert.All(chunks.Where(chunk=>chunk.Type=="IDAT"),chunk=>Assert.InRange(chunk.Bytes.Length,1,65536));
        foreach(var chunk in chunks)Assert.Equal(Crc(Encoding.ASCII.GetBytes(chunk.Type).Concat(chunk.Bytes).ToArray()),BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(chunk.Start+8+chunk.Bytes.Length)));
        Assert.Equal(rgba,InflateRows(bytes,512,512));Assert.InRange(target.MaxWrite,1,65536);Assert.True(target.CanWrite);
    }

    [Fact]
    public async Task PNG_encoder_rejects_bad_geometry_and_observes_cancellation_without_owning_target()
    {
        using var target=new MemoryStream();await Assert.ThrowsAsync<DiscoveryException>(()=>PngRgbaEncoder.WriteAsync(target,1025,1,new byte[4100],new byte[]{1},default));Assert.Equal(0,target.Length);
        await Assert.ThrowsAsync<DiscoveryException>(()=>PngRgbaEncoder.WriteAsync(target,1,1,new byte[3],new byte[]{1},default));
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PngRgbaEncoder.WriteAsync(target,1,1,new byte[4],new byte[]{1},cancel.Token));Assert.True(target.CanWrite);
        using var during=new CancellationTokenSource();using var output=new AsyncOutput{AfterWrite=()=>during.Cancel()};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>PngRgbaEncoder.WriteAsync(output,1,1,new byte[4],new byte[]{1},during.Token));Assert.True(output.CanWrite);
    }

    private static async Task<byte[]> Pixels(IconArtifacts assets,IconPngArtifact artifact){await using var file=await assets.OpenVerifiedAsync(artifact);return InflateRows(await ReadAll(file),artifact.Proof.Width,artifact.Proof.Height);}
    private static byte[] InflateRows(byte[] png,int width,int height)
    {
        var compressed=Chunks(png).Where(chunk=>chunk.Type=="IDAT").SelectMany(chunk=>chunk.Bytes).ToArray();using var zlib=new ZLibStream(new MemoryStream(compressed),CompressionMode.Decompress);var result=new byte[width*height*4];
        for(var row=0;row<height;row++){Assert.Equal(0,zlib.ReadByte());zlib.ReadExactly(result.AsSpan(row*width*4,width*4));}Assert.Equal(-1,zlib.ReadByte());return result;
    }
    private static async Task<byte[]> Encode(IconPngProof proof,byte[] rgba){using var target=new MemoryStream();await PngRgbaEncoder.WriteAsync(target,proof.Width,proof.Height,rgba,IconArtifacts.ProofBytes(proof),default);return target.ToArray();}
    private static async Task<string> Code(Func<Task> action)=>(await Assert.ThrowsAsync<DiscoveryException>(action)).Code;
    private static async Task<byte[]> ReadAll(Stream stream){using var target=new MemoryStream();await stream.CopyToAsync(target);return target.ToArray();}
    private static ContentReceipt ToReceipt(IconSourceBinding source)=>new(source.ContentKey,source.EncodingKey,source.EncodedSha256,source.EncodedBytes.ToString(),source.DecodedBytes.ToString(),"https://us.cdn.blizzard.com/tpr/wow/data/x","2026-10-19T00:00:00Z","2026-10-19T00:00:00Z",source.VerifierRevision,new("loose","0",source.EncodedBytes.ToString(),null));
    private static async Task<ContentReceipt> PublishSource(VerifiedObjectStore objects,byte[] decoded)
    {
        byte[] encoded=[.."BLTE"u8,0,0,0,0,(byte)'N',..decoded];var stored=await PublishBytes(objects,encoded);
        return new(Md5(decoded),Md5(encoded),stored.Sha,encoded.Length.ToString(),decoded.Length.ToString(),"https://us.cdn.blizzard.com/tpr/wow/data/fixture","2026-10-19T00:00:00Z","2026-10-19T00:00:00Z","content-blte-v1",new("loose","0",encoded.Length.ToString(),null));
    }
    private static async Task<(string Sha,long Length,byte[] Bytes)> PublishBytes(VerifiedObjectStore objects,byte[] bytes)
    {string path;await using(var stage=await objects.CreateStagingFileAsync(default)){path=stage.Name;await stage.WriteAsync(bytes);}return(await objects.PublishFileAsync(path,bytes.Length,default),bytes.Length,bytes);}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Md5(byte[] bytes)=>Convert.ToHexStringLower(MD5.HashData(bytes));
    private static string ObjectPath(string directory,string sha)=>Path.Combine(directory,"objects-v1",sha[..2],sha[2..]);
    private static byte[] Blp(bool mips=false)
    {
        const int width=2;var depth=mips?0:8;var bytes=new byte[1172+(mips?5:8)];"BLP2"u8.CopyTo(bytes);Put(bytes,4,1);bytes[8]=1;bytes[9]=(byte)depth;bytes[11]=(byte)(mips?1:0);Put(bytes,12,(uint)width);Put(bytes,16,2);Put(bytes,20,1172);Put(bytes,84,(uint)(mips?4:8));
        byte[][] colors=[[200,10,20,13],[30,220,40,13],[50,60,240,13],[250,230,210,13]];
        for(var index=0;index<4;index++){var at=148+(index+1)*4;bytes[at]=colors[index][2];bytes[at+1]=colors[index][1];bytes[at+2]=colors[index][0];bytes[at+3]=13;bytes[1172+index]=(byte)(index+1);}
        if(mips){Put(bytes,24,1176);Put(bytes,88,1);bytes[1176]=2;}else{bytes[1176]=0;bytes[1177]=128;bytes[1178]=255;bytes[1179]=17;}return bytes;
    }
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    private static byte[] Change(byte[] input,int at){var bytes=input.ToArray();bytes[at]^=1;return bytes;}
    private static byte[] ChangeWord(byte[] input,int at,uint value){var bytes=input.ToArray();BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at),value);return bytes;}
    private static List<(string Type,byte[] Bytes,int Start)> Chunks(byte[] png)
    {var chunks=new List<(string,byte[],int)>();for(var at=8;at<png.Length;){var length=(int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));chunks.Add((Encoding.ASCII.GetString(png,at+4,4),png.AsSpan(at+8,length).ToArray(),at));at+=12+length;}return chunks;}
    private static byte[] Rewrite(IEnumerable<(string Type,byte[] Bytes)> chunks)
    {using var output=new MemoryStream();output.Write(new byte[]{137,80,78,71,13,10,26,10});foreach(var chunk in chunks){var word=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(word,(uint)chunk.Bytes.Length);output.Write(word);var type=Encoding.ASCII.GetBytes(chunk.Type);output.Write(type);output.Write(chunk.Bytes);BinaryPrimitives.WriteUInt32BigEndian(word,Crc(type.Concat(chunk.Bytes).ToArray()));output.Write(word);}return output.ToArray();}
    private static uint Crc(byte[] bytes){uint crc=uint.MaxValue;foreach(var value in bytes){crc^=value;for(var bit=0;bit<8;bit++)crc=(crc&1)==0?crc>>1:(crc>>1)^0xEDB88320u;}return ~crc;}
    private sealed class Harness:IAsyncDisposable
    {
        public string Directory{get;}=Path.Combine(Path.GetTempPath(),"vm-icons-"+Guid.NewGuid().ToString("N"));public VerifiedObjectStore Objects{get;}public IconArtifacts Assets{get;}
        public Harness(){System.IO.Directory.CreateDirectory(Directory);Objects=new(Directory);Assets=new(Objects);}
        public async ValueTask DisposeAsync(){await Objects.DisposeAsync();System.IO.Directory.Delete(Directory,true);}
    }
    private sealed class AsyncOutput:MemoryStream
    {
        public int MaxWrite;public Action? AfterWrite;
        public override void Write(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous output IO");public override void Write(ReadOnlySpan<byte> buffer)=>throw new InvalidOperationException("Synchronous output IO");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default){cancellationToken.ThrowIfCancellationRequested();MaxWrite=Math.Max(MaxWrite,buffer.Length);var bytes=buffer.ToArray();base.Write(bytes,0,bytes.Length);AfterWrite?.Invoke();return ValueTask.CompletedTask;}
    }
}
