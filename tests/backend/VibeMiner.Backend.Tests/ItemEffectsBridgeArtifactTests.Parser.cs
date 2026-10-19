using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class ItemEffectsBridgeArtifactTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualPinnedBridgeParserHeaderAndExternalRelationBuildAndOpenWithFullReceipt(bool nativeKeyedSection)
    {
        var provider=await TablesCatalog.Provider.Value;var definition=provider.Definitions[ItemEffectsBridgePolicy.Table];
        var wire=BridgeWire(nativeKeyedSection);var sink=new BridgeSink();using var input=new MemoryStream(wire);
        var completion=await Wdc5TableReader.ReadAsync(input,definition.Document,"12.1.5.70448",new(AllowPartialEncryptedSections:true),sink);
        Assert.Equal(5u,sink.Header!.SchemaVersion);Assert.Equal(new[]{"ID","ItemEffectID","ItemID"},sink.Header.Columns.Select(c=>c.Name));
        Assert.Equal("7",Assert.Single(sink.Rows).Values[2]);Assert.Equal("0",sink.Rows[0].Values[1]);Assert.Equal(1,completion.PhysicalRows);
        Assert.Equal(nativeKeyedSection?"unencryptedSections":"complete",completion.Scope);Assert.Equal(nativeKeyedSection?1:0,completion.ExcludedEncryptedSections);
        await using var h=await Fixture.Create(sink.Rows.ToArray());var b=h.Binding;
        var source=b.Source.Source with{DecodedBytes=wire.Length.ToString(),Sha256=Convert.ToHexStringLower(SHA256.HashData(wire))};
        var rb=b.Rows.Binding with{SourceSha256=source.Sha256,DecodedSha256=source.Sha256};
        var rows=b.Rows with{Binding=rb,ContentKey=TableRowArtifacts.ContentKey(rb),Header=sink.Header,Completion=completion};
        h.Binding=b with{Rows=rows,Source=b.Source with{Source=source,ArtifactId=rows.ContentKey,Columns=sink.Header.Columns.Select(c=>TableDataStore.ProjectColumn(c,definition.Document)).ToArray(),
            Coverage=new(completion.Scope,completion.DeclaredRecords.ToString(),completion.PhysicalRows.ToString(),completion.CopyRows.ToString(),completion.ExcludedEncryptedSections.ToString(),
                (completion.ExcludedPhysicalRecords+completion.ExcludedCopyRecords).ToString(),sink.Header.DeclaredCopyRecords.ToString(),completion.ExcludedPhysicalRecords.ToString(),completion.ExcludedCopyRecords.ToString())}};
        Assert.Null(h.Binding.Source.Source.Projection);Assert.Equal("content-blte-v1",h.Binding.Source.Source.VerifierRevision);Assert.Equal(TableDataStore.DecoderRevision,h.Binding.Source.DecoderRevision);
        await h.Build();await using var session=await h.Open();var page=await session.ReadPageAsync(h.Request(7),h.Binding.Source,default);
        Assert.Equal("0",Assert.Single(page.Edges).ItemEffectId.Value);Assert.Equal(completion.Scope,page.BridgeSource.Coverage.Scope);
        Assert.Equal(5u,h.Artifact.Binding.Rows.Header.SchemaVersion);
        var invalid=h.Artifact with{Binding=h.Artifact.Binding with{Rows=h.Artifact.Binding.Rows with{Header=h.Artifact.Binding.Rows.Header with{SchemaVersion=1}}}};
        var owned=h.Input();Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.OpenAsync(invalid,owned,default))).Code);Assert.True(owned.SafeFileHandle.IsClosed);
    }
    // Authored WDC5 schema5 with one clear external-ID row and native external ItemID relation.
    // Optional complete keyed section remains in the full decoded stream; parser honestly excludes its row.
    private static byte[] BridgeWire(bool nativeKeyedSection)
    {
        var sections=nativeKeyedSection?2:1;var descriptor=204+sections*40;var storage=descriptor+4;var start=storage+24+(nativeKeyedSection?8:0);
        var bytes=new byte[start+sections*28];"WDC5"u8.CopyTo(bytes);Put(4,5);Put(136,(uint)sections);Put(140,1);Put(144,4);
        Put(152,0x00cb674f);Put(156,0x96F083AD);Half(172,4);Put(176,1);Put(184,1);Put(188,24);Put(200,(uint)sections);
        Half(storage+2,32);if(nativeKeyedSection){Put(storage+24,1);Put(storage+28,10);}
        for(var i=0;i<sections;i++)
        {
            var section=204+i*40;var body=start+i*28;if(i!=0)BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(section),123);
            Put(section+8,(uint)body);Put(section+12,1);Put(section+24,4);Put(section+28,20);
            Put(body,0);Put(body+4,(uint)(9+i));Put(body+8,1);Put(body+12,0);Put(body+16,0);Put(body+20,(uint)(7+i));Put(body+24,0);
        }
        return bytes;
        void Put(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset),value);
        void Half(int offset,ushort value)=>BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),value);
    }
    private sealed class BridgeSink:IWdc5Consumer
    {
        internal Wdc5TableHeader? Header;internal readonly List<Db2Row> Rows=[];
        public ValueTask HeaderAsync(Wdc5TableHeader value,CancellationToken token){Header=value;return ValueTask.CompletedTask;}
        public ValueTask RowAsync(Db2Row value,CancellationToken token){Rows.Add(value);return ValueTask.CompletedTask;}
    }
}
