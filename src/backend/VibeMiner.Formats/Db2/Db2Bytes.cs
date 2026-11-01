namespace VibeMiner.Formats;

public static partial class Db2Reader
{
    /// <summary>Bounded reads over the immutable source. Never forwards a read that touches a hole.</summary>
    internal sealed class Db2Bytes(IDb2ByteSource source,long length,Db2Hole[] holes,string format)
    {
        public long Length=>length;
        /// <summary>The header version ("WDC3", "WDC4" or "WDC5") that error messages name.</summary>
        public string Format=>format;
        public async Task<byte[]> ReadAt(long offset,int count,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(offset<0||count<0||offset>length||count>length-offset)throw Fail("INVALID",format+" data is truncated or addresses outside its source.");
            var bytes=new byte[count];await Fill(offset,bytes,token);
            token.ThrowIfCancellationRequested();return bytes;
        }
        public async ValueTask Fill(long offset,Memory<byte> destination,CancellationToken token)
        {
            if(destination.Length==0)return;
            var last=offset+destination.Length;
            foreach(var hole in holes)
            {
                if(hole.Offset>=last)break;
                // Same code and message the legacy projection stream raised, so both readers fail identically.
                if(hole.Offset+hole.Length>offset)
                    throw new Db2FormatException("BLTE_RANGE_UNAVAILABLE","The requested plaintext range intersects an unavailable encrypted chunk.");
            }
            await source.ReadRangeAsync(offset,destination,token);
        }
    }

    // Source authentication belongs to the caller. These checks admit only a bounded
    // structural subset: no unavailable plaintext may masquerade as padding or a row.
    // Source.Holes is read exactly once: the magic peek and CaptureHoles both use this copy. Null when the list is
    // missing or over the bound, which CaptureHoles refuses; the entries themselves are checked there.
    private static Db2Hole[]? SnapshotHoles(IDb2ByteSource source)
    {
        var declared=source.Holes;
        if(declared is null)return null;
        var count=declared.Count;if(count>128)return null;
        var copy=new Db2Hole[count];for(var index=0;index<count;index++)copy[index]=declared[index];
        return copy;
    }

    private static Db2Hole[] CaptureHoles(Db2Hole[]? declared,long length,Db2Limits limits,CancellationToken token)
    {
        if(declared is null)throw Fail("PROJECTION_INVALID","Projection hole metadata exceeds the supported chunk bound.");
        var result=new Db2Hole[declared.Length];long after=0;
        for(var index=0;index<result.Length;index++)
        {
            token.ThrowIfCancellationRequested();var hole=declared[index];
            if(hole is null||hole.Offset<after||hole.Length<=0||hole.Offset>length||hole.Length>length-hole.Offset
                ||string.IsNullOrEmpty(hole.Name))
                throw Fail("PROJECTION_INVALID","Projection holes must be bounded, ordered, disjoint original chunk ranges.");
            result[index]=hole;after=hole.Offset+hole.Length;
        }
        if(result.Length>0&&!limits.AllowPartialEncryptedSections)
            throw Fail("ENCRYPTED","A partial plaintext source requires explicit partial-section admission.");
        return result;
    }

    private static void ValidateHoles(IReadOnlyList<Db2Hole> holes,IReadOnlyList<Section> sections,
        long prefixEnd,CancellationToken token)
    {
        foreach(var hole in holes)
        {
            token.ThrowIfCancellationRequested();
            // Deliberately require a single keyed body, even for adjacent keyed spans.
            // Key-name equality is not a universal BLTE-to-WDC5 format invariant.
            if(hole.Offset<prefixEnd||!sections.Any(section=>section.Key!=0&&hole.Offset>=section.Offset
                &&hole.Offset<section.End&&hole.Length<=section.End-hole.Offset))
                throw Fail("PROJECTION_UNSUPPORTED","Unavailable plaintext must lie wholly inside one excluded keyed-section body.");
        }
    }
}
