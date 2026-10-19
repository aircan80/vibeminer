using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace VibeMiner.Formats.Wow;

public static partial class Wdc5TableReader
{
    // Original strict subset informed by the project's Java ValidatedSparseSection
    // and DBCD e732093f8864240fc5884bd1bba6b02f3dfc0d56 WDC5 reader/writer.
    // The writer records absolute map offsets before each variable record. We
    // require contiguous complete coverage instead of repairing or guessing maps.
    private sealed record SparseLocation(long Offset,int ByteLength,int Section,int LocalIndex,uint PhysicalId);

    private static void ValidateSparseFields(IReadOnlyList<BoundField> fields,Storage[] storage,CancellationToken token)
    {
        long nominal=0;
        foreach(var binding in fields)
        {
            token.ThrowIfCancellationRequested();if(binding.StorageIndex<0)continue;
            var column=storage[binding.StorageIndex];var width=NoneWidth(column);
            if(column.Kind!=0||column.Additional!=0||width%8!=0||column.FieldOffset!=nominal
                ||(binding.Field.Column.Family is "string" or "locstring")&&width!=32)
                throw Fail("UNSUPPORTED","Sparse fields require ordered byte-aligned NONE storage and scalar inline strings.");
            nominal=checked(nominal+width/8L*binding.Field.ArrayLength);
        }
        // Header recordSize is nominal. In the observed cohort it exceeds the
        // nominal field sum; that does not establish padding in variable records.
    }

    private static async Task<Wdc5Completion> ReadSparse(Stream decoded,IReadOnlyList<Section> sections,
        IReadOnlyList<BoundField> fields,Storage[] storage,IReadOnlyList<Db2Column> columns,HashSet<uint> encryptedIds,
        uint schema,uint tableHash,uint layoutHash,long declared,long declaredCopies,int nominalRecordSize,
        int excludedSections,long excluded,long excludedCopies,long length,Wdc5Limits limits,IWdc5Consumer consumer,CancellationToken token,ushort flags)
    {
        var locations=new Dictionary<uint,SparseLocation>();var copies=new Dictionary<uint,uint>();
        for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
        {
            token.ThrowIfCancellationRequested();var section=sections[sectionIndex];if(section.Key!=0)continue;
            var tail=Tail(section,true,(flags&2)!=0,nominalRecordSize);long next=section.Offset;
            // Three bounded named regions, never a whole suffix containing a duplicate relationship block.
            for(var first=0;first<section.Count;)
            {
                token.ThrowIfCancellationRequested();var count=Math.Min(4096,section.Count-first);
                var ids=await ReadAt(decoded,tail.Ids+first*4L,count*4,token);
                var maps=await ReadAt(decoded,tail.Maps+first*6L,count*6,token);
                var mapIds=await ReadAt(decoded,tail.MapIds+first*4L,count*4,token);
                for(var n=0;n<count;n++)
                {
                    token.ThrowIfCancellationRequested();var index=first+n;var id=BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(n*4));
                    var mapId=BinaryPrimitives.ReadUInt32LittleEndian(mapIds.AsSpan(n*4));
                    var offset=BinaryPrimitives.ReadUInt32LittleEndian(maps.AsSpan(n*6));var size=BinaryPrimitives.ReadUInt16LittleEndian(maps.AsSpan(n*6+4));
                    if(id!=mapId)throw Fail("INVALID","Sparse external IDs and map IDs disagree.");
                    if(size==0||offset!=next||size>section.RecordsEnd-next)throw Fail("INVALID","Sparse maps must exactly cover the record area with positive contiguous absolute spans.");
                    if(size>limits.MaxRecordBytes)throw Fail("LIMIT","A sparse variable record exceeds its byte bound.");
                    if(!locations.TryAdd(id,new(offset,size,sectionIndex,index,id)))throw Fail("INVALID","Sparse readable records repeat an identity.");
                    next+=size;
                }
                first+=count;await Task.Yield();
            }
            if(next!=section.RecordsEnd)throw Fail("INVALID","Sparse maps leave undeclared record-area bytes.");
            await ForRecords(decoded,tail.Copies,section.Copies,8,(row,_)=>
            {
                var target=BinaryPrimitives.ReadUInt32LittleEndian(row);var source=BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                if(!copies.TryAdd(target,source))throw Fail("INVALID","Sparse copies repeat a target identity.");
            },token);
        }
        if(copies.Keys.Any(locations.ContainsKey))throw Fail("INVALID","Sparse copy targets overlap readable physical identities.");
        var relationships=await ReadRelationships(decoded,sections,true,flags,nominalRecordSize,fields,
            (key,section)=>locations.TryGetValue(key,out var location)&&location.Section==section?location.LocalIndex:-1,token);
        await consumer.HeaderAsync(new(schema,tableHash.ToString("X8",CultureInfo.InvariantCulture),layoutHash.ToString("X8",CultureInfo.InvariantCulture),
            declared,declaredCopies,nominalRecordSize,columns),token);token.ThrowIfCancellationRequested();
        long physicalRows=0,copyRows=0,textIssueCount=0,rawTextBytes=0;
        void ChargeText(IReadOnlyList<Db2TextIssue>? issues)
        {
            if(issues is null)return;
            foreach(var issue in issues)
            {
                token.ThrowIfCancellationRequested();var bytes=issue.RawBytesHex.Length/2;
                if(bytes>limits.MaxRawTextEvidenceBytes-rawTextBytes)throw Fail("LIMIT","Sparse raw text evidence exceeds its aggregate emitted-byte bound.");
                rawTextBytes+=bytes;textIssueCount++;
            }
        }
        foreach(var (id,location) in locations)
        {
            var (values,issues)=await DecodeSparseValues(decoded,location,id,fields,storage,limits,token,relationships,true);ChargeText(issues);
            await consumer.RowAsync(new(id,false,null,values,issues),token);token.ThrowIfCancellationRequested();physicalRows++;
        }
        foreach(var (id,source) in copies)
        {
            var current=source;var seen=new HashSet<uint>{id};SparseLocation? location=null;
            for(var depth=0;depth<limits.MaxCopyDepth;depth++)
            {
                token.ThrowIfCancellationRequested();if(!seen.Add(current))throw Fail("INVALID","Sparse copy declarations contain a cycle.");
                if(locations.TryGetValue(current,out location))break;
                if(encryptedIds.Contains(current)){location=null;break;}
                if(!copies.TryGetValue(current,out current))throw Fail("INVALID","A sparse copy source is neither readable nor declared protected.");
            }
            if(location is null)
            {
                if(encryptedIds.Contains(current)){excludedCopies++;continue;}
                throw Fail("LIMIT","Sparse copy resolution exceeds its chain bound.");
            }
            var (values,issues)=await DecodeSparseValues(decoded,location,id,fields,storage,limits,token,relationships,false);ChargeText(issues);
            await consumer.RowAsync(new(id,true,source,values,issues),token);token.ThrowIfCancellationRequested();copyRows++;
        }
        decoded.Position=length;var probe=new byte[1];
        if(await decoded.ReadAsync(probe,token)!=0||decoded.Length!=length)throw Fail("INVALID","The sparse WDC5 source changed during parsing.");
        return new(declared,physicalRows,copyRows,excludedSections,excluded,excludedCopies,
            excludedSections==0&&excludedCopies==0?"complete":"unencryptedSections",length,textIssueCount);
    }

    private static async Task<(IReadOnlyList<string> Values,IReadOnlyList<Db2TextIssue>? Issues)> DecodeSparseValues(Stream decoded,SparseLocation location,uint id,
        IReadOnlyList<BoundField> fields,Storage[] storage,Wdc5Limits limits,CancellationToken token,RelationshipData?[] relationships,bool validateRelation)
    {
        var record=await ReadAt(decoded,location.Offset,location.ByteLength,token);var values=new List<string>();var cursor=0;List<Db2TextIssue>? issues=null;
        foreach(var binding in fields)
        {
            token.ThrowIfCancellationRequested();var field=binding.Field;
            if(binding.Source==FieldSource.RecordId){values.Add(id.ToString(CultureInfo.InvariantCulture));continue;}
            if(binding.Source==FieldSource.Relationship){values.Add(ExternalRelationship(relationships,location.Section,location.LocalIndex,field));continue;}
            var column=storage[binding.StorageIndex];var width=NoneWidth(column);
            for(var element=0;element<field.ArrayLength;element++)
            {
                token.ThrowIfCancellationRequested();
                if(field.Column.Family is "string" or "locstring")
                {
                    var first=cursor;
                    while(cursor<record.Length&&record[cursor]!=0)
                    {
                        if(cursor-first>=limits.MaxStringBytes)throw Fail("LIMIT","A sparse inline string exceeds its byte bound.");
                        if((cursor&255)==0)token.ThrowIfCancellationRequested();cursor++;
                    }
                    if(cursor==record.Length)throw Fail("INVALID","A sparse inline string is not terminated inside its own record.");
                    try{values.Add(Utf8.GetString(record,first,cursor-first));}
                    catch(DecoderFallbackException)
                    {
                        if(!limits.AllowRawTextEvidence)throw Fail("INVALID","A sparse inline string is not strict UTF-8.");
                        // Preserve exactly the complete pre-NUL bytes. An empty value
                        // is a typed issue placeholder, never a decoded empty string.
                        (issues??=[]).Add(new(values.Count,"DB2_TEXT_ENCODING_INVALID",Convert.ToHexStringLower(record.AsSpan(first,cursor-first))));
                        values.Add("");
                    }
                    cursor++;
                }
                else
                {
                    var bytes=width/8;
                    if(bytes>record.Length-cursor)throw Fail("INVALID","A sparse trailing field exceeds its own variable record.");
                    var raw=Bits(record,cursor*8L,width);cursor+=bytes;var value=Number(raw,column,field);
                    if(validateRelation&&field.RelationStorage)CheckInlineRelationship(relationships,location.Section,location.LocalIndex,field,value);values.Add(value);
                }
            }
        }
        // The pinned WDC5 writer rounds Sparse records to a four-byte boundary.
        // Admit only that minimal zero suffix; nominal header-size gaps never
        // establish additional padding or change field positions within a record.
        if(cursor!=record.Length&&(record.Length!=checked((cursor+3)&~3)||record.AsSpan(cursor).IndexOfAnyExcept((byte)0)>=0))
            throw Fail("UNSUPPORTED","Sparse fields leave bytes other than the minimal zero alignment to a four-byte record boundary.");
        return (values.AsReadOnly(),issues?.AsReadOnly());
    }
}
