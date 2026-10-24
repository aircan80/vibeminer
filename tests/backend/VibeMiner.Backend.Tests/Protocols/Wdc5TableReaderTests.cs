using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5TableReaderTests
{
    [Fact]
    public async Task Decodes_exact_signed_unsigned_float_strings_and_copy_IDs_with_async_caller_owned_input()
    {
        var fixture=ScalarFixture();var sink=new Sink();using var input=new AsyncInput(fixture.Bytes);
        var done=await Read(input,fixture.Definition,sink);
        Assert.Equal(new Wdc5Completion(2,2,1,0,0,0,"complete",fixture.Bytes.Length),done);
        Assert.Equal("01020304",sink.Header!.LayoutHash);Assert.Equal("11223344",sink.Header.TableHash);
        Assert.Equal(["ID","Small","Weight","Name","Wide"],sink.Header.Columns.Select(column=>column.Name));
        Assert.Equal(["1","-1","1.25","café","18446744073709551615"],sink.Rows[0].Values);
        Assert.Equal(["4294967295","-128","-0.5","hello","9223372036854775817"],sink.Rows[1].Values);
        Assert.Equal(["8","-1","1.25","café","18446744073709551615"],sink.Rows[2].Values);
        Assert.True(sink.Rows[2].IsCopy);Assert.Equal(1u,sink.Rows[2].CopySourceId);Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Grouped_pallet_and_pallet_array_auxiliary_blocks_common_target_overrides_and_signed_bits_are_distinct()
    {
        var columns=new[]{new Column(32,0,0,1,16,4,0,1,2,U32(10,11,20,21)),new Column(32,0,0,0,16,2,7,0,0,U32(10,8,20,99)),new Column(32,0,1,1,8,3,1,1,0,U32(100,200)),new Column(32,0,2,3,0,5,2,3,0,[])};
        var definition=Definition("int ID\nint Pair\nint Common\nint Palette\nint Small","$noninline,id$ID<u32>\nPair<u32>[2]\nCommon<u32>\nPalette<u32>\nSmall<8>");
        var bytes=Build(columns,1,[new([0b11101,0b00110],[10,11],[],[(20,10)])]);var sink=new Sink();
        await Read(new AsyncInput(bytes),definition,sink);
        Assert.Equal(["ID","Pair[0]","Pair[1]","Common","Palette","Small"],sink.Header!.Columns.Select(column=>column.Name));
        Assert.Equal(["10","20","21","8","100","-1"],sink.Rows[0].Values);
        Assert.Equal(["11","10","11","7","200","1"],sink.Rows[1].Values);
        Assert.Equal(["20","20","21","99","100","-1"],sink.Rows[2].Values);
    }

    [Fact]
    public async Task Packed_offset_relative_union_parameters_match_observed_ItemClass_shape()
    {
        var columns=new[]{new Column(0,0,0,32,0,0,0,0,0,[]),new Column(32,4,32,6,0,5,0,6,0,[]),new Column(32,4,38,2,8,3,6,2,0,U32(0x3f800000,0x3f000000)),new Column(32,4,40,2,0,5,8,2,0,[])};
        var records=new byte[8];BinaryPrimitives.WriteInt32LittleEndian(records,9);records[4]=0b01000101;records[5]=0b11;
        var definition=Definition("int ID\nlocstring ClassName_lang\nint ClassID\nfloat PriceModifier\nint Flags","$noninline,id$ID<32>\nClassName_lang\nClassID<8>\nPriceModifier\nFlags<u8>");
        var bytes=Build(columns,8,[new(records,[1],"\0Weapons\0"u8.ToArray(),[])],packedOffset:4);var sink=new Sink();
        await Read(new AsyncInput(bytes),definition,sink);
        Assert.Equal(["1","Weapons","5","0.5","255"],Assert.Single(sink.Rows).Values);
    }

    [Theory]
    [InlineData(0, 8, 255ul, "<32>", "255")]
    [InlineData(0, 32, 4294967295ul, "<8>", "-1")]
    [InlineData(1, 12, 4095ul, "<8>", "-1")]
    [InlineData(5, 2, 3ul, "<8>", "-1")]
    [InlineData(5, 2, 3ul, "<u8>", "255")]
    [InlineData(2, 32, 4294967295ul, "<8>", "-1")]
    [InlineData(3, 32, 4294967295ul, "<8>", "-1")]
    public async Task Canonical_signed_narrowing_and_None_zero_extension_preserve_explicit_physical_semantics(
        uint kind,int width,ulong raw,string schema,string expected)
    {
        var (bytes,definition)=NumericFixture(kind,width,raw,schema);var sink=new Sink();
        await Read(new AsyncInput(bytes),definition,sink);
        Assert.Equal(expected,Assert.Single(sink.Rows).Values[1]);
        Assert.Null(sink.Header!.Columns[1].Narrowing); // A canonical sign extension fits, so nothing was narrowed.
    }

    [Theory]
    [InlineData(0, 32, 256ul, "<u8>")]
    [InlineData(0, 32, 511ul, "<8>")]
    [InlineData(0, 16, 65535ul, "<u8>")]
    [InlineData(1, 12, 511ul, "<8>")]
    [InlineData(5, 12, 3967ul, "<u8>")]
    [InlineData(5, 12, 3967ul, "<8>")]
    [InlineData(5, 12, 256ul, "<8>")]
    public async Task Noncanonical_high_bits_never_disappear_into_plausible_narrowed_cells(
        uint kind,int width,ulong raw,string schema)
    {
        var (bytes,definition)=NumericFixture(kind,width,raw,schema);var sink=new Sink();
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),definition,sink))).Code);
        Assert.Empty(sink.Rows);
    }

    // D079: pallet and common values are 32 bits whatever the field, so their high bits are dropped visibly instead.
    [Theory]
    [InlineData(2, 32, 65535ul, "<8>", "-1")]
    [InlineData(3, 32, 256ul, "<u8>", "0")]
    public async Task Noncanonical_pallet_and_common_high_bits_are_dropped_with_a_column_marker(
        uint kind,int width,ulong raw,string schema,string expected)
    {
        var (bytes,definition)=NumericFixture(kind,width,raw,schema);var sink=new Sink();
        await Read(new AsyncInput(bytes),definition,sink);
        Assert.Equal(expected,Assert.Single(sink.Rows).Values[1]);Assert.Equal(new Db2ValueNarrowing(32,8),sink.Header!.Columns[1].Narrowing);
    }

    [Fact]
    public async Task Global_relative_string_coordinates_span_multiple_sections_without_merging_record_positions()
    {
        var definition=Definition("int ID\nstring Name","$noninline,id$ID<u32>\nName");
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(9),[1],"\0one\0"u8.ToArray(),[]),new(U32(10),[2],"\0two\0"u8.ToArray(),[])]);
        var sink=new Sink();await Read(new AsyncInput(bytes),definition,sink);Assert.Equal(["one","two"],sink.Rows.Select(row=>row.Values[1]));
    }

    [Fact]
    public async Task Zero_record_ID_is_retained_but_zero_filled_duplicate_IDs_are_not_repaired()
    {
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
        var columns=new[]{new Column(0,0,0,32,0,0,0,0,0,[])};var sink=new Sink();
        await Read(new AsyncInput(Build(columns,4,[new(U32(7),[0],[],[])])),definition,sink);Assert.Equal(0u,Assert.Single(sink.Rows).Id);
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(Build(columns,4,[new(U32(7,8),[0,0],[],[])])),definition,new Sink()))).Code);
    }

    [Fact]
    public async Task Inline_IDs_and_uncompressed_arrays_preserve_scalar_widths()
    {
        var definition=Definition("int ID\nint Values","$id$ID<u32>\nValues<u8>[2]");
        var records=new byte[6];BinaryPrimitives.WriteUInt32LittleEndian(records,42);records[4]=128;records[5]=255;
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[]),new(24,4,32,16,0,0,0,0,0,[])],6,[new(records,[],[],[])],flags:0);var sink=new Sink();
        await Read(new AsyncInput(bytes),definition,sink);Assert.Equal(["42","128","255"],Assert.Single(sink.Rows).Values);
    }

    [Fact]
    public async Task Explicit_keyed_scope_reports_every_excluded_record_and_copy_instead_of_empty_success()
    {
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");
        var bytes=Build([new(0,0,0,32,0,0,0,0,0,[])],4,[new(U32(99),[7],[],[(8,7),(9,21)]),new(new byte[8],[0,0],[],[],123,[21,22])]);
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),definition,new Sink()))).Code);
        Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),definition,new Sink(),new(MaxIndexBytes:500,AllowPartialEncryptedSections:true)))).Code);
        var sink=new Sink();var completion=await Read(new AsyncInput(bytes),definition,sink,new(AllowPartialEncryptedSections:true));
        Assert.Equal(new Wdc5Completion(3,1,1,1,2,1,"unencryptedSections",bytes.Length),completion);Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));
    }

    [Theory]
    [InlineData("readable")][InlineData("copy")][InlineData("sections")][InlineData("sameList")]
    public async Task Encrypted_section_IDs_never_repeat_a_readable_row_copy_target_or_another_protected_ID(string kind)
    {
        // Regression for #49: Follow and reference text treat an ID match in a partial table as unique.
        var definition=Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue<u32>");var columns=new[]{new Column(0,0,0,32,0,0,0,0,0,[])};
        byte[] Fixture(uint[] first,uint[] second)=>Build(columns,4,[new(U32(99),[7],[],[(8,7)]),new(new byte[8],[0,0],[],[],123,first),new(new byte[4],[0],[],[],456,second)]);
        var clean=Fixture([21,22],[23]);var sink=new Sink();
        Assert.Equal(new Wdc5Completion(4,1,1,2,3,0,"unencryptedSections",clean.Length),await Read(new AsyncInput(clean),definition,sink,new(AllowPartialEncryptedSections:true)));
        Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));
        var bytes=kind switch{"readable"=>Fixture([21,22],[7]),"copy"=>Fixture([21,22],[8]),"sections"=>Fixture([21,22],[22]),_=>Fixture([21,21],[23])};sink=new Sink();
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),definition,sink,new(AllowPartialEncryptedSections:true)))).Code);
        Assert.Null(sink.Header);Assert.Empty(sink.Rows);
        Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),definition,new Sink()))).Code);
    }

    [Fact]
    public async Task Every_missing_source_byte_and_trailing_byte_fails_complete_geometry()
    {
        var fixture=ScalarFixture();
        for(var length=0;length<fixture.Bytes.Length;length++)
            await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(fixture.Bytes[..length]),fixture.Definition,new Sink()));
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput([..fixture.Bytes,0]),fixture.Definition,new Sink()))).Code);
    }

    [Theory]
    [InlineData("rows")][InlineData("fields")][InlineData("record")][InlineData("index")][InlineData("columns")][InlineData("source")][InlineData("string")]
    public async Task Explicit_source_work_allocation_and_string_limits_fail_honestly(string kind)
    {
        var fixture=ScalarFixture();var limits=kind switch{"rows"=>new Wdc5Limits(MaxRows:2),"fields"=>new(MaxFields:2),"record"=>new(MaxRecordBytes:16),"index"=>new(MaxIndexBytes:32),"columns"=>new(MaxColumns:2),"source"=>new(MaxDecodedBytes:204),_=>new(MaxStringBytes:3)};
        Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(fixture.Bytes),fixture.Definition,new Sink(),limits))).Code);
    }

    [Fact]
    public async Task Unsupported_storage_sparse_and_actual_schema_mismatch_never_fabricate_values()
    {
        var fixture=ScalarFixture();var bytes=(byte[])fixture.Bytes.Clone();bytes[172]=1;
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
        bytes=(byte[])fixture.Bytes.Clone();BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(204+40+4*4+8),6);
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
        bytes=(byte[])fixture.Bytes.Clone();Put(bytes,176,5);
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
        bytes=(byte[])fixture.Bytes.Clone();BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(204+40+4*4),1);
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
        bytes=(byte[])fixture.Bytes.Clone();bytes[156]^=1;
        Assert.Equal("DB2_SCHEMA_MISSING",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
    }

    [Fact]
    public async Task An_unlisted_build_reads_by_layout_but_still_checks_every_field_against_the_file()
    {
        // D080: no block lists 1.2.9.9, so the file's layout picks the block on its version line.
        var fixture=ScalarFixture();
        const string columns="COLUMNS\nint ID\nint Small\nfloat Weight\nstring Name\nint Wide\n\n";
        var layoutOnly=DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(columns+"LAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nSmall<8>\nWeight\nName\nWide<u64>\n"));
        var sink=new Sink();
        await Wdc5TableReader.ReadAsync(new AsyncInput(fixture.Bytes),layoutOnly,"1.2.9.9",new(),sink);
        Assert.Equal("01020304",sink.Header!.LayoutHash);Assert.Equal(["1","-1","1.25","café","18446744073709551615"],sink.Rows[0].Values);
        // Same layout hash but a different field set: the field checks fail as they do for a listed build.
        var narrower=DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(columns+"LAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nSmall<8>\nWeight\nName\n"));
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Assert.ThrowsAsync<DiscoveryException>(()=>Wdc5TableReader.ReadAsync(new AsyncInput(fixture.Bytes),narrower,"1.2.9.9",new(),new Sink()))).Code);
        // An unknown layout still fails clearly, and a block on another version line is never used.
        var bytes=(byte[])fixture.Bytes.Clone();bytes[156]^=1;
        var unknown=await Assert.ThrowsAsync<DiscoveryException>(()=>Wdc5TableReader.ReadAsync(new AsyncInput(bytes),layoutOnly,"1.2.9.9",new(),new Sink()));
        Assert.Equal("DB2_SCHEMA_MISSING",unknown.Code);Assert.Contains("not listed",unknown.Message);
        Assert.Equal("DB2_SCHEMA_MISSING",(await Assert.ThrowsAsync<DiscoveryException>(()=>Wdc5TableReader.ReadAsync(new AsyncInput(fixture.Bytes),layoutOnly,"2.0.0.9",new(),new Sink()))).Code);
    }

    [Fact]
    public async Task Invalid_UTF8_relative_pointer_and_unterminated_strings_fail_without_repairs()
    {
        var fixture=ScalarFixture();var bytes=(byte[])fixture.Bytes.Clone();var stringsAt=204+40+4*28+2*17;bytes[stringsAt+1]=255;
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
        bytes=(byte[])fixture.Bytes.Clone();bytes[stringsAt+12]=1;
        await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()));
        bytes=(byte[])fixture.Bytes.Clone();BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(204+40+4*28+5),-999);
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,new Sink()))).Code);
    }

    [Fact]
    public async Task Copy_cycles_and_late_IO_failure_leave_previously_emitted_rows_provisional()
    {
        var fixture=ScalarFixture();var bytes=(byte[])fixture.Bytes.Clone();BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length-4),8);var sink=new Sink();
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncInput(bytes),fixture.Definition,sink))).Code);Assert.Equal(2,sink.Rows.Count);
        sink=new Sink();using var input=new AsyncInput(fixture.Bytes){FailAtEof=true};await Assert.ThrowsAsync<IOException>(()=>Read(input,fixture.Definition,sink));Assert.Equal(3,sink.Rows.Count);Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Cancellation_and_consumer_backpressure_are_observed_after_each_owned_row()
    {
        var fixture=ScalarFixture();using var cancel=new CancellationTokenSource();var sink=new Sink{AfterRow=()=>{cancel.Cancel();return Task.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Wdc5TableReader.ReadAsync(new AsyncInput(fixture.Bytes),fixture.Definition,"1.2.3.4",new(),sink,cancel.Token));Assert.Single(sink.Rows);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var expected=new InvalidOperationException("authored consumer failure");
        sink=new Sink{AfterRow=async()=>{entered.TrySetResult();await release.Task;throw expected;}};var reading=Read(new AsyncInput(fixture.Bytes),fixture.Definition,sink);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(reading.IsCompleted);Assert.Single(sink.Rows);release.SetResult();Assert.Same(expected,await Assert.ThrowsAsync<InvalidOperationException>(()=>reading));
    }

    private static Task<Wdc5Completion> Read(Stream stream,DbdDocument definition,Sink sink,Wdc5Limits? limits=null)=>Wdc5TableReader.ReadAsync(stream,definition,"1.2.3.4",limits??new(),sink);
    private sealed class Sink:IWdc5Consumer
    {
        public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];public Func<Task>? AfterRow;
        public ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return AfterRow is null?ValueTask.CompletedTask:new(AfterRow());}
    }
    private sealed record Column(short Bits,ushort Position,ushort Offset,ushort Width,int Additional,uint Kind,uint P1,uint P2,uint P3,byte[] Aux);
    private sealed record Part(byte[] Records,uint[] Ids,byte[] Strings,(uint Target,uint Source)[] Copies,ulong Key=0,uint[]? EncryptedIds=null);
    private static DbdDocument Definition(string columns,string fields)=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\n"+columns+"\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fields+"\n"));
    private static (byte[] Bytes,DbdDocument Definition) NumericFixture(uint kind,int width,ulong raw,string schema)
    {
        var record=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(record,kind is 2 or 3?0:raw);
        var column=kind switch
        {
            0=>new Column((short)(32-width),0,0,(ushort)width,0,0,0,0,0,[]),
            2=>new Column(32,0,0,0,0,2,(uint)raw,0,0,[]),
            3=>new Column(32,0,0,1,4,3,0,1,0,U32((uint)raw)),
            _=>new Column(32,0,0,(ushort)width,0,kind,0,(uint)width,0,[])
        };
        return(Build([column],8,[new(record,[1],[],[])]),Definition("int ID\nint Value","$noninline,id$ID<u32>\nValue"+schema));
    }
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
    private static byte[] U32(params uint[] values){var bytes=new byte[values.Length*4];for(var i=0;i<values.Length;i++)Put(bytes,i*4,values[i]);return bytes;}
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at,4),value);
    private sealed class AsyncInput:MemoryStream
    {
        private readonly byte[] bytes;public bool FailAtEof;
        public AsyncInput(byte[] bytes):base(bytes){this.bytes=bytes;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Synchronous I/O forbidden");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Synchronous I/O forbidden");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken cancellationToken=default)
        {
            cancellationToken.ThrowIfCancellationRequested();if(Position==Length&&FailAtEof)throw new IOException("authored EOF failure");
            var count=Math.Min(7,Math.Min(target.Length,(int)(Length-Position)));bytes.AsMemory((int)Position,count).CopyTo(target);Position+=count;return ValueTask.FromResult(count);
        }
    }
}
