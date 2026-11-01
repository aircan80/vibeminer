using System.Buffers.Binary;

namespace VibeMiner.Formats;

public static partial class Db2Reader
{
    internal enum FieldSource { Physical, RecordId, Relationship }
    private sealed record SectionTail(long Strings,long Ids,long Copies,long Maps,long MapIds,long Relationships,long End);
    internal sealed record RelationshipData(uint[] Values,bool[] Present,uint MinimumBits,uint MaximumBits);
    private delegate void RecordAction(ReadOnlySpan<byte> record,int ordinal);
    private static readonly Storage RelationshipStorage=new(0,32,0,0,0,0,0,0,0);
    private static SectionTail Tail(Section section,bool sparse,bool secondary,int recordSize)
    {
        var strings=checked(section.Offset+section.Count*(long)recordSize);
        var ids=sparse?section.RecordsEnd:checked(strings+section.Strings);
        var copies=checked(ids+section.IdBytes);var maps=checked(copies+section.Copies*8L);
        var afterMaps=checked(maps+section.MapCount*6L);
        var mapIds=secondary?afterMaps:checked(afterMaps+section.Relations);
        var relations=secondary?checked(afterMaps+section.MapCount*4L):afterMaps;
        return new(strings,ids,copies,maps,mapIds,relations,checked(afterMaps+section.MapCount*4L+section.Relations));
    }
    private static long ReserveRelationships(IReadOnlyList<Section> sections,Db2Limits limits,long charged,bool hasRelation)
    {
        long bytes=0,capacity=0,state=0,scratch=0;
        foreach(var section in sections)
        {
            if(section.Relations!=0)
            {
                if(section.Relations<12||(section.Relations-12)%8!=0)throw Fail("INVALID","A relationship block needs a complete three-word header and whole pairs.");
                bytes=checked(bytes+section.Relations);capacity=checked(capacity+(section.Relations-12)/8);
                // Conservatively charge even protected declarations, without reading their unavailable pair headers.
                // D292: plus 4 bytes per row for the extra pairs its readable copies allow.
                state=checked(state+section.Count*9L+128);scratch=Math.Max(scratch,Math.Min(65536L,section.Relations));
            }
            scratch=Math.Max(scratch,Math.Min(65536L,Math.Max(section.Count*14L,section.Copies*8L)));
        }
        if(bytes>limits.MaxRelationshipBytes||capacity>limits.MaxRelationshipEntries)throw Fail("LIMIT","Aggregate relationship bytes/entry capacity exceed policy.");
        if(hasRelation)charged=checked(charged+state+capacity*8L+sections.Sum(s=>s.Count*16L)+scratch);
        if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","Relationship state and bounded suffix scratch exceed the aggregate index reservation.");
        return charged;
    }
    private static void ValidateRelationshipSchema(IReadOnlyList<BoundField> fields,uint lookups,bool empty)
    {
        var relation=fields.SingleOrDefault(field=>field.Field.RelationStorage);
        if(relation is null&&lookups!=0)throw Fail("UNSUPPORTED","A native relationship column has no selected schema binding.");
        if(relation?.Source==FieldSource.Relationship&&lookups!=1&&!empty)
            throw Fail("UNSUPPORTED","External relationship values require a declared native lookup column.");
    }
    private static string RelationshipNumber(uint value,DbdField field)=>Number(value,RelationshipStorage,field);
    /// <summary>The issue code on an external relation cell whose row has no pair: the row has no parent, never 0 (D291).</summary>
    public const string RelationAbsent="DB2_RELATION_ABSENT";
    // D291: a row with no pair, or in a section with no block, has no parent: "" plus a RelationAbsent issue, never a default.
    private static void AddExternalRelationship(List<string> values,ref List<Db2TextIssue>? issues,RelationshipData?[] maps,int section,int local,DbdField field)
    {
        var value=ExternalRelationship(maps,section,local,field);
        if(value is null)(issues??=[]).Add(new(values.Count,RelationAbsent,""));
        values.Add(value??"");
    }
    private static string? ExternalRelationship(RelationshipData?[] maps,int section,int local,DbdField field)
    {
        var map=maps[section];
        if(map is null||!map.Present[local])return null;
        // D290: an unverified name reads the stored u32 parent; when every parent fits the DBD width this is the DBD's own text.
        return field.Column.VerifiedName?RelationshipNumber(map.Values[local],field):map.Values[local].ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    // D290: an unverified external relation keeps raw evidence for the whole column when any readable parent doesn't fit
    // the DBD width and sign. Chosen once every relationship block is read, before Header.
    private static bool ExternalRelationNeedsRaw(IReadOnlyList<BoundField> fields,RelationshipData?[] maps,CancellationToken token)
    {
        var relation=fields.SingleOrDefault(field=>field.Field.RelationStorage);
        if(relation is null||relation.Source!=FieldSource.Relationship||relation.Field.Column.VerifiedName)return false;
        var width=relation.Field.IntegerBits??32;
        var maximum=relation.Field.Unsigned==true?(width==32?uint.MaxValue:(1u<<width)-1):(1u<<(width-1))-1;
        foreach(var map in maps)
        {
            if(map is null)continue;token.ThrowIfCancellationRequested();
            for(var i=0;i<map.Values.Length;i++)if(map.Present[i]&&map.Values[i]>maximum)return true;
        }
        return false;
    }
    private static void CheckInlineRelationship(RelationshipData?[] maps,int section,int local,DbdField field,Storage storage,ulong raw,string value)
    {
        var map=maps[section];
        if(map is null||!map.Present[local])return;
        // D290: an unverified name compares the pair's u32 parent with the integer the row emits, as a u32, never by text.
        // A raw column emits the physical value: kind 5 sign-extended at its width, kinds 0/1 as stored (above u32 can't
        // match), kinds 2/3/4 the stored u32. Any other column emits the value at the DBD width and sign. Relation columns
        // never narrow to low bits (D079), and unverified pallet/common columns narrower than 32 bits are always raw.
        var agrees=field.Column.VerifiedName?RelationshipNumber(map.Values[local],field)==value
            :!UsesRawPhysical(storage,field)?map.Values[local]==EmittedU32(raw,storage,field)
            :storage.Kind==5?map.Values[local]==unchecked((uint)Signed(raw,storage.Width))
            :raw<=uint.MaxValue&&map.Values[local]==(uint)raw;
        if(!agrees)throw Fail("SCHEMA_MISMATCH","The native relationship accelerator disagrees with its physical origin value.");
    }
    // The integer Number emits for a non-raw integer column, as a u32: kind 5 sign-extended, then the DBD width and sign.
    // Number has already refused any value that doesn't fit, so the masked bits are exactly what was emitted.
    private static uint EmittedU32(ulong raw,Storage storage,DbdField field)
    {
        var width=field.IntegerBits??32;if(storage.Kind==5)raw=unchecked((ulong)Signed(raw,storage.Width));
        if(width<64)raw&=(1UL<<width)-1;
        return unchecked(field.Unsigned==true?(uint)raw:(uint)Signed(raw,width));
    }
    private static async Task ForRecords(Db2Bytes source,long offset,int count,int stride,RecordAction consume,CancellationToken token)
    {
        if(count==0)return;
        var batch=Math.Max(1,65536/stride);var buffer=new byte[Math.Min(count,batch)*stride];
        if(offset<0||offset>source.Length||count*(long)stride>source.Length-offset)throw Fail("INVALID","A bounded suffix region exceeds the source.");
        for(var first=0;first<count;)
        {
            token.ThrowIfCancellationRequested();var n=Math.Min(batch,count-first);
            await source.Fill(offset+first*(long)stride,buffer.AsMemory(0,n*stride),token);
            for(var i=0;i<n;i++){token.ThrowIfCancellationRequested();consume(buffer.AsSpan(i*stride,stride),first+i);}
            first+=n;await Task.Yield();
        }
    }
    private static async Task<RelationshipData?[]> ReadRelationships(Db2Bytes source,IReadOnlyList<Section> sections,bool sparse,ushort flags,
        int recordSize,IReadOnlyList<BoundField> fields,RowIndexes index,CopyResolution[] resolutions,Func<uint,int,int> resolvePhysicalKey,CancellationToken token)
    {
        var result=new RelationshipData?[sections.Count];var relation=fields.SingleOrDefault(field=>field.Field.RelationStorage);
        if(relation is null)return result;
        for(var s=0;s<sections.Count;s++)
        {
            token.ThrowIfCancellationRequested();var section=sections[s];if(section.Hidden)continue;
            if(section.Relations==0)continue;
            var tail=Tail(section,sparse,(flags&2)!=0,recordSize);var header=await source.ReadAt(tail.Relationships,12,token);
            // D292: Blizzard writes one extra pair per copy row, keyed by the copy's terminal row (its source row for a direct
            // copy), so a block holds at most rows + copies.
            var declared=BinaryPrimitives.ReadUInt32LittleEndian(header);
            if(declared>(long)section.Count+section.Copies)throw Fail("INVALID","The relationship count exceeds the section's rows plus copies.");
            var count=(int)declared;
            if(section.Relations!=12+count*8L)throw Fail("INVALID","The relationship count does not consume its exact declared block.");
            // Extra pairs each key may have: one per readable copy of this section whose terminal row is that key's row.
            var extra=section.Copies==0?null:new int[section.Count];
            for(var c=index.CopyStart[s];extra is not null&&c<index.CopyStart[s+1];c++)
            {
                token.ThrowIfCancellationRequested();var resolution=resolutions[c];
                if(resolution.Failure==CopyFailure.None&&resolution.Row>=0&&index.Rows[resolution.Row].Section==s)extra[index.Rows[resolution.Row].LocalIndex]++;
            }
            var map=new RelationshipData(new uint[section.Count],new bool[section.Count],BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
            result[s]=map;
            await ForRecords(source,tail.Relationships+12,count,8,(pair,_)=>
            {
                var parent=BinaryPrimitives.ReadUInt32LittleEndian(pair);var key=BinaryPrimitives.ReadUInt32LittleEndian(pair[4..]);
                var local=(flags&2)!=0?resolvePhysicalKey(key,s):key<section.Count?(int)key:-1;
                if(local<0||local>=section.Count)throw Fail("INVALID","A relationship key does not identify a physical row in its own section.");
                if(map.Present[local])
                {
                    if(extra is null||extra[local]==0)throw Fail("INVALID","Relationship pairs repeat a physical key.");
                    extra[local]--;
                    if(map.Values[local]!=parent)throw Fail("SCHEMA_MISMATCH","Relationship pairs for one physical key disagree on the parent.");
                    return;
                }
                map.Present[local]=true;map.Values[local]=parent;
                // Validate selected external numeric representation before emitting any rows.
                if(relation.Source==FieldSource.Relationship&&relation.Field.Column.VerifiedName)RelationshipNumber(parent,relation.Field);
            },token);
        }
        return result;
    }
}
