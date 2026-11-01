using System.Buffers.Binary;
using System.Text;
using VibeMiner.Formats;
using Xunit;

namespace VibeMiner.Formats.Tests;

public sealed class Db2IndexSizeTests
{
    // Open charges 64 bytes of index budget per declared copy. That covers the copy entry, its resolution and its index
    // entry only while CopyResolution stays 12 bytes; growing it means raising the per-copy charge.
    [Fact]
    public void Copy_resolution_stays_12_bytes()=>
        Assert.Equal(12,System.Runtime.CompilerServices.Unsafe.SizeOf<Db2Reader.CopyResolution>());

    // D293: Retail SoundKitEntry's shape. Every fixture ends with one undeclared byte, so a table that passes every limit stops
    // at the trailing-byte check, before any index is allocated; test memory stays at the zero-filled source.
    private const int SoundKitRecords=1_312_714,SoundKitCopies=1_112,SoundKitPairs=SoundKitRecords+SoundKitCopies;
    private const string Trailing="The WDC5 source has undeclared trailing bytes.";
    private static readonly Db2Limits Old=new(MaxRows:1_000_000,MaxRelationshipEntries:1_000_000,MaxIndexBytes:128L<<20);

    [Fact]
    public async Task Retail_SoundKitEntry_shape_passes_the_default_limits_and_each_old_limit_refused_it()
    {
        var bytes=Build(SoundKitRecords,SoundKitCopies,SoundKitPairs);
        Assert.Equal(("DB2_INVALID",Trailing),await Error(bytes,new()));
        Assert.Equal(("DB2_LIMIT","WDC5 record count exceeds policy."),await Error(bytes,Old));
        var defaults=new Db2Limits();
        Assert.Equal("DB2_LIMIT",(await Error(bytes,defaults with{MaxRows=Old.MaxRows})).Code);
        Assert.Equal(("DB2_LIMIT","Aggregate relationship bytes/entry capacity exceed policy."),await Error(bytes,defaults with{MaxRelationshipEntries=Old.MaxRelationshipEntries}));
        Assert.Equal(("DB2_LIMIT","Relationship state and bounded suffix scratch exceed the aggregate index reservation."),await Error(bytes,defaults with{MaxIndexBytes=Old.MaxIndexBytes}));
    }

    [Fact]
    public async Task One_row_or_entry_over_two_million_is_still_refused()
    {
        Assert.Equal("DB2_LIMIT",(await Error(Build(2_000_001,0,1),new())).Code);
        Assert.Equal(("DB2_LIMIT","Physical plus declared copy rows exceed policy."),await Error(Build(1_000_000,1_000_001,1),new()));
        Assert.Equal(("DB2_INVALID",Trailing),await Error(Build(1,0,2_000_000),new()));
        Assert.Equal(("DB2_LIMIT","Aggregate relationship bytes/entry capacity exceed policy."),await Error(Build(1,0,2_000_001),new()));
    }

    // One fixed section of one-byte records with an inline ID, a noninline relation, and a trailing byte. Copies and pairs are
    // zeros: they are read only after the trailing-byte check.
    private static byte[] Build(int records,int copies,int pairs)
    {
        const int start=204+40+28;long relations=12+pairs*8L;
        var end=checked(start+records+copies*8L+relations);var bytes=new byte[end+1];
        "WDC5"u8.CopyTo(bytes);Put(bytes,4,5);Put(bytes,136,(uint)records);Put(bytes,140,1);Put(bytes,144,1);Put(bytes,152,0x11223344);Put(bytes,156,0x01020304);
        Put(bytes,176,1);Put(bytes,184,1);Put(bytes,188,24);Put(bytes,200,1);
        Put(bytes,204+8,start);Put(bytes,204+12,(uint)records);Put(bytes,204+28,(uint)relations);Put(bytes,204+36,(uint)copies);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(244),24);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(248+2),8);
        Put(bytes,(int)(start+records+copies*8L),(uint)pairs);Put(bytes,(int)(start+records+copies*8L+8),uint.MaxValue);
        return bytes;
    }
    private static readonly DbdDocument Definition=DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(
        "COLUMNS\nint ID\nint Parent\n\nLAYOUT 01020304\nBUILD 1.2.3.4\n$id$ID<u8>\n$noninline,relation$Parent<u32>\n"));
    private static async Task<(string Code,string Message)> Error(byte[] bytes,Db2Limits limits)
    {
        var error=await Assert.ThrowsAsync<Db2FormatException>(()=>Db2Reader.ReadAsync(new MemoryByteSource(bytes),Definition,"1.2.3.4",limits,new Sink(),default));
        return (error.Code,error.Message);
    }
    private static void Put(byte[] b,int at,uint n)=>BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),n);
    private sealed class Sink:IDb2Consumer
    {
        public ValueTask HeaderAsync(Db2TableHeader header,CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask RowAsync(Db2Row row,CancellationToken token)=>ValueTask.CompletedTask;
    }
}
