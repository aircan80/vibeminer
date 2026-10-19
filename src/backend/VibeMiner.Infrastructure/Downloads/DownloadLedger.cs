using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

internal sealed record DownloadLedgerArtifact(string Sha256,long ByteLength,long ObjectCount,long TotalEncodedBytes);
internal sealed record DownloadLedgerRow(long Id,string EncodingKey,long EncodedBytes);
internal sealed record DownloadLedgerLimits(int ChunkRecords=349525,int MergeFanIn=32,long MaxTemporaryBytes=1L<<30,
    long MaxEntries=10_000_000,long MaxTotalEncodedBytes=long.MaxValue,int MaxRunFiles=128);

/// <summary>Original compact, immutable DL-scope ledger. Original tags/ordinals stay in retained DL metadata.</summary>
internal sealed partial class DownloadLedger
{
    internal const string Revision="download-ledger-v1";
    internal const int HeaderBytes=32,RecordBytes=24;
    private static readonly byte[] Magic="VMDLEDG1"u8.ToArray();
    private static readonly SemaphoreSlim Builds=new(1,1);
    private static readonly SemaphoreSlim BuildAdmissions=new(8,8);
    private readonly VerifiedObjectStore objects;
    private readonly DownloadLedgerLimits limits;
    private readonly AcquisitionResources? resources;
    private readonly IAcquisitionWorkContext? workContext;
    internal DownloadLedger(VerifiedObjectStore objects,AcquisitionResources resources,
        IAcquisitionWorkContext? workContext,DownloadLedgerLimits? limits=null) : this(objects,limits)
    {
        this.resources=resources??throw new ArgumentNullException(nameof(resources));this.workContext=workContext;
    }
    internal DownloadLedger(VerifiedObjectStore objects,DownloadLedgerLimits? limits=null)
    {
        this.objects=objects??throw new ArgumentNullException(nameof(objects));this.limits=limits??new();
        if(this.limits.ChunkRecords is <1 or >349525||this.limits.MergeFanIn is <2 or >32||this.limits.MaxTemporaryBytes is <32 or >1073741824L||
            this.limits.MaxEntries is <0 or >10_000_000||this.limits.MaxTotalEncodedBytes<0||this.limits.MaxRunFiles is <2 or >128)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    internal async Task<DownloadLedgerArtifact> BuildAsync(Stream decoded,MetadataReceipt source,Func<string,Task> progress,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(decoded);ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(progress);
        var decodedBytes=ValidateSource(source);token.ThrowIfCancellationRequested();
        if(!await BuildAdmissions.WaitAsync(0,token))throw Error("DOWNLOAD_LEDGER_BUSY","The bounded download-ledger preparation queue is full.");
        try{await Builds.WaitAsync(token);}catch{BuildAdmissions.Release();throw;}
        var files=new LedgerTemporaryFiles(objects,limits.MaxTemporaryBytes,limits.MaxRunFiles);
        try
        {
            var runs=new List<LedgerRun>();var buffer=new LedgerValue[limits.ChunkRecords];var count=0;
            async Task Flush()
            {
                if(count==0)return;
                await progress("Ordering download sizes");
                var admitted=count;
                try { await Task.Run(()=>Array.Sort(buffer,0,admitted,new LedgerValueComparer(token)),token); }
                catch(InvalidOperationException error)when(error.InnerException is OperationCanceledException&&token.IsCancellationRequested)
                {throw new OperationCanceledException(token);}
                runs.Add(await WriteRunAsync(buffer,admitted,files,token));count=0;
            }
            var consumer=new LedgerConsumer(buffer,()=>count,value=>count=value,Flush,progress);
            using var checkedInput=new LedgerSourceStream(decoded,decodedBytes);
            var parsed=await DownloadInventoryReader.ReadAsync(checkedInput,new(MaxDecodedBytes:decodedBytes,MaxEntries:limits.MaxEntries),consumer,token);
            if(parsed.ConsumedBytes!=decodedBytes||checkedInput.ConsumedBytes!=decodedBytes||checkedInput.ContentKey()!=source.ContentKey)
                throw Error("DOWNLOAD_SOURCE_INTEGRITY","Decoded download metadata differs from its source receipt.");
            await Flush();
            while(runs.Count>1)
            {
                var next=new List<LedgerRun>();
                for(var start=0;start<runs.Count;start+=limits.MergeFanIn)
                {
                    token.ThrowIfCancellationRequested();
                    var group=runs.Skip(start).Take(limits.MergeFanIn).ToArray();
                    await progress("Merging download sizes");
                    var merged=await MergeRunsAsync(group,files,token);next.Add(merged);
                    foreach(var run in group)files.Delete(run.Path);
                }
                runs=next;
            }
            var final=runs.Count==0?new LedgerRun("",0,0,0,""):runs[0];
            var length=checked(HeaderBytes+final.Count*RecordBytes);
            await progress("Publishing download sizes");
            string path;
            await using(var output=await files.CreateAsync(token))
            {
                path=output.Name;files.Reserve(path,HeaderBytes);
                var header=Header(final.Count,final.Total);await output.WriteAsync(header,token);
                if(final.Count>0)
                {
                    files.Reserve(path,final.Length);
                    await using var input=OpenRun(final.Path);
                    if(input.Length!=final.Length)throw Error("DOWNLOAD_LEDGER_INTEGRITY","A private download run changed length.");
                    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var copyBuffer=new byte[65536];long copied=0;
                    while(true)
                    {
                        token.ThrowIfCancellationRequested();var bytes=await input.ReadAsync(copyBuffer,token);if(bytes==0)break;
                        if(bytes>final.Length-copied)throw Error("DOWNLOAD_LEDGER_INTEGRITY","A private download run grew during publication.");
                        await output.WriteAsync(copyBuffer.AsMemory(0,bytes),token);hash.AppendData(copyBuffer,0,bytes);copied+=bytes;
                    }
                    if(copied!=final.Length||Convert.ToHexStringLower(hash.GetHashAndReset())!=final.Sha256)
                        throw Error("DOWNLOAD_LEDGER_INTEGRITY","A private download sorting run failed final integrity verification.");
                }
                await output.FlushAsync(token);
                if(output.Length!=length)throw Error("DOWNLOAD_LEDGER_INTEGRITY","The final download ledger changed length.");
            }
            var sha=resources is null?await objects.PublishFileAsync(path,length,token)
                :await objects.PublishFileAsync(path,length,resources,workContext,token);files.Forget(path);
            var artifact=new DownloadLedgerArtifact(sha,length,final.Count,final.Total);Remember(artifact);return artifact;
        }
        finally {try{files.Dispose();}finally{Builds.Release();BuildAdmissions.Release();}}
    }
    private async Task<LedgerRun> WriteRunAsync(LedgerValue[] rows,int count,LedgerTemporaryFiles files,CancellationToken token)
    {
        await using var output=await files.CreateAsync(token);using var writer=new LedgerRowWriter(output,files);
        LedgerValue? previous=null;long unique=0,total=0;
        for(var i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var value=rows[i];
            if(previous is {} prior&&prior.SameKey(value))
            {if(prior.Bytes!=value.Bytes)throw Error("DOWNLOAD_SIZE_CONFLICT","A download object has conflicting published lengths.");continue;}
            total=AddTotal(total,value.Bytes);unique++;await writer.WriteAsync(value,token);previous=value;
        }
        await writer.FlushAsync(token);await output.FlushAsync(token);
        return new(output.Name,output.Length,unique,total,writer.CompleteHash());
    }
    private async Task<LedgerRun> MergeRunsAsync(IReadOnlyList<LedgerRun> runs,LedgerTemporaryFiles files,CancellationToken token)
    {
        var readers=new List<LedgerRunReader>();
        try
        {
            var queue=new PriorityQueue<LedgerRunReader,LedgerValue>(new LedgerValueComparer(token));
            foreach(var run in runs)
            {var reader=new LedgerRunReader(OpenRun(run.Path),run.Length,run.Sha256);readers.Add(reader);if(await reader.NextAsync(token))queue.Enqueue(reader,reader.Current);}
            await using var output=await files.CreateAsync(token);using var writer=new LedgerRowWriter(output,files);
            LedgerValue? previous=null;long unique=0,total=0;
            while(queue.TryDequeue(out var reader,out _))
            {
                token.ThrowIfCancellationRequested();var value=reader.Current;
                if(previous is {} prior&&prior.SameKey(value))
                {if(prior.Bytes!=value.Bytes)throw Error("DOWNLOAD_SIZE_CONFLICT","A download object has conflicting published lengths.");}
                else {total=AddTotal(total,value.Bytes);unique++;await writer.WriteAsync(value,token);previous=value;}
                if(await reader.NextAsync(token))queue.Enqueue(reader,reader.Current);
            }
            await writer.FlushAsync(token);await output.FlushAsync(token);
            return new(output.Name,output.Length,unique,total,writer.CompleteHash());
        }
        finally {foreach(var reader in readers)await reader.DisposeAsync();}
    }
    private long AddTotal(long total,long bytes)
    {
        if(bytes<0||bytes>limits.MaxTotalEncodedBytes-total)throw Error("DOWNLOAD_LEDGER_LIMIT","Download size exceeds the admitted exact accounting range.");
        return checked(total+bytes);
    }
    private static long ValidateSource(MetadataReceipt source)
    {
        if(source.Role!="download"||!Canonical(source.ContentKey,32)||!Canonical(source.EncodingKey,32)||!Canonical(source.Sha256,64)||
            source.ParserRevision is not ("download-v1" or "metadata-blte-source-v1")||
            !long.TryParse(source.DecodedBytes,NumberStyles.None,CultureInfo.InvariantCulture,out var decoded)||decoded<11||decoded>1L<<30||
            !long.TryParse(source.EncodedBytes,NumberStyles.None,CultureInfo.InvariantCulture,out var encoded)||encoded<9||encoded>VerifiedObjectStore.MaxObjectBytes)
            throw Error("DOWNLOAD_SOURCE_INVALID","A complete supported download metadata receipt is required.");
        return decoded;
    }
    private static byte[] Header(long count,long total)
    {
        var bytes=new byte[HeaderBytes];Magic.CopyTo(bytes,0);BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8),1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12),RecordBytes);BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16),count);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24),total);return bytes;
    }
    private static FileStream OpenRun(string path)=>new(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
    private static bool Canonical(string? value,int length)=>value is not null&&value.Length==length&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static DiscoveryException Error(string code,string message)=>new(code,message);
    private sealed class LedgerConsumer(LedgerValue[] buffer,Func<int> count,Action<int> setCount,Func<Task> flush,Func<string,Task> progress):IDownloadConsumer
    {
        private long total;
        public ValueTask HeaderAsync(DownloadHeader header,CancellationToken token){total=header.EntryCount;return new(progress(InventoryProgress.Format("Reading download sizes",0,total,"entries")));}
        public ValueTask EntryAsync(long ordinal,DownloadEntry entry,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var index=count();buffer[index]=LedgerValue.Parse(entry.EncodingKey,entry.EncodedBytes);setCount(index+1);
            if(index+1==buffer.Length)return new(flush());
            return (ordinal+1)%65536==0?new(progress(InventoryProgress.Format("Reading download sizes",ordinal+1,total,"entries"))):ValueTask.CompletedTask;
        }
        public ValueTask TagAsync(int ordinal,DownloadTag tag,CancellationToken token){token.ThrowIfCancellationRequested();return ValueTask.CompletedTask;}
    }
}
