using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// CDN only: every call fetches and verifies, nothing is stored and nothing is recorded (RecordAsync answers Present, not recorded). Whole objects over 64 MiB are
/// Unsupported (there is nowhere to stage them); a range of a chunked loose one is fetched chunk by chunk (D340). Anything reading the store's index directly, like the Downloaded figure, sees 0%.
/// </summary>
public sealed class CdnObjectSource : IObjectSource
{
    private readonly Observations observations;
    private readonly CdnFetcher fetcher;
    private readonly Sink sink = new();

    public CdnObjectSource(ICdnClient cdn, IReadOnlyList<IObservationSource> observationSources, IObjectVerifier verifier, ObjectSourceOptions? options = null,
        ICdnClient? interactiveCdn = null)
    {
        observations = new(observationSources);
        fetcher = new CdnFetcher(cdn, verifier, options ?? new(), interactiveCdn);
    }

    public StreamScheduler Streams => fetcher.Streams;

    public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cdn is null) return NoContext(request.Key);
        for (var attempt = 0; ; attempt++)
        {
            var result = (await fetcher.FetchAsync([request], cdn, priority, sink, null, cancellationToken).ConfigureAwait(false))[0];
            // Present without bytes: it joined another caller's transfer, whose bytes this source didn't keep. Fetch it again.
            if (!result.IsPresent || result.Bytes is not null || attempt == 2) return result;
        }
    }

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var distinct = requests.DistinctBy(r => r.Key).ToArray();
        var fetched = cdn is null ? distinct.Select(r => NoContext(r.Key)).ToArray()
            : await fetcher.FetchAsync(distinct, cdn, priority, sink, progress, cancellationToken).ConfigureAwait(false);
        var byKey = distinct.Zip(fetched).ToDictionary(p => p.First.Key, p => p.Second with { Bytes = null });
        return requests.Select(r => byKey[r.Key]).ToArray();
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A big loose encoded object: only the proven chunks under the range (D340). Anything else is fetched and verified
        // whole first, since a bare byte range of it can't be checked against its key.
        if (cdn is not null && fetcher.CanFetchRange(request)
            && await fetcher.FetchRangeAsync(request, offset, destination, cdn, priority, cancellationToken).ConfigureAwait(false) is { } ranged)
            return ranged;
        var whole = await GetKeyAsync(request, cdn, priority, cancellationToken).ConfigureAwait(false);
        if (!whole.IsPresent) return whole;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + destination.Length > whole.Bytes!.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
        whole.Bytes.AsMemory((int)offset, destination.Length).CopyTo(destination);
        return whole with { Bytes = null };
    }

    public async Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        var (observation, failed) = await observations.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        return observation is null ? failed! : new(ObjectState.Present, observation, Recorded: false);
    }

    /// <summary>
    /// No history log: a valid observation is answered as <see cref="ObserveAsync"/> answers, Present and not recorded; an
    /// invalid one is Refused.
    /// </summary>
    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ObservationRequest.RecordProblem(observation) is { } problem ? Observations.Invalid(problem)
            : new ObservationResult(ObjectState.Present, observation, Recorded: false));
    }

    /// <summary>Nothing is stored, so nothing counts as downloaded.</summary>
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => Task.FromResult(0L);

    /// <summary>Nothing is stored, so no key is held.</summary>
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<bool>>(new bool[keys.Count]);
    }

    /// <summary>No history log: nothing is recorded.</summary>
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HistoryEntry>>([]);

    internal static ObjectResult NoContext(ObjectKey key) =>
        new(key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT", Message: "No CDN route was given.");

    private sealed class Sink : IFetchSink
    {
        public Task<IReadOnlyList<ObjectResult>> DeliverAsync(IReadOnlyList<VerifiedObject> objects, bool urgent, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ObjectResult>>(objects.Select(o =>
                new ObjectResult(o.Request.Key, ObjectState.Present, ServedFrom.Cdn, ObjectOrigin.Cdn, o.Bytes.Length, o.Bytes.ToArray())).ToArray());

        public Task<ObjectResult> StreamLargeAsync(ObjectRequest request, long declaredLength,
            Func<long, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task<CdnReply?>> fetch, CancellationToken cancellationToken) =>
            Task.FromResult(new ObjectResult(request.Key, ObjectState.Unsupported, Code: "OBJECT_ACCESS_TOO_LARGE",
                Message: "A CDN-only source holds objects in memory, at most 64 MiB."));

        public ValueTask<ObjectResult?> AlreadyHaveAsync(ObjectKey key, CancellationToken cancellationToken) => ValueTask.FromResult<ObjectResult?>(null);
    }
}

/// <summary>
/// Local only, a true offline mode: the own store, then the shared dev store. Never touches the network; anything missing is
/// <see cref="ObjectState.NeedsNetwork"/>. Damaged copies are reported as refused, never repaired.
/// </summary>
public sealed class LocalObjectSource(IObjectReader store, IHistoryReader? history = null, IObjectReader? sharedStore = null) : IObjectSource
{
    private const long WholeReadLimit = 64L << 20;

    public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (reader, from) in Readers())
        {
            if (request.DeclaredLength > WholeReadLimit)
            {
                if (await reader.HasAsync(request.Key, cancellationToken).ConfigureAwait(false)) return new(request.Key, ObjectState.Present, from, Length: request.DeclaredLength);
                continue;
            }
            ObjectRead read;
            try { read = await reader.ReadAsync(request.Key, cancellationToken).ConfigureAwait(false); }
            catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.Limit) { return new(request.Key, ObjectState.Present, from); }
            if (read.State == ReadState.Present) return new(request.Key, ObjectState.Present, from, read.Info?.Origin, read.Bytes!.Length, read.Bytes);
            if (read.State != ReadState.Absent) return Damaged(request.Key, read.State, read.Problem);
        }
        return NeedsNetwork(request.Key);
    }

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new ObjectResult[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            results[i] = NeedsNetwork(requests[i].Key);
            foreach (var (reader, from) in Readers())
                if (await reader.HasAsync(requests[i].Key, cancellationToken).ConfigureAwait(false)) { results[i] = new(requests[i].Key, ObjectState.Present, from); break; }
            progress?.Report(new(i + 1, requests.Count, 0));
        }
        return results;
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var (reader, from) in Readers())
        {
            var read = await reader.ReadRangeAsync(request.Key, offset, destination, cancellationToken).ConfigureAwait(false);
            if (read.State == ReadState.Present) return new(request.Key, ObjectState.Present, from, read.Info?.Origin, read.Info?.Length);
            if (read.State != ReadState.Absent) return Damaged(request.Key, read.State, read.Problem);
        }
        return NeedsNetwork(request.Key);
    }

    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new ObservationResult(ObjectState.NeedsNetwork, Code: "OBJECT_ACCESS_NEEDS_NETWORK", Message: "This source is local only."));

    /// <summary>Local only and read-only: answered as <see cref="ObserveAsync"/> is (NeedsNetwork); an invalid observation is Refused.</summary>
    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ObservationRequest.RecordProblem(observation) is { } problem ? Observations.Invalid(problem)
            : new ObservationResult(ObjectState.NeedsNetwork, Code: "OBJECT_ACCESS_NEEDS_NETWORK", Message: "This source is local only."));
    }

    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
        Presence.CountAsync(store, keys, cancellationToken);

    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
        Presence.HasAsync(store, keys, cancellationToken);

    /// <summary>The own store's history log, so builds stay listed offline.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        history is null ? [] : await history.ReadHistoryAsync(query, cancellationToken).ConfigureAwait(false);

    private IEnumerable<(IObjectReader, ServedFrom)> Readers()
    {
        yield return (store, ServedFrom.Store);
        if (sharedStore is not null) yield return (sharedStore, ServedFrom.SharedStore);
    }

    private static ObjectResult NeedsNetwork(ObjectKey key) =>
        new(key, ObjectState.NeedsNetwork, Code: "OBJECT_ACCESS_NEEDS_NETWORK", Message: "Not stored, and this source is local only.");

    private static ObjectResult Damaged(ObjectKey key, ReadState state, string? problem) =>
        new(key, ObjectState.Refused, Code: "OBJECT_ACCESS_" + state.ToString().ToUpperInvariant(), Message: problem);
}
