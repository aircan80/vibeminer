using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureGeometryC2AuthorizationTests
{
    private static readonly CreaturesContext Context=new("wow",new('a',64),"us","enUS");
    private static readonly CreatureDisplayMetadataRequest Request=new(Context,new('b',64),"1",2);
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact]
    public async Task PositiveUniqueChainReturnsOriginalCoreSlotCopiesAndMaximumUnsignedFileWithoutAcquisition()
    {
        var source=new Sources{DisplayReady=true,ModelReady=true,DisplayEdge="65535",FileEdge=uint.MaxValue.ToString(),Copy=true,Probability="-0.25"};
        await using var store=new CreatureDisplayMetadataStore(source);var result=await store.AuthorizeGeometryAsync(Request,default);
        var proof=Assert.IsType<GeometryDisplayAuthorization>(result.Authorization);Assert.Same(source.OriginalCore,result.Core);Assert.Same(result.Core,proof.Core);
        Assert.Same(result.Selected,proof.Selected);Assert.Equal(2,proof.Selected.Slot);Assert.Equal("-0.25",proof.Selected.Probability);
        Assert.Equal(uint.MaxValue,proof.ModelFileDataId);Assert.Equal("65535",result.Metadata.ModelId);Assert.Equal(uint.MaxValue.ToString(),result.Metadata.ModelFileDataId);
        Assert.Equal(CreatureDisplayMetadataState.MetadataResolved,result.Metadata.State);Assert.Equal(CreatureDisplayAppearance.NotEvaluated,result.Metadata.Appearance);
        Assert.Equal(2,result.Delivered.Count);Assert.Same(result.Delivered[0],proof.Display);Assert.Same(result.Delivered[1],proof.Model);
        Assert.Equal("0",result.Metadata.Targets[0].Row!.CopySourceId);Assert.Equal("100",result.Metadata.Targets[0].Row!.Source.RowId);
        Assert.Equal("65535",result.Metadata.Targets[1].Row!.Source.RowId);Assert.Equal(0,source.Ensures);Assert.Equal(1,source.Fences);
        Assert.Equal(1,source.Authorizations);
    }
    [Theory]
    [InlineData(false,"bfdaf9f1",0)] [InlineData(true,"7f695fa5",1)]
    public async Task UnpreparedPathRetainsOriginalCoreSelectedSlotAndActuallyDeliveredSources(bool displayReady,string required,int targets)
    {
        var source=new Sources{DisplayReady=displayReady};await using var store=new CreatureDisplayMetadataStore(source);
        var result=await store.AuthorizeGeometryAsync(Request,default);Assert.Same(source.OriginalCore,result.Core);Assert.NotNull(result.Selected);Assert.Null(result.Authorization);
        Assert.Equal(CreatureDisplayMetadataState.NotPrepared,result.Metadata.State);Assert.Equal(required,result.Metadata.RequiredTableId);Assert.Equal(targets,result.Delivered.Count);
        Assert.Equal(targets,result.Metadata.Targets.Count);Assert.Equal(displayReady?"33":null,result.Metadata.ModelId);Assert.Equal(0,source.Ensures);Assert.Equal(1,source.Fences);
    }
    [Theory]
    [InlineData(null,CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope)]
    [InlineData("0",CreatureDisplayMetadataState.NoDeclaredDisplay)] [InlineData("-1",CreatureDisplayMetadataState.InvalidDisplayReference)]
    public async Task MissingZeroAndNegativeCreatureSlotNeverProbeOrInventGeometryProof(string? display,CreatureDisplayMetadataState expected)
    {
        var source=new Sources{Display=display,Partial=true};await using var store=new CreatureDisplayMetadataStore(source);
        var result=await store.AuthorizeGeometryAsync(Request,default);Assert.Same(source.OriginalCore,result.Core);Assert.Equal(expected,result.Metadata.State);Assert.Null(result.Authorization);
        Assert.Empty(result.Delivered);Assert.Empty(result.Metadata.Targets);Assert.Equal(0,source.Probes);Assert.Equal(0,source.Ensures);
        Assert.Equal(display is null,result.Selected is null);if(display is null)Assert.Equal(CreatureDisplayAbsence.UnknownOutsideReadableScope,result.Metadata.Absence);
    }
    [Theory]
    [InlineData(false,"0",CreatureDisplayMetadataState.DisplayMissingInAdmittedScope)]
    [InlineData(false,"2",CreatureDisplayMetadataState.DisplayAmbiguous)]
    [InlineData(true,"0",CreatureDisplayMetadataState.ModelMissingInAdmittedScope)]
    [InlineData(true,"2",CreatureDisplayMetadataState.ModelAmbiguous)]
    public async Task MissingAndAmbiguousTargetRetainTheirExactOriginalScopedBindingsWithoutRows(bool model,string count,CreatureDisplayMetadataState state)
    {
        var source=new Sources{DisplayReady=true,ModelReady=true,Partial=true};if(model)source.ModelMatch=count;else source.DisplayMatch=count;
        await using var store=new CreatureDisplayMetadataStore(source);var result=await store.AuthorizeGeometryAsync(Request,default);
        Assert.Null(result.Authorization);Assert.Equal(state,result.Metadata.State);Assert.Equal(model?2:1,result.Delivered.Count);
        Assert.Null(result.Metadata.Targets[^1].Row);Assert.Equal(count,result.Metadata.Targets[^1].MatchCount);Assert.Equal("unencryptedSections",result.Metadata.Targets[^1].Coverage.Scope);
        Assert.Same(source.Returned[^1],result.Delivered[^1]);if(count=="0")Assert.Equal(CreatureDisplayAbsence.UnknownOutsideReadableScope,result.Metadata.Absence);
    }
    [Theory]
    [InlineData(false,CreatureDisplayMetadataState.NoDeclaredModel)] [InlineData(true,CreatureDisplayMetadataState.NoDeclaredModelFile)]
    public async Task ZeroTargetEdgeRetainsEvidenceWithoutAuthorization(bool model,CreatureDisplayMetadataState state)
    {
        var source=new Sources{DisplayReady=true,ModelReady=true};if(model)source.FileEdge="0";else source.DisplayEdge="0";
        await using var store=new CreatureDisplayMetadataStore(source);var result=await store.AuthorizeGeometryAsync(Request,default);
        Assert.Null(result.Authorization);Assert.Equal(state,result.Metadata.State);Assert.Equal("0",result.Metadata.Targets[^1].Row!.EdgeValue.Value);
        Assert.Equal(model?2:1,source.Probes);Assert.Equal(0,source.Ensures);
    }
    [Fact]
    public async Task OptionalFailureBeforeDeliveryPreservesPriorTargetAndExactProblemForFutureGeometryMapping()
    {
        var source=new Sources{DisplayReady=true,ModelReady=true};source.BeforeProbe=(role,_,_)=>role==CreatureDisplayTargetRole.Model?throw new DiscoveryException("BLTE_KEY_UNAVAILABLE","private diagnostic",true):Task.CompletedTask;
        await using var store=new CreatureDisplayMetadataStore(source);var result=await store.AuthorizeGeometryAsync(Request,default);
        Assert.Null(result.Authorization);Assert.Equal(CreatureDisplayMetadataState.Protected,result.Metadata.State);Assert.Single(result.Delivered);Assert.Equal("33",result.Metadata.ModelId);
        Assert.Equal("BLTE_KEY_UNAVAILABLE",result.Problem!.Code);Assert.True(result.Problem.Retryable);Assert.Equal(CreatureDisplayTargetRole.Model,result.Problem.Target);Assert.DoesNotContain("private",result.Problem.Message);
    }
    [Fact]
    public async Task UnsupportedDeliveredSourceIsRetainedAsOriginalProofAndHasNoInventedRow()
    {
        var source=new Sources{DisplayReady=true,Unsupported=true};await using var store=new CreatureDisplayMetadataStore(source);
        var result=await store.AuthorizeGeometryAsync(Request,default);Assert.Null(result.Authorization);Assert.Equal(CreatureDisplayMetadataState.Unsupported,result.Metadata.State);
        Assert.Single(result.Delivered);Assert.Null(result.Metadata.Targets[0].Row);Assert.Null(result.Metadata.Targets[0].MatchCount);Assert.Same(source.Returned[0],result.Delivered[0]);Assert.NotNull(result.Problem);
    }
    [Theory]
    [InlineData("role")] [InlineData("table")] [InlineData("binding")] [InlineData("artifact")] [InlineData("column")]
    [InlineData("row")] [InlineData("copy")] [InlineData("value")] [InlineData("width")] [InlineData("coverage")]
    public async Task InconsistentResolvedTargetEvidenceCannotBecomeGeometryAuthorization(string mutation)
    {
        var source=new Sources{DisplayReady=true,ModelReady=true};source.Mutate=probe=>
        {
            if(probe.Target.Role!=CreatureDisplayTargetRole.Display)return probe;
            var t=probe.Target;var row=t.Row!;
            return mutation switch
            {
                "role"=>probe with{Target=t with{Role=CreatureDisplayTargetRole.Model}},"table"=>probe with{Target=t with{Snapshot=t.Snapshot with{TableId="00000000"}}},
                "binding"=>probe with{Binding=probe.Binding with{TableId="00000000"}},"artifact"=>probe with{Target=t with{Snapshot=t.Snapshot with{ArtifactId=new('e',64)}}},
                "column"=>probe with{Target=t with{Row=row with{Source=row.Source with{ColumnIndex=0}}}},"row"=>probe with{Target=t with{Row=row with{Source=row.Source with{RowId="101"}}}},
                "copy"=>probe with{Target=t with{Row=row with{IsCopy=true,CopySourceId=null}}},"value"=>probe with{Target=t with{Row=row with{EdgeValue=row.EdgeValue with{Value="34"}}}},
                "width"=>probe with{Edge="65536",Target=t with{Row=row with{EdgeValue=row.EdgeValue with{Value="65536"}}}},
                _=>probe with{Target=t with{Snapshot=t.Snapshot with{Coverage=t.Snapshot.Coverage with{PhysicalRows="2"}}}}
            };
        };
        await using var store=new CreatureDisplayMetadataStore(source);Assert.Equal("CREATURE_DISPLAY_INTEGRITY",(await Assert.ThrowsAsync<DiscoveryException>(()=>store.AuthorizeGeometryAsync(Request,default))).Code);Assert.Equal(0,source.Ensures);
    }
    [Fact]
    public async Task GeometryAndPublicReadShareOnlyTwoLocalAdmissionsIncludingNotificationRetirement()
    {
        var entered=Signal();var notificationEntered=Signal();var notificationRelease=Signal();var cleanupEntered=Signal();var cleanupRelease=Signal();var blocked=Signal();var calls=0;
        var source=new Sources();source.BeforeAuthorize=async token=>
        {
            var first=Interlocked.Increment(ref calls)==1;
            using var registration=first?token.Register(()=>{notificationEntered.TrySetResult();notificationRelease.Task.GetAwaiter().GetResult();}):default;
            if(first)entered.TrySetResult();
            try{await blocked.Task.WaitAsync(token);}finally{cleanupEntered.TrySetResult();await cleanupRelease.Task;}
        };
        var store=new CreatureDisplayMetadataStore(source);using var cancel=new CancellationTokenSource();
        try
        {
            var geometry=store.AuthorizeGeometryAsync(Request,cancel.Token);await entered.Task;
            var local=store.ReadDisplayAsync(Request,default);
            Assert.Equal("CREATURES_QUERY_BUSY",(await Assert.ThrowsAsync<DiscoveryException>(()=>store.AuthorizeGeometryAsync(Request,default))).Code);
            var stopping=cancel.CancelAsync();await notificationEntered.Task;Assert.False(geometry.IsCompleted);
            Assert.Equal("CREATURES_QUERY_BUSY",(await Assert.ThrowsAsync<DiscoveryException>(()=>store.ReadDisplayAsync(Request,default))).Code);
            notificationRelease.SetResult();await stopping;await cleanupEntered.Task;Assert.False(geometry.IsCompleted);cleanupRelease.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>geometry);blocked.SetResult();await local;
        }
        finally{notificationRelease.TrySetResult();cleanupRelease.TrySetResult();blocked.TrySetResult();await store.DisposeAsync();}
    }
    [Theory]
    [InlineData("authorize")] [InlineData("probe")] [InlineData("finish")]
    public async Task GeometryShutdownUsesExistingLocalLifetimeAndWaitsAcceptedWorkCleanup(string stage)
    {
        var entered=Signal();var cleanup=Signal();var release=Signal();var never=Signal();
        async Task Block(CancellationToken token){entered.TrySetResult();try{await never.Task.WaitAsync(token);}finally{cleanup.TrySetResult();await release.Task;}}
        var source=new Sources{DisplayReady=true,ModelReady=true};if(stage=="authorize")source.BeforeAuthorize=Block;
        if(stage=="probe")source.BeforeProbe=(_,_,token)=>Block(token);if(stage=="finish")source.BeforeFinish=Block;
        var store=new CreatureDisplayMetadataStore(source);
        try
        {
            var call=store.AuthorizeGeometryAsync(Request,default);await entered.Task;var close=store.DisposeAsync().AsTask();await cleanup.Task;
            Assert.False(call.IsCompleted);Assert.False(close.IsCompleted);Assert.False(source.Disposed);release.SetResult();await close;await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>call);Assert.True(source.Disposed);
            Assert.Equal("ENGINE_STOPPING",(await Assert.ThrowsAsync<DiscoveryException>(()=>store.AuthorizeGeometryAsync(Request,default))).Code);
        }
        finally{release.TrySetResult();await store.DisposeAsync();}
    }

    [Fact]
    public async Task RealC2SourceCatalogAndRetainedReadersProduceCompactOriginalGeometryProofLocally()
    {
        await using var fixture=await CreatureDisplaySourcesTests.Fixture.Create();var source=fixture.Sources();await using var store=new CreatureDisplayMetadataStore(source);
        var result=await store.AuthorizeGeometryAsync(fixture.Request,default);var proof=Assert.IsType<GeometryDisplayAuthorization>(result.Authorization);
        Assert.Equal(777u,proof.ModelFileDataId);Assert.Equal(fixture.Core.Artifact.ContentKey,result.Core.Artifact.ContentKey);
        Assert.Equal(fixture.Core.Opening.Reference,result.Core.Opening.Reference);Assert.Equal(fixture.Core.Summary!.DisplaySlots[0],result.Selected);
        Assert.True(proof.Display.Target.Row!.IsCopy);Assert.Equal("122",proof.Display.Target.Row.CopySourceId);Assert.Equal("123",proof.Display.Target.Row.Source.RowId);
        Assert.Equal(2,source.Pressure.Readers);Assert.Equal(0,source.Pressure.Borrowed);Assert.Empty(fixture.Prepared);
        var again=await store.AuthorizeGeometryAsync(fixture.Request,default);Assert.Equal(777u,again.Authorization!.ModelFileDataId);Assert.Equal(2,source.Pressure.Readers);Assert.Empty(fixture.Prepared);
        await store.DisposeAsync();Assert.Equal(0,source.Pressure.Readers);
    }
    [Theory]
    [InlineData(CreatureDisplayTargetRole.Display)] [InlineData(CreatureDisplayTargetRole.Model)]
    public async Task HistoricalRealC2AdmissionReturnsOriginalUnpreparedAnchorAndOnlyExplicitPrepareRefreshes(CreatureDisplayTargetRole historical)
    {
        await using var fixture=await CreatureDisplaySourcesTests.Fixture.Create();await fixture.PublishTarget(historical,historical:true);
        await using var store=new CreatureDisplayMetadataStore(fixture.Sources());var before=await store.AuthorizeGeometryAsync(fixture.Request,default);
        Assert.Null(before.Authorization);Assert.Equal(CreatureDisplayMetadataState.NotPrepared,before.Metadata.State);Assert.Equal(CreatureDisplayPolicy.Table(historical),before.Metadata.RequiredTableId);
        Assert.Equal(historical==CreatureDisplayTargetRole.Model?1:0,before.Delivered.Count);Assert.Empty(fixture.Prepared);
        var original=before.Selected;var partial=before.Delivered.ToArray();
        await store.PrepareDisplayAsync(fixture.Request,_=>Task.CompletedTask,default);var after=await store.AuthorizeGeometryAsync(fixture.Request,default);
        Assert.NotNull(after.Authorization);Assert.Equal(original,before.Selected);Assert.Equal(original,after.Selected);Assert.Equal(partial,before.Delivered);
        Assert.Equal(new[]{CreatureDisplayPolicy.Table(historical)},fixture.Prepared);
    }
    private sealed class Sources:ICreatureDisplaySources
    {
        internal string? Display="100";internal string Probability="0.25",DisplayEdge="33",FileEdge="777",DisplayMatch="1",ModelMatch="1";
        internal bool DisplayReady,ModelReady,Copy,Partial,Unsupported,Disposed;internal int Ensures,Probes,Fences,Authorizations;
        internal CreaturesDisplayAuthorization? OriginalCore;internal readonly List<CreatureDisplayProbe> Returned=[];
        internal Func<CancellationToken,Task>? BeforeAuthorize,BeforeFinish;internal Func<CreatureDisplayTargetRole,string,CancellationToken,Task>? BeforeProbe;
        internal Func<CreatureDisplayProbe,CreatureDisplayProbe>? Mutate;
        public async Task<CreaturesDisplayAuthorization> AuthorizeAsync(CreatureDisplayMetadataRequest request,CancellationToken token)
        {Authorizations++;if(BeforeAuthorize is not null)await BeforeAuthorize(token);return OriginalCore??=Core(request,Display,Probability,Partial);}
        public async Task<CreatureDisplayProbe?> ProbeAsync(CreaturesDisplayAuthorization core,CreatureDisplayTargetRole role,string id,CancellationToken token)
        {
            Probes++;if(BeforeProbe is not null)await BeforeProbe(role,id,token);if(!(role==CreatureDisplayTargetRole.Display?DisplayReady:ModelReady))return null;
            var match=role==CreatureDisplayTargetRole.Display?DisplayMatch:ModelMatch;var edge=role==CreatureDisplayTargetRole.Display?DisplayEdge:FileEdge;
            var (snapshot,admission)=Target(role,Partial,Copy,(int)Math.Max(1,uint.Parse(match)));
            var evidence=match=="1"?new CreatureDisplayRowEvidence(new(snapshot.TableId,snapshot.ArtifactId,id,1),Copy,Copy?"0":null,new(snapshot.TableId,1,edge)):null;
            var binding=new CreatureDisplayCatalogBinding(snapshot.TableId,admission);var probe=new CreatureDisplayProbe(new(role,snapshot,match,evidence),binding,match=="1"?edge:null);
            if(Unsupported&&role==CreatureDisplayTargetRole.Display)probe=new(new(role,snapshot,null),binding,null,CreatureDisplayMetadataState.Unsupported,new("CREATURE_DISPLAY_SCHEMA_UNSUPPORTED","Unsupported exact source.",false,role));
            probe=Mutate?.Invoke(probe)??probe;Returned.Add(probe);return probe;
        }
        public Task EnsureAsync(CreaturesContext context,CreatureDisplayTargetRole role,Func<CreaturesPreparationProgress,Task> progress,CancellationToken token){Ensures++;throw new InvalidOperationException("Geometry authorization must be strictly local.");}
        public async Task FinishAsync(CreatureDisplayMetadataRequest request,CreaturesDisplayAuthorization core,IReadOnlyList<CreatureDisplayProbe> targets,CancellationToken token){Fences++;if(BeforeFinish is not null)await BeforeFinish(token);}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
    }
    private static (TableDataSnapshot,StoredTableAdmission) Target(CreatureDisplayTargetRole role,bool partial,bool copy,int total)
    {
        var table=CreatureDisplayPolicy.Table(role);var hash=new string(role==CreatureDisplayTargetRole.Display?'d':'e',64);var field=CreatureDisplayPolicy.Field(role);
        Db2Column[] columns=[new("ID","integer",true),new(field,"integer",false)];
        var binding=new TableArtifactBinding(new('1',64),new('2',64),CreatureDisplayPolicy.Definition(role),BundledTableDefinitions.Revision,"enUS",TableDataStore.DecoderRevision,new('3',64));
        var completion=new Wdc5Completion(total+(partial?1:0),total-(copy?1:0),copy?1:0,partial?1:0,partial?1:0,0,partial?"unencryptedSections":"complete",64);
        var header=new Wdc5TableHeader(5,table.ToUpperInvariant(),CreatureDisplayPolicy.Layout(role),completion.DeclaredRecords,completion.CopyRows,4,columns);
        if(copy)completion=completion with{DeclaredRecords=completion.PhysicalRows+completion.ExcludedPhysicalRecords};header=header with{DeclaredRecords=completion.DeclaredRecords};
        var rows=new TableRowsArtifact(hash,new('4',64),128,binding,header,completion);
        var receipt=new ContentReceipt(new('5',32),new('6',32),binding.SourceSha256,"128","64","https://cdn.example/data","original","original","content-blte-v1",new("loose","0","128",null));
        MetadataReceipt Metadata(string r)=>new(r,new('7',32),new('8',32),new('9',64),"128","64","https://cdn.example/data","original","original","parser");
        var association=new SelectedFileAssociation(CreatureDisplayPolicy.File(role),"enUS","enus-consistent-root-variants-v1",Metadata("encoding"),Metadata("root"),[new("0","0",receipt.ContentKey,"2","0","0","0","0",null)]);
        var admission=new StoredTableAdmission(rows,receipt,association,"original");
        var snapshot=new TableDataSnapshot(Context.ProductId,Context.BuildIdentity,Context.SourceRegion,table,"enUS",hash,"original",header.LayoutHash,binding.DefinitionSha256,binding.ProviderRevision,binding.DecoderRevision,receipt,association,
            columns.Select(c=>new TableDataColumn(c.Name,c.Kind,c.IsId,null)).ToArray(),new(completion.Scope,completion.DeclaredRecords.ToString(),completion.PhysicalRows.ToString(),completion.CopyRows.ToString(),completion.ExcludedEncryptedSections.ToString(),completion.ExcludedPhysicalRecords.ToString(),completion.CopyRows.ToString(),completion.ExcludedPhysicalRecords.ToString(),"0"),total.ToString());
        return(snapshot,admission);
    }
    private static CreaturesDisplayAuthorization Core(CreatureDisplayMetadataRequest request,string? display,string probability,bool partial)
    {
        var fields=new CreatureFieldMap(new("c9d6b6b3",0),new("c9d6b6b3",1),new("c9d6b6b3",2),new("c9d6b6b3",3),[],Enumerable.Range(0,4).Select(i=>new CreatureFieldReference("c9d6b6b3",i)).ToArray(),Enumerable.Range(4,4).Select(i=>new CreatureFieldReference("c9d6b6b3",i)).ToArray());
        var (target,_)=Target(CreatureDisplayTargetRole.Display,partial,false,1);var source=target with{TableId="c9d6b6b3",ArtifactId=new('c',64),Columns=Enumerable.Range(0,8).Select(i=>new TableDataColumn("Field"+i,i<4?"integer":"float",false,null)).ToArray()};
        var snapshot=new CreaturesSnapshot(request.Context,request.ArtifactId,"original","creatures-core-v1","query","profile",source,new("1","1","0","0","4","0","0"),fields);
        var artifact=new CreaturesArtifact(request.ArtifactId,new('f',64),64,null!,snapshot.Counts);
        var opening=new CapturedOpeningContext(null!,new("1","us",new('a',64),0,new('b',64),0),null!,null!);
        var summary=display is null?null:new CreatureSummary(request.CreatureId,new(CreatureNameState.Valid,"Authored"),[],Enumerable.Range(0,4).Select(i=>new CreatureDisplaySlot(i,display,probability,display[0]=='-'?CreatureDisplayState.InvalidDisplayReference:display=="0"?CreatureDisplayState.NoDeclaredDisplay:CreatureDisplayState.Declared,fields.DisplayIds[i],fields.Probabilities[i])).ToArray());
        return new(opening,artifact,snapshot,summary);
    }
}
