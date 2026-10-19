using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemEffectsHostTests
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)}};
    private static readonly string[] Methods=["items.effects.data","items.effects.prepare","items.effects.page","items.effects.details","items.effects.details.prepare","items.effects.names","items.effects.names.prepare"];
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static T Fixture<T>(string name)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"contracts/discovery.schema.json")))directory=directory.Parent;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory!.FullName,"contracts/examples",name+".json")),Json)!;
    }
    private static ItemEffectsReadiness Readiness()
    {var value=Fixture<ItemEffectsReadiness>("item-effects-readiness");return value with{Source=value.Source! with{Snapshot=value.Snapshot!.BridgeSource}};}
    private static Dictionary<string,object?> Scope(string method)
    {
        Dictionary<string,object?> value=new(){["productId"]="wow",["buildIdentity"]=new string('a',64),["sourceRegion"]="us",["researchLocale"]="enUS",["itemsArtifactId"]=new string('b',64),["itemId"]="0"};
        if(method is not("items.effects.data" or "items.effects.prepare"))value["bridgeIndexArtifactId"]=new string('c',64);
        if(method.Contains("details",StringComparison.Ordinal)||method.Contains("names",StringComparison.Ordinal))value["edges"]=new[]{new{indexOrdinal="0"},new{indexOrdinal="1"}};
        if(method.Contains("names",StringComparison.Ordinal))value["itemEffectArtifactId"]=new string('d',64);
        return value;
    }
    private static object Request(string id,string method,object parameters)=>new{protocolVersion=1,id,method,@params=parameters};

    [Fact]
    public async Task MissingServiceAdvertisesNoEffectsAndAllSevenRoutesRefuseWithoutSimulatedData()
    {
        await using var session=await Session.Start(null);await session.Send("hello","hello",new{});
        Assert.DoesNotContain((await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray(),v=>v.GetString()!.Contains("effects",StringComparison.Ordinal)||v.GetString()=="item-effect-spell-names-v1");
        for(var i=0;i<Methods.Length;i++) {await session.Send($"r{i}",Methods[i],Scope(Methods[i]));Assert.Equal("ITEM_EFFECTS_UNAVAILABLE",(await session.Receive()).GetProperty("error").GetProperty("code").GetString());}
    }
    [Fact]
    public async Task InjectedServiceAdvertisesThreePhasesTogether()
    {
        await using var service=new ItemEffectsService(new Store());await using var session=await Session.Start(service);await session.Send("hello","hello",new{});
        var caps=(await session.Receive()).GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(v=>v.GetString()).ToArray();
        foreach(var cap in new[]{"item-effects-links-v1","item-effects-inputs-v1","item-effect-spell-names-v1"})Assert.Contains(cap,caps);
    }
    [Theory]
    [InlineData("items.effects.data")][InlineData("items.effects.prepare")][InlineData("items.effects.page")]
    [InlineData("items.effects.details")][InlineData("items.effects.details.prepare")][InlineData("items.effects.names")][InlineData("items.effects.names.prepare")]
    public async Task InjectedActualAppServicePreservesUsefulOriginalSourceAndOrderedEvidence(string method)
    {
        var store=new Store();await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);
        await session.Send("work",method,Scope(method));var frame=await session.Receive();Assert.True(frame.TryGetProperty("result",out var result),frame.GetRawText());
        Assert.Equal("0",result.GetProperty("selection").GetProperty("itemId").GetString());Assert.Equal(new string('b',64),result.GetProperty("selection").GetProperty("itemsArtifactId").GetString());
        if(method.Contains("names",StringComparison.Ordinal)){Assert.Equal(2,result.GetProperty("sources").GetArrayLength());Assert.Equal("0",result.GetProperty("outcomes")[0].GetProperty("effect").GetProperty("spellId").GetProperty("value").GetString());}
        else if(method.Contains("details",StringComparison.Ordinal)){var fields=result.GetProperty("outcomes")[0].GetProperty("fields");Assert.Equal(9,fields.GetArrayLength());Assert.Equal("4294967295",fields[2].GetProperty("value").GetString());Assert.Equal("rawPhysicalInteger",fields[2].GetProperty("evidence").GetString());Assert.Equal("unverifiedCandidate",fields[8].GetProperty("evidence").GetString());}
        else if(method.EndsWith("page",StringComparison.Ordinal)){Assert.Equal("1",result.GetProperty("edges")[0].GetProperty("bridge").GetProperty("sourceOrdinal").GetString());Assert.True(result.GetProperty("edges")[1].GetProperty("bridge").GetProperty("isCopy").GetBoolean());}
        else Assert.Equal("ready",result.GetProperty("state").GetString());
        Assert.Equal(1,store.Calls.Sum());Assert.True(Encoding.UTF8.GetByteCount(frame.GetRawText())+1<=ProtocolHost.MaxItemEffectsFrameBytes);
    }
    [Theory]
    [InlineData("itemId","0")][InlineData("itemId","\"01\"")][InlineData("itemId","\"4294967296\"")]
    [InlineData("itemsArtifactId","\"../item\"")][InlineData("researchLocale","\"us\"")]
    [InlineData("edges","[]")][InlineData("edges","[{\"indexOrdinal\":\"0\"},{\"indexOrdinal\":\"0\"}]")]
    [InlineData("edges","[{\"indexOrdinal\":0}]")][InlineData("edges","[{\"indexOrdinal\":\"65536\"}]")]
    [InlineData("policyRevision","null")][InlineData("policyRevision","\"unknown\"")]
    [InlineData("spellId","\"0\"")][InlineData("url","\"https://example.invalid/file\"")]
    public async Task MalformedAuthorityPolicyAndEdgeKeysFailBeforeStoreWork(string field,string raw)
    {
        var store=new Store();await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);
        var value=JsonSerializer.SerializeToNode(Scope("items.effects.names.prepare"),Json)!;value[field]=System.Text.Json.Nodes.JsonNode.Parse(raw);
        await session.Send("bad","items.effects.names.prepare",value);Assert.Equal("INVALID_REQUEST",(await session.Receive()).GetProperty("error").GetProperty("code").GetString());Assert.Equal(0,store.Calls.Sum());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FourCompletedResultsAndTwoLongLeasesRemainChargedUntilActualFlushIncludingShutdown(bool shutdown)
    {
        var context=new QueuedContext();var previous=SynchronizationContext.Current;var visited=0;var store=new Store();
        store.NamesResult=request=>Names(request) with{Outcomes=new Counted<ItemEffectNameOutcome>(Names(request).Outcomes,()=>visited++)};
        var methods=new[]{"items.effects.prepare","items.effects.details.prepare","items.effects.names","items.effects.page","items.effects.names.prepare","items.effects.data"};
        var bytes=Encoding.UTF8.GetBytes(string.Join('\n',methods.Select((m,i)=>JsonSerializer.Serialize(Request($"r{i}",m,Scope(m)),Json)))+"\n");
        using var input=new HeldInput(bytes);using var output=new HeldFlush();using var stop=new CancellationTokenSource();
        SynchronizationContext.SetSynchronizationContext(context);var service=new ItemEffectsService(store);Task? running=null;
        try
        {
            running=new ProtocolHost(new DiscoveryService(new Unused(),new Unused()),itemEffects:service).RunAsync(input,output,stop.Token);
            SynchronizationContext.SetSynchronizationContext(previous);await context.DrainUntilAsync(()=>input.Waiting.Task.IsCompleted&&output.Started.Task.IsCompleted);context.Drain();
            Assert.Equal(4,store.Calls.Sum());Assert.Equal(0,store.Calls[(int)ItemEffectsOperation.PrepareNames]);Assert.Equal(0,visited);Assert.False(running.IsCompleted);
            if(shutdown)stop.Cancel();output.Release.TrySetResult();input.Release.TrySetResult();await context.DrainUntilAsync(()=>running.IsCompleted);await running;
        }
        finally
        {
            output.Release.TrySetResult();input.Release.TrySetResult();SynchronizationContext.SetSynchronizationContext(previous);await context.DrainUntilAsync(()=>running?.IsCompleted??true);
            var closing=service.DisposeAsync().AsTask();await context.DrainUntilAsync(()=>closing.IsCompleted);await closing;
        }
        var frames=Frames(output.ToArray());Assert.Equal(6,frames.Length);Assert.Equal(2,visited);
        for(var i=0;i<4;i++)Assert.True(Reply(frames,$"r{i}").TryGetProperty("result",out _));
        for(var i=4;i<6;i++)Assert.Equal("ITEM_EFFECTS_BUSY",Reply(frames,$"r{i}").GetProperty("error").GetProperty("code").GetString());
    }
    [Fact]
    public async Task ActualCleanupDebtIsObservedWithOneTerminalAndPublisherRetirementStillLetsPeersDrain()
    {
        var previous=SynchronizationContext.Current;var context=new QueuedContext();var store=new Store{Before=(_,_)=>throw new DiscoveryException("ITEM_EFFECTS_CLEANUP_FAILED","Authored cleanup debt.")};
        var requests=new[]{Request("debt","items.effects.data",Scope("items.effects.data")),Request("core","products.list",new{})};
        using var input=new HeldInput(Encoding.UTF8.GetBytes(string.Join('\n',requests.Select(r=>JsonSerializer.Serialize(r,Json)))+"\n"));using var output=new MemoryStream();
        SynchronizationContext.SetSynchronizationContext(context);var service=new ItemEffectsService(store);Task? running=null;
        try
        {
            running=new ProtocolHost(new DiscoveryService(new Unused(),new Unused()),itemEffects:service).RunAsync(input,output);
            SynchronizationContext.SetSynchronizationContext(previous);await context.DrainUntilAsync(()=>input.Waiting.Task.IsCompleted);context.Drain();
            var leases=Enumerable.Range(0,3).Select(_=>service.Admit(ItemEffectsOperation.Read)).ToArray();
            Assert.Equal("ITEM_EFFECTS_BUSY",Assert.Throws<DiscoveryException>(()=>service.Admit(ItemEffectsOperation.Read)).Code);
            foreach(var lease in leases){var closing=lease.DisposeAsync().AsTask();await context.DrainUntilAsync(()=>closing.IsCompleted);await closing;}
            input.Release.TrySetResult();await context.DrainUntilAsync(()=>running.IsCompleted);
            Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED",(await Assert.ThrowsAsync<DiscoveryException>(()=>running)).Code);
        }
        finally{input.Release.TrySetResult();SynchronizationContext.SetSynchronizationContext(previous);var closing=service.DisposeAsync().AsTask();await context.DrainUntilAsync(()=>closing.IsCompleted);await Assert.ThrowsAsync<DiscoveryException>(()=>closing);}
        var frames=Frames(output.ToArray());Assert.Equal(2,frames.Length);Assert.Equal("ITEM_EFFECTS_CLEANUP_FAILED",Reply(frames,"debt").GetProperty("error").GetProperty("code").GetString());Assert.True(Reply(frames,"core").TryGetProperty("result",out _));
    }
    [Theory][InlineData("items.effects.data")][InlineData("items.effects.page")][InlineData("items.effects.details")][InlineData("items.effects.names")]
    public async Task CompleteEscapedErrorEnvelopeAcceptsExactly512KiBAndOneMoreByteGetsWholeFallback(string method)
    {
        var text="a";var store=new Store{Before=(_,_)=>throw new DiscoveryException("AUTHORED_ERROR",text)};await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);
        await session.Send(new string('<',128),method,Scope(method));var sample=await session.Receive();
        var remaining=ProtocolHost.MaxItemEffectsFrameBytes-Encoding.UTF8.GetByteCount(sample.GetRawText())-1;
        text+=new string('<',remaining/6)+new string('a',remaining%6);
        await session.Send(new string('>',128),method,Scope(method));var exact=await session.Receive();
        Assert.Equal(ProtocolHost.MaxItemEffectsFrameBytes,Encoding.UTF8.GetByteCount(exact.GetRawText())+1);Assert.Equal(text,exact.GetProperty("error").GetProperty("message").GetString());
        text+="a";await session.Send(new string('&',128),method,Scope(method));var refused=await session.Receive();Assert.Equal("ITEM_EFFECTS_RESULT_LIMIT",refused.GetProperty("error").GetProperty("code").GetString());
    }
    [Theory]
    [InlineData("items.effects.prepare","items.effects.prepare")]
    [InlineData("items.effects.prepare","items.effects.details.prepare")]
    [InlineData("items.effects.prepare","items.effects.names.prepare")]
    [InlineData("items.effects.details.prepare","items.effects.details.prepare")]
    [InlineData("items.effects.details.prepare","items.effects.names.prepare")]
    [InlineData("items.effects.names.prepare","items.effects.names.prepare")]
    public async Task MixedLongOperationsKeepCoreUsefulAndCancelOneWithoutRetiringItsPeerOrEarlyCleanupCapacity(string firstMethod,string secondMethod)
    {
        var first=Signal();var second=Signal();var canceled=Signal();var cleanup=Signal();var survivor=Signal();var ordinal=0;
        var store=new Store{Before=async(operation,token)=>
        {
            if(operation is not(ItemEffectsOperation.PrepareLinks or ItemEffectsOperation.PrepareDetails or ItemEffectsOperation.PrepareNames))return;
            if(Interlocked.Increment(ref ordinal)==1)
            {first.TrySetResult();try{await Task.Delay(Timeout.InfiniteTimeSpan,token);}catch(OperationCanceledException){canceled.TrySetResult();await cleanup.Task;throw;}}
            else {second.TrySetResult();await survivor.Task.WaitAsync(token);}
        }};
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);
        try
        {
            await session.Send("first",firstMethod,Scope(firstMethod));await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("second",secondMethod,Scope(secondMethod));await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("third","items.effects.names.prepare",Scope("items.effects.names.prepare"));Assert.Equal("ITEM_EFFECTS_BUSY",(await session.Receive()).GetProperty("error").GetProperty("code").GetString());
            await session.Send("core","products.list",new{});Assert.True((await session.Receive()).TryGetProperty("result",out _));
            await session.Send("page","items.effects.page",Scope("items.effects.page"));Assert.True((await session.Receive()).TryGetProperty("result",out _));
            await session.Send("cancel","request.cancel",new{requestId="first"});Assert.True((await session.Receive()).GetProperty("result").GetProperty("requested").GetBoolean());await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.Send("retiring","items.effects.names.prepare",Scope("items.effects.names.prepare"));Assert.Equal("ITEM_EFFECTS_BUSY",(await session.Receive()).GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(2,ordinal);Assert.False(survivor.Task.IsCompleted);
            cleanup.TrySetResult();var terminal=await session.Receive();Assert.Equal("first",terminal.GetProperty("id").GetString());Assert.Equal("CANCELED",terminal.GetProperty("error").GetProperty("code").GetString());
            survivor.TrySetResult();var healthy=await session.Receive();Assert.Equal("second",healthy.GetProperty("id").GetString());Assert.True(healthy.TryGetProperty("result",out _));
        }
        finally{cleanup.TrySetResult();survivor.TrySetResult();}
    }
    [Fact]
    public async Task ActualPrepareProgressPreservesCurrentRoleAndSafeMeasuredCountersWithoutInventingTotal()
    {
        var producer=Signal();var store=new Store{Report=async report=>{await report(new("Reading observed rows","rows",1,null,"00cb674f"));await producer.Task;}};
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);
        try
        {
            await session.Send("progress","items.effects.prepare",Scope("items.effects.prepare"));var frame=await session.Receive();Assert.Equal("progress",frame.GetProperty("type").GetString());Assert.Equal("00cb674f",frame.GetProperty("tableId").GetString());Assert.Equal(1,frame.GetProperty("completed").GetInt64());Assert.Equal(JsonValueKind.Null,frame.GetProperty("total").ValueKind);Assert.Equal("rows",frame.GetProperty("unit").GetString());
            producer.TrySetResult();Assert.True((await session.Receive()).TryGetProperty("result",out _));
        }
        finally{producer.TrySetResult();}
    }
    [Theory][InlineData("items.effects.page")][InlineData("items.effects.details")][InlineData("items.effects.names")]
    public async Task AdversarialOversizeSourceGraphGetsOneWholeLimitFrameBeforeLazyOutcomeEnumeration(string method)
    {
        // Controlled malformed adapter evidence: App's finite source relations admit this
        // metadata scalar, but transport must bound it before any partial stdout or outcomes.
        var visited=0;var huge=new string('<',600_000);
        var store=new Store
        {
            PageResult=_=>{var v=Fixture<ItemEffectsPage>("item-effects-page");return v with{BridgeSource=Inflate(v.BridgeSource,huge)};},
            DetailsResult=request=>{var v=Details(request);return v with{Source=v.Source with{Snapshot=Inflate(v.Source.Snapshot!,huge)},Outcomes=new Counted<ItemEffectOutcome>(v.Outcomes,()=>visited++)};},
            NamesResult=request=>{var v=Names(request);return v with{Sources=v.Sources.Select((source,index)=>index==0?source with{Snapshot=Inflate(source.Snapshot!,huge)}:source).ToArray(),Outcomes=new Counted<ItemEffectNameOutcome>(v.Outcomes,()=>visited++)};}
        };
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);await session.Send("large",method,Scope(method));
        var frame=await session.Receive();Assert.Equal("ITEM_EFFECTS_RESULT_LIMIT",frame.GetProperty("error").GetProperty("code").GetString());Assert.Equal(0,visited);
        await session.Send("core","products.list",new{});Assert.Equal("core",(await session.Receive()).GetProperty("id").GetString());Assert.Equal(1,store.Calls.Sum());
    }
    private static TableDataSnapshot Inflate(TableDataSnapshot value,string text)=>value with{Association=value.Association with{RootSource=value.Association.RootSource with{SourceUrl=text}}};
    [Fact]
    public async Task MaximumEightEscapedNamesKeepAllEvidenceAndUTF16UnitsInsideWholeBoundedFrame()
    {
        var store=new Store{NamesResult=request=>{var v=Fixture<ItemEffectNamesResult>("item-effect-names");return v with{Outcomes=request.Edges.Select(key=>v.Outcomes[0] with{Key=key,Text=new string('<',4096)}).ToArray()};}};
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);var parameters=Scope("items.effects.names");parameters["edges"]=Enumerable.Range(0,8).Select(i=>new{indexOrdinal=i.ToString(System.Globalization.CultureInfo.InvariantCulture)}).ToArray();await session.Send(new string('<',128),"items.effects.names",parameters);
        var frame=await session.Receive();var outcomes=frame.GetProperty("result").GetProperty("outcomes");Assert.Equal(8,outcomes.GetArrayLength());for(var i=0;i<8;i++){Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture),outcomes[i].GetProperty("key").GetProperty("indexOrdinal").GetString());Assert.Equal(new string('<',4096),outcomes[i].GetProperty("text").GetString());}Assert.True(Encoding.UTF8.GetByteCount(frame.GetRawText())+1<=ProtocolHost.MaxItemEffectsFrameBytes);
    }
    [Fact]
    public async Task CanonicalNodeAuthoredCursorAgreesWithActualAppRequestDigestAndRetainsOccurrenceOrder()
    {
        const string cursor="eyJiaW5kaW5nIjoiNGZiZmMyNDdmN2JiODVmY2NkNGViOTcyZGFhY2E3YmRkMzlhOTg1ZDQ2YTBhM2VhYWMyMTgzOWM4Y2FlN2Q2MCIsImFmdGVyIjoiMSJ9";
        var store=new Store{PageResult=request=>{var page=Fixture<ItemEffectsPage>("item-effects-page");return page with{Edges=[page.Edges[request.After is null?0:1]],NextCursor=request.After is null?cursor:null};}};
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);var p=Scope("items.effects.page");p["limit"]=1;
        await session.Send("page1","items.effects.page",p);var first=(await session.Receive()).GetProperty("result");Assert.Equal(cursor,first.GetProperty("nextCursor").GetString());Assert.Equal("0",first.GetProperty("edges")[0].GetProperty("key").GetProperty("indexOrdinal").GetString());
        p["after"]=cursor;await session.Send("page2","items.effects.page",p);var second=(await session.Receive()).GetProperty("result");Assert.Equal("1",second.GetProperty("edges")[0].GetProperty("key").GetProperty("indexOrdinal").GetString());Assert.Equal("2",second.GetProperty("edges")[0].GetProperty("bridge").GetProperty("sourceOrdinal").GetString());Assert.Equal(JsonValueKind.Null,second.GetProperty("nextCursor").ValueKind);
        p["limit"]=2;await session.Send("foreign","items.effects.page",p);Assert.Equal("ITEM_EFFECTS_CURSOR_INVALID",(await session.Receive()).GetProperty("error").GetProperty("code").GetString());Assert.Equal(2,store.Calls.Sum());
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task InvalidTextLocatorRequiresOriginalNameSnapshotToDeclareAnEncodingIssue(bool declared)
    {
        var store=new Store{NamesResult=request=>{var value=Names(request);return value with
        {Sources=[value.Sources[0],value.Sources[1] with{Snapshot=value.Sources[1].Snapshot! with{TextIssueCount=declared?"1":"0"}}],
         Outcomes=value.Outcomes.Select((outcome,index)=>index==0?outcome with{State=ItemEffectNameState.InvalidText,Text=null,Issue=new("DB2_TEXT_ENCODING_INVALID",outcome.Target! with{ColumnIndex=1})}:outcome).ToArray()};}};
        await using var service=new ItemEffectsService(store);await using var session=await Session.Start(service);await session.Send("invalid-text","items.effects.names",Scope("items.effects.names"));var frame=await session.Receive();
        if(declared){var outcome=frame.GetProperty("result").GetProperty("outcomes")[0];Assert.Equal("invalidText",outcome.GetProperty("state").GetString());Assert.Equal(1,outcome.GetProperty("issue").GetProperty("source").GetProperty("columnIndex").GetInt32());}
        else Assert.Equal("ITEM_EFFECTS_RESULT_INVALID",frame.GetProperty("error").GetProperty("code").GetString());
    }
    private static ItemEffectDetailsResult Details(ItemEffectDetailsRequest request)
    {var template=Fixture<ItemEffectDetailsResult>("item-effect-details");return template with{Outcomes=request.Edges.Select(key=>template.Outcomes.Single(v=>v.Key==key)).ToArray()};}
    private static ItemEffectNamesResult Names(ItemEffectNamesRequest request)
    {var template=Fixture<ItemEffectNamesResult>("item-effect-names");return template with{Outcomes=request.Edges.Select(key=>template.Outcomes.Single(v=>v.Key==key)).ToArray()};}
    private sealed class Store:IItemEffectsStore
    {
        internal readonly int[] Calls=new int[7];internal Func<ItemEffectsOperation,CancellationToken,Task>? Before;
        internal Func<ItemEffectNamesRequest,ItemEffectNamesResult> NamesResult=Names;
        internal Func<ItemEffectDetailsRequest,ItemEffectDetailsResult> DetailsResult=Details;
        internal Func<ItemEffectsPageRequest,ItemEffectsPage> PageResult=_=>Fixture<ItemEffectsPage>("item-effects-page");
        internal Func<Func<ItemEffectsProgress,Task>,Task>? Report;
        private async Task<T> Prepare<T>(ItemEffectsOperation operation,Func<ItemEffectsProgress,Task> report,CancellationToken token,Func<T> result){if(Report is not null)await Report(report);return await Work(operation,token,result);}
        private async Task<T> Work<T>(ItemEffectsOperation operation,CancellationToken token,Func<T> result){Calls[(int)operation]++;if(Before is not null)await Before(operation,token);return result();}
        public Task<ItemEffectsReadiness> ReadAsync(ItemEffectsSelection selection,CancellationToken token)=>Work(ItemEffectsOperation.Read,token,Readiness);
        public Task<ItemEffectsReadiness> PrepareLinksAsync(ItemEffectsSelection selection,Func<ItemEffectsProgress,Task> progress,CancellationToken token)=>Prepare(ItemEffectsOperation.PrepareLinks,progress,token,Readiness);
        public Task<ItemEffectsPage> ReadPageAsync(ItemEffectsPageRequest request,CancellationToken token)=>Work(ItemEffectsOperation.ReadPage,token,()=>PageResult(request));
        public Task<ItemEffectDetailsResult> ReadDetailsAsync(ItemEffectDetailsRequest request,CancellationToken token)=>Work(ItemEffectsOperation.ReadDetails,token,()=>DetailsResult(request));
        public Task<ItemEffectDetailsResult> PrepareDetailsAsync(ItemEffectDetailsRequest request,Func<ItemEffectsProgress,Task> progress,CancellationToken token)=>Prepare(ItemEffectsOperation.PrepareDetails,progress,token,()=>DetailsResult(request));
        public Task<ItemEffectNamesResult> ReadNamesAsync(ItemEffectNamesRequest request,CancellationToken token)=>Work(ItemEffectsOperation.ReadNames,token,()=>NamesResult(request));
        public Task<ItemEffectNamesResult> PrepareNamesAsync(ItemEffectNamesRequest request,Func<ItemEffectsProgress,Task> progress,CancellationToken token)=>Prepare(ItemEffectsOperation.PrepareNames,progress,token,()=>NamesResult(request));
    }
    private sealed class Counted<T>(IReadOnlyList<T> values,Action visit):IReadOnlyList<T>
    {public int Count=>values.Count;public T this[int index]=>values[index];public IEnumerator<T> GetEnumerator(){foreach(var value in values){visit();yield return value;}}System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();}
    private static JsonElement Reply(IEnumerable<JsonElement> frames,string id)=>Assert.Single(frames,f=>f.GetProperty("id").GetString()==id);
    private static JsonElement[] Frames(byte[] bytes)=>Encoding.UTF8.GetString(bytes).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>{using var doc=JsonDocument.Parse(line);return doc.RootElement.Clone();}).ToArray();
    private sealed class HeldInput(byte[] bytes) : MemoryStream(bytes)
    {
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, cancellationToken);
            Waiting.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return 0;
        }
    }
    private sealed class HeldFlush : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Flushes;
        public override async Task FlushAsync(CancellationToken cancellationToken)
        { if (++Flushes == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); } }
    }
    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> pending = new();
        private readonly SemaphoreSlim posted = new(0, 1);
        public override void Post(SendOrPostCallback callback, object? state)
        { lock (pending) { pending.Enqueue((callback, state)); if (posted.CurrentCount == 0) posted.Release(); } }
        internal async Task DrainUntilAsync(Func<bool> completed)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!completed())
            {
                Drain();
                if (!completed()) await posted.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        internal void Drain()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (pending) { if (!pending.TryDequeue(out work)) return; }
                var previous = Current; SetSynchronizationContext(this);
                try { work.Callback(work.State); } finally { SetSynchronizationContext(previous); }
            }
        }
    }
    private sealed class Session:IAsyncDisposable
    {
        private readonly TcpClient client,server;private readonly StreamWriter writer;private readonly StreamReader reader;private readonly Task completion;
        private Session(TcpClient client,TcpClient server,ItemEffectsService? effects)
        {this.client=client;this.server=server;writer=new(client.GetStream(),new UTF8Encoding(false),leaveOpen:true);reader=new(client.GetStream(),Encoding.UTF8,leaveOpen:true);completion=new ProtocolHost(new DiscoveryService(new Unused(),new Unused()),itemEffects:effects).RunAsync(server.GetStream(),server.GetStream());}
        internal static async Task<Session> Start(ItemEffectsService? effects)
        {using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var client=new TcpClient();await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);return new(client,await listener.AcceptTcpClientAsync(),effects);}
        internal async Task Send(string id,string method,object parameters){await writer.WriteLineAsync(JsonSerializer.Serialize(Request(id,method,parameters),Json));await writer.FlushAsync();}
        internal async Task<JsonElement> Receive(){using var doc=JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);return doc.RootElement.Clone();}
        public async ValueTask DisposeAsync(){client.Dispose();try{await completion.WaitAsync(TimeSpan.FromSeconds(10));}finally{server.Dispose();reader.Dispose();await writer.DisposeAsync();}}
    }
    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
