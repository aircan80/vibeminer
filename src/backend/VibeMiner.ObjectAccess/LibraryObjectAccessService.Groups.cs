using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

public sealed partial class LibraryObjectAccessService
{
    private readonly HashSet<TransferGroup> groups = [];
    internal int ActiveGroups { get { lock (gate) return groups.Count; } }

    private sealed class TransferGroup(ObjectRequest[] requests, CdnContext? cdn)
    {
        public readonly ObjectRequest[] Requests = requests;
        public readonly CdnContext? Cdn = cdn;
        public readonly Dictionary<ObjectKey, Flight> Members = [];
        public readonly HashSet<ObjectKey> Excluded = [];
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Canceling;
        public bool Finishing;
        public long CdnBytes;
    }
    private bool Live(Flight flight) => flight.Consumers.Any(c => !c.Token.IsCancellationRequested);

    private async Task DetachConsumerAsync(Flight flight, Consumer consumer)
    {
        Task? drain = null;
        lock (gate)
        {
            flight.Consumers.Remove(consumer);
            if (!Live(flight) && !flight.Done.Task.IsCompleted)
            {
                flight.Retiring = true;
                if (flight.Group is { } group)
                {
                    if (!group.Members.Values.Any(Live))
                    {
                        if (!group.Finishing) group.Canceling ??= group.Stop.CancelAsync();
                        drain = group.Retired.Task;
                    }
                }
                else
                {
                    if (!flight.Finishing) flight.Canceling ??= flight.Stop.CancelAsync();
                    drain = flight.Done.Task;
                }
            }
        }
        if (drain is not null) { try { await drain.ConfigureAwait(false); } catch (Exception) { } }
    }

    // A bounded window reserves all new per-key flights before scheduling one planner invocation. Existing flights remain
    // independent consumers and their advertised intervals remain holes in this invocation's archive ranges.
    private async Task<ObjectResult[]> EnsureGroupAsync(View view, ObjectRequest[] requests, CdnContext? cdn,
        FetchPriority priority, CancellationToken ct, Action<long> transferred)
    {
        var waits = new Task<ObjectResult>[requests.Length];
        TransferGroup? group = null;
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < requests.Length; i++)
            {
                var request = requests[i];
                if (Known(request.Key) is { } bad) { waits[i] = Task.FromResult(bad); continue; }
                var fresh = !flights.TryGetValue(request.Key, out var flight);
                if (fresh)
                {
                    if (flights.Count >= limits.MaxFlights || (group is null && groups.Count >= limits.MaxGroups))
                    { waits[i] = Task.FromResult(new ObjectResult(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy, Message: "The archive group or key-flight budget is in use.")); continue; }
                    if (group is null) { group = new(requests, cdn); groups.Add(group); }
                    flight = new(request, cdn) { Group = group };
                    flights.Add(request.Key, flight); group.Members.Add(request.Key, flight);
                }
                if (flight!.Retiring)
                {
                    // Retry only after the previous physical producer has retired; never start overlapping staging work.
                    waits[i] = RetryRetiredAsync(flight, view, request, cdn, priority, ct, transferred); continue;
                }
                if (flight.Consumers.Count >= limits.MaxConsumersPerFlight)
                { waits[i] = Task.FromResult(new ObjectResult(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy)); continue; }
                if (request.DeclaredLength is { } expected && flight.Request.DeclaredLength is { } actual && expected != actual)
                { waits[i] = Task.FromResult(new ObjectResult(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch)); continue; }
                var consumer = new Consumer(view.Owner, request, priority, ct, null); flight.Consumers.Add(consumer);
                if (!fresh) Interlocked.Increment(ref joinedConsumers);
                if (priority == FetchPriority.Interactive) _ = fetcher.Join(request.Key, priority);
                waits[i] = AwaitConsumerAsync(flight, consumer, fresh, request, cdn, ct);
            }
            if (group is not null)
                foreach (var request in requests) if (!group.Members.ContainsKey(request.Key)) group.Excluded.Add(request.Key);
        }
        if (group is not null) _ = Task.Run(() => ProduceGroupAsync(group));
        var results = await Task.WhenAll(waits).ConfigureAwait(false);
        // The initiator accounts for a physical response once, irrespective of member count or later joins.
        if (group is not null) transferred(Interlocked.Read(ref group.CdnBytes));
        return results;
    }
    private async Task<ObjectResult> RetryRetiredAsync(Flight flight, View view, ObjectRequest request, CdnContext? cdn,
        FetchPriority priority, CancellationToken ct, Action<long> transferred)
    {
        try { await flight.Done.Task.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return await EnsureAsync(view, request, cdn, priority, ct, transferred).ConfigureAwait(false);
    }
    private sealed class GroupProgress(TransferGroup group) : IProgress<ObjectProgress>
    { public void Report(ObjectProgress progress) => Interlocked.Exchange(ref group.CdnBytes, progress.CdnBytes); }

    private async Task ProduceGroupAsync(TransferGroup group)
    {
        ObjectResult[]? results = null; Exception? failure = null;
        try
        {
            limits.Fault?.Invoke(LibraryGroupPoint.BeforeAdmission);
            group.Stop.Token.ThrowIfCancellationRequested();
            var sink = new GroupSink(this, group);
            if (group.Cdn is null)
            {
                var list = new List<ObjectResult>();
                foreach (var request in group.Requests)
                    list.Add(await sink.AlreadyHaveAsync(request.Key, group.Stop.Token).ConfigureAwait(false) ??
                        new(request.Key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT"));
                results = list.ToArray();
            }
            else
            {
                FetchPriority priority;
                lock (gate) priority = group.Members.Values.Any(f => f.Consumers.Any(c => c.Priority == FetchPriority.Interactive && !c.Token.IsCancellationRequested))
                    ? FetchPriority.Interactive : FetchPriority.Background;
                results = await fetcher.FetchAsync(group.Requests, group.Cdn, priority, sink, new GroupProgress(group), group.Stop.Token, group.Excluded).ConfigureAwait(false);
            }
        }
        catch (Exception e) { failure = e; }
        Task? canceling;
        lock (gate)
        {
            group.Finishing = true; canceling = group.Canceling;
            foreach (var member in group.Members.Values) { member.Finishing = true; member.Retiring = true; }
        }
        if (canceling is not null) { try { await canceling.ConfigureAwait(false); } catch (Exception) { } }
        group.Stop.Dispose();
        lock (gate)
        {
            groups.Remove(group);
            for (var i = 0; i < group.Requests.Length; i++)
            {
                if (!group.Members.TryGetValue(group.Requests[i].Key, out var flight)) continue;
                flight.Stop.Dispose(); flights.Remove(flight.Request.Key);
                if (failure is OperationCanceledException) flight.Done.TrySetCanceled();
                else if (failure is ObjectStoreException storeFailure) flight.Done.TrySetResult(new(flight.Request.Key, ObjectState.StoreFailed, Code: storeFailure.Code, Message: storeFailure.Message));
                else if (failure is not null) flight.Done.TrySetException(failure);
                else flight.Done.TrySetResult(results![i]);
            }
            group.Retired.TrySetResult();
        }
    }

    private sealed class GroupSink(LibraryObjectAccessService service, TransferGroup group) : IAdmissionRecheckingSink
    {
        public async ValueTask<ObjectResult?> AlreadyHaveAsync(ObjectKey key, CancellationToken ct)
        {
            lock (service.gate)
                if (!group.Members.TryGetValue(key, out var flight) || !service.Live(flight))
                    return new(key, ObjectState.StoreFailed, Code: "LIBRARY_ACQUISITION_MEMBER_RETIRED");
            return service.Known(key) ?? (await service.library.HasAsync(key, ct).ConfigureAwait(false) ? new ObjectResult(key, ObjectState.Present, ServedFrom.Store) : null);
        }
        public async Task<IReadOnlyList<ObjectResult>> DeliverAsync(IReadOnlyList<VerifiedObject> objects, bool urgent, CancellationToken ct)
        {
            var results = new ObjectResult[objects.Count];
            var owners = new Dictionary<LibraryObjectOwner, List<int>>();
            for (var i = 0; i < objects.Count; i++)
            {
                var item = objects[i]; LibraryObjectOwner? owner;
                try { owner = service.HandoffOwner(group.Members[item.Request.Key], item.Bytes.Length); }
                catch (OperationCanceledException) { results[i] = new(item.Request.Key, ObjectState.StoreFailed, Code: "LIBRARY_ACQUISITION_MEMBER_RETIRED"); continue; }
                if (owner is null) { results[i] = new(item.Request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch); continue; }
                if (!owners.TryGetValue(owner, out var indices)) owners.Add(owner, indices = []);
                indices.Add(i);
            }
            foreach (var (owner, indices) in owners)
            {
                // Captured immutable owners are the accepted handoffs; cancellation afterward cannot rewrite attribution.
                var delivered = await ObjectSource.DeliverVerifiedAsync(owner, service.commits, indices.Select(i => objects[i]).ToArray(),
                    ObjectOrigin.Cdn, ServedFrom.Cdn, ct, urgent, withBytes: false).ConfigureAwait(false);
                for (var n = 0; n < indices.Count; n++) results[indices[n]] = delivered[n];
            }
            return results;
        }
        public Task<ObjectResult> StreamLargeAsync(ObjectRequest request, long length,
            Func<long, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task<CdnReply?>> fetch, CancellationToken ct)
        {
            var source = new ObjectSource(service.library, new HandoffWriter(service, group.Members[request.Key]), service.library,
                ObjectVerification.Default, service.options, service.fetcher, service, service.commits);
            return source.SharedSink.StreamLargeAsync(request, length, fetch, ct);
        }
    }
}
