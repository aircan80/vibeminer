using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class CreatureGeometryStore
{
    private sealed class Group(CreatureDisplayMetadataRequest request, GeometryProducerPayload payload, IDisposable capture)
    {
        internal readonly CreatureDisplayMetadataRequest Request = request;
        internal readonly GeometryProducerPayload Payload = payload;
        internal readonly IDisposable Capture = capture;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly List<Consumer> Consumers = new(2);
        internal readonly TaskCompletionSource Initialized = Signal(), WorkerRetired = Signal(), Retired = Signal();
        internal readonly TaskCompletionSource<IGeometryStorePublication?> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal GeometryDisplayAuthorizationResult? Anchor, Resolved;
        internal Task? Work, Retirement;
        internal TaskCompletionSource? Notification;
        internal IGeometryStorePublication? Publication;
        internal int Captures;
        internal bool Closing;
        internal Exception? Failure;
    }
    private sealed class Consumer(Group group, Func<CreatureGeometryProgress,Task> callback, TimeProvider clock)
    {
        internal readonly Group Group = group;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly CreatureGeometryStoreProgress Progress = new(callback,clock);
        internal readonly object Lifetime = new();
        internal readonly TaskCompletionSource BodyRetired=Signal();
        internal TaskCompletionSource? Notification;
        internal GeometryDisplayAuthorizationResult? Baseline;
        internal bool Active = true, Finished;
    }
    public async Task<CreatureGeometryResult> PrepareAsync(CreatureGeometryRequest request,
        Func<CreatureGeometryProgress,Task> progress, CancellationToken token)
    {
        CreatureGeometryValidation.Validate(request); ArgumentNullException.ThrowIfNull(progress); token.ThrowIfCancellationRequested();
        var selection=Own(request.Selection); Consumer consumer; Group group;
        lock(gate)
        {
            RequireOpen();
            if(producer is null)
            {
                var reserved=payload.ReserveProducer();IDisposable capture;
                try{capture=reserved.Reserve(16L<<20);}catch{reserved.Dispose();throw;}
                producer=new(selection,reserved,capture);
            }
            group=producer;
            if(group.Closing||group.Request!=selection||group.Consumers.Count==2||group.Captures==2)throw Busy();
            consumer=new(group,progress,clock);group.Consumers.Add(consumer);group.Captures++;
        }
        using var cancellation=token.Register(()=>Stop(consumer));
        try
        {
            await producerCapture.WaitAsync(consumer.Cancellation.Token);
            try
            {
                consumer.Baseline=await work.AuthorizeAsync(selection,consumer.Cancellation.Token);
                await CheckCaptureAsync(consumer.Baseline,consumer.Cancellation.Token);
                GeometryDisplayAuthorizationResult anchor;
                lock(gate)
                {
                    if(!consumer.Active||closed||group.Closing)throw new OperationCanceledException(consumer.Cancellation.Token);
                    anchor=group.Anchor??=consumer.Baseline;
                }
                if(!ReferenceEquals(anchor,consumer.Baseline))await CompareAsync(anchor,consumer.Baseline,true,consumer.Cancellation.Token);
            }
            finally{producerCapture.Release();}
            lock(gate)
            {
                if(!consumer.Active||closed||group.Closing)throw new OperationCanceledException(consumer.Cancellation.Token);
                if(group.Work is null){group.Work=RunGroupAsync(group);group.Initialized.TrySetResult();}
            }
            var publication=await group.Ready.Task.WaitAsync(consumer.Cancellation.Token);
            var current=await ReauthorizeProducerAsync(selection,consumer.Baseline,consumer.Cancellation.Token,group.Resolved!);
            if(publication is null)
            {
                if(current.Metadata.State==CreatureDisplayMetadataState.NotPrepared&&group.Resolved!.Problem is not null)
                    current=current with{Metadata=group.Resolved.Metadata,Problem=group.Resolved.Problem};
                return MetadataResult(selection,current);
            }
            if(current.Authorization is null)throw Stale();
            // Complete bounded output before the final transaction; canonical-first substitutions below
            // have the same finite shape and are returned directly, without a second catalog lookup.
            _=Result(selection,current,publication.State,publication.Sources,publication.Manifest,publication.Problem);
            var final=await publication.FinalizeAsync(current.Authorization,consumer.Cancellation.Token);
            consumer.Cancellation.Token.ThrowIfCancellationRequested();
            return Result(selection,current,publication.State,final.Sources,final.Manifest,publication.Problem);
        }
        finally{consumer.BodyRetired.TrySetResult();await RetireConsumerAsync(consumer);}
    }
    private async Task RunGroupAsync(Group group)
    {
        await Task.Yield();
        try
        {
            var token=group.Cancellation.Token;var resolved=group.Anchor!;
            if(resolved.Metadata.State==CreatureDisplayMetadataState.NotPrepared)
            {
                var prepared=await work.EnsureMetadataAsync(group.Request,p=>Broadcast(group,p),token);
                resolved=await ReauthorizeProducerAsync(group.Request,resolved,token);
                if(prepared.State==CreatureDisplayMetadataState.MetadataResolved&&resolved.Authorization is null)throw Stale();
                if(resolved.Metadata.State==CreatureDisplayMetadataState.NotPrepared&&prepared.Problem is not null)
                    resolved=resolved with{Metadata=resolved.Metadata with{State=prepared.State,Absence=prepared.Absence,
                        ModelId=prepared.ModelId,ModelFileDataId=prepared.ModelFileDataId,RequiredTableId=prepared.RequiredTableId},Problem=prepared.Problem};
            }
            group.Resolved=resolved;
            if(resolved.Authorization is not null)
                group.Publication=await TryReuseAsync(resolved.Authorization,token)??await work.ProduceAsync(new SelectionFence(this,group.Request,resolved),group.Payload,p=>Broadcast(group,p),token);
            group.Ready.TrySetResult(group.Publication);
        }
        catch(Exception error)
        { if(CleanupFailure(error)){group.Failure=error;FailClosed(error);}group.Ready.TrySetException(error); }
    }
    private Task Broadcast(Group group,CreatureGeometryProgress progress)
    {
        Consumer[] current;lock(gate)current=group.Consumers.Where(c=>c.Active).ToArray();
        foreach(var consumer in current)consumer.Progress.Post(progress);return Task.CompletedTask;
    }
    private void Stop(Consumer consumer)
    {
        TaskCompletionSource notification;
        lock(consumer.Lifetime)
        {
            if(consumer.Finished||consumer.Notification is not null)return;
            consumer.Notification=notification=Signal();
        }
        lock(gate)consumer.Active=false;
        _=NotifyAsync();MaybeStop(consumer.Group);
        async Task NotifyAsync(){try{await ObserveCancellationAsync(consumer.Cancellation);}finally{notification.TrySetResult();}}
    }
    private void MaybeStop(Group group)
    {
        TaskCompletionSource notification;Task[] bodies;
        lock(gate)
        {
            if(group.Consumers.Any(c=>c.Active)||group.Notification is not null)return;
            group.Closing=true;group.Notification=notification=Signal();bodies=group.Consumers.Select(c=>c.BodyRetired.Task).ToArray();
            if(group.Work is null)group.Initialized.TrySetResult();
        }
        _=StopCoreAsync();
        async Task StopCoreAsync()
        {
            try
            {
                await ObserveCancellationAsync(group.Cancellation);notification.TrySetResult();await group.Initialized.Task;
                if(group.Work is not null)await group.Work;
                else group.Ready.TrySetResult(null);
                // Observe Ready even when every waiting consumer withdrew before producer failure.
                try{await group.Ready.Task;}catch(Exception){}
                await Task.WhenAll(bodies);
                if(group.Publication is not null)await group.Publication.DisposeAsync();
            }
            catch(Exception error){group.Failure=error;FailClosed(error);}
            finally{notification.TrySetResult();group.WorkerRetired.TrySetResult();}
        }
    }
    private async Task RetireConsumerAsync(Consumer consumer)
    {
        var group=consumer.Group;
        lock(gate)consumer.Active=false;MaybeStop(group);
        try
        {
            Task notification;lock(consumer.Lifetime){consumer.Finished=true;notification=consumer.Notification?.Task??Task.CompletedTask;}
            await notification;await consumer.Progress.CloseAsync();
            bool lastActive;lock(gate)lastActive=!group.Consumers.Any(c=>c.Active);
            if(lastActive)await group.WorkerRetired.Task;
        }
        finally
        {
            consumer.Cancellation.Dispose();
            lock(gate)
            {
                if(group.Anchor is null||!ReferenceEquals(group.Anchor,consumer.Baseline))group.Captures--;
                consumer.Baseline=null;group.Consumers.Remove(consumer);
                if(group.Consumers.Count==0)group.Retirement??=RetireGroupAsync(group);
            }
        }
        Task? retirement;lock(gate)retirement=group.Consumers.Count==0?group.Retirement:null;
        if(retirement is not null)await retirement;
        if(group.Failure is not null)throw Cleanup();
    }
    private async Task RetireGroupAsync(Group group)
    {
        await Task.Yield();await group.WorkerRetired.Task;
        try
        {
            if(group.Failure is not null)throw Cleanup();
            group.Anchor=null;group.Resolved=null;group.Publication=null;
            group.Capture.Dispose();group.Payload.Dispose();group.Cancellation.Dispose();
            lock(gate){if(ReferenceEquals(producer,group))producer=null;}
        }
        catch(Exception error){FailClosed(error);group.Failure=error;}
        finally{group.Retired.TrySetResult();}
    }
}
