using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreatureGeometryStore
{
    private async Task<IGeometryStorePublication?> TryReuseAsync(GeometryDisplayAuthorization selection,CancellationToken token)
    {
        // Locator materialization and verification both belong to the same charged physical union slot.
        var slot=ReserveReader();await using var cancellation=token.Register(()=>StartReadRetirement(slot));
        try
        {
            var retained=await work.FindAsync(selection,null,slot.Cancellation.Token);if(retained is null)return null;slot.Owner=retained;
            try{slot.Stream=await retained.OpenVerifiedAsync(slot.Cancellation.Token);}
            catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_MISSING"){return null;}
            catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_CORRUPT")
            {return new FailurePublication(CreatureGeometryState.Corrupt,retained.Sources,new(error.Code,error.Message,error.Retryable,CreatureGeometryDependency.Artifact));}
            // A ready geometry is reused only with its retained appearance; otherwise the producer prepares both again.
            var appearance=await retained.FindAppearanceAsync(selection,slot.Cancellation.Token);if(appearance is null)return null;
            var corrupt=false;
            if(appearance.Appearance.Manifest is not null)
            {
                try{await (await appearance.OpenVerifiedAsync(slot.Cancellation.Token)).DisposeAsync();}
                catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_MISSING"){return null;}
                catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_CORRUPT"){corrupt=true;}
            }
            slot.Cancellation.Token.ThrowIfCancellationRequested();return new ReusedPublication(retained,appearance,corrupt?Corrupt(appearance.Appearance):null);
        }
        catch(Exception error)when(CleanupFailure(error)){slot.Failure=error;FailClosed(error);throw;}
        finally{slot.Opened.TrySetResult();StartReadRetirement(slot);await slot.Retired.Task;}
    }
    private static CreatureGeometryAppearance Corrupt(CreatureGeometryAppearance retained)=>GeometryAppearanceSources.Outcome(CreatureGeometryAppearanceState.Corrupt,
        retained.Display,retained.Textures,retained.Effects,new("LOCAL_OBJECT_CORRUPT","The retained appearance artifact failed complete structural verification.",false));
    private sealed class ReusedPublication(IGeometryStoreRetained retained,IGeometryStoreAppearance? appearance=null,CreatureGeometryAppearance? replaced=null):IGeometryStorePublication
    {
        public CreatureGeometryAppearance? Appearance=>replaced??appearance?.Appearance;
        public CreatureGeometryState State=>CreatureGeometryState.Ready;
        public CreatureGeometryManifest Manifest=>retained.Manifest;
        public IReadOnlyList<CreatureGeometrySource> Sources=>retained.Sources;
        public CreatureGeometryProblem? Problem=>null;
        public async Task<GeometryStoreFinalized> FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)
        {
            await retained.FinalizeAsync(selection,token);
            if(appearance is not null&&replaced is null)await appearance.FinalizeAsync(selection,token);
            return new(retained.Manifest,retained.Sources,Appearance);
        }
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
