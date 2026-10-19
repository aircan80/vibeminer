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
            slot.Cancellation.Token.ThrowIfCancellationRequested();return new ReusedPublication(retained);
        }
        catch(Exception error)when(CleanupFailure(error)){slot.Failure=error;FailClosed(error);throw;}
        finally{slot.Opened.TrySetResult();StartReadRetirement(slot);await slot.Retired.Task;}
    }
    private sealed class ReusedPublication(IGeometryStoreRetained retained):IGeometryStorePublication
    {
        public CreatureGeometryState State=>CreatureGeometryState.Ready;
        public CreatureGeometryManifest Manifest=>retained.Manifest;
        public IReadOnlyList<CreatureGeometrySource> Sources=>retained.Sources;
        public CreatureGeometryProblem? Problem=>null;
        public async Task<GeometryStoreFinalized> FinalizeAsync(GeometryDisplayAuthorization selection,CancellationToken token)
        {await retained.FinalizeAsync(selection,token);return new(retained.Manifest,retained.Sources);}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
