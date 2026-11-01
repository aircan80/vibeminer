using System.Buffers.Binary;
using System.Text;

namespace Db2Comparison;

/// <summary>The synthetic base corpus. Authored values only; no game content. The three builders are compact copies of
/// the fixed, sparse and relationship builders in VibeMiner.Formats.Tests.</summary>
internal static class Fixtures
{
    public static readonly Fixture[] All=Build();

    private static Fixture[] Build()
    {
        var list=new List<Fixture>();
        // Fixed tables: every storage kind, signed/unsigned and narrowing rules.
        var (scalar,scalarDbd)=Scalar([(8,1)]);
        list.Add(new("fixed-none-float-string-u64-copy",scalar,scalarDbd,"complete"));
        list.Add(Grouped());
        list.Add(PackedOffset());
        list.Add(Numeric("bitpacked-signed-narrow",1,12,4095,"<8>","complete"));
        list.Add(Numeric("bitpacked-unsigned",1,12,4000,"<u16>","complete"));
        list.Add(Numeric("signed-kind5-as-u8",5,2,3,"<u8>","complete"));
        list.Add(Numeric("signed-kind5-as-8",5,12,4095,"<8>","complete"));
        list.Add(Numeric("none-zero-extend",0,8,255,"<32>","complete"));
        list.Add(Numeric("none-high-bits-mismatch",0,32,256,"<u8>","DB2_SCHEMA_MISMATCH"));
        list.Add(Numeric("D079-pallet-narrows",3,32,256,"<u8>","complete"));
        list.Add(Numeric("D079-common-narrows",2,32,65535,"<8>","complete"));
        list.Add(Pallet("D087-varying-upper-bits",[0x100,0x200,7],"<u8>","DB2_SCHEMA_MISMATCH"));
        list.Add(Pallet("D087-one-pattern-narrows",[0x30005,0x30007,9],"<u16>","complete"));
        list.Add(Pallet("D109-low-bits-collide",[5,0xFFFF0005,7],"<u16>","complete"));
        list.Add(Pallet("D117-signed-fit-collide",[0x0000FFFF,0xFFFFFFFF],"<16>","complete"));
        list.Add(Pallet("D108-rule-narrows-when-constant",[0x30005,0x30007],"<u16>","complete",
            new Lim(Sha:true,Rules:[new(1,"Value",3,32,16,true,NarrowsWhenConstant:true)])));
        list.Add(Pallet("D108-rule-varying-keeps-raw",[0x30005,0x40007],"<u16>","complete",
            new Lim(Sha:true,Rules:[new(1,"Value",3,32,16,true,NarrowsWhenConstant:true)])));
        list.Add(Numeric("raw-verified-rule",0,32,511,"<u8>","complete",new Lim(Sha:true,Rules:[new(1,"Value",0,32,8,true)])));
        list.Add(Numeric("raw-unverified-name",0,32,511,"<u8>","complete",unverified:true));
        list.Add(InlineIdArrays());
        list.Add(MultiSectionStrings());
        list.AddRange(StringArrays());
        var (chain,chainDbd)=Scalar([(8,1),(9,8),(10,9)]);
        list.Add(new("fixed-copy-chain",chain,chainDbd,"complete"));
        var (cycle,cycleDbd)=Scalar([(8,9),(9,8)]);
        list.Add(new("fixed-copy-cycle-after-rows",cycle,cycleDbd,"DB2_INVALID"));
        list.AddRange(FixedKeyed());

        // Relationship blocks: flags 0/4/6 fixed, 5/7 sparse, inline and noninline relations.
        foreach(ushort flags in new ushort[]{0,4,6,5,7})
        {
            var r=Rel(flags,[new([900,0],[7,0],Copies:[(100,901)]),new([901],[8],Copies:[(101,100)])]);
            list.Add(new($"rel-external-flags{flags}",r.Bytes,r.Dbd,"complete"));
        }
        var inline=Rel(4,[new([700,11],[2,3])],external:false);list.Add(new("rel-inline-relation-flags4",inline.Bytes,inline.Dbd,"complete"));
        inline=Rel(6,[new([700,11],[2,3])],external:false);list.Add(new("rel-inline-relation-flags6",inline.Bytes,inline.Dbd,"complete"));
        foreach(ushort flags in new ushort[]{5,7})
        {var names=Rel(flags,[new([900,10],[2,3])],names:true);list.Add(new($"rel-sparse-names-flags{flags}",names.Bytes,names.Dbd,"complete"));}
        var fixedNames=Rel(4,[new([900],[2]),new([10],[3])],names:true);list.Add(new("rel-fixed-names-two-sections",fixedNames.Bytes,fixedNames.Dbd,"complete"));
        var common=Rel(4,[new([900],[3],Copies:[(111,900)])],external:false,commonOverride:(111,9));list.Add(new("rel-inline-common-override",common.Bytes,common.Dbd,"complete"));
        var keyed=Rel(4,[new([900],[2],Copies:[(111,901)]),new([901],[3],Key:123,Protected:[901])]);
        var protectedStart=keyed.Tails[1].Start;Array.Clear(keyed.Bytes,protectedStart,keyed.Bytes.Length-protectedStart);
        list.Add(new("rel-keyed-section-hole",keyed.Bytes,keyed.Dbd,"unencryptedSections",new Lim(Partial:true),[(protectedStart,keyed.Bytes.Length-protectedStart)]));
        var empty=Rel(4,[],lookup:1);list.Add(new("rel-empty-external-table",empty.Bytes,empty.Dbd,"complete"));
        var fields=(int)BinaryPrimitives.ReadUInt32LittleEndian(empty.Bytes.AsSpan(140));var metadataOnly=empty.Bytes[..(204+fields*4)];Put(metadataOnly,188,0);
        list.Add(new("metadata-only-empty",metadataOnly,empty.Dbd,"complete"));

        // Sparse tables: flags 5, 4- and 8-byte padding, inline strings, raw text evidence, keyed sections.
        var sparse=Sparse([new([Record("é"),Record("a much longer name",quality:4)],[7,21],[(8,7),(9,8)])]);
        list.Add(new("sparse-basic-copies",sparse.Bytes,SparseDbd,"complete"));
        var padded=Sparse([new([Pad(Record("a"),4),Pad(Record("abc"),4),Record("é")],[7,21,30],[(8,21)])]);
        list.Add(new("sparse-4-byte-padding",padded.Bytes,SparseDbd,"complete"));
        var wide=Sparse([new([Pad(WideRecord("abc"),8),Pad(WideRecord(""),8)],[7,9],[(8,7)])],layout:"wideFirst");
        list.Add(new("sparse-8-byte-padding",wide.Bytes,SparseDbd,"complete",Build:"1.2.3.6"));
        var bad=new byte[]{255,0}.Concat(Record("")[1..]).ToArray();
        var raw=Sparse([new([Record(""),Record("é"),bad],[0,1,7],[(8,7),(9,8)])]);
        list.Add(new("sparse-raw-text-evidence",raw.Bytes,SparseDbd,"complete",new Lim(RawText:true)));
        list.Add(new("sparse-invalid-utf8-strict",raw.Bytes,SparseDbd,"DB2_INVALID"));
        var sparseKeyed=Sparse([new([Record("clear")],[7],[(8,7),(9,21)]),new([Record("not plaintext")],[21],[(22,21)],123,[21,22])]);
        list.Add(new("sparse-keyed-hole",sparseKeyed.Bytes,SparseDbd,"unencryptedSections",new Lim(Partial:true),
            [(sparseKeyed.Starts[1],sparseKeyed.SectionEnds[1]-sparseKeyed.Starts[1])]));
        var cross=Sparse([new([Record("")],[0],[(9,8)]),new([Record("other")],[30],[(8,30)])]);
        list.Add(new("sparse-cross-section-chain",cross.Bytes,SparseDbd,"complete"));
        var two=Sparse([new([Record("clear")],[7],[(8,7)]),new([Record("one")],[21],[],123,[21]),new([Record("two")],[30],[],456,[30])]);
        // D310: a keyed section without a hole is read by the new reader only, so both keyed bodies are holes here.
        list.Add(new("sparse-two-keyed-holes",two.Bytes,SparseDbd,"unencryptedSections",new Lim(Partial:true),
            [(two.Starts[1],two.SectionEnds[1]-two.Starts[1]),(two.Starts[2],two.SectionEnds[2]-two.Starts[2])]));

        // Broken copy chains, fixed and sparse, so each legacy message variant is compared.
        var (unresolved,unresolvedDbd)=Scalar([(8,77)]);
        list.Add(new("fixed-copy-unresolved",unresolved,unresolvedDbd,"DB2_INVALID"));
        list.Add(new("fixed-copy-chain-too-deep",chain,chainDbd,"DB2_LIMIT",new Lim(MaxCopyDepth:2)));
        var sparseUnresolved=Sparse([new([Record("a")],[7],[(8,77)])]);
        list.Add(new("sparse-copy-unresolved",sparseUnresolved.Bytes,SparseDbd,"DB2_INVALID"));
        var sparseCycle=Sparse([new([Record("a")],[7],[(8,9),(9,8)])]);
        list.Add(new("sparse-copy-cycle",sparseCycle.Bytes,SparseDbd,"DB2_INVALID"));
        var sparseDeep=Sparse([new([Record("a")],[7],[(8,7),(9,8),(10,9)])]);
        list.Add(new("sparse-copy-chain-too-deep",sparseDeep.Bytes,SparseDbd,"DB2_LIMIT",new Lim(MaxCopyDepth:2)));

        // Raw text evidence over its byte bound.
        list.Add(new("sparse-raw-text-evidence-limit",raw.Bytes,SparseDbd,"DB2_LIMIT",new Lim(RawText:true,MaxRawText:0)));

        // Holes over readable bytes: records, string block and copy table, with partial on and off.
        var records=(long)BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(212));
        var recordBytes=(long)BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(136))*BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(144));
        var stringBytes=(long)BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(148));
        var copiesAt=records+recordBytes+stringBytes+BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(204+24));
        var copyBytes=8L*BinaryPrimitives.ReadUInt32LittleEndian(scalar.AsSpan(204+36));
        foreach(var (where,hole) in new[]{("records",(records,recordBytes)),("strings",(records+recordBytes,stringBytes)),("copies",(copiesAt,copyBytes))})
            foreach(var partial in new[]{false,true})
                list.Add(new($"fixed-readable-hole-{where}-partial{(partial?1:0)}",scalar,scalarDbd,partial?"DB2_PROJECTION_UNSUPPORTED":"DB2_ENCRYPTED",new Lim(Partial:partial),[hole]));
        foreach(var partial in new[]{false,true})
            list.Add(new($"sparse-readable-hole-records-partial{(partial?1:0)}",sparse.Bytes,SparseDbd,partial?"DB2_PROJECTION_UNSUPPORTED":"DB2_ENCRYPTED",new Lim(Partial:partial),[(sparse.Starts[0],8)]));
        return [..list];
    }

    // ---- Fixed builder (from Db2ReaderTests) ----
    private sealed record Column(short Bits,ushort Position,ushort Offset,ushort Width,int Additional,uint Kind,uint P1,uint P2,uint P3,byte[] Aux);
    private sealed record Part(byte[] Records,uint[] Ids,byte[] Strings,(uint Target,uint Source)[] Copies,ulong Key=0,uint[]? EncryptedIds=null);
    private static string FixedDbd(string columns,string fields)=>"COLUMNS\n"+columns+"\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fields+"\n";

    private static (byte[] Bytes,string Dbd) Scalar((uint,uint)[] copies)
    {
        var columns=new[]{new Column(24,0,0,8,0,0,0,0,0,[]),new Column(0,1,8,32,0,0,0,0,0,[]),new Column(0,5,40,32,0,0,0,0,0,[]),new Column(-32,9,72,64,0,0,0,0,0,[])};
        var records=new byte[34];records[0]=255;records[17]=128;BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(1),1.25f);BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(18),-0.5f);
        BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(5),30);BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(22),19);
        BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(9),ulong.MaxValue);BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(26),(1UL<<63)+9);
        return(BuildFixed(columns,17,[new(records,[1,uint.MaxValue],Encoding.UTF8.GetBytes("\0café\0hello\0"),copies)]),
            FixedDbd("int ID\nint Small\nfloat Weight\nstring Name\nint Wide","$noninline,id$ID<u32>\nSmall<8>\nWeight\nName\nWide<u64>"));
    }
    private static Fixture Grouped()
    {
        var columns=new[]{new Column(32,0,0,1,16,4,0,1,2,U32(10,11,20,21)),new Column(32,0,0,0,16,2,7,0,0,U32(10,8,20,99)),new Column(32,0,1,1,8,3,1,1,0,U32(100,200)),new Column(32,0,2,3,0,5,2,3,0,[]),new Column(32,0,5,3,0,1,5,3,0,[])};
        var dbd=FixedDbd("int ID\nint Pair\nint Common\nint Palette\nint Small\nint Packed","$noninline,id$ID<u32>\nPair<u32>[2]\nCommon<u32>\nPalette<u32>\nSmall<8>\nPacked<u8>");
        return new("fixed-pallet-array-common-pallet-signed-bitpacked",BuildFixed(columns,1,[new([0b11101,0b10100110],[10,11],[],[(20,10)])]),dbd,"complete");
    }
    private static Fixture PackedOffset()
    {
        var columns=new[]{new Column(0,0,0,32,0,0,0,0,0,[]),new Column(32,4,32,6,0,5,0,6,0,[]),new Column(32,4,38,2,8,3,6,2,0,U32(0x3f800000,0x3f000000)),new Column(32,4,40,2,0,5,8,2,0,[])};
        var records=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(records,9);records[4]=0b01000101;records[5]=0b11;
        var dbd=FixedDbd("int ID\nlocstring ClassName_lang\nint ClassID\nfloat PriceModifier\nint Flags","$noninline,id$ID<32>\nClassName_lang\nClassID<8>\nPriceModifier\nFlags<u8>");
        return new("fixed-packed-offset-pallet-float-locstring",BuildFixed(columns,8,[new(records,[1],"\0Weapons\0"u8.ToArray(),[])],packedOffset:4),dbd,"complete");
    }
    private static Fixture Numeric(string name,uint kind,int width,ulong raw,string schema,string expect,Lim? limits=null,bool unverified=false)
    {
        var record=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(record,kind is 2 or 3?0:raw);
        var column=kind switch
        {
            0=>new Column((short)(32-width),0,0,(ushort)width,0,0,0,0,0,[]),
            2=>new Column(32,0,0,0,8,2,(uint)raw,0,0,U32(5,7)),
            3=>new Column(32,0,0,1,4,3,0,1,0,U32((uint)raw)),
            _=>new Column(32,0,0,(ushort)width,0,kind,0,(uint)width,0,[])
        };
        // Two rows; the common column overrides record 5 with 7.
        return new(name,BuildFixed([column],8,[new([..record,..record],[1,5],[],[(6,1)])]),
            FixedDbd("int ID\nint Value"+(unverified?"?":""),"$noninline,id$ID<u32>\nValue"+schema),expect,limits);
    }
    private static Fixture Pallet(string name,uint[] palette,string schema,string expect,Lim? limits=null)
    {
        var records=palette.Select((_,i)=>(byte)i).ToArray();
        return new(name,BuildFixed([new Column(32,0,0,4,palette.Length*4,3,0,4,0,U32(palette))],1,[new(records,[..Enumerable.Range(1,palette.Length).Select(i=>(uint)i)],[],[(100,1)])]),
            FixedDbd("int ID\nint Value","$noninline,id$ID<u32>\nValue"+schema),expect,limits);
    }
    private static Fixture InlineIdArrays()
    {
        var records=new byte[12];BinaryPrimitives.WriteUInt32LittleEndian(records,42);records[4]=128;records[5]=255;BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(6),43);records[10]=1;records[11]=2;
        var bytes=BuildFixed([new(0,0,0,32,0,0,0,0,0,[]),new(24,4,32,16,0,0,0,0,0,[])],6,[new(records,[],[],[(50,43)])],flags:0);
        return new("fixed-inline-ids-uncompressed-array",bytes,FixedDbd("int ID\nint Values","$id$ID<u32>\nValues<u8>[2]"),"complete");
    }
    private static Fixture MultiSectionStrings()
    {
        var bytes=BuildFixed([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(9),[1],"\0one\0"u8.ToArray(),[]),new(U32(10),[2],"\0two\0"u8.ToArray(),[(3,1)])]);
        return new("fixed-strings-across-sections",bytes,FixedDbd("int ID\nstring Name","$noninline,id$ID<u32>\nName"),"complete");
    }
    // D295: fixed string arrays. The legacy reader refuses every one; the new reader reads 32-bit uncompressed ones.
    private static IEnumerable<Fixture> StringArrays()
    {
        var three=FixedDbd("int ID\nstring Names\nint Value","$noninline,id$ID<u32>\nNames[3]\nValue<u32>");
        yield return new("D295-fixed-string-array",BuildFixed([new(0,0,0,96,0,0,0,0,0,[]),new(0,12,96,32,0,0,0,0,0,[])],16,
            [new(U32(33,35,0,100,28,30,9,200),[1,2],"\0alpha\0beta\0gamma\0delta\0"u8.ToArray(),[(3,2)])]),three,"complete",StringArray:0);
        yield return new("D295-fixed-string-array-across-sections",BuildFixed([new(0,0,0,64,0,0,0,0,0,[])],8,
            [new(U32(17,17),[1],"\0one\0uno\0"u8.ToArray(),[]),new(U32(18,18),[2],"\0two\0dos\0"u8.ToArray(),[(3,1)])]),
            FixedDbd("int ID\nstring Names","$noninline,id$ID<u32>\nNames[2]"),"complete",StringArray:0);
        var refused=FixedDbd("int ID\nstring Names","$noninline,id$ID<u32>\nNames[3]");
        yield return new("D295-pallet-array-string-array-refused",BuildFixed([new(32,0,0,1,12,4,0,1,3,U32(1,2,3))],1,[new([0],[1],"\0a\0"u8.ToArray(),[])]),refused,"DB2_UNSUPPORTED",StringArray:0);
        yield return new("D295-16-bit-string-array-refused",BuildFixed([new(16,0,0,48,0,0,0,0,0,[])],6,[new(new byte[6],[1],[],[])]),refused,"DB2_UNSUPPORTED",StringArray:0);
    }
    private static IEnumerable<Fixture> FixedKeyed()
    {
        var dbd=FixedDbd("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");var columns=new[]{new Column(0,0,0,32,0,0,0,0,0,[])};
        var bytes=BuildFixed(columns,4,[new(U32(99),[7],[],[(8,7),(9,21)]),new(new byte[8],[0,0],[],[],123,[21,22])]);
        var hole=(long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(204+40+8));
        yield return new("fixed-keyed-hole-partial",bytes,dbd,"unencryptedSections",new Lim(Partial:true),[(hole,bytes.Length-hole)]);
        yield return new("fixed-keyed-hole-not-partial",bytes,dbd,"DB2_ENCRYPTED",null,[(hole,bytes.Length-hole)]);
        var three=BuildFixed(columns,4,[new(U32(99),[7],[],[(8,7)]),new(new byte[8],[0,0],[],[],123,[21,22]),new(new byte[4],[0],[],[],456,[23])]);
        // D310: a keyed section without a hole is read by the new reader only, so both keyed bodies are holes here.
        long Start(int section)=>BinaryPrimitives.ReadUInt32LittleEndian(three.AsSpan(204+section*40+8));
        yield return new("fixed-two-keyed-holes-partial",three,dbd,"unencryptedSections",new Lim(Partial:true),[(Start(1),Start(2)-Start(1)),(Start(2),three.Length-Start(2))]);
    }
    private static byte[] BuildFixed(Column[] columns,int recordSize,Part[] sections,ushort flags=4,uint packedOffset=0)
    {
        var palette=columns.Where(c=>c.Kind==3).Concat(columns.Where(c=>c.Kind==4)).SelectMany(c=>c.Aux).ToArray();var common=columns.Where(c=>c.Kind==2).SelectMany(c=>c.Aux).ToArray();
        var prefix=204+sections.Length*40+columns.Length*28+palette.Length+common.Length+sections.Where(s=>s.Key!=0).Sum(s=>4+(s.EncryptedIds?.Length??0)*4);
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Encoding.UTF8.GetBytes("authored").CopyTo(header,8);
        Put(header,136,(uint)sections.Sum(s=>s.Records.Length/recordSize));Put(header,140,(uint)columns.Length);Put(header,144,(uint)recordSize);Put(header,148,(uint)sections.Sum(s=>s.Strings.Length));
        Put(header,152,0x11223344);Put(header,156,0x01020304);Half(header,172,flags);Put(header,176,(uint)columns.Length);Put(header,180,packedOffset);Put(header,188,(uint)columns.Length*24);Put(header,192,(uint)common.Length);Put(header,196,(uint)palette.Length);Put(header,200,(uint)sections.Length);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(header);var offset=prefix;
        foreach(var section in sections)
        {
            writer.Write(section.Key);writer.Write((uint)offset);writer.Write((uint)(section.Records.Length/recordSize));writer.Write((uint)section.Strings.Length);writer.Write(0u);writer.Write(flags==4?(uint)section.Ids.Length*4:0);writer.Write(0u);writer.Write(0u);writer.Write((uint)section.Copies.Length);
            offset+=section.Records.Length+section.Strings.Length+(flags==4?section.Ids.Length*4:0)+section.Copies.Length*8;
        }
        foreach(var column in columns){writer.Write(column.Bits);writer.Write(column.Position);}
        foreach(var column in columns){writer.Write(column.Offset);writer.Write(column.Width);writer.Write((uint)column.Additional);writer.Write(column.Kind);writer.Write(column.P1);writer.Write(column.P2);writer.Write(column.P3);}
        writer.Write(palette);writer.Write(common);
        foreach(var section in sections.Where(s=>s.Key!=0)){writer.Write((uint)(section.EncryptedIds?.Length??0));foreach(var id in section.EncryptedIds??[])writer.Write(id);}
        foreach(var section in sections){writer.Write(section.Records);writer.Write(section.Strings);if(flags==4)foreach(var id in section.Ids)writer.Write(id);foreach(var copy in section.Copies){writer.Write(copy.Target);writer.Write(copy.Source);}}
        writer.Flush();return output.ToArray();
    }

    // ---- Sparse builder (from Db2SparseTests) ----
    private const string SparseDbd="COLUMNS\nint ID\nstring Name\nint Quality\nfloat Ratio\nint Budget\nint Unknown?\nint Wide\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nName\nQuality<u8>\nRatio\nBudget<16>[2]\nUnknown<u8>\n\nLAYOUT 01020305\nBUILD 1.2.3.5\n$noninline,id$ID<u32>\nName\nQuality<u8>\nRatio\nBudget<16>[2]\nWide<64>\n\nLAYOUT 01020306\nBUILD 1.2.3.6\n$noninline,id$ID<u32>\nWide<64>\nName\nQuality<u8>\nRatio\nBudget<16>[2]\n";
    private static byte[] Pad(byte[] record,int alignment)=>record.Concat(new byte[(-record.Length)&(alignment-1)]).ToArray();
    private static byte[] WideRecord(string name)=>[..BitConverter.GetBytes(0x8000000000000001UL),..Record(name)[..^4]];
    private static byte[] Record(string name,byte quality=2,float ratio=1.5f)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(Encoding.UTF8.GetBytes(name));writer.Write((byte)0);
        writer.Write(quality);writer.Write(ratio);writer.Write((short)-1);writer.Write((short)200);writer.Write(0x12345678u);writer.Flush();return output.ToArray();
    }
    private sealed record SparsePart(byte[][] Records,uint[] Ids,(uint Target,uint Source)[] Copies,ulong Key=0,uint[]? EncryptedIds=null);
    private sealed record SparseFixture(byte[] Bytes,int[] Starts,int[] SectionEnds);
    private static SparseFixture Sparse(SparsePart[] parts,string layout="narrow")
    {
        var prefix=204+parts.Length*40+5*28+parts.Where(part=>part.Key!=0).Sum(part=>4+4*(part.EncryptedIds?.Length??0));
        var starts=new int[parts.Length];var ends=new int[parts.Length];var sectionEnds=new int[parts.Length];var next=prefix;
        for(var index=0;index<parts.Length;index++)
        {
            var part=parts[index];starts[index]=next;ends[index]=next+part.Records.Sum(record=>record.Length);
            sectionEnds[index]=ends[index]+part.Ids.Length*4+part.Copies.Length*8+part.Ids.Length*10;next=sectionEnds[index];
        }
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Put(header,136,(uint)parts.Sum(part=>part.Ids.Length));Put(header,140,5);Put(header,144,24);
        Put(header,152,0x11223344);Put(header,156,layout switch{"wideLast"=>0x01020305u,"wideFirst"=>0x01020306u,_=>0x01020304u});Half(header,172,5);Put(header,176,5);Put(header,180,24);Put(header,188,120);Put(header,200,(uint)parts.Length);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(header);
        for(var index=0;index<parts.Length;index++){var part=parts[index];writer.Write(part.Key);writer.Write((uint)starts[index]);writer.Write((uint)part.Ids.Length);writer.Write(0u);writer.Write((uint)ends[index]);writer.Write((uint)part.Ids.Length*4);writer.Write(0u);writer.Write((uint)part.Ids.Length);writer.Write((uint)part.Copies.Length);}
        var (bits,positions,widths)=layout switch
        {
            "wideFirst"=>(new short[]{-32,0,24,0,16},new ushort[]{0,8,12,13,17},new ushort[]{64,32,8,32,32}),
            "wideLast"=>(new short[]{0,24,0,16,-32},new ushort[]{0,4,5,9,13},new ushort[]{32,8,32,32,64}),
            _=>(new short[]{0,24,0,16,0},new ushort[]{0,4,5,9,13},new ushort[]{32,8,32,32,32})
        };
        for(var index=0;index<5;index++){writer.Write(bits[index]);writer.Write(positions[index]);}
        for(var index=0;index<5;index++){writer.Write((ushort)(positions[index]*8));writer.Write(widths[index]);for(var item=0;item<5;item++)writer.Write(0u);}
        foreach(var part in parts.Where(part=>part.Key!=0)){writer.Write((uint)(part.EncryptedIds?.Length??0));foreach(var id in part.EncryptedIds??[])writer.Write(id);}
        for(var index=0;index<parts.Length;index++)
        {
            var part=parts[index];foreach(var record in part.Records)writer.Write(record);foreach(var id in part.Ids)writer.Write(id);
            foreach(var (target,source) in part.Copies){writer.Write(target);writer.Write(source);}
            var at=starts[index];foreach(var record in part.Records){writer.Write((uint)at);writer.Write((ushort)record.Length);at+=record.Length;}foreach(var id in part.Ids)writer.Write(id);
        }
        writer.Flush();return new(output.ToArray(),starts,sectionEnds);
    }

    // ---- Relationship builder (from Db2RelationshipTests) ----
    private sealed record Spec(uint[] Ids,uint[] Parents,(uint Parent,uint Key)[]? Pairs=null,bool Native=true,(uint Target,uint Source)[]? Copies=null,ulong Key=0,uint[]? Protected=null);
    private sealed record Tail(int Start,int Relations,int MapIds);
    private sealed record RelFixture(byte[] Bytes,string Dbd,Tail[] Tails);
    private static RelFixture Rel(ushort flags,Spec[] sections,bool external=true,string parentType="<u32>",uint lookup=1,bool names=false,(uint Id,uint Value)? commonOverride=null)
    {
        var sparse=(flags&1)!=0;var indexed=(flags&4)!=0;var secondary=(flags&2)!=0;var parentWidth=parentType.Contains("8>")?1:parentType.Contains("16>")?2:4;
        var physical=new List<(int Width,bool Name,bool Common)>();if(!indexed)physical.Add((4,false,false));physical.Add((4,names,false));if(!external)physical.Add((parentWidth,false,commonOverride is not null));physical.Add((4,false,false));
        var recordSize=physical.Where(p=>!p.Common).Sum(p=>p.Width);var fields=physical.Count;var aux=commonOverride is null?0:8;
        var prefixBytes=sections.Where(s=>s.Key!=0).Sum(s=>4+(s.Protected?.Length??0)*4);var start=204+sections.Length*40+fields*28+aux+prefixBytes;
        var bodies=new List<byte[]>();var tails=new List<Tail>();var headers=new List<byte[]>();var totalRecords=sections.Sum(s=>s.Ids.Length);var global=0;var allStrings=0;
        foreach(var section in sections)
        {
            using var body=new MemoryStream();using var w=new BinaryWriter(body);var maps=new List<(uint Offset,ushort Size)>();var strings=new MemoryStream();strings.WriteByte(0);
            for(var i=0;i<section.Ids.Length;i++)
            {
                var before=body.Position;if(!indexed)w.Write(section.Ids[i]);
                var name=new string('a',i%3+1)+i;
                if(names&&sparse){w.Write(Encoding.UTF8.GetBytes(name));w.Write((byte)0);}
                else if(names){var stringAt=allStrings+(int)strings.Position;w.Write((totalRecords-(global+i))*recordSize+stringAt);strings.Write(Encoding.UTF8.GetBytes(name+"\0"));}
                else w.Write((uint)(1000+i));
                if(!external&&commonOverride is null){var value=section.Parents[i];if(parentWidth==1)w.Write((byte)value);else if(parentWidth==2)w.Write((ushort)value);else w.Write(value);}
                w.Write((uint)(2000+i));if(sparse)while((body.Position-before)%4!=0)w.Write((byte)0);
                maps.Add(((uint)(start+before),(ushort)(body.Position-before)));
            }
            var recordsEnd=(int)(start+body.Position);var stringBytes=names&&!sparse?strings.ToArray():[];w.Write(stringBytes);allStrings+=stringBytes.Length;
            if(indexed)foreach(var id in section.Ids)w.Write(id);
            foreach(var copy in section.Copies??[]){w.Write(copy.Target);w.Write(copy.Source);}
            if(sparse)foreach(var map in maps){w.Write(map.Offset);w.Write(map.Size);}
            var mapIdsAt=0;if(sparse&&secondary){mapIdsAt=start+(int)body.Position;foreach(var id in section.Ids)w.Write(id);}
            var relationsAt=start+(int)body.Position;var pairBytes=0;
            if(section.Native)
            {
                var pairs=section.Pairs??section.Ids.Select((id,i)=>(section.Parents[i],secondary?id:(uint)i)).ToArray();
                w.Write((uint)pairs.Length);w.Write(0u);w.Write(uint.MaxValue);foreach(var pair in pairs){w.Write(pair.Item1);w.Write(pair.Item2);}pairBytes=12+pairs.Length*8;
            }
            if(sparse&&!secondary){mapIdsAt=start+(int)body.Position;foreach(var id in section.Ids)w.Write(id);}
            w.Flush();
            var sh=new byte[40];BinaryPrimitives.WriteUInt64LittleEndian(sh,section.Key);Put(sh,8,(uint)start);Put(sh,12,(uint)section.Ids.Length);Put(sh,16,(uint)stringBytes.Length);Put(sh,20,sparse?(uint)recordsEnd:0);Put(sh,24,indexed?(uint)section.Ids.Length*4:0);Put(sh,28,(uint)pairBytes);Put(sh,32,sparse?(uint)section.Ids.Length:0);Put(sh,36,(uint)(section.Copies?.Length??0));headers.Add(sh);tails.Add(new(start,relationsAt,mapIdsAt));bodies.Add(body.ToArray());start+=(int)body.Length;global+=section.Ids.Length;
        }
        var bytes=new byte[start];"WDC5"u8.CopyTo(bytes);Put(bytes,4,5);Put(bytes,136,(uint)totalRecords);Put(bytes,140,(uint)fields);Put(bytes,144,(uint)recordSize);Put(bytes,148,(uint)allStrings);Put(bytes,152,0x11223344);Put(bytes,156,0x01020304);Half(bytes,172,flags);Put(bytes,176,(uint)fields);Put(bytes,184,lookup);Put(bytes,188,(uint)fields*24);Put(bytes,192,(uint)aux);Put(bytes,200,(uint)sections.Length);
        for(var i=0;i<headers.Count;i++)headers[i].CopyTo(bytes,204+i*40);
        var metadata=204+sections.Length*40;var offset=0;for(var i=0;i<fields;i++)
        {
            var p=physical[i];Half(bytes,metadata+i*4,(ushort)(32-p.Width*8));Half(bytes,metadata+i*4+2,(ushort)offset);var at=metadata+fields*4+i*24;Half(bytes,at,(ushort)(offset*8));Half(bytes,at+2,(ushort)(p.Common?0:p.Width*8));Put(bytes,at+4,p.Common?8u:0);Put(bytes,at+8,p.Common?2u:0);Put(bytes,at+12,p.Common?sections[0].Parents[0]:0);if(!p.Common)offset+=p.Width;
        }
        var position=metadata+fields*28;if(commonOverride is{}co){Put(bytes,position,co.Id);Put(bytes,position+4,co.Value);position+=8;}
        foreach(var section in sections.Where(s=>s.Key!=0)){Put(bytes,position,(uint)(section.Protected?.Length??0));position+=4;foreach(var id in section.Protected??[]){Put(bytes,position,id);position+=4;}}
        for(var i=0;i<bodies.Count;i++)bodies[i].CopyTo(bytes,tails[i].Start);
        var fieldsText=(indexed?"$noninline,id$ID<u32>":"$id$ID<u32>")+"\nBefore"+(names?"":"<u32>")+"\n"+(external?"$noninline,relation$":"$relation$")+"Parent"+parentType+"\nAfter<u32>";
        var dbd="COLUMNS\nint ID\n"+(names?"string":"int")+" Before\nint Parent\nint After\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fieldsText+"\n";
        return new(bytes,dbd,[..tails]);
    }

    private static byte[] U32(params uint[] values){var bytes=new byte[values.Length*4];for(var i=0;i<values.Length;i++)Put(bytes,i*4,values[i]);return bytes;}
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    private static void Half(byte[] bytes,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at,2),value);
}
