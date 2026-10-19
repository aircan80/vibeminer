using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class BlteProjectionTests
{
    [Fact]
    public async Task Authenticates_interleaved_clear_spans_and_original_encrypted_ordinal_without_claiming_CKey()
    {
        var fixture = Make(true, new(Raw("abc"u8),3), new(Encrypted(),4), new(Zlib("tail"u8),4));
        using var encoded = new AsyncInput(fixture.Bytes); using var decoded = new AsyncOutput();
        var proof = await Decode(fixture, encoded, decoded);
        Assert.False(proof.ContentKeyVerified);
        Assert.Equal([new BlteReadableRange(0,3,Sha("abc"u8)),new(7,4,Sha("tail"u8))],proof.ReadableRanges);
        Assert.Equal(new BlteUnavailableRange(3,4,1,"0807060504030201"),Assert.Single(proof.UnavailableRanges));
        Assert.Equal(11,decoded.Length); Assert.True(encoded.CanRead); Assert.True(decoded.CanWrite);
        decoded.Position=0;
        using var guarded = new BlteProjectionStream(decoded,proof.UnavailableRanges,readableRanges:proof.ReadableRanges);
        var first=new byte[3]; Assert.Equal(3,await guarded.ReadAsync(first)); Assert.Equal("abc"u8.ToArray(),first);
        Assert.Equal("BLTE_RANGE_UNAVAILABLE",Assert.Throws<DiscoveryException>(()=>guarded.ReadByte()).Code);
        guarded.Seek(7,SeekOrigin.Begin);var tail=new byte[4];Assert.Equal(4,await guarded.ReadAsync(tail));Assert.Equal("tail"u8.ToArray(),tail);
        Assert.Equal(0,await guarded.ReadAsync(new byte[1]));
        using var replay=new AsyncInput(fixture.Bytes);var verified=await BlteDecoder.VerifyAvailableAsync(replay,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded);
        Assert.Equal(proof.ReadableRanges,verified.ReadableRanges);Assert.Equal(proof.UnavailableRanges,verified.UnavailableRanges);Assert.False(verified.ContentKeyVerified);
    }

    [Theory]
    [InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task All_clear_projection_still_requires_full_CKey_and_semantic_EKey(bool table,bool compressed)
    {
        var content="authored complete plaintext"u8.ToArray();var fixture=Make(table,new Block(compressed?Zlib(content):Raw(content),content.Length));
        fixture=fixture with{CKey=Md5(content)};using var output=new MemoryStream();using var input=new AsyncInput(fixture.Bytes);
        var proof=await Decode(fixture,input,output);Assert.True(proof.ContentKeyVerified);Assert.Empty(proof.UnavailableRanges);Assert.Equal(content,output.ToArray());
        Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{CKey=new string('0',32)}));
        Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{EKey=new string('0',32)}));
    }

    [Fact]
    public async Task Empty_clear_chunk_does_not_renumber_original_encrypted_chunk()
    {
        var fixture=Make(true,new Block(Raw([]),0),new(Encrypted(),2));using var output=new MemoryStream();using var input=new MemoryStream(fixture.Bytes);
        var proof=await Decode(fixture,input,output);Assert.Empty(proof.ReadableRanges);Assert.Equal(1,Assert.Single(proof.UnavailableRanges).ChunkOrdinal);Assert.Equal(2,output.Length);
    }

    [Fact]
    public async Task Strict_decoder_keeps_rejecting_encryption_and_zero_header_partial_is_unsupported()
    {
        var fixture=Make(true,new Block(Encrypted(),4));using var input=new MemoryStream(fixture.Bytes);using var output=new MemoryStream();
        Assert.Equal("BLTE_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>BlteDecoder.DecodeAsync(input,output,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded))).Code);
        Assert.Equal("BLTE_UNSUPPORTED",await ErrorCode(Make(false,new Block(Encrypted(),4))));
    }

    [Fact]
    public async Task Rejects_changed_header_clear_chunk_encrypted_ciphertext_and_late_corruption()
    {
        var fixture=Make(true,new Block(Raw("clear"u8),5),new(Encrypted(),3));
        Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{EKey=new string('0',32)}));
        var body=fixture.Bytes.ToArray();body[^1]^=1;using var input=new MemoryStream(body);using var output=new MemoryStream();
        Assert.Equal("BLTE_INTEGRITY",(await Assert.ThrowsAsync<DiscoveryException>(()=>Decode(fixture,input,output))).Code);
        Assert.Equal("clear"u8.ToArray(),output.ToArray()); // Provisional clear output never establishes a successful projection.
        body=fixture.Bytes.ToArray();body[12+2*24+1]^=1;Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{Bytes=body}));
    }

    [Fact]
    public async Task Every_encoded_truncation_trailing_data_and_independent_totals_fail()
    {
        var fixture=Make(true,new Block(Zlib("clear"u8),5),new(Encrypted(),3));
        for(var length=0;length<fixture.Bytes.Length;length++)Assert.Equal("BLTE_INVALID",await ErrorCode(fixture with{Bytes=fixture.Bytes[..length]}));
        // Keep independently pinned encodedLength, so appended bytes are caught by the EOF probe.
        using var input=new MemoryStream([..fixture.Bytes,0]);using var output=new MemoryStream();
        Assert.Equal("BLTE_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Decode(fixture,input,output))).Code);
        Assert.Equal("BLTE_INVALID",await ErrorCode(fixture with{Decoded=fixture.Decoded+1}));
        var bytes=fixture.Bytes.ToArray();BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12),0);
        Assert.Equal("BLTE_INVALID",await ErrorCode(fixture with{Bytes=bytes}));
    }

    [Fact]
    public async Task Truncated_encrypted_envelopes_never_become_unavailable_receipts()
    {
        var envelope=Encrypted();for(var size=1;size<envelope.Length;size++)
            Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block(envelope[..size],4))));
        Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block(envelope,0))));
    }

    [Theory]
    [InlineData(1,7)][InlineData(10,3)][InlineData(15,65)]
    public async Task Unsupported_key_width_IV_width_or_cipher_remain_unsupported(int position,byte value)
    {
        var bytes=Encrypted();bytes[position]=value;Assert.Equal("BLTE_UNSUPPORTED",await ErrorCode(Make(true,new Block(bytes,4))));
    }

    [Fact]
    public async Task Exact_zlib_and_raw_lengths_are_required_even_when_other_chunks_are_encrypted()
    {
        var block=Zlib("inflate"u8);for(var size=2;size<block.Length;size++)
            Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block(block[..size],7),new(Encrypted(),1))));
        Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block([..block,0],7),new(Encrypted(),1))));
        Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block(block,6),new(Encrypted(),1))));
        Assert.Equal("BLTE_INVALID",await ErrorCode(Make(true,new Block(Raw("raw"u8),4),new(Encrypted(),1))));
        Assert.Equal("BLTE_UNSUPPORTED",await ErrorCode(Make(true,new Block([(byte)'F'],1),new(Encrypted(),1))));
    }

    [Fact]
    public async Task Projection_limits_reject_before_large_chunk_allocation_and_strict_raw_support_is_unchanged()
    {
        var fixture=Make(true,Enumerable.Range(0,BlteDecoder.MaxProjectionChunks+1).Select(_=>new Block(Raw([]),0)).ToArray());
        Assert.Equal("BLTE_LIMIT",await ErrorCode(fixture));
        fixture=Make(true,new Block(Encrypted(),BlteDecoder.MaxCompressedChunkBytes+1L));Assert.Equal("BLTE_LIMIT",await ErrorCode(fixture));
        var bytes=new byte[8];"BLTE"u8.CopyTo(bytes);BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4),uint.MaxValue);
        Assert.Equal("BLTE_LIMIT",await ErrorCode(new(bytes,new string('0',32),new string('0',32),0,100)));
        var content=new byte[BlteDecoder.MaxCompressedChunkBytes];fixture=Make(false,new Block(Raw(content),content.Length)) with{CKey=Md5(content)};
        Assert.Equal("BLTE_LIMIT",await ErrorCode(fixture));
        using var source=new MemoryStream(fixture.Bytes);using var sink=new MemoryStream();
        var strict=await BlteDecoder.DecodeAsync(source,sink,fixture.EKey,fixture.CKey,fixture.Bytes.Length,content.Length);Assert.Equal(content.Length,strict.DecodedLength);
    }

    [Fact]
    public async Task Large_headers_coalesce_clear_chunks_into_exact_hash_spans_without_renumbering_holes()
    {
        var prefix=Enumerable.Range(0,130).Select(index=>(byte)index).ToArray();var tail="tail0tail1tail2tail3tail4tail5tail6tail7tail8"u8.ToArray();
        var blocks=prefix.Select(value=>new Block(Raw([value]),1)).Append(new(Encrypted(),3))
            .Concat(Enumerable.Range(0,9).Select(index=>new Block(index%2==0?Raw(System.Text.Encoding.ASCII.GetBytes("tail"+index)):Zlib(System.Text.Encoding.ASCII.GetBytes("tail"+index)),5))).ToArray();
        var fixture=Make(true,blocks);using var input=new AsyncInput(fixture.Bytes);using var output=new MemoryStream();var proof=await Decode(fixture,input,output);
        Assert.False(proof.ContentKeyVerified);Assert.Equal([new BlteReadableRange(0,130,Sha(prefix)),new(133,45,Sha(tail))],proof.ReadableRanges);
        Assert.Equal(new BlteUnavailableRange(130,3,130,"0807060504030201"),Assert.Single(proof.UnavailableRanges));
        Assert.InRange(input.MaxRequested,1,65536);using var replay=new AsyncInput(fixture.Bytes);
        var checkedProof=await BlteDecoder.VerifyAvailableAsync(replay,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded);
        Assert.Equal(proof.ReadableRanges,checkedProof.ReadableRanges);Assert.Equal(proof.UnavailableRanges,checkedProof.UnavailableRanges);
    }

    [Theory]
    [InlineData(128)][InlineData(129)]
    public async Task Coalescing_threshold_preserves_every_existing_small_container_range_identity(int count)
    {
        var blocks=Enumerable.Range(0,count-1).Select(_=>new Block(Raw("x"u8),1)).Append(new(Encrypted(),1)).ToArray();
        var fixture=Make(true,blocks);using var input=new MemoryStream(fixture.Bytes);using var output=new MemoryStream();var proof=await Decode(fixture,input,output);
        Assert.Equal(count-1,Assert.Single(proof.UnavailableRanges).ChunkOrdinal);
        if(count==128)
        {
            Assert.Equal(127,proof.ReadableRanges.Count);
            for(var index=0;index<127;index++)Assert.Equal(new BlteReadableRange(index,1,Sha("x"u8)),proof.ReadableRanges[index]);
        }
        else Assert.Equal(new BlteReadableRange(0,128,Sha(Enumerable.Repeat((byte)'x',128).ToArray())),Assert.Single(proof.ReadableRanges));
    }

    [Fact]
    public async Task Zero_output_clear_chunks_keep_original_ordinals_and_do_not_create_spurious_ranges()
    {
        var blocks=Enumerable.Range(0,126).Select(_=>new Block(Raw([]),0)).Concat(new Block[]{new(Raw("a"u8),1),new(Raw([]),0),new(Encrypted(),2),new(Raw([]),0),new(Raw("b"u8),1)}).ToArray();
        var fixture=Make(true,blocks);using var source=new MemoryStream(fixture.Bytes);using var output=new MemoryStream();var proof=await Decode(fixture,source,output);
        Assert.Equal([new BlteReadableRange(0,1,Sha("a"u8)),new(3,1,Sha("b"u8))],proof.ReadableRanges);
        Assert.Equal(new BlteUnavailableRange(1,2,128,"0807060504030201"),Assert.Single(proof.UnavailableRanges));
        blocks=Enumerable.Range(0,129).Select(_=>new Block(Raw([]),0)).Append(new(Encrypted(),3)).ToArray();fixture=Make(true,blocks);
        using var onlyHole=new MemoryStream(fixture.Bytes);var checkedProof=await BlteDecoder.VerifyAvailableAsync(onlyHole,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded);
        Assert.Empty(checkedProof.ReadableRanges);Assert.Equal(129,Assert.Single(checkedProof.UnavailableRanges).ChunkOrdinal);
    }

    [Fact]
    public async Task Coalesced_all_clear_output_still_verifies_complete_CKey()
    {
        var blocks=Enumerable.Range(0,300).Select(index=>new Block(Raw([(byte)index]),1)).ToArray();var content=Enumerable.Range(0,300).Select(index=>(byte)index).ToArray();
        var fixture=Make(true,blocks) with{CKey=Md5(content)};using var input=new MemoryStream(fixture.Bytes);using var output=new MemoryStream();var proof=await Decode(fixture,input,output);
        Assert.True(proof.ContentKeyVerified);Assert.Empty(proof.UnavailableRanges);Assert.Equal(new BlteReadableRange(0,300,Sha(content)),Assert.Single(proof.ReadableRanges));Assert.Equal(content,output.ToArray());
        Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{CKey=new string('0',32)}));
    }

    [Fact]
    public async Task Excessive_alternating_or_individual_encrypted_spans_remain_explicitly_bounded()
    {
        var alternating=Enumerable.Range(0,129).Select(index=>index%2==0?new Block(Raw("x"u8),1):new Block(Encrypted(),1)).ToArray();
        Assert.Equal("BLTE_LIMIT",await ErrorCode(Make(true,alternating)));
        Assert.Equal("BLTE_LIMIT",await ErrorCode(Make(true,Enumerable.Range(0,129).Select(_=>new Block(Encrypted(),1)).ToArray())));
    }

    [Fact]
    public async Task Large_container_checks_late_chunk_corruption_and_cancellation_without_publishing_a_span()
    {
        var blocks=Enumerable.Range(0,300).Select(_=>new Block(Raw("x"u8),1)).Append(new(Encrypted(),1)).ToArray();var fixture=Make(true,blocks);
        var bytes=fixture.Bytes.ToArray();bytes[^1]^=1;Assert.Equal("BLTE_INTEGRITY",await ErrorCode(fixture with{Bytes=bytes}));
        using var cancel=new CancellationTokenSource();var header=12+blocks.Length*24;
        using var input=new AsyncInput(fixture.Bytes){AfterRead=position=>{if(position>=header+50)cancel.Cancel();}};using var output=new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BlteDecoder.DecodeAvailableAsync(input,output,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded,cancel.Token));
        Assert.InRange(output.Length,0,25);Assert.True(input.CanRead);Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task Caller_output_must_be_empty_seekable_and_cancellation_preserves_ownership()
    {
        var fixture=Make(true,new Block(Raw(new byte[200000]),200000),new(Encrypted(),1));using var input=new MemoryStream(fixture.Bytes);using var output=new MemoryStream([1]);
        await Assert.ThrowsAsync<ArgumentException>(()=>Decode(fixture,input,output));Assert.Equal(0,input.Position);
        using var canceled=new CancellationTokenSource();canceled.Cancel();using var empty=new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BlteDecoder.DecodeAvailableAsync(input,empty,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded,canceled.Token));
        Assert.Equal(0,input.Position);
        using var during=new CancellationTokenSource();using var target=new CancelOutput(during);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>BlteDecoder.DecodeAvailableAsync(input,target,fixture.EKey,fixture.CKey,fixture.Bytes.Length,fixture.Decoded,during.Token));
        Assert.InRange(target.Length,1,65536);Assert.True(input.CanRead);Assert.True(target.CanWrite);
    }

    [Fact]
    public async Task Every_sync_and_async_read_overload_fails_before_touching_holes()
    {
        using var backing=new MemoryStream("abcdefghi"u8.ToArray());using var stream=new BlteProjectionStream(backing,[new(3,3,1,"0123456789ABCDEF")]);
        Assert.Equal(3,stream.Read(new byte[3],0,3));Assert.Equal(3,stream.Position);
        Assert.Equal("BLTE_RANGE_UNAVAILABLE",Assert.Throws<DiscoveryException>(()=>stream.ReadByte()).Code);Assert.Equal(3,backing.Position);
        Assert.Throws<DiscoveryException>(()=>stream.Read(new byte[1].AsSpan()));Assert.Throws<DiscoveryException>(()=>stream.Read(new byte[1],0,1));
        await Assert.ThrowsAsync<DiscoveryException>(()=>stream.ReadAsync(new byte[1],0,1));
        await Assert.ThrowsAsync<DiscoveryException>(()=>stream.ReadAsync(new byte[1].AsMemory()).AsTask());Assert.Equal(3,backing.Position);
        stream.Position=2;Assert.Throws<DiscoveryException>(()=>stream.Read(new byte[5]));Assert.Equal(2,backing.Position);
        Assert.Equal(0,stream.Read(Span<byte>.Empty));stream.Seek(6,SeekOrigin.Begin);Assert.Equal((int)'g',stream.ReadByte());
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>stream.ReadAsync(new byte[1],cancel.Token).AsTask());
        Assert.False(stream.CanWrite);Assert.Throws<NotSupportedException>(()=>stream.WriteByte(1));Assert.Throws<NotSupportedException>(()=>stream.SetLength(1));
    }

    [Fact]
    public async Task Guard_owns_immutable_range_snapshot_and_explicit_backing_disposal_policy()
    {
        using var backing=new MemoryStream();backing.SetLength(6);var holes=new List<BlteUnavailableRange>{new(2,2,0,"0000000000000001")};
        var guard=new BlteProjectionStream(backing,holes);holes.Clear();guard.Position=2;Assert.Throws<DiscoveryException>(()=>guard.ReadByte());
        await guard.DisposeAsync();Assert.True(backing.CanRead);Assert.Throws<ObjectDisposedException>(()=>guard.ReadByte());
        var owned=new MemoryStream(new byte[6]);using(var owner=new BlteProjectionStream(owned,[],leaveOpen:false)){}Assert.False(owned.CanRead);
        using var clear=new BlteProjectionStream(backing,[]);backing.SetLength(7);Assert.Equal("BLTE_INVALID",Assert.Throws<DiscoveryException>(()=>clear.ReadByte()).Code);
    }

    [Fact]
    public void Guard_rejects_overlaps_bounds_duplicate_ordinals_and_false_readable_partition()
    {
        using var backing=new MemoryStream(new byte[10]);
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(1,4,0,"0000000000000001"),new(4,1,1,"0000000000000002")]));
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(9,2,0,"0000000000000001")]));
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(0,0,0,"0000000000000001")]));
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(0,1,0,"0000000000000001"),new(2,1,0,"0000000000000002")]));
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(0,1,0,"abcdefabcdefabcd")]));
        Assert.Throws<DiscoveryException>(()=>new BlteProjectionStream(backing,[new(2,2,0,"0000000000000001")],readableRanges:[new(0,2,new string('0',64))]));
    }

    private sealed record Block(byte[] Bytes,long Decoded);
    private sealed record Fixture(byte[] Bytes,string EKey,string CKey,long Decoded,long? EncodedLength=null);
    private static Task<BlteProjectionVerification> Decode(Fixture fixture,Stream input,Stream output)=>BlteDecoder.DecodeAvailableAsync(input,output,fixture.EKey,fixture.CKey,fixture.EncodedLength??fixture.Bytes.Length,fixture.Decoded);
    private static async Task<string> ErrorCode(Fixture fixture)
    {using var input=new MemoryStream(fixture.Bytes);using var output=new MemoryStream();return (await Assert.ThrowsAsync<DiscoveryException>(()=>Decode(fixture,input,output))).Code;}
    private static Fixture Make(bool table,params Block[] blocks)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output);writer.Write("BLTE"u8);var header=table?12+24*blocks.Length:0;Big(writer,(uint)header);
        if(table){writer.Write((byte)15);writer.Write((byte)(blocks.Length>>16));writer.Write((byte)(blocks.Length>>8));writer.Write((byte)blocks.Length);foreach(var block in blocks){Big(writer,(uint)block.Bytes.Length);Big(writer,(uint)block.Decoded);writer.Write(MD5.HashData(block.Bytes));}}
        foreach(var block in blocks)writer.Write(block.Bytes);var bytes=output.ToArray();return new(bytes,Md5(table?bytes.AsSpan(0,header):bytes),new string('0',32),blocks.Sum(block=>block.Decoded));
    }
    private static byte[] Raw(ReadOnlySpan<byte> bytes)=>[(byte)'N',..bytes];
    private static byte[] Zlib(ReadOnlySpan<byte> bytes){using var target=new MemoryStream();target.WriteByte((byte)'Z');using(var zlib=new ZLibStream(target,CompressionLevel.SmallestSize,true))zlib.Write(bytes);return target.ToArray();}
    private static byte[] Encrypted()=>[(byte)'E',8,1,2,3,4,5,6,7,8,4,0x11,0x22,0x33,0x44,(byte)'S',0x99];
    private static string Md5(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(MD5.HashData(bytes));
    private static string Sha(ReadOnlySpan<byte> bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Big(BinaryWriter writer,uint value){Span<byte> bytes=stackalloc byte[4];BinaryPrimitives.WriteUInt32BigEndian(bytes,value);writer.Write(bytes);}
    private sealed class AsyncInput:MemoryStream
    {
        private readonly byte[] bytes;public Action<long>? AfterRead;public int MaxRequested;public AsyncInput(byte[] bytes):base(bytes){this.bytes=bytes;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous source read");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Synchronous source read");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();MaxRequested=Math.Max(MaxRequested,target.Length);var count=Math.Min(17,Math.Min(target.Length,(int)(Length-Position)));bytes.AsMemory((int)Position,count).CopyTo(target);Position+=count;AfterRead?.Invoke(Position);return ValueTask.FromResult(count);}
    }
    private sealed class AsyncOutput:MemoryStream
    {
        public override void Write(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous target write");
        public override void Write(ReadOnlySpan<byte> buffer)=>throw new InvalidOperationException("Synchronous target write");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {cancellationToken.ThrowIfCancellationRequested();var bytes=buffer.ToArray();base.Write(bytes,0,bytes.Length);return ValueTask.CompletedTask;}
    }
    private sealed class CancelOutput(CancellationTokenSource cancellation):MemoryStream
    {public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default){await base.WriteAsync(buffer,cancellationToken);cancellation.Cancel();}}
}
