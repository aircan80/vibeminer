using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// Where missing objects come from: the product's CDN route (from the current <c>cdns</c> reply) and the CDN config's
/// archive list, which orders merged ranges and picks one location for an EKey two archives list.
/// </summary>
public sealed record CdnContext(CdnRoute Route, IReadOnlyList<CdnKey>? Archives = null);

/// <summary>
/// One object wanted. <see cref="DeclaredLength"/> is what its source declared (an EN row, an index entry, a config's size
/// pair), never a CDN config <c>*-index-size</c> hint. <see cref="Locations"/> are the archive index entries that list it:
/// with any, it is fetched as a byte range of an archive; without, as a loose object. <see cref="Order"/> groups requests
/// in one <see cref="IObjectSource.GetKeysAsync"/> call: lower groups are fetched first (Download all puts DOWNLOAD priority 0 first).
/// </summary>
public sealed record ObjectRequest(ObjectKey Key, long? DeclaredLength = null, IReadOnlyList<ArchiveLocation>? Locations = null, int Order = 0);

public enum ObjectState
{
    /// <summary>The bytes passed their key's check. They came from <see cref="ObjectResult.ServedFrom"/>.</summary>
    Present,
    /// <summary>Every CDN host answered 404 at <see cref="ObjectResult.ObservedAt"/>. A dated, retryable observation; never absence.</summary>
    NotFound,
    /// <summary>The CDN couldn't be used just now (offline, timeout, 5xx, broken connection). Retryable. See the code.</summary>
    Unavailable,
    /// <summary>The bytes or the declarations are wrong (key check, size conflict, overlap) or the request breaks a rule. See the code.</summary>
    Refused,
    /// <summary>Not stored, and this source has no network (local-only).</summary>
    NeedsNetwork,
    /// <summary>The object store couldn't take the bytes (for example <c>OBJECT_STORE_DISK_FULL</c>). See the code.</summary>
    StoreFailed,
}

/// <summary>Where this call got the bytes from.</summary>
public enum ServedFrom { Store, SharedStore, Cdn }

/// <summary>
/// The outcome for one key. <see cref="Bytes"/> is set only by <see cref="IObjectSource.GetKeyAsync"/> for an object that fits
/// in memory (64 MiB); <see cref="Length"/> is set whenever the object is present. <see cref="Origin"/> is what the store
/// recorded about where its copy first came from.
/// </summary>
public sealed record ObjectResult(ObjectKey Key, ObjectState State, ServedFrom? ServedFrom = null, ObjectOrigin? Origin = null,
    long? Length = null, byte[]? Bytes = null, string? Code = null, string? Message = null, DateTimeOffset? ObservedAt = null)
{
    public bool IsPresent => State == ObjectState.Present;
    /// <summary>NotFound, Unavailable and NeedsNetwork may succeed later; Refused and StoreFailed need something to change first.</summary>
    public bool Retryable => State is ObjectState.NotFound or ObjectState.Unavailable or ObjectState.NeedsNetwork;
}

/// <summary>
/// A kind of dated build observation. The numbers are the history log's record kinds. The wago.tools build list (#96) adds a
/// kind here, a history kind in layer 1 and an <see cref="IObservationSource"/>; nothing else changes. <see cref="Summary"/> is
/// the version service's list of every product; it has no product.
/// </summary>
public enum ObservationKind { Versions = 1, Cdns = 2, Bgdl = 3, Summary = 4 }

/// <summary>One observation to make. <see cref="Product"/> is "" for <see cref="ObservationKind.Summary"/>.</summary>
public sealed record ObservationRequest(ObservationKind Kind, string Product, string Region);

/// <summary>One raw reply, exactly as served, with the URL and time it was observed.</summary>
public sealed record Observation(ObservationKind Kind, string Product, string Region, Uri Url, DateTimeOffset ObservedAt, byte[] Bytes);

/// <summary>An outside source of dated build observations, behind a layer 1 client. Throws <see cref="CdnException"/> on failure.</summary>
public interface IObservationSource
{
    IReadOnlyCollection<ObservationKind> Kinds { get; }
    Task<Observation> FetchAsync(ObservationRequest request, CancellationToken cancellationToken);
}

/// <summary>The version service: versions, cdns, bgdl and the summary.</summary>
public sealed class VersionServiceObservations(IVersionClient client) : IObservationSource
{
    public IReadOnlyCollection<ObservationKind> Kinds { get; } = [ObservationKind.Versions, ObservationKind.Cdns, ObservationKind.Bgdl, ObservationKind.Summary];
    public async Task<Observation> FetchAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await client.FetchAsync(request.Product, request.Region, (VersionDocumentKind)(int)request.Kind, cancellationToken).ConfigureAwait(false);
        return new(request.Kind, reply.Product, reply.Region, reply.Url, reply.ObservedAt, reply.Bytes);
    }
}

/// <summary>An observation and whether it is now in the history log.</summary>
public sealed record ObservationResult(ObjectState State, Observation? Observation = null, bool Recorded = false, string? Code = null,
    string? Message = null, DateTimeOffset? ObservedAt = null);

/// <summary>Objects finished so far in one <see cref="IObjectSource.GetKeysAsync"/> call, and the CDN bytes fetched (gap bytes included).</summary>
public readonly record struct ObjectProgress(long Done, long Total, long CdnBytes);

/// <summary>
/// Layer 2 object access: the only place that decides local or CDN. Implementations: <see cref="ObjectSource"/> (store, then
/// the shared dev store, then the CDN), <see cref="CdnObjectSource"/> (CDN only, nothing stored) and
/// <see cref="LocalObjectSource"/> (local only, a true offline mode). Layers above test against the in-memory fake.
/// </summary>
public interface IObjectSource
{
    /// <summary>One object with its bytes (when they fit in 64 MiB). Concurrent calls for the same key share one transfer.</summary>
    Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken);

    /// <summary>
    /// Makes every object present and returns one result per request, in request order, without bytes. Missing objects are
    /// planned into merged ranges by index membership; one bad object fails only itself.
    /// </summary>
    Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/> of the object, fetching the object first if needed.</summary>
    Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken);

    /// <summary>Fetches one dated build observation and records it in the history log (when this source has one).</summary>
    Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// How many of the keys the profile's own store holds (index only, no bytes read). The Downloaded figure counts this way;
    /// a CDN-only source reports 0.
    /// </summary>
    Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken);

    /// <summary>Whether the profile's own store holds each key, in key order (index only, no bytes read, no CDN call). A CDN-only source answers false for every key.</summary>
    Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken);

    /// <summary>The history log, oldest first, filtered and paged. Read an entry's reply with <see cref="GetKeyAsync"/> on its document key.</summary>
    Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);
}

public sealed record ObjectSourceOptions
{
    public StreamOptions Streams { get; init; } = new();
    public RangePlanOptions Ranges { get; init; } = new();
    /// <summary>Response bytes background work holds in memory across all its streams at once. An object of unknown length counts as 4 MiB.</summary>
    public long InFlightBytes { get; init; } = 256L << 20;
    /// <summary>Response bytes interactive work may hold at once, apart from <see cref="InFlightBytes"/>.</summary>
    public long InteractiveInFlightBytes { get; init; } = 64L << 20;
    /// <summary>Objects larger than this are streamed into staging instead of memory (the store's whole-Put limit).</summary>
    public long InMemoryObjectBytes { get; init; } = 64L << 20;
    /// <summary>A host that failed is tried last for this long.</summary>
    public TimeSpan HostCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
