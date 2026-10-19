using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal readonly record struct LedgerValue(ulong High,ulong Low,long Bytes)
{
    internal static LedgerValue Parse(string key,long bytes)
    {
        if(key.Length!=32)throw new DiscoveryException("DOWNLOAD_LEDGER_INVALID","A complete encoded identity is required.");
        Span<byte> raw=stackalloc byte[16];
        if(Convert.FromHexString(key.AsSpan(),raw,out var consumed,out var written)!=System.Buffers.OperationStatus.Done||consumed!=32||written!=16)
            throw new DiscoveryException("DOWNLOAD_LEDGER_INVALID","An encoded identity is malformed.");
        return new(BinaryPrimitives.ReadUInt64BigEndian(raw),BinaryPrimitives.ReadUInt64BigEndian(raw[8..]),bytes);
    }
    internal static LedgerValue Read(ReadOnlySpan<byte> bytes)=>new(BinaryPrimitives.ReadUInt64BigEndian(bytes),BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),BinaryPrimitives.ReadInt64BigEndian(bytes[16..]));
    internal void Write(Span<byte> bytes)
    {BinaryPrimitives.WriteUInt64BigEndian(bytes,High);BinaryPrimitives.WriteUInt64BigEndian(bytes[8..],Low);BinaryPrimitives.WriteInt64BigEndian(bytes[16..],Bytes);}
    internal bool SameKey(LedgerValue other)=>High==other.High&&Low==other.Low;
    internal int CompareKey(LedgerValue other)=>High!=other.High?High.CompareTo(other.High):Low.CompareTo(other.Low);
    internal string Key=>$"{High:x16}{Low:x16}";
}
internal sealed class LedgerValueComparer(CancellationToken token):IComparer<LedgerValue>
{
    private int comparisons;
    public int Compare(LedgerValue left,LedgerValue right)
    {if((++comparisons&4095)==0)token.ThrowIfCancellationRequested();return left.CompareKey(right);}
}
internal sealed record LedgerRun(string Path,long Length,long Count,long Total,string Sha256);
internal sealed class LedgerTemporaryFiles(VerifiedObjectStore objects,long maximum,int maxFiles):IDisposable
{
    private readonly Dictionary<string,long> files=new(StringComparer.Ordinal);
    private long reserved;
    internal async Task<FileStream> CreateAsync(CancellationToken token)
    {
        if(files.Count>=maxFiles)throw new DiscoveryException("DOWNLOAD_LEDGER_LIMIT","Download sorting exceeds its finite run-file limit.");
        var stream=await objects.CreateStagingFileAsync(token);files.Add(stream.Name,0);return stream;
    }
    internal void Reserve(string path,long bytes)
    {
        if(bytes<0||bytes>maximum-reserved)throw new DiscoveryException("DOWNLOAD_LEDGER_LIMIT","Download sorting exceeds its temporary-storage reservation.");
        reserved+=bytes;files[path]=checked(files[path]+bytes);
    }
    internal void Delete(string path){if(File.Exists(path))File.Delete(path);Forget(path);}
    internal void Forget(string path){if(files.Remove(path,out var bytes))reserved-=bytes;}
    public void Dispose()
    {
        Exception? failure=null;
        foreach(var path in files.Keys)
        {try{if(File.Exists(path))File.Delete(path);}catch(IOException error){failure??=error;}}
        files.Clear();reserved=0;if(failure is not null)throw failure;
    }
}
internal sealed class LedgerRowWriter(FileStream output,LedgerTemporaryFiles files):IDisposable
{
    private readonly byte[] buffer=new byte[24*2730];
    private readonly IncrementalHash hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private int count;
    internal ValueTask WriteAsync(LedgerValue value,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();files.Reserve(output.Name,24);value.Write(buffer.AsSpan(count,24));count+=24;
        return count==buffer.Length?FlushAsync(token):ValueTask.CompletedTask;
    }
    internal async ValueTask FlushAsync(CancellationToken token)
    {if(count==0)return;await output.WriteAsync(buffer.AsMemory(0,count),token);hash.AppendData(buffer,0,count);count=0;}
    internal string CompleteHash()=>Convert.ToHexStringLower(hash.GetHashAndReset());
    public void Dispose()=>hash.Dispose();
}
internal sealed class LedgerRunReader:IAsyncDisposable
{
    private readonly FileStream input;
    private readonly byte[] buffer=new byte[24*2730];
    private long remaining;
    private int at,end;
    private LedgerValue? previous;
    private bool eof;
    private readonly string? expectedSha256;
    private readonly IncrementalHash? hash;
    internal LedgerRunReader(FileStream input,long length,string? expectedSha256=null)
    {
        this.input=input;remaining=length;this.expectedSha256=expectedSha256;
        if(length<0||length%24!=0||input.Length-input.Position!=length)
        {
            input.Dispose();
            throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY","A download ledger has invalid record geometry.");
        }
        if(expectedSha256 is not null)hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    internal LedgerValue Current{get;private set;}
    internal async ValueTask<bool> NextAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(at==end)
        {
            if(remaining==0)
            {
                if(!eof)
                {
                    eof=true;if(await input.ReadAsync(buffer.AsMemory(0,1),token)!=0)throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY","A download ledger has trailing bytes.");
                    if(hash is not null&&Convert.ToHexStringLower(hash.GetHashAndReset())!=expectedSha256)
                        throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY","A private download sorting run failed integrity verification.");
                }
                return false;
            }
            var bytes=(int)Math.Min(buffer.Length,remaining);
            try{await input.ReadExactlyAsync(buffer.AsMemory(0,bytes),token);}
            catch(EndOfStreamException){throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY","A download ledger ended unexpectedly.");}
            hash?.AppendData(buffer,0,bytes);
            remaining-=bytes;at=0;end=bytes;
        }
        var value=LedgerValue.Read(buffer.AsSpan(at,24));at+=24;
        if(value.Bytes<0||value.Bytes>0xffffffffffL||previous is {} prior&&prior.CompareKey(value)>0)
            throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY","A download ledger has an invalid size or ordering.");
        previous=value;Current=value;return true;
    }
    public ValueTask DisposeAsync(){hash?.Dispose();return input.DisposeAsync();}
}
internal sealed class LedgerSourceStream(Stream source,long maximum):Stream
{
    private readonly IncrementalHash hash=IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    internal long ConsumedBytes{get;private set;}
    internal string ContentKey()=>Convert.ToHexStringLower(hash.GetHashAndReset());
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)
    {
        var count=await source.ReadAsync(buffer,cancellationToken);
        if(count>maximum-ConsumedBytes)throw new DiscoveryException("DOWNLOAD_SOURCE_INTEGRITY","Decoded download metadata exceeds its source length.");
        hash.AppendData(buffer.Span[..count]);ConsumedBytes+=count;return count;
    }
    protected override void Dispose(bool disposing){if(disposing)hash.Dispose();base.Dispose(disposing);}
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException("Asynchronous source reads are required.");
    public override void Flush()=>throw new NotSupportedException();
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
