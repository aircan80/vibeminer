using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

/// <summary>Original bounded WDC5 fixed and sparse decoder. Caller owns the seekable decoded source.</summary>
// Format research: original Java VibeMiner checkpoint 8f353d046d961108f7acc86bfe1381004467afd5
// (Wdc5HeaderInspector, Wdc5MetadataInspector, FixedSectionIndexParser, FieldValueDecoder,
// RecordBitFields), and DBCD e732093f8864240fc5884bd1bba6b02f3dfc0d56 WDC5Reader.cs.
// Independently authored implementation: no reference parser is a runtime dependency.
// Duplicate geometry checks define a conservative supported subset, not a universal wire rule.
public static partial class Wdc5TableReader
{
    public const string Revision="wdc5-values-v7";
    private sealed record Section(ulong Key,long Offset,int Count,int Strings,long RecordsEnd,int IdBytes,int Relations,int MapCount,int Copies,long End);
    private sealed record Storage(int Offset,int Width,int Additional,uint Kind,uint P1,uint P2,uint P3,short FieldBits,ushort FieldOffset)
    {
        public uint[] Palette=[];
        public Dictionary<uint,uint>? Common;
        public bool VerifiedRawEvidence;
        public bool KeepsLowBits;
    }
    private sealed record Location(long Offset,int Section,int GlobalIndex,int LocalIndex,uint PhysicalId);
    private sealed record BoundField(DbdField Field,int StorageIndex,FieldSource Source=FieldSource.Physical);
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static DiscoveryException Fail(string code,string message)=>new("DB2_"+code,message);

    public static async Task<Wdc5Completion> ReadAsync(Stream decoded,DbdDocument definition,string buildVersion,
        Wdc5Limits limits,IWdc5Consumer consumer,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(decoded);ArgumentNullException.ThrowIfNull(definition);ArgumentNullException.ThrowIfNull(limits);ArgumentNullException.ThrowIfNull(consumer);
        ValidateLimits(limits);token.ThrowIfCancellationRequested();
        var rawRules=SnapshotRawIntegerRules(limits,token);
        if(!decoded.CanRead||!decoded.CanSeek)throw new ArgumentException("A readable seekable caller-owned decoded stream is required.",nameof(decoded));
        var length=decoded.Length;if(length<204)throw Fail("INVALID","The WDC5 mandatory204-byte header is truncated.");
        if(length>limits.MaxDecodedBytes)throw Fail("LIMIT","Decoded DB2 exceeds its source-byte bound.");
        var unavailable=CaptureUnavailable(decoded,length,limits,token);
        var header=await ReadAt(decoded,0,204,token);
        if(!header.AsSpan(0,4).SequenceEqual("WDC5"u8))throw Fail("UNSUPPORTED","Only WDC5 is supported.");
        uint U(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at,4));
        var schema=U(4);if(schema!=5)throw Fail("UNSUPPORTED","Only WDC5 schema version5 is admitted.");
        ValidateSchemaString(header.AsSpan(8,128));
        var records=Count(U(136),limits.MaxRows,"record count");var fieldCount=Count(U(140),limits.MaxFields,"field count");
        var recordSize=Count(U(144),limits.MaxRecordBytes,"record width");var stringTotal=U(148);
        var flags=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(172));var idIndex=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(174));
        if(flags is not (0 or 4 or 5 or 6 or 7))throw Fail("UNSUPPORTED","Only fixed or sparse external-ID WDC5 flags are admitted.");
        var sparse=(flags&1)!=0;
        if(U(176)!=fieldCount||U(184)>1)throw Fail("UNSUPPORTED","Additional lookup columns are unsupported.");
        var sectionCount=Count(U(200),limits.MaxSections,"section count");
        var metadataOnlyEmpty=records==0&&sectionCount==0&&U(188)==0&&U(192)==0&&U(196)==0;
        if(!metadataOnlyEmpty&&U(188)!=fieldCount*24L)throw Fail("UNSUPPORTED","WDC5 field-storage geometry is unsupported.");
        var paletteBytes=Count(U(196),limits.MaxAuxiliaryBytes,"pallet bytes");var commonBytes=Count(U(192),limits.MaxAuxiliaryBytes,"common bytes");
        if((long)paletteBytes+commonBytes>limits.MaxAuxiliaryBytes)throw Fail("LIMIT","WDC5 aggregate auxiliary bytes exceed policy.");
        if(sparse&&(paletteBytes!=0||commonBytes!=0))throw Fail("UNSUPPORTED","Sparse auxiliary compression requires a separate supported profile.");
        var descriptorEnd=checked(204L+sectionCount*40L+fieldCount*(metadataOnlyEmpty?4L:28L));
        var auxiliaryEnd=checked(descriptorEnd+paletteBytes+commonBytes);
        if(auxiliaryEnd>length)throw Fail("INVALID","WDC5 metadata and auxiliary declarations exceed the source.");
        var metadata=await ReadAt(decoded,204,checked((int)(descriptorEnd-204)),token);
        var sections=new List<Section>(sectionCount);long declared=0,copies=0,strings=0,excluded=0,excludedCopies=0;int excludedSections=0;
        for(var i=0;i<sectionCount;i++)
        {
            token.ThrowIfCancellationRequested();var at=i*40;
            uint S(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(at+offset,4));
            var key=BinaryPrimitives.ReadUInt64LittleEndian(metadata.AsSpan(at,8));
            var count=Count(S(12),limits.MaxRows,"section records");var str=Count(S(16),limits.MaxDecodedBytes,"section strings");
            var ids=Count(S(24),limits.MaxDecodedBytes,"section ID bytes");var rel=Count(S(28),limits.MaxDecodedBytes,"relationship bytes");
            var map=Count(S(32),limits.MaxRows,"offset-map records");var copy=Count(S(36),limits.MaxRows,"copy count");
            if(count>0&&recordSize==0)throw Fail("INVALID","Nonempty records have zero width.");
            if(!sparse&&(S(20)!=0||map!=0))throw Fail("UNSUPPORTED","Fixed offset maps require a separate supported profile.");
            if(ids!=((flags&4)!=0?count*4L:0))throw Fail("INVALID","The fixed section ID-list size disagrees with the ID storage mode.");
            if(sparse&&(str!=0||map!=count))throw Fail("UNSUPPORTED","Sparse sections require inline strings and one map entry per physical record.");
            if(sparse&&(S(20)<S(8)||(count==0)!=(S(20)==S(8))))throw Fail("INVALID","Sparse record-area bounds disagree with its physical count.");
            if(rel!=0&&U(184)==0)throw Fail("INVALID","Relationship bytes require a native lookup column.");
            var span=sparse?checked((long)S(20)-S(8)+ids+copy*8L+map*10L+rel):checked(count*(long)recordSize+str+ids+copy*8L+rel);
            var end=checked(S(8)+span);
            if(end>length||span>0&&S(8)<auxiliaryEnd)throw Fail("INVALID","A section lies outside the source or overlaps descriptors.");
            sections.Add(new(key,S(8),count,str,S(20),ids,rel,map,copy,end));declared+=count;copies+=copy;strings+=str;
            if(key!=0){excludedSections++;excluded+=count;excludedCopies+=copy;}
        }
        if(declared!=records||strings!=stringTotal)throw Fail("INVALID","Header and section record/string counts disagree.");
        if(records+copies>limits.MaxRows)throw Fail("LIMIT","Physical plus declared copy rows exceed policy.");
        if(excludedSections>0&&!limits.AllowPartialEncryptedSections)throw Fail("ENCRYPTED","The table declares keyed sections; partial decoding was not requested.");
        long charged=checked(records*96L+copies*64L+paletteBytes*2L+commonBytes);
        if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","DB2 ID/copy/auxiliary work exceeds its index-memory reservation.");
        if(U(180)>recordSize)throw Fail("INVALID","The packed-data byte offset exceeds its physical record.");
        var storage=metadataOnlyEmpty?[]:ReadStorage(metadata,sectionCount,fieldCount,recordSize,U(180),limits,token);
        // An unlisted build may select by layout alone (D080); BindFields still checks every field against the file.
        var selection=definition.SelectFile(buildVersion,U(156).ToString("X8",CultureInfo.InvariantCulture),token);
        if(selection.Blocks.Count!=1)throw Fail(selection.Blocks.Count==0?"SCHEMA_MISSING":"SCHEMA_AMBIGUOUS",selection.Match=="layout"
            ?"The build is not listed and the actual layout does not select one definition block on its version line."
            :"The actual layout and exact build do not select one definition block.");
        var fields=BindFields(selection.Blocks[0],storage,flags,idIndex,recordSize,limits,token,metadataOnlyEmpty?fieldCount:null);
        ApplyRawIntegerRules(fields,storage,U(152),U(156),limits.DefinitionSha256,rawRules,token);
        ValidateRelationshipSchema(fields,U(184),records==0&&copies==0);
        charged=ReserveRelationships(sections,limits,charged,fields.Any(f=>f.Field.RelationStorage));
        if(sparse)ValidateSparseFields(fields,storage,token);
        var columns=Columns(fields,storage,limits);
        if(metadataOnlyEmpty)
        {
            if(stringTotal!=0||descriptorEnd!=length)throw Fail("INVALID","The metadata-only empty table has unsupported payload bytes.");
            await consumer.HeaderAsync(new(schema,U(152).ToString("X8",CultureInfo.InvariantCulture),U(156).ToString("X8",CultureInfo.InvariantCulture),0,0,recordSize,columns),token);
            token.ThrowIfCancellationRequested();decoded.Position=length;
            if(await decoded.ReadAsync(new byte[1],token)!=0||decoded.Length!=length)throw Fail("INVALID","The empty source changed during parsing.");
            return new(0,0,0,0,0,0,"complete",length);
        }
        var aux=await ReadAt(decoded,descriptorEnd,paletteBytes+commonBytes,token);
        LoadAuxiliary(storage,aux,paletteBytes,commonBytes,limits,ref charged,token);
        // Narrowing depends on the pallet and common values, so it is selected once they are loaded, still before Header.
        if(SelectLowBitNarrowing(fields,storage,token))columns=Columns(fields,storage,limits);
        var encryptedIds=new HashSet<uint>();long prefixEnd=auxiliaryEnd;
        foreach(var section in sections.Where(section=>section.Key!=0))
        {
            var size=await ReadAt(decoded,prefixEnd,4,token);var count=Count(BinaryPrimitives.ReadUInt32LittleEndian(size),limits.MaxRows,"encrypted ID count");prefixEnd+=4;
            charged=checked(charged+count*64L);if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","Encrypted-ID metadata exceeds its working-memory bound.");
            var bytes=await ReadAt(decoded,prefixEnd,checked(count*4),token);prefixEnd+=count*4L;
            // Partial rows are joined by record ID, so a protected ID must name exactly one record:
            // never twice across (or within) encrypted lists, never as a readable row or copy below.
            for(var i=0;i<count;i++)
            {
                token.ThrowIfCancellationRequested();
                if(!encryptedIds.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i*4,4))))throw Fail("INVALID","Encrypted-section ID lists repeat a record ID.");
            }
        }
        var endOfFile=prefixEnd;
        var populated=sections.Where(section=>section.End>section.Offset).OrderBy(section=>section.Offset).ToArray();
        foreach(var section in populated)
        {
            token.ThrowIfCancellationRequested();
            if(section.Offset<endOfFile)throw Fail("INVALID","WDC5 section spans overlap each other or their prefix.");
            endOfFile=section.End;
        }
        if(endOfFile!=length)throw Fail("INVALID","The WDC5 source has undeclared trailing bytes.");
        ValidateUnavailable(unavailable,populated,prefixEnd,token);
        var gapStart=prefixEnd;
        foreach(var section in populated)
        {await VerifyGap(decoded,gapStart,section.Offset,token);gapStart=section.End;}
        if(sparse)return await ReadSparse(decoded,sections,fields,storage,columns,encryptedIds,schema,U(152),U(156),records,copies,
            recordSize,excludedSections,excluded,excludedCopies,length,limits,consumer,token,flags);
        var locations=new Dictionary<uint,Location>();var copyMap=new Dictionary<uint,uint>();long global=0;
        for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
        {
            var section=sections[sectionIndex];
            if(section.Key!=0){global+=section.Count;continue;}
            var tail=Tail(section,false,(flags&2)!=0,recordSize);
            void Add(uint id,int row)
            {
                var location=new Location(section.Offset+row*(long)recordSize,sectionIndex,checked((int)(global+row)),row,id);
                if(encryptedIds.Contains(id))throw Fail("INVALID","A readable physical row repeats an encrypted-section record ID.");
                if(!locations.TryAdd(id,location))throw Fail("INVALID","Readable physical rows repeat a record ID.");
            }
            if((flags&4)!=0)
                await ForRecords(decoded,tail.Ids,section.Count,4,(row,index)=>Add(BinaryPrimitives.ReadUInt32LittleEndian(row),index),token);
            else
            {
                var idField=fields.Single(field=>field.Field.IsId);
                for(var row=0;row<section.Count;row++)
                {
                    token.ThrowIfCancellationRequested();var record=await ReadAt(decoded,section.Offset+row*(long)recordSize,recordSize,token);
                    var raw=Raw(record,storage[idField.StorageIndex],idField.Field,0,0);
                    if(raw>uint.MaxValue)throw Fail("INVALID","The inline record ID exceeds unsigned32.");Add((uint)raw,row);
                }
            }
            await ForRecords(decoded,tail.Copies,section.Copies,8,(row,_)=>
            {
                var target=BinaryPrimitives.ReadUInt32LittleEndian(row);var source=BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                if(encryptedIds.Contains(target))throw Fail("INVALID","A readable copy target repeats an encrypted-section record ID.");
                if(!copyMap.TryAdd(target,source))throw Fail("INVALID","Copy declarations repeat a target ID.");
            },token);
            global+=section.Count;
        }
        if(copyMap.Keys.Any(locations.ContainsKey))throw Fail("INVALID","Copy targets overlap readable physical IDs.");
        var relationships=await ReadRelationships(decoded,sections,false,flags,recordSize,fields,
            (key,section)=>locations.TryGetValue(key,out var location)&&location.Section==section?location.LocalIndex:-1,token);
        await consumer.HeaderAsync(new(schema,U(152).ToString("X8",CultureInfo.InvariantCulture),U(156).ToString("X8",CultureInfo.InvariantCulture),records,copies,recordSize,columns),token);token.ThrowIfCancellationRequested();
        long physicalRows=0,copyRows=0;
        foreach(var (id,location) in locations)
        {
            var values=await DecodeValues(decoded,location,id,fields,storage,sections,recordSize,records,limits,token,relationships,true);
            await consumer.RowAsync(new(id,false,null,values),token);token.ThrowIfCancellationRequested();physicalRows++;
        }
        foreach(var (id,source) in copyMap)
        {
            var current=source;var seen=new HashSet<uint>{id};Location? location=null;
            for(var depth=0;depth<limits.MaxCopyDepth;depth++)
            {
                token.ThrowIfCancellationRequested();if(!seen.Add(current))throw Fail("INVALID","Copy declarations contain a cycle.");
                if(locations.TryGetValue(current,out location))break;
                if(encryptedIds.Contains(current)){location=null;break;}
                if(!copyMap.TryGetValue(current,out current))throw Fail("INVALID","A copy source has no readable record or declared encrypted identity.");
            }
            if(location is null)
            {
                if(encryptedIds.Contains(current)){excludedCopies++;continue;}
                throw Fail("LIMIT","Copy resolution exceeds its admitted chain depth.");
            }
            var values=await DecodeValues(decoded,location,id,fields,storage,sections,recordSize,records,limits,token,relationships,false);
            await consumer.RowAsync(new(id,true,source,values),token);token.ThrowIfCancellationRequested();copyRows++;
        }
        decoded.Position=length;var probe=new byte[1];if(await decoded.ReadAsync(probe,token)!=0||decoded.Length!=length)throw Fail("INVALID","The decoded WDC5 source changed length during parsing.");
        return new(records,physicalRows,copyRows,excludedSections,excluded,excludedCopies,excludedSections==0&&excludedCopies==0?"complete":"unencryptedSections",length);
    }
    private static void ValidateLimits(Wdc5Limits limits)
    {
        if(limits.MaxDecodedBytes<204||limits.MaxRows<0||limits.MaxRows>int.MaxValue||limits.MaxFields<1||limits.MaxSections<0||limits.MaxRecordBytes<1||limits.MaxAuxiliaryBytes<0||limits.MaxStringBytes<1||limits.MaxIndexBytes<0||limits.MaxColumns<1||limits.MaxCopyDepth<1||limits.MaxRawTextEvidenceBytes<0||limits.MaxRelationshipBytes<0||limits.MaxRelationshipEntries<0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    private static int Count(uint value,long maximum,string label)
    {if(value>maximum||value>int.MaxValue)throw Fail("LIMIT","WDC5 "+label+" exceeds policy.");return (int)value;}
    private static void ValidateSchemaString(ReadOnlySpan<byte> bytes)
    {
        var end=bytes.Length;while(end>0&&bytes[end-1]==0)end--;
        if(bytes[..end].Contains((byte)0))throw Fail("INVALID","The schema string contains an interior NUL.");
        try{_=Utf8.GetString(bytes[..end]);}catch(DecoderFallbackException){throw Fail("INVALID","The schema string is not strict UTF-8.");}
    }
    private static async Task<byte[]> ReadAt(Stream input,long offset,int count,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(offset<0||count<0||offset>input.Length||count>input.Length-offset)throw Fail("INVALID","WDC5 data is truncated or addresses outside its source.");
        var bytes=new byte[count];if(input.Position!=offset)input.Position=offset;
        try{await input.ReadExactlyAsync(bytes,token);}catch(EndOfStreamException){throw Fail("INVALID","WDC5 data ended unexpectedly.");}
        token.ThrowIfCancellationRequested();return bytes;
    }
    private static async Task VerifyGap(Stream input,long first,long last,CancellationToken token)
    {
        while(first<last)
        {
            var bytes=await ReadAt(input,first,(int)Math.Min(65536,last-first),token);
            if(bytes.Any(value=>value!=0))throw Fail("UNSUPPORTED","A WDC5 gap contains unsupported non-padding bytes.");first+=bytes.Length;
        }
    }
    private static Storage[] ReadStorage(byte[] metadata,int sections,int count,int recordSize,uint packedOffset,Wdc5Limits limits,CancellationToken token)
    {
        var fieldsAt=sections*40;var columnsAt=fieldsAt+count*4;var result=new Storage[count];
        for(var i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var at=columnsAt+i*24;
            ushort H(int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(at+offset,2));
            uint U(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(at+offset,4));
            var kind=U(8);if(kind>5)throw Fail("UNSUPPORTED","A field uses an unknown WDC5 compression kind.");
            var additional=Count(U(4),limits.MaxAuxiliaryBytes,"per-field auxiliary bytes");
            result[i]=new(H(0),H(2),additional,kind,U(12),U(16),U(20),BinaryPrimitives.ReadInt16LittleEndian(metadata.AsSpan(fieldsAt+i*4,2)),BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(fieldsAt+i*4+2,2)));
            var c=result[i];
            if(kind==2)
            {if(c.Additional%8!=0||c.P2!=0||c.P3!=0)throw Fail("UNSUPPORTED","Common storage has unsupported parameters.");}
            else if(kind is 3 or 4)
            {if(c.Additional==0||c.Additional%4!=0||c.Width is <1 or >32||c.Offset!=packedOffset*8L+c.P1||c.Width!=c.P2||c.Offset+(long)c.Width>recordSize*8L)throw Fail("UNSUPPORTED","Pallet storage has unsupported index geometry.");}
            else if(kind is 1 or 5)
            {if(c.Additional!=0||c.P3!=0||c.Width is <1 or >64||c.Offset!=packedOffset*8L+c.P1||c.Width!=c.P2||c.Offset+(long)c.Width>recordSize*8L)throw Fail("UNSUPPORTED","Immediate storage has unsupported or conflicting physical geometry.");}
            else if(c.Additional!=0)throw Fail("UNSUPPORTED","Uncompressed fields cannot consume auxiliary bytes.");
        }
        return result;
    }
    private static List<BoundField> BindFields(DbdBlock schema,Storage[] storage,ushort flags,ushort idIndex,int recordSize,Wdc5Limits limits,CancellationToken token,int? emptyPhysicalCount=null)
    {
        if(schema.Fields.Count(field=>field.IsId)!=1)throw Fail("UNSUPPORTED","A table definition must identify exactly one scalar ID.");
        if(schema.Fields.Count(field=>field.RelationStorage)>1)throw Fail("UNSUPPORTED","Only one selected relationship column is supported.");
        var inlineId=schema.Fields.Select((field,index)=>(field,index)).FirstOrDefault(pair=>pair.field.IsId&&!pair.field.NonInline);
        if(inlineId.field is not null&&schema.Fields.Take(inlineId.index).Any(field=>field.NonInline&&field.RelationStorage))
            throw Fail("UNSUPPORTED","An external relation before an inline ID has no admitted header-index interpretation.");
        var result=new List<BoundField>();var physical=0;
        for(var i=0;i<schema.Fields.Count;i++)
        {
            token.ThrowIfCancellationRequested();var field=schema.Fields[i];
            if(field.RelationStorage&&(field.IsId||field.Column.Family!="int"||field.ArrayLength!=1||field.IntegerBits is not (8 or 16 or 32)))
                throw Fail("UNSUPPORTED","Relationships require one scalar non-ID integer of an admitted8/16/32-bit schema.");
            if(field.NonInline&&!field.IsId&&!field.RelationStorage)throw Fail("UNSUPPORTED","Non-ID external fields need an explicit relationship annotation.");
            if(field.IsId&&(field.Column.Family!="int"||field.ArrayLength!=1||field.NonInline!=((flags&4)!=0)||idIndex!=i&&!(field.NonInline&&idIndex==ushort.MaxValue)))
                throw Fail("UNSUPPORTED","The selected definition and physical ID storage disagree.");
            if(field.NonInline)
            {
                if(field.RelationStorage&&!field.Column.VerifiedName)throw Fail("UNSUPPORTED","Unverified external relationship interpretation has no admitted numeric policy.");
                result.Add(new(field,-1,field.IsId?FieldSource.RecordId:FieldSource.Relationship));continue;
            }
            if(emptyPhysicalCount is not null){result.Add(new(field,-1));physical++;continue;}
            if(physical>=storage.Length)throw Fail("SCHEMA_MISMATCH","The definition has more physical fields than the actual header.");
            var column=storage[physical];
            if(column.Kind==0)
            {
                var width=32-(int)column.FieldBits;if(width<=0)width=checked((int)column.P2);
                if(width is <1 or >64||column.FieldOffset*8L+width*(long)field.ArrayLength>recordSize*8L
                    ||column.Offset!=column.FieldOffset*8L||column.Width!=width*(long)field.ArrayLength)
                    throw Fail("UNSUPPORTED","Uncompressed field geometry is unsupported.");
            }
            if(field.ArrayLength>1&&column.Kind is not (0 or 4))throw Fail("UNSUPPORTED","Only uncompressed or pallet-array arrays are supported.");
            if(column.Kind==4&&column.P3!=field.ArrayLength||column.Kind==3&&column.P3!=0&&column.P3!=1)
                throw Fail("SCHEMA_MISMATCH","Pallet cardinality differs from the selected array definition.");
            if(field.Column.Family is "string" or "locstring")
            {if(column.Kind!=0||field.ArrayLength>1)throw Fail("UNSUPPORTED","Only scalar uncompressed relative strings are admitted.");}
            if(field.IsId&&column.Kind==2)throw Fail("UNSUPPORTED","An inline ID cannot depend on ID-keyed common storage.");
            result.Add(new(field,physical++));
        }
        if(physical!=(emptyPhysicalCount??storage.Length))throw Fail("SCHEMA_MISMATCH","The selected definition does not cover every actual physical field.");
        return result;
    }
    private static IReadOnlyList<Db2Column> Columns(IReadOnlyList<BoundField> fields,Storage[] storage,Wdc5Limits limits)
    {
        var columns=new List<Db2Column>();
        foreach(var binding in fields)
        {
            var field=binding.Field;var kind=field.Column.Family switch{"int"=>"integer","float"=>"float","string" or "locstring"=>"string",_=>throw Fail("UNSUPPORTED","Unknown scalar definition family.")};
            var interpretation=binding.StorageIndex<0?null:RawInterpretation(storage[binding.StorageIndex],field);
            var narrowing=binding.StorageIndex<0?null:LowBitNarrowing(storage[binding.StorageIndex],field);
            for(var i=0;i<field.ArrayLength;i++)
            {if(columns.Count>=limits.MaxColumns)throw Fail("LIMIT","Flattened columns exceed policy.");columns.Add(new(field.ArrayLength==1?field.Column.Name:field.Column.Name+"["+i.ToString(CultureInfo.InvariantCulture)+"]",kind,field.IsId,interpretation,narrowing));}
        }
        return columns.AsReadOnly();
    }
    private static void LoadAuxiliary(Storage[] storage,byte[] bytes,int paletteBytes,int commonBytes,Wdc5Limits limits,ref long charged,CancellationToken token)
    {
        var at=0;
        foreach(var kind in new uint[]{3,4})
            foreach(var column in storage.Where(column=>column.Kind==kind))
            {
                token.ThrowIfCancellationRequested();if(column.Additional>paletteBytes-at)throw Fail("INVALID","Pallet declarations exceed their aggregate block.");
                column.Palette=new uint[column.Additional/4];
                for(var i=0;i<column.Palette.Length;i++){token.ThrowIfCancellationRequested();column.Palette[i]=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));at+=4;}
            }
        if(at!=paletteBytes)throw Fail("INVALID","Pallet declarations do not consume their entire aggregate block.");
        foreach(var column in storage.Where(column=>column.Kind==2))
        {
            if(column.Additional>bytes.Length-at)throw Fail("INVALID","Common declarations exceed their aggregate block.");
            charged=checked(charged+(column.Additional/8)*64L);if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","Common-value indexing exceeds its working-memory reservation.");
            column.Common=new Dictionary<uint,uint>();
            for(var n=0;n<column.Additional/8;n++)
            {
                token.ThrowIfCancellationRequested();var id=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));var value=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at+4,4));at+=8;
                if(!column.Common.TryAdd(id,value))throw Fail("INVALID","A common field repeats an override ID.");
            }
        }
        if(at!=paletteBytes+commonBytes)throw Fail("INVALID","Auxiliary field declarations do not consume their aggregate blocks.");
    }
}
