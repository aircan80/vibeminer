using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// The real <see cref="IObjectSource"/>: the profile's own store, then the shared dev store (read-only; copied in, never pointed
/// into), then the CDN. CDN bytes are verified before they are put, in batches, and every object records where it came from.
/// </summary>
public sealed class ObjectSource : IObjectSource
{
    private readonly IObjectReader store;
    private readonly IObjectWriter writer;
    private readonly IHistoryLog history;
    private readonly IObjectReader? shared;
    private readonly Observations observations;
    private readonly IObjectVerifier verifier;
    private readonly ObjectSourceOptions options;
    private readonly CdnFetcher fetcher;
    private readonly Sink sink;

    public ObjectSource(IObjectReader store, IObjectWriter writer, IHistoryLog history, IObjectReader? sharedStore, ICdnClient cdn,
        IReadOnlyList<IObservationSource> observationSources, IObjectVerifier verifier, ObjectSourceOptions? options = null, ICdnClient? interactiveCdn = null)
    {
        this.store = store; this.writer = writer; this.history = history; shared = sharedStore;
        observations = new(observationSources); this.verifier = verifier; this.options = options ?? new();
        fetcher = new CdnFetcher(cdn, verifier, this.options, interactiveCdn);
        sink = new Sink(this);
    }

    /// <summary>The shared streams, so callers and tests can see the background limit and what is in use.</summary>
    public StreamScheduler Streams => fetcher.Streams;

    public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; ; attempt++)
        {
            if (await ReadStoredAsync(request, cancellationToken).ConfigureAwait(false) is { } stored) return stored;
            if (await CopySharedAsync(request, withBytes: true, priority, cancellationToken).ConfigureAwait(false) is { } copied) return copied;
            if (cdn is null) return NoContext(request.Key);
            var result = (await fetcher.FetchAsync([request], cdn, priority, sink, null, cancellationToken).ConfigureAwait(false))[0];
            if (!result.IsPresent || result.Bytes is not null || result.Length > options.InMemoryObjectBytes || attempt == 2) return result;
            // Present without bytes: another caller's transfer stored it. Read it back (loop once more through the store).
        }
    }

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new ObjectResult[requests.Count];
        var first = new Dictionary<ObjectKey, int>();
        var missing = new List<ObjectRequest>();
        for (var i = 0; i < requests.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (first.ContainsKey(requests[i].Key)) continue;
            first[requests[i].Key] = i;
            if (await store.HasAsync(requests[i].Key, cancellationToken).ConfigureAwait(false))
                results[i] = new(requests[i].Key, ObjectState.Present, ServedFrom.Store);
            else if (await CopySharedAsync(requests[i], withBytes: false, priority, cancellationToken).ConfigureAwait(false) is { } copied)
                results[i] = copied;
            else missing.Add(requests[i]);
        }
        if (missing.Count > 0)
        {
            var fetched = cdn is null ? missing.Select(r => NoContext(r.Key)).ToArray()
                : await fetcher.FetchAsync(missing, cdn, priority, sink, progress, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < missing.Count; i++) results[first[missing[i].Key]] = fetched[i] with { Bytes = null };
        }
        for (var i = 0; i < requests.Count; i++) results[i] ??= results[first[requests[i].Key]];
        return results;
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; ; attempt++)
        {
            var read = await store.ReadRangeAsync(request.Key, offset, destination, cancellationToken).ConfigureAwait(false);
            if (read.State == ReadState.Present) return new(request.Key, ObjectState.Present, attempt == 0 ? ServedFrom.Store : ServedFrom.Cdn, read.Info?.Origin, read.Info?.Length);
            if (attempt > 0) return new(request.Key, ObjectState.Refused, Code: "OBJECT_ACCESS_" + read.State.ToString().ToUpperInvariant(), Message: read.Problem);
            if (read.State != ReadState.Absent) await DropDamagedAsync(request.Key, cancellationToken).ConfigureAwait(false);
            var made = (await GetKeysAsync([request], cdn, priority, null, cancellationToken).ConfigureAwait(false))[0];
            if (!made.IsPresent) return made;
        }
    }

    public async Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        var (observation, failed) = await observations.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        if (observation is null) return failed!;
        try
        {
            var put = await history.AppendHistoryAsync(new HistoryObservation((HistoryKind)(byte)observation.Kind, observation.Product, observation.Region,
                observation.ObservedAt, observation.Url.AbsoluteUri, observation.Bytes), cancellationToken).ConfigureAwait(false);
            var refused = put.FirstOrDefault(p => p.Outcome is PutOutcome.Refused or PutOutcome.Canceled);
            return refused is null ? new(ObjectState.Present, observation, true)
                : new(ObjectState.StoreFailed, observation, false, refused.Code ?? "OBJECT_ACCESS_CANCELED", refused.Message);
        }
        catch (ObjectStoreException ex) { return new(ObjectState.StoreFailed, observation, false, ex.Code, ex.Message); }
    }

    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
        Presence.CountAsync(store, keys, cancellationToken);

    public async Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
        await history.ReadHistoryAsync(query, cancellationToken).ConfigureAwait(false);

    private static ObjectResult NoContext(ObjectKey key) =>
        new(key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT", Message: "The object isn't stored and no CDN route was given.");

    /// <summary>The own store's copy with its bytes, or null when it has none. A damaged copy is deleted so it can be fetched again.</summary>
    private async Task<ObjectResult?> ReadStoredAsync(ObjectRequest request, CancellationToken cancellationToken)
    {
        if (request.DeclaredLength > options.InMemoryObjectBytes)
            return await store.HasAsync(request.Key, cancellationToken).ConfigureAwait(false)
                ? new ObjectResult(request.Key, ObjectState.Present, ServedFrom.Store, Length: request.DeclaredLength) : null;
        ObjectRead read;
        try { read = await store.ReadAsync(request.Key, cancellationToken).ConfigureAwait(false); }
        catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.Limit)
        { return new(request.Key, ObjectState.Present, ServedFrom.Store, Message: "Larger than a whole read; use GetRange."); }
        switch (read.State)
        {
            case ReadState.Present: return new(request.Key, ObjectState.Present, ServedFrom.Store, read.Info?.Origin, read.Bytes!.Length, read.Bytes);
            case ReadState.Absent: return null;
            default: await DropDamagedAsync(request.Key, cancellationToken).ConfigureAwait(false); return null;
        }
    }

    /// <summary>A stored copy that fails its read checks (or whose pack is gone) is deleted so a fresh copy can be put.</summary>
    private async Task DropDamagedAsync(ObjectKey key, CancellationToken cancellationToken)
    {
        try { await writer.DeleteAsync([key], cancellationToken).ConfigureAwait(false); }
        catch (ObjectStoreException) { /* the Put that follows reports the store's state */ }
    }

    /// <summary>Copies an object from the shared dev store into the own store (re-verified by Put), or null when it has none.</summary>
    private async Task<ObjectResult?> CopySharedAsync(ObjectRequest request, bool withBytes, FetchPriority priority, CancellationToken cancellationToken)
    {
        if (shared is null || !await shared.HasAsync(request.Key, cancellationToken).ConfigureAwait(false)) return null;
        // A large copy is staged under the fetcher's in-flight claim: one staging per key, and a concurrent caller joins it.
        if (request.DeclaredLength > options.InMemoryObjectBytes)
            return await fetcher.ClaimAsync(request.Key, priority, token => CopyLargeSharedAsync(request, request.DeclaredLength!.Value, token), cancellationToken).ConfigureAwait(false);
        ObjectRead read;
        try { read = await shared.ReadAsync(request.Key, cancellationToken).ConfigureAwait(false); }
        catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.Limit) { return null; }
        if (read.State != ReadState.Present) return null;
        var declared = request.DeclaredLength ?? read.Bytes!.Length;
        var put = await sink.PutAsync([new(request, declared, read.Bytes!)], ObjectOrigin.SharedStore, ServedFrom.SharedStore, cancellationToken).ConfigureAwait(false);
        var result = put[0];
        return result.IsPresent ? result with { Bytes = withBytes ? read.Bytes : null } : result.State == ObjectState.Refused ? null : result;
    }

    /// <summary>
    /// Runs under the key's claim. A shared copy that can't be read or whose bytes are refused gives null (the CDN fetches it);
    /// a failure of the own store is that object's StoreFailed result, never a throw for the whole batch.
    /// </summary>
    private async Task<ObjectResult?> CopyLargeSharedAsync(ObjectRequest request, long declared, CancellationToken cancellationToken)
    {
        // Checked again under the claim: another caller's copy or transfer may have stored it since.
        if (await store.HasAsync(request.Key, cancellationToken).ConfigureAwait(false))
            return new(request.Key, ObjectState.Present, ServedFrom.Store, Length: declared);
        IObjectStaging staging;
        try { staging = await writer.StageAsync(request.Key, declared, ObjectOrigin.SharedStore, cancellationToken).ConfigureAwait(false); }
        catch (ObjectStoreException ex) { return new(request.Key, ObjectState.StoreFailed, Code: ex.Code, Message: ex.Message); }
        await using (staging.ConfigureAwait(false))
        {
            try
            {
                var buffer = new byte[4 << 20];
                while (staging.Length < declared)
                {
                    var slice = buffer.AsMemory(0, (int)Math.Min(buffer.Length, declared - staging.Length));
                    RangeRead read;
                    try { read = await shared!.ReadRangeAsync(request.Key, staging.Length, slice, cancellationToken).ConfigureAwait(false); }
                    catch (ObjectStoreException) { read = new(ReadState.Corrupt); }
                    if (read.State != ReadState.Present) { await staging.AbandonAsync().ConfigureAwait(false); return null; }
                    await staging.AppendAsync(slice, cancellationToken).ConfigureAwait(false);
                }
                var put = await staging.CommitAsync(cancellationToken).ConfigureAwait(false);
                return put.Outcome == PutOutcome.Refused ? null : Sink.FromPut(put, ServedFrom.SharedStore, declared);
            }
            catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.StagingRejected)
            {
                // Bad shared bytes: drop them rather than resume a CDN transfer on top of them.
                await staging.AbandonAsync().ConfigureAwait(false);
                return null;
            }
            catch (ObjectStoreException ex) { return new(request.Key, ObjectState.StoreFailed, Code: ex.Code, Message: ex.Message); }
        }
    }

    private sealed class Sink(ObjectSource owner) : IFetchSink
    {
        public Task<IReadOnlyList<ObjectResult>> DeliverAsync(IReadOnlyList<VerifiedObject> objects, bool urgent, CancellationToken cancellationToken) =>
            PutAsync(objects, ObjectOrigin.Cdn, ServedFrom.Cdn, cancellationToken, urgent);

        /// <summary>Urgent (interactive) batches commit at once; background ones join the store's group commit (D123).</summary>
        public async Task<IReadOnlyList<ObjectResult>> PutAsync(IReadOnlyList<VerifiedObject> objects, ObjectOrigin origin, ServedFrom servedFrom,
            CancellationToken cancellationToken, bool urgent = true)
        {
            IReadOnlyList<PutResult> put;
            try
            {
                put = await owner.writer.PutAsync(objects.Select(o => new PutObject(o.Request.Key, o.DeclaredLength, o.Bytes, origin)).ToArray(),
                    commitNow: urgent, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectStoreException ex)
            { return objects.Select(o => new ObjectResult(o.Request.Key, ObjectState.StoreFailed, Code: ex.Code, Message: ex.Message)).ToArray(); }
            return objects.Select((o, i) =>
            {
                var r = FromPut(put[i], servedFrom, o.DeclaredLength);
                return r.IsPresent ? r with { Bytes = o.Bytes.ToArray() } : r;
            }).ToArray();
        }

        internal static ObjectResult FromPut(PutResult put, ServedFrom servedFrom, long length) => put.Outcome switch
        {
            PutOutcome.Stored => new(put.Key, ObjectState.Present, servedFrom, servedFrom == ServedFrom.SharedStore ? ObjectOrigin.SharedStore : ObjectOrigin.Cdn, length),
            // Another caller stored it first: present, and its recorded origin is whatever that caller recorded.
            PutOutcome.AlreadyPresent => new(put.Key, ObjectState.Present, ServedFrom.Store, null, length),
            PutOutcome.Refused => new(put.Key, ObjectState.Refused, Code: put.Code, Message: put.Message),
            _ => new(put.Key, ObjectState.StoreFailed, Code: "OBJECT_ACCESS_PUT_CANCELED", Message: put.Message),
        };

        public async Task<ObjectResult> StreamLargeAsync(ObjectRequest request, long declaredLength,
            Func<long, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task<CdnReply?>> fetch, CancellationToken cancellationToken)
        {
            (string? Code, string? Message) rejected = default;
            try
            {
                // A refused chunk cuts the staging back to its proven bytes; a new staging resumes there from the next host.
                // A refused commit (the whole-object check) leaves nothing staged, so the next host starts from 0.
                while (true)
                {
                    await using var staging = await owner.writer.StageAsync(request.Key, declaredLength, ObjectOrigin.Cdn, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        // A resumed staging keeps only chunk-proven bytes: fetch the rest as one range.
                        if (await fetch(staging.Length, (bytes, token) => new ValueTask(staging.AppendAsync(bytes, token)), cancellationToken).ConfigureAwait(false) is null)
                            return new(request.Key, ObjectState.Refused, Code: rejected.Code ?? ObjectStoreCodes.StagingRejected,
                                Message: rejected.Message ?? "Every host's bytes were refused.");
                    }
                    catch (CdnException failure) { return CdnFetcher.FromCdnFailure(request.Key, failure); }
                    catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.StagingRejected) { rejected = (ex.Code, ex.Message); continue; }
                    var put = await staging.CommitAsync(cancellationToken).ConfigureAwait(false);
                    if (put.Outcome == PutOutcome.Refused) { rejected = (put.Code, put.Message); continue; }
                    return FromPut(put, ServedFrom.Cdn, declaredLength);
                }
            }
            catch (ObjectStoreException ex) when (ex.Code == ObjectStoreCodes.StagingRejected)
            { return new(request.Key, ObjectState.Refused, Code: ex.Code, Message: ex.Message); }
            catch (ObjectStoreException ex)
            { return new(request.Key, ObjectState.StoreFailed, Code: ex.Code, Message: ex.Message); }
        }

        public async ValueTask<ObjectResult?> AlreadyHaveAsync(ObjectKey key, CancellationToken cancellationToken) =>
            await owner.store.HasAsync(key, cancellationToken).ConfigureAwait(false) ? new ObjectResult(key, ObjectState.Present, ServedFrom.Store) : null;
    }
}
