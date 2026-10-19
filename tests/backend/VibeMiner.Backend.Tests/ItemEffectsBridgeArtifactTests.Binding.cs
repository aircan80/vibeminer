using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class ItemEffectsBridgeArtifactTests
{
    [Fact]
    public async Task PartialBridgeRetainsExactReadableScopeAndUnknownOutsideThatScope()
    {
        await using var h=await Fixture.Create([Row(1,7,0),Row(2,7,-1)]);var b=h.Binding;
        var projection=new ContentProjection(ContentProjectionIdentity.Policy,"",[new("0","32",new('a',64))],[new("32","32",1,"0011223344556677")]);
        projection=projection with{IdentitySha256=ContentProjectionIdentity.Compute(b.Source.Source.ContentKey,b.Source.Source.EncodingKey,b.Source.Source.DecodedBytes,projection)};
        var source=b.Source.Source with{Projection=projection,VerifierRevision=ContentProjectionIdentity.VerifierRevision};
        var rb=b.Rows.Binding with{DecodedSha256=projection.IdentitySha256,DecoderRevision=TableDataStore.ProjectionDecoderRevision};
        var rows=b.Rows with{Binding=rb,ContentKey=TableRowArtifacts.ContentKey(rb),Header=b.Rows.Header with{DeclaredRecords=3},
            Completion=b.Rows.Completion with{DeclaredRecords=3,ExcludedEncryptedSections=1,ExcludedPhysicalRecords=1,Scope="unencryptedSections"}};
        h.Binding=b with{Rows=rows,Source=b.Source with{Source=source,ArtifactId=rows.ContentKey,DecoderRevision=rb.DecoderRevision,Coverage=new("unencryptedSections","3","2","0","1","1","0","1","0")}};
        await h.Build();await using var reader=await h.Open();var page=await reader.ReadPageAsync(h.Request(7),h.Binding.Source,default);
        Assert.Equal("unencryptedSections",page.BridgeSource.Coverage.Scope);Assert.Equal("1",page.BridgeSource.Coverage.ExcludedPhysicalRecords);Assert.Equal("2",page.ObservedAssociations);
        Assert.Equal(new[]{"0","-1"},page.Edges.Select(e=>e.ItemEffectId.Value));
        var empty=await reader.ReadPageAsync(h.Request(8),h.Binding.Source,default);Assert.Empty(empty.Edges);Assert.Equal("unencryptedSections",empty.BridgeSource.Coverage.Scope);
    }
    [Theory]
    [InlineData("columns")] [InlineData("headerColumns")] [InlineData("memberships")] [InlineData("configurations")] [InlineData("ranges")]
    public async Task PathSpecificCountsFailBeforeCaptureOrSourceFeed(string field)
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);var b=h.Binding;
        b=field switch
        {
            "columns"=>b with{Source=b.Source with{Columns=Enumerable.Repeat(b.Source.Columns[0],513).ToArray()}},
            "headerColumns"=>b with{Rows=b.Rows with{Header=b.Rows.Header with{Columns=Enumerable.Repeat(b.Rows.Header.Columns[0],513).ToArray()}}},
            "memberships"=>b with{Source=b.Source with{Association=b.Source.Association with{Memberships=Enumerable.Repeat(b.Source.Association.Memberships[0],257).ToArray()}}},
            "configurations"=>b with{Configurations=[..b.Configurations,b.Configurations[0]]},
            _=>b with{Source=b.Source with{Source=b.Source.Source with{Projection=new(ContentProjectionIdentity.Policy,new('a',64),Enumerable.Repeat(new ContentReadableRange("0","1",new('b',64)),129).ToArray(),[])}}}
        };
        await using var stage=h.Stage();var called=false;
        Assert.Equal("ITEM_EFFECTS_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.BuildAsync(b,stage,(_,_,_)=>{called=true;throw new Exception();},default))).Code);
        Assert.False(called);Assert.Equal(0,stage.Length);
    }
    [Theory]
    [InlineData("field")] [InlineData("table")] [InlineData("layout")] [InlineData("provider")] [InlineData("definition")] [InlineData("decoder")]
    [InlineData("scope")] [InlineData("coverage")] [InlineData("rowCount")] [InlineData("consumed")] [InlineData("schema")] [InlineData("verifier")]
    public async Task IncorrectSourceBindingCannotBeTurnedIntoAnIndex(string kind)
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);var b=h.Binding;
        b=kind switch
        {
            "field"=>b with{Fields=b.Fields with{ItemId=new(ItemEffectsBridgePolicy.Table,1)}},
            "table"=>b with{Source=b.Source with{TableId="00000000"}},"layout"=>b with{Source=b.Source with{LayoutHash="00000000"}},
            "provider"=>b with{Source=b.Source with{ProviderRevision="old"}},"definition"=>b with{Source=b.Source with{DefinitionSha256=new('a',64)}},
            "decoder"=>b with{Source=b.Source with{DecoderRevision="old"}},"scope"=>b with{Rows=b.Rows with{Completion=b.Rows.Completion with{Scope="unknown"}}},
            "coverage"=>b with{Source=b.Source with{Coverage=b.Source.Coverage with{PhysicalRows="2"}}},"rowCount"=>b with{Source=b.Source with{RowCount="2"}},
            "consumed"=>b with{Rows=b.Rows with{Completion=b.Rows.Completion with{ConsumedBytes=63}}},
            "schema"=>b with{Rows=b.Rows with{Header=b.Rows.Header with{SchemaVersion=1}}},
            _=>b with{Source=b.Source with{Source=b.Source.Source with{VerifierRevision="unproved"}}}
        };
        await using var stage=h.Stage();Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.BuildAsync(b,stage,h.Feed,default))).Code);Assert.Equal(0,stage.Length);
    }
    [Fact]
    public async Task ReusableMetadataNormalizesOnlyExplicitObservationTimestamps()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);var b=h.Binding;var key=ItemEffectsBridgePolicy.ContentKey(b,"1");
        var later=b with{Configurations=b.Configurations.Select(c=>c with{RetrievedAt="later",VerifiedAt="later"}).ToArray(),Source=b.Source with{PreparedAt="later",Source=b.Source.Source with{RetrievedAt="later",VerifiedAt="later"},
            Association=b.Source.Association with{EncodingSource=b.Source.Association.EncodingSource with{RetrievedAt="later",VerifiedAt="later"},RootSource=b.Source.Association.RootSource with{RetrievedAt="later",VerifiedAt="later"}}}};
        Assert.Equal(key,ItemEffectsBridgePolicy.ContentKey(later,"1"));Assert.False(ItemEffectsBridgePolicy.SameAuthority(b,later));
        Assert.NotEqual(key,ItemEffectsBridgePolicy.ContentKey(b with{SourceReference=b.SourceReference with{Generation="2"}},"1"));
        Assert.NotEqual(key,ItemEffectsBridgePolicy.ContentKey(b with{Source=b.Source with{Source=b.Source.Source with{Location=b.Source.Source.Location with{Offset="1"}}}},"1"));
    }
}
