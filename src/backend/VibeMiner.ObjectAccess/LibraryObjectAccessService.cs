using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// Isolated owner-bound acquisition over one borrowed library. Per-key flights retain consumers independently; all physical
/// transfers use one fetcher, scheduler, host ranking and byte budget. Qualified archive batches reuse bounded merged HTTP planning while retaining each member's live consumers.
/// </summary>
public sealed partial class LibraryObjectAccessService : ILibraryObjectAccess, ILibraryDamagePolicy
{
    private readonly ObjectStore library;
    private readonly ObjectSourceOptions options;
    private readonly LibraryObjectAccessOptions limits;
    private readonly CdnFetcher fetcher;
    private readonly object gate = new();
    private readonly HashSet<View> views = [];
    private readonly HashSet<Operation> operations = [];
    private readonly Dictionary<ObjectKey, Flight> flights = [];
    private readonly Dictionary<ObjectKey, ObjectResult> damaged = [];
    private readonly StoreCommitFeed commits = new();
    private readonly HashSet<StoreCommitSubscription> subscriptions = [];
    private bool closed, damageOverflow;
    private long joinedConsumers;
    internal long JoinedConsumers => Volatile.Read(ref joinedConsumers);
    internal int ActiveFlights { get { lock (gate) return flights.Count; } }
    private Task? closing;

    public LibraryObjectAccessService(ObjectStore library, ICdnClient cdn, ObjectSourceOptions? options = null,
        LibraryObjectAccessOptions? limits = null, ICdnClient? interactiveCdn = null)
    {
        ArgumentNullException.ThrowIfNull(library); ArgumentNullException.ThrowIfNull(cdn);
        if (library.Kind != ObjectStoreKind.Library) throw new ArgumentException("Shared owner acquisition requires a mixed library.", nameof(library));
        this.library = library; this.options = options ?? new(); this.limits = limits ?? new();
        if (this.limits.MaxViews is < 1 or > 32 || this.limits.MaxOperations is < 1 or > 64 || this.limits.MaxBatch is < 1 or > 1024 ||
            this.limits.MaxFlights is < 1 or > 128 || this.limits.MaxKnownDamage is < 1 or > 1024 ||
            this.limits.MaxConsumersPerFlight is < 1 or > 32 || this.limits.MaxSubscriptions is < 1 or > 64 ||
            this.limits.MaxSubscriptionCapacity is < 1 or > 1024 || this.limits.MaxGroups is < 1 or > 8 ||
            this.limits.MaxGroupMembers is < 1 or > 64 || this.limits.MaxStageRetargets is < 1 or > 32 ||
            this.options.InMemoryObjectBytes is < 1 or > (64L << 20) || this.options.InFlightBytes < 1 || this.options.InteractiveInFlightBytes < 1 || this.options.Time is null || this.options.Ranges is null ||
            this.options.Ranges.TargetBytes < 1 || this.options.Ranges.CapBytes < this.options.Ranges.TargetBytes ||
            this.options.Ranges.CapBytes > (64L << 20) || this.options.Ranges.GapBytes < 0 || this.options.Ranges.GapBytes > this.options.Ranges.CapBytes)
            throw new ArgumentException("Library acquisition limits are invalid.");
        fetcher = new(cdn, ObjectVerification.Default, this.options, interactiveCdn, OnClaimed);
    }
    public StreamScheduler Streams => fetcher.Streams;
    internal CdnFetcher Fetcher => fetcher;
    public ILibraryObjectSource ForOwner(string owner, LibraryOwnerScope scope, string producer)
    {
        lock (gate)
        {
            if (closed) throw Error(LibraryObjectAccessCodes.Closed, "The library acquisition service is closed.");
            if (views.Count >= limits.MaxViews) throw Error(LibraryObjectAccessCodes.Busy, "The owner-view budget is in use.");
            var view = new View(this, library.ForOwner(owner, scope, producer, LibraryObjectRole.Original));
            views.Add(view); return view;
        }
    }
    private void OnClaimed(ObjectKey key, StreamScheduler.Ticket ticket)
    {
        lock (gate) if (flights.TryGetValue(key, out var flight) && flight.Consumers.Any(c => c.Priority == FetchPriority.Interactive && !c.Token.IsCancellationRequested)) Streams.Promote(ticket);
    }
    ObjectResult? ILibraryDamagePolicy.Known(ObjectKey key) => Known(key);
    private ObjectResult? Known(ObjectKey key)
    {
        lock (gate)
        {
            if (damaged.TryGetValue(key, out var result)) return result;
            return damageOverflow ? new(key, ObjectState.Refused, Code: LibraryObjectAccessCodes.DamageLimit,
                Message: "Known-damage capacity was exhausted. This service fails closed; library repair is unsupported.") : null;
        }
    }
    ObjectResult ILibraryDamagePolicy.Mark(ObjectKey key, ReadState state, string? problem) => Mark(key, state, problem);
    private ObjectResult Mark(ObjectKey key, ReadState state, string? problem)
    {
        var result = new ObjectResult(key, ObjectState.Refused, Code: state == ReadState.Lost ? LibraryObjectAccessCodes.Lost : LibraryObjectAccessCodes.Corrupt,
            Message: "The indexed library object is damaged or lost; preserve the library for repair.");
        lock (gate)
        {
            if (damaged.ContainsKey(key) || damaged.Count < limits.MaxKnownDamage) damaged[key] = result;
            else damageOverflow = true; // Never evict a damaged key and turn a later index-only hit into a silent success.
        }
        return result;
    }
    private abstract class Operation(View view, CancellationToken caller)
    {
        public readonly View View = view;
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(caller);
        public abstract Task Completion { get; }
        public Task? Canceling;
        public bool Retiring;
    }
    private sealed class Operation<T>(View view, CancellationToken caller) : Operation(view, caller)
    {
        public readonly TaskCompletionSource<T> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task Completion => Done.Task;
    }
    private Task<T> Execute<T>(View view, CancellationToken ct, Func<CancellationToken, Task<T>> work)
    {
        Operation<T> operation;
        lock (gate)
        {
            if (closed || view.Closed) throw Error(LibraryObjectAccessCodes.Closed, "The owner view is closed.");
            ct.ThrowIfCancellationRequested();
            if (operations.Count >= limits.MaxOperations) throw Error(LibraryObjectAccessCodes.Busy, "The operation budget is in use.");
            operation = new(view, ct); operations.Add(operation);
        }
        _ = Task.Run(async () =>
        {
            T result = default!; Exception? failure = null; var token = operation.Stop.Token;
            try { token.ThrowIfCancellationRequested(); result = await work(token).ConfigureAwait(false); }
            catch (Exception e) { failure = e; }
            Task? canceled; lock (gate) { operation.Retiring = true; canceled = operation.Canceling; }
            if (canceled is not null) { try { await canceled.ConfigureAwait(false); } catch (Exception) { } }
            operation.Stop.Dispose();
            lock (gate)
            {
                operations.Remove(operation);
                if (failure is OperationCanceledException) operation.Done.TrySetCanceled(token);
                else if (failure is not null) operation.Done.TrySetException(failure);
                else operation.Done.TrySetResult(result);
            }
        });
        return operation.Done.Task;
    }
    private static LibraryObjectAccessException Error(string code, string message) => new(code, message);
    private void Batch(int count) { if (count > limits.MaxBatch) throw Error(LibraryObjectAccessCodes.Limit, $"A library source batch is at most {limits.MaxBatch} entries."); }
    private static ObjectRequest Freeze(ObjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Locations?.Count > 64) throw Error(LibraryObjectAccessCodes.Limit, "A request has at most 64 archive locations.");
        return request with { Locations = request.Locations is null ? null : Array.AsReadOnly(request.Locations.ToArray()) };
    }
    private static CdnContext? Freeze(CdnContext? cdn)
    {
        if (cdn is null) return null;
        if (cdn.Route is null || cdn.Route.Hosts.Count > 16 || cdn.Archives?.Count > 8192) throw Error(LibraryObjectAccessCodes.Limit, "The CDN context exceeds its route/archive bounds.");
        if (cdn.Route.Hosts.Any(h => h is null || h.Length > 253)) throw new ArgumentException("A CDN host is invalid.", nameof(cdn));
        return new(new CdnRoute(cdn.Route.Hosts.ToArray(), cdn.Route.Path), cdn.Archives is null ? null : Array.AsReadOnly(cdn.Archives.ToArray()));
    }
    private static void Priority(FetchPriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
    }
    private readonly record struct RequestedRange(long Offset, int Length)
    {
        public bool Fits(long length) => Offset >= 0 && Offset <= length && Length <= length - Offset;
    }
    private sealed class Consumer(LibraryObjectOwner owner, ObjectRequest request, FetchPriority priority, CancellationToken token, RequestedRange? range)
    {
        public readonly LibraryObjectOwner Owner = owner;
        public readonly ObjectRequest Request = request;
        public readonly FetchPriority Priority = priority;
        public readonly CancellationToken Token = token;
        public readonly RequestedRange? Range = range;
    }
    private sealed class Flight(ObjectRequest request, CdnContext? cdn)
    {
        public readonly ObjectRequest Request = request;
        public readonly CdnContext? Cdn = cdn;
        public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource<ObjectResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly HashSet<Consumer> Consumers = [];
        public bool Retiring, Finishing;
        public Task? Canceling;
        public long CdnBytes;
        public bool StagingOpened;
        public ObjectOrigin StagingOrigin;
        public TransferGroup? Group;
    }
    private async Task<ObjectResult> EnsureAsync(View view, ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct, Action<long>? transferred = null, RequestedRange? range = null)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Known(request.Key) is { } bad) return bad;
            Flight flight; Consumer? consumer = null; var start = false;
            lock (gate)
            {
                if (!flights.TryGetValue(request.Key, out flight!))
                {
                    if (flights.Count >= limits.MaxFlights) return new(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy, Message: "The shared key-flight budget is in use.");
                    flight = new(request, cdn); flights.Add(request.Key, flight); start = true;
                }
                if (!flight.Retiring)
                {
                    if (flight.Consumers.Count >= limits.MaxConsumersPerFlight) return new(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy, Message: "The key's consumer budget is in use.");
                    if (request.DeclaredLength is { } expected && flight.Request.DeclaredLength is { } actual && expected != actual)
                        return new(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch, Message: "Joined declared lengths disagree.");
                    consumer = new(view.Owner, request, priority, ct, range); flight.Consumers.Add(consumer);
                    if (!start) Interlocked.Increment(ref joinedConsumers);
                    if (priority == FetchPriority.Interactive) _ = fetcher.Join(request.Key, priority);
                }
            }
            if (start) _ = Task.Run(() => ProduceAsync(flight));
            if (consumer is null)
            {
                try { await flight.Done.Task.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                continue;
            }
            return await AwaitConsumerAsync(flight, consumer, start, request, cdn, ct, transferred).ConfigureAwait(false);
        }
    }
    private async Task<ObjectResult> AwaitConsumerAsync(Flight flight, Consumer consumer, bool start, ObjectRequest request,
        CdnContext? cdn, CancellationToken ct, Action<long>? transferred = null)
    {
        try
        {
            var result = await flight.Done.Task.WaitAsync(ct).ConfigureAwait(false);
            if (start) transferred?.Invoke(Interlocked.Read(ref flight.CdnBytes));
            if (!start)
            {
                if (result.IsPresent) return result with { ServedFrom = ServedFrom.Store, Bytes = null };
                if ((result.State is ObjectState.NotFound or ObjectState.Unavailable or ObjectState.Refused or ObjectState.Unsupported) &&
                    result.Code is not (LibraryObjectAccessCodes.Corrupt or LibraryObjectAccessCodes.Lost or LibraryObjectAccessCodes.DamageLimit) &&
                    !SameSource(flight, request, cdn))
                    return new(request.Key, ObjectState.Unavailable, Code: LibraryObjectAccessCodes.ContextUntried,
                        Message: "The joined request failed under another captured source context; this caller's route was not contacted.");
            }
            return result;
        }
        finally { await DetachConsumerAsync(flight, consumer).ConfigureAwait(false); }
    }
    private static bool SameSource(Flight flight, ObjectRequest request, CdnContext? cdn)
    {
        var first = flight.Cdn;
        if (first is null || cdn is null) { if (first is not null || cdn is not null) return false; }
        else if (!first.Route.Equals(cdn.Route) || !(first.Archives ?? []).SequenceEqual(cdn.Archives ?? [])) return false;
        return flight.Request.DeclaredLength == request.DeclaredLength &&
            (flight.Request.Locations ?? []).SequenceEqual(request.Locations ?? []);
    }
    private sealed class FlightProgress(Flight flight) : IProgress<ObjectProgress>
    {
        public void Report(ObjectProgress progress) => Interlocked.Exchange(ref flight.CdnBytes, progress.CdnBytes);
    }
    private async Task ProduceAsync(Flight flight)
    {
        ObjectResult? result = null; Exception? failure = null;
        try
        {
            FetchPriority priority;
            lock (gate) priority = flight.Consumers.Any(c => c.Priority == FetchPriority.Interactive) ? FetchPriority.Interactive : FetchPriority.Background;
            var source = new ObjectSource(library, new HandoffWriter(this, flight), library, ObjectVerification.Default, options, fetcher, this, commits);
            result = (await source.GetKeysAsync([flight.Request], flight.Cdn, priority, new FlightProgress(flight), flight.Stop.Token).ConfigureAwait(false))[0];
        }
        catch (Exception e) { failure = e; }
        Task? canceling; lock (gate) { flight.Finishing = true; flight.Retiring = true; canceling = flight.Canceling; }
        if (canceling is not null) { try { await canceling.ConfigureAwait(false); } catch (Exception) { } }
        flight.Stop.Dispose();
        lock (gate)
        {
            flights.Remove(flight.Request.Key);
            if (failure is OperationCanceledException) flight.Done.TrySetCanceled();
            else if (failure is ObjectStoreException storeFailure) flight.Done.TrySetResult(new(flight.Request.Key, ObjectState.StoreFailed, Code: storeFailure.Code, Message: storeFailure.Message));
            else if (failure is not null) flight.Done.TrySetException(failure);
            else flight.Done.TrySetResult(result!);
        }
    }
    private async Task<ObjectResult> CheckedAsync(ObjectRequest request, ObjectResult acquired, CancellationToken ct)
    {
        if (!acquired.IsPresent) return acquired;
        if (Known(request.Key) is { } bad) return bad;
        try
        {
            var read = await library.ReadRangeAsync(request.Key, 0, Memory<byte>.Empty, ct).ConfigureAwait(false);
            if (read.State != ReadState.Present) return read.State == ReadState.Absent
                ? new(request.Key, ObjectState.StoreFailed, Code: "LIBRARY_ACQUISITION_ABSENT", Message: "The reported object has no committed index entry.")
                : Mark(request.Key, read.State, read.Problem);
            if (request.DeclaredLength is { } declared && declared != read.Info!.Length)
                return new(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch, Message: "The stored length differs from the request.");
            return acquired with { Length = read.Info!.Length, Origin = read.Info.Origin, Bytes = null };
        }
        catch (ObjectStoreException e) { return new(request.Key, ObjectState.StoreFailed, Code: e.Code, Message: e.Message); }
    }
    private async Task<ObjectResult> AttributeAsync(View view, ObjectResult result, CancellationToken ct)
    {
        if (!result.IsPresent) return result;
        try
        {
            if (!(await view.Owner.ClaimIndexedAsync([result.Key], ct).ConfigureAwait(false))[0])
                return new(result.Key, ObjectState.StoreFailed, Code: "LIBRARY_ACQUISITION_ABSENT", Message: "The indexed object disappeared before attribution.");
            return result;
        }
        catch (ObjectStoreException e) { return new(result.Key, ObjectState.StoreFailed, Code: e.Code, Message: e.Message); }
    }
    private async Task<ObjectResult> WholeAsync(View view, ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct)
    {
        var result = await CheckedAsync(request, await EnsureAsync(view, request, cdn, priority, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (!result.IsPresent) return result;
        if (result.Length > options.InMemoryObjectBytes) return await AttributeAsync(view, result, ct).ConfigureAwait(false);
        try
        {
            var read = await library.ReadAsync(request.Key, ct).ConfigureAwait(false);
            if (read.State != ReadState.Present) return Mark(request.Key, read.State, read.Problem);
            result = result with { Bytes = read.Bytes };
        }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Limit) { }
        catch (ObjectStoreException e) { return new(request.Key, ObjectState.StoreFailed, Code: e.Code, Message: e.Message); }
        return await AttributeAsync(view, result, ct).ConfigureAwait(false);
    }
    private async Task<IReadOnlyList<ObjectResult>> ManyAsync(View view, ObjectRequest[] requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken ct)
    {
        var results = new ObjectResult[requests.Length];
        var first = new Dictionary<ObjectKey, int>(); var unique = new List<int>();
        for (var i = 0; i < requests.Length; i++) if (first.TryAdd(requests[i].Key, i)) unique.Add(i);
        unique.Sort((left, right) =>
        {
            var order = requests[left].Order.CompareTo(requests[right].Order);
            return order != 0 ? order : left.CompareTo(right);
        });
        long cdnBytes = 0;
        for (var start = 0; start < unique.Count;)
        {
            ct.ThrowIfCancellationRequested();
            var end = start + 1;
            while (end < unique.Count && requests[unique[end]].Order == requests[unique[start]].Order) end++;
            // Preserve an explicit barrier between Order groups. Each archive window reserves only bounded members;
            // loose requests keep the existing individual-flight path and share the same scheduler and budgets.
            var archive = unique.GetRange(start, end - start).Where(i => requests[i].Key.Type == ObjectKeyType.Encoded &&
                requests[i].Key.Namespace == 0 && requests[i].DeclaredLength is not < 1 &&
                requests[i].Locations is { Count: > 0 } locations && locations.All(l => l.Size <= options.InMemoryObjectBytes)).ToArray();
            var window = Math.Min(limits.MaxGroupMembers, limits.MaxFlights);
            for (var at = 0; at < archive.Length; at += window)
            {
                ct.ThrowIfCancellationRequested();
                var slots = archive.Skip(at).Take(window).ToArray();
                var acquired = await EnsureGroupAsync(view, slots.Select(i => requests[i]).ToArray(), cdn, priority, ct,
                    bytes => Interlocked.Add(ref cdnBytes, bytes)).ConfigureAwait(false);
                for (var n = 0; n < slots.Length; n++)
                    results[slots[n]] = await CheckedAsync(requests[slots[n]], acquired[n], ct).ConfigureAwait(false);
            }
            var loose = unique.GetRange(start, end - start).Where(i => results[i] is null).ToArray();
            var next = -1;
            await Task.WhenAll(Enumerable.Range(0, Math.Min(Math.Min(8, limits.MaxFlights), loose.Length)).Select(async _ =>
            {
                int at;
                while ((at = Interlocked.Increment(ref next)) < loose.Length)
                {
                    var i = loose[at]; results[i] = await CheckedAsync(requests[i], await EnsureAsync(view, requests[i], cdn, priority, ct,
                        bytes => Interlocked.Add(ref cdnBytes, bytes)).ConfigureAwait(false), ct).ConfigureAwait(false);
                }
            })).ConfigureAwait(false);
            start = end;
        }
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= results[first[requests[i].Key]];
            if (results[i].IsPresent && requests[i].DeclaredLength is { } declared && declared != results[i].Length)
                results[i] = new(requests[i].Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch, Message: "The stored length differs from this request occurrence.");
        }
        var present = results.Select((result, i) => (result, i)).Where(p => p.result.IsPresent).ToArray();
        if (present.Length > 0)
        {
            try
            {
                var claimed = await view.Owner.ClaimIndexedAsync(present.Select(p => p.result.Key).ToArray(), ct).ConfigureAwait(false);
                for (var n = 0; n < claimed.Count; n++) if (!claimed[n]) results[present[n].i] = new(present[n].result.Key, ObjectState.StoreFailed, Code: "LIBRARY_ACQUISITION_ABSENT");
            }
            catch (ObjectStoreException e) { foreach (var p in present) results[p.i] = new(p.result.Key, ObjectState.StoreFailed, Code: e.Code, Message: e.Message); }
        }
        progress?.Report(new(requests.Length, requests.Length, Interlocked.Read(ref cdnBytes)));
        return results;
    }
    private async Task<ObjectResult> RangeAsync(View view, ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (request.DeclaredLength is { } declared && (offset > declared || destination.Length > declared - offset))
            throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the declared object length.");
        if (destination.Length > (64 << 20)) throw Error(LibraryObjectAccessCodes.Limit, "A range is at most 64 MiB.");
        var result = await CheckedAsync(request, await EnsureAsync(view, request, cdn, priority, ct, range: new RequestedRange(offset, destination.Length)).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (!result.IsPresent) return result;
        if (result.Length is not { } length || !new RequestedRange(offset, destination.Length).Fits(length))
            throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the actual object length.");
        try
        {
            var read = await library.ReadRangeAsync(request.Key, offset, destination, ct).ConfigureAwait(false);
            if (read.State != ReadState.Present) return Mark(request.Key, read.State, read.Problem);
        }
        catch (ObjectStoreException e) { return new(request.Key, ObjectState.StoreFailed, Code: e.Code, Message: e.Message); }
        return await AttributeAsync(view, result, ct).ConfigureAwait(false);
    }
    private static async Task RetireAsync(Operation operation)
    {
        try { await operation.Completion.ConfigureAwait(false); } catch (Exception) { }
    }
    private Task CloseView(View view)
    {
        lock (gate)
        {
            if (view.Closing is not null) return view.Closing;
            view.Closed = true;
            foreach (var subscription in view.Subscriptions.ToArray()) subscription.Dispose();
            var own = operations.Where(o => ReferenceEquals(o.View, view)).ToArray();
            foreach (var op in own) if (!op.Retiring) op.Canceling ??= op.Stop.CancelAsync();
            view.Closing = RetireViewAsync(view, own); return view.Closing;
        }
    }
    private async Task RetireViewAsync(View view, Operation[] own)
    {
        await Task.WhenAll(own.Select(RetireAsync)).ConfigureAwait(false);
        lock (gate) views.Remove(view);
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (closing is not null) return new(closing);
            closed = true;
            foreach (var view in views) view.Closed = true;
            views.Clear();
            var own = operations.ToArray();
            foreach (var op in own) if (!op.Retiring) op.Canceling ??= op.Stop.CancelAsync();
            closing = CloseServiceAsync(own); return new(closing);
        }
    }
    private async Task CloseServiceAsync(Operation[] own)
    {
        await Task.WhenAll(own.Select(RetireAsync)).ConfigureAwait(false);
        lock (gate) foreach (var subscription in subscriptions.ToArray()) subscription.Dispose();
    }
    private StoreCommitSubscription Subscribe(View view, int capacity)
    {
        lock (gate)
        {
            if (closed || view.Closed) throw Error(LibraryObjectAccessCodes.Closed, "The owner view is closed.");
            if (capacity is < 1 || capacity > limits.MaxSubscriptionCapacity) throw Error(LibraryObjectAccessCodes.Limit, "The subscription capacity exceeds its bound.");
            if (subscriptions.Count >= limits.MaxSubscriptions) throw Error(LibraryObjectAccessCodes.Busy, "The shared commit subscription budget is in use.");
            var subscription = commits.Subscribe(capacity, s =>
            {
                lock (gate) { subscriptions.Remove(s); view.Subscriptions.Remove(s); }
            });
            subscriptions.Add(subscription); view.Subscriptions.Add(subscription); return subscription;
        }
    }
    private sealed class View(LibraryObjectAccessService service, LibraryObjectOwner owner) : ILibraryObjectSource
    {
        public readonly LibraryObjectOwner Owner = owner;
        public bool Closed;
        public Task? Closing;
        public readonly HashSet<StoreCommitSubscription> Subscriptions = [];
        public StoreCommitSubscription Subscribe(int capacity = StoreCommitFeed.DefaultCapacity) => service.Subscribe(this, capacity);
        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
        { Priority(priority); var frozen = Freeze(request); var route = Freeze(cdn); return service.Execute(this, cancellationToken, ct => service.WholeAsync(this, frozen, route, priority, ct)); }
        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
        {
            Priority(priority); ArgumentNullException.ThrowIfNull(requests); service.Batch(requests.Count);
            var frozen = requests.Select(Freeze).ToArray(); var route = Freeze(cdn);
            return service.Execute(this, cancellationToken, ct => service.ManyAsync(this, frozen, route, priority, progress, ct));
        }
        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
        { Priority(priority); var frozen = Freeze(request); var route = Freeze(cdn); return service.Execute(this, cancellationToken, ct => service.RangeAsync(this, frozen, offset, destination, route, priority, ct)); }
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(keys); service.Batch(keys.Count); var copy = keys.ToArray();
            return service.Execute(this, cancellationToken, ct => Presence.HasAsync(service.library, copy, ct));
        }
        public async Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
            (await HasKeysAsync(keys, cancellationToken).ConfigureAwait(false)).LongCount(p => p);
        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            service.Execute(this, cancellationToken, async ct => await service.library.ReadHistoryAsync(query, ct).ConfigureAwait(false));
        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) =>
            service.Execute(this, cancellationToken, ct => Task.FromResult(new ObservationResult(ObjectState.Unsupported, Code: LibraryObjectAccessCodes.ObservationsUnsupported)));
        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) =>
            service.Execute(this, cancellationToken, ct => Task.FromResult(new ObservationResult(ObjectState.Unsupported, Code: LibraryObjectAccessCodes.ObservationsUnsupported)));
        public ValueTask DisposeAsync() => new(service.CloseView(this));
    }
}
