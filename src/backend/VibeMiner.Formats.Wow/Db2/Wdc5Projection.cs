using VibeMiner.Protocols.Tact;

namespace VibeMiner.Formats.Wow;

public static partial class Wdc5TableReader
{
    // Source authentication belongs to the caller. These checks admit only a bounded
    // structural subset: no unavailable plaintext may masquerade as padding or a row.
    private static BlteUnavailableRange[] CaptureUnavailable(Stream source,long length,Wdc5Limits limits,CancellationToken token)
    {
        if(source is not IBlteProjectionSource projection)return [];
        var declared=projection.UnavailableRanges;
        if(declared is null||declared.Count>128)throw Fail("PROJECTION_INVALID","Projection hole metadata exceeds the supported chunk bound.");
        var result=new BlteUnavailableRange[declared.Count];var ordinals=new HashSet<int>();long after=0;
        for(var index=0;index<result.Length;index++)
        {
            token.ThrowIfCancellationRequested();var hole=declared[index];
            if(hole is null||hole.Offset<after||hole.ByteLength<=0||hole.Offset>length||hole.ByteLength>length-hole.Offset
                ||hole.ChunkOrdinal is <0 or >=65_536||!ordinals.Add(hole.ChunkOrdinal))
                throw Fail("PROJECTION_INVALID","Projection holes must be bounded, ordered, disjoint original chunk ranges.");
            result[index]=hole;after=hole.Offset+hole.ByteLength;
        }
        if(result.Length>0&&!limits.AllowPartialEncryptedSections)
            throw Fail("ENCRYPTED","A partial plaintext source requires explicit partial-section admission.");
        return result;
    }

    private static void ValidateUnavailable(IReadOnlyList<BlteUnavailableRange> holes,IReadOnlyList<Section> sections,
        long prefixEnd,CancellationToken token)
    {
        foreach(var hole in holes)
        {
            token.ThrowIfCancellationRequested();
            // Deliberately require a single keyed body, even for adjacent keyed spans.
            // Key-name equality is not a universal BLTE-to-WDC5 format invariant.
            if(hole.Offset<prefixEnd||!sections.Any(section=>section.Key!=0&&hole.Offset>=section.Offset
                &&hole.Offset<section.End&&hole.ByteLength<=section.End-hole.Offset))
                throw Fail("PROJECTION_UNSUPPORTED","Unavailable plaintext must lie wholly inside one excluded keyed-section body.");
        }
    }
}
