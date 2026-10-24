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
    [InlineData("<16>",0xd1fffful,0xd0005ul,"-1","5")]
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
