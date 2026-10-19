using System.Buffers.Binary;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class DownloadLedger
{
    private readonly object validatedGate=new();
    private readonly Queue<DownloadLedgerArtifact> validated=new();
    private bool Known(DownloadLedgerArtifact artifact){lock(validatedGate)return validated.Contains(artifact);}
    private void Remember(DownloadLedgerArtifact artifact)
    {lock(validatedGate){if(validated.Contains(artifact))return;while(validated.Count>=16)validated.Dequeue();validated.Enqueue(artifact);}}
    internal async Task ValidateAsync(DownloadLedgerArtifact artifact,CancellationToken token,Func<string,Task>? progress=null)
    {
        await using var input=await OpenLedgerAsync(artifact,token);
        await ValidateRowsAsync(input,artifact,token,progress);Remember(artifact);
    }
    internal async Task<IReadOnlyList<DownloadLedgerRow>> ReadPageAsync(DownloadLedgerArtifact artifact,long after,int limit,CancellationToken token)
    {
        if(after<0||after>artifact.ObjectCount||limit is <1 or >200)throw Error("INVALID_REQUEST","Choose a bounded download ledger page.");
        await using var input=await OpenLedgerAsync(artifact,token);
        if(!Known(artifact)){await ValidateRowsAsync(input,artifact,token,null);Remember(artifact);}
        input.Position=checked(HeaderBytes+after*RecordBytes);
        var available=artifact.ObjectCount-after;var count=(int)Math.Min(limit,available);
        if(count==0)return [];
        var reader=new LedgerRunReader(input,checked(available*RecordBytes));
        var rows=new List<DownloadLedgerRow>(count);
        for(var i=0;i<count;i++)
        {
            if(!await reader.NextAsync(token))throw Error("DOWNLOAD_LEDGER_INTEGRITY","A download ledger page ended unexpectedly.");
            var row=reader.Current;rows.Add(new(after+i+1,row.Key,row.Bytes));
        }
        return rows;
    }
    internal async Task<IReadOnlyList<string>> AssessAsync(DownloadLedgerArtifact artifact,IReadOnlyDictionary<string,long> candidates,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidates);token.ThrowIfCancellationRequested();
        if(candidates.Count>10000)throw Error("DOWNLOAD_LEDGER_LIMIT","Retained-object assessment exceeds its bounded candidate count.");
        var keys=new LedgerValue[candidates.Count];var index=0;
        foreach(var (key,bytes) in candidates)
        {if(!Canonical(key,32)||bytes<0||bytes>0xffffffffffL)throw Error("DOWNLOAD_LEDGER_INVALID","A retained-object candidate has an invalid exact identity or size.");keys[index++]=LedgerValue.Parse(key,bytes);}
        try{Array.Sort(keys,new LedgerValueComparer(token));}
        catch(InvalidOperationException error)when(error.InnerException is OperationCanceledException&&token.IsCancellationRequested){throw new OperationCanceledException(token);}
        await using var input=await OpenLedgerAsync(artifact,token);
        if(!Known(artifact)){await ValidateRowsAsync(input,artifact,token,null);Remember(artifact);}
        input.Position=HeaderBytes;var reader=new LedgerRunReader(input,artifact.ByteLength-HeaderBytes);
        var matched=new List<string>();index=0;
        while(index<keys.Length&&await reader.NextAsync(token))
        {
            var row=reader.Current;
            while(index<keys.Length&&keys[index].CompareKey(row)<0)index++;
            if(index==keys.Length)break;
            if(keys[index].SameKey(row)){if(keys[index].Bytes==row.Bytes)matched.Add(row.Key);index++;}
        }
        return matched;
    }
    private Task<FileStream> OpenLedgerAsync(DownloadLedgerArtifact artifact,CancellationToken token) =>
        OpenLedgerAsync(artifact,resources,workContext,token);
    private async Task<FileStream> OpenLedgerAsync(DownloadLedgerArtifact artifact,AcquisitionResources? openingResources,
        IAcquisitionWorkContext? openingContext,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if(!Canonical(artifact.Sha256,64)||artifact.ObjectCount is <0 or >10_000_000||artifact.TotalEncodedBytes<0||
            artifact.ByteLength!=HeaderBytes+artifact.ObjectCount*RecordBytes||artifact.ByteLength>VerifiedObjectStore.MaxObjectBytes)
            throw Error("DOWNLOAD_LEDGER_INVALID","A download ledger descriptor is invalid.");
        var input=openingResources is null?await objects.OpenVerifiedAsync(artifact.Sha256,artifact.ByteLength,token)
            :await objects.OpenVerifiedAsync(artifact.Sha256,artifact.ByteLength,openingResources,openingContext,token);
        try
        {
            var header=new byte[HeaderBytes];await input.ReadExactlyAsync(header,token);
            if(!header.AsSpan(0,8).SequenceEqual(Magic)||BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8))!=1||
                BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12))!=RecordBytes||BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16))!=artifact.ObjectCount||
                BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(24))!=artifact.TotalEncodedBytes)
                throw Error("DOWNLOAD_LEDGER_INTEGRITY","The download ledger header differs from its descriptor or supported format.");
            return input;
        }
        catch{await input.DisposeAsync();throw;}
    }
    private static async Task ValidateRowsAsync(FileStream input,DownloadLedgerArtifact artifact,CancellationToken token,Func<string,Task>? progress)
    {
        input.Position=HeaderBytes;var reader=new LedgerRunReader(input,artifact.ByteLength-HeaderBytes);
        LedgerValue? previous=null;long count=0,total=0;
        if(progress is not null)await progress(InventoryProgress.Format("Verifying saved download sizes",0,artifact.ObjectCount,"objects"));
        while(await reader.NextAsync(token))
        {
            var value=reader.Current;
            if(previous is {} prior&&prior.CompareKey(value)>=0)throw Error("DOWNLOAD_LEDGER_INTEGRITY","Download ledger identities are not unique and strictly ordered.");
            if(value.Bytes>long.MaxValue-total)throw Error("DOWNLOAD_LEDGER_INTEGRITY","Download ledger accounting overflows the supported integer range.");
            total+=value.Bytes;count++;previous=value;
            if(progress is not null&&(count%65536==0||count==artifact.ObjectCount))
                await progress(InventoryProgress.Format("Verifying saved download sizes",count,artifact.ObjectCount,"objects"));
        }
        if(count!=artifact.ObjectCount||total!=artifact.TotalEncodedBytes)throw Error("DOWNLOAD_LEDGER_INTEGRITY","Download ledger counts or total differ from its complete records.");
    }
}
