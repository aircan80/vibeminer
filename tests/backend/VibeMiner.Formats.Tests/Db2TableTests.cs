using System.Buffers.Binary;
using System.Text;
using VibeMiner.Formats;
using Xunit;

namespace VibeMiner.Formats.Tests;

// Smoke tests for the lazy layer 5 API. The full legacy suites are ported separately; fixtures are synthetic bytes only.
public sealed class Db2TableTests
{
    private const int FixedRecordsAt=204+40+4*28;

    [Fact]
    public async Task Fixed_table_opens_without_record_bodies_and_finds_one_record_with_its_strings()
    {
        var (bytes,definition)=ScalarFixture();var source=new MemoryByteSource(bytes);
        var table=await Db2Reader.OpenAsync(source,definition,"1.2.3.4",new());
        // Records [356,390) and the string block [390,403) stay unread; the ID list and copy table are read.
        Assert.False(source.ReadAny(FixedRecordsAt,FixedRecordsAt+34+13));
        Assert.Equal(new Db2Completion(2,2,1,0,0,0,"complete",bytes.Length),table.Completion);
        Assert.Equal([new Db2Section(0,2,1,"0000000000000000",false)],table.Sections);

        source.ClearReads();
        var found=Assert.IsType<Db2Lookup.Found>(await table.FindAsync(uint.MaxValue));
        Assert.Equal(["4294967295","-128","-0.5","hello","9223372036854775817"],found.Row.Values);
        Assert.True(source.ReadOnlyWithin((FixedRecordsAt+17,FixedRecordsAt+34),(FixedRecordsAt+34,FixedRecordsAt+34+13)));
        Assert.Contains(source.Reads,read=>read.Offset==FixedRecordsAt+17&&read.Length==17);

        var copy=Assert.IsType<Db2Lookup.Found>(await table.FindAsync(8));
        Assert.Equal(new Db2Row(8,true,1,copy.Row.Values),copy.Row);Assert.Equal("café",copy.Row.Values[3]);
        Assert.IsType<Db2Lookup.Absent>(await table.FindAsync(5));

        var all=new List<Db2Row>();await foreach(var row in table.ReadAllAsync())all.Add(row);
        Assert.Equal([1u,uint.MaxValue,8u],all.Select(row=>row.Id));
        var sink=new Sink();
        Assert.Equal(table.Completion,await Db2Reader.ReadAsync(new MemoryByteSource(bytes),definition,"1.2.3.4",new(),sink));
        Assert.Equal(table.Header with{Columns=[]},sink.Header! with{Columns=[]});Assert.Equal(table.Header.Columns,sink.Header.Columns);Assert.Equal(all.Select(row=>string.Join("|",row.Values)),sink.Rows.Select(row=>string.Join("|",row.Values)));
    }

    [Fact]
    public async Task Sparse_table_opens_without_record_bodies_and_finds_only_that_record()
    {
        var fixture=SparseFixture([Pad(SparseRecord("abc")),Pad(SparseRecord("hello"))],[10,20]);var source=new MemoryByteSource(fixture.Bytes);
        var table=await Db2Reader.OpenAsync(source,SparseDefinition(),"1.2.3.4",new());
        Assert.False(source.ReadAny(fixture.RecordsAt,fixture.RecordsEnd));
        source.ClearReads();
        var found=Assert.IsType<Db2Lookup.Found>(await table.FindAsync(20));
        Assert.Equal("hello",found.Row.Values[1]);Assert.Equal(["20","hello","2","1.5","-1","200","305419896"],found.Row.Values);
        var second=(fixture.RecordsAt+20,fixture.RecordsEnd);
        Assert.Equal([(second.Item1,20)],source.Reads);
        var rows=new List<Db2Row>();await foreach(var row in table.ReadSectionAsync(0))rows.Add(row);
        Assert.Equal([10u,20u],rows.Select(row=>row.Id));
    }

    [Fact]
    public async Task Keyed_partial_table_reports_protected_IDs_and_never_reads_its_hole()
    {
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(99),[7],[],[(8,7),(9,21)]),new(new byte[8],[0,0],[],[],123,[21,22])]);
        var keyedAt=(long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(204+40+8));
        var source=new MemoryByteSource(bytes,[new(keyedAt,bytes.Length-keyedAt,"E chunk 1")]);
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.OpenAsync(source,definition,"1.2.3.4",new()))).Code);

        var table=await Db2Reader.OpenAsync(source,definition,"1.2.3.4",new(AllowPartialEncryptedSections:true));
        Assert.Equal(new Db2Completion(3,1,1,1,2,1,"unencryptedSections",bytes.Length),table.Completion);
        Assert.Equal(new Db2Section(1,2,0,"000000000000007B",true),table.Sections[1]);
        // Copy 9 is declared in readable section 0 but excluded: its chain ends at encrypted ID 21.
        Assert.Equal(new Db2Section(0,1,2,"0000000000000000",false,ProtectedCopyCount:1),table.Sections[0]);Assert.Equal(0,table.UnresolvedCopies);
        var refused=await Assert.ThrowsAsync<Db2FormatException>(async()=>{await foreach(var _ in table.ReadSectionAsync(1)){}});
        Assert.Equal("DB2_ENCRYPTED",refused.Code);Assert.Contains("000000000000007B",refused.Message);

        Assert.Equal(new Db2Lookup.Protected(1,"000000000000007B",21),await table.FindAsync(21));
        // A copy whose chain ends at 21 names that ID, so the caller knows the copy itself is not in section 1.
        Assert.Equal(new Db2Lookup.Protected(1,"000000000000007B",21),await table.FindAsync(9));
        Assert.IsType<Db2Lookup.NotInReadableSections>(await table.FindAsync(5));
        Assert.Equal(["8","99"],Assert.IsType<Db2Lookup.Found>(await table.FindAsync(8)).Row.Values);
        var rows=new List<Db2Row>();await foreach(var row in table.ReadAllAsync())rows.Add(row);
        Assert.Equal([7u,8u],rows.Select(row=>row.Id));

        var sink=new Sink();
        Assert.Equal(table.Completion,await Db2Reader.ReadAsync(source,definition,"1.2.3.4",new(AllowPartialEncryptedSections:true),sink));
        Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));
        // A hole outside a keyed body is refused before any row.
        var outside=new MemoryByteSource(bytes,[new(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(204+8)),4,"E chunk 0")]);
        Assert.Equal("DB2_PROJECTION_UNSUPPORTED",(await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.OpenAsync(outside,definition,"1.2.3.4",new(AllowPartialEncryptedSections:true)))).Code);
    }

    private const string NoSource="A copy source has no readable record or declared encrypted identity.";

    [Fact]
    public async Task Broken_copy_chains_open_and_count_but_throw_the_legacy_error_only_when_read()
    {
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
        // 9 and 16 resolve, 10 names an unknown source, 12 and 13 form a cycle, 14 chains past MaxCopyDepth 2.
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(99,100),[7,8],[],[(9,7),(10,11),(12,13),(13,12),(14,16),(16,9)])]);
        var limits=new Db2Limits(MaxCopyDepth:2);
        var table=await Db2Reader.OpenAsync(new MemoryByteSource(bytes),definition,"1.2.3.4",limits);
        // Completion keeps the legacy shape and still says complete; UnresolvedCopies says a full read will fail.
        Assert.Equal(new Db2Completion(2,2,2,0,0,0,"complete",bytes.Length),table.Completion);
        Assert.Equal(4,table.UnresolvedCopies);
        Assert.Equal(new Db2Section(0,2,6,"0000000000000000",false,0,4),table.Sections[0]);

        Assert.Equal(["9","99"],Assert.IsType<Db2Lookup.Found>(await table.FindAsync(9)).Row.Values);
        var copy=Assert.IsType<Db2Lookup.Found>(await table.FindAsync(16)).Row;Assert.Equal((16u,true,(uint?)9),(copy.Id,copy.IsCopy,copy.CopySourceId));Assert.Equal(["16","99"],copy.Values);
        await AssertFails("DB2_INVALID",NoSource,()=>table.FindAsync(10));
        await AssertFails("DB2_INVALID","Copy declarations contain a cycle.",()=>table.FindAsync(12));
        await AssertFails("DB2_INVALID","Copy declarations contain a cycle.",()=>table.FindAsync(13));
        await AssertFails("DB2_LIMIT","Copy resolution exceeds its admitted chain depth.",()=>table.FindAsync(14));

        // Every physical row and the copies before the first broken one are emitted before the error.
        var rows=new List<Db2Row>();
        await AssertFails("DB2_INVALID",NoSource,async()=>{await foreach(var row in table.ReadAllAsync())rows.Add(row);});
        Assert.Equal([7u,8u,9u],rows.Select(row=>row.Id));
        rows.Clear();
        await AssertFails("DB2_INVALID",NoSource,async()=>{await foreach(var row in table.ReadSectionAsync(0))rows.Add(row);});
        Assert.Equal([7u,8u,9u],rows.Select(row=>row.Id));
        var sink=new Sink();
        await AssertFails("DB2_INVALID",NoSource,()=>Db2Reader.ReadAsync(new MemoryByteSource(bytes),definition,"1.2.3.4",limits,sink));
        Assert.NotNull(sink.Header);Assert.Equal([7u,8u,9u],sink.Rows.Select(row=>row.Id));
    }

    [Fact]
    public async Task Inline_IDs_are_read_from_record_bodies_in_bounded_batches_and_rows_decode_on_demand()
    {
        var definition=Definition("int ID\nint Value","$id$ID<u32>\nValue<u32>");
        const int count=20_000;var records=new byte[count*8];
        for(var i=0;i<count;i++){Put(records,i*8,(uint)(i*2+1));Put(records,i*8+4,(uint)(i*3));}
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[]),new(0,4,32,32,0,0,0,0,0,[])],8,[new(records,[],[],[(100_001,39_999)])],flags:0);
        var start=204+40+2*28;var source=new MemoryByteSource(bytes);
        var table=await Db2Reader.OpenAsync(source,definition,"1.2.3.4",new());
        // Open reads the record area once, in three 64 KiB-bounded batches, only to take the IDs, then the copy table.
        var bodyReads=source.Reads.Where(read=>read.Offset>=start&&read.Offset<start+records.Length).ToArray();
        Assert.Equal([(start,8192*8),(start+8192*8,8192*8),(start+2*8192*8,(count-2*8192)*8)],bodyReads);
        Assert.Equal(new Db2Completion(count,count,1,0,0,0,"complete",bytes.Length),table.Completion);

        source.ClearReads();
        Assert.Equal(["39999","59997"],Assert.IsType<Db2Lookup.Found>(await table.FindAsync(39_999)).Row.Values);
        Assert.Equal([(start+19_999*8L,8)],source.Reads);
        Assert.Equal(["20001","30000"],Assert.IsType<Db2Lookup.Found>(await table.FindAsync(20_001)).Row.Values);
        var copy=Assert.IsType<Db2Lookup.Found>(await table.FindAsync(100_001)).Row;Assert.Equal((100_001u,true,(uint?)39_999),(copy.Id,copy.IsCopy,copy.CopySourceId));Assert.Equal(["100001","59997"],copy.Values);
        Assert.IsType<Db2Lookup.Absent>(await table.FindAsync(2));
        var total=0;await foreach(var row in table.ReadAllAsync()){Assert.Equal(row.IsCopy?"100001":(total*2+1).ToString(System.Globalization.CultureInfo.InvariantCulture),row.Values[0]);total++;}
        Assert.Equal(count+1,total);
    }

    [Fact]
    public async Task A_read_that_touches_a_hole_fails_with_the_legacy_range_error_before_any_callback()
    {
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(99),[7],[],[]),new(new byte[8],[0,0],[],[],123,[21,22])]);
        var limits=new Db2Limits(AllowPartialEncryptedSections:true);
        // The header, and the keyed section's encrypted-ID list right after the descriptors.
        foreach(var hole in new Db2Hole[]{new(0,204,"E chunk 0"),new(204+2*40+28+4,8,"E chunk 1")})
        {
            await AssertFails("BLTE_RANGE_UNAVAILABLE","The requested plaintext range intersects an unavailable encrypted chunk.",
                ()=>Db2Reader.OpenAsync(new MemoryByteSource(bytes,[hole]),definition,"1.2.3.4",limits));
            var sink=new Sink();
            await AssertFails("BLTE_RANGE_UNAVAILABLE","The requested plaintext range intersects an unavailable encrypted chunk.",
                ()=>Db2Reader.ReadAsync(new MemoryByteSource(bytes,[hole]),definition,"1.2.3.4",limits,sink));
            Assert.Null(sink.Header);Assert.Empty(sink.Rows);
        }
    }

    private static async Task AssertFails(string code,string message,Func<Task> action)
    {
        var error=await Assert.ThrowsAsync<Db2FormatException>(action);
        Assert.Equal(code,error.Code);Assert.Equal(message,error.Message);
    }

    private sealed class Sink:IDb2Consumer
    {
        public Db2TableHeader? Header;public List<Db2Row> Rows=[];
        public ValueTask HeaderAsync(Db2TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return ValueTask.CompletedTask;}
    }

    // Fixed-table builder, as in the legacy Wdc5TableReaderTests.
    private sealed record Column(short Bits,ushort Position,ushort Offset,ushort Width,int Additional,uint Kind,uint P1,uint P2,uint P3,byte[] Aux);
    private sealed record Part(byte[] Records,uint[] Ids,byte[] Strings,(uint Target,uint Source)[] Copies,ulong Key=0,uint[]? EncryptedIds=null);
    private static DbdDocument Definition(string columns,string fields)=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\n"+columns+"\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fields+"\n"));
    private static (byte[] Bytes,DbdDocument Definition) ScalarFixture()
    {
        var columns=new[]{new Column(24,0,0,8,0,0,0,0,0,[]),new Column(0,1,8,32,0,0,0,0,0,[]),new Column(0,5,40,32,0,0,0,0,0,[]),new Column(-32,9,72,64,0,0,0,0,0,[])};
        var records=new byte[34];records[0]=255;records[17]=128;BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(1),1.25f);BinaryPrimitives.WriteSingleLittleEndian(records.AsSpan(18),-0.5f);
        BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(5),30);BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(22),19);
        BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(9),ulong.MaxValue);BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(26),(1UL<<63)+9);
        var definition=Definition("int ID\nint Small\nfloat Weight\nstring Name\nint Wide","$noninline,id$ID<u32>\nSmall<8>\nWeight\nName\nWide<u64>");
        return(Build(columns,17,[new(records,[1,uint.MaxValue],Encoding.UTF8.GetBytes("\0café\0hello\0"),[(8,1)])]),definition);
    }
    private static byte[] Build(Column[] columns,int recordSize,Part[] sections,ushort flags=4,uint packedOffset=0)
    {
        var palette=columns.Where(c=>c.Kind==3).Concat(columns.Where(c=>c.Kind==4)).SelectMany(c=>c.Aux).ToArray();var common=columns.Where(c=>c.Kind==2).SelectMany(c=>c.Aux).ToArray();
        var prefix=204+sections.Length*40+columns.Length*28+palette.Length+common.Length+sections.Where(s=>s.Key!=0).Sum(s=>4+(s.EncryptedIds?.Length??0)*4);
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Encoding.UTF8.GetBytes("authored").CopyTo(header,8);
        Put(header,136,(uint)sections.Sum(s=>s.Records.Length/recordSize));Put(header,140,(uint)columns.Length);Put(header,144,(uint)recordSize);Put(header,148,(uint)sections.Sum(s=>s.Strings.Length));
        Put(header,152,0x11223344);Put(header,156,0x01020304);BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172),flags);Put(header,176,(uint)columns.Length);Put(header,180,packedOffset);Put(header,188,(uint)columns.Length*24);Put(header,192,(uint)common.Length);Put(header,196,(uint)palette.Length);Put(header,200,(uint)sections.Length);
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
        return output.ToArray();
    }

    // Sparse builder (flags 5, one section, narrow layout), as in the legacy Wdc5SparseTests.
    private sealed record SparseBytes(byte[] Bytes,int RecordsAt,int RecordsEnd);
    private static DbdDocument SparseDefinition()=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID\nstring Name\nint Quality\nfloat Ratio\nint Budget\nint Unknown?\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nName\nQuality<u8>\nRatio\nBudget<16>[2]\nUnknown<u8>\n"));
    private static byte[] SparseRecord(string name)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(Encoding.UTF8.GetBytes(name));writer.Write((byte)0);
        writer.Write((byte)2);writer.Write(1.5f);writer.Write((short)-1);writer.Write((short)200);writer.Write(0x12345678u);return output.ToArray();
    }
    private static byte[] Pad(byte[] record)=>[..record,..new byte[(4-record.Length%4)%4]];
    private static SparseBytes SparseFixture(byte[][] records,uint[] ids)
    {
        var start=204+40+5*28;var end=start+records.Sum(record=>record.Length);
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Put(header,136,(uint)ids.Length);Put(header,140,5);Put(header,144,24);
        Put(header,152,0x11223344);Put(header,156,0x01020304);BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(172),5);Put(header,176,5);Put(header,180,24);Put(header,188,120);Put(header,200,1);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(header);
        writer.Write(0UL);writer.Write((uint)start);writer.Write((uint)ids.Length);writer.Write(0u);writer.Write((uint)end);writer.Write((uint)ids.Length*4);writer.Write(0u);writer.Write((uint)ids.Length);writer.Write(0u);
        short[] bits=[0,24,0,16,0];ushort[] positions=[0,4,5,9,13];ushort[] widths=[32,8,32,32,32];
        for(var index=0;index<5;index++){writer.Write(bits[index]);writer.Write(positions[index]);}
        for(var index=0;index<5;index++){writer.Write((ushort)(positions[index]*8));writer.Write(widths[index]);for(var item=0;item<5;item++)writer.Write(0u);}
        foreach(var record in records)writer.Write(record);foreach(var id in ids)writer.Write(id);
        var at=start;foreach(var record in records){writer.Write((uint)at);writer.Write((ushort)record.Length);at+=record.Length;}foreach(var id in ids)writer.Write(id);
        return new(output.ToArray(),start,end);
    }
    private static byte[] U32(params uint[] values){var bytes=new byte[values.Length*4];for(var i=0;i<values.Length;i++)Put(bytes,i*4,values[i]);return bytes;}
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
}
