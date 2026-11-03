using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess.Testing;

/// <summary>Managed shared-owner acquisition model for upper tests. One transfer, scheduler and immutable claim per view.</summary>
public sealed partial class InMemoryLibraryObjectAccess : ILibraryObjectAccess
{
    private sealed record Claim(string Owner, LibraryOwnerScope Scope, string Producer);
    private sealed class Consumer(Claim claim, long? declaredLength, long minimumLength)
    { public readonly Claim Claim = claim; public readonly long? DeclaredLength = declaredLength; public readonly long MinimumLength = minimumLength; }
    private sealed record Stored(byte[] Bytes, ObjectOrigin Origin, string? Damage = null);
    private sealed class Flight(ObjectRequest request, CdnContext context, Claim claim, StreamScheduler.Ticket ticket)
    {
        public readonly ObjectRequest Request = request; public readonly CdnContext Context = context; public readonly Claim Claim = claim;
        public readonly StreamScheduler.Ticket Ticket = ticket; public readonly CancellationTokenSource Stop = new();
        public readonly TaskCompletionSource<ObjectResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Work = Task.CompletedTask; public int Consumers; public ArchiveGroup? Group;
        public readonly List<Consumer> LiveConsumers = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<ObjectKey, byte[]> available = [];
    private readonly Dictionary<ObjectKey, Stored> stored = [];
    private readonly Dictionary<ObjectKey, string> damage = [];
    private readonly Dictionary<Claim, HashSet<ObjectKey>> claims = [];
    private readonly Dictionary<ObjectKey, Flight> flights = [];
    private readonly HashSet<View> views = [];
    private readonly StoreCommitFeed commits = new();
    private int subscriptions;
    private readonly List<(ObjectKey Key, CdnContext Context)> fetched = [];
    private readonly LibraryObjectAccessOptions limits;
    private readonly ObjectSourceOptions options;
    private bool closed, damageOverflow; private int operations;
    private long joins;
    public long JoinedConsumers => Volatile.Read(ref joins);
    public InMemoryLibraryObjectAccess(ObjectSourceOptions? options = null, LibraryObjectAccessOptions? limits = null)
    {
        this.limits = limits ?? new(); this.options = options ?? new();
        if (this.limits.MaxViews is < 1 or > 32 || this.limits.MaxOperations is < 1 or > 64 || this.limits.MaxBatch is < 1 or > 1024 ||
            this.limits.MaxFlights is < 1 or > 128 || this.limits.MaxKnownDamage is < 1 or > 1024 || this.limits.MaxConsumersPerFlight is < 1 or > 32 ||
            this.limits.MaxGroups is < 1 or > 8 || this.limits.MaxGroupMembers is < 1 or > 64 ||
            this.limits.MaxSubscriptions is < 1 or > 64 || this.limits.MaxSubscriptionCapacity is < 1 or > 1024 || this.limits.MaxStageRetargets is < 1 or > 32 ||
            this.options.Ranges.CapBytes > (64L << 20) || this.options.Ranges.TargetBytes < 1 || this.options.Ranges.CapBytes < this.options.Ranges.TargetBytes ||
            this.options.Ranges.GapBytes < 0 || this.options.Ranges.GapBytes > this.options.Ranges.CapBytes ||
            this.options.InMemoryObjectBytes is < 1 or > (64L << 20) || this.options.InFlightBytes < 1 || this.options.InteractiveInFlightBytes < 1)
            throw new ArgumentException("Invalid managed acquisition bounds.");
        Streams = new(this.options.Streams);
    }
    public StreamScheduler Streams { get; }
    public Func<ObjectRequest, CdnContext, CancellationToken, Task>? BeforeFetch { get; set; }
    public Func<string, ObjectKey, CancellationToken, Task>? BeforeClaim { get; set; }
    public IReadOnlyList<(ObjectKey Key, CdnContext Context)> Fetched { get { lock (gate) return fetched.ToArray(); } }
    public InMemoryLibraryObjectAccess Add(PutObject item, bool alreadyStored = false, ObjectOrigin origin = ObjectOrigin.Cdn)
    { lock (gate) { available[item.Key] = item.Bytes.ToArray(); if (alreadyStored) stored[item.Key] = new(item.Bytes.ToArray(), origin); return this; } }
    public bool Owns(string owner, LibraryOwnerScope scope, string producer, ObjectKey key)
    { lock (gate) return claims.TryGetValue(new(owner, scope, producer), out var keys) && keys.Contains(key); }
    public void MarkDamaged(ObjectKey key, bool lost = false)
    { lock (gate) { var item = stored[key]; stored[key] = item with { Damage = lost ? LibraryObjectAccessCodes.Lost : LibraryObjectAccessCodes.Corrupt }; } }
    private static ObjectRequest Freeze(ObjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Locations?.Count > 64) throw Error(LibraryObjectAccessCodes.Limit);
        return request with { Locations = request.Locations is null ? null : Array.AsReadOnly(request.Locations.ToArray()) };
    }
    private static CdnContext? Freeze(CdnContext? context)
    {
        if (context is null) return null;
        if (context.Route is null || context.Route.Hosts.Count > 16 || context.Archives?.Count > 8192) throw Error(LibraryObjectAccessCodes.Limit);
        if (context.Route.Hosts.Any(host => host is null || host.Length > 253)) throw new ArgumentException("Invalid CDN host.", nameof(context));
        return new(new CdnRoute(context.Route.Hosts.ToArray(), context.Route.Path), context.Archives is null ? null : Array.AsReadOnly(context.Archives.ToArray()));
    }
    private static bool SameSource(Flight flight, ObjectRequest request, CdnContext context) =>
        flight.Context.Route.Equals(context.Route) && (flight.Context.Archives ?? []).SequenceEqual(context.Archives ?? []) &&
        flight.Request.DeclaredLength == request.DeclaredLength && (flight.Request.Locations ?? []).SequenceEqual(request.Locations ?? []);
    private static LibraryObjectAccessException Error(string code) => new(code, "The managed shared acquisition request was refused.");
    public ILibraryObjectSource ForOwner(string owner, LibraryOwnerScope scope, string producer)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(owner) > 512 ||
            string.IsNullOrWhiteSpace(producer) || producer.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(producer) > 128 || !Enum.IsDefined(scope)) throw new ArgumentException("Invalid captured owner.");
        lock (gate) { if (closed) throw Error(LibraryObjectAccessCodes.Closed); if (views.Count >= limits.MaxViews) throw Error(LibraryObjectAccessCodes.Busy); var view = new View(this, new(owner, scope, producer)); views.Add(view); return view; }
    }
    public async ValueTask DisposeAsync()
    {
        View[] snapshot; lock (gate) { closed = true; snapshot = views.ToArray(); }
        foreach (var view in snapshot) await view.DisposeAsync();
    }
    private void Credit(Claim claim, ObjectKey key)
    { if (!claims.TryGetValue(claim, out var keys)) claims.Add(claim, keys = []); keys.Add(key); }
    private async Task ProduceAsync(Flight flight)
    {
        try
        {
            using var stream = await Streams.AcquireAsync(flight.Ticket, flight.Stop.Token);
            lock (gate) fetched.Add((flight.Request.Key, flight.Context));
            if (BeforeFetch is { } before) await before(flight.Request, flight.Context, flight.Stop.Token);
            flight.Stop.Token.ThrowIfCancellationRequested(); ObjectResult result; var published = false;
            lock (gate)
            {
                if (!TrySourceBytes(flight.Request, flight.Context, out var bytes)) result = new(flight.Request.Key, ObjectState.NotFound, Code: "CDN_NOT_FOUND", ObservedAt: DateTimeOffset.UtcNow);
                else if (flight.Request.DeclaredLength is { } length && length != bytes.Length) result = new(flight.Request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                else if (ObjectVerification.Default.Check(flight.Request.Key, bytes, flight.Request.DeclaredLength ?? bytes.Length) is { } refusal)
                    result = new(flight.Request.Key, ObjectState.Refused, Code: refusal.Code, Message: refusal.Message);
                else
                {
                    var actualOwner = flight.LiveConsumers.FirstOrDefault(consumer => consumer.MinimumLength <= bytes.LongLength && (consumer.DeclaredLength is null || consumer.DeclaredLength == bytes.LongLength));
                    if (actualOwner is null) result = new(flight.Request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                    else
                    {
                        published = stored.TryAdd(flight.Request.Key, new(bytes, ObjectOrigin.Cdn)); Credit(actualOwner.Claim, flight.Request.Key);
                        var committed = stored[flight.Request.Key]; result = new(flight.Request.Key, ObjectState.Present, ServedFrom.Cdn, committed.Origin, committed.Bytes.LongLength);
                    }
                }
            }
            if (published) commits.Publish([flight.Request.Key], []);
            stream.Complete(null); flight.Done.TrySetResult(result);
        }
        catch (OperationCanceledException) { flight.Done.TrySetCanceled(flight.Stop.Token); }
        catch (Exception e) { flight.Done.TrySetException(e); }
        finally { lock (gate) if (flights.TryGetValue(flight.Request.Key, out var current) && ReferenceEquals(current, flight)) flights.Remove(flight.Request.Key); }
    }
    private async Task<ObjectResult> GetAsync(Claim claim, ObjectRequest request, CdnContext? context, FetchPriority priority, bool bytes, CancellationToken ct, long minimumLength = 0)
    {
        var consumer = new Consumer(claim, request.DeclaredLength, minimumLength);
        Flight? flight = null; ObjectResult? result = null; var joined = false;
        lock (gate)
        {
            if (damage.TryGetValue(request.Key, out var code)) return new(request.Key, ObjectState.Refused, Code: code);
            if (damageOverflow) return new(request.Key, ObjectState.Refused, Code: LibraryObjectAccessCodes.DamageLimit);
            if (stored.TryGetValue(request.Key, out var hit))
            {
                if (hit.Damage is { } found)
                {
                    if (damage.Count >= limits.MaxKnownDamage) damageOverflow = true;
                    else damage[request.Key] = found;
                    return new(request.Key, ObjectState.Refused, Code: found);
                }
                if (request.DeclaredLength is { } length && length != hit.Bytes.Length) return new(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                result = new(request.Key, ObjectState.Present, ServedFrom.Store, hit.Origin, hit.Bytes.LongLength);
            }
            else if (context is null) return new(request.Key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT");
            else
            {
                if (!flights.TryGetValue(request.Key, out flight))
                {
                    if (flights.Count >= limits.MaxFlights) return new(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy);
                    var captured = new CdnContext(new CdnRoute(context.Route.Hosts.ToArray(), context.Route.Path), context.Archives?.ToArray());
                    var created = new Flight(request with { Locations = request.Locations?.ToArray() }, captured, claim, Streams.NewTicket(priority));
                    flight = created; flights.Add(request.Key, created); created.Work = Task.Run(() => ProduceAsync(created));
                }
                else if (flight.Consumers > 0 || flight.Group is null || flight.Group.Claim != claim) { joined = true; Interlocked.Increment(ref joins); }
                if (flight.Consumers >= limits.MaxConsumersPerFlight) return new(request.Key, ObjectState.StoreFailed, Code: LibraryObjectAccessCodes.Busy);
                if (request.DeclaredLength is { } expected && flight.Request.DeclaredLength is { } declared && expected != declared)
                    return new(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                if (priority == FetchPriority.Interactive) Streams.Promote(flight.Ticket); flight.Consumers++; flight.LiveConsumers.Add(consumer);
            }
        }
        if (flight is not null)
        {
            try
            {
                result = await flight.Done.Task.WaitAsync(ct);
                if (joined && result.IsPresent) result = result with { ServedFrom = ServedFrom.Store };
                else if (joined && context is not null && result.State is ObjectState.NotFound or ObjectState.Unavailable or ObjectState.Refused or ObjectState.Unsupported &&
                    !SameSource(flight, request, context))
                    result = new(request.Key, ObjectState.Unavailable, Code: LibraryObjectAccessCodes.ContextUntried);
            }
            finally
            {
                var stop = false; lock (gate) { flight.Consumers--; flight.LiveConsumers.Remove(consumer); stop = flight.Consumers == 0 && !flight.Done.Task.IsCompleted; }
                if (stop) await StopFlightAsync(flight);
            }
        }
        if (flight?.Group is { } retiredGroup) await retiredGroup.Retired.Task;
        if (result!.IsPresent)
        {
            if (request.DeclaredLength is { } expected && expected != result.Length) return new(request.Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
            if (minimumLength > result.Length) throw new ArgumentOutOfRangeException(nameof(minimumLength));
            try { if (BeforeClaim is { } claimHook) await claimHook(claim.Owner, request.Key, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return new(request.Key, ObjectState.StoreFailed, Code: e is ObjectStoreException storeError ? storeError.Code : "LIBRARY_ACQUISITION_CLAIM_FAILED", Message: e.Message); }
            ct.ThrowIfCancellationRequested();
            lock (gate) { Credit(claim, request.Key); if (bytes && stored[request.Key].Bytes.LongLength <= options.InMemoryObjectBytes) result = result with { Bytes = (byte[])stored[request.Key].Bytes.Clone() }; }
        }
        return result;
    }
    private sealed class View(InMemoryLibraryObjectAccess owner, Claim claim) : ILibraryObjectSource
    {
        private readonly CancellationTokenSource stop = new(); private readonly object local = new(); private readonly HashSet<Task> live = []; private volatile bool closed;
        private readonly HashSet<StoreCommitSubscription> subscriptions = [];
        public StoreCommitSubscription Subscribe(int capacity = StoreCommitFeed.DefaultCapacity)
        {
            lock (owner.gate)
            {
                if (owner.closed || closed) throw Error(LibraryObjectAccessCodes.Closed);
                if (capacity < 1 || capacity > owner.limits.MaxSubscriptionCapacity) throw Error(LibraryObjectAccessCodes.Limit);
                if (owner.subscriptions >= owner.limits.MaxSubscriptions) throw Error(LibraryObjectAccessCodes.Busy);
                var subscription = owner.commits.Subscribe(capacity, done => { lock (owner.gate) { if (subscriptions.Remove(done)) owner.subscriptions--; } });
                subscriptions.Add(subscription); owner.subscriptions++; return subscription;
            }
        }
        private Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken caller)
        {
            lock (local)
            {
                if (closed) throw Error(LibraryObjectAccessCodes.Closed); caller.ThrowIfCancellationRequested();
                lock (owner.gate) { if (owner.closed) throw Error(LibraryObjectAccessCodes.Closed); if (owner.operations >= owner.limits.MaxOperations) throw Error(LibraryObjectAccessCodes.Busy); owner.operations++; }
                var task = Execute(action, caller); live.Add(task); _ = task.ContinueWith(done => { lock (local) live.Remove(done); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); return task;
            }
        }
        private async Task<T> Execute<T>(Func<CancellationToken, Task<T>> action, CancellationToken caller)
        { using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, stop.Token); try { return await action(linked.Token); } finally { lock (owner.gate) owner.operations--; } }
        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
        { var frozen = Freeze(request); var context = Freeze(cdn); return Run(ct => owner.GetAsync(claim, frozen, context, priority, true, ct), cancellationToken); }
        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
        {
            if (requests.Count > owner.limits.MaxBatch) throw Error(LibraryObjectAccessCodes.Limit);
            var frozen = requests.Select(Freeze).ToArray(); var context = Freeze(cdn);
            return Run<IReadOnlyList<ObjectResult>>(async ct =>
        {
            var unique = new Dictionary<ObjectKey, ObjectResult>(); var results = new ObjectResult[frozen.Length]; long transferred = 0;
            foreach (var order in frozen.DistinctBy(request => request.Key).GroupBy(request => request.Order).OrderBy(group => group.Key))
            {
                foreach (var requestsInGroup in order.Chunk(owner.limits.MaxGroupMembers))
                {
                    if (owner.BeforeArchiveAdmission is { } admission) await admission(ct);
                    ct.ThrowIfCancellationRequested(); ArchiveGroup[] groups;
                    lock (owner.gate) groups = owner.PrepareArchiveGroups(claim, requestsInGroup, context, priority);
                    var pending = requestsInGroup.Select(async request => (request.Key, Result: await owner.GetPreparedBatchAsync(claim, request, context, priority, ct))).ToArray();
                    foreach (var group in groups) _ = Task.Run(() => owner.ProduceArchiveAsync(group));
                    foreach (var (key, result) in await Task.WhenAll(pending)) unique[key] = result;
                    transferred += groups.Sum(group => group.Transferred);
                }
            }
            for (var i = 0; i < frozen.Length; i++)
            {
                var result = unique[frozen[i].Key];
                if (result.IsPresent && frozen[i].DeclaredLength is { } declared && declared != result.Length) result = new(frozen[i].Key, ObjectState.Refused, Code: ObjectRefusalCodes.SizeMismatch);
                results[i] = result with { Bytes = null };
            }
            progress?.Report(new(results.LongLength, results.LongLength, transferred)); return results;
        }, cancellationToken);
        }
        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
        {
            request = Freeze(request); cdn = Freeze(cdn);
            return Run(async ct =>
        {
            if (offset < 0 || offset > long.MaxValue - destination.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            lock (owner.gate)
            {
                var length = owner.stored.TryGetValue(request.Key, out var existing) ? existing.Bytes.LongLength : request.DeclaredLength;
                if (length is { } known && offset > known - destination.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            }
            var result = await owner.GetAsync(claim, request, cdn, priority, false, ct, offset + destination.Length); if (!result.IsPresent) return result;
            lock (owner.gate) { var bytes = owner.stored[request.Key].Bytes; if (offset > bytes.LongLength - destination.Length) throw new ArgumentOutOfRangeException(nameof(offset)); bytes.AsMemory((int)offset, destination.Length).CopyTo(destination); }
            return result with { Bytes = null };
        }, cancellationToken);
        }
        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => Run(ct => { lock (owner.gate) return Task.FromResult(keys.LongCount(owner.stored.ContainsKey)); }, cancellationToken);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => Run<IReadOnlyList<bool>>(ct => { lock (owner.gate) return Task.FromResult<IReadOnlyList<bool>>(keys.Select(owner.stored.ContainsKey).ToArray()); }, cancellationToken);
        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) => Run<IReadOnlyList<HistoryEntry>>(_ => Task.FromResult<IReadOnlyList<HistoryEntry>>([]), cancellationToken);
        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) => Run(_ => Task.FromResult(new ObservationResult(ObjectState.Unsupported, Code: LibraryObjectAccessCodes.ObservationsUnsupported)), cancellationToken);
        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => Run(_ => Task.FromResult(new ObservationResult(ObjectState.Unsupported, Code: LibraryObjectAccessCodes.ObservationsUnsupported)), cancellationToken);
        public async ValueTask DisposeAsync()
        {
            Task[] snapshot; lock (local) { closed = true; snapshot = live.ToArray(); }
            await stop.CancelAsync(); try { await Task.WhenAll(snapshot); } catch (OperationCanceledException) { }
            StoreCommitSubscription[] ownSubscriptions;
            lock (owner.gate) { owner.views.Remove(this); ownSubscriptions = subscriptions.ToArray(); }
            foreach (var subscription in ownSubscriptions) subscription.Dispose();
        }
    }
}
