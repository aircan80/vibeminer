using System.Buffers.Binary;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5VerifiedRawIntegerTests
{
    [Fact]
    public async Task Verified_palette_width_mismatch_keeps_low_bits_by_default_and_explicitly_whole_column_evidence_when_requested()
    {
        var f=Fixture(3,32,[300,400],"<8>");var narrowed=new Sink();await Read(f,narrowed);
        Assert.Equal(new[]{"44","-112"},narrowed.Rows.Select(r=>r.Values[1]));Assert.Equal(new Db2ValueNarrowing(32,8),narrowed.Header!.Columns[1].Narrowing);
        Assert.Null(narrowed.Header.Columns[1].Interpretation);
        var evidence=new Sink();await Read(f,evidence,Evidence(f));Assert.Null(evidence.Header!.Columns[1].Narrowing);
        Assert.Equal(new[]{"300","400"},evidence.Rows.Select(r=>r.Values[1]));
        var interpretation=evidence.Header!.Columns[1].Interpretation!;
        Assert.Equal("raw-physical-integer-v1",interpretation.Policy);Assert.Equal(32,interpretation.PhysicalBits);Assert.False(interpretation.PhysicalSigned);
        Assert.Equal(8,interpretation.CandidateBits);Assert.False(interpretation.CandidateUnsigned);Assert.Contains("Explicit raw evidence",interpretation.Warning);Assert.DoesNotContain("Unverified provider",interpretation.Warning);
        Assert.True(evidence.AnnotatedBeforeRows);Assert.True(f.Definition.Columns.Single(c=>c.Name=="Value").VerifiedName);
        Assert.NotNull(f.Definition.Columns.Single(c=>c.Name=="Value").Reference); // Original candidate provenance stays intact; interpretation fences adapter references.
    }

    [Fact]
    public async Task All_fit_values_still_get_one_metadata_selected_evidence_policy_before_any_rows()
    {
        var f=Fixture(3,32,[1,2],"<u8>");var strict=new Sink();await Read(f,strict);Assert.Null(strict.Header!.Columns[1].Interpretation);
        var raw=new Sink();await Read(f,raw,Evidence(f));Assert.True(raw.AnnotatedBeforeRows);Assert.All(raw.Rows,r=>Assert.NotNull(raw.Header!.Columns[1].Interpretation));
        Assert.Equal(strict.Rows.Select(r=>r.Values[1]),raw.Rows.Select(r=>r.Values[1]));
    }

    [Theory]
    [InlineData(0,16,300ul,"300",false)][InlineData(1,12,4095ul,"4095",false)]
    [InlineData(5,12,4095ul,"-1",true)][InlineData(2,32,300ul,"300",false)]
    public async Task Raw_evidence_retains_exact_value_width_and_only_explicit_signedImmediate_sign(uint kind,int width,ulong value,string expected,bool signed)
    {
        var f=Fixture(kind,width,[value,1],"<u8>");var sink=new Sink();await Read(f,sink,Evidence(f));
        Assert.Equal(expected,sink.Rows[0].Values[1]);Assert.Equal(signed,sink.Header!.Columns[1].Interpretation!.PhysicalSigned);Assert.Equal(width,sink.Header.Columns[1].Interpretation!.PhysicalBits);
    }

    [Fact]
    public async Task Pallet_array_members_share_the_same_policy_including_members_that_fit_the_hint()
    {
        var f=Fixture(4,32,[300,1,400,2],"<8>",array:2);var sink=new Sink();await Read(f,sink,Evidence(f));
        Assert.Equal(new[]{"ID","Value[0]","Value[1]"},sink.Header!.Columns.Select(c=>c.Name));
        Assert.Equal(new[]{"1","300","1"},sink.Rows[0].Values);Assert.Equal(new[]{"2","400","2"},sink.Rows[1].Values);
        Assert.Equal(sink.Header.Columns[1].Interpretation,sink.Header.Columns[2].Interpretation);Assert.NotNull(sink.Header.Columns[2].Interpretation);
    }

    [Fact]
    public async Task Same_width_and_float_fields_do_not_receive_integer_evidence_and_nonfinite_float_remains_refused()
    {
        var f=Fixture(0,8,[200],"<u8>");var sink=new Sink();await Read(f,sink,Evidence(f) with{VerifiedRawIntegerRules=[Evidence(f).VerifiedRawIntegerRules![0] with{PhysicalBits=32}]} );Assert.Null(sink.Header!.Columns[1].Interpretation);Assert.Equal("200",sink.Rows[0].Values[1]);
        var floating=Fixture(3,32,[0x7fc00000],"",family:"float");sink=new();
        Assert.Equal("DB2_UNSUPPORTED",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(floating,sink,Evidence(floating) with{VerifiedRawIntegerRules=[Evidence(floating).VerifiedRawIntegerRules![0] with{CandidateBits=8}]} ))).Code);Assert.Null(sink.Header!.Columns[1].Interpretation);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Verified_relation_and_inline_ID_never_opt_into_raw_evidence()
    {
        var relation=Fixture(3,32,[300],"<8>",relation:true);var sink=new Sink();
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(relation,sink,Evidence(relation)))).Code);Assert.Null(sink.Header!.Columns[1].Interpretation);
        var id=Fixture(1,64,[0x100000000],"<u32>",idOnly:true);
        Assert.Equal("DB2_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(id,new Sink(),Evidence(id)))).Code);
    }

    [Fact]
    public async Task Legacy_unverified_policy_is_unchanged_and_limits_cancellation_remain_applicable()
    {
        var f=Fixture(3,32,[300],"<8>",unverified:true);var sink=new Sink();await Read(f,sink);Assert.Equal("300",sink.Rows[0].Values[1]);Assert.Contains("Unverified provider",sink.Header!.Columns[1].Interpretation!.Warning);
        f=Fixture(3,32,[300],"<8>");Assert.Equal("DB2_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(f,new Sink(),Evidence(f) with{MaxIndexBytes=1}))).Code);
        using var input=new MemoryStream(f.Bytes);using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Wdc5TableReader.ReadAsync(input,f.Definition,"1.2.3.4",Evidence(f),new Sink(),cts.Token));Assert.True(input.CanRead);Assert.Equal(0,input.Position);
    }

    [Fact]
    public async Task A_valid_narrowing_on_an_unselected_field_keeps_its_normal_interpretation()
    {
        var f=Fixture(3,32,[1,2],"<u8>");var limits=Evidence(f);var rule=limits.VerifiedRawIntegerRules![0];var sink=new Sink();
        await Read(f,sink,limits with{VerifiedRawIntegerRules=[rule with{FieldName="UnrelatedColumn"}]});
        Assert.Null(sink.Header!.Columns[1].Interpretation);Assert.Equal(new[]{"1","2"},sink.Rows.Select(r=>r.Values[1]));
    }

    [Theory]
    [InlineData("context")][InlineData("sha")][InlineData("table")][InlineData("layout")][InlineData("ordinal")]
    [InlineData("name")][InlineData("kind")][InlineData("physical")][InlineData("candidate")][InlineData("unsigned")]
    public async Task Any_mismatching_exact_scope_leaves_the_verified_column_to_low_bit_narrowing(string mismatch)
    {
        var f=Fixture(3,32,[300],"<8>");var limits=Evidence(f);var rule=limits.VerifiedRawIntegerRules![0];
        rule=mismatch switch
        {
            "sha"=>rule with{ExpectedDefinitionSha256=new('a',64)},"table"=>rule with{TableHash="99887766"},
            "layout"=>rule with{LayoutHash="99887766"},"ordinal"=>rule with{FieldOrdinal=0},"name"=>rule with{FieldName="Other"},
            "kind"=>rule with{StorageKind=2},"physical"=>rule with{StorageKind=1,PhysicalBits=16},"candidate"=>rule with{CandidateBits=16},
            "unsigned"=>rule with{CandidateUnsigned=true},_=>rule
        };
        limits=limits with{DefinitionSha256=mismatch=="context"?new('b',64):limits.DefinitionSha256,VerifiedRawIntegerRules=[rule]};
        var sink=new Sink();await Read(f,sink,limits);
        Assert.Null(sink.Header!.Columns[1].Interpretation);Assert.Equal(new Db2ValueNarrowing(32,8),sink.Header.Columns[1].Narrowing);Assert.Equal("44",Assert.Single(sink.Rows).Values[1]);
    }

    [Fact]
    public async Task Rules_are_bounded_validated_and_snapshotted_before_the_first_source_read()
    {
        var f=Fixture(3,32,[300],"<8>");var baseline=Evidence(f);var rule=baseline.VerifiedRawIntegerRules![0];
        foreach(var invalid in new[]{baseline with{DefinitionSha256="bad"},baseline with{VerifiedRawIntegerRules=Enumerable.Repeat(rule,9).ToArray()},
            baseline with{VerifiedRawIntegerRules=[rule,rule]},baseline with{VerifiedRawIntegerRules=[rule,rule with{StorageKind=2}]},
            baseline with{VerifiedRawIntegerRules=[rule with{ExpectedDefinitionSha256=new('A',64)}]},baseline with{VerifiedRawIntegerRules=[rule with{TableHash="1122334a"}]},
            baseline with{VerifiedRawIntegerRules=[rule with{FieldName="bad-name"}]},baseline with{VerifiedRawIntegerRules=[rule with{FieldOrdinal=-1}]},
            baseline with{VerifiedRawIntegerRules=[rule with{StorageKind=6}]},baseline with{VerifiedRawIntegerRules=[rule with{PhysicalBits=8}]},baseline with{VerifiedRawIntegerRules=[rule with{PhysicalBits=16}]}})
        {
            using var input=new MutationSource(f.Bytes,()=>throw new InvalidOperationException("Invalid rules read source bytes."));
            await Assert.ThrowsAsync<ArgumentException>(()=>Wdc5TableReader.ReadAsync(input,f.Definition,"1.2.3.4",invalid,new Sink()));Assert.Equal(0,input.Position);
        }
        var changingCount=new ChangingCountRules(rule);var counted=new Sink();
        await Read(f,counted,baseline with{VerifiedRawIntegerRules=changingCount});Assert.Equal(1,changingCount.CountReads);Assert.NotNull(counted.Header!.Columns[1].Interpretation);
        using(var vanished=new MutationSource(f.Bytes,()=>throw new InvalidOperationException("A missing rule read source bytes.")))
            await Assert.ThrowsAsync<ArgumentException>(()=>Wdc5TableReader.ReadAsync(vanished,f.Definition,"1.2.3.4",baseline with{VerifiedRawIntegerRules=new MissingRuleList()},new Sink()));
        var mutable=new List<Wdc5RawIntegerRule>{rule};using var stream=new MutationSource(f.Bytes,()=>mutable[0]=rule with{FieldName="Changed"});var sink=new Sink();
        await Wdc5TableReader.ReadAsync(stream,f.Definition,"1.2.3.4",baseline with{VerifiedRawIntegerRules=mutable},sink);
        Assert.NotNull(sink.Header!.Columns[1].Interpretation);Assert.Equal("300",sink.Rows.Single().Values[1]);Assert.True(stream.CanRead);
        var noContext=new Sink();await Read(f,noContext,baseline with{DefinitionSha256=null});
        Assert.Null(noContext.Header!.Columns[1].Interpretation);Assert.NotNull(noContext.Header.Columns[1].Narrowing);
    }
    private sealed class ChangingCountRules(Wdc5RawIntegerRule rule):IReadOnlyList<Wdc5RawIntegerRule>
    {
        public int CountReads;public int Count=>++CountReads==1?1:int.MaxValue;public Wdc5RawIntegerRule this[int index]=>index==0?rule:throw new IndexOutOfRangeException();
        public IEnumerator<Wdc5RawIntegerRule> GetEnumerator()=>throw new NotSupportedException();System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class MissingRuleList:IReadOnlyList<Wdc5RawIntegerRule>
    {
        public int Count=>1;public Wdc5RawIntegerRule this[int index]=>throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<Wdc5RawIntegerRule> GetEnumerator()=>throw new NotSupportedException();System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private sealed class MutationSource(byte[] bytes,Action mutate):MemoryStream(bytes)
    {
        private bool first=true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {if(first){first=false;mutate();}return base.ReadAsync(buffer,token);}
    }

    internal sealed record TestFixture(byte[] Bytes,DbdDocument Definition,string DefinitionSha256);
    internal static Wdc5Limits Evidence(TestFixture f)
    {
        var field=f.Definition.Blocks.Single().Fields.Last();var at=248;var kind=BinaryPrimitives.ReadUInt32LittleEndian(f.Bytes.AsSpan(at+8));
        var physical=kind==0?32-BinaryPrimitives.ReadInt16LittleEndian(f.Bytes.AsSpan(244)):kind is 1 or 5?BinaryPrimitives.ReadUInt16LittleEndian(f.Bytes.AsSpan(at+2)):32;
        return new(DefinitionSha256:f.DefinitionSha256,VerifiedRawIntegerRules:[new(f.DefinitionSha256,"11223344","01020304",f.Definition.Blocks.Single().Fields.Count-1,field.Column.Name,kind,physical,field.IntegerBits??32,field.Unsigned==true)]);
    }
    // Authored independently: a compact scalar/palette array/Common fixture, never copied game payloads.
    internal static TestFixture Fixture(uint kind,int width,ulong[] values,string schema,int array=1,string family="int",bool relation=false,bool idOnly=false,bool unverified=false)
    {
        var rows=kind==4?values.Length/array:values.Length;var recordSize=kind is 0 or 1 or 5?(width+7)/8:1;
        var aux=kind is 3 or 4?values.SelectMany(v=>Word((uint)v)).ToArray():kind==2?Word(2).Concat(Word((uint)values[^1])).ToArray():[];
        var recordAt=204+40+28+aux.Length;var length=recordAt+rows*recordSize+(idOnly?0:rows*4);var b=new byte[length];"WDC5"u8.CopyTo(b);
        P(b,4,5);P(b,136,(uint)rows);P(b,140,1);P(b,144,(uint)recordSize);P(b,152,0x11223344);P(b,156,0x01020304);H(b,172,idOnly?(ushort)0:(ushort)4);P(b,176,1);P(b,188,24);P(b,kind==2?192:196,(uint)aux.Length);P(b,200,1);
        P(b,212,(uint)recordAt);P(b,216,(uint)rows);P(b,228,idOnly?0:(uint)rows*4);
        H(b,244,kind==0?(ushort)(32-width):(ushort)32);H(b,248,0);H(b,250,(ushort)(kind==0?width:kind is 1 or 5?width:kind is 3 or 4?1:0));P(b,252,(uint)aux.Length);P(b,256,kind);P(b,260,kind==2?(uint)values[0]:0);P(b,264,kind is 1 or 5?(uint)width:kind is 3 or 4?1u:0);P(b,268,kind==4?(uint)array:0);
        aux.CopyTo(b,272);for(var row=0;row<rows;row++)
        {
            var raw=kind is 3 or 4?(ulong)row:kind==2?0:values[row];for(var n=0;n<recordSize;n++)b[recordAt+row*recordSize+n]=(byte)(raw>>(n*8));
            if(!idOnly)P(b,recordAt+rows*recordSize+row*4,(uint)row+1);
        }
        var name=idOnly?"ID":"Value";var fields=idOnly?"$id$ID"+schema:"$noninline,id$ID<u32>\n"+(relation?"$relation$":"")+name+schema+(array>1?"["+array+"]":"");
        var definitionBytes=Encoding.UTF8.GetBytes("COLUMNS\nint ID\n"+(idOnly?"":family+(family=="int"?"<Other::ID>":"")+" Value"+(unverified?"?":"")+"\n")+"\nLAYOUT 01020304\nBUILD 1.2.3.4\n"+fields+"\n");
        return new(b,DbdDefinitionReader.Parse(definitionBytes),Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(definitionBytes)));
    }
    private static byte[] Word(uint value){var b=new byte[4];P(b,0,value);return b;}
    private static void P(byte[] b,int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),value);
    private static void H(byte[] b,int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),value);
    internal static async Task Read(TestFixture f,Sink sink,Wdc5Limits? limits=null)
    {using var input=new MemoryStream(f.Bytes);await Wdc5TableReader.ReadAsync(input,f.Definition,"1.2.3.4",limits??new(),sink);}
    internal sealed class Sink:IWdc5Consumer
    {public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];public bool AnnotatedBeforeRows,NarrowedBeforeRows;public ValueTask HeaderAsync(Wdc5TableHeader h,CancellationToken t){Header=h;AnnotatedBeforeRows=h.Columns.Any(c=>c.Interpretation is not null);NarrowedBeforeRows=Rows.Count==0&&h.Columns.Any(c=>c.Narrowing is not null);return ValueTask.CompletedTask;}public ValueTask RowAsync(Db2Row r,CancellationToken t){Rows.Add(r);return ValueTask.CompletedTask;}}
}
