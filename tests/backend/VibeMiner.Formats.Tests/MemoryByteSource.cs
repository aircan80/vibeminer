using System.Buffers.Binary;
using System.Collections.Concurrent;
using VibeMiner.Formats;

namespace VibeMiner.Formats.Tests;

/// <summary>An in-memory DB2 byte source that records every range read and fails any read that touches a hole
/// or leaves the source, so tests can prove what the reader asked for.</summary>
internal sealed class MemoryByteSource(byte[] bytes,IReadOnlyList<Db2Hole>? holes=null):IDb2ByteSource
{
    private readonly ConcurrentQueue<(long Offset,int Length)> reads=new();
    public long Length=>bytes.Length;
    public IReadOnlyList<Db2Hole> Holes{get;}=holes??[];
    public IReadOnlyList<(long Offset,int Length)> Reads=>[..reads];
    public void ClearReads()=>reads.Clear();

    public async ValueTask ReadRangeAsync(long offset,Memory<byte> destination,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(offset<0||offset>bytes.Length||destination.Length>bytes.Length-offset)
            throw new InvalidOperationException($"Read {offset}+{destination.Length} leaves the {bytes.Length}-byte source.");
        foreach(var hole in Holes)
            if(offset<hole.Offset+hole.Length&&hole.Offset<offset+destination.Length)
                throw new InvalidOperationException($"Read {offset}+{destination.Length} touches hole {hole.Name}.");
        reads.Enqueue((offset,destination.Length));
        await Task.Yield();
        bytes.AsMemory((int)offset,destination.Length).CopyTo(destination);
    }

    /// <summary>A section key name for tests: generated from <paramref name="seed"/>, never a real key.</summary>
    public static ulong TestKeyName(int seed)=>(ulong)new Random(seed).NextInt64(1,long.MaxValue);

    /// <summary>A source over a WDC3, WDC4 or WDC5 file whose every nonempty keyed section body (or only those in <paramref name="only"/>) is a hole named by its key,
    /// as layer 4 gives it when the key is unknown. Assumes the sections lie in order, each ending where the next begins
    /// and the last at the end of the file, as the test builders write them.</summary>
    public static MemoryByteSource Hiding(byte[] bytes,params int[] only)
    {
        var fieldsAt=bytes.AsSpan(0,4).SequenceEqual("WDC5"u8)?136:4;var headerBytes=fieldsAt==136?204:72;
        var count=(int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(fieldsAt+64,4));
        var sections=Enumerable.Range(0,count).Select(i=>(Key:BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(headerBytes+i*40,8)),
            Offset:(long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(headerBytes+i*40+8,4)))).ToArray();
        var holes=new List<Db2Hole>();
        for(var i=0;i<count;i++)
        {
            var end=i+1<count?sections[i+1].Offset:bytes.Length;
            if(sections[i].Key!=0&&end>sections[i].Offset&&(only.Length==0||only.Contains(i)))holes.Add(new(sections[i].Offset,end-sections[i].Offset,sections[i].Key.ToString("X16")));
        }
        return new(bytes,holes);
    }

    /// <summary>True when some recorded read overlaps [start, end).</summary>
    public bool ReadAny(long start,long end)=>reads.Any(read=>read.Offset<end&&start<read.Offset+read.Length);
    /// <summary>True when every recorded read lies inside one of the given ranges.</summary>
    public bool ReadOnlyWithin(params (long Start,long End)[] ranges)=>
        reads.All(read=>ranges.Any(range=>read.Offset>=range.Start&&read.Offset+read.Length<=range.End));
}
