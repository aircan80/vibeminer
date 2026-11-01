using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess.Testing;

/// <summary>
/// The standard <see cref="IObjectSource"/> test double for layer 3 and up: serves synthetic objects from memory, with
/// scripted 404s, outages and refusals, version, definition and listfile replies with their real URLs, scripted observation failures,
/// and counts what was asked. The shared contract tests run it beside the real source.
/// Like the real source it publishes its store commits: a key the first time an answer stores it, and keys dropped with <see cref="Remove"/>.
/// </summary>
public sealed class InMemoryObjectSource : IObjectSource, IStoreCommits
{
    private readonly Lock gate = new();
    private readonly Dictionary<ObjectKey, byte[]> objects = [];
    private readonly Dictionary<ObjectKey, ObjectResult> scripted = [];
    private readonly List<(ObjectKey Key, FetchPriority Priority)> asked = [];
    private readonly List<ObjectKey> fetched = [];
    private readonly Dictionary<(string, string, ObservationKind), byte[]> replies = [];
    private readonly Dictionary<string, (string Name, string Revision, byte[] Bytes)> definitions = [];
    private readonly Dictionary<(string, string, ObservationKind), ObservationResult> failures = [];
    private readonly List<ObservationRequest> observed = [];
    private readonly HashSet<ObjectKey> stored = [];
    private readonly List<HistoryEntry> history = [];
    private readonly StoreCommitFeed commits = new();

    public TimeProvider Time { get; set; } = TimeProvider.System;
    /// <summary>When set, every object request waits for this first (cancellation still ends the wait).</summary>
    public Func<ObjectKey, CancellationToken, Task>? BeforeAnswer { get; set; }

    public InMemoryObjectSource Add(ObjectKey key, byte[] bytes) { lock (gate) objects[key] = bytes; return this; }
    public InMemoryObjectSource NotFound(ObjectKey key) =>
        Script(new(key, ObjectState.NotFound, Code: "CDN_NOT_FOUND", ObservedAt: Time.GetUtcNow()));
    public InMemoryObjectSource Unavailable(ObjectKey key, string code = "CDN_OFFLINE") => Script(new(key, ObjectState.Unavailable, Code: code));
    /// <summary>The object answers NeedsNetwork, as a local-only source does for an object it doesn't hold.</summary>
    public InMemoryObjectSource NeedsNetwork(ObjectKey key) =>
        Script(new(key, ObjectState.NeedsNetwork, Code: "OBJECT_ACCESS_NEEDS_NETWORK", Message: "Not stored, and this source is local only."));
    public InMemoryObjectSource StoreFailed(ObjectKey key, string code = "OBJECT_STORE_DISK_FULL") => Script(new(key, ObjectState.StoreFailed, Code: code));
    public InMemoryObjectSource Refuse(ObjectKey key, string code) => Script(new(key, ObjectState.Refused, Code: code));
    public InMemoryObjectSource AddReply(string product, string region, ObservationKind kind, byte[] bytes)
    {
        lock (gate) { replies[(product, region, kind)] = bytes; failures.Remove((product, region, kind)); }
        return this;
    }
    /// <summary>
    /// Serves a WoWDBDefs definition for <paramref name="tableId"/> (8 lower hex) as fetched at <paramref name="revision"/>: its URL
    /// is <see cref="WoWDBDefsObservations.DefinitionUrl"/>. A request naming another table gets a 404, as GitHub would answer.
    /// </summary>
    public InMemoryObjectSource AddDefinition(string tableId, string tableName, string revision, byte[] bytes)
    {
        lock (gate) { definitions[tableId] = (tableName, revision, bytes); failures.Remove((tableId, ObservationRequest.DefinitionRegion, ObservationKind.Definition)); }
        return this;
    }
    /// <summary>
    /// Scripts a whole wow-listfile release: <paramref name="releaseJson"/> is the latest-release reply and
    /// <paramref name="asset"/> is split into 8 MiB part replies p0..pN for <paramref name="tag"/>, the last one short (an empty
    /// asset has no parts). Part URLs are <see cref="WowListfileObservations.AssetUrl"/>; a part past the end is Unavailable
    /// <c>CDN_HTTP_STATUS</c>, as the real client's 416 is.
    /// </summary>
    public InMemoryObjectSource AddListfile(string tag, byte[] releaseJson, byte[] asset)
    {
        ArgumentNullException.ThrowIfNull(releaseJson);
        ArgumentNullException.ThrowIfNull(asset);
        var parts = (asset.Length + ObservationRequest.ListfilePartBytes - 1) / ObservationRequest.ListfilePartBytes;
        if (parts > ObservationRequest.ListfileMaxParts) throw new ArgumentException("A listfile asset is at most 512 MiB.", nameof(asset));
        AddReply(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, releaseJson);
        for (var i = 0; i < parts; i++)
        {
            var start = i * ObservationRequest.ListfilePartBytes;
            AddReply(tag, ObservationRequest.ListfilePartRegion(i), ObservationKind.ListfilePart,
                asset.AsSpan(start, Math.Min(ObservationRequest.ListfilePartBytes, asset.Length - start)).ToArray());
        }
        return this;
    }
    /// <summary>
    /// Every observation of (product, region, kind) fails with this state and code until a reply is added again. NotFound and
    /// Unavailable are dated now, like the real source's.
    /// </summary>
    public InMemoryObjectSource FailObservation(string product, string region, ObservationKind kind, ObjectState state, string code, string? message = null)
    {
        if (state == ObjectState.Present) throw new ArgumentException("A scripted failure isn't present.", nameof(state));
        lock (gate) failures[(product, region, kind)] = new(state, Code: code, Message: message,
            ObservedAt: state is ObjectState.NotFound or ObjectState.Unavailable ? Time.GetUtcNow() : null);
        return this;
    }
    /// <summary>
    /// Marks an object as already in the profile's store (it counts as present and its history is listed): already stored before
    /// this source started, or by another process; publishes nothing.
    /// </summary>
    public InMemoryObjectSource AddStored(ObjectKey key, byte[] bytes) { lock (gate) { objects[key] = bytes; stored.Add(key); } return this; }
    /// <summary>Drops a stored key (its bytes stay available to fetch again) and publishes it as removed.</summary>
    public InMemoryObjectSource Remove(ObjectKey key)
    {
        bool removed;
        lock (gate) removed = stored.Remove(key);
        if (removed) commits.Publish([], [key]);
        return this;
    }

    public StoreCommitSubscription Subscribe(int capacity = StoreCommitFeed.DefaultCapacity) => commits.Subscribe(capacity);
    private InMemoryObjectSource Script(ObjectResult result) { lock (gate) scripted[result.Key] = result; return this; }

    public IReadOnlyList<(ObjectKey Key, FetchPriority Priority)> Asked { get { lock (gate) return asked.ToArray(); } }
    /// <summary>Every object served that wasn't stored yet (a fetch, which stores it), in order.</summary>
    public IReadOnlyList<ObjectKey> Fetched { get { lock (gate) return fetched.ToArray(); } }
    /// <summary>Every <see cref="ObserveAsync"/> call made, in order, refused and failed ones included.</summary>
    public IReadOnlyList<ObservationRequest> Observed { get { lock (gate) return observed.ToArray(); } }

    private async Task<ObjectResult> AnswerAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, bool withBytes, CancellationToken token)
    {
        lock (gate) asked.Add((request.Key, priority));
        if (BeforeAnswer is { } wait) await wait(request.Key, token);
        token.ThrowIfCancellationRequested();
        ObjectResult result;
        lock (gate)
        {
            if (scripted.TryGetValue(request.Key, out var s)) return s;
            if (objects.TryGetValue(request.Key, out var bytes))
            {
                if (request.DeclaredLength is { } declared && declared != bytes.Length)
                    return new(request.Key, ObjectState.Refused, Code: "OBJECT_SIZE_MISMATCH");
                var from = stored.Add(request.Key) ? ServedFrom.Cdn : ServedFrom.Store;
                if (from == ServedFrom.Cdn) fetched.Add(request.Key);
                result = new(request.Key, ObjectState.Present, from, ObjectOrigin.Cdn, bytes.Length, withBytes ? (byte[])bytes.Clone() : null);
            }
            else return cdn is null
                ? new(request.Key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT")
                : new(request.Key, ObjectState.NotFound, Code: "CDN_NOT_FOUND", ObservedAt: Time.GetUtcNow());
        }
        // Published outside the lock, after the key is stored, so HasKeysAsync already agrees.
        if (result.ServedFrom == ServedFrom.Cdn) commits.Publish([request.Key], []);
        return result;
    }

    public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
        AnswerAsync(request, cdn, priority, true, cancellationToken);

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        var byKey = new Dictionary<ObjectKey, ObjectResult>();
        var results = new ObjectResult[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            if (!byKey.TryGetValue(requests[i].Key, out var r))
            {
                byKey[requests[i].Key] = r = await AnswerAsync(requests[i], cdn, priority, false, cancellationToken);
                progress?.Report(new(byKey.Count, requests.DistinctBy(x => x.Key).Count(), 0));
            }
            results[i] = r;
        }
        return results;
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        var r = await AnswerAsync(request, cdn, priority, true, cancellationToken);
        if (!r.IsPresent) return r;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + destination.Length > r.Bytes!.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
        r.Bytes.AsMemory((int)offset, destination.Length).CopyTo(destination);
        return r with { Bytes = null };
    }

    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            observed.Add(request);
            if (request.Problem() is { } problem) return Task.FromResult(Invalid(problem));
            if (failures.TryGetValue((request.Product, request.Region, request.Kind), out var failure)) return Task.FromResult(failure);
            Uri url;
            byte[] bytes;
            if (request.Kind == ObservationKind.Definition)
            {
                if (!definitions.TryGetValue(request.Product, out var d) || d.Name != request.Name) return Task.FromResult(NotObserved());
                (url, bytes) = (new Uri(WoWDBDefsObservations.DefinitionUrl(d.Revision, d.Name)), d.Bytes);
            }
            else
            {
                if (!replies.TryGetValue((request.Product, request.Region, request.Kind), out var reply))
                    return Task.FromResult(request.Kind == ObservationKind.ListfilePart ? PastTheEnd() : NotObserved());
                url = request.Kind switch
                {
                    ObservationKind.ListfileRelease => new Uri(WowListfileObservations.ReleaseUrl),
                    ObservationKind.ListfilePart => new Uri(WowListfileObservations.AssetUrl(request.Product)),
                    _ => VersionClient.UrlFor(request.Product, request.Region, (VersionDocumentKind)(int)request.Kind),
                };
                bytes = reply;
            }
            var observation = new Observation(request.Kind, request.Product, request.Region, url, Time.GetUtcNow(), bytes);
            var put = HistoryRecordFormat.Objects(new HistoryObservation((HistoryKind)(byte)request.Kind, request.Product, request.Region,
                observation.ObservedAt, url.AbsoluteUri, bytes));
            objects[put[0].Key] = bytes;
            var newlyStored = stored.Add(put[0].Key);
            history.Add(new HistoryEntry((HistoryKind)(byte)request.Kind, request.Product, request.Region, observation.ObservedAt, url.AbsoluteUri, put[0].Key, put[1].Key));
            // Publish never blocks, so it's safe under the lock here.
            if (newlyStored) commits.Publish([put[0].Key], []);
            return Task.FromResult(new ObservationResult(ObjectState.Present, observation, true));
        }
    }

    // GitHub answers a range past the end of the asset with a 416, which the real client reports as HttpStatus.
    private ObservationResult PastTheEnd() =>
        new(ObjectState.Unavailable, Code: "CDN_HTTP_STATUS", Message: "The listfile ends before this part.", ObservedAt: Time.GetUtcNow());
    private ObservationResult NotObserved() => new(ObjectState.NotFound, Code: "CDN_NOT_FOUND", ObservedAt: Time.GetUtcNow());
    private static ObservationResult Invalid(string message) => new(ObjectState.Refused, Code: "OBSERVATION_REQUEST_INVALID", Message: message);

    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult((long)keys.Count(stored.Contains));
    }

    private int hasKeysCalls;
    /// <summary>How many <see cref="HasKeysAsync"/> calls were made.</summary>
    public int HasKeysCalls => Volatile.Read(ref hasKeysCalls);

    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref hasKeysCalls);
        lock (gate) return Task.FromResult<IReadOnlyList<bool>>(keys.Select(stored.Contains).ToArray());
    }

    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<HistoryEntry>>(history
                .Where(h => (query.Product is null || h.Product == query.Product) && (query.Region is null || h.Region == query.Region) && (query.Kind is null || h.Kind == query.Kind))
                .OrderBy(h => h.ObservedAt).ThenBy(h => h.Record)
                .SkipWhile(h => query.After is not null && (h.ObservedAt, h.Record) != (query.After.ObservedAt, query.After.Record)).Skip(query.After is null ? 0 : 1)
                .Take(query.Limit).ToArray());
    }
}
