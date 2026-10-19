using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DownloadLedgerTests
{
    [Fact]
    public async Task External_merge_deduplicates_full_keys_and_pages_exact_sorted_sizes()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var ledger=new DownloadLedger(objects,new(ChunkRecords:3,MergeFanIn:2));
        var fixture=Fixture((Key(9),9),(Key(1),11),(Key(4),0xffffffffffL),(Key(2),22),(Key(9),9),(Key(5),55),(Key(2),22),(Key(7),77),(Key(4),0xffffffffffL),(Key(1),11));
        using var input=new AsyncOnlyInput(fixture.Bytes);var stages=new List<string>();
        var artifact=await ledger.BuildAsync(input,fixture.Receipt,text=>{stages.Add(text);return Task.CompletedTask;},default);
        Assert.Equal(6,artifact.ObjectCount);Assert.Equal(32+6*24,artifact.ByteLength);
        Assert.Equal(9+11+22+55+77+0xffffffffffL,artifact.TotalEncodedBytes);Assert.True(input.CanRead);
        Assert.Contains("Merging download sizes",stages);Assert.Empty(Staging(directory));
        await ledger.ValidateAsync(artifact,default);
        var first=await ledger.ReadPageAsync(artifact,0,3,default);
        Assert.Equal([1L,2,3],first.Select(row=>row.Id));Assert.Equal([Key(1),Key(2),Key(4)],first.Select(row=>row.EncodingKey));
        var second=await ledger.ReadPageAsync(artifact,3,101,default);
        Assert.Equal([4L,5,6],second.Select(row=>row.Id));Assert.Equal([Key(5),Key(7),Key(9)],second.Select(row=>row.EncodingKey));
        Assert.Empty(await ledger.ReadPageAsync(artifact,6,100,default));
        var matches=await ledger.AssessAsync(artifact,new Dictionary<string,long>{{Key(4),0xffffffffffL},{Key(5),54},{Key(9),9},{Key(99),99}},default);
        Assert.Equal([Key(4),Key(9)],matches);
    }

    [Fact]
    public async Task Artifact_reopens_with_new_store_and_original_source_is_not_needed_for_local_queries()
    {
        using var directory=new TempDirectory();DownloadLedgerArtifact artifact;
        await using(var objects=new VerifiedObjectStore(directory.Path))
        {
            var fixture=Fixture((Key(2),21),(Key(1),10));
            artifact=await new DownloadLedger(objects).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default);
        }
        await using(var objects=new VerifiedObjectStore(directory.Path))
        {
            var ledger=new DownloadLedger(objects);await ledger.ValidateAsync(artifact,default);
            Assert.Equal([Key(1),Key(2)],(await ledger.ReadPageAsync(artifact,0,100,default)).Select(row=>row.EncodingKey));
            Assert.Equal([Key(2)],await ledger.AssessAsync(artifact,new Dictionary<string,long>{{Key(2),21}},default));
        }
    }

    [Fact]
    public async Task Ordering_compares_all_sixteen_key_bytes_as_unsigned_binary_values()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        string[] keys=[new string('f',32),"80000000000000000000000000000001","7fffffffffffffffffffffffffffffff","0000000000000000ffffffffffffffff"];
        var fixture=Fixture(keys.Select(key=>(key,1L)).ToArray());var ledger=new DownloadLedger(objects,new(ChunkRecords:2,MergeFanIn:2));
        var artifact=await ledger.BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default);
        Assert.Equal(keys.Order(StringComparer.Ordinal),(await ledger.ReadPageAsync(artifact,0,100,default)).Select(row=>row.EncodingKey));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public async Task Conflicting_duplicate_size_never_publishes_a_ledger(int chunk)
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(1),10),(Key(2),20),(Key(1),11));
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>new DownloadLedger(objects,new(ChunkRecords:chunk,MergeFanIn:2)).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default));
        Assert.Equal("DOWNLOAD_SIZE_CONFLICT",error.Code);Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Fact]
    public async Task Late_source_grammar_hash_or_IO_failure_retires_every_sort_run()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var original=Fixture((Key(3),30),(Key(2),20),(Key(1),10));
        var ledger=new DownloadLedger(objects,new(ChunkRecords:1,MergeFanIn:2));
        var trailing=FixtureBytes([..original.Bytes,0]);
        Assert.Equal("DL_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.BuildAsync(new MemoryStream(trailing.Bytes),trailing.Receipt,_=>Task.CompletedTask,default))).Code);
        Assert.Equal("DOWNLOAD_SOURCE_INTEGRITY",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.BuildAsync(new MemoryStream(original.Bytes),original.Receipt with{ContentKey=Key(80)},_=>Task.CompletedTask,default))).Code);
        using var failure=new AsyncOnlyInput(original.Bytes){FailAtEof=true};
        await Assert.ThrowsAsync<IOException>(()=>ledger.BuildAsync(failure,original.Receipt,_=>Task.CompletedTask,default));
        Assert.True(failure.CanRead);Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_length_private_run_corruption_cannot_publish_a_wrong_source_derived_ledger(bool merge)
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(3),30),(Key(1),10),(Key(2),20));var changed=false;
        var ledger=new DownloadLedger(objects,new(ChunkRecords:merge?1:20,MergeFanIn:2));
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,text=>
        {
            if(!changed&&text==(merge?"Merging download sizes":"Publishing download sizes"))
            {
                var path=Staging(directory).First();var bytes=File.ReadAllBytes(path);var length=bytes.Length;
                bytes[23]^=1;File.WriteAllBytes(path,bytes);Assert.Equal(length,new FileInfo(path).Length);changed=true;
            }
            return Task.CompletedTask;
        },default));
        Assert.True(changed);Assert.Equal("DOWNLOAD_LEDGER_INTEGRITY",error.Code);
        Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Fact]
    public async Task Changed_private_run_length_retires_the_opened_reader_before_staging_cleanup()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(2),20),(Key(1),10));var changed=false;
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>new DownloadLedger(objects,new(ChunkRecords:1,MergeFanIn:2)).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,text=>
        {
            if(!changed&&text=="Merging download sizes")
            {
                var path=Staging(directory).First();using var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None);
                file.SetLength(23);changed=true;
            }
            return Task.CompletedTask;
        },default));
        Assert.True(changed);Assert.Equal("DOWNLOAD_LEDGER_INTEGRITY",error.Code);
        Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Theory]
    [InlineData("Ordering download sizes")]
    [InlineData("Merging download sizes")]
    public async Task Cancellation_during_a_sort_stage_retires_staging_without_publication(string cancelAt)
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(3),3),(Key(2),2),(Key(1),1));using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new DownloadLedger(objects,new(ChunkRecords:1,MergeFanIn:2)).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,text=>{if(text==cancelAt)cancel.Cancel();return Task.CompletedTask;},cancel.Token));
        Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Fact]
    public async Task Bounded_sorting_awaits_progress_and_observes_failures_before_publication()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(1),10));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var expected=new InvalidOperationException("authored progress failure");
        var building=new DownloadLedger(objects).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,async _=>{entered.TrySetResult();await release.Task;throw expected;},default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(building.IsCompleted);release.SetResult();
        Assert.Same(expected,await Assert.ThrowsAsync<InvalidOperationException>(()=>building));Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Fact]
    public async Task Independent_builders_wait_for_the_bounded_shared_worker_instead_of_failing_busy()
    {
        using var firstDirectory=new TempDirectory();using var secondDirectory=new TempDirectory();
        await using var firstObjects=new VerifiedObjectStore(firstDirectory.Path);await using var secondObjects=new VerifiedObjectStore(secondDirectory.Path);
        var fixture=Fixture((Key(1),10));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=new DownloadLedger(firstObjects).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,async _=>{entered.TrySetResult();await release.Task;},default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second=new DownloadLedger(secondObjects).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default);
        Assert.False(second.IsCompleted);release.SetResult();
        var artifacts=await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(artifacts[0],artifacts[1]);Assert.Empty(Staging(firstDirectory));Assert.Empty(Staging(secondDirectory));
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("total")]
    [InlineData("temporary")]
    [InlineData("runs")]
    public async Task Resource_limits_fail_before_unbounded_work_and_cleanup_all_files(string kind)
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var fixture=Fixture((Key(3),15),(Key(2),15),(Key(1),15));
        var limits=kind switch
        {
            "entries"=>new DownloadLedgerLimits(MaxEntries:2),
            "total"=>new DownloadLedgerLimits(MaxTotalEncodedBytes:20),
            "temporary"=>new DownloadLedgerLimits(ChunkRecords:1,MaxTemporaryBytes:32),
            _=>new DownloadLedgerLimits(ChunkRecords:1,MaxRunFiles:2)
        };
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>new DownloadLedger(objects,limits).BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default));
        Assert.Equal(kind=="entries"?"DL_LIMIT":"DOWNLOAD_LEDGER_LIMIT",error.Code);Assert.Empty(Staging(directory));Assert.Empty(Published(directory));
    }

    [Fact]
    public async Task Empty_download_scope_produces_valid_zero_summary_and_invalid_requests_are_bounded()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);var ledger=new DownloadLedger(objects);
        var fixture=Fixture();var artifact=await ledger.BuildAsync(new MemoryStream(fixture.Bytes),fixture.Receipt,_=>Task.CompletedTask,default);
        Assert.Equal(0,artifact.ObjectCount);Assert.Equal(0,artifact.TotalEncodedBytes);Assert.Equal(32,artifact.ByteLength);
        await ledger.ValidateAsync(artifact,default);Assert.Empty(await ledger.ReadPageAsync(artifact,0,100,default));
        Assert.Empty(await ledger.AssessAsync(artifact,new Dictionary<string,long>{{Key(1),10}},default));
        Assert.Equal("INVALID_REQUEST",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ReadPageAsync(artifact,-1,100,default))).Code);
        Assert.Equal("INVALID_REQUEST",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ReadPageAsync(artifact,0,201,default))).Code);
        Assert.Equal("DOWNLOAD_LEDGER_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ValidateAsync(artifact with{ObjectCount=long.MaxValue},default))).Code);
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("version")]
    [InlineData("width")]
    [InlineData("count")]
    [InlineData("total")]
    [InlineData("unordered")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    [InlineData("overflow")]
    public async Task Full_SHA_does_not_replace_structural_and_accounting_validation(string defect)
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var bytes=RawLedger(2,30,(Key(1),10),(Key(2),20));
        switch(defect)
        {
            case "magic":bytes[0]^=1;break;
            case "version":BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8),99);break;
            case "width":BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12),25);break;
            case "count":BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16),3);break;
            case "total":BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24),31);break;
            case "unordered":Convert.FromHexString(Key(3)).CopyTo(bytes,32);break;
            case "duplicate":Convert.FromHexString(Key(1)).CopyTo(bytes,56);break;
            case "negative":BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(48),-1);break;
            case "overflow":BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(48),long.MaxValue);break;
        }
        var artifact=await PublishRaw(objects,bytes,2,30);
        Assert.Equal("DOWNLOAD_LEDGER_INTEGRITY",(await Assert.ThrowsAsync<DiscoveryException>(()=>new DownloadLedger(objects).ValidateAsync(artifact,default))).Code);
    }

    [Fact]
    public async Task Physical_corruption_missing_bytes_and_trailing_geometry_are_distinct_failures()
    {
        using var directory=new TempDirectory();await using var objects=new VerifiedObjectStore(directory.Path);
        var artifact=await PublishRaw(objects,RawLedger(1,10,(Key(1),10)),1,10);var ledger=new DownloadLedger(objects);
        var path=Path.Combine(directory.Path,"objects-v1",artifact.Sha256[..2],artifact.Sha256[2..]);
        await File.WriteAllBytesAsync(path,new byte[artifact.ByteLength]);
        Assert.Equal("LOCAL_OBJECT_CORRUPT",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ValidateAsync(artifact,default))).Code);
        File.Delete(path);Assert.Equal("LOCAL_OBJECT_MISSING",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ValidateAsync(artifact,default))).Code);
        var trailing=await PublishRaw(objects,[..RawLedger(1,10,(Key(1),10)),1],1,10);
        Assert.Equal("DOWNLOAD_LEDGER_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>ledger.ValidateAsync(trailing,default))).Code);
    }

    private static string Key(int value)=>value.ToString("x32");
    private static IEnumerable<string> Staging(TempDirectory directory)=>Directory.Exists(Path.Combine(directory.Path,"staging-v1"))?Directory.EnumerateFiles(Path.Combine(directory.Path,"staging-v1")):[];
    private static IEnumerable<string> Published(TempDirectory directory)=>Directory.Exists(Path.Combine(directory.Path,"objects-v1"))?Directory.EnumerateFiles(Path.Combine(directory.Path,"objects-v1"),"*",SearchOption.AllDirectories).Where(path=>Path.GetFileName(path)!=".owner"):[];
    private static (byte[] Bytes,MetadataReceipt Receipt) Fixture(params (string Key,long Bytes)[] rows)
    {
        using var output=new MemoryStream();output.Write("DL"u8);output.Write([3,16,0]);Big(output,rows.Length,4);Big(output,1,2);output.Write([2,0xff,1,2,3]);
        foreach(var row in rows){output.Write(Convert.FromHexString(row.Key));Big(output,row.Bytes,5);output.Write([0x80,1,2]);}
        output.Write("all"u8);output.WriteByte(0);Big(output,5,2);output.Write(new byte[(rows.Length+7)/8]);return FixtureBytes(output.ToArray());
    }
    private static (byte[] Bytes,MetadataReceipt Receipt) FixtureBytes(byte[] decoded)
    {
        byte[] encoded=[(byte)'B',(byte)'L',(byte)'T',(byte)'E',0,0,0,0,(byte)'N',..decoded];
        var ckey=Convert.ToHexStringLower(MD5.HashData(decoded));var ekey=Convert.ToHexStringLower(MD5.HashData(encoded));
        return(decoded,new("download",ckey,ekey,Convert.ToHexStringLower(SHA256.HashData(encoded)),encoded.Length.ToString(),decoded.Length.ToString(),InventoryFixture.Url("data",ekey),"2026-10-18T12:00:00Z","2026-10-18T12:00:00Z","download-v1"));
    }
    private static byte[] RawLedger(long count,long total,params (string Key,long Bytes)[] rows)
    {
        using var output=new MemoryStream();output.Write("VMDLEDG1"u8);Big(output,1,4);Big(output,24,4);Big(output,count,8);Big(output,total,8);
        foreach(var row in rows){output.Write(Convert.FromHexString(row.Key));Big(output,row.Bytes,8);}return output.ToArray();
    }
    private static async Task<DownloadLedgerArtifact> PublishRaw(VerifiedObjectStore objects,byte[] bytes,long count,long total)
    {
        string path;await using(var file=await objects.CreateStagingFileAsync(default)){path=file.Name;await file.WriteAsync(bytes);await file.FlushAsync();}
        return new(await objects.PublishFileAsync(path,bytes.Length,default),bytes.Length,count,total);
    }
    private static void Big(Stream output,long value,int width){Span<byte> bytes=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(bytes,value);output.Write(bytes[(8-width)..]);}
    private sealed class AsyncOnlyInput:MemoryStream
    {
        private readonly byte[] bytes;
        public AsyncOnlyInput(byte[] bytes):base(bytes){this.bytes=bytes;}
        public bool FailAtEof{get;init;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous I/O forbidden");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Synchronous I/O forbidden");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();if(Position==Length&&FailAtEof)throw new IOException("authored late source failure");
            var count=Math.Min(7,Math.Min(buffer.Length,(int)(Length-Position)));bytes.AsMemory((int)Position,count).CopyTo(buffer);Position+=count;return ValueTask.FromResult(count);
        }
    }
}
