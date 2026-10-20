using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class CreatureGeometryStoreTests
{
    [Fact]
    public async Task AssetContextCleanupFailsAdmissionClosedBeforeHeldObserverCanRetire()
    {
        await using var f=await Fixture.Create();var observerEntered=f.Gate();var observerRelease=f.Gate();var canceled=f.Gate();
        CancellationTokenRegistration notification=default;IDisposable? sourceDebt=null;
        f.Work.Produce=async(_,budget,progress,token)=>
        {
            sourceDebt=budget.Reserve(32L<<20);notification=token.Register(()=>canceled.TrySetResult());
            await progress(new("sourceContext","Opening exact sources","stage"));await observerEntered.Task;
            throw new DiscoveryException("ASSET_SOURCE_CLEANUP","Authored B context cleanup uncertainty.");
        };
        f.ExpectDebt=true;
        var prepare=f.Store.PrepareAsync(new(f.Request),async _=>{observerEntered.TrySetResult();await observerRelease.Task;},default);
        await canceled.Task.WaitAsync(Guard);Assert.False(prepare.IsCompleted);Assert.NotNull(sourceDebt);
        Assert.True(f.Store.Pressure.Closed);Assert.Equal(48L<<20,f.Store.Pressure.Payload);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.ReadAsync(new(f.Request),default))).Code);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.OpenAsync(new(f.Request,CreatureGeometryTestFixture.Manifest().ArtifactId),default))).Code);
        observerRelease.SetResult();await Assert.ThrowsAsync<DiscoveryException>(()=>prepare.WaitAsync(Guard));await notification.DisposeAsync();
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DerivedStageDeleteOrReservationFaultKeepsActualOwnerAndStoreClosed(bool releaseFault)
    {
        await using var f=await Fixture.Create();DerivedArtifactStaging? retained=null;FaultCharge? charge=null;
        f.Work.Produce=async(_,budget,_,token)=>
        {
            charge=new(budget.Reserve(32L<<20),releaseFault);
            retained=new(charge,path=>{if(!releaseFault)throw new IOException("Authored delete debt");File.Delete(path);},"CREATURE_GEOMETRY_CLEANUP_FAILED");
            await retained.CreateAsync(f.Source.Catalog.Projection.Objects.CreateStagingFileAsync,token);
            await retained.CloseAsync();if(!await retained.RetireAsync())throw retained.Failure();
            return new Publication();
        };
        f.ExpectDebt=true;
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard))).Code);
        Assert.NotNull(retained);Assert.False(await retained.RetireAsync());Assert.False(charge!.Released);
        Assert.Equal(releaseFault?1:0,charge.Attempts);Assert.Equal((0,0,48L<<20,true),f.Store.Pressure);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.ReadAsync(new(f.Request),default))).Code);
    }
    private sealed class FaultCharge(IDisposable inner,bool fail):IDisposable
    {
        internal int Attempts;internal bool Released;
        public void Dispose(){Attempts++;if(fail)throw new IOException("Authored release debt");inner.Dispose();Released=true;}
    }
    [Fact]
    public async Task LastConsumerBodyRetiresBeforePublicationCanDropItsOwnedSourceSnapshot()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var bodyRelease=f.Gate();var canceled=f.Gate();var bodyFinished=false;var calls=0;
        f.Work.Authorize=async(_,_)=>
        {
            if(Interlocked.Increment(ref calls)==2){entered.TrySetResult();await bodyRelease.Task;bodyFinished=true;}
            return f.Proof;
        };
        CancellationTokenRegistration notification=default;
        var publication=new Publication{Dispose=()=>{Assert.True(bodyFinished);return Task.CompletedTask;}};
        f.Work.Produce=(_,_,_,token)=>{notification=token.Register(()=>canceled.TrySetResult());return Task.FromResult<IGeometryStorePublication>(publication);};
        using var stop=new CancellationTokenSource();var prepare=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);await entered.Task.WaitAsync(Guard);
        stop.Cancel();await canceled.Task.WaitAsync(Guard);Assert.False(prepare.IsCompleted);Assert.Equal(48L<<20,f.Store.Pressure.Payload);
        bodyRelease.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>prepare.WaitAsync(Guard));await notification.DisposeAsync();
        Assert.Equal(1,publication.Closes);Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task TypedOriginalSourceCloseDebtKeepsActualHolderAndProducerReservation()
    {
        await using var f=await Fixture.Create();var staging=new Charge();
        f.Work.Produce=async(_,budget,_,_)=>
        {
            var source=budget.Reserve(32L<<20);var owner=new GeometrySourceVerification.Owner(staging);owner.OwnEncoded(new FailingCloseStream());
            try{await owner.CloseEncodedAsync();throw new InvalidOperationException("Expected encoded close failure");}
            catch(GeometrySourceCleanupException error){error.RetainPayload(source);throw;}
        };
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard))).Code);
        Assert.False(staging.Released);Assert.Equal((0,0,48L<<20,true),f.Store.Pressure);f.ExpectDebt=true;
    }
    private sealed class Charge:IDisposable{internal bool Released;public void Dispose()=>Released=true;}
    [Fact]
    public async Task CancellationAfterSuccessfulFinalFenceCannotPublishLateTerminalSuccess()
    {
        await using var f=await Fixture.Create();var committed=f.Gate();var deliver=f.Gate();
        f.Work.Produce=(_,_,_,_)=>Task.FromResult<IGeometryStorePublication>(new Publication{Finalize=async(_,_)=>
        {committed.TrySetResult();await deliver.Task;return new(null,Sources);}});
        using var stop=new CancellationTokenSource();var task=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);
        await committed.Task.WaitAsync(Guard);stop.Cancel();Assert.False(task.IsCompleted);deliver.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(Guard));Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task ApplicationOwnsOnlyDeadlineWhileStoreHoldsExpiredPhysicalLeaseUntilActualClose()
    {
        await using var f=await Fixture.Create();await f.Store.DisposeAsync();var clock=new ManualTimerClock();
        f.Store=new(f.Work,new(),new Cpu(),clock);var closing=f.Gate();var release=f.Gate();
        f.Work.Retained=new Retained{Open=()=>Task.FromResult<Stream>(new CloseStream(closing,release))};
        await using var service=new CreatureGeometryService(f.Store,clock);
        var lease=await service.OpenAsync(new(f.Request,f.Work.Retained.Manifest.ArtifactId));Assert.Equal(1,clock.TimerCount);
        clock.Advance(TimeSpan.FromSeconds(30));await closing.Task.WaitAsync(Guard);Assert.False(lease.Retirement.IsCompleted);
        Assert.Equal(8L<<20,f.Store.Pressure.Payload);Assert.Equal(1,clock.TimerCount);release.SetResult();await lease.Retirement.WaitAsync(Guard);
        Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ProvisionalCloseFailureBeforeStreamTransferKeepsReaderDebtAndClosesFeature(bool open)
    {
        await using var f=await Fixture.Create();var physical=new FailingCloseStream();
        f.Work.Retained=new Retained{Open=async()=>
        {
            try{await physical.DisposeAsync();}catch(IOException){throw new DiscoveryException("CREATURE_GEOMETRY_CLEANUP_FAILED","authored provisional close debt");}
            throw new InvalidOperationException("Expected close failure");
        }};
        var error=open?await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.OpenAsync(new(f.Request,f.Work.Retained.Manifest.ArtifactId),default)):
            await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.ReadAsync(new(f.Request),default));
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",error.Code);Assert.Equal((0,1,8L<<20,true),f.Store.Pressure);f.ExpectDebt=true;
    }
    [Fact]
    public async Task ThrowingCancellationCallbackIsActuallyInvokedAndProducerCleanupStillRetires()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var invoked=f.Gate();var producerExit=f.Gate();var closing=f.Gate();var cleanup=f.Gate();
        f.Work.Produce=async(_,_,_,token)=>
        {
            using var registration=token.Register(()=>{try{throw new InvalidOperationException("authored throwing callback");}finally{invoked.TrySetResult();}});
            entered.SetResult();await producerExit.Task;return new Publication{Dispose=async()=>{closing.SetResult();await cleanup.Task;}};
        };
        using var stop=new CancellationTokenSource();var task=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);await entered.Task.WaitAsync(Guard);
        stop.Cancel();await invoked.Task.WaitAsync(Guard);Assert.False(task.IsCompleted);producerExit.SetResult();await closing.Task.WaitAsync(Guard);
        Assert.False(task.IsCompleted);Assert.Equal(48L<<20,f.Store.Pressure.Payload);cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(Guard));Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task HeldObserverRetainsConsumerAndGlobalChargeAfterProducerCompletes()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var release=f.Gate();var producerEntered=f.Gate();var producerExit=f.Gate();Func<CreatureGeometryProgress,Task>? report=null;
        f.Work.Produce=async(_,_,p,_)=>{report=p;producerEntered.SetResult();await producerExit.Task;return new Publication();};
        var prepare=f.Store.PrepareAsync(new(f.Request),async _=>{entered.TrySetResult();await release.Task;},default);await producerEntered.Task.WaitAsync(Guard);
        await report!(new("modelParsing","Reading model","bytes",1,2));await entered.Task.WaitAsync(Guard);producerExit.SetResult();
        // The actual callback has entered and is independently held; completing producer work cannot retire it.
        var read=await f.Store.ReadAsync(new(f.Request),default);Assert.Equal(CreatureGeometryState.NotPrepared,read.State);
        Assert.False(prepare.IsCompleted);Assert.Equal(48L<<20,f.Store.Pressure.Payload);release.SetResult();await prepare.WaitAsync(Guard);
        Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task SlowAndThrowingObserversDoNotHoldProducerOrPeerProgress()
    {
        await using var f=await Fixture.Create();var producerEntered=f.Gate();var producerExit=f.Gate();var aEntered=f.Gate();var aExit=f.Gate();var bEntered=f.Gate();Func<CreatureGeometryProgress,Task>? report=null;
        f.Work.Produce=async(_,_,p,_)=>{report=p;producerEntered.SetResult();await producerExit.Task;return new Publication();};
        var a=f.Store.PrepareAsync(new(f.Request),async _=>{aEntered.TrySetResult();await aExit.Task;throw new IOException("authored observer failure");},default);
        await producerEntered.Task.WaitAsync(Guard);
        var b=f.Store.PrepareAsync(new(f.Request),_=>{bEntered.TrySetResult();return Task.CompletedTask;},default);
        await report!(new("skinParsing","Reading skin","bytes",10,10));await Task.WhenAll(aEntered.Task,bEntered.Task).WaitAsync(Guard);
        producerExit.SetResult();Assert.Equal(CreatureGeometryState.NoDeclaredTriangles,(await b.WaitAsync(Guard)).State);Assert.False(a.IsCompleted);
        aExit.SetResult();await a.WaitAsync(Guard);Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task FixedClockProgressHoldsOneRunningAndOnlyLatestOfThousandUpdates()
    {
        var clock=new ManualTimerClock();var entered=Signal();var release=Signal();var latest=Signal();var values=new List<long?>();
        var observer=new CreatureGeometryStoreProgress(async p=>
        {
            values.Add(p.Completed);if(p.Completed==0){entered.TrySetResult();await release.Task;}else latest.TrySetResult();
        },clock);
        observer.Post(new("modelParsing","Reading model","bytes",0,1000));await entered.Task.WaitAsync(Guard);
        for(var i=1;i<=1000;i++)observer.Post(new("modelParsing","Reading model","bytes",i,1000));
        Assert.Equal(new long?[]{0},values);clock.Advance(TimeSpan.FromMilliseconds(100));release.SetResult();await latest.Task.WaitAsync(Guard);
        await observer.CloseAsync();Assert.Equal(new long?[]{0,1000},values);
    }
}
