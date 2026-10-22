using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5SparseTests
{
    [Fact]
    public async Task Variable_strings_then_all_trailing_scalars_arrays_and_raw_fields_use_the_actual_cursor()
    {
        var fixture=Build([new([Record("é"),Record("a much longer name",quality:4)], [7,21],[(8,7),(9,8)])]);var sink=new Sink();
        using var input=new AsyncSource(fixture.Bytes);var completion=await Read(input,sink);
        Assert.Equal(new Wdc5Completion(2,2,2,0,0,0,"complete",fixture.Bytes.Length),completion);
        Assert.Equal(24,sink.Header!.RecordSize);Assert.Equal(["ID","Name","Quality","Ratio","Budget[0]","Budget[1]","Unknown"],sink.Header.Columns.Select(column=>column.Name));
        Assert.Equal(["7","é","2","1.5","-1","200","305419896"],sink.Rows[0].Values);
        Assert.Equal(["21","a much longer name","4","1.5","-1","200","305419896"],sink.Rows[1].Values);
        Assert.Equal(["9","é","2","1.5","-1","200","305419896"],sink.Rows[3].Values);Assert.Equal(8u,sink.Rows[3].CopySourceId);
        Assert.Equal("raw-physical-integer-v1",sink.Header.Columns[6].Interpretation!.Policy);Assert.True(input.CanRead);
    }

    [Fact]
    public async Task Clear_sections_cross_section_copy_chains_and_unsigned_zero_identity_remain_exact()
    {
        var fixture=Build([new([Record("")],[0],[(9,8)]),new([Record("other")],[30],[(8,30)])]);var sink=new Sink();
        await Read(new AsyncSource(fixture.Bytes),sink);Assert.Equal([0u,30u,9u,8u],sink.Rows.Select(row=>row.Id));Assert.Equal("other",sink.Rows[2].Values[1]);
    }

    [Theory]
    [InlineData("offset")][InlineData("zeroSize")][InlineData("longSize")][InlineData("mapID")][InlineData("repeatID")][InlineData("uncovered")]
    public async Task Invalid_absolute_maps_identity_mismatches_and_uncovered_record_bytes_fail_before_callbacks(string kind)
    {
        var fixture=Build([new([Record("one"),Record("two")],[7,21],[])]);var bytes=fixture.Bytes;
        switch(kind)
        {
            case "offset":Put(bytes,fixture.Maps[0],1);break;
            case "zeroSize":Half(bytes,fixture.Maps[0]+4,0);break;
            case "longSize":Half(bytes,fixture.Maps[0]+4,ushort.MaxValue);break;
            case "mapID":Put(bytes,fixture.MapIds[0],999);break;
            case "repeatID":Put(bytes,fixture.RecordEnds[0]+4,7);Put(bytes,fixture.MapIds[0]+4,7);break;
            default:Half(bytes,fixture.Maps[0]+6+4,(ushort)(Record("two").Length-1));break;
        }
        var sink=new Sink();Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(bytes),sink))).Code);Assert.Null(sink.Header);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Every_truncated_source_and_any_undeclared_tail_fails_outer_geometry()
    {
        var fixture=Build([new([Record("abc")],[7],[])]);
        for(var length=0;length<fixture.Bytes.Length;length++)
            await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes[..length]),new Sink()));
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource([..fixture.Bytes,0]),new Sink()))).Code);
    }

    [Theory]
    [InlineData("trailing")][InlineData("numeric")][InlineData("utf8")][InlineData("unterminated")][InlineData("float")]
    public async Task Malformed_trailing_fields_or_text_never_become_a_successful_names_only_projection(string kind)
    {
        byte[] record=kind switch
        {
            "trailing"=>[..Record("x"),0,0],"numeric"=>Record("x")[..^1],
            "utf8"=>[255,0,..Record("")[1..]],"unterminated"=>Enumerable.Repeat((byte)65,20).ToArray(),
            _=>Record("x",ratio:float.NaN)
        };
        var fixture=Build([new([record],[7],[])]);var sink=new Sink();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink));
        Assert.Equal(kind is "trailing" or "float"?"DB2_UNSUPPORTED":"DB2_INVALID",error.Code);Assert.Empty(sink.Rows);
    }

    [Theory]
    [InlineData("")][InlineData("a")][InlineData("ab")][InlineData("abc")][InlineData("é")][InlineData("€")]
    public async Task Only_minimal_zero_suffix_to_four_byte_record_alignment_is_supported(string name)
    {
        var record=Record(name);var padding=(-record.Length)&3;var padded=record.Concat(new byte[padding]).ToArray();
        var fixture=Build([new([padded],[7],[(8,7)])]);var sink=new Sink();var done=await Read(new AsyncSource(fixture.Bytes),sink);
        Assert.Equal(1,done.PhysicalRows);Assert.Equal(1,done.CopyRows);Assert.Equal(name,sink.Rows[0].Values[1]);Assert.Equal(name,sink.Rows[1].Values[1]);
        if(padding>0)
        {
            padded[^1]=1;fixture=Build([new([padded],[7],[])]);
            Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),new Sink()))).Code);
        }
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(7)]
    public async Task Extra_zero_suffix_is_not_accepted_merely_because_some_padding_is_supported(int extra)
    {
        var fixture=Build([new([[..Record("x"),..new byte[extra]]],[7],[])]);var sink=new Sink();
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink))).Code);Assert.Empty(sink.Rows);
    }

    [Theory]
    [InlineData("ff")][InlineData("c0af")][InlineData("eda080")][InlineData("f4908080")][InlineData("61ff62")]
    public async Task Raw_text_is_explicit_opt_in_and_preserves_exact_pre_NUL_bytes_without_guessing_an_encoding(string hex)
    {
        var record=Convert.FromHexString(hex).Concat(new byte[]{0}).Concat(Record("")[1..]).ToArray();var fixture=Build([new([record],[7],[])]);
        var strict=new Sink();Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),strict))).Code);Assert.Empty(strict.Rows);
        var sink=new Sink();var done=await Read(new AsyncSource(fixture.Bytes),sink,new(AllowRawTextEvidence:true));var row=Assert.Single(sink.Rows);
        Assert.Equal(["7","","2","1.5","-1","200","305419896"],row.Values);
        Assert.Equal(new Db2TextIssue(1,"DB2_TEXT_ENCODING_INVALID",hex),Assert.Single(row.TextIssues!));Assert.Equal(1,done.TextIssueCount);Assert.Equal(1,done.PhysicalRows);
    }

    [Fact]
    public async Task Valid_empty_and_UTF8_text_remain_normal_values_and_evidence_is_charged_for_physical_and_copy_cells()
    {
        var bad=new byte[]{255,0}.Concat(Record("")[1..]).ToArray();var fixture=Build([new([Record(""),Record("é"),bad],[0,1,7],[(8,7),(9,8)])]);var sink=new Sink();
        var done=await Read(new AsyncSource(fixture.Bytes),sink,new(AllowRawTextEvidence:true,MaxRawTextEvidenceBytes:3));
        Assert.Equal("",sink.Rows[0].Values[1]);Assert.Null(sink.Rows[0].TextIssues);Assert.Equal("é",sink.Rows[1].Values[1]);Assert.Null(sink.Rows[1].TextIssues);
        Assert.Equal(3,done.TextIssueCount);Assert.Equal(3,done.PhysicalRows);Assert.Equal(2,done.CopyRows);
        foreach(var row in sink.Rows.Skip(2))Assert.Equal(new Db2TextIssue(1,"DB2_TEXT_ENCODING_INVALID","ff"),Assert.Single(row.TextIssues!));
        sink=new();Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink,new(AllowRawTextEvidence:true,MaxRawTextEvidenceBytes:2)))).Code);
        Assert.Equal(4,sink.Rows.Count); // Last copy would amplify evidence beyond the admitted aggregate bound.
        Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),new Sink(),new(AllowRawTextEvidence:true,MaxRawTextEvidenceBytes:0)))).Code);
    }

    [Theory]
    [InlineData("numeric")][InlineData("padding")][InlineData("unterminated")][InlineData("float")]
    public async Task Raw_text_evidence_never_hides_a_later_record_structure_or_numeric_failure(string failure)
    {
        var record=failure=="unterminated"?Enumerable.Repeat((byte)255,20).ToArray():new byte[]{255,0}.Concat(Record("",ratio:failure=="float"?float.NaN:1.5f)[1..]).ToArray();
        if(failure=="numeric")record=record[..^1];if(failure=="padding")record=[..record,1];
        var fixture=Build([new([record],[7],[])]);var sink=new Sink();
        Assert.Equal(failure is "float" or "padding"?"DB2_UNSUPPORTED":"DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink,new(AllowRawTextEvidence:true)))).Code);Assert.Empty(sink.Rows);
    }

    [Theory]
    [InlineData("flags")][InlineData("relation")][InlineData("maps")][InlineData("strings")][InlineData("packed")][InlineData("order")][InlineData("bits")]
    public async Task Unsupported_sparse_grammar_is_refused_without_guessing(string kind)
    {
        var fixture=Build([new([Record("x")],[7],[])]);var bytes=fixture.Bytes;
        switch(kind)
        {
            case "flags":Half(bytes,172,3);break; // SecondaryKey without external IDs remains outside the finite profile.
            case "relation":Put(bytes,204+28,4);break;
            case "maps":Put(bytes,204+32,0);break;
            case "strings":Put(bytes,148,1);Put(bytes,204+16,1);break;
            case "packed":Put(bytes,244+5*4+8,1);break;
            case "order":Half(bytes,244+4+2,3);Half(bytes,244+5*4+24,24);break;
            default:Half(bytes,244+4,25);Half(bytes,244+5*4+24+2,7);break;
        }
        var sink=new Sink();await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(bytes),sink));Assert.Null(sink.Header);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Protected_section_and_copies_depending_on_protected_IDs_report_exact_excluded_scope()
    {
        var fixture=Build([new([Record("clear")],[7],[(8,7),(9,21)]),new([Record("not plaintext")],[21],[(22,21)],123,[21,22])]);
        using var input=Projection(fixture,1);var sink=new Sink();var done=await Read(input,sink,new(AllowPartialEncryptedSections:true));
        Assert.Equal(new Wdc5Completion(2,1,1,1,1,2,"unencryptedSections",fixture.Bytes.Length),done);Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));
        using var strict=Projection(fixture,1);Assert.Equal("DB2_ENCRYPTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(strict,new Sink()))).Code);
    }

    [Theory]
    [InlineData("readable")][InlineData("copy")][InlineData("sections")]
    public async Task Sparse_encrypted_IDs_never_repeat_a_readable_identity_copy_target_or_another_protected_ID(string kind)
    {
        // Regression for #49, on the profile Retail ItemSparse uses.
        Fixture Two(uint[] second)=>Build([new([Record("clear")],[7],[(8,7)]),new([Record("one")],[21],[],123,[21]),new([Record("two")],[30],[],456,second)]);
        var clean=Two([30]);var sink=new Sink();
        Assert.Equal(new Wdc5Completion(3,1,1,2,2,0,"unencryptedSections",clean.Bytes.Length),await Read(new AsyncSource(clean.Bytes),sink,new(AllowPartialEncryptedSections:true)));
        Assert.Equal([7u,8u],sink.Rows.Select(row=>row.Id));
        var fixture=Two(kind switch{"readable"=>[7],"copy"=>[8],_=>[21]});sink=new Sink();
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink,new(AllowPartialEncryptedSections:true)))).Code);
        Assert.Null(sink.Header);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Every_ordinary_body_and_suffix_byte_is_required_before_callbacks_even_for_sparse_sources()
    {
        var fixture=Build([new([Record("clear")],[7],[(8,7)]),new([Record("protected")],[21],[],123,[21])],gap:3);
        for(var at=fixture.PrefixEnd;at<fixture.SectionEnds[0];at++)
        {
            using var input=new BlteProjectionStream(new AsyncSource(fixture.Bytes),[new(at,1,0,"000000000000007B")]);var sink=new Sink();
            Assert.Equal("DB2_PROJECTION_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(input,sink,new(AllowPartialEncryptedSections:true)))).Code);Assert.Null(sink.Header);
        }
    }

    [Theory]
    [InlineData("cycle")][InlineData("missing")][InlineData("physicalTarget")][InlineData("duplicateTarget")][InlineData("depth")]
    public async Task Copy_conflicts_unresolved_sources_cycles_and_depth_bounds_never_publish_success(string kind)
    {
        (uint,uint)[] copies=kind switch{"cycle"=>[(8,9),(9,8)],"missing"=>[(8,999)],"physicalTarget"=>[(7,7)],"duplicateTarget"=>[(8,7),(8,7)],_=>[(8,9),(9,7)]};
        var fixture=Build([new([Record("clear")],[7],copies)]);var sink=new Sink();
        Assert.Equal(kind=="depth"?"DB2_LIMIT":"DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),sink,new(MaxCopyDepth:kind=="depth"?1:64)))).Code);
    }

    [Theory]
    [InlineData("source")][InlineData("rows")][InlineData("index")][InlineData("record")][InlineData("string")][InlineData("columns")]
    public async Task Sparse_source_memory_record_text_and_projection_limits_remain_bounded(string kind)
    {
        var fixture=Build([new([Record(new string('x',30))],[7],[])]);var limits=kind switch
        {"source"=>new Wdc5Limits(MaxDecodedBytes:204),"rows"=>new(MaxRows:0),"index"=>new(MaxIndexBytes:1),"record"=>new(MaxRecordBytes:24),"string"=>new(MaxStringBytes:1),_=>new(MaxColumns:2)};
        Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(new AsyncSource(fixture.Bytes),new Sink(),limits))).Code);
    }

    [Fact]
    public async Task Cancellation_backpressure_and_late_consumer_failure_preserve_caller_ownership_and_provisional_rows()
    {
        var fixture=Build([new([Record("one"),Record("two")],[7,21],[])]);using var input=new AsyncSource(fixture.Bytes);using var cancel=new CancellationTokenSource();
        var sink=new Sink{AfterRow=()=>{cancel.Cancel();return Task.CompletedTask;}};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Read(input,sink,token:cancel.Token));Assert.Single(sink.Rows);Assert.True(input.CanRead);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected=new IOException("authored consumer failure");sink=new Sink{AfterRow=async()=>{entered.SetResult();await release.Task;throw expected;}};
        var reading=Read(input,sink);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(reading.IsCompleted);release.SetResult();
        Assert.Same(expected,await Assert.ThrowsAsync<IOException>(()=>reading));Assert.Single(sink.Rows);Assert.True(input.CanRead);
    }

    private static Task<Wdc5Completion> Read(Stream source,Sink sink,Wdc5Limits? limits=null,CancellationToken token=default)=>Wdc5TableReader.ReadAsync(source,Definition(),"1.2.3.4",limits??new(),sink,token);
    private static DbdDocument Definition()=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID\nstring Name\nint Quality\nfloat Ratio\nint Budget\nint Unknown?\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nName\nQuality<u8>\nRatio\nBudget<16>[2]\nUnknown<u8>\n"));
    private static byte[] Record(string name,byte quality=2,float ratio=1.5f)
    {
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(Encoding.UTF8.GetBytes(name));writer.Write((byte)0);
        writer.Write(quality);writer.Write(ratio);writer.Write((short)-1);writer.Write((short)200);writer.Write(0x12345678u);return output.ToArray();
    }
    private sealed record Part(byte[][] Records,uint[] Ids,(uint Target,uint Source)[] Copies,ulong Key=0,uint[]? EncryptedIds=null);
    private sealed record Fixture(byte[] Bytes,int PrefixEnd,int[] Starts,int[] RecordEnds,int[] Maps,int[] MapIds,int[] SectionEnds);
    private static Fixture Build(Part[] parts,int gap=0)
    {
        var prefix=204+parts.Length*40+5*28+parts.Where(part=>part.Key!=0).Sum(part=>4+4*(part.EncryptedIds?.Length??0));
        var starts=new int[parts.Length];var ends=new int[parts.Length];var maps=new int[parts.Length];var mapIds=new int[parts.Length];var sectionEnds=new int[parts.Length];var next=prefix+gap;
        for(var index=0;index<parts.Length;index++)
        {
            var part=parts[index];starts[index]=next;ends[index]=next+part.Records.Sum(record=>record.Length);
            maps[index]=ends[index]+part.Ids.Length*4+part.Copies.Length*8;mapIds[index]=maps[index]+part.Ids.Length*6;sectionEnds[index]=mapIds[index]+part.Ids.Length*4;next=sectionEnds[index];
        }
        var header=new byte[204];"WDC5"u8.CopyTo(header);Put(header,4,5);Put(header,136,(uint)parts.Sum(part=>part.Ids.Length));Put(header,140,5);Put(header,144,24);
        Put(header,152,0x11223344);Put(header,156,0x01020304);Half(header,172,5);Put(header,176,5);Put(header,180,24);Put(header,188,120);Put(header,200,(uint)parts.Length);
        using var output=new MemoryStream();using var writer=new BinaryWriter(output,Encoding.UTF8,true);writer.Write(header);
        for(var index=0;index<parts.Length;index++){var part=parts[index];writer.Write(part.Key);writer.Write((uint)starts[index]);writer.Write((uint)part.Ids.Length);writer.Write(0u);writer.Write((uint)ends[index]);writer.Write((uint)part.Ids.Length*4);writer.Write(0u);writer.Write((uint)part.Ids.Length);writer.Write((uint)part.Copies.Length);}
        short[] bits=[0,24,0,16,0];ushort[] positions=[0,4,5,9,13];ushort[] widths=[32,8,32,32,32];
        for(var index=0;index<5;index++){writer.Write(bits[index]);writer.Write(positions[index]);}
        for(var index=0;index<5;index++){writer.Write((ushort)(positions[index]*8));writer.Write(widths[index]);for(var item=0;item<5;item++)writer.Write(0u);}
        foreach(var part in parts.Where(part=>part.Key!=0)){writer.Write((uint)(part.EncryptedIds?.Length??0));foreach(var id in part.EncryptedIds??[])writer.Write(id);}
        writer.Write(new byte[gap]);
        for(var index=0;index<parts.Length;index++)
        {
            var part=parts[index];foreach(var record in part.Records)writer.Write(record);foreach(var id in part.Ids)writer.Write(id);
            foreach(var (target,source) in part.Copies){writer.Write(target);writer.Write(source);}
            var at=starts[index];foreach(var record in part.Records){writer.Write((uint)at);writer.Write((ushort)record.Length);at+=record.Length;}foreach(var id in part.Ids)writer.Write(id);
        }
        return new(output.ToArray(),prefix,starts,ends,maps,mapIds,sectionEnds);
    }
    private static BlteProjectionStream Projection(Fixture fixture,int section)=>new(new AsyncSource(fixture.Bytes),[new(fixture.Starts[section],fixture.SectionEnds[section]-fixture.Starts[section],0,"FFFFFFFFFFFFFFFF")]);
    private static void Put(byte[] bytes,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
    private static void Half(byte[] bytes,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),value);
    private sealed class Sink:IWdc5Consumer
    {
        public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];public Func<Task>? AfterRow;
        public ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return AfterRow is null?ValueTask.CompletedTask:new(AfterRow());}
    }
    private sealed class AsyncSource:MemoryStream
    {
        private readonly byte[] bytes;public AsyncSource(byte[] bytes):base(bytes){this.bytes=bytes;}
        public override int Read(byte[] buffer,int offset,int count)=>throw new InvalidOperationException("Async reads required.");
        public override int Read(Span<byte> buffer)=>throw new InvalidOperationException("Async reads required.");
        public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)
        {token.ThrowIfCancellationRequested();var count=(int)Math.Min(target.Length,Length-Position);bytes.AsSpan((int)Position,count).CopyTo(target.Span);Position+=count;return ValueTask.FromResult(count);}
    }
}
