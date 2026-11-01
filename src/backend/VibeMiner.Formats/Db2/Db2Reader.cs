using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace VibeMiner.Formats;

/// <summary>Original bounded WDC3, WDC4 and WDC5 fixed and sparse decoder over an immutable range source. <see cref="OpenAsync"/> checks
/// the whole file structure and builds the ID indexes without reading record bodies; <see cref="Db2Table"/> decodes rows on demand.</summary>
// Ported from the legacy Formats.Wow Wdc5TableReader (revision wdc5-values-v7); every check, code and message is kept.
// Format research: original Java VibeMiner checkpoint 8f353d046d961108f7acc86bfe1381004467afd5
// (Wdc5HeaderInspector, Wdc5MetadataInspector, FixedSectionIndexParser, FieldValueDecoder,
// RecordBitFields), and DBCD e732093f8864240fc5884bd1bba6b02f3dfc0d56 WDC5Reader.cs.
// Independently authored implementation: no reference parser is a runtime dependency.
// Duplicate geometry checks define a conservative supported subset, not a universal wire rule.
public static partial class Db2Reader
{
    public const string Revision="db2-values-v2";
    internal sealed record Section(ulong Key,long Offset,int Count,int Strings,long RecordsEnd,int IdBytes,int Relations,int MapCount,int Copies,long End);
    internal sealed record Storage(int Offset,int Width,int Additional,uint Kind,uint P1,uint P2,uint P3,short FieldBits,ushort FieldOffset)
    {
        public uint[] Palette=[];
        public Dictionary<uint,uint>? Common;
        public bool VerifiedRawEvidence;
        // D108: the matched raw rule gives way to narrowing when the column has one constant upper-bit pattern.
        public bool RawNarrowsWhenConstant;
        public bool KeepsLowBits;
        // #101: one upper pattern, but keeping only the low bits would give two different stored values the same number.
        public bool RawBecauseLowBitsCollide;
    }
    // Fixed rows: ByteLength is the record size. Sparse rows: GlobalIndex is unused and ByteLength comes from the offset map.
    internal sealed record Location(long Offset,int ByteLength,int Section,int GlobalIndex,int LocalIndex,uint PhysicalId);
    internal sealed record BoundField(DbdField Field,int StorageIndex,FieldSource Source=FieldSource.Physical);
    internal readonly record struct CopyEntry(uint Target,uint Source,int Section);
    internal enum CopyFailure:byte{None,Cycle,Unresolved,TooDeep}
    // Row >= 0: resolved to that physical row. Row -1 without a failure: the chain ends at encrypted ID ProtectedId.
    // A failure is the legacy error for this copy, raised only when the copy is read. 12 bytes, so the copy
    // entry, its resolution and its index entry stay inside the 64 bytes charged per declared copy.
    internal readonly record struct CopyResolution(int Row,uint ProtectedId,CopyFailure Failure)
    {
        public bool IsProtected=>Failure==CopyFailure.None&&Row<0;
    }
    internal static Db2FormatException CopyError(CopyFailure failure,bool sparse)=>failure switch
    {
        CopyFailure.Cycle=>Fail("INVALID",sparse?"Sparse copy declarations contain a cycle.":"Copy declarations contain a cycle."),
        CopyFailure.Unresolved=>Fail("INVALID",sparse?"A sparse copy source is neither readable nor declared protected.":"A copy source has no readable record or declared encrypted identity."),
        CopyFailure.TooDeep=>Fail("LIMIT",sparse?"Sparse copy resolution exceeds its chain bound.":"Copy resolution exceeds its admitted chain depth."),
        _=>throw new ArgumentOutOfRangeException(nameof(failure)),
    };
    /// <summary>Everything Open proved. Immutable after open: Storage's mutable fields are only set during open.</summary>
    internal sealed record TableState(Db2Bytes Bytes,bool Sparse,int Alignment,IReadOnlyList<Section> Sections,IReadOnlyList<BoundField> Fields,
        Storage[] Storage,int RecordSize,int DeclaredRecords,Db2Limits Limits,RelationshipData?[] Relationships,
        Location[] Rows,Dictionary<uint,int> RowIndex,int[] RowStart,CopyEntry[] Copies,CopyResolution[] Resolutions,Dictionary<uint,int> CopyIndex,
        int[] CopyStart,Dictionary<uint,int> EncryptedIds,bool Partial);
    private static readonly UTF8Encoding Utf8=new(false,true);
    private static Db2FormatException Fail(string code,string message)=>new("DB2_"+code,message);

    // Every header field read lives here. WDC5 puts them after its 4-byte schema version and 128-byte schema
    // string, so they start at byte 136; WDC3 and WDC4 have neither, so theirs start right after the magic.
    // Everything after the header (section headers, field structures, storage info, pallet/common blocks, section
    // tails) is the same in all three. WDC3 has no encrypted-ID lists after common data; WDC4 and WDC5 do.
    private const int Wdc5HeaderBytes=204;
    private const int Wdc5FieldsAt=136;
    internal sealed record HeaderFormat(string Name,int HeaderBytes,int FieldsAt,bool EncryptedIdLists);
    private static readonly HeaderFormat Wdc3=new("WDC3",72,4,false),Wdc4=new("WDC4",72,4,true),Wdc5=new("WDC5",Wdc5HeaderBytes,Wdc5FieldsAt,true);
    private static HeaderFormat? KnownFormat(ReadOnlySpan<byte> magic)=>
        magic.SequenceEqual("WDC5"u8)?Wdc5:magic.SequenceEqual("WDC4"u8)?Wdc4:magic.SequenceEqual("WDC3"u8)?Wdc3:null;
    // Older and other DB2-family magics are refused by name. CLS1 is stored reversed ("1SLC"), so both spellings name it.
    private static Db2FormatException UnsupportedFormat(ReadOnlySpan<byte> magic)
    {
        var printable=true;foreach(var b in magic)if(b is <0x20 or >0x7E)printable=false;
        var shown=printable?"'"+Encoding.ASCII.GetString(magic)+"'":"0x"+Convert.ToHexString(magic);
        if(magic.SequenceEqual("1SLC"u8))shown+=" (CLS1)";
        return Fail("UNSUPPORTED","DB2 format "+shown+" is not supported; only WDC3, WDC4 and WDC5 are read.");
    }
    private readonly record struct HeaderFields(uint Records,uint FieldCount,uint RecordSize,uint StringTotal,uint TableHash,uint LayoutHash,
        ushort Flags,ushort IdIndex,uint TotalFieldCount,uint PackedOffset,uint LookupCount,uint StorageInfoBytes,uint CommonBytes,uint PalletBytes,uint SectionCount);
    private static HeaderFields ReadHeaderFields(ReadOnlySpan<byte> header,int at)
    {
        var f=header.Slice(at,68);
        static uint U(ReadOnlySpan<byte> f,int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(offset,4));
        static ushort H(ReadOnlySpan<byte> f,int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(f.Slice(offset,2));
        return new(U(f,0),U(f,4),U(f,8),U(f,12),U(f,16),U(f,20),H(f,36),H(f,38),U(f,40),U(f,44),U(f,48),U(f,52),U(f,56),U(f,60),U(f,64));
    }

    /// <summary>The layout hash of a WDC3, WDC4 or WDC5 header, as eight upper-case hex digits, or null when the bytes are
    /// not a whole header of one of them (a WDC5 one must be schema 5). Only picks which definition to try (#55); the full
    /// reader still checks everything.</summary>
    public static string? LayoutHash(ReadOnlySpan<byte> header)
    {
        if(header.Length<4)return null;var format=KnownFormat(header[..4]);
        if(format is null||header.Length<format.HeaderBytes||format==Wdc5&&BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4,4))!=5)return null;
        return ReadHeaderFields(header,format.FieldsAt).LayoutHash.ToString("X8",CultureInfo.InvariantCulture);
    }

    /// <summary>Checks the header, descriptors, auxiliary data, definition selection, encrypted-ID lists, section layout and
    /// gaps, and builds the ID, copy and relationship indexes. Reads no record bodies or string blocks, except inline IDs
    /// when the table has no external ID list.</summary>
    public static Task<Db2Table> OpenAsync(IDb2ByteSource source,DbdDocument definition,string buildVersion,
        Db2Limits limits,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(definition);ArgumentNullException.ThrowIfNull(limits);
        return OpenCoreAsync(source,definition,buildVersion,limits,token);
    }

    /// <summary>The legacy single-pass shape: Header, then every row in the legacy order, then the completion.
    /// All of it is provisional until this returns.</summary>
    public static async Task<Db2Completion> ReadAsync(IDb2ByteSource source,DbdDocument definition,string buildVersion,
        Db2Limits limits,IDb2Consumer consumer,CancellationToken token=default)
    {
        ArgumentNullException.ThrowIfNull(source);ArgumentNullException.ThrowIfNull(definition);ArgumentNullException.ThrowIfNull(limits);ArgumentNullException.ThrowIfNull(consumer);
        var table=await OpenCoreAsync(source,definition,buildVersion,limits,token);
        await consumer.HeaderAsync(table.Header,token);token.ThrowIfCancellationRequested();
        var budget=new TextBudget();
        await foreach(var row in table.ReadAllCore(budget,token))
        {await consumer.RowAsync(row,token);token.ThrowIfCancellationRequested();}
        return table.Completion with{TextIssueCount=budget.Count};
    }

    private static async Task<Db2Table> OpenCoreAsync(IDb2ByteSource source,DbdDocument definition,string buildVersion,
        Db2Limits limits,CancellationToken token)
    {
        ValidateLimits(limits);token.ThrowIfCancellationRequested();
        var rawRules=SnapshotRawIntegerRules(limits,token);
        var length=source.Length;if(length<4)throw Fail("INVALID","The WDC5 mandatory204-byte header is truncated.");
        if(length>limits.MaxDecodedBytes)throw Fail("LIMIT","Decoded DB2 exceeds its source-byte bound.");
        // The magic picks the header size, so it is peeked before the holes are checked, as the legacy WDC5 reader
        // reported a short file before its holes. The hole list is read once; a list over the bound, or any hole that
        // could touch the magic, leaves the WDC5 path, which fails on it in the legacy order. So does any magic other than
        // WDC3/WDC4: the WDC5 truncation and hole checks run first and the magic check after them, as in the legacy reader.
        var holeSnapshot=SnapshotHoles(source);
        var format=Wdc5;
        if(holeSnapshot is not null&&!holeSnapshot.Any(hole=>hole is null||hole.Offset<4))
        {
            var magic=new byte[4];await source.ReadRangeAsync(0,magic,token);token.ThrowIfCancellationRequested();
            format=KnownFormat(magic)??Wdc5;
        }
        var name=format.Name;var headerBytes=format.HeaderBytes;
        if(length<headerBytes)throw Fail("INVALID",format==Wdc5?"The WDC5 mandatory204-byte header is truncated.":"The "+name+" mandatory "+headerBytes.ToString(CultureInfo.InvariantCulture)+"-byte header is truncated.");
        var holes=CaptureHoles(holeSnapshot,length,limits,token);var decoded=new Db2Bytes(source,length,holes,name);
        var header=await decoded.ReadAt(0,headerBytes,token);
        uint schema=0;
        if(format==Wdc5)
        {
            if(!header.AsSpan(0,4).SequenceEqual("WDC5"u8))throw UnsupportedFormat(header.AsSpan(0,4));
            schema=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4,4));if(schema!=5)throw Fail("UNSUPPORTED","Only WDC5 schema version5 is admitted.");
            ValidateSchemaString(header.AsSpan(8,128));
        }
        else if(KnownFormat(header.AsSpan(0,4))!=format)throw UnsupportedFormat(header.AsSpan(0,4));
        var h=ReadHeaderFields(header,format.FieldsAt);
        var records=Count(h.Records,limits.MaxRows,"record count",name);var fieldCount=Count(h.FieldCount,limits.MaxFields,"field count",name);
        var recordSize=Count(h.RecordSize,limits.MaxRecordBytes,"record width",name);var stringTotal=h.StringTotal;
        var flags=h.Flags;var idIndex=h.IdIndex;
        if(flags is not (0 or 4 or 5 or 6 or 7))throw Fail("UNSUPPORTED","Only fixed or sparse external-ID "+name+" flags are admitted.");
        var sparse=(flags&1)!=0;
        if(h.TotalFieldCount!=fieldCount||h.LookupCount>1)throw Fail("UNSUPPORTED","Additional lookup columns are unsupported.");
        var sectionCount=Count(h.SectionCount,limits.MaxSections,"section count",name);
        var metadataOnlyEmpty=records==0&&sectionCount==0&&h.StorageInfoBytes==0&&h.CommonBytes==0&&h.PalletBytes==0;
        if(!metadataOnlyEmpty&&h.StorageInfoBytes!=fieldCount*24L)throw Fail("UNSUPPORTED",name+" field-storage geometry is unsupported.");
        var paletteBytes=Count(h.PalletBytes,limits.MaxAuxiliaryBytes,"pallet bytes",name);var commonBytes=Count(h.CommonBytes,limits.MaxAuxiliaryBytes,"common bytes",name);
        if((long)paletteBytes+commonBytes>limits.MaxAuxiliaryBytes)throw Fail("LIMIT",name+" aggregate auxiliary bytes exceed policy.");
        if(sparse&&(paletteBytes!=0||commonBytes!=0))throw Fail("UNSUPPORTED","Sparse auxiliary compression requires a separate supported profile.");
        var descriptorEnd=checked(headerBytes+sectionCount*40L+fieldCount*(metadataOnlyEmpty?4L:28L));
        var auxiliaryEnd=checked(descriptorEnd+paletteBytes+commonBytes);
        if(auxiliaryEnd>length)throw Fail("INVALID",name+" metadata and auxiliary declarations exceed the source.");
        var metadata=await decoded.ReadAt(headerBytes,checked((int)(descriptorEnd-headerBytes)),token);
        var sections=new List<Section>(sectionCount);long declared=0,copies=0,strings=0,excluded=0,excludedCopies=0;int excludedSections=0;
        for(var i=0;i<sectionCount;i++)
        {
            token.ThrowIfCancellationRequested();var at=i*40;
            uint S(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(at+offset,4));
            var key=BinaryPrimitives.ReadUInt64LittleEndian(metadata.AsSpan(at,8));
            var count=Count(S(12),limits.MaxRows,"section records",name);var str=Count(S(16),limits.MaxDecodedBytes,"section strings",name);
            var ids=Count(S(24),limits.MaxDecodedBytes,"section ID bytes",name);var rel=Count(S(28),limits.MaxDecodedBytes,"relationship bytes",name);
            var map=Count(S(32),limits.MaxRows,"offset-map records",name);var copy=Count(S(36),limits.MaxRows,"copy count",name);
            if(count>0&&recordSize==0)throw Fail("INVALID","Nonempty records have zero width.");
            if(!sparse&&(S(20)!=0||map!=0))throw Fail("UNSUPPORTED","Fixed offset maps require a separate supported profile.");
            if(ids!=((flags&4)!=0?count*4L:0))throw Fail("INVALID","The fixed section ID-list size disagrees with the ID storage mode.");
            if(sparse&&(str!=0||map!=count))throw Fail("UNSUPPORTED","Sparse sections require inline strings and one map entry per physical record.");
            if(sparse&&(S(20)<S(8)||(count==0)!=(S(20)==S(8))))throw Fail("INVALID","Sparse record-area bounds disagree with its physical count.");
            if(rel!=0&&h.LookupCount==0)throw Fail("INVALID","Relationship bytes require a native lookup column.");
            var span=sparse?checked((long)S(20)-S(8)+ids+copy*8L+map*10L+rel):checked(count*(long)recordSize+str+ids+copy*8L+rel);
            var end=checked(S(8)+span);
            if(end>length||span>0&&S(8)<auxiliaryEnd)throw Fail("INVALID","A section lies outside the source or overlaps descriptors.");
            sections.Add(new(key,S(8),count,str,S(20),ids,rel,map,copy,end));declared+=count;copies+=copy;strings+=str;
            if(key!=0){excludedSections++;excluded+=count;excludedCopies+=copy;}
        }
        if(declared!=records||strings!=stringTotal)throw Fail("INVALID","Header and section record/string counts disagree.");
        if(records+copies>limits.MaxRows)throw Fail("LIMIT","Physical plus declared copy rows exceed policy.");
        if(excludedSections>0&&!limits.AllowPartialEncryptedSections)throw Fail("ENCRYPTED","The table declares keyed sections; partial decoding was not requested.");
        // WDC3 lists no encrypted IDs, so a hidden record sharing a readable ID, or a copy whose source is hidden, can't be
        // told from corruption: a keyed WDC3 table is never read partially.
        if(excludedSections>0&&!format.EncryptedIdLists)throw Fail("ENCRYPTED","The "+name+" table declares keyed sections and has no encrypted-ID lists, so it is not read partially.");
        long charged=checked(records*96L+copies*64L+paletteBytes*2L+commonBytes);
        if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","DB2 ID/copy/auxiliary work exceeds its index-memory reservation.");
        if(h.PackedOffset>recordSize)throw Fail("INVALID","The packed-data byte offset exceeds its physical record.");
        var storage=metadataOnlyEmpty?[]:ReadStorage(metadata,sectionCount,fieldCount,recordSize,h.PackedOffset,limits,name,token);
        // An unlisted build may select by layout alone (D080); BindFields still checks every field against the file.
        var tableHash=h.TableHash.ToString("X8",CultureInfo.InvariantCulture);var layoutHash=h.LayoutHash.ToString("X8",CultureInfo.InvariantCulture);
        var selection=definition.SelectFile(buildVersion,layoutHash,token);
        if(selection.Blocks.Count!=1)throw Fail(selection.Blocks.Count==0?"SCHEMA_MISSING":"SCHEMA_AMBIGUOUS",selection.Match=="layout"
            ?"The build is not listed and the actual layout does not select one definition block on its version line."
            :"The actual layout and exact build do not select one definition block.");
        var fields=BindFields(selection.Blocks[0],storage,flags,idIndex,recordSize,limits,token,metadataOnlyEmpty?fieldCount:null);
        ApplyRawIntegerRules(fields,storage,h.TableHash,h.LayoutHash,limits.DefinitionSha256,rawRules,token);
        ValidateRelationshipSchema(fields,h.LookupCount,records==0&&copies==0);
        charged=ReserveRelationships(sections,limits,charged,fields.Any(f=>f.Field.RelationStorage));
        var sparseAlignment=sparse?ValidateSparseFields(fields,storage,token):0;
        var columns=Columns(fields,storage,limits);
        if(metadataOnlyEmpty)
        {
            if(stringTotal!=0||descriptorEnd!=length)throw Fail("INVALID","The metadata-only empty table has unsupported payload bytes.");
            var empty=new TableState(decoded,sparse,sparseAlignment,sections,fields,storage,recordSize,records,limits,[],[],[],[0],[],[],[],[0],[],false);
            return new(empty,new(schema,tableHash,layoutHash,0,0,recordSize,columns,name),[],new(0,0,0,0,0,0,"complete",length),0);
        }
        var aux=await decoded.ReadAt(descriptorEnd,paletteBytes+commonBytes,token);
        LoadAuxiliary(storage,aux,paletteBytes,commonBytes,limits,ref charged,token);
        // Narrowing depends on the pallet and common values, so it is selected once they are loaded, still before Header.
        if(SelectLowBitNarrowing(fields,storage,token))columns=Columns(fields,storage,limits);
        var encryptedIds=new Dictionary<uint,int>();long prefixEnd=auxiliaryEnd;
        for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
        {
            if(sections[sectionIndex].Key==0||!format.EncryptedIdLists)continue;
            var size=await decoded.ReadAt(prefixEnd,4,token);var count=Count(BinaryPrimitives.ReadUInt32LittleEndian(size),limits.MaxRows,"encrypted ID count",name);prefixEnd+=4;
            charged=checked(charged+count*64L);if(charged>limits.MaxIndexBytes)throw Fail("LIMIT","Encrypted-ID metadata exceeds its working-memory bound.");
            var bytes=await decoded.ReadAt(prefixEnd,checked(count*4),token);prefixEnd+=count*4L;
            // Partial rows are joined by record ID, so a protected ID must name exactly one record:
            // never twice across (or within) encrypted lists, never as a readable row or copy below.
            for(var i=0;i<count;i++)
            {
                token.ThrowIfCancellationRequested();
                if(!encryptedIds.TryAdd(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i*4,4)),sectionIndex))throw Fail("INVALID","Encrypted-section ID lists repeat a record ID.");
            }
        }
        var endOfFile=prefixEnd;
        var populated=sections.Where(section=>section.End>section.Offset).OrderBy(section=>section.Offset).ToArray();
        foreach(var section in populated)
        {
            token.ThrowIfCancellationRequested();
            if(section.Offset<endOfFile)throw Fail("INVALID",name+" section spans overlap each other or their prefix.");
            endOfFile=section.End;
        }
        if(endOfFile!=length)throw Fail("INVALID","The "+name+" source has undeclared trailing bytes.");
        ValidateHoles(holes,populated,prefixEnd,token);
        var gapStart=prefixEnd;
        foreach(var section in populated)
        {await VerifyGap(decoded,gapStart,section.Offset,token);gapStart=section.End;}
        var index=sparse
            ?await IndexSparse(decoded,sections,limits,encryptedIds,recordSize,flags,token)
            :await IndexFixed(decoded,sections,fields,storage,encryptedIds,recordSize,flags,token);
        // Copy chains resolve here, in memory; a broken chain still fails only when that copy is read, as it always did.
        // They resolve before the relationship blocks, which admit one extra pair per readable copy of a row (D292).
        var resolutions=new CopyResolution[index.Copies.Length];long copyRows=0;
        var protectedCopies=new int[sections.Count];var unresolvedCopies=new int[sections.Count];
        for(var i=0;i<resolutions.Length;i++)
        {
            var resolution=ResolveCopy(index.Copies[i],index,encryptedIds,limits.MaxCopyDepth,token);resolutions[i]=resolution;
            if(resolution.Failure!=CopyFailure.None)unresolvedCopies[index.Copies[i].Section]++;
            else if(resolution.Row>=0)copyRows++;
            else{excludedCopies++;protectedCopies[index.Copies[i].Section]++;}
        }
        var relationships=await ReadRelationships(decoded,sections,sparse,flags,recordSize,fields,index,resolutions,
            (key,section)=>index.RowIndex.TryGetValue(key,out var row)&&index.Rows[row].Section==section?index.Rows[row].LocalIndex:-1,token);
        if(ExternalRelationNeedsRaw(fields,relationships,token))columns=Columns(fields,storage,limits,rawExternalRelation:true);
        var state=new TableState(decoded,sparse,sparseAlignment,sections.AsReadOnly(),fields,storage,recordSize,records,limits,relationships,
            index.Rows,index.RowIndex,index.RowStart,index.Copies,resolutions,index.CopyIndex,index.CopyStart,encryptedIds,excludedSections>0);
        var sectionInfo=sections.Select((section,i)=>new Db2Section(i,section.Count,section.Copies,section.Key.ToString("X16",CultureInfo.InvariantCulture),section.Key!=0,
            protectedCopies[i],unresolvedCopies[i])).ToArray();
        return new(state,new(schema,tableHash,layoutHash,records,copies,recordSize,columns,name),Array.AsReadOnly(sectionInfo),
            new(records,index.Rows.Length,copyRows,excludedSections,excluded,excludedCopies,excludedSections==0&&excludedCopies==0?"complete":"unencryptedSections",length),
            unresolvedCopies.Sum());
    }

    internal sealed record RowIndexes(Location[] Rows,Dictionary<uint,int> RowIndex,int[] RowStart,CopyEntry[] Copies,Dictionary<uint,int> CopyIndex,int[] CopyStart);

    private static async Task<RowIndexes> IndexFixed(Db2Bytes decoded,IReadOnlyList<Section> sections,IReadOnlyList<BoundField> fields,Storage[] storage,
        Dictionary<uint,int> encryptedIds,int recordSize,ushort flags,CancellationToken token)
    {
        var (rows,rowIndex,copies,copyIndex)=IndexStorage(sections);var rowCount=0;var copyCount=0;
        var rowStart=new int[sections.Count+1];var copyStart=new int[sections.Count+1];long global=0;
        for(var sectionIndex=0;sectionIndex<sections.Count;sectionIndex++)
        {
            var section=sections[sectionIndex];rowStart[sectionIndex]=rowCount;copyStart[sectionIndex]=copyCount;
            if(section.Key!=0){global+=section.Count;continue;}
            var tail=Tail(section,false,(flags&2)!=0,recordSize);
            void Add(uint id,int row)
            {
                var location=new Location(section.Offset+row*(long)recordSize,recordSize,sectionIndex,checked((int)(global+row)),row,id);
                if(encryptedIds.ContainsKey(id))throw Fail("INVALID","A readable physical row repeats an encrypted-section record ID.");
                if(!rowIndex.TryAdd(id,rowCount))throw Fail("INVALID","Readable physical rows repeat a record ID.");
                rows[rowCount++]=location;
            }
            if((flags&4)!=0)
                await ForRecords(decoded,tail.Ids,section.Count,4,(row,index)=>Add(BinaryPrimitives.ReadUInt32LittleEndian(row),index),token);
            else
            {
                // Inline IDs: whole records in bounded batches, only to read the ID field.
                var idField=fields.Single(field=>field.Field.IsId);var batch=Math.Max(1,65536/recordSize);
                for(var first=0;first<section.Count;)
                {
                    var n=Math.Min(batch,section.Count-first);
                    var block=await decoded.ReadAt(section.Offset+first*(long)recordSize,n*recordSize,token);
                    for(var i=0;i<n;i++)
                    {
                        token.ThrowIfCancellationRequested();var row=first+i;
                        var raw=Raw(block.AsSpan(i*recordSize,recordSize),storage[idField.StorageIndex],idField.Field,0,0);
                        if(raw>uint.MaxValue)throw Fail("INVALID","The inline record ID exceeds unsigned32.");Add((uint)raw,row);
                    }
                    first+=n;
                }
            }
            await ForRecords(decoded,tail.Copies,section.Copies,8,(row,_)=>
            {
                var target=BinaryPrimitives.ReadUInt32LittleEndian(row);var source=BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                if(encryptedIds.ContainsKey(target))throw Fail("INVALID","A readable copy target repeats an encrypted-section record ID.");
                if(!copyIndex.TryAdd(target,copyCount))throw Fail("INVALID","Copy declarations repeat a target ID.");
                copies[copyCount++]=new(target,source,sectionIndex);
            },token);
            global+=section.Count;
        }
        rowStart[sections.Count]=rowCount;copyStart[sections.Count]=copyCount;
        if(copies.Any(copy=>rowIndex.ContainsKey(copy.Target)))throw Fail("INVALID","Copy targets overlap readable physical IDs.");
        return new(rows,rowIndex,rowStart,copies,copyIndex,copyStart);
    }

    // Exact-size arrays and pre-sized indexes for the readable sections, so the row and copy indexes never grow by
    // doubling or get copied: peak memory stays inside the per-row and per-copy charge taken at open.
    private static (Location[] Rows,Dictionary<uint,int> RowIndex,CopyEntry[] Copies,Dictionary<uint,int> CopyIndex) IndexStorage(IReadOnlyList<Section> sections)
    {
        int rows=0,copies=0;
        foreach(var section in sections)if(section.Key==0){rows=checked(rows+section.Count);copies=checked(copies+section.Copies);}
        return (new Location[rows],new(rows),new CopyEntry[copies],new(copies));
    }

    private static CopyResolution ResolveCopy(CopyEntry copy,RowIndexes index,Dictionary<uint,int> encryptedIds,int maxDepth,CancellationToken token)
    {
        var current=copy.Source;var seen=new HashSet<uint>{copy.Target};var found=-1;
        for(var depth=0;depth<maxDepth;depth++)
        {
            token.ThrowIfCancellationRequested();
            if(!seen.Add(current))return new(-1,0,CopyFailure.Cycle);
            if(index.RowIndex.TryGetValue(current,out found))break;
            found=-1;if(encryptedIds.ContainsKey(current))break;
            if(!index.CopyIndex.TryGetValue(current,out var next))return new(-1,0,CopyFailure.Unresolved);
            current=index.Copies[next].Source;
        }
        if(found<0)return encryptedIds.ContainsKey(current)?new(-1,current,CopyFailure.None):new(-1,0,CopyFailure.TooDeep);
        return new(found,0,CopyFailure.None);
    }

    private static void ValidateLimits(Db2Limits limits)
    {
        if(limits.MaxDecodedBytes<204||limits.MaxRows<0||limits.MaxRows>int.MaxValue||limits.MaxFields<1||limits.MaxSections<0||limits.MaxRecordBytes<1||limits.MaxAuxiliaryBytes<0||limits.MaxStringBytes<1||limits.MaxIndexBytes<0||limits.MaxColumns<1||limits.MaxCopyDepth<1||limits.MaxRawTextEvidenceBytes<0||limits.MaxRelationshipBytes<0||limits.MaxRelationshipEntries<0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }
    private static int Count(uint value,long maximum,string label,string format)
    {if(value>maximum||value>int.MaxValue)throw Fail("LIMIT",format+" "+label+" exceeds policy.");return (int)value;}
    private static void ValidateSchemaString(ReadOnlySpan<byte> bytes)
    {
        var end=bytes.Length;while(end>0&&bytes[end-1]==0)end--;
        if(bytes[..end].Contains((byte)0))throw Fail("INVALID","The schema string contains an interior NUL.");
        try{_=Utf8.GetString(bytes[..end]);}catch(DecoderFallbackException){throw Fail("INVALID","The schema string is not strict UTF-8.");}
    }
    private static async Task VerifyGap(Db2Bytes input,long first,long last,CancellationToken token)
    {
        while(first<last)
        {
            var bytes=await input.ReadAt(first,(int)Math.Min(65536,last-first),token);
            if(bytes.Any(value=>value!=0))throw Fail("UNSUPPORTED","A "+input.Format+" gap contains unsupported non-padding bytes.");first+=bytes.Length;
        }
    }
    private static Storage[] ReadStorage(byte[] metadata,int sections,int count,int recordSize,uint packedOffset,Db2Limits limits,string format,CancellationToken token)
    {
        var fieldsAt=sections*40;var columnsAt=fieldsAt+count*4;var result=new Storage[count];
        for(var i=0;i<count;i++)
        {
            token.ThrowIfCancellationRequested();var at=columnsAt+i*24;
            ushort H(int offset)=>BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(at+offset,2));
            uint U(int offset)=>BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(at+offset,4));
            var kind=U(8);if(kind>5)throw Fail("UNSUPPORTED","A field uses an unknown "+format+" compression kind.");
            var additional=Count(U(4),limits.MaxAuxiliaryBytes,"per-field auxiliary bytes",format);
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
    private static List<BoundField> BindFields(DbdBlock schema,Storage[] storage,ushort flags,ushort idIndex,int recordSize,Db2Limits limits,CancellationToken token,int? emptyPhysicalCount=null)
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
            {
                // D295: a fixed-table string array is admitted only as uncompressed 32-bit offsets, one per element.
                if(column.Kind!=0||field.ArrayLength>1&&((flags&1)!=0||NoneWidth(column)!=32))
                    throw Fail("UNSUPPORTED","Only scalar uncompressed relative strings are admitted.");
            }
            if(field.IsId&&column.Kind==2)throw Fail("UNSUPPORTED","An inline ID cannot depend on ID-keyed common storage.");
            result.Add(new(field,physical++));
        }
        if(physical!=(emptyPhysicalCount??storage.Length))throw Fail("SCHEMA_MISMATCH","The selected definition does not cover every actual physical field.");
        return result;
    }
    private static IReadOnlyList<Db2Column> Columns(IReadOnlyList<BoundField> fields,Storage[] storage,Db2Limits limits,bool rawExternalRelation=false)
    {
        var columns=new List<Db2Column>();
        foreach(var binding in fields)
        {
            var field=binding.Field;var kind=field.Column.Family switch{"int"=>"integer","float"=>"float","string" or "locstring"=>"string",_=>throw Fail("UNSUPPORTED","Unknown scalar definition family.")};
            var interpretation=binding.StorageIndex>=0?RawInterpretation(storage[binding.StorageIndex],field)
                :rawExternalRelation&&binding.Source==FieldSource.Relationship?RawExternalRelationInterpretation(field):null;
            var narrowing=binding.StorageIndex<0?null:LowBitNarrowing(storage[binding.StorageIndex],field);
            for(var i=0;i<field.ArrayLength;i++)
            {if(columns.Count>=limits.MaxColumns)throw Fail("LIMIT","Flattened columns exceed policy.");columns.Add(new(field.ArrayLength==1?field.Column.Name:field.Column.Name+"["+i.ToString(CultureInfo.InvariantCulture)+"]",kind,field.IsId,interpretation,narrowing));}
        }
        return columns.AsReadOnly();
    }
    private static void LoadAuxiliary(Storage[] storage,byte[] bytes,int paletteBytes,int commonBytes,Db2Limits limits,ref long charged,CancellationToken token)
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
