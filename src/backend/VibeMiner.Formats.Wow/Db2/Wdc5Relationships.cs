using System.Buffers.Binary;

namespace VibeMiner.Formats.Wow;

public static partial class Wdc5TableReader
{
    private enum FieldSource { Physical, RecordId, Relationship }
    private sealed record SectionTail(long Strings,long Ids,long Copies,long Maps,long MapIds,long Relationships,long End);
    private sealed record RelationshipData(uint[] Values,bool[] Present,uint MinimumBits,uint MaximumBits);
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
    private static long ReserveRelationships(IReadOnlyList<Section> sections,Wdc5Limits limits,long charged,bool hasRelation)
    {
        long bytes=0,capacity=0,state=0,scratch=0;
        foreach(var section in sections)
        {
            if(section.Relations!=0)
            {
                if(section.Relations<12||(section.Relations-12)%8!=0)throw Fail("INVALID","A relationship block needs a complete three-word header and whole pairs.");
                bytes=checked(bytes+section.Relations);capacity=checked(capacity+(section.Relations-12)/8);
                // Conservatively charge even protected declarations, without reading their unavailable pair headers.
                state=checked(state+section.Count*5L+128);scratch=Math.Max(scratch,Math.Min(65536L,section.Relations));
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
    private static string ExternalRelationship(RelationshipData?[] maps,int section,int local,DbdField field)
    {
        var map=maps[section];
        if(map is null||!map.Present[local])throw Fail("UNSUPPORTED","Readable external relationship coverage is incomplete; no default parent was supplied.");
        return RelationshipNumber(map.Values[local],field);
    }
    private static void CheckInlineRelationship(RelationshipData?[] maps,int section,int local,DbdField field,string value)
    {
        var map=maps[section];
        if(map is null||!map.Present[local])return;
        if(!field.Column.VerifiedName)
            throw Fail("UNSUPPORTED","An accelerator on an unverified inline relationship needs a separately admitted raw comparison policy.");
        if(RelationshipNumber(map.Values[local],field)!=value)
            throw Fail("SCHEMA_MISMATCH","The native relationship accelerator disagrees with its physical origin value.");
    }
    private static async Task ForRecords(Stream source,long offset,int count,int stride,RecordAction consume,CancellationToken token)
    {
        if(count==0)return;
        var batch=Math.Max(1,65536/stride);var buffer=new byte[Math.Min(count,batch)*stride];
        if(offset<0||offset>source.Length||count*(long)stride>source.Length-offset)throw Fail("INVALID","A bounded suffix region exceeds the source.");
        source.Position=offset;
        for(var first=0;first<count;)
        {
            token.ThrowIfCancellationRequested();var n=Math.Min(batch,count-first);
            try{await source.ReadExactlyAsync(buffer.AsMemory(0,n*stride),token);}catch(EndOfStreamException){throw Fail("INVALID","A suffix record ended unexpectedly.");}
            for(var i=0;i<n;i++){token.ThrowIfCancellationRequested();consume(buffer.AsSpan(i*stride,stride),first+i);}
            first+=n;await Task.Yield();
        }
    }
    private static async Task<RelationshipData?[]> ReadRelationships(Stream source,IReadOnlyList<Section> sections,bool sparse,ushort flags,
        int recordSize,IReadOnlyList<BoundField> fields,Func<uint,int,int> resolvePhysicalKey,CancellationToken token)
    {
        var result=new RelationshipData?[sections.Count];var relation=fields.SingleOrDefault(field=>field.Field.RelationStorage);
        if(relation is null)return result;
        for(var s=0;s<sections.Count;s++)
        {
            token.ThrowIfCancellationRequested();var section=sections[s];if(section.Key!=0)continue;
            if(section.Relations==0)
            {if(relation.Source==FieldSource.Relationship&&section.Count!=0)throw Fail("UNSUPPORTED","The external relationship section lacks required readable associations.");continue;}
            var tail=Tail(section,sparse,(flags&2)!=0,recordSize);var header=await ReadAt(source,tail.Relationships,12,token);
            var count=Count(BinaryPrimitives.ReadUInt32LittleEndian(header),section.Count,"relationship entries");
            if(section.Relations!=12+count*8L)throw Fail("INVALID","The relationship count does not consume its exact declared block.");
            if(relation.Source==FieldSource.Relationship&&count!=section.Count)throw Fail("UNSUPPORTED","External parent coverage is incomplete for readable physical rows.");
            var map=new RelationshipData(new uint[section.Count],new bool[section.Count],BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)),BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
            result[s]=map;
            await ForRecords(source,tail.Relationships+12,count,8,(pair,_)=>
            {
                var parent=BinaryPrimitives.ReadUInt32LittleEndian(pair);var key=BinaryPrimitives.ReadUInt32LittleEndian(pair[4..]);
                var local=(flags&2)!=0?resolvePhysicalKey(key,s):key<section.Count?(int)key:-1;
                if(local<0||local>=section.Count)throw Fail("INVALID","A relationship key does not identify a physical row in its own section.");
                if(map.Present[local])throw Fail("INVALID","Relationship pairs repeat a physical key.");
                map.Present[local]=true;map.Values[local]=parent;
                // Validate selected external numeric representation before emitting any rows.
                if(relation.Source==FieldSource.Relationship)RelationshipNumber(parent,relation.Field);
            },token);
        }
        return result;
    }
}
