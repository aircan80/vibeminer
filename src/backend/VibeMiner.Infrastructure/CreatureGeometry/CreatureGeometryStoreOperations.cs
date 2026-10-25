using System.Numerics;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class CreatureGeometryStore
{
    /// <param name="uses">The VMA1 files in use, shared with <see cref="GeometryAppearanceCleanup"/> (D103).</param>
    public CreatureGeometryStore(DiscoveryCatalog catalog,VerifiedObjectStore objects,CreatureDisplayMetadataStore displays,
        AssetResolutionSessions sessions,ISharedAcquisition shared,AcquisitionResources resources,TimeProvider? clock=null,GeometryAppearanceFileUses? uses=null)
    {
        payload=new();cpu=new GeometryWorkBudget(resources);this.clock=clock??TimeProvider.System;
        work=new Operations(catalog,objects,displays,new(catalog,objects,sessions,shared,resources),cpu,uses??new());
    }
    private sealed class Operations(DiscoveryCatalog catalog,VerifiedObjectStore objects,CreatureDisplayMetadataStore displays,
        GeometrySourceServices services,IGeometryArtifactWorkBudget budget,GeometryAppearanceFileUses uses):ICreatureGeometryStoreWork
    {
        private DerivedArtifactStaging? stagingDebt;
        public Task<GeometryDisplayAuthorizationResult> AuthorizeAsync(CreatureDisplayMetadataRequest selection,CancellationToken token)=>displays.AuthorizeGeometryAsync(selection,token);
        public Task<CreatureDisplayMetadataResult> EnsureMetadataAsync(CreatureDisplayMetadataRequest selection,Func<CreatureGeometryProgress,Task> progress,CancellationToken token)=>
            displays.PrepareDisplayAsync(selection,p=>progress(new("metadata","Preparing exact Creature display tables",p.Unit,p.Completed,p.Total,CreatureGeometryDependency.Metadata)),token);
        public async Task<IGeometryStoreRetained?> FindAsync(GeometryDisplayAuthorization selection,string? expectedArtifact,CancellationToken token)
        {
            var locator=await catalog.FindCreatureGeometryLocatorAsync(selection,expectedArtifact,budget,token);
            return locator is null?null:new Retained(catalog,objects,services.Resources,budget,locator,uses);
        }
        public async Task<IGeometryStorePublication> ProduceAsync(IGeometrySelectionFence selection,IGeometryPayloadBudget payload,
            Func<CreatureGeometryProgress,Task> progress,CancellationToken token)
        {
            if(stagingDebt is not null)throw Cleanup();
            GeometryPreparedSources? prepared=null;
            try
            {
                prepared=await GeometrySourceClosure.PrepareAsync(services,selection,payload,progress,token);
                GeometryArtifactDescriptor? descriptor=null;
                if(prepared.Closure.Capsule.Counts.SectionTriangleIndices!=0)
                {
                    await progress(new("artifactBuild","Building geometry artifact","stage",Dependency:CreatureGeometryDependency.Artifact));
                    descriptor=await BuildAsync(prepared,progress,token);
                }
                var (appearance,binding,retain,use)=descriptor is null?(null,null,false,null):await BuildAppearanceAsync(prepared,progress,token);
                var result=new Publication(catalog,budget,prepared,descriptor,appearance,binding,retain,use);prepared=null;return result;
            }
            catch(GeometrySourceFailureException error)
            {return new FailurePublication(error.State,error.Sources,new(error.Problem.Code,error.Problem.Message,error.Problem.Retryable,error.Dependency));}
            catch(DiscoveryException error)when(prepared is not null&&error.Code.StartsWith("GEOMETRY_ARTIFACT_",StringComparison.Ordinal))
            {return new FailurePublication(error.Code=="GEOMETRY_ARTIFACT_LIMIT"?CreatureGeometryState.ResourceLimit:error.Code=="GEOMETRY_ARTIFACT_UNSUPPORTED"?CreatureGeometryState.Unsupported:CreatureGeometryState.Corrupt,
                prepared.Sources,new(error.Code,error.Message,error.Retryable,CreatureGeometryDependency.Artifact));}
            finally{if(prepared is not null)await prepared.DisposeAsync();}
        }
        // A ready texture chain becomes one verified VMA1 object. An artifact failure is an appearance outcome, never a geometry one.
        // Decoded texture slots retire as soon as the build ends, before the final transaction.
        // A published VMA1 file stays in use until its publication is disposed, so cleanup never deletes it before its row commits (D103).
        private async Task<(CreatureGeometryAppearance?,AppearanceBinding?,bool,IDisposable?)> BuildAppearanceAsync(GeometryPreparedSources prepared,
            Func<CreatureGeometryProgress,Task> progress,CancellationToken token)
        {
            var source=prepared.Appearance;if(source is null)return(null,null,false,null);
            IDisposable? use=null;
            try
            {
                if(source.Evidence.State!=CreatureGeometryAppearanceState.Ready||!source.HasImages)return(source.Evidence,null,source.Cacheable,null);
                await progress(new("appearanceBuild","Building textured appearance artifact","stage",Dependency:CreatureGeometryDependency.Texture));
                var reservation=await services.Resources.ReserveStagingAsync(ModelAppearanceArtifacts.MaximumBytes,null,token);
                var staging=new DerivedArtifactStaging(reservation,File.Delete,"CREATURE_GEOMETRY_CLEANUP_FAILED");
                try
                {
                    var stage=await staging.CreateAsync(objects.CreateStagingFileAsync,token);var path=stage.Name;
                    var artifact=await ModelAppearanceArtifacts.BuildAsync(prepared.Skin,source.Draws!,source.Binding!,source.Texels,stage,budget,token);
                    source.Dispose();
                    await stage.FlushAsync(token);stage.Position=0;
                    await progress(new("appearanceVerification","Verifying complete appearance artifact","bytes",0,artifact.ByteLength,CreatureGeometryDependency.Texture));
                    await ModelAppearanceArtifacts.VerifyAsync(stage,artifact,budget,token);
                    await staging.CloseAsync();
                    await progress(new("appearancePublication","Publishing verified appearance artifact","bytes",artifact.ByteLength,artifact.ByteLength,CreatureGeometryDependency.Texture));
                    use=await uses.UseAsync(artifact.Sha256,token);
                    var sha=await objects.PublishFileAsync(path,artifact.ByteLength,services.Resources,null,token);
                    if(sha!=artifact.Sha256)throw new DiscoveryException("LOCAL_OBJECT_CORRUPT","The published appearance identity differs from the verified artifact.");
                    if(uses.AfterPublished is{}published)await published(sha);
                    var held=use;use=null;
                    return(source.Evidence with{Manifest=ToDomain(artifact.Manifest)},artifact.Binding,true,held);
                }
                finally{if(!await staging.RetireAsync()){stagingDebt=staging;throw staging.Failure();}}
            }
            catch(DiscoveryException error)when(error.Code.StartsWith("GEOMETRY_ARTIFACT_",StringComparison.Ordinal)||error.Code=="LOCAL_OBJECT_CORRUPT")
            {
                return(GeometryAppearanceSources.Outcome(error.Code=="GEOMETRY_ARTIFACT_LIMIT"?CreatureGeometryAppearanceState.ResourceLimit:CreatureGeometryAppearanceState.Corrupt,
                    source.Evidence.Display,source.Evidence.Textures,source.Evidence.Effects,new(error.Code,"The textured appearance artifact could not be built and verified.",error.Retryable)),null,false,null);
            }
            finally{use?.Dispose();source.Dispose();}
        }
        private async Task<GeometryArtifactDescriptor> BuildAsync(GeometryPreparedSources prepared,Func<CreatureGeometryProgress,Task> progress,CancellationToken token)
        {
            var reservation=await services.Resources.ReserveStagingAsync(2L<<20,null,token);
            var staging=new DerivedArtifactStaging(reservation,File.Delete,"CREATURE_GEOMETRY_CLEANUP_FAILED");
            try
            {
                var stage=await staging.CreateAsync(objects.CreateStagingFileAsync,token);var path=stage.Name;
                var artifact=await ModelGeometryArtifacts.BuildAsync(prepared.Skin,prepared.DataBinding,stage,budget,token);
                await stage.FlushAsync(token);stage.Position=0;
                await progress(new("artifactVerification","Verifying complete geometry artifact","bytes",0,artifact.ByteLength,CreatureGeometryDependency.Artifact));
                await ModelGeometryArtifacts.VerifyAsync(stage,artifact,budget,token);
                await staging.CloseAsync();
                await progress(new("publication","Publishing verified geometry artifact","bytes",artifact.ByteLength,artifact.ByteLength,CreatureGeometryDependency.Artifact));
                var sha=await objects.PublishFileAsync(path,artifact.ByteLength,services.Resources,null,token);
                if(sha!=artifact.Sha256)throw new DiscoveryException("LOCAL_OBJECT_CORRUPT","The published geometry identity differs from the verified artifact.");
                return new(artifact.Binding,ToDomain(artifact.Manifest));
            }
            finally
            {
                if(!await staging.RetireAsync()){stagingDebt=staging;throw staging.Failure();}
            }
        }
    }
    private sealed class Retained(DiscoveryCatalog catalog,VerifiedObjectStore objects,AcquisitionResources resources,
        IGeometryArtifactWorkBudget budget,GeometryLocator locator,GeometryAppearanceFileUses uses):IGeometryStoreRetained
    {
        private Stream? closeDebt;
        public CreatureGeometryManifest Manifest=>locator.Descriptor.Manifest;
        public IReadOnlyList<CreatureGeometrySource> Sources=>locator.Sources;
        public async Task<Stream> OpenVerifiedAsync(CancellationToken token)
        {
            if(closeDebt is not null)throw Cleanup();
            Stream? stream=null;
            try
            {
                stream=await objects.OpenVerifiedAsync(Manifest.Sha256,Manifest.ByteLength,resources,null,token);
                try{await ModelGeometryArtifacts.VerifyAsync(stream,ToPure(locator.Descriptor),budget,token);}
                catch(DiscoveryException error)when(error.Code.StartsWith("GEOMETRY_ARTIFACT_",StringComparison.Ordinal))
                {
                    await catalog.RecordObjectInvalidityAsync(Manifest.Sha256,Manifest.ByteLength,"LOCAL_OBJECT_CORRUPT",CancellationToken.None);
                    throw new DiscoveryException("LOCAL_OBJECT_CORRUPT","The retained geometry artifact failed complete structural verification.");
                }
                stream.Position=0;var result=stream;stream=null;return result;
            }
            finally{if(stream is not null)try{await stream.DisposeAsync();}catch(Exception){closeDebt=stream;throw Cleanup();}}
        }
        public Task FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)=>catalog.FinalizeCreatureGeometryReuseAsync(selection,locator,budget,token);
        public async Task<IGeometryStoreAppearance?> FindAppearanceAsync(GeometryDisplayAuthorization selection,CancellationToken token)
        {
            var record=await catalog.FindCreatureGeometryAppearanceAsync(selection,locator,budget,token);
            return record is null?null:new RetainedAppearance(catalog,objects,resources,budget,locator,record,uses);
        }
    }
    private sealed class RetainedAppearance(DiscoveryCatalog catalog,VerifiedObjectStore objects,AcquisitionResources resources,
        IGeometryArtifactWorkBudget budget,GeometryLocator locator,GeometryAppearanceRecord record,GeometryAppearanceFileUses uses):IGeometryStoreAppearance
    {
        private Stream? closeDebt;
        public CreatureGeometryAppearance Appearance=>record.Descriptor.Appearance;
        public async Task<Stream> OpenVerifiedAsync(CancellationToken token)
        {
            if(closeDebt is not null)throw Cleanup();
            var manifest=Appearance.Manifest??throw new DiscoveryException("CREATURE_GEOMETRY_NOT_PREPARED","This display has no retained textured appearance.",true);
            Stream? stream=null;
            // The use is taken before the file is opened and released when the returned stream is disposed (D103).
            IDisposable? use=await uses.UseAsync(manifest.Sha256,token);
            try
            {
                stream=new GeometryAppearanceUseStream(await objects.OpenVerifiedAsync(manifest.Sha256,manifest.ByteLength,resources,null,token),use);use=null;
                try{await ModelAppearanceArtifacts.VerifyAsync(stream,ToPure(record.Descriptor),budget,token);}
                catch(DiscoveryException error)when(error.Code.StartsWith("GEOMETRY_ARTIFACT_",StringComparison.Ordinal))
                {
                    await catalog.RecordObjectInvalidityAsync(manifest.Sha256,manifest.ByteLength,"LOCAL_OBJECT_CORRUPT",CancellationToken.None);
                    throw new DiscoveryException("LOCAL_OBJECT_CORRUPT","The retained appearance artifact failed complete structural verification.");
                }
                stream.Position=0;var result=stream;stream=null;return result;
            }
            finally{use?.Dispose();if(stream is not null)try{await stream.DisposeAsync();}catch(Exception){closeDebt=stream;throw Cleanup();}}
        }
        public Task FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)=>
            catalog.FinalizeCreatureGeometryAppearanceAsync(selection,locator,record.Descriptor,false,budget,token);
    }
    private sealed class FailurePublication(CreatureGeometryState state,IReadOnlyList<CreatureGeometrySource> observations,CreatureGeometryProblem problem):IGeometryStorePublication
    {
        public CreatureGeometryState State{get;}=observations.Count<=2?state:throw CaptureLimit();
        public CreatureGeometryManifest? Manifest=>null;
        // Compact reached observations never become a public current source claim without the pair fence.
        public IReadOnlyList<CreatureGeometrySource> Sources=>Array.Empty<CreatureGeometrySource>();
        public CreatureGeometryProblem? Problem=>problem;
        public Task<GeometryStoreFinalized> FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)
        {token.ThrowIfCancellationRequested();return Task.FromResult(new GeometryStoreFinalized(null,Array.Empty<CreatureGeometrySource>()));}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class Publication(DiscoveryCatalog catalog,IGeometryArtifactWorkBudget budget,GeometryPreparedSources prepared,
        GeometryArtifactDescriptor? descriptor,CreatureGeometryAppearance? appearance=null,AppearanceBinding? binding=null,bool retain=false,IDisposable? use=null):IGeometryStorePublication
    {
        public CreatureGeometryAppearance? Appearance=>appearance;
        private readonly object sync=new();
        private GeometryPreparedSources? owned=prepared;
        private int active;
        private TaskCompletionSource? idle,retirement;
        public CreatureGeometryState State=>descriptor is null?CreatureGeometryState.NoDeclaredTriangles:CreatureGeometryState.Ready;
        public CreatureGeometryManifest? Manifest=>descriptor?.Manifest;
        public IReadOnlyList<CreatureGeometrySource> Sources=>owned?.Sources??Array.Empty<CreatureGeometrySource>();
        public CreatureGeometryProblem? Problem=>null;
        public async Task<GeometryStoreFinalized> FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)
        {
            GeometryPreparedSources source;
            lock(sync){if(retirement is not null)throw new OperationCanceledException(token);source=owned!;active++;}
            try
            {
                var canonical=await catalog.FinalizeCreatureGeometryAsync(selection,source.Closure,descriptor,
                    descriptor is null?GeometryFinalAction.ValidateNoDeclaredTriangles:GeometryFinalAction.Publish,budget,token);
                var final=appearance;
                // Ready and model/display-determined refusals are retained; transient texture outcomes are reported, never cached.
                if(canonical is not null&&appearance is not null&&retain)
                    final=(await catalog.FinalizeCreatureGeometryAppearanceAsync(selection,canonical,new(appearance,binding),true,budget,token)).Descriptor.Appearance;
                return new(canonical?.Descriptor.Manifest,canonical?.Sources??source.Sources,canonical is null?null:final);
            }
            finally{lock(sync){active--;if(active==0)idle?.TrySetResult();}}
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource completion;Task wait;
            lock(sync)
            {
                if(retirement is not null)return new(retirement.Task);
                completion=retirement=Signal();wait=active==0?Task.CompletedTask:(idle??=Signal()).Task;
            }
            _=RetireAsync();return new(completion.Task);
            async Task RetireAsync()
            {
                try{await wait;use?.Dispose();if(owned is not null)await owned.DisposeAsync();owned=null;completion.TrySetResult();}
                catch(Exception error){completion.TrySetException(error);}
            }
        }
    }
    private static CreatureGeometryManifest ToDomain(GeometryManifest m)=>new(m.ArtifactId,m.BindingSha256,m.Sha256,m.ByteLength,m.FormatVersion,m.HeaderBytes,m.Scope,m.CoordinatePolicy,
        new(m.Evaluations.Material,m.Evaluations.Pose,m.Evaluations.Visibility,m.Evaluations.Appearance),m.VertexCount,m.VertexLookupCount,m.SourceTriangleIndexCount,m.SectionIndexCount,m.LineIndexCount,m.SectionCount,
        new(m.Positions.Offset,m.Positions.ByteLength),new(m.LineIndices.Offset,m.LineIndices.ByteLength),new(m.Sections.Offset,m.Sections.ByteLength),new(m.Bounds.Min,m.Bounds.Max),
        m.ModelFileDataId,m.SkinFileDataId,m.MainSkinOrdinal,m.ModelReaderRevision,m.SkinReaderRevision,m.InterpretationPolicy,m.ArtifactRevision,m.EdgePolicy);
    private static CreatureGeometryAppearanceManifest ToDomain(AppearanceManifest m)=>new(m.ArtifactId,m.BindingSha256,m.Sha256,m.ByteLength,m.FormatVersion,m.HeaderBytes,
        m.CoordinatePolicy,m.VertexCount,m.IndexCount,m.DrawCount,m.TextureCount,new(m.Positions.Offset,m.Positions.ByteLength),new(m.Normals.Offset,m.Normals.ByteLength),new(m.Uvs.Offset,m.Uvs.ByteLength),
        new(m.Indices.Offset,m.Indices.ByteLength),new(m.Draws.Offset,m.Draws.ByteLength),new(m.Textures.Offset,m.Textures.ByteLength),new(m.Texels.Offset,m.Texels.ByteLength),
        new(m.Bounds.Min,m.Bounds.Max),m.AlphaReference,m.DisplayId,m.GeometryBindingSha256,m.MaterialPolicy,m.TexturePolicy,m.ReaderRevision,m.DecoderRevision,m.ArtifactRevision);
    private static AppearanceArtifact ToPure(GeometryAppearanceDescriptor descriptor)
    {
        var m=descriptor.Appearance.Manifest??throw new DiscoveryException("CREATURE_GEOMETRY_NOT_PREPARED","This display has no retained textured appearance.",true);
        var manifest=new AppearanceManifest(m.ArtifactId,m.BindingSha256,m.Sha256,m.ByteLength,m.FormatVersion,m.HeaderBytes,m.CoordinatePolicy,m.VertexCount,m.IndexCount,
            m.DrawCount,m.TextureCount,new(m.Positions.Offset,m.Positions.ByteLength),new(m.Normals.Offset,m.Normals.ByteLength),new(m.Uvs.Offset,m.Uvs.ByteLength),new(m.Indices.Offset,m.Indices.ByteLength),
            new(m.Draws.Offset,m.Draws.ByteLength),new(m.Textures.Offset,m.Textures.ByteLength),new(m.Texels.Offset,m.Texels.ByteLength),
            new(new Vector3(m.Bounds.Min[0],m.Bounds.Min[1],m.Bounds.Min[2]),new Vector3(m.Bounds.Max[0],m.Bounds.Max[1],m.Bounds.Max[2])),m.AlphaReference,m.DisplayId,
            m.GeometryBindingSha256,m.MaterialPolicy,m.TexturePolicy,m.ReaderRevision,m.DecoderRevision,m.ArtifactRevision);
        return new(descriptor.Binding!,m.BindingSha256,m.ArtifactId,m.Sha256,m.ByteLength,manifest);
    }
    private static GeometryArtifact ToPure(GeometryArtifactDescriptor descriptor)
    {
        var m=descriptor.Manifest;
        var manifest=new GeometryManifest(m.ArtifactId,m.BindingSha256,m.Sha256,m.ByteLength,m.FormatVersion,m.HeaderBytes,m.Scope,m.CoordinatePolicy,
            new(m.Evaluations.Material,m.Evaluations.Pose,m.Evaluations.Visibility,m.Evaluations.Appearance),m.VertexCount,m.VertexLookupCount,m.SourceTriangleIndexCount,m.SectionIndexCount,m.LineIndexCount,m.SectionCount,
            new(m.Positions.Offset,m.Positions.ByteLength),new(m.LineIndices.Offset,m.LineIndices.ByteLength),new(m.Sections.Offset,m.Sections.ByteLength),
            new(new Vector3(m.Bounds.Min[0],m.Bounds.Min[1],m.Bounds.Min[2]),new Vector3(m.Bounds.Max[0],m.Bounds.Max[1],m.Bounds.Max[2])),m.ModelFileDataId,m.SkinFileDataId,m.MainSkinOrdinal,
            m.ModelReaderRevision,m.SkinReaderRevision,m.InterpretationPolicy,m.ArtifactRevision,m.EdgePolicy);
        return new(descriptor.Binding,m.BindingSha256,m.ArtifactId,m.Sha256,m.ByteLength,manifest);
    }
}
