using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreatureGeometryStore
{
    private sealed class ReadSlot(IDisposable reservation)
    {
        internal readonly IDisposable Reservation = reservation;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource Opened = Signal(), Retired = Signal();
        internal readonly object Sync = new();
        internal Task? Closing;
        internal TaskCompletionSource? Reading;
        internal Stream? Stream;
        internal object? Owner;
        internal Exception? Failure;
        internal bool Stopping;
    }
    private ReadSlot ReserveReader()
    {
        lock(gate)
        {
            RequireOpen();if(readers.Count==2)throw Busy();
            var slot=new ReadSlot(payload.Reserve(GeometryPayloadBudget.ReaderBytes));readers.Add(slot);return slot;
        }
    }
    private void StartReadRetirement(ReadSlot slot)
    {
        lock(slot.Sync){slot.Stopping=true;slot.Closing??=RetireReaderAsync(slot);}
    }
    private async Task RetireReaderAsync(ReadSlot slot)
    {
        await Task.Yield();
        try
        {
            await ObserveCancellationAsync(slot.Cancellation);await slot.Opened.Task;
            Task reading;lock(slot.Sync)reading=slot.Reading?.Task??Task.CompletedTask;
            await reading;
            if(slot.Stream is not null)await slot.Stream.DisposeAsync();
            slot.Stream=null;
            if(slot.Failure is not null)throw Cleanup();
            slot.Owner=null;slot.Cancellation.Dispose();slot.Reservation.Dispose();
            lock(gate)readers.Remove(slot);
            slot.Retired.TrySetResult();
        }
        catch(Exception error){FailClosed(error);slot.Retired.TrySetException(Cleanup());}
    }
    public async Task<CreatureGeometryResult> ReadAsync(CreatureGeometryReadRequest request,CancellationToken token)
    {
        CreatureGeometryValidation.Validate(request);token.ThrowIfCancellationRequested();var selection=Own(request.Selection);
        var slot=ReserveReader();await using var registration=token.Register(()=>StartReadRetirement(slot));
        try
        {
            var proof=await work.AuthorizeAsync(selection,slot.Cancellation.Token);await CheckCaptureAsync(proof,slot.Cancellation.Token);
            if(proof.Authorization is null)return MetadataResult(selection,proof);
            var retained=await work.FindAsync(proof.Authorization,request.ExpectedGeometryArtifactId,slot.Cancellation.Token);slot.Owner=retained;
            if(retained is null)return Result(selection,await ReauthorizeAsync(selection,proof,slot.Cancellation.Token),CreatureGeometryState.NotPrepared);
            try{slot.Stream=await retained.OpenVerifiedAsync(slot.Cancellation.Token);}
            catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_MISSING")
            {return Result(selection,await ReauthorizeAsync(selection,proof,slot.Cancellation.Token),CreatureGeometryState.NotPrepared);}
            catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_CORRUPT")
            {return Result(selection,await ReauthorizeAsync(selection,proof,slot.Cancellation.Token),CreatureGeometryState.Corrupt,problem:new(error.Code,error.Message,error.Retryable,CreatureGeometryDependency.Artifact));}
            var appearance=await AppearanceAsync(retained,proof.Authorization,slot.Cancellation.Token);
            var result=Result(selection,proof,CreatureGeometryState.Ready,retained.Sources,retained.Manifest,appearance:appearance.Value);
            await retained.FinalizeAsync(proof.Authorization,slot.Cancellation.Token);
            if(appearance.Retained is not null)await appearance.Retained.FinalizeAsync(proof.Authorization,slot.Cancellation.Token);
            slot.Cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch(Exception error)when(CleanupFailure(error)){slot.Failure=error;FailClosed(error);throw;}
        finally{slot.Opened.TrySetResult();StartReadRetirement(slot);await slot.Retired.Task;}
    }
    public async Task<ICreatureGeometryReadLease> OpenAsync(CreatureGeometryOpenRequest request,CancellationToken token)
    {
        CreatureGeometryValidation.Validate(request);token.ThrowIfCancellationRequested();var selection=Own(request.Selection);
        var slot=ReserveReader();await using var registration=token.Register(()=>StartReadRetirement(slot));var transferred=false;
        try
        {
            var proof=await work.AuthorizeAsync(selection,slot.Cancellation.Token);await CheckCaptureAsync(proof,slot.Cancellation.Token);
            if(proof.Authorization is null)throw NotPrepared();
            var retained=await work.FindAsync(proof.Authorization,null,slot.Cancellation.Token)??throw NotPrepared();slot.Owner=retained;
            // The requested artifact is either this geometry (VMG1) or its retained display appearance (VMA1).
            IGeometryStoreAppearance? appearance=null;
            if(retained.Manifest.ArtifactId!=request.GeometryArtifactId)
            {
                appearance=await retained.FindAppearanceAsync(proof.Authorization,slot.Cancellation.Token);
                if(appearance?.Appearance.Manifest?.ArtifactId!=request.GeometryArtifactId)throw Stale();
            }
            try{slot.Stream=appearance is null?await retained.OpenVerifiedAsync(slot.Cancellation.Token):await appearance.OpenVerifiedAsync(slot.Cancellation.Token);}
            catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_MISSING"){throw NotPrepared();}
            var ready=Result(selection,proof,CreatureGeometryState.Ready,retained.Sources,retained.Manifest,appearance:appearance?.Appearance);
            await retained.FinalizeAsync(proof.Authorization,slot.Cancellation.Token);
            if(appearance is not null)await appearance.FinalizeAsync(proof.Authorization,slot.Cancellation.Token);
            lock(slot.Sync)
            {
                slot.Cancellation.Token.ThrowIfCancellationRequested();if(slot.Stopping)throw new OperationCanceledException(slot.Cancellation.Token);
                slot.Stream.Position=0;transferred=true;return new ReadLease(this,slot,ready.Manifest!,ready.Appearance?.Manifest);
            }
        }
        catch(Exception error)when(CleanupFailure(error)){slot.Failure=error;FailClosed(error);throw;}
        finally{slot.Opened.TrySetResult();if(!transferred){StartReadRetirement(slot);await slot.Retired.Task;}}
    }
    // Read reports the retained appearance: Ready after full VMA1 verification, a retained refusal as is, otherwise notPrepared.
    private static async Task<(CreatureGeometryAppearance Value,IGeometryStoreAppearance? Retained)> AppearanceAsync(IGeometryStoreRetained geometry,
        GeometryDisplayAuthorization selection,CancellationToken token)
    {
        var retained=await geometry.FindAppearanceAsync(selection,token);
        if(retained is null)return(GeometryAppearanceSources.NotPrepared(selection),null);
        if(retained.Appearance.Manifest is null)return(retained.Appearance,retained);
        try{await (await retained.OpenVerifiedAsync(token)).DisposeAsync();return(retained.Appearance,retained);}
        catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_MISSING"){return(GeometryAppearanceSources.NotPrepared(selection),null);}
        catch(DiscoveryException error)when(error.Code=="LOCAL_OBJECT_CORRUPT"){return(Corrupt(retained.Appearance),null);}
    }
    private static DiscoveryException NotPrepared()=>new("CREATURE_GEOMETRY_NOT_PREPARED","The exact geometry artifact is not available locally. Prepare the selected slot first.",true);
    private sealed class ReadLease(CreatureGeometryStore owner,ReadSlot slot,CreatureGeometryManifest manifest,CreatureGeometryAppearanceManifest? appearance=null):ICreatureGeometryReadLease
    {
        public CreatureGeometryManifest Manifest=>manifest;
        public CreatureGeometryAppearanceManifest? Appearance=>appearance;
        public Stream Content{get;}=new LeaseStream(slot);
        public Task Retirement=>slot.Retired.Task;
        public ValueTask DisposeAsync(){owner.StartReadRetirement(slot);return new(slot.Retired.Task);}
    }
    // Async-only sequential view. There is one physical operation, no implicit read waiter queue.
    private sealed class LeaseStream(ReadSlot slot):Stream
    {
        public override bool CanRead=>!slot.Stopping;
        public override bool CanSeek=>true;
        public override bool CanWrite=>false;
        public override long Length{get{lock(slot.Sync)return Current().Length;}}
        public override long Position{get{lock(slot.Sync)return Current().Position;}set=>Seek(value,SeekOrigin.Begin);}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            TaskCompletionSource completion;Stream input;
            lock(slot.Sync)
            {
                if(slot.Stopping)throw new ObjectDisposedException(nameof(LeaseStream));
                if(slot.Reading is not null)throw Busy();
                completion=slot.Reading=Signal();input=slot.Stream!;
            }
            try
            {
                using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(token,slot.Cancellation.Token);
                return await input.ReadAsync(buffer[..Math.Min(buffer.Length,65536)],cancellation.Token);
            }
            finally{lock(slot.Sync){slot.Reading=null;completion.TrySetResult();}}
        }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException("Use asynchronous reads.");
        public override void Flush()=>throw new NotSupportedException();
        private Stream Current(){if(slot.Stopping||slot.Stream is null)throw new ObjectDisposedException(nameof(LeaseStream));return slot.Stream;}
        public override long Seek(long offset,SeekOrigin origin)
        {
            lock(slot.Sync)
            {
                var input=Current();if(slot.Reading is not null)throw Busy();
                var position=checked((origin switch{SeekOrigin.Begin=>0L,SeekOrigin.Current=>input.Position,SeekOrigin.End=>input.Length,_=>throw new ArgumentOutOfRangeException(nameof(origin))})+offset);
                if(position<0||position>input.Length)throw new ArgumentOutOfRangeException(nameof(offset));
                return input.Seek(position,SeekOrigin.Begin);
            }
        }
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)throw new NotSupportedException("Dispose the owning geometry lease asynchronously.");}
    }
}
