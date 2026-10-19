using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed partial class ItemEffectsBridgeArtifactTests
{
    [Fact]
    public async Task CursorPagingAndByteLimitedContinuationNeverSkipOrConfuseSourceAndIndexOrdinals()
    {
        await using var h=await Fixture.Create(Enumerable.Range(0,22).Select(i=>Row((uint)i,i<2?9:7,i)).ToArray());await h.Build();
        ItemEffectsPage two;await using(var session=await h.Open())two=await session.ReadPageAsync(h.Request(7,2),h.Binding.Source,default);
        var frame=JsonSerializer.SerializeToUtf8Bytes(new{protocolVersion=1,id=new string('\u0800',128),result=two},ItemEffectsBridgePolicy.Json).Length+1;
        var engine=new ItemEffectsBridgeArtifacts((action,_)=>{action();return Task.CompletedTask;},frame-1);
        await using var reader=await engine.OpenAsync(h.Artifact,h.Input(),default);var actual=new List<string>();string? after=null;var pages=0;
        do
        {
            var page=await reader.ReadPageAsync(h.Request(7,16,after),h.Binding.Source,default);Assert.NotEmpty(page.Edges);Assert.True(page.Edges.Count<16);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(new{protocolVersion=1,id=new string('\u0800',128),result=page},new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length+1<frame);
            actual.AddRange(page.Edges.Select(e=>e.Bridge.SourceOrdinal!));after=page.NextCursor;pages++;
            Assert.InRange(reader.LastTupleReads,1,reader.LastLowerProbes+reader.LastUpperProbes+page.Edges.Count+1);
            Assert.True(pages<=20);
        }while(after is not null);
        Assert.Equal(Enumerable.Range(2,20).Select(i=>i.ToString()),actual);Assert.True(pages>1);
        var request=h.Request(9,1);var other=await reader.ReadPageAsync(request,h.Binding.Source,default);
        Assert.Equal("20",other.Edges[0].Key.IndexOrdinal);Assert.Equal("0",other.Edges[0].Bridge.SourceOrdinal);
    }
    [Theory]
    [InlineData("limit")] [InlineData("items")] [InlineData("context")] [InlineData("artifact")] [InlineData("parent")]
    [InlineData("padding")] [InlineData("whitespace")] [InlineData("start")] [InlineData("end")] [InlineData("leadingZero")]
    public async Task CursorIsBoundToExactRequestAndCanonicalInteriorIndexOrdinal(string kind)
    {
        await using var h=await Fixture.Create([Row(0,3,1),Row(1,7,1),Row(2,7,2),Row(3,7,3),Row(4,9,1)]);await h.Build();await using var session=await h.Open();
        var first=await session.ReadPageAsync(h.Request(7,1),h.Binding.Source,default);var cursor=first.NextCursor!;var request=h.Request(7,1,cursor);
        request=kind switch
        {
            "limit"=>request with{Limit=2},"items"=>request with{Selection=request.Selection with{ItemsArtifactId=new('2',64)}},
            "context"=>request with{Selection=request.Selection with{Context=request.Selection.Context with{SourceRegion="eu"}}},
            "artifact"=>request with{BridgeIndexArtifactId=new('3',64)},"parent"=>request with{Selection=request.Selection with{ItemId="9"}},
            "padding"=>request with{After=cursor+"="},_=>request
        };
        if(kind is "whitespace" or "start" or "end" or "leadingZero")
        {
            var raw=Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-','+').Replace('_','/')+new string('=',(4-cursor.Length%4)%4)));
            raw=kind switch{"whitespace"=>" "+raw,"start"=>raw.Replace("\"after\":\"2\"","\"after\":\"1\""),"end"=>raw.Replace("\"after\":\"2\"","\"after\":\"4\""),_=>raw.Replace("\"after\":\"2\"","\"after\":\"02\"")};
            request=request with{After=Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+','-').Replace('/','_')};
        }
        Assert.Equal("ITEM_EFFECTS_CURSOR_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>session.ReadPageAsync(request,h.Binding.Source,default))).Code);
    }
    [Fact]
    public async Task CurrentSourceRefreshRetainsOriginalIndexButChangedReceiptCannotProduceAPage()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await h.Build();await using var reader=await h.Open();
        var current=h.Binding.Source with{PreparedAt="new-admission",Source=h.Binding.Source.Source with{VerifiedAt="new-verification"}};
        var page=await reader.ReadPageAsync(h.Request(0),current,default);Assert.Equal("new-admission",page.BridgeSource.PreparedAt);Assert.Equal(h.Artifact.ContentKey,page.BridgeIndexArtifactId);
        current=current with{Source=current.Source with{RetrievedAt="changed-original"}};
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>reader.ReadPageAsync(h.Request(0),current,default))).Code);
    }
    [Fact]
    public async Task SingleEdgeTooLargeIsTypedRatherThanEmptyOrLooping()
    {
        await using var h=await Fixture.Create([Row(1,0,0)],100);await h.Build();await using var reader=await h.Open();
        Assert.Equal("ITEM_EFFECTS_RESULT_LIMIT",(await Assert.ThrowsAsync<DiscoveryException>(()=>reader.ReadPageAsync(h.Request(0),h.Binding.Source,default))).Code);
    }
    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("swallowed")]
    public async Task CpuExecutorMustInvokeExactlyOnceAndPropagateOwnedActionFailure(string kind)
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await using var stage=h.Stage();
        var engine=new ItemEffectsBridgeArtifacts((action,_)=>
        {
            if(kind=="missing")return Task.CompletedTask;
            if(kind=="duplicate"){action();action();return Task.CompletedTask;}
            try{action();}catch(DiscoveryException){}return Task.CompletedTask;
        });
        var feed=kind=="swallowed"?new Func<long,int,CancellationToken,Task<TableArtifactPage>>((_,_,_)=>Task.FromResult(new TableArtifactPage([Row(1,0,0) with{Values=["1","+1","0"]}],1,null))):h.Feed;
        Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>engine.BuildAsync(h.Binding,stage,feed,default))).Code);
        Assert.True(stage.CanWrite);Assert.Equal(0,stage.Length);
    }
    [Fact]
    public async Task FeedWaitDoesNotHoldCpuAndCanceledBuildLeavesItsCallerStageOwned()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await using var stage=h.Stage();using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.FeedHook=async (_,_,token)=>{Assert.False(h.InCpu);entered.SetResult();await release.Task.WaitAsync(token);};
        var build=h.Engine.BuildAsync(h.Binding,stage,h.Feed,cancel.Token);await entered.Task;Assert.False(h.InCpu);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>build);Assert.False(h.InCpu);Assert.True(stage.CanWrite);Assert.Equal(0,stage.Length);
    }
    [Fact]
    public async Task CancellationDuringMergeRetiresCpuBeforeCallerStageCleanup()
    {
        await using var h=await Fixture.Create(Enumerable.Range(0,8192).Select(i=>Row((uint)i,8192-i,i)).ToArray());await using var stage=h.Stage();using var cancel=new CancellationTokenSource();
        var sourceFinished=false;var slices=0;var inCpu=false;
        var engine=new ItemEffectsBridgeArtifacts(async (action,token)=>{inCpu=true;try{action();if(sourceFinished&&++slices==3)cancel.Cancel();}finally{inCpu=false;}await Task.Yield();});
        async Task<TableArtifactPage> Feed(long after,int limit,CancellationToken token){var page=await h.Feed(after,limit,token);sourceFinished=page.NextCursor is null;return page;}
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>engine.BuildAsync(h.Binding,stage,Feed,cancel.Token));Assert.Equal(3,slices);Assert.False(inCpu);Assert.Equal(0,stage.Length);Assert.True(stage.CanWrite);
    }
    [Theory]
    [InlineData("canceled")] [InlineData("descriptor")] [InlineData("position")]
    public async Task OpenConsumesItsInputOnEveryUnsuccessfulEntry(string kind)
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await h.Build();var input=h.Input();using var cancel=new CancellationTokenSource();
        var artifact=h.Artifact;if(kind=="canceled")cancel.Cancel();if(kind=="descriptor")artifact=artifact with{Sha256="bad"};if(kind=="position")input.Position=1;
        await Assert.ThrowsAnyAsync<Exception>(()=>h.Engine.OpenAsync(artifact,input,cancel.Token));Assert.True(input.SafeFileHandle.IsClosed);
    }
    [Fact]
    public async Task FailedOpenCloseReportsDebtAndGatedCloseDoesNotPretendToRetire()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await h.Build();var input=new ControlledFile(h.Path){Fail=true};
        var invalid=h.Artifact with{Sha256="bad"};Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>h.Engine.OpenAsync(invalid,input,default))).Code);
        Assert.False(input.CapturedHandle.IsClosed);input.Fail=false;await input.DisposeAsync();
        input=new(h.Path){Hold=true};var open=h.Engine.OpenAsync(invalid,input,default);await input.Entered.Task;Assert.False(open.IsCompleted);Assert.False(input.CapturedHandle.IsClosed);
        input.Release.SetResult();Assert.Equal("ITEM_EFFECTS_INDEX_INVALID",(await Assert.ThrowsAsync<DiscoveryException>(()=>open)).Code);Assert.True(input.CapturedHandle.IsClosed);
    }
    [Fact]
    public async Task SessionShutdownWaitsForAcceptedReadAndRetainsFailedPhysicalCloseDebt()
    {
        await using var h=await Fixture.Create([Row(1,0,0)]);await h.Build();var hold=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine=new ItemEffectsBridgeArtifacts(async (action,token)=>{if(hold){entered.TrySetResult();await release.Task.WaitAsync(token);}action();});
        var input=new ControlledFile(h.Path);var reader=await engine.OpenAsync(h.Artifact,input,default);hold=true;
        var read=reader.ReadTupleAsync(0,0,default);await entered.Task;var close=reader.DisposeAsync().AsTask();Assert.False(close.IsCompleted);Assert.False(input.CapturedHandle.IsClosed);
        release.SetResult();await read;await close;Assert.True(input.CapturedHandle.IsClosed);await Assert.ThrowsAsync<ObjectDisposedException>(()=>reader.FindRangeAsync(0,default));
        input=new(h.Path){Fail=true};reader=await h.Engine.OpenAsync(h.Artifact,input,default);
        Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>reader.DisposeAsync().AsTask())).Code);Assert.False(input.CapturedHandle.IsClosed);
        input.Fail=false;await input.DisposeAsync();
    }
    [Fact]
    public async Task EveryStageWriteIsOutsideCpuAtMost64KiBAndCancellationLeavesCallerCleanup()
    {
        await using var h=await Fixture.Create(Enumerable.Range(0,5000).Select(i=>Row((uint)i,7,i)).ToArray());using var cancel=new CancellationTokenSource();
        var inCpu=false;var engine=new ItemEffectsBridgeArtifacts((action,_)=>{inCpu=true;try{action();}finally{inCpu=false;}return Task.CompletedTask;});
        await using var stage=new ObservedStage(h.Path,(bytes,calls)=>{Assert.False(inCpu);Assert.InRange(bytes,1,65536);if(calls==3)cancel.Cancel();});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>engine.BuildAsync(h.Binding,stage,h.Feed,cancel.Token));
        Assert.Equal(3,stage.Writes);Assert.InRange(stage.Length,65,131072);Assert.True(stage.CanWrite);Assert.False(inCpu);
    }
    [Fact]
    public async Task CanceledPhysicalOpenScanClosesConsumedInput()
    {
        await using var h=await Fixture.Create(Enumerable.Range(0,5000).Select(i=>Row((uint)i,7,i)).ToArray());await h.Build();using var cancel=new CancellationTokenSource();
        var callbacks=0;var engine=new ItemEffectsBridgeArtifacts((action,_)=>{action();if(++callbacks==5)cancel.Cancel();return Task.CompletedTask;});var input=h.Input();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>engine.OpenAsync(h.Artifact,input,cancel.Token));Assert.Equal(5,callbacks);Assert.True(input.SafeFileHandle.IsClosed);
    }
    private sealed class ObservedStage(string path,Action<int,int> onWrite):FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.Asynchronous)
    {
        internal int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){onWrite(buffer.Length,++Writes);return base.WriteAsync(buffer,token);}
    }
    private sealed class ControlledFile:FileStream
    {
        internal readonly Microsoft.Win32.SafeHandles.SafeFileHandle CapturedHandle;
        internal ControlledFile(string path):base(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.RandomAccess){CapturedHandle=SafeFileHandle;}
        internal bool Fail,Hold;internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DisposeAsync(){Entered.TrySetResult();if(Hold)await Release.Task;if(Fail)throw new IOException("authored close failure");await base.DisposeAsync();}
    }
}
