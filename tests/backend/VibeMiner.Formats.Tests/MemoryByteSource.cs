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

    /// <summary>True when some recorded read overlaps [start, end).</summary>
    public bool ReadAny(long start,long end)=>reads.Any(read=>read.Offset<end&&start<read.Offset+read.Length);
    /// <summary>True when every recorded read lies inside one of the given ranges.</summary>
    public bool ReadOnlyWithin(params (long Start,long End)[] ranges)=>
        reads.All(read=>ranges.Any(range=>read.Offset>=range.Start&&read.Offset+read.Length<=range.End));
}
