using System.Collections.Concurrent;
using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>Bytes that passed their key's check, ready for the sink.</summary>
internal readonly record struct VerifiedObject(ObjectRequest Request, long DeclaredLength, ReadOnlyMemory<byte> Bytes);

/// <summary>What a source does with verified CDN bytes: the real source puts them in the store, the CDN-only one keeps them.</summary>
internal interface IFetchSink
{
    /// <summary>A result per object, in order. Must not throw for store failures: they are <see cref="ObjectState.StoreFailed"/>.</summary>
    Task<IReadOnlyList<ObjectResult>> DeliverAsync(IReadOnlyList<VerifiedObject> objects, bool urgent, CancellationToken cancellationToken);

    /// <summary>
    /// An object too large for memory. <paramref name="fetch"/> streams the object from a byte offset (0, or where a resumed
    /// staging stopped) into the given sink and throws <see cref="CdnException"/> on failure. Called again after the sink refused
    /// the bytes, it streams from the next host, never the ones whose bytes were refused; null means no host is left.
    /// </summary>
    Task<ObjectResult> StreamLargeAsync(ObjectRequest request, long declaredLength,
        Func<long, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task<CdnReply?>> fetch, CancellationToken cancellationToken);

    /// <summary>The store's answer when it already has the key (checked again just before a transfer starts), else null.</summary>
    ValueTask<ObjectResult?> AlreadyHaveAsync(ObjectKey key, CancellationToken cancellationToken);
}

/// <summary>
/// Plans and runs CDN transfers for objects a source doesn't have. One instance per source, so every caller shares its
/// streams, host ranking, in-flight byte budget and in-flight transfers: a second caller for a key already being fetched
/// waits for that transfer (and promotes it when the second caller is interactive) instead of starting another.
/// </summary>
/// <summary>
/// Response bytes held in memory at once. A request takes all its bytes in one step (one taker at a time accumulates, so two
/// large requests can never each hold half and wait for the other); a request larger than the whole allowance takes all of it.
/// </summary>
internal sealed class ByteBudget(long bytes)
{
    private const int Unit = 64 << 10;
    private readonly int total = (int)Math.Clamp(bytes / Unit, 1, int.MaxValue);
    private readonly SemaphoreSlim units = new((int)Math.Clamp(bytes / Unit, 1, int.MaxValue));
    private readonly SemaphoreSlim taker = new(1, 1);
    private int waiting;

    /// <summary>Takers not yet granted, for tests to wait on.</summary>
    internal int Waiting => Volatile.Read(ref waiting);

    public sealed class Grant(ByteBudget owner, int units)
    {
        private int released;
        public void Release() { if (Interlocked.Exchange(ref released, 1) == 0 && units > 0) owner.units.Release(units); }
    }

    public async Task<Grant> TakeAsync(long bytes, CancellationToken cancellationToken)
    {
        var need = (int)Math.Clamp((bytes + Unit - 1) / Unit, 1, total);
        Interlocked.Increment(ref waiting);
        try
        {
            await taker.WaitAsync(cancellationToken).ConfigureAwait(false);
            var taken = 0;
            try
            {
                for (; taken < need; taken++) await units.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new Grant(this, need);
            }
            catch { if (taken > 0) units.Release(taken); throw; }
            finally { taker.Release(); }
        }
        finally { Interlocked.Decrement(ref waiting); }
    }
}

internal sealed partial class CdnFetcher
{
    private const int Unit = 64 << 10;
    /// <summary>What a loose reply with no declared length (a config, say) is charged and the most it may be; a longer one is TooLarge.</summary>
    internal const long UnknownLengthBytes = 4L << 20;
    private readonly ICdnClient cdn, interactiveCdn;
    private readonly IObjectVerifier verifier;
    private readonly ObjectSourceOptions options;
    private readonly ByteBudget background, interactive;
    private readonly ConcurrentDictionary<ObjectKey, InFlight> inFlight = new();

    /// <param name="interactiveCdn">A client over its own connections for interactive work, so a screen's request never queues
    /// behind bulk transfers on a shared HTTP/2 connection. Null uses <paramref name="cdn"/> for both.</param>
    public CdnFetcher(ICdnClient cdn, IObjectVerifier verifier, ObjectSourceOptions options, ICdnClient? interactiveCdn = null)
    {
        this.cdn = cdn; this.interactiveCdn = interactiveCdn ?? cdn; this.verifier = verifier; this.options = options;
        Streams = new StreamScheduler(options.Streams);
        Hosts = new HostRanking(options.HostCooldown, options.Time);
        background = new ByteBudget(options.InFlightBytes);
        interactive = new ByteBudget(options.InteractiveInFlightBytes);
    }

    public StreamScheduler Streams { get; }
    public HostRanking Hosts { get; }
    private int joins;

    /// <summary>Background requests waiting for response bytes, for tests to wait on.</summary>
    internal int BackgroundBytesWaiting => background.Waiting;
    /// <summary>How many times a caller joined a transfer already running, for tests to wait on.</summary>
    internal int Joins => Volatile.Read(ref joins);

    private sealed class InFlight(StreamScheduler.Ticket ticket)
    {
        public readonly TaskCompletionSource<ObjectResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly StreamScheduler.Ticket Ticket = ticket;
    }

    private sealed record Work(PlannedRange? Range, ObjectRequest? Loose, IReadOnlyDictionary<CdnKey, ObjectRequest> Members, long Bytes);

    internal static CdnObjectKind? KindOf(ObjectKey key) => key.Namespace != 0 ? null : key.Type switch
    {
        ObjectKeyType.Config => CdnObjectKind.Config,
        ObjectKeyType.ArchiveIndex or ObjectKeyType.FileIndex => CdnObjectKind.Index,
        ObjectKeyType.Encoded => CdnObjectKind.Data,
        _ => null,
    };

    internal static CdnKey CdnKeyOf(ObjectKey key) => new(key.KeyBytes());

    internal static ObjectResult FromCdnFailure(ObjectKey key, CdnException failure)
    {
        var code = "CDN_" + failure.Overall switch
        {
            CdnFailure.NotFound => "NOT_FOUND", CdnFailure.Offline => "OFFLINE", CdnFailure.HttpStatus => "HTTP_STATUS",
            CdnFailure.Timeout => "TIMEOUT", CdnFailure.Network => "NETWORK", CdnFailure.RangeIgnored => "RANGE_IGNORED",
            CdnFailure.RangeMismatch => "RANGE_MISMATCH", CdnFailure.SizeConflict => "SIZE_CONFLICT",
            CdnFailure.VerificationFailed => "VERIFICATION_FAILED", CdnFailure.Protocol => "PROTOCOL", CdnFailure.TooLarge => "TOO_LARGE",
            _ => "FAILED",
        };
        if (failure.Overall == CdnFailure.VerificationFailed && failure.Attempts.LastOrDefault(a => a.Code is not null)?.Code is { } refusal) code = refusal;
        var state = failure.Overall == CdnFailure.NotFound ? ObjectState.NotFound : failure.Retryable ? ObjectState.Unavailable : ObjectState.Refused;
        return new(key, state, Code: code, Message: failure.Message, ObservedAt: failure.ObservedAt);
    }

    /// <summary>Joins a transfer already running for the key, promoting it for an interactive caller. Null when none runs.</summary>
    public Task<ObjectResult>? Join(ObjectKey key, FetchPriority priority)
    {
        if (!inFlight.TryGetValue(key, out var running)) return null;
        Interlocked.Increment(ref joins);
        if (priority == FetchPriority.Interactive) Streams.Promote(running.Ticket);
        return running.Done.Task;
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the key's in-flight claim, so a concurrent caller (or a CDN transfer for the key) waits
    /// for it instead of starting another. A key already claimed is joined; a joined result that isn't present, or a claim
    /// released without one, lets this caller claim the key itself. Null from <paramref name="work"/> (or a throw, which
    /// propagates) releases the claim with no result, so waiting callers carry on themselves.
    /// </summary>
    internal async Task<ObjectResult?> ClaimAsync(ObjectKey key, FetchPriority priority, Func<CancellationToken, Task<ObjectResult?>> work,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            if (Join(key, priority) is { } running)
            {
                try
                {
                    var joined = await running.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (joined.IsPresent) return joined;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                continue;
            }
            var flight = new InFlight(Streams.NewTicket(priority));
            if (!inFlight.TryAdd(key, flight)) continue;
            ObjectResult? result = null;
            try { result = await work(cancellationToken).ConfigureAwait(false); }
            finally
            {
                inFlight.TryRemove(new(key, flight));
                if (result is null) flight.Done.TrySetCanceled();
                else flight.Done.TrySetResult(result with { Bytes = null });
            }
            return result;
        }
    }

    /// <summary>
    /// Fetches every request (distinct keys the caller doesn't have) and returns one result per request, in order. A key another
    /// caller is already fetching is awaited, not fetched twice; if that caller is canceled, this one fetches it itself.
    /// </summary>
    public async Task<ObjectResult[]> FetchAsync(IReadOnlyList<ObjectRequest> requests, CdnContext context, FetchPriority priority,
        IFetchSink sink, IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        var results = new ObjectResult?[requests.Count];
        var index = new Dictionary<ObjectKey, int>();
        for (var i = 0; i < requests.Count; i++)
            if (!index.TryAdd(requests[i].Key, i)) throw new ArgumentException("FetchAsync takes distinct keys.", nameof(requests));
        var pending = Enumerable.Range(0, requests.Count).ToList();
        long done = 0, cdnBytes = 0;
        void Finish(ObjectKey key, ObjectResult result)
        {
            results[index[key]] = result;
            progress?.Report(new(Interlocked.Increment(ref done), requests.Count, Interlocked.Read(ref cdnBytes)));
        }

        for (var round = 0; pending.Count > 0; round++)
        {
            var joined = new List<(ObjectKey Key, Task<ObjectResult> Task)>();
            var work = Plan(pending.Select(i => requests[i]).ToArray(), context, priority, Finish, joined);
            await RunAsync(work, context, priority, sink, Finish, bytes => Interlocked.Add(ref cdnBytes, bytes), joined, cancellationToken).ConfigureAwait(false);
            pending.Clear();
            foreach (var (key, task) in joined)
            {
                try { Finish(key, await task.WaitAsync(cancellationToken).ConfigureAwait(false)); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { pending.Add(index[key]); }
            }
        }
        return results.Select((r, i) => r ?? new ObjectResult(requests[i].Key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_INCOMPLETE")).ToArray();
    }

    private List<Work> Plan(IReadOnlyList<ObjectRequest> requests, CdnContext context, FetchPriority priority,
        Action<ObjectKey, ObjectResult> finish, List<(ObjectKey, Task<ObjectResult>)> joined)
    {
        var work = new List<Work>();
        foreach (var group in requests.GroupBy(r => r.Order).OrderBy(g => g.Key))
        {
            var planned = new List<PlanObject>();
            var byKey = new Dictionary<CdnKey, ObjectRequest>();
            var loose = new List<ObjectRequest>();
            foreach (var r in group)
            {
                if (Join(r.Key, priority) is { } running) { joined.Add((r.Key, running)); continue; }
                if (KindOf(r.Key) is null)
                { finish(r.Key, new(r.Key, ObjectState.Refused, Code: "OBJECT_NOT_ON_CDN", Message: "This key type or namespace isn't served by the CDN.")); continue; }
                if (r.DeclaredLength is < 1)
                { finish(r.Key, new(r.Key, ObjectState.Refused, Code: "OBJECT_LENGTH_INVALID", Message: "A declared length is positive.")); continue; }
                if (r.Locations is { Count: > 0 } locations)
                {
                    if (r.Key.Type != ObjectKeyType.Encoded)
                    { finish(r.Key, new(r.Key, ObjectState.Refused, Code: "OBJECT_LOCATION_UNSUPPORTED", Message: "Only encoded objects live in archives.")); continue; }
                    if (locations.Any(l => l.Size > options.InMemoryObjectBytes))
                    { finish(r.Key, new(r.Key, ObjectState.Refused, Code: "OBJECT_LOCATION_TOO_LARGE", Message: "An archive entry over 64 MiB isn't fetched as a range.")); continue; }
                    var key = CdnKeyOf(r.Key);
                    planned.Add(new(key, r.DeclaredLength, locations)); byKey[key] = r;
                }
                else loose.Add(r);
            }
            var plan = RangePlanner.Plan(planned, context.Archives, options.Ranges);
            foreach (var rejected in plan.Rejected)
            {
                var r = byKey[rejected.EKey];
                finish(r.Key, new(r.Key, ObjectState.Refused, Code: rejected.Code, Message: rejected.Message));
            }
            foreach (var range in plan.Ranges)
                work.Add(new(range, null, range.Members.ToDictionary(m => m.EKey, m => byKey[m.EKey]), range.Length));
            foreach (var r in loose)
                work.Add(new(null, r, new Dictionary<CdnKey, ObjectRequest> { [CdnKeyOf(r.Key)] = r },
                    r.DeclaredLength is { } length && length <= options.InMemoryObjectBytes ? length : r.DeclaredLength is null ? UnknownLengthBytes : Unit));
        }
        return work;
    }

    private async Task RunAsync(List<Work> work, CdnContext context, FetchPriority priority, IFetchSink sink,
        Action<ObjectKey, ObjectResult> finish, Action<long> transferred, List<(ObjectKey, Task<ObjectResult>)> joined, CancellationToken cancellationToken)
    {
        if (work.Count == 0) return;
        var next = -1;
        var workers = Math.Min(work.Count, priority == FetchPriority.Interactive ? options.Streams.Interactive + options.Streams.MaxBackground : options.Streams.MaxBackground);
        var tasks = new Task[workers];
        for (var w = 0; w < workers; w++)
            tasks[w] = Task.Run(async () =>
            {
                int i;
                while ((i = Interlocked.Increment(ref next)) < work.Count)
                    await RunOneAsync(work[i], context, priority, sink, finish, transferred, joined, cancellationToken).ConfigureAwait(false);
            }, CancellationToken.None);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Claims the members nobody else is fetching, then runs the transfer. Every claimed key ends with a result or a cancellation.</summary>
    private async Task RunOneAsync(Work item, CdnContext context, FetchPriority priority, IFetchSink sink,
        Action<ObjectKey, ObjectResult> finish, Action<long> transferred, List<(ObjectKey, Task<ObjectResult>)> joined, CancellationToken cancellationToken)
    {
        var ticket = Streams.NewTicket(priority);
        var claimed = new Dictionary<CdnKey, (ObjectRequest Request, InFlight Flight)>();
        try
        {
            foreach (var (key, request) in item.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var flight = new InFlight(ticket);
                if (!inFlight.TryAdd(request.Key, flight))
                {
                    if (Join(request.Key, priority) is { } running) lock (joined) joined.Add((request.Key, running));
                    else lock (joined) joined.Add((request.Key, Task.FromCanceled<ObjectResult>(new CancellationToken(true))));
                    continue;
                }
                // Claimed before the store check, so a throw or cancellation there still releases the flight in the finally.
                claimed[key] = (request, flight);
                // Checked again here: an interactive caller may have stored it since this plan was made. A store that fails
                // the check fails only this object.
                ObjectResult? have;
                try { have = await sink.AlreadyHaveAsync(request.Key, cancellationToken).ConfigureAwait(false); }
                catch (ObjectStoreException ex) { have = new(request.Key, ObjectState.StoreFailed, Code: ex.Code, Message: ex.Message); }
                if (have is not null)
                {
                    // Completed before it leaves claimed: if finish throws, the finally still releases the flight.
                    Complete(request.Key, flight, have, finish); claimed.Remove(key);
                }
            }
            if (claimed.Count == 0) return;

            void Settle(CdnKey key, ObjectResult result) { var c = claimed[key]; Complete(c.Request.Key, c.Flight, result, finish); claimed.Remove(key); }
            if (item.Range is { } range) await RangeAsync(range, claimed.ToDictionary(c => c.Key, c => c.Value.Request), ticket, context, sink, Settle, transferred, cancellationToken).ConfigureAwait(false);
            else { var (key, c) = claimed.Single(); await LooseAsync(c.Request, ticket, context, sink, r => Settle(key, r), transferred, cancellationToken).ConfigureAwait(false); }
        }
        finally
        {
            // Anything still claimed (cancellation, or an unexpected failure) is released so waiting callers can fetch it themselves.
            foreach (var (_, c) in claimed)
            {
                inFlight.TryRemove(new(c.Request.Key, c.Flight));
                c.Flight.Done.TrySetCanceled();
            }
        }
    }

    private ICdnClient Client(StreamScheduler.Ticket ticket) => ticket.CurrentPriority == FetchPriority.Interactive ? interactiveCdn : cdn;

    private void Complete(ObjectKey key, InFlight flight, ObjectResult result, Action<ObjectKey, ObjectResult> finish)
    {
        finish(key, result);
        inFlight.TryRemove(new(key, flight));
        flight.Done.TrySetResult(result with { Bytes = null });
    }

    /// <summary>
    /// Response bytes first (interactive work has its own allowance, so it never waits behind background bytes), then a stream.
    /// A background ticket promoted while it waits for bytes stops waiting and takes from the interactive allowance instead.
    /// </summary>
    private async Task<(StreamScheduler.Lease Lease, ByteBudget.Grant Bytes)> AdmitAsync(StreamScheduler.Ticket ticket, long bytes, CancellationToken cancellationToken)
    {
        ByteBudget.Grant grant;
        if (ticket.CurrentPriority == FetchPriority.Interactive) grant = await interactive.TakeAsync(bytes, cancellationToken).ConfigureAwait(false);
        else
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var take = background.TakeAsync(bytes, stop.Token);
            if (await Task.WhenAny(take, ticket.Promoted.Task).ConfigureAwait(false) != take) stop.Cancel();
            // A grant that landed anyway is kept; a wait canceled by the promotion (units taken so far are given back) moves over.
            try { grant = await take.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { grant = await interactive.TakeAsync(bytes, cancellationToken).ConfigureAwait(false); }
        }
        try { return (await Streams.AcquireAsync(ticket, cancellationToken).ConfigureAwait(false), grant); }
        catch { grant.Release(); throw; }
    }

    /// <param name="badHost">The host whose bytes for this (single-member) range failed their check; it isn't asked again.</param>
    private async Task RangeAsync(PlannedRange range, Dictionary<CdnKey, ObjectRequest> members, StreamScheduler.Ticket ticket, CdnContext context,
        IFetchSink sink, Action<CdnKey, ObjectResult> settle, Action<long> transferred, CancellationToken cancellationToken,
        string? badHost = null, string? badCode = null)
    {
        // Members another caller took are dropped; the range shrinks to the members left.
        var kept = range.Members.Where(m => members.ContainsKey(m.EKey)).ToArray();
        var start = range.Offset + kept[0].OffsetInRange;
        var end = range.Offset + kept[^1].OffsetInRange + kept[^1].Size;
        var shrunk = new PlannedRange(range.Archive, start, end - start,
            kept.Select(m => m with { OffsetInRange = m.OffsetInRange - (start - range.Offset) }).ToArray());

        var route = Hosts.Order(context.Route, ticket.CurrentPriority);
        if (badHost is not null)
        {
            var others = route.Hosts.Where(h => h != badHost).ToArray();
            if (others.Length == 0)
            {
                var only = members[kept[0].EKey];
                settle(kept[0].EKey, new(only.Key, ObjectState.Refused, Code: badCode, Message: "The bytes the index points at don't pass the key's check."));
                return;
            }
            route = route.WithHosts(others);
        }
        // A lone member is checked inside the client, so bad bytes from one host move on to the next (D132).
        CdnCheck? check = null;
        if (kept.Length == 1)
        {
            var (lone, size) = (members[kept[0].EKey].Key, kept[0].Size);
            check = bytes => verifier.Check(lone, bytes, size)?.Code;
        }

        byte[] reply;
        string servedBy;
        var (lease, units) = await AdmitAsync(ticket, shrunk.Length, cancellationToken).ConfigureAwait(false);
        try
        {
            CdnFailure? outcome = null;
            try
            {
                var answer = await Client(ticket).FetchAsync(route,
                    CdnRequest.Range(CdnObjectKind.Data, shrunk.Archive, shrunk.Offset, shrunk.Length, check: check), cancellationToken).ConfigureAwait(false);
                Hosts.Observe(answer); transferred(answer.Length);
                reply = answer.Bytes!; servedBy = answer.Host;
            }
            catch (CdnException failure)
            {
                outcome = failure.Failure; Hosts.Observe(failure);
                lease.Complete(outcome); lease = null!;
                units.Release();
                // A merged range that failed everywhere for a passing reason is retried one object at a time (#150); a 404,
                // no connection or a refusal answers for every member.
                if (kept.Length > 1 && failure.Retryable && failure.Overall is not (CdnFailure.NotFound or CdnFailure.Offline))
                {
                    foreach (var m in kept)
                        await RangeAsync(new PlannedRange(shrunk.Archive, range.Offset + m.OffsetInRange, m.Size, [m with { OffsetInRange = 0 }]),
                            new() { [m.EKey] = members[m.EKey] }, Streams.NewTicket(ticket.CurrentPriority), context, sink, settle, transferred, cancellationToken).ConfigureAwait(false);
                    return;
                }
                foreach (var m in kept) settle(m.EKey, FromCdnFailure(members[m.EKey].Key, failure));
                return;
            }
            lease.Complete(null); lease = null!;

            var verified = new List<VerifiedObject>();
            var retry = new List<(PlannedMember Member, string Code)>();
            foreach (var (m, (ekey, bytes)) in shrunk.Members.Zip(RangePlanner.Split(shrunk, reply)))
            {
                var request = members[ekey];
                var code = verifier.Check(request.Key, bytes.Span, m.Size)?.Code;
                if (code is null) verified.Add(new(request, m.Size, bytes));
                else if (kept.Length > 1) retry.Add((m, code));
                else settle(ekey, new(request.Key, ObjectState.Refused, Code: code, Message: "The bytes the index points at don't pass the key's check."));
            }
            if (retry.Count > 0) Hosts.SentBadBytes(servedBy);
            foreach (var batch in verified.Chunk(4096))
            {
                var results = await sink.DeliverAsync(batch, ticket.CurrentPriority == FetchPriority.Interactive, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < batch.Length; i++) settle(CdnKeyOf(batch[i].Request.Key), results[i]);
            }
            units.Release();
            // One bad object fails only itself: it gets one request of its own on the other hosts, in ranked order, before it is refused.
            foreach (var (m, code) in retry)
                await RangeAsync(new PlannedRange(shrunk.Archive, shrunk.Offset + m.OffsetInRange, m.Size, [m with { OffsetInRange = 0 }]),
                    new() { [m.EKey] = members[m.EKey] }, Streams.NewTicket(ticket.CurrentPriority), context, sink, settle, transferred, cancellationToken,
                    servedBy, code).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
            units.Release();
        }
    }

    private async Task LooseAsync(ObjectRequest request, StreamScheduler.Ticket ticket, CdnContext context, IFetchSink sink,
        Action<ObjectResult> settle, Action<long> transferred, CancellationToken cancellationToken)
    {
        var kind = KindOf(request.Key)!.Value;
        var key = CdnKeyOf(request.Key);
        var large = request.DeclaredLength > options.InMemoryObjectBytes;
        var (lease, units) = await AdmitAsync(ticket, large ? Unit : request.DeclaredLength ?? UnknownLengthBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            if (large)
            {
                var declared = request.DeclaredLength!.Value;
                CdnFailure? failure = null;
                // Hosts whose bytes the sink refused. Each host gets a request of its own so the one that sent bytes is known.
                var refused = new HashSet<string>(StringComparer.Ordinal);
                string? streaming = null;
                var result = await sink.StreamLargeAsync(request, declared, async (offset, into, token) =>
                {
                    // Called again: the sink refused what the last host sent.
                    if (streaming is not null) { refused.Add(streaming); Hosts.SentBadBytes(streaming); streaming = null; }
                    var hosts = Hosts.Order(context.Route, ticket.CurrentPriority).Hosts.Where(h => !refused.Contains(h)).ToArray();
                    if (hosts.Length == 0) return null;
                    var cdnRequest = offset == 0 ? CdnRequest.Whole(kind, key, declared) : CdnRequest.Range(kind, key, offset, declared - offset, declared);
                    var attempts = new List<CdnAttempt>();
                    foreach (var host in hosts)
                    {
                        string? started = null;
                        try
                        {
                            var reply = await Client(ticket).FetchToAsync(context.Route.WithHosts([host]), cdnRequest, (bytes, t) =>
                            { started = streaming = host; return into(bytes, t); }, token).ConfigureAwait(false);
                            Hosts.Observe(reply); transferred(reply.Length);
                            return reply with { Failed = [.. attempts, .. reply.Failed] };
                        }
                        catch (CdnException ex)
                        {
                            Hosts.Observe(ex); attempts.AddRange(ex.Attempts);
                            // Bytes already reached the sink: never move on, so it never sees two hosts' bytes without a restart.
                            if (started is not null) break;
                        }
                    }
                    var all = new CdnException(attempts);
                    failure = all.Failure;
                    throw all;
                }, cancellationToken).ConfigureAwait(false);
                lease.Complete(failure); lease = null!;
                settle(result);
                return;
            }
            CdnReply answer;
            try
            {
                var declared = request.DeclaredLength;
                answer = await Client(ticket).FetchAsync(Hosts.Order(context.Route, ticket.CurrentPriority),
                    CdnRequest.Whole(kind, key, declared, bytes => verifier.Check(request.Key, bytes, declared ?? bytes.Length)?.Code,
                        declared is null ? UnknownLengthBytes : null), cancellationToken).ConfigureAwait(false);
                Hosts.Observe(answer); transferred(answer.Length);
            }
            catch (CdnException failure)
            {
                Hosts.Observe(failure); lease.Complete(failure.Failure); lease = null!;
                settle(FromCdnFailure(request.Key, failure));
                return;
            }
            lease.Complete(null); lease = null!;
            var results = await sink.DeliverAsync([new(request, answer.Length, answer.Bytes!)], ticket.CurrentPriority == FetchPriority.Interactive, cancellationToken).ConfigureAwait(false);
            settle(results[0]);
        }
        finally
        {
            lease?.Dispose();
            units.Release();
        }
    }
}
