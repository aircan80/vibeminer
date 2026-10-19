using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class Wdc5RawIntegerTests
{
    [Theory]
    [InlineData(0,32,65537ul,"65537",32,false)]
    [InlineData(0,64,18446744073709551615ul,"18446744073709551615",64,false)]
    [InlineData(1,12,511ul,"511",12,false)]
    [InlineData(2,32,65537ul,"65537",32,false)]
    [InlineData(3,1,65537ul,"65537",32,false)]
    [InlineData(4,1,65537ul,"65537",32,false)]
    [InlineData(5,12,3967ul,"-129",12,true)]
    public async Task Unverified_wider_integer_preserves_exact_physical_value_and_candidate_evidence(
        uint kind,int bits,ulong raw,string expected,int physicalBits,bool physicalSigned)
    {
        var sink=new Sink();await Read(Fixture(kind,bits,raw),Definition(),sink);
        Assert.Equal(["7",expected],Assert.Single(sink.Rows).Values);
        Assert.Null(sink.Header!.Columns[0].Interpretation);
        var evidence=Assert.IsType<Db2ValueInterpretation>(sink.Header.Columns[1].Interpretation);
        Assert.Equal("raw-physical-integer-v1",evidence.Policy);Assert.Equal(physicalBits,evidence.PhysicalBits);Assert.Equal(physicalSigned,evidence.PhysicalSigned);
        Assert.Equal(8,evidence.CandidateBits);Assert.True(evidence.CandidateUnsigned);Assert.InRange(evidence.Warning.Length,1,400);
    }

    [Theory]
    [InlineData(0,32,256ul)][InlineData(1,12,511ul)][InlineData(2,32,256ul)]
    [InlineData(3,1,65537ul)][InlineData(4,1,65537ul)][InlineData(5,12,3967ul)]
    public async Task Verified_columns_still_reject_lossy_narrowing_instead_of_using_raw_policy(uint kind,int bits,ulong raw)
    {
        var sink=new Sink();var error=await Assert.ThrowsAsync<DiscoveryException>(()=>Read(Fixture(kind,bits,raw),Definition(verified:true),sink));
        Assert.Equal("DB2_SCHEMA_MISMATCH",error.Code);Assert.Empty(sink.Rows);
        Assert.Contains("'Value'",error.Message);Assert.Contains("storage kind "+kind.ToString(CultureInfo.InvariantCulture),error.Message);
        Assert.Contains("unsigned8-bit",error.Message);Assert.DoesNotContain(raw.ToString(CultureInfo.InvariantCulture),error.Message);
    }

    [Fact]
    public async Task Whole_column_interpretation_is_chosen_before_rows_even_when_all_observed_values_fit()
    {
        var sink=new Sink();await Read(Fixture(3,1,7),Definition(),sink);
        Assert.NotNull(sink.Header!.Columns[1].Interpretation);Assert.Equal("7",Assert.Single(sink.Rows).Values[1]);
        sink=new();await Read(Fixture(3,1,7),Definition(verified:true),sink);Assert.Null(sink.Header!.Columns[1].Interpretation);
    }

    [Theory]
    [InlineData(0,8,255ul,"255")][InlineData(1,8,255ul,"255")][InlineData(5,2,3ul,"255")]
    public async Task Unverified_fields_whose_physical_width_fits_keep_the_candidate_numeric_policy(uint kind,int bits,ulong raw,string expected)
    {
        var sink=new Sink();await Read(Fixture(kind,bits,raw),Definition(),sink);
        Assert.Null(sink.Header!.Columns[1].Interpretation);Assert.Equal(expected,Assert.Single(sink.Rows).Values[1]);
    }

    [Theory]
    [InlineData(0)][InlineData(4)]
    public async Task Every_flattened_array_member_preserves_raw_value_and_identical_policy(uint kind)
    {
        var sink=new Sink();await Read(Fixture(kind,kind==0?32:1,65537,second:4294967295),Definition(array:true),sink);
        Assert.Equal(["ID","Value[0]","Value[1]"],sink.Header!.Columns.Select(column=>column.Name));
        Assert.Equal(["7","65537","4294967295"],Assert.Single(sink.Rows).Values);
        Assert.Equal(sink.Header.Columns[1].Interpretation,sink.Header.Columns[2].Interpretation);Assert.NotNull(sink.Header.Columns[1].Interpretation);
    }

    [Fact]
    public async Task Signed_candidate_is_preserved_as_evidence_but_unsigned_physical_bits_stay_unsigned()
    {
        var sink=new Sink();await Read(Fixture(3,1,4294967295),Definition(unsigned:false),sink);
        Assert.Equal("4294967295",Assert.Single(sink.Rows).Values[1]);Assert.False(sink.Header!.Columns[1].Interpretation!.CandidateUnsigned);
        sink=new();await Read(Fixture(3,1,4294967295),Definition(verified:true,unsigned:false),sink);
        Assert.Equal("-1",Assert.Single(sink.Rows).Values[1]);Assert.Null(sink.Header!.Columns[1].Interpretation);
    }

    [Fact]
    public async Task Unverified_inline_identity_never_opts_into_raw_physical_columns()
    {
        var bytes=Fixture(0,64,256);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(172),0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(204+24),0);
        // The sole physical field is now the inline ID; discard the external ID list.
        bytes=bytes[..^4];var definition=DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes("COLUMNS\nint ID?\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$id$ID<u32>\n"));
        var sink=new Sink();await Read(bytes,definition,sink);Assert.Null(Assert.Single(sink.Header!.Columns).Interpretation);Assert.Equal(256u,Assert.Single(sink.Rows).Id);
    }

    private static DbdDocument Definition(bool verified=false,bool array=false,bool unsigned=true)=>DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(
        "COLUMNS\nint ID\nint Value"+(verified?"":"?")+"\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$noninline,id$ID<u32>\nValue<"+(unsigned?"u":"")+"8>"+(array?"[2]":"")+"\n"));
    private static async Task Read(byte[] bytes,DbdDocument definition,Sink sink)
    {using var input=new AsyncSource(bytes);await Wdc5TableReader.ReadAsync(input,definition,"1.2.3.4",new(),sink);Assert.True(input.CanRead);}
    private static byte[] Fixture(uint kind,int bits,ulong raw,ulong? second=null)
    {
        var recordSize=kind==0?checked((bits*(second.HasValue?2:1)+7)/8):8;
        var extra=kind is 3 or 4?(second.HasValue?8:4):0;
        var prefix=204+40+28+extra;var bytes=new byte[prefix+recordSize+4];"WDC5"u8.CopyTo(bytes);
        void U(int at,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at),value);
        void H(int at,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at),value);
        U(4,5);U(136,1);U(140,1);U(144,(uint)recordSize);U(152,0x11223344);U(156,0x01020304);H(172,4);U(176,1);U(188,24);U(196,(uint)extra);U(200,1);
        U(212,(uint)prefix);U(216,1);U(228,4);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(244),(short)(kind==0?32-bits:32));
        H(248,0);H(250,(ushort)(kind==0?bits*(second.HasValue?2:1):kind==2?0:bits));U(252,(uint)extra);U(256,kind);
        U(260,kind==2?(uint)raw:0);U(264,kind is 1 or 3 or 4 or 5?(uint)bits:0);U(268,kind==4?(second.HasValue?2u:1u):0);
        if(kind is 3 or 4){U(272,(uint)raw);if(second.HasValue)U(276,(uint)second.Value);}
        if(kind is 0 or 1 or 5)
        {
            for(var at=0;at<recordSize&&at<8;at++)bytes[prefix+at]=(byte)(raw>>(at*8));
            if(second.HasValue)for(var at=0;at<bits/8;at++)bytes[prefix+bits/8+at]=(byte)(second.Value>>(at*8));
        }
        U(prefix+recordSize,7);return bytes;
    }
    private sealed class Sink:IWdc5Consumer
    {
        public Wdc5TableHeader? Header;public List<Db2Row> Rows=[];
        public ValueTask HeaderAsync(Wdc5TableHeader header,CancellationToken token){Header=header;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row row,CancellationToken token){Rows.Add(row);return ValueTask.CompletedTask;}
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
