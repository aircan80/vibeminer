using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class CreatureGeometryStoreTests
{
    private static GeometryDisplayAuthorizationResult Partial(GeometryDisplayAuthorizationResult full)=>full with
    {Authorization=null,Delivered=Array.AsReadOnly(full.Delivered.Take(1).ToArray()),Metadata=full.Metadata with
    {State=CreatureDisplayMetadataState.NotPrepared,Targets=Array.AsReadOnly(full.Metadata.Targets.Take(1).ToArray()),ModelFileDataId=null,RequiredTableId="7f695fa5"}};
    [Fact]
    public async Task CaptureReservationPrecedesHelperAndOversizeProofRefusesBeforeSourceWork()
    {
        await using var f=await Fixture.Create();
        f.Work.Authorize=(_,_)=>
        {
            Assert.Equal(48L<<20,f.Store.Pressure.Payload);
            return Task.FromResult(f.Proof with{Core=f.Proof.Core with{Opening=f.Proof.Core.Opening with
                {Versions=f.Proof.Core.Opening.Versions with{Bytes=new byte[(1<<20)+1]}}}});
        };
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard))).Code);
        Assert.Equal(0,f.Work.Produces);Assert.Equal(0,f.Store.Pressure.Payload);Assert.False(f.Store.Pressure.Closed);
    }
    [Fact]
    public async Task LocalUnpreparedMetadataNeverStartsPrerequisitesOrSources()
    {
        await using var f=await Fixture.Create();var partial=Partial(f.Proof);f.Work.Authorize=(_,_)=>Task.FromResult(partial);
        var read=await f.Store.ReadAsync(new(f.Request),default);Assert.Equal(CreatureGeometryState.NotPrepared,read.State);
        Assert.Single(read.Metadata.Targets);Assert.Equal(0,f.Work.Ensures);Assert.Equal(0,f.Work.Produces);
    }
    [Fact]
    public async Task MetadataPrerequisiteKeepsOriginalCoreSlotAndDeliveredDisplayBinding()
    {
        await using var f=await Fixture.Create();var original=Partial(f.Proof);var current=original;var entered=f.Gate();var release=f.Gate();
        f.Work.Authorize=(_,_)=>Task.FromResult(current);
        f.Work.Ensure=async token=>{entered.SetResult();await release.Task.WaitAsync(token);current=f.Proof;return await f.Displays.ReadDisplayAsync(f.Request,token);};
        f.Work.Produce=(selection,_,_,_)=>
        {
            Assert.Same(original.Core,selection.Original.Core);Assert.Same(original.Selected,selection.Original.Selected);
            Assert.Same(original.Delivered[0],selection.Original.Display);return Task.FromResult<IGeometryStorePublication>(new Publication());
        };
        var prepare=f.Store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);
        Assert.Equal(48L<<20,f.Store.Pressure.Payload);release.SetResult();Assert.Equal(CreatureGeometryState.NoDeclaredTriangles,(await prepare.WaitAsync(Guard)).State);
        Assert.Equal(1,f.Work.Ensures);Assert.Equal(1,f.Work.Produces);
    }
    [Theory]
    [InlineData("opening")][InlineData("slot")][InlineData("displayPreparedAt")][InlineData("creaturePreparedAt")]
    public async Task MetadataWaitNeverAdoptsReplacementAuthority(string mutation)
    {
        await using var f=await Fixture.Create();var original=Partial(f.Proof);var current=original;var entered=f.Gate();var release=f.Gate();
        f.Work.Authorize=(_,_)=>Task.FromResult(current);
        f.Work.Ensure=async token=>{entered.SetResult();await release.Task.WaitAsync(token);return await f.Displays.ReadDisplayAsync(f.Request,token);};
        var prepare=f.Store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);current=f.Proof;
        if(mutation=="opening")current=current with{Core=current.Core with{Opening=current.Core.Opening with{Reference=current.Core.Opening.Reference with{Generation="999"}}}};
        if(mutation=="slot")current=current with{Selected=current.Selected! with{Probability="0.75"}};
        if(mutation=="creaturePreparedAt")current=current with{Core=current.Core with{Snapshot=current.Core.Snapshot with{PreparedAt="2026-10-20T00:00:00Z"}}};
        if(mutation=="displayPreparedAt")
        {
            var display=current.Delivered[0];display=display with{Binding=display.Binding with{Admission=display.Binding.Admission with{PreparedAt="2026-10-20T00:00:00Z"}}};
            current=current with{Delivered=Array.AsReadOnly(new[]{display,current.Delivered[1]})};
        }
        release.SetResult();Assert.Equal("CREATURE_GEOMETRY_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>prepare.WaitAsync(Guard))).Code);
        Assert.Equal(0,f.Work.Produces);Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task FailedMetadataPrerequisiteKeepsItsActualErrorWithoutInventingMissingRow()
    {
        await using var f=await Fixture.Create();var partial=Partial(f.Proof);f.Work.Authorize=(_,_)=>Task.FromResult(partial);
        var full=await f.Displays.ReadDisplayAsync(f.Request,default);
        f.Work.Ensure=_=>Task.FromResult(full with{State=CreatureDisplayMetadataState.Protected,RequiredTableId=null,ModelFileDataId=null,
            Problem=new("BLTE_ENCRYPTED","The exact model table is protected.",false,CreatureDisplayTargetRole.Model)});
        var result=await f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard);
        Assert.Equal(CreatureGeometryState.MetadataNotResolved,result.State);Assert.Equal(CreatureDisplayMetadataState.Protected,result.Metadata.State);
        Assert.Equal("BLTE_ENCRYPTED",result.Problem!.Code);Assert.Equal(CreatureDisplayTargetRole.Model,result.Problem.Target);
        Assert.Null(result.Metadata.Absence);Assert.Single(result.Metadata.Targets);Assert.Equal(0,f.Work.Produces);
    }
}
