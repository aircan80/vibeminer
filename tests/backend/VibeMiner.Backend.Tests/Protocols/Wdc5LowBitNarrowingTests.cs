using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using static VibeMiner.Backend.Tests.Protocols.Wdc5VerifiedRawIntegerTests;

namespace VibeMiner.Backend.Tests.Protocols;

// D079 (#53): hand-built fixtures whose 32-bit pallet and common values carry junk upper bits, as classic Item,
// CreatureDisplayInfo and ItemEffect files do, e.g. ItemGroupSoundsID 0x780107 for ItemGroupSounds 7.
public sealed class Wdc5LowBitNarrowingTests
{
    [Fact]
    public async Task Unsigned_pallet_values_keep_their_low_bits_and_the_column_says_so_before_any_row()
    {
        var f=Fixture(3,32,[0x780107,0x780100],"<u8>");var sink=new Sink();await Read(f,sink);
        Assert.Equal(["7","0"],sink.Rows.Select(r=>r.Values[1]));
        Assert.Equal(new Db2ValueNarrowing(32,8),sink.Header!.Columns[1].Narrowing);Assert.True(sink.NarrowedBeforeRows);
        Assert.Null(sink.Header.Columns[1].Interpretation);Assert.Null(sink.Header.Columns[0].Narrowing);
    }

    [Theory]
    [InlineData(0x2fful,0x205ul,"-1","5")]
    [InlineData(0x280ul,0xfffffffful,"-128","-1")]
    public async Task Signed_pallet_values_keep_their_low_bits_signed_at_the_definition_width(ulong a,ulong b,string first,string second)
    {
        var sink=new Sink();await Read(Fixture(3,32,[a,b],"<8>"),sink);
        Assert.Equal([first,second],sink.Rows.Select(r=>r.Values[1]));Assert.Equal(new Db2ValueNarrowing(32,8),sink.Header!.Columns[1].Narrowing);
    }

    [Theory]
    [InlineData("<u16>",0x20005ul,0x21234ul,"5","4660")]
    [InlineData("<16>",0xdfffful,0xd0005ul,"-1","5")]
    public async Task Common_default_and_override_values_keep_their_low_bits(string schema,ulong fallback,ulong value,string first,string second)
    {
        // Record 1 reads the common default, record 2 its override.
        var sink=new Sink();await Read(Fixture(2,32,[fallback,value],schema),sink);
        Assert.Equal([first,second],sink.Rows.Select(r=>r.Values[1]));Assert.Equal(new Db2ValueNarrowing(32,16),sink.Header!.Columns[1].Narrowing);
    }

    [Fact]
    public async Task A_misfit_common_default_alone_selects_narrowing_for_the_whole_column()
    {
        var sink=new Sink();await Read(Fixture(2,32,[0x200,5],"<u8>"),sink);
        Assert.Equal(["0","5"],sink.Rows.Select(r=>r.Values[1]));Assert.NotNull(sink.Header!.Columns[1].Narrowing);
    }

    [Fact]
    public async Task Every_pallet_array_member_shares_the_narrowing()
    {
        var sink=new Sink();await Read(Fixture(4,32,[0x40001,0x40002,0x40003,4],"<u8>",array:2),sink);
        Assert.Equal(["1","1","2"],sink.Rows[0].Values);Assert.Equal(["2","3","4"],sink.Rows[1].Values);
        Assert.Equal(sink.Header!.Columns[1].Narrowing,sink.Header.Columns[2].Narrowing);Assert.NotNull(sink.Header.Columns[2].Narrowing);
    }

    // #56: narrowing is only for one constant upper-bit pattern per column; varying upper bits stay a mismatch, before Header.
    [Fact]
    public async Task One_constant_upper_pattern_narrows_even_among_values_that_fit()
    {
        // The fixture's one-bit pallet index reads entries 0 and 1; selection still checks every pallet entry.
        var sink=new Sink();await Read(Fixture(3,32,[0x780107,5,0x7801ff,0],"<u8>"),sink);
        Assert.Equal(["7","5","7","5"],sink.Rows.Select(r=>r.Values[1]));
        Assert.Equal(new Db2ValueNarrowing(32,8),sink.Header!.Columns[1].Narrowing);
    }

    [Theory]
    [InlineData(3,"<u8>",new ulong[]{0x780107,0x790105},"pallet","unsigned8","2 different patterns above bit 8 (0x780100, 0x790100)")]
    [InlineData(3,"<8>",new ulong[]{0x2ff,0x1ff,0xffffffff},"pallet","signed8","2 different patterns above bit 8 (0x100, 0x200)")]
    [InlineData(2,"<u16>",new ulong[]{0x20005,0x30005},"common","unsigned16","2 different patterns above bit 16 (0x20000, 0x30000)")]
    [InlineData(4,"<u8>",new ulong[]{0x40001,0x40002,0x50003,4},"pallet array","unsigned8","2 different patterns above bit 8 (0x40000, 0x50000)")]
    [InlineData(3,"<u16>",new ulong[]{0x10000,0x20000,0x30000,0x40000,0x50000,0x60000},"pallet","unsigned16","at least 4 different patterns above bit 16 (0x10000, 0x20000, 0x30000, 0x40000)")]
    public async Task Varying_upper_bits_are_refused_before_any_row_naming_the_field_and_patterns(uint kind,string schema,ulong[] values,string storage,string width,string patterns)
    {
        var sink=new Sink();
        var error=await Assert.ThrowsAsync<DiscoveryException>(()=>Read(Fixture(kind,32,values,schema,array:kind==4?2:1),sink));
        Assert.Equal("DB2_SCHEMA_MISMATCH",error.Code);Assert.Null(sink.Header);Assert.Empty(sink.Rows);
        Assert.Equal($"Field 'Value' stores 32-bit {storage} values that don't fit its {width}-bit definition width with {patterns}; only one constant pattern is narrowed, so the definition may be too narrow.",error.Message);
    }

    [Fact]
    public async Task Varying_upper_bits_on_unverified_names_and_exact_rules_keep_their_raw_evidence()
    {
        var unverified=new Sink();await Read(Fixture(3,32,[0x780107,0x790105],"<u8>",unverified:true),unverified);
        Assert.Equal(["7864583","7930117"],unverified.Rows.Select(r=>r.Values[1]));Assert.Null(unverified.Header!.Columns[1].Narrowing);
        var f=Fixture(3,32,[0x780107,0x790105],"<u8>");var rule=new Sink();await Read(f,rule,Evidence(f));
        Assert.Equal(["7864583","7930117"],rule.Rows.Select(r=>r.Values[1]));Assert.NotNull(rule.Header!.Columns[1].Interpretation);
    }

    [Theory]
    [InlineData("<8>",1ul,0xfffffffful,"1","-1")]
    [InlineData("<u8>",1ul,255ul,"1","255")]
    [InlineData("<u32>",1ul,0x80000000ul,"1","2147483648")]
    public async Task Columns_whose_stored_values_all_fit_are_unchanged_and_unmarked(string schema,ulong a,ulong b,string first,string second)
    {
        var sink=new Sink();await Read(Fixture(3,32,[a,b],schema),sink);
        Assert.Equal([first,second],sink.Rows.Select(r=>r.Values[1]));Assert.Null(sink.Header!.Columns[1].Narrowing);
    }

    [Theory]
    [InlineData(0,16,300ul)][InlineData(1,12,4095ul)][InlineData(5,12,256ul)]
    public async Task Inline_storage_that_does_not_fit_is_still_refused(uint kind,int width,ulong value)
    {
        var sink=new Sink();var error=await Assert.ThrowsAsync<DiscoveryException>(()=>Read(Fixture(kind,width,[value],"<u8>"),sink));
        Assert.Equal("DB2_SCHEMA_MISMATCH",error.Code);Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Relationship_keys_stay_strict()
    {
        var sink=new Sink();
        Assert.Equal("DB2_SCHEMA_MISMATCH",(await Assert.ThrowsAsync<DiscoveryException>(()=>Read(Fixture(3,32,[0x780107],"<u8>",relation:true),sink))).Code);
        Assert.Empty(sink.Rows);
    }

    [Fact]
    public async Task Raw_evidence_wins_over_narrowing_for_unverified_names_and_exact_rules()
    {
        var unverified=new Sink();await Read(Fixture(3,32,[0x780107],"<u8>",unverified:true),unverified);
        Assert.Equal("7864583",Assert.Single(unverified.Rows).Values[1]);Assert.NotNull(unverified.Header!.Columns[1].Interpretation);Assert.Null(unverified.Header.Columns[1].Narrowing);
        var f=Fixture(3,32,[0x780107],"<u8>");var rule=new Sink();await Read(f,rule,Evidence(f));
        Assert.Equal("7864583",Assert.Single(rule.Rows).Values[1]);Assert.NotNull(rule.Header!.Columns[1].Interpretation);Assert.Null(rule.Header.Columns[1].Narrowing);
    }

    // D108 (#95): a rule that narrows when constant reads like any D087 column when its misfit values share one upper-bit
    // pattern, keeps its raw evidence when they don't (never refusing the table), and reads at its width when all fit.
    [Theory]
    [InlineData(new ulong[]{0xffff0000,0xffffffff},"<16>","0,-1",true)]
    [InlineData(new ulong[]{0x30005,0x30007},"<u16>","5,7",true)]
    [InlineData(new ulong[]{0x10000,0x80000000},"<16>","65536,2147483648",false)]
    [InlineData(new ulong[]{0x10005,0x20005},"<u16>","65541,131077",false)]
    [InlineData(new ulong[]{5,0xffffffff},"<16>","5,-1",null)]
    public async Task A_rule_that_narrows_when_constant_narrows_one_pattern_and_keeps_mixed_columns_raw(ulong[] values,string schema,string expected,bool? narrowed)
    {
        var f=Fixture(3,32,values,schema);var limits=Evidence(f);
        limits=limits with{VerifiedRawIntegerRules=[limits.VerifiedRawIntegerRules![0] with{NarrowsWhenConstant=true}]};
        var sink=new Sink();await Read(f,sink,limits);
        Assert.Equal(expected.Split(','),sink.Rows.Select(r=>r.Values[1]));
        var column=sink.Header!.Columns[1];
        Assert.Equal(narrowed==false,column.Interpretation is not null);
        Assert.Equal(narrowed==true?new Db2ValueNarrowing(32,16):null,column.Narrowing);
        Assert.True(sink.NarrowedBeforeRows||narrowed!=true);
    }

    [Fact]
    public async Task A_rule_without_narrows_when_constant_keeps_a_constant_pattern_raw()
    {
        var f=Fixture(3,32,[0xffff0000,0xffff0001],"<16>");var sink=new Sink();await Read(f,sink,Evidence(f));
        Assert.Equal(["4294901760","4294901761"],sink.Rows.Select(r=>r.Values[1]));
        Assert.NotNull(sink.Header!.Columns[1].Interpretation);Assert.Null(sink.Header.Columns[1].Narrowing);
    }

    [Fact]
    public async Task A_narrowed_column_keeps_its_declared_reference_on_the_wire()
    {
        var f=Fixture(3,32,[0x780107],"<u8>");var sink=new Sink();await Read(f,sink);
        var column=TableDataStore.ProjectColumn(sink.Header!.Columns[1],f.Definition);
        Assert.Equal(new TableReference("Other","ID"),column.Reference);Assert.Equal(new TableColumnNarrowing(32,8),column.Narrowing);Assert.Null(column.Interpretation);
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Contains("\"narrowing\":{\"storedBits\":32,\"usedBits\":8}",JsonSerializer.Serialize(column,json));
        Assert.DoesNotContain("narrowing",JsonSerializer.Serialize(TableDataStore.ProjectColumn(sink.Header.Columns[0],f.Definition),json));
    }

    [Fact]
    public void Columns_without_narrowing_serialize_to_the_same_descriptor_bytes_as_before()
    {
        // Retained table artifacts compare their stored descriptor byte for byte, so no key may appear on them.
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal("{\"name\":\"Value\",\"kind\":\"integer\",\"isId\":false,\"interpretation\":null}",JsonSerializer.Serialize(new Db2Column("Value","integer",false),json));
        var narrowed=new Db2Column("Value","integer",false,null,new(32,8));
        Assert.Equal(narrowed,JsonSerializer.Deserialize<Db2Column>(JsonSerializer.Serialize(narrowed,json),json));
    }
}
