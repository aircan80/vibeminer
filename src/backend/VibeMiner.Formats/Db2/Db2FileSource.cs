using VibeMiner.Files;

namespace VibeMiner.Formats;

/// <summary>
/// How opening a DB2 file through layer 4 ended. <see cref="Open"/> has a table. The states from <see cref="UnknownFile"/> to
/// <see cref="TooLarge"/> are layer 4's <see cref="FileReadState"/> of the same name: the lookup states, layer 2's Missing
/// (a dated 404, never proof of absence), NeedsNetwork (not stored, network off), Unavailable and StoreFailed, and Corrupt,
/// Unsupported and WrongKey. <see cref="Protected"/> means every byte of the file is in holes. <see cref="Rejected"/> means
/// the bytes were read but the DB2 reader refused them; <see cref="Db2Open.Code"/> is its DB2_* or BLTE_* code.
/// </summary>
public enum Db2OpenState
{
    Open,
    UnknownFile,
    NotInLocale,
    NotInEncoding,
    Missing,
    NeedsNetwork,
    Unavailable,
    Corrupt,
    Unsupported,
    WrongKey,
    StoreFailed,
    TooLarge,
    Protected,
    Rejected,
}

/// <summary>
/// A DB2 byte read through layer 4 failed after the source opened. <see cref="State"/> is never Open or Rejected;
/// <see cref="Code"/> and <see cref="ObservedAt"/> are layer 4's (layer 2's where the failure came from there).
/// </summary>
public sealed class Db2SourceException(Db2OpenState state,string? code,string? message,DateTimeOffset? observedAt=null)
    :Exception(message??code??state.ToString())
{
    public Db2OpenState State{get;}=state;
    public string? Code{get;}=code;
    public DateTimeOffset? ObservedAt{get;}=observedAt;
}

/// <summary>
/// An <see cref="IDb2ByteSource"/> over one file of a layer 4 handle: <see cref="Length"/> and <see cref="Holes"/> come from
/// <see cref="IBuildFiles.OpenReadAsync"/>, which decodes nothing, and each layer 4 hole becomes a <see cref="Db2Hole"/> named
/// by its TACT key. Reads go through that one stream, one at a time, so concurrent table reads are safe. A read that fails throws
/// <see cref="Db2SourceException"/>, never a layer 4 exception. The source outlives the handle; dispose it when done with every
/// table opened over it.
/// <para>
/// The stream keeps only its last decoded BLTE chunk, and a row read touches the record and then its string, which usually lie
/// in different chunks: read straight through, every row decoded two chunks (#192). So a read shorter than <see cref="BlockSize"/>
/// is served from the last <see cref="MaxBlocks"/> aligned blocks of the file, each read through the stream once. A block that
/// overlaps a hole, or whose read failed, isn't kept: such reads go straight to the stream for exactly their range, so a failure
/// is the same one, at the same row, as without blocks. Dispose clears the blocks.
/// </para>
/// </summary>
public sealed class Db2FileSource:IDb2ByteSource,IAsyncDisposable
{
    /// <summary>The size of one kept block of the file.</summary>
    public const int BlockSize=256<<10;
    /// <summary>The most blocks kept at once (least recently used goes first).</summary>
    public const int MaxBlocks=4;

    private readonly FileReadStream stream;
    private readonly SemaphoreSlim gate=new(1,1);
    // Most recently used first. Guarded by the gate.
    private readonly List<(long Index,byte[] Bytes,int Length)> blocks=new(MaxBlocks);
    private readonly HashSet<long> unkept=[];
    private bool disposed;

    private Db2FileSource(FileReadStream stream,IReadOnlyList<Db2Hole> holes)
    {this.stream=stream;Holes=holes;Length=stream.Length;}

    public long Length{get;}
    public IReadOnlyList<Db2Hole> Holes{get;}

    /// <summary>
    /// Opens <paramref name="file"/> on <paramref name="files"/>. On a layer 4 state with no stream, <see cref="Db2SourceOpen.Source"/>
    /// is null and the state says why; a file wholly in holes still opens (the reader then refuses it).
    /// </summary>
    public static async Task<Db2SourceOpen> OpenAsync(IBuildFiles files,FileRef file,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var opened=await files.OpenReadAsync(file,token).ConfigureAwait(false);
        if(opened.Stream is not { } stream)
            return new(StateOf(opened.State),null,opened.Code,opened.Message,opened.ObservedAt);
        try
        {
            var holes=stream.Holes.Select(hole=>new Db2Hole(hole.Offset,hole.Length,hole.KeyName)).ToArray();
            return new(Db2OpenState.Open,new Db2FileSource(stream,holes),null,null,null);
        }
        catch{await stream.DisposeAsync().ConfigureAwait(false);throw;}
    }

    public async ValueTask ReadRangeAsync(long offset,Memory<byte> destination,CancellationToken token)
    {
        if(offset<0||offset>Length||destination.Length>Length-offset)
            throw new ArgumentOutOfRangeException(nameof(offset),"The range leaves the file.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(destination.Length==0)return;
            if(destination.Length<BlockSize&&await FromBlocksAsync(offset,destination,token).ConfigureAwait(false))return;
            await ReadStreamAsync(offset,destination,token).ConfigureAwait(false);
        }
        finally{gate.Release();}
    }

    /// <summary>Copies the range out of its blocks (at most two), reading the ones not kept. False when one can't be kept.</summary>
    private async ValueTask<bool> FromBlocksAsync(long offset,Memory<byte> destination,CancellationToken token)
    {
        var end=offset+destination.Length;
        for(var index=offset/BlockSize;index*BlockSize<end;index++)
            if(await BlockAsync(index,token).ConfigureAwait(false) is null)return false;
        for(long at=offset;at<end;)
        {
            var block=(await BlockAsync(at/BlockSize,token).ConfigureAwait(false))!.Value;
            var from=(int)(at-block.Index*BlockSize);var count=(int)Math.Min(block.Length-from,end-at);
            block.Bytes.AsMemory(from,count).CopyTo(destination[(int)(at-offset)..]);
            at+=count;
        }
        return true;
    }

    /// <summary>The kept block, read now when it isn't kept; null when it overlaps a hole or its read failed.</summary>
    private async ValueTask<(long Index,byte[] Bytes,int Length)?> BlockAsync(long index,CancellationToken token)
    {
        for(var i=0;i<blocks.Count;i++)
        {
            if(blocks[i].Index!=index)continue;
            var hit=blocks[i];
            if(i>0){blocks.RemoveAt(i);blocks.Insert(0,hit);}
            return hit;
        }
        if(unkept.Contains(index))return null;
        var start=index*BlockSize;var length=(int)Math.Min(BlockSize,Length-start);
        if(Holes.Any(hole=>hole.Offset<start+length&&hole.Offset+hole.Length>start)){unkept.Add(index);return null;}
        byte[] bytes;
        if(blocks.Count==MaxBlocks){bytes=blocks[^1].Bytes;blocks.RemoveAt(blocks.Count-1);}
        else bytes=new byte[BlockSize];
        try{await ReadStreamAsync(start,bytes.AsMemory(0,length),token).ConfigureAwait(false);}
        catch(Db2SourceException){Array.Clear(bytes);unkept.Add(index);return null;}
        catch{Array.Clear(bytes);throw;}
        var block=(index,bytes,length);
        blocks.Insert(0,block);
        return block;
    }

    private async ValueTask ReadStreamAsync(long offset,Memory<byte> destination,CancellationToken token)
    {
        stream.Position=offset;
        for(var filled=0;filled<destination.Length;)
        {
            int read;
            try{read=await stream.ReadAsync(destination[filled..],token).ConfigureAwait(false);}
            catch(FileHoleException hole)
            {throw new Db2SourceException(Db2OpenState.Protected,"DB2_SOURCE_PROTECTED",hole.Message);}
            catch(FileReadException failed)
            {throw new Db2SourceException(StateOf(failed.State),failed.Code,failed.Message,failed.ObservedAt);}
            if(read==0)throw new Db2SourceException(Db2OpenState.Corrupt,"DB2_SOURCE_SHORT","The file ended before its declared length.");
            filled+=read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if(disposed)return;
            disposed=true;
            // The blocks can hold decrypted bytes.
            foreach(var block in blocks)Array.Clear(block.Bytes);
            blocks.Clear();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        finally{gate.Release();}
    }

    // A failure that claims bytes (Complete or Partial) or a state this code doesn't know is Corrupt: never Open, never a throw.
    internal static Db2OpenState StateOf(FileReadState state)=>state switch
    {
        FileReadState.Protected=>Db2OpenState.Protected,
        FileReadState.UnknownFile=>Db2OpenState.UnknownFile,
        FileReadState.NotInLocale=>Db2OpenState.NotInLocale,
        FileReadState.NotInEncoding=>Db2OpenState.NotInEncoding,
        FileReadState.Missing=>Db2OpenState.Missing,
        FileReadState.NeedsNetwork=>Db2OpenState.NeedsNetwork,
        FileReadState.Unavailable=>Db2OpenState.Unavailable,
        FileReadState.Corrupt=>Db2OpenState.Corrupt,
        FileReadState.Unsupported=>Db2OpenState.Unsupported,
        FileReadState.WrongKey=>Db2OpenState.WrongKey,
        FileReadState.StoreFailed=>Db2OpenState.StoreFailed,
        FileReadState.TooLarge=>Db2OpenState.TooLarge,
        _=>Db2OpenState.Corrupt,
    };
}

/// <summary>The outcome of <see cref="Db2FileSource.OpenAsync"/>. <see cref="Source"/> is set only when <see cref="State"/> is Open.</summary>
public sealed record Db2SourceOpen(Db2OpenState State,Db2FileSource? Source,string? Code,string? Message,DateTimeOffset? ObservedAt);

/// <summary>
/// The outcome of <see cref="Db2Reader.OpenAsync(IBuildFiles,FileRef,DbdDocument,string,Db2Limits,CancellationToken)"/>.
/// <see cref="Table"/> is set only when <see cref="State"/> is Open, and reads its bytes through this result's source:
/// dispose the result when done with the table. Row reads after open throw <see cref="Db2FormatException"/> or
/// <see cref="Db2SourceException"/>.
/// </summary>
public sealed class Db2Open:IAsyncDisposable
{
    private readonly Db2FileSource? source;

    internal Db2Open(Db2OpenState state,Db2Table? table,Db2FileSource? source,string? code,string? message,DateTimeOffset? observedAt,
        IReadOnlyList<Db2Hole>? holes=null)
    {State=state;Table=table;this.source=source;Code=code;Message=message;ObservedAt=observedAt;Holes=holes??[];}

    public Db2OpenState State{get;}
    public Db2Table? Table{get;}
    public string? Code{get;}
    public string? Message{get;}
    public DateTimeOffset? ObservedAt{get;}
    /// <summary>The file's holes, as layer 4 listed them, whenever its stream opened (also when the open then failed); else empty.</summary>
    public IReadOnlyList<Db2Hole> Holes{get;}

    public ValueTask DisposeAsync()=>source?.DisposeAsync()??ValueTask.CompletedTask;
}

public static partial class Db2Reader
{
    /// <summary>
    /// Opens a DB2 table straight from a layer 4 handle. Never throws for the file or its bytes: a layer 4 state, a read
    /// failure during open and a reader refusal (<see cref="Db2OpenState.Rejected"/>) all come back as the state. Throws only
    /// for bad arguments, a disposed handle and cancellation. Holes need <see cref="Db2Limits.AllowPartialEncryptedSections"/>.
    /// </summary>
    public static async Task<Db2Open> OpenAsync(IBuildFiles files,FileRef file,DbdDocument definition,string buildVersion,
        Db2Limits limits,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(files);ArgumentNullException.ThrowIfNull(definition);ArgumentNullException.ThrowIfNull(limits);
        var opened=await Db2FileSource.OpenAsync(files,file,token).ConfigureAwait(false);
        if(opened.Source is not { } source)return new(opened.State,null,null,opened.Code,opened.Message,opened.ObservedAt);
        try
        {
            if(source.Length>0&&source.Holes.Sum(hole=>hole.Length)==source.Length)
            {
                await source.DisposeAsync().ConfigureAwait(false);
                return new(Db2OpenState.Protected,null,null,"DB2_SOURCE_PROTECTED",
                    "Every byte of the file needs TACT key "+string.Join(", ",source.Holes.Select(hole=>hole.Name).Distinct())+".",null,source.Holes);
            }
            var table=await OpenCoreAsync(source,definition,buildVersion,limits,token).ConfigureAwait(false);
            return new(Db2OpenState.Open,table,source,null,null,null,source.Holes);
        }
        catch(Exception failure) when(failure is Db2FormatException or Db2SourceException)
        {
            await source.DisposeAsync().ConfigureAwait(false);
            return failure is Db2SourceException read
                ?new(read.State,null,null,read.Code,read.Message,read.ObservedAt,source.Holes)
                :new(Db2OpenState.Rejected,null,null,((Db2FormatException)failure).Code,failure.Message,null,source.Holes);
        }
        catch{await source.DisposeAsync().ConfigureAwait(false);throw;}
    }
}
