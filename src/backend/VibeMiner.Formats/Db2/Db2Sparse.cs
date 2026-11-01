using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace VibeMiner.Formats;

public static partial class Db2Reader
{
    // Original strict subset informed by the project's Java ValidatedSparseSection
    // and DBCD e732093f8864240fc5884bd1bba6b02f3dfc0d56 WDC5 reader/writer.
    // The writer records absolute map offsets before each variable record. We
    // require contiguous complete coverage instead of repairing or guessing maps.

    // Returns the record alignment: 8 bytes when any stored field is 64 bits wide, else 4.
    private static int ValidateSparseFields(IReadOnlyList<BoundField> fields,Storage[] storage,CancellationToken token)
    {
        long nominal=0;var alignment=4;string? firstString=null;
        foreach(var binding in fields)
        {
            token.ThrowIfCancellationRequested();if(binding.StorageIndex<0)continue;
            var column=storage[binding.StorageIndex];var width=NoneWidth(column);
            if(column.Kind!=0||column.Additional!=0||width%8!=0||column.FieldOffset!=nominal
                ||(binding.Field.Column.Family is "string" or "locstring")&&width!=32)
                throw Fail("UNSUPPORTED","Sparse fields require ordered byte-aligned NONE storage and scalar inline strings.");
            // After an inline string a 64-bit field may sit behind padding inside the
            // record that the cursor does not model; refuse it until one is observed.
            if(width==64&&firstString is not null)
                throw Fail("UNSUPPORTED",$"Sparse 64-bit field {binding.Field.Column.Name} follows inline string {firstString}; its in-record alignment is not established.");
            if(binding.Field.Column.Family is "string" or "locstring")firstString??=binding.Field.Column.Name;
            nominal=checked(nominal+width/8L*binding.Field.ArrayLength);if(width==64)alignment=8;
        }
        // Header recordSize is nominal. In the observed cohort it exceeds the
        // nominal field sum; that does not establish padding in variable records.
        // Records end on a boundary of their widest stored field, at least four
        // bytes (D084): 12.0.1.66220 ItemSparse has a 64-bit field and pads to 8,
        // files without one pad to 4. Chosen from storage once, never from rows.
        // ABF517CD stores its 64-bit AllowableRace before every string.
        return alignment;
    }

    private static async Task<RowIndexes> IndexSparse(Db2Bytes decoded,IReadOnlyList<Section> sections,Db2Limits limits,
        Dictionary<uint,int> encryptedIds,int nominalRecordSize,ushort flags,CancellationToken token)
    {
        var (rows,rowIndex,copies,copyIndex)=IndexStorage(sections);var rowCount=0;var copyCount=0;
        var rowStart=new int[sections.Count+1];var copyStart=new int[sections.Count+1];
        for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
        {
            rowStart[sectionIndex]=rowCount;copyStart[sectionIndex]=copyCount;
            token.ThrowIfCancellationRequested();var section=sections[sectionIndex];if(section.Key!=0)continue;
            var tail=Tail(section,true,(flags&2)!=0,nominalRecordSize);long next=section.Offset;
            // Three bounded named regions, never a whole suffix containing a duplicate relationship block.
            for(var first=0;first<section.Count;)
            {
                token.ThrowIfCancellationRequested();var count=Math.Min(4096,section.Count-first);
                var ids=await decoded.ReadAt(tail.Ids+first*4L,count*4,token);
                var maps=await decoded.ReadAt(tail.Maps+first*6L,count*6,token);
                var mapIds=await decoded.ReadAt(tail.MapIds+first*4L,count*4,token);
                for(var n=0;n<count;n++)
                {
                    token.ThrowIfCancellationRequested();var index=first+n;var id=BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(n*4));
                    var mapId=BinaryPrimitives.ReadUInt32LittleEndian(mapIds.AsSpan(n*4));
                    var offset=BinaryPrimitives.ReadUInt32LittleEndian(maps.AsSpan(n*6));var size=BinaryPrimitives.ReadUInt16LittleEndian(maps.AsSpan(n*6+4));
                    if(id!=mapId)throw Fail("INVALID","Sparse external IDs and map IDs disagree.");
                    if(size==0||offset!=next||size>section.RecordsEnd-next)throw Fail("INVALID","Sparse maps must exactly cover the record area with positive contiguous absolute spans.");
                    if(size>limits.MaxRecordBytes)throw Fail("LIMIT","A sparse variable record exceeds its byte bound.");
                    if(encryptedIds.ContainsKey(id))throw Fail("INVALID","A sparse readable record repeats an encrypted-section identity.");
                    if(!rowIndex.TryAdd(id,rowCount))throw Fail("INVALID","Sparse readable records repeat an identity.");
                    rows[rowCount++]=new(offset,size,sectionIndex,0,index,id);
                    next+=size;
                }
                first+=count;await Task.Yield();
            }
            if(next!=section.RecordsEnd)throw Fail("INVALID","Sparse maps leave undeclared record-area bytes.");
            await ForRecords(decoded,tail.Copies,section.Copies,8,(row,_)=>
            {
                var target=BinaryPrimitives.ReadUInt32LittleEndian(row);var source=BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                if(encryptedIds.ContainsKey(target))throw Fail("INVALID","A sparse readable copy target repeats an encrypted-section identity.");
                if(!copyIndex.TryAdd(target,copyCount))throw Fail("INVALID","Sparse copies repeat a target identity.");
                copies[copyCount++]=new(target,source,sectionIndex);
            },token);
        }
        rowStart[sections.Count]=rowCount;copyStart[sections.Count]=copyCount;
        if(copies.Any(copy=>rowIndex.ContainsKey(copy.Target)))throw Fail("INVALID","Sparse copy targets overlap readable physical identities.");
        return new(rows,rowIndex,rowStart,copies,copyIndex,copyStart);
    }

    /// <summary>The raw-text evidence emitted by one read call, charged against its aggregate cap.</summary>
    internal sealed class TextBudget{public long Bytes;public long Count;}
    private static void ChargeText(IReadOnlyList<Db2TextIssue>? issues,TextBudget budget,Db2Limits limits,CancellationToken token)
    {
        if(issues is null)return;
        foreach(var issue in issues)
        {
            token.ThrowIfCancellationRequested();var bytes=issue.RawBytesHex.Length/2;
            if(bytes>limits.MaxRawTextEvidenceBytes-budget.Bytes)throw Fail("LIMIT","Sparse raw text evidence exceeds its aggregate emitted-byte bound.");
            budget.Bytes+=bytes;budget.Count++;
        }
    }

    private static async Task<(IReadOnlyList<string> Values,IReadOnlyList<Db2TextIssue>? Issues)> DecodeSparseValues(Db2Bytes decoded,Location location,uint id,
        IReadOnlyList<BoundField> fields,Storage[] storage,Db2Limits limits,CancellationToken token,RelationshipData?[] relationships,bool validateRelation,int alignment)
    {
        var record=await decoded.ReadAt(location.Offset,location.ByteLength,token);var values=new List<string>();var cursor=0;List<Db2TextIssue>? issues=null;
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
        // The pinned WDC5 writer rounds Sparse records to a four-byte boundary;
        // records with a 64-bit field round to eight. Admit only that minimal zero
        // suffix; nominal header-size gaps never establish additional padding or
        // change field positions within a record.
        if(cursor!=record.Length&&(record.Length!=checked((cursor+alignment-1)&~(alignment-1))||record.AsSpan(cursor).IndexOfAnyExcept((byte)0)>=0))
            throw Fail("UNSUPPORTED",string.Create(CultureInfo.InvariantCulture,
                $"Sparse record {location.PhysicalId} has {record.Length-cursor} bytes after its fields, not the minimal zero padding to a {alignment}-byte record boundary."));
        return (values.AsReadOnly(),issues?.AsReadOnly());
    }
}
