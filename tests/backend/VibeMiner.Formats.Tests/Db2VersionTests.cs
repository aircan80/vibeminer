using System.Buffers.Binary;
using System.Text;
using VibeMiner.Formats;
using Xunit;

namespace VibeMiner.Formats.Tests;

/// <summary>#177: WDC3 and WDC4 share WDC5's body after a 72-byte header (magic plus the 68 header-field bytes WDC5 keeps at
/// 136). Every WDC3/WDC4 fixture here is a WDC5 fixture with bytes 4..135 dropped and the magic swapped; WDC3 also has no
/// encrypted-ID lists.</summary>
public sealed class Db2VersionTests
{
    public static TheoryData<string> Versions()=>new(){"WDC3","WDC4"};
    public static TheoryData<string,string> VersionsAndShapes()
    {
        var data=new TheoryData<string,string>();
        foreach(var version in new[]{"WDC3","WDC4"})foreach(var shape in new[]{"kinds","relation","sparse"})data.Add(version,shape);
        return data;
    }

    [Theory]
    [MemberData(nameof(VersionsAndShapes))]
    public async Task A_clear_WDC3_or_WDC4_table_gives_the_same_rows_as_the_equivalent_WDC5_table(string version,string shape)
    {
        var (dbd,make)=Shape(shape);
        var wdc5=make("WDC5");var other=make(version);
        Assert.Equal(wdc5.Bytes.Length-132,other.Bytes.Length);
        var expected=new Sink();var expectedDone=await Read(wdc5.Bytes,dbd,expected);
        var actual=new Sink();var done=await Read(other.Bytes,dbd,actual);
        Assert.NotEmpty(expected.Rows);
        Assert.Equal(Canon(expected.Rows),Canon(actual.Rows));
        Assert.Equal(expectedDone with{ConsumedBytes=other.Bytes.Length},done);
        Assert.Equal("complete",done.Scope);
        Assert.Equal(version,actual.Header!.Format);Assert.Equal(0u,actual.Header.SchemaVersion);
        Assert.Equal("WDC5",expected.Header!.Format);Assert.Equal(5u,expected.Header.SchemaVersion);
        Assert.Equal(expected.Header with{SchemaVersion=0,Format=version,Columns=actual.Header.Columns},actual.Header);
        Assert.Equal(expected.Header.Columns,actual.Header.Columns);
        // The lazy table and lookups read the same file the same way.
        var table=await Open(other.Bytes,dbd);
        Assert.Equal(Canon(expected.Rows),Canon(await ToList(table.ReadAllAsync())));
        foreach(var row in expected.Rows)Assert.Equal(Canon([row]),Canon([Assert.IsType<Db2Lookup.Found>(await table.FindAsync(row.Id)).Row]));
        Assert.IsType<Db2Lookup.Absent>(await table.FindAsync(999));
    }

    [Fact]
    public async Task The_every_kind_fixture_decodes_the_values_it_was_built_with()
    {
        // Pins the shared fixture so the WDC3/WDC4 comparison above compares real values, not two equal failures.
        var (dbd,make)=Shape("kinds");var sink=new Sink();await Read(make("WDC3").Bytes,dbd,sink);
        Assert.Equal(["ID","Name","Value","Pair[0]","Pair[1]","Common","Palette","Small","Bits"],sink.Header!.Columns.Select(c=>c.Name));
        Assert.Equal(["10|0|-|10|alpha|513|20|21|8|200|-1|7","11|0|-|11||65535|10|11|7|100|1|2","12|0|-|12|beta|0|10|11|7|200|0|0","20|1|10|20|alpha|513|20|21|99|200|-1|7"],Canon(sink.Rows));
    }

    [Fact]
    public async Task A_keyed_WDC3_table_is_encrypted_with_or_without_partial_admission()
    {
        var dbd=KeyedDefinition();
        foreach(var allow in new[]{false,true})
        {
            var fixture=Keyed("WDC3");var sink=new Sink();
            var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(fixture.Bytes,dbd,sink,new(AllowPartialEncryptedSections:allow)));
            Assert.Equal("DB2_ENCRYPTED",error.Code);Assert.Null(sink.Header);Assert.Empty(sink.Rows);
            // Also when the keyed body is a hole, the way layer 4 hands over an encrypted chunk.
            var holed=new MemoryByteSource(fixture.Bytes,[new(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1],"E chunk 1")]);
            error=await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.ReadAsync(holed,dbd,"1.2.3.4",new(AllowPartialEncryptedSections:allow),new Sink()));
            Assert.Equal("DB2_ENCRYPTED",error.Code);
            await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.OpenAsync(new MemoryByteSource(fixture.Bytes),dbd,"1.2.3.4",new(AllowPartialEncryptedSections:allow)));
        }
        // The same table as WDC4, with its encrypted-ID list, opens partially.
        Assert.Equal("unencryptedSections",(await Read(Keyed("WDC4").Bytes,dbd,new Sink(),new(AllowPartialEncryptedSections:true))).Scope);
    }

    [Fact]
    public async Task A_partial_keyed_WDC4_table_with_a_hole_names_protected_rows_and_copies()
    {
        var dbd=KeyedDefinition();var fixture=Keyed("WDC4");
        var source=new MemoryByteSource(fixture.Bytes,[new(fixture.Starts[1],fixture.Ends[1]-fixture.Starts[1],"E chunk 1")]);
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.OpenAsync(source,dbd,"1.2.3.4",new()))).Code);
        var table=await Db2Reader.OpenAsync(source,dbd,"1.2.3.4",new(AllowPartialEncryptedSections:true));
        Assert.Equal(new Db2Completion(3,1,1,1,2,1,"unencryptedSections",fixture.Bytes.Length),table.Completion);
        Assert.Equal("WDC4",table.Header.Format);
        Assert.Equal([7u,8u],(await ToList(table.ReadAllAsync())).Select(row=>row.Id));
        var key=123UL.ToString("X16",System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new Db2Lookup.Protected(1,key,21),await table.FindAsync(21));
        Assert.Equal(new Db2Lookup.Protected(1,key,22),await table.FindAsync(22));
        Assert.Equal(new Db2Lookup.Protected(1,key,21),await table.FindAsync(9));
        Assert.IsType<Db2Lookup.Found>(await table.FindAsync(8));
        Assert.IsType<Db2Lookup.NotInReadableSections>(await table.FindAsync(500));
        Assert.True(table.Sections[1].IsEncrypted);Assert.Equal(1,table.Sections[0].ProtectedCopyCount);
        Assert.False(source.ReadAny(fixture.Starts[1],fixture.Ends[1]));
        // The same file decoded as WDC5 gives the same table.
        var wdc5=Keyed("WDC5");
        var reference=await Db2Reader.OpenAsync(new MemoryByteSource(wdc5.Bytes,[new(wdc5.Starts[1],wdc5.Ends[1]-wdc5.Starts[1],"E chunk 1")]),dbd,"1.2.3.4",new(AllowPartialEncryptedSections:true));
        Assert.Equal(reference.Completion with{ConsumedBytes=fixture.Bytes.Length},table.Completion);
        Assert.Equal(reference.Sections,table.Sections);
    }

    [Theory]
    [InlineData("WDBC")][InlineData("WDB2")][InlineData("WDB3")][InlineData("WDB4")][InlineData("WDB5")][InlineData("WDB6")]
    [InlineData("WDC1")][InlineData("WDC2")][InlineData("1SLC")][InlineData("CLS1")][InlineData("WDC6")][InlineData("wdc5")][InlineData("ABCD")]
    public async Task Other_DB2_family_and_unknown_magics_are_refused_by_name(string magic)
    {
        var (dbd,make)=Shape("kinds");
        foreach(var bytes in new[]{make("WDC5").Bytes,make("WDC3").Bytes,make("WDC3").Bytes[..10]})
        {
            var copy=(byte[])bytes.Clone();Encoding.ASCII.GetBytes(magic).CopyTo(copy,0);var sink=new Sink();
            var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(copy,dbd,sink));Assert.Null(sink.Header);
            Assert.Null(Db2Reader.LayoutHash(copy));
            // An unknown magic is sized as WDC5, so a file shorter than 204 bytes is truncated, as the legacy reader said.
            if(copy.Length<204){Assert.Equal(("DB2_INVALID","The WDC5 mandatory204-byte header is truncated."),(error.Code,error.Message));continue;}
            Assert.Equal("DB2_UNSUPPORTED",error.Code);Assert.Contains("'"+magic+"'",error.Message);
            if(magic=="1SLC")Assert.Contains("CLS1",error.Message);
        }
    }

    // A refused magic keeps the legacy check order: WDC5 truncation, then the hole checks, then the magic.
    [Theory]
    [InlineData("WDC2")][InlineData("ABCD")]
    public async Task A_refused_magic_with_holes_fails_in_the_legacy_order(string magic)
    {
        var (dbd,make)=Shape("kinds");var bytes=make("WDC5").Bytes;Encoding.ASCII.GetBytes(magic).CopyTo(bytes,0);
        Assert.True(bytes.Length>300);
        foreach(var allow in new[]{false,true})
        {
            var limits=new Db2Limits(AllowPartialEncryptedSections:allow);
            // Short file: truncated before the holes are looked at, even an out-of-range hole.
            Assert.Equal(("DB2_INVALID","The WDC5 mandatory204-byte header is truncated."),await Fails(bytes[..150],[new(100,10,"E 1")],dbd,limits));
            Assert.Equal(("DB2_INVALID","The WDC5 mandatory204-byte header is truncated."),await Fails(bytes[..150],[new(bytes.Length,10,"E 1")],dbd,limits));
            // Long file: bad hole metadata first, then partial admission, then a hole inside the header, then the magic.
            Assert.Equal("DB2_PROJECTION_INVALID",(await Fails(bytes,[new(bytes.Length,10,"E 1")],dbd,limits)).Code);
            Assert.Equal("DB2_PROJECTION_INVALID",(await Fails(bytes,[..Enumerable.Range(0,129).Select(i=>new Db2Hole(i*2,1,"E "+i))],dbd,limits)).Code);
            Assert.Equal(allow?"BLTE_RANGE_UNAVAILABLE":"DB2_ENCRYPTED",(await Fails(bytes,[new(100,10,"E 1")],dbd,limits)).Code);
            var late=await Fails(bytes,[new(300,10,"E 1")],dbd,limits);
            Assert.Equal(allow?"DB2_UNSUPPORTED":"DB2_ENCRYPTED",late.Code);
            if(allow)Assert.Contains("'"+magic+"'",late.Message);
        }
    }

    // The magic can't be read under a hole, so the file is taken as WDC5: a long one fails on the hole as the legacy reader
    // did, and a short one is truncated whatever its real version.
    [Theory]
    [InlineData("WDC3")][InlineData("WDC4")][InlineData("WDC5")][InlineData("ABCD")]
    public async Task A_hole_over_the_magic_fails_as_a_WDC5_hole(string version)
    {
        var (dbd,make)=Shape("kinds");var bytes=make(version=="ABCD"?"WDC5":version).Bytes;
        if(version=="ABCD")Encoding.ASCII.GetBytes(version).CopyTo(bytes,0);
        Assert.True(bytes.Length>=204);
        foreach(var allow in new[]{false,true})
        {
            var limits=new Db2Limits(AllowPartialEncryptedSections:allow);
            foreach(var hole in new Db2Hole[]{new(0,4,"E 0"),new(2,1,"E 0"),new(3,200,"E 0")})
            {
                var source=new MemoryByteSource(bytes,[hole]);
                var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.ReadAsync(source,dbd,"1.2.3.4",limits,new Sink()));
                Assert.Equal(allow?("BLTE_RANGE_UNAVAILABLE","The requested plaintext range intersects an unavailable encrypted chunk."):("DB2_ENCRYPTED","A partial plaintext source requires explicit partial-section admission."),(error.Code,error.Message));
                Assert.Empty(source.Reads);
            }
            Assert.Equal(("DB2_INVALID","The WDC5 mandatory204-byte header is truncated."),await Fails(bytes[..150],[new(0,4,"E 0")],dbd,limits));
        }
    }

    private static async Task<(string Code,string Message)> Fails(byte[] bytes,Db2Hole[] holes,DbdDocument dbd,Db2Limits limits)
    {
        var sink=new Sink();
        var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.ReadAsync(new MemoryByteSource(bytes,holes),dbd,"1.2.3.4",limits,sink));
        Assert.Null(sink.Header);return (error.Code,error.Message);
    }

    [Fact]
    public async Task An_unprintable_magic_is_named_in_hex()
    {
        var (dbd,make)=Shape("kinds");var bytes=make("WDC3").Bytes;bytes[0]=0;bytes[1]=0x01;bytes[2]=0xFF;bytes[3]=0x7F;
        var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink()));
        Assert.Equal("DB2_UNSUPPORTED",error.Code);Assert.Contains("0x0001FF7F",error.Message);
    }

    [Fact]
    public async Task WDC5_keeps_its_schema_version_and_schema_string_rules()
    {
        var (dbd,make)=Shape("kinds");var bytes=make("WDC5").Bytes;Put(bytes,4,4);
        var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink()));
        Assert.Equal(("DB2_UNSUPPORTED","Only WDC5 schema version5 is admitted."),(error.Code,error.Message));
        Assert.Null(Db2Reader.LayoutHash(bytes));
        error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(make("WDC5").Bytes[..100],dbd,new Sink()));
        Assert.Equal(("DB2_INVALID","The WDC5 mandatory204-byte header is truncated."),(error.Code,error.Message));
    }

    [Fact]
    public void LayoutHash_reads_all_three_headers_and_refuses_short_ones()
    {
        var (_,make)=Shape("kinds");
        foreach(var version in new[]{"WDC3","WDC4","WDC5"})
        {
            var bytes=make(version).Bytes;var size=version=="WDC5"?204:72;
            Assert.Equal("01020304",Db2Reader.LayoutHash(bytes));Assert.Equal("01020304",Db2Reader.LayoutHash(bytes.AsSpan(0,size)));
            Assert.Null(Db2Reader.LayoutHash(bytes.AsSpan(0,size-1)));
        }
        Assert.Null(Db2Reader.LayoutHash([]));Assert.Null(Db2Reader.LayoutHash("WDC"u8));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task Every_truncation_fails_with_a_DB2_code_and_never_returns_rows(string version)
    {
        foreach(var shape in new[]{"kinds","relation","sparse"})
        {
            var (dbd,make)=Shape(shape);var bytes=make(version).Bytes;
            for(var length=0;length<bytes.Length;length++)
            {
                var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes[..length],dbd,new Sink()));
                Assert.StartsWith("DB2_",error.Code);
                if(length>=4)Assert.DoesNotContain("WDC5",error.Message);
                if(length is >=4 and <72)Assert.Equal(("DB2_INVALID","The "+version+" mandatory 72-byte header is truncated."),(error.Code,error.Message));
            }
        }
        var keyed=Keyed(version).Bytes;var limits=new Db2Limits(AllowPartialEncryptedSections:true);
        for(var length=0;length<keyed.Length;length++)
            Assert.StartsWith("DB2_",(await Assert.ThrowsAsync<Db2FormatException>(()=>Read(keyed[..length],KeyedDefinition(),new Sink(),limits))).Code);
    }

    // Header-field offsets inside the 68-byte body (WDC3/WDC4 put it at 4).
    [Theory]
    [InlineData(0,"DB2_LIMIT")]   // record count
    [InlineData(4,"DB2_LIMIT")]   // field count
    [InlineData(8,"DB2_LIMIT")]   // record size
    [InlineData(12,"DB2_INVALID")] // string table size
    [InlineData(40,"DB2_UNSUPPORTED")] // total field count must equal field count
    [InlineData(44,"DB2_INVALID")] // packed-data offset
    [InlineData(48,"DB2_UNSUPPORTED")] // lookup column count
    [InlineData(52,"DB2_UNSUPPORTED")] // storage info size must be field count x 24
    [InlineData(56,"DB2_LIMIT")]  // common data size
    [InlineData(60,"DB2_LIMIT")]  // pallet data size
    [InlineData(64,"DB2_LIMIT")]  // section count
    public async Task Header_counts_and_sizes_at_uint_max_fail_with_their_DB2_code(int field,string code)
    {
        foreach(var version in new[]{"WDC3","WDC4","WDC5"})
        {
            var (dbd,make)=Shape("kinds");var bytes=make(version).Bytes;Put(bytes,(version=="WDC5"?136:4)+field,uint.MaxValue);
            var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink()));
            Assert.Equal(code,error.Code);
        }
    }

    [Theory]
    [InlineData(8)][InlineData(12)][InlineData(16)][InlineData(20)][InlineData(24)][InlineData(28)][InlineData(32)][InlineData(36)]
    public async Task Section_header_fields_at_uint_max_fail_as_limit_invalid_or_unsupported(int field)
    {
        foreach(var version in new[]{"WDC3","WDC4"})foreach(var shape in new[]{"kinds","sparse"})
        {
            var (dbd,make)=Shape(shape);var bytes=make(version).Bytes;Put(bytes,72+field,uint.MaxValue);
            var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink()));
            Assert.Contains(error.Code,new[]{"DB2_LIMIT","DB2_INVALID","DB2_UNSUPPORTED"});
        }
    }

    [Fact]
    public async Task Counts_near_int_max_inside_raised_limits_fail_as_invalid_not_as_an_overflow()
    {
        var (dbd,make)=Shape("kinds");var bytes=make("WDC3").Bytes;Put(bytes,4+64,int.MaxValue);
        var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink(),new(MaxSections:int.MaxValue)));
        Assert.Equal("DB2_INVALID",error.Code);
        bytes=make("WDC3").Bytes;Put(bytes,4+0,int.MaxValue);
        // Within MaxRows, the record count then disagrees with the sections.
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<Db2FormatException>(()=>Read(bytes,dbd,new Sink(),new(MaxRows:int.MaxValue)))).Code);
    }

    // ---- fixtures ----

    private sealed record Col(short Bits,ushort Position,ushort Offset,ushort Width,uint Kind,uint P1,uint P2,uint P3,byte[] Aux);
    private sealed record Sec(byte[][] Records,uint[] Ids,byte[] Strings,(uint Target,uint Source)[] Copies,byte[] Relations,ulong Key=0,uint[]? Encrypted=null);
    private sealed record Built(byte[] Bytes,long[] Starts,long[] Ends);

    private static (DbdDocument Definition,Func<string,Built> Make) Shape(string shape)=>shape switch
    {
        "kinds"=>(Definition("int ID\nstring Name\nint Value\nint Pair\nint Common\nint Palette\nint Small\nint Bits",
            "$noninline,id$ID<u32>\nName\nValue<u16>\nPair<u32>[2]\nCommon<u32>\nPalette<u32>\nSmall<8>\nBits<u8>"),Kinds),
        "relation"=>(Definition("int ID\nint Value\nint Parent","$noninline,id$ID<u32>\nValue<u32>\n$noninline,relation$Parent<u32>"),Relation),
        _=>(Definition("int ID\nstring Name\nint Value","$noninline,id$ID<u32>\nName\nValue<u32>"),Sparse),
    };

    // Every storage kind across two sections, with relative strings and a copy. Record: Name (None, bytes 0-3),
    // Value (None 16, bytes 4-5), then packed byte 6: pallet-array index bit 0, pallet index bit 1, signed 3 bits, unsigned 3 bits.
    private static Built Kinds(string version)
    {
        var columns=new[]
        {
            new Col(0,0,0,32,0,0,0,0,[]),new Col(16,4,32,16,0,0,0,0,[]),
            new Col(32,6,48,1,4,0,1,2,U32(10,11,20,21)),new Col(32,6,0,0,2,7,0,0,U32(10,8,20,99)),
            new Col(32,6,49,1,3,1,1,0,U32(100,200)),new Col(32,6,50,3,5,2,3,0,[]),new Col(32,6,53,3,1,5,3,0,[]),
        };
        // 3 records x 7 bytes; strings concatenate as "\0alpha\0" + "\0beta\0", so alpha is at 21+1 and beta at 21+8.
        byte[] Record(int global,uint nameAt,ushort value,byte packed)
        {var r=new byte[7];Put(r,0,nameAt==0?0:(uint)(21+nameAt-global*7));BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4),value);r[6]=packed;return r;}
        return Build(version,columns,7,[
            new([Record(0,1,513,0b111_111_1_1),Record(1,0,65535,0b010_001_0_0)],[10,11],"\0alpha\0"u8.ToArray(),[],[]),
            new([Record(2,8,0,0b000_000_1_0)],[12],"\0beta\0"u8.ToArray(),[(20,10)],[]),
        ],packedOffset:6);
    }

    // A noninline parent from each section's relationship block (key = ordinal inside the section).
    private static Built Relation(string version)=>Build(version,[new Col(0,0,0,32,0,0,0,0,[])],4,[
        new([U32(100),U32(101)],[1,2],[],[(5,2)],Relationship((7,1),(9,0))),
        new([U32(102)],[3],[],[],Relationship((0,0))),
    ],lookup:1);

    // Sparse flags 5: offset map, inline strings, records padded to 4, copies, two sections.
    private static Built Sparse(string version)
    {
        static byte[] Record(string name,uint value){var text=Encoding.UTF8.GetBytes(name+"\0");var r=new byte[(text.Length+4+3)/4*4];text.CopyTo(r,0);Put(r,text.Length,value);return r;}
        return Build(version,[new Col(0,0,0,32,0,0,0,0,[]),new Col(0,4,32,32,0,0,0,0,[])],8,[
            new([Record("one",11),Record("three",33)],[1,3],[],[(9,3)],[]),
            new([Record("x",5)],[4],[],[],[]),
        ],flags:5);
    }

    private static DbdDocument KeyedDefinition()=>Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
    // Section 0 is clear (7, copy 8 of 7, copy 9 of hidden 21); section 1 is keyed and lists 21 and 22.
    private static Built Keyed(string version)=>Build(version,[new Col(0,0,0,32,0,0,0,0,[])],4,[
        new([U32(99)],[7],[],[(8,7),(9,21)],[]),
        new([new byte[4],new byte[4]],[0,0],[],[],[],123,[21,22]),
    ]);

    // Writes a WDC5 file whose offsets assume the target header size, then drops bytes 4..135 and swaps the magic for
    // WDC3/WDC4. WDC3 gets no encrypted-ID lists.
    private static Built Build(string version,Col[] columns,int recordSize,Sec[] sections,ushort flags=4,uint packedOffset=0,uint lookup=0)
    {
        var sparse=(flags&1)!=0;var headerSize=version=="WDC5"?204:72;var lists=version!="WDC3";
        var palette=columns.Where(c=>c.Kind==3).Concat(columns.Where(c=>c.Kind==4)).SelectMany(c=>c.Aux).ToArray();
        var common=columns.Where(c=>c.Kind==2).SelectMany(c=>c.Aux).ToArray();
        long offset=headerSize+sections.Length*40+columns.Length*28+palette.Length+common.Length+(lists?sections.Where(s=>s.Key!=0).Sum(s=>4+(s.Encrypted?.Length??0)*4):0);
        var starts=new long[sections.Length];var ends=new long[sections.Length];
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Encoding.UTF8.GetBytes("authored").CopyTo(header,8);
        Put(header,136,(uint)sections.Sum(s=>s.Records.Length));Put(header,140,(uint)columns.Length);Put(header,144,(uint)recordSize);Put(header,148,(uint)sections.Sum(s=>s.Strings.Length));
        Put(header,152,0x11223344);Put(header,156,0x01020304);BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172),flags);Put(header,176,(uint)columns.Length);Put(header,180,packedOffset);
        Put(header,184,lookup);Put(header,188,(uint)columns.Length*24);Put(header,192,(uint)common.Length);Put(header,196,(uint)palette.Length);Put(header,200,(uint)sections.Length);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);
        if(version=="WDC5")writer.Write(header);else{writer.Write(Encoding.ASCII.GetBytes(version));writer.Write(header,136,68);}
        for(var i=0;i<sections.Length;i++)
        {
            var s=sections[i];var recordBytes=s.Records.Sum(r=>r.Length);var ids=(flags&4)!=0?s.Ids.Length*4:0;
            starts[i]=offset;
            writer.Write(s.Key);writer.Write((uint)offset);writer.Write((uint)s.Records.Length);writer.Write((uint)s.Strings.Length);writer.Write(sparse?(uint)(offset+recordBytes):0u);
            writer.Write((uint)ids);writer.Write((uint)s.Relations.Length);writer.Write(sparse?(uint)s.Records.Length:0u);writer.Write((uint)s.Copies.Length);
            offset+=recordBytes+s.Strings.Length+ids+s.Copies.Length*8+s.Relations.Length+(sparse?s.Records.Length*10:0);ends[i]=offset;
        }
        foreach(var c in columns){writer.Write(c.Bits);writer.Write(c.Position);}
        foreach(var c in columns){writer.Write(c.Offset);writer.Write(c.Width);writer.Write((uint)c.Aux.Length);writer.Write(c.Kind);writer.Write(c.P1);writer.Write(c.P2);writer.Write(c.P3);}
        writer.Write(palette);writer.Write(common);
        if(lists)foreach(var s in sections.Where(s=>s.Key!=0)){writer.Write((uint)(s.Encrypted?.Length??0));foreach(var id in s.Encrypted??[])writer.Write(id);}
        for(var i=0;i<sections.Length;i++)
        {
            var s=sections[i];foreach(var r in s.Records)writer.Write(r);writer.Write(s.Strings);
            if((flags&4)!=0)foreach(var id in s.Ids)writer.Write(id);
            foreach(var (target,source) in s.Copies){writer.Write(target);writer.Write(source);}
            if(sparse)
            {
                var at=starts[i];foreach(var r in s.Records){writer.Write((uint)at);writer.Write((ushort)r.Length);at+=r.Length;}
                writer.Write(s.Relations);foreach(var id in s.Ids)writer.Write(id);
            }
            else writer.Write(s.Relations);
        }
        return new(output.ToArray(),starts,ends);
    }

    private static byte[] Relationship(params (uint Parent,uint Key)[] pairs)
    {
        var bytes=new byte[12+pairs.Length*8];Put(bytes,0,(uint)pairs.Length);Put(bytes,4,pairs.Min(p=>p.Parent));Put(bytes,8,pairs.Max(p=>p.Parent));
        for(var i=0;i<pairs.Length;i++){Put(bytes,12+i*8,pairs[i].Parent);Put(bytes,16+i*8,pairs[i].Key);}
        return bytes;
    }
    private static DbdDocument Definition(string columns,string fields)=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\n"+columns+"\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fields+"\n"));
    private static byte[] U32(params uint[] values){var bytes=new byte[values.Length*4];for(var i=0;i<values.Length;i++)Put(bytes,i*4,values[i]);return bytes;}
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    private static List<string> Canon(IEnumerable<Db2Row> rows)=>rows.Select(r=>r.Id+"|"+(r.IsCopy?1:0)+"|"+(r.CopySourceId?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"-")+"|"+string.Join("|",r.Values)).ToList();
    private static async Task<List<Db2Row>> ToList(IAsyncEnumerable<Db2Row> rows){var list=new List<Db2Row>();await foreach(var row in rows)list.Add(row);return list;}
    private static Task<Db2Completion> Read(byte[] bytes,DbdDocument definition,Sink sink,Db2Limits? limits=null)=>Db2Reader.ReadAsync(new MemoryByteSource(bytes),definition,"1.2.3.4",limits??new(),sink);
    private static Task<Db2Table> Open(byte[] bytes,DbdDocument definition)=>Db2Reader.OpenAsync(new MemoryByteSource(bytes),definition,"1.2.3.4",new());
    private sealed class Sink:IDb2Consumer
    {
        public Db2TableHeader? Header;public List<Db2Row> Rows=[];
        public ValueTask HeaderAsync(Db2TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return ValueTask.CompletedTask;}
    }
}
