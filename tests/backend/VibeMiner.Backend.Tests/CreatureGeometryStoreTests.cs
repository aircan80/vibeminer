using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed partial class CreatureGeometryStoreTests
{
    private static readonly TimeSpan Guard=TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Progress(CreatureGeometryProgress _)=>Task.CompletedTask;
    [Fact]
    public async Task TwoCompatibleConsumersShareProducerAndOtherSelectionNeverQueues()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var release=f.Gate();
        f.Work.Produce=async(_,_,_,_)=>{entered.SetResult();await release.Task;return new Publication();};
        var first=f.Store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);
        var second=f.Store.PrepareAsync(new(f.Request),Progress,default);
        Assert.Equal("CREATURE_GEOMETRY_BUSY",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default))).Code);
        Assert.Equal("CREATURE_GEOMETRY_BUSY",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request with{Slot=1}),Progress,default))).Code);
        Assert.Equal(1,f.Work.Produces);Assert.Equal(48L<<20,f.Store.Pressure.Payload);
        release.SetResult();var results=await Task.WhenAll(first,second).WaitAsync(Guard);
        Assert.All(results,r=>Assert.Equal(CreatureGeometryState.NoDeclaredTriangles,r.State));
        Assert.Equal((0,0,0L,false),f.Store.Pressure);
    }
    [Fact]
    public async Task CancelOneConsumerPreservesOtherAndLastCompletionReleasesPayload()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var release=f.Gate();
        f.Work.Produce=async(_,_,_,_)=>{entered.SetResult();await release.Task;return new Publication();};
        using var stop=new CancellationTokenSource();var first=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);await entered.Task.WaitAsync(Guard);
        var second=f.Store.PrepareAsync(new(f.Request),Progress,default);stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first.WaitAsync(Guard));Assert.False(second.IsCompleted);
        Assert.Equal(48L<<20,f.Store.Pressure.Payload);release.SetResult();await second.WaitAsync(Guard);Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task LastCancellationWaitsProducerAndPublicationCleanupBeforeCapacity()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var canceled=f.Gate();var exit=f.Gate();var closing=f.Gate();var closed=f.Gate();
        f.Work.Produce=async(_,_,_,token)=>{using var r=token.Register(()=>canceled.TrySetResult());entered.SetResult();await exit.Task;return new Publication{Dispose=async()=>{closing.SetResult();await closed.Task;}};};
        using var stop=new CancellationTokenSource();var prepare=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);await entered.Task.WaitAsync(Guard);
        stop.Cancel();await canceled.Task.WaitAsync(Guard);Assert.False(prepare.IsCompleted);Assert.Equal(48L<<20,f.Store.Pressure.Payload);
        exit.SetResult();await closing.Task.WaitAsync(Guard);Assert.False(prepare.IsCompleted);closed.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>prepare.WaitAsync(Guard));Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task CanceledAuthorizationBeforeWorkerExistsRetiresWithoutHanging()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();
        f.Work.Authorize=async(_,token)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);return f.Proof;};
        using var stop=new CancellationTokenSource();var task=f.Store.PrepareAsync(new(f.Request),Progress,stop.Token);await entered.Task.WaitAsync(Guard);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(Guard));Assert.Equal((0,0,0L,false),f.Store.Pressure);
    }
    [Fact]
    public async Task ShutdownWaitsCancellationAwareAuthorizationAndReleasesReservation()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();
        f.Work.Authorize=async(_,token)=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);return f.Proof;};
        var task=f.Store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);
        var shutdown=f.Store.DisposeAsync().AsTask();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(Guard));await shutdown.WaitAsync(Guard);
        Assert.Equal((0,0,0L,true),f.Store.Pressure);
    }
    [Fact]
    public async Task SourceFinalizationFailureDoesNotReturnReadyAndCleanupStillRuns()
    {
        await using var f=await Fixture.Create();var publication=new Publication{Finalize=(_,_)=>throw new DiscoveryException("CREATURE_GEOMETRY_STALE","authored final fence")};
        f.Work.Produce=(_,_,_,_)=>Task.FromResult<IGeometryStorePublication>(publication);
        Assert.Equal("CREATURE_GEOMETRY_STALE",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard))).Code);
        Assert.Equal(1,publication.Closes);Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task SourceCleanupDebtLatchesFeatureAndRetainsProducerCharge()
    {
        await using var f=await Fixture.Create();f.Work.Produce=(_,_,_,_)=>Task.FromResult<IGeometryStorePublication>(new Publication{Dispose=()=>throw new IOException("authored close debt")});
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.PrepareAsync(new(f.Request),Progress,default).WaitAsync(Guard))).Code);
        Assert.Equal(48L<<20,f.Store.Pressure.Payload);Assert.True(f.Store.Pressure.Closed);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.ReadAsync(new(f.Request),default))).Code);
        f.ExpectDebt=true;
    }
    [Fact]
    public async Task LocalReadDoesNotPrepareAndMissingArtifactIsNotPrepared()
    {
        await using var f=await Fixture.Create();var result=await f.Store.ReadAsync(new(f.Request),default);
        Assert.Equal(CreatureGeometryState.NotPrepared,result.State);Assert.Empty(result.Sources);Assert.Equal(0,f.Work.Produces);Assert.Equal(0,f.Work.Ensures);
        Assert.Equal((0,0,0L,false),f.Store.Pressure);
    }
    [Fact]
    public async Task HeldOpenAndTemporaryReadShareExactlyTwoPhysicalSlots()
    {
        await using var f=await Fixture.Create();var retained=new Retained();f.Work.Retained=retained;
        var lease=await f.Store.OpenAsync(new(f.Request,retained.Manifest.ArtifactId),default);var entered=f.Gate();var release=f.Gate();
        retained.Open=async()=>{entered.SetResult();await release.Task;return new MemoryStream(new byte[320],false);};
        var read=f.Store.ReadAsync(new(f.Request),default);await entered.Task.WaitAsync(Guard);
        Assert.Equal((0,2,16L<<20,false),f.Store.Pressure);
        Assert.Equal("CREATURE_GEOMETRY_BUSY",(await Assert.ThrowsAsync<DiscoveryException>(()=>f.Store.OpenAsync(new(f.Request,retained.Manifest.ArtifactId),default))).Code);
        release.SetResult();Assert.Equal(CreatureGeometryState.Ready,(await read.WaitAsync(Guard)).State);
        Assert.Equal(1,f.Store.Pressure.Readers);await lease.DisposeAsync();await lease.Retirement;Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task LateCanceledOpenAndGatedCloseKeepPhysicalSlotCharged()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var late=f.Gate();var closing=f.Gate();var releaseClose=f.Gate();
        var retained=new Retained{Open=async()=>{entered.SetResult();await late.Task;return new CloseStream(closing,releaseClose);}};f.Work.Retained=retained;
        using var stop=new CancellationTokenSource();var open=f.Store.OpenAsync(new(f.Request,retained.Manifest.ArtifactId),stop.Token);
        await entered.Task.WaitAsync(Guard);stop.Cancel();Assert.False(open.IsCompleted);Assert.Equal(8L<<20,f.Store.Pressure.Payload);
        late.SetResult();await closing.Task.WaitAsync(Guard);Assert.False(open.IsCompleted);Assert.Equal(1,f.Store.Pressure.Readers);
        releaseClose.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>open.WaitAsync(Guard));Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task LeaseDisposalWaitsActuallyHeldReadAndRetirementMatchesCleanup()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var exit=f.Gate();var closing=f.Gate();var closed=f.Gate();var cancellationObserved=f.Gate();
        f.Work.Retained=new Retained{Open=()=>Task.FromResult<Stream>(new ReadStream(entered,exit,closing,closed,cancellationObserved))};
        var lease=await f.Store.OpenAsync(new(f.Request,CreatureGeometryTestFixture.Manifest().ArtifactId),default);
        var read=lease.Content.ReadAsync(new byte[100]).AsTask();await entered.Task.WaitAsync(Guard);
        var dispose=lease.DisposeAsync().AsTask();Assert.False(lease.Retirement.IsCompleted);Assert.False(dispose.IsCompleted);
        await cancellationObserved.Task.WaitAsync(Guard);Assert.False(read.IsCompleted);Assert.False(lease.Retirement.IsCompleted);Assert.Equal(8L<<20,f.Store.Pressure.Payload);
        exit.SetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read.WaitAsync(Guard));await closing.Task.WaitAsync(Guard);
        Assert.Equal(8L<<20,f.Store.Pressure.Payload);closed.SetResult();await dispose.WaitAsync(Guard);await lease.Retirement;
        Assert.Equal(0,f.Store.Pressure.Payload);
    }
    [Fact]
    public async Task FailedLeaseCleanupFaultsRetirementAndKeepsPhysicalDebt()
    {
        await using var f=await Fixture.Create();f.Work.Retained=new Retained{Open=()=>Task.FromResult<Stream>(new FailingCloseStream())};
        var lease=await f.Store.OpenAsync(new(f.Request,CreatureGeometryTestFixture.Manifest().ArtifactId),default);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>lease.DisposeAsync().AsTask())).Code);
        await Assert.ThrowsAsync<DiscoveryException>(()=>lease.Retirement);Assert.Equal((0,1,8L<<20,true),f.Store.Pressure);f.ExpectDebt=true;
    }
    [Fact]
    public async Task OneProducerAndTwoReadLeasesFitExactlyShared64MiB()
    {
        await using var f=await Fixture.Create();var entered=f.Gate();var exit=f.Gate();
        f.Work.Produce=async(_,_,_,_)=>{entered.SetResult();await exit.Task;return new Publication();};
        var prepare=f.Store.PrepareAsync(new(f.Request),Progress,default);await entered.Task.WaitAsync(Guard);f.Work.Retained=new Retained();
        var a=await f.Store.OpenAsync(new(f.Request,f.Work.Retained.Manifest.ArtifactId),default);
        var b=await f.Store.OpenAsync(new(f.Request,f.Work.Retained.Manifest.ArtifactId),default);
        Assert.Equal(64L<<20,f.Store.Pressure.Payload);exit.SetResult();await prepare.WaitAsync(Guard);Assert.Equal(16L<<20,f.Store.Pressure.Payload);
        await a.DisposeAsync();await b.DisposeAsync();Assert.Equal(0,f.Store.Pressure.Payload);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal CreatureDisplaySourcesTests.Fixture Source=null!;internal CreatureDisplayMetadataStore Displays=null!;
        internal GeometryDisplayAuthorizationResult Proof=null!;internal Work Work=null!;internal CreatureGeometryStore Store=null!;internal bool ExpectDebt;
        private readonly List<TaskCompletionSource> gates=[];
        internal TaskCompletionSource Gate(){var result=Signal();gates.Add(result);return result;}
        internal CreatureDisplayMetadataRequest Request=>Source.Request;
        internal static async Task<Fixture>Create()
        {
            var f=new Fixture();f.Source=await CreatureDisplaySourcesTests.Fixture.Create();f.Displays=new(f.Source.Sources());
            f.Proof=await f.Displays.AuthorizeGeometryAsync(f.Request,default);f.Work=new(f.Proof);f.Store=new(f.Work,new(),new Cpu());return f;
        }
        public async ValueTask DisposeAsync()
        {foreach(var signal in gates)signal.TrySetResult();try{await Store.DisposeAsync();}catch(DiscoveryException)when(ExpectDebt){}await Displays.DisposeAsync();await Source.DisposeAsync();}
    }
    private sealed class Cpu:IGeometryArtifactWorkBudget
    {public ValueTask<IDisposable>EnterAsync(CancellationToken token){token.ThrowIfCancellationRequested();return ValueTask.FromResult<IDisposable>(new Scope());}private sealed class Scope:IDisposable{public void Dispose(){}}}
    private sealed class Work(GeometryDisplayAuthorizationResult proof):ICreatureGeometryStoreWork
    {
        internal int Produces,Ensures;internal Retained? Retained;
        internal Func<CancellationToken,Task<CreatureDisplayMetadataResult>>? Ensure;
        internal Func<CreatureDisplayMetadataRequest,CancellationToken,Task<GeometryDisplayAuthorizationResult>> Authorize=(_,_)=>Task.FromResult(proof);
        internal Func<IGeometrySelectionFence,IGeometryPayloadBudget,Func<CreatureGeometryProgress,Task>,CancellationToken,Task<IGeometryStorePublication>> Produce=(_,_,_,_)=>Task.FromResult<IGeometryStorePublication>(new Publication());
        public Task<GeometryDisplayAuthorizationResult>AuthorizeAsync(CreatureDisplayMetadataRequest s,CancellationToken t)=>Authorize(s,t);
        public Task<CreatureDisplayMetadataResult>EnsureMetadataAsync(CreatureDisplayMetadataRequest s,Func<CreatureGeometryProgress,Task>p,CancellationToken t){Ensures++;return Ensure?.Invoke(t)??throw new InvalidOperationException("Unexpected metadata prepare");}
        public Task<IGeometryStoreRetained?>FindAsync(GeometryDisplayAuthorization s,string? expected,CancellationToken t)=>Task.FromResult<IGeometryStoreRetained?>(Retained);
        public Task<IGeometryStorePublication>ProduceAsync(IGeometrySelectionFence s,IGeometryPayloadBudget b,Func<CreatureGeometryProgress,Task>p,CancellationToken t){Produces++;return Produce(s,b,p,t);}
    }
    private static IReadOnlyList<CreatureGeometrySource>Sources=>CreatureGeometryTestFixture.Result(ready:true).Sources.Select((s,i)=>s with{FileDataId=i==0?"777":"778"}).ToArray();
    private sealed class Publication:IGeometryStorePublication
    {
        internal int Closes;internal Func<Task>Dispose=()=>Task.CompletedTask;
        internal Func<GeometryDisplayAuthorization,CancellationToken,Task<GeometryStoreFinalized>>Finalize=(_,_)=>Task.FromResult(new GeometryStoreFinalized(null,CreatureGeometryStoreTests.Sources));
        public CreatureGeometryState State=>CreatureGeometryState.NoDeclaredTriangles;public CreatureGeometryManifest?Manifest=>null;
        public IReadOnlyList<CreatureGeometrySource>Sources=>CreatureGeometryStoreTests.Sources;public CreatureGeometryProblem?Problem=>null;
        public Task<GeometryStoreFinalized>FinalizeAsync(GeometryDisplayAuthorization s,CancellationToken t)=>Finalize(s,t);
        public async ValueTask DisposeAsync(){Closes++;await Dispose();}
    }
    private sealed class Retained:IGeometryStoreRetained
    {
        internal Func<Task<Stream>>Open=()=>Task.FromResult<Stream>(new MemoryStream(new byte[320],false));
        public CreatureGeometryManifest Manifest=>CreatureGeometryTestFixture.Manifest()with{ModelFileDataId=777,SkinFileDataId=778};
        public IReadOnlyList<CreatureGeometrySource>Sources=>CreatureGeometryStoreTests.Sources;
        public Task<Stream>OpenVerifiedAsync(CancellationToken t)=>Open();
        public Task FinalizeAsync(GeometryDisplayAuthorization s,CancellationToken t){t.ThrowIfCancellationRequested();return Task.CompletedTask;}
    }
    private class CloseStream(TaskCompletionSource closing,TaskCompletionSource release):MemoryStream(new byte[320],false)
    {public override async ValueTask DisposeAsync(){closing.TrySetResult();await release.Task;await base.DisposeAsync();}}
    private sealed class ReadStream(TaskCompletionSource entered,TaskCompletionSource exit,TaskCompletionSource closing,TaskCompletionSource release,TaskCompletionSource cancellationObserved):CloseStream(closing,release)
    {
        public override async ValueTask<int>ReadAsync(Memory<byte>b,CancellationToken token=default)
        {
            using var registration=token.Register(()=>cancellationObserved.TrySetResult());
            entered.TrySetResult();await exit.Task;token.ThrowIfCancellationRequested();return 0;
        }
    }
    private sealed class FailingCloseStream:MemoryStream
    {public override ValueTask DisposeAsync()=>ValueTask.FromException(new IOException("authored cleanup failure"));}
}
