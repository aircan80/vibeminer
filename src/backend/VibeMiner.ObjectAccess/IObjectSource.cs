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
    /// <summary>
    /// Nothing is wrong with the object or the request, but this source can't fetch it that way (an archive entry over 64 MiB,
    /// <c>OBJECT_LOCATION_TOO_LARGE</c>; a whole object over 64 MiB from the CDN-only source, <c>OBJECT_ACCESS_TOO_LARGE</c>). Not retryable.
    /// </summary>
    Unsupported,
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
    /// <summary>NotFound, Unavailable and NeedsNetwork may succeed later; Refused, StoreFailed and Unsupported need something to change first.</summary>
    public bool Retryable => State is ObjectState.NotFound or ObjectState.Unavailable or ObjectState.NeedsNetwork;
}

/// <summary>
/// A kind of dated observation. The numbers are the history log's record kinds. The wago.tools build list (#96) adds a
/// kind here, a history kind in layer 1 and an <see cref="IObservationSource"/>; nothing else changes. <see cref="Summary"/> is
/// the version service's list of every product; it has no product. <see cref="Definition"/> is one WoWDBDefs table definition
/// (D081), served by <see cref="WoWDBDefsObservations"/>. <see cref="ListfileRelease"/> is the wow-listfile's latest GitHub
/// release (the release JSON) and <see cref="ListfilePart"/> one fixed 8 MiB part of that release's CSV asset.
/// <see cref="ListfileDelta"/> is a release recorded against the one before it, in layer 4's format (opaque here): no source
/// serves it, so it is never fetched, only recorded with <see cref="IObjectSource.RecordAsync"/>.
/// </summary>
public enum ObservationKind { Versions = 1, Cdns = 2, Bgdl = 3, Summary = 4, Definition = 5, ListfileRelease = 6, ListfilePart = 7, ListfileDelta = 8 }

/// <summary>
/// One observation to make. <see cref="Product"/> is "" for <see cref="ObservationKind.Summary"/>. A
/// <see cref="ObservationKind.Definition"/> request is <see cref="Product"/> = the table id (8 lower hex, the manifest's table
/// hash), <see cref="Region"/> = <see cref="DefinitionRegion"/> and <see cref="Name"/> = the exact table name. Only a definition
/// request has a <see cref="Name"/>. A <see cref="ObservationKind.ListfileRelease"/> request is <see cref="Product"/> =
/// <see cref="ListfileProduct"/> and <see cref="Region"/> = <see cref="ListfileReleaseRegion"/>. A
/// <see cref="ObservationKind.ListfilePart"/> request is <see cref="Product"/> = the release tag (1-64 of [a-z0-9_-]) and
/// <see cref="Region"/> = <see cref="ListfilePartRegion"/> of the part index. A <see cref="ObservationKind.ListfileDelta"/> is
/// never fetched. A request that breaks these rules is Refused with <c>OBSERVATION_REQUEST_INVALID</c>.
/// With <see cref="Record"/> false the observation is fetched and returned but nothing is written to the history log or the
/// store (<see cref="ObservationResult.Recorded"/> false); the caller can record it later with <see cref="IObjectSource.RecordAsync"/>.
/// </summary>
public sealed record ObservationRequest(ObservationKind Kind, string Product, string Region, string? Name = null, bool Record = true)
{
    /// <summary>The region every definition observation is recorded under.</summary>
    public const string DefinitionRegion = "wowdbdefs";
    /// <summary>The product every listfile release observation is recorded under.</summary>
    public const string ListfileProduct = "wowlistfile";
    /// <summary>The region every listfile release observation is recorded under.</summary>
    public const string ListfileReleaseRegion = "latest";
    /// <summary>The size of every listfile part but the last.</summary>
    public const int ListfilePartBytes = 8 * 1024 * 1024;
    /// <summary>The number of listfile parts a release can have (0..63, so an asset of at most 512 MiB).</summary>
    public const int ListfileMaxParts = 64;
    /// <summary>The region every listfile delta is recorded under.</summary>
    public const string ListfileDeltaRegion = "delta";
    /// <summary>The largest listfile delta: a history document's limit, 16 MiB.</summary>
    public const int ListfileMaxDeltaBytes = 16 * 1024 * 1024;

    /// <summary>The region a listfile part is recorded under: "p" and the index in decimal, no leading zeros.</summary>
    public static string ListfilePartRegion(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ListfileMaxParts);
        return "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The part index a region names ("p0".."p63", no leading zeros), or null for any other region.</summary>
    public static int? ListfilePartIndex(string? region)
    {
        if (region is not { Length: 2 or 3 } || region[0] != 'p' || region.Skip(1).Any(c => c is < '0' or > '9')) return null;
        if (region.Length == 3 && region[1] == '0') return null;
        var index = region.Length == 2 ? region[1] - '0' : (region[1] - '0') * 10 + (region[2] - '0');
        return index < ListfileMaxParts ? index : null;
    }

    /// <summary>Whether a listfile release tag is 1-64 of [a-z0-9_-].</summary>
    internal static bool ValidListfileTag(string? tag) =>
        tag is { Length: > 0 and <= 64 } && tag.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// Why <see cref="IObjectSource.RecordAsync"/> refuses this observation, or null. Only a listfile part or delta the caller
    /// holds is recorded: a part is the release tag, <see cref="ListfilePartRegion"/>, <see cref="WowListfileObservations.AssetUrl"/>
    /// of the tag and 1 byte to 8 MiB; a delta the tag, <see cref="ListfileDeltaRegion"/>, the same URL and 1 byte to 16 MiB.
    /// </summary>
    public static string? RecordProblem(Observation? observation)
    {
        if (observation is null) return "There is no observation to record.";
        var (kind, limit) = observation.Kind switch
        {
            ObservationKind.ListfilePart => ("listfile part", ListfilePartBytes),
            ObservationKind.ListfileDelta => ("listfile delta", ListfileMaxDeltaBytes),
            _ => ((string?)null, 0),
        };
        if (kind is null) return $"Only listfile parts and deltas are recorded from a held observation, not {observation.Kind}.";
        if (!ValidListfileTag(observation.Product)) return $"A {kind}'s product is the release tag, 1-64 of [a-z0-9_-].";
        if (observation.Kind == ObservationKind.ListfilePart ? ListfilePartIndex(observation.Region) is null : observation.Region != ListfileDeltaRegion)
            return observation.Kind == ObservationKind.ListfilePart ? "A listfile part's region is \"p0\"..\"p63\", no leading zeros."
                : $"A listfile delta's region is \"{ListfileDeltaRegion}\".";
        if (observation.Url is null || observation.Url.AbsoluteUri != WowListfileObservations.AssetUrl(observation.Product))
            return $"A {kind}'s URL is its release's asset address.";
        if (observation.Bytes is not { Length: > 0 } bytes || bytes.Length > limit) return $"A {kind} is 1 to {limit} bytes.";
        return null;
    }

    /// <summary>Why this request's shape is refused, or null. Product and region rules of the other kinds are their client's.</summary>
    public string? Problem()
    {
        if (Kind == ObservationKind.ListfileDelta) return "A listfile delta is never fetched; record it with RecordAsync.";
        if (Kind == ObservationKind.ListfileRelease)
        {
            if (Product != ListfileProduct || Region != ListfileReleaseRegion)
                return $"A listfile release request is product \"{ListfileProduct}\", region \"{ListfileReleaseRegion}\".";
            return Name is null ? null : "Only a definition request has a name, not ListfileRelease.";
        }
        if (Kind == ObservationKind.ListfilePart)
        {
            if (!ValidListfileTag(Product)) return "A listfile part request's product is the release tag, 1-64 of [a-z0-9_-].";
            if (ListfilePartIndex(Region) is null) return "A listfile part request's region is \"p0\"..\"p63\", no leading zeros.";
            return Name is null ? null : "Only a definition request has a name, not ListfilePart.";
        }
        if (Kind != ObservationKind.Definition)
            return Name is null ? null : $"Only a definition request has a name, not {Kind}.";
        if (Product is not { Length: 8 } || Product.Any(c => c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))))
            return "A definition request's product is the table id, 8 lower hex digits.";
        if (Region != DefinitionRegion) return $"A definition request's region is \"{DefinitionRegion}\".";
        if (Name is not { Length: > 0 and <= 128 } || Name.Any(c => c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')))
            return "A definition request names its table: 1-128 of [A-Za-z0-9_-].";
        return null;
    }
}

/// <summary>One raw reply, exactly as served, with the URL and time it was observed.</summary>
public sealed record Observation(ObservationKind Kind, string Product, string Region, Uri Url, DateTimeOffset ObservedAt, byte[] Bytes);

/// <summary>
/// An outside source of dated observations, behind a layer 1 client. Throws <see cref="CdnException"/> on failure and
/// <see cref="ArgumentException"/> for a request it refuses. Layer 2 refuses a request whose shape fails
/// <see cref="ObservationRequest.Problem"/> (Refused, <c>OBSERVATION_REQUEST_INVALID</c>) before any source runs; any other
/// argument error propagates.
/// </summary>
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

/// <summary>
/// WoWDBDefs table definitions (D081): serves only <see cref="ObservationKind.Definition"/>. The observation's URL is the raw
/// file at the commit it came from, and its time the reply's. A request of the wrong shape is refused before the client is asked.
/// </summary>
public sealed class WoWDBDefsObservations(IWoWDBDefsClient client) : IObservationSource
{
    public IReadOnlyCollection<ObservationKind> Kinds { get; } = [ObservationKind.Definition];

    /// <summary>The raw definition URL at a commit, as the client builds it.</summary>
    public static string DefinitionUrl(string revision, string tableName) => WoWDBDefsClient.DefinitionUrl(revision, tableName);

    /// <summary>The 40-hex commit an exact raw definition URL for <paramref name="tableName"/> names, or null for any other URL.</summary>
    public static string? DefinitionRevision(string url, string tableName)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(tableName);
        var prefix = $"https://{WoWDBDefsClient.RawHost}/{WoWDBDefsClient.Repository}/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || url.Length != prefix.Length + 40 + $"/definitions/{tableName}.dbd".Length) return null;
        var revision = url.Substring(prefix.Length, 40);
        if (!revision.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        return url == WoWDBDefsClient.DefinitionUrl(revision, tableName) ? revision : null;
    }
    public async Task<Observation> FetchAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind != ObservationKind.Definition) throw new ArgumentException($"WoWDBDefs serves only definitions, not {request.Kind}.", nameof(request));
        if (request.Problem() is { } problem) throw new ArgumentException(problem, nameof(request));
        var reply = await client.FetchAsync(request.Name!, cancellationToken).ConfigureAwait(false);
        return new(ObservationKind.Definition, request.Product, request.Region, reply.Url, reply.ObservedAt, reply.Bytes);
    }
}

/// <summary>
/// The wow-listfile's GitHub releases (D105): serves <see cref="ObservationKind.ListfileRelease"/> (the latest release JSON) and
/// <see cref="ObservationKind.ListfilePart"/> (one 8 MiB part of a release's CSV asset). A part's URL is the github.com asset
/// address, never the signed address it redirected to. A request of the wrong shape is refused before the client is asked; a part
/// that isn't exactly the one asked for (address, offset, length, a file of at most 512 MiB) is a Protocol failure.
/// </summary>
public sealed class WowListfileObservations(IWowListfileClient client) : IObservationSource
{
    public IReadOnlyCollection<ObservationKind> Kinds { get; } = [ObservationKind.ListfileRelease, ObservationKind.ListfilePart];

    /// <summary>The release's CSV asset name.</summary>
    public const string AssetName = WowListfileClient.AssetName;
    /// <summary>The github.com address every part of release <paramref name="tag"/>'s asset is recorded under.</summary>
    public static string AssetUrl(string tag) => WowListfileClient.AssetUrl(tag).AbsoluteUri;
    /// <summary>The address the latest release JSON is recorded under.</summary>
    public static string ReleaseUrl => WowListfileClient.LatestReleaseUrl.AbsoluteUri;

    public async Task<Observation> FetchAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is not (ObservationKind.ListfileRelease or ObservationKind.ListfilePart))
            throw new ArgumentException($"The wow-listfile serves only releases and parts, not {request.Kind}.", nameof(request));
        if (request.Problem() is { } problem) throw new ArgumentException(problem, nameof(request));
        if (request.Kind == ObservationKind.ListfileRelease)
        {
            var reply = await client.FetchReleaseAsync(cancellationToken).ConfigureAwait(false);
            return new(ObservationKind.ListfileRelease, request.Product, request.Region, reply.Url, reply.ObservedAt, reply.Bytes);
        }
        var index = ObservationRequest.ListfilePartIndex(request.Region)!.Value;
        var part = await client.FetchPartAsync(request.Product, index, cancellationToken).ConfigureAwait(false);
        var asset = new Uri(AssetUrl(request.Product));
        if (part.Url is null || part.Url.AbsoluteUri != asset.AbsoluteUri) throw Protocol(asset, part.ObservedAt, $"The wow-listfile client returned part {index} from another address.");
        if (part.Offset != (long)index * ObservationRequest.ListfilePartBytes)
            throw Protocol(asset, part.ObservedAt, $"The wow-listfile client returned bytes at offset {part.Offset} for part {index}.");
        if (part.TotalLength <= part.Offset || part.TotalLength > WowListfileClient.MaxAssetBytes)
            throw Protocol(asset, part.ObservedAt, $"The wow-listfile client returned part {index} of a file of {part.TotalLength} bytes.");
        if (part.Bytes is null || part.Bytes.Length != Math.Min(ObservationRequest.ListfilePartBytes, part.TotalLength - part.Offset))
            throw Protocol(asset, part.ObservedAt, $"The wow-listfile client returned part {index} with the wrong number of bytes.");
        return new(ObservationKind.ListfilePart, request.Product, request.Region, part.Url, part.ObservedAt, part.Bytes);
    }

    private static CdnException Protocol(Uri url, DateTimeOffset at, string message) =>
        new([new CdnAttempt(url.Host, url, CdnFailure.Protocol, null, at, message)]);
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

    /// <summary>
    /// Fetches one dated build observation and records it in the history log (when this source has one and
    /// <see cref="ObservationRequest.Record"/> is true).
    /// </summary>
    Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Records an observation the caller holds (one fetched with <see cref="ObservationRequest.Record"/> false, or a listfile
    /// delta derived from such) as one history entry, exactly as <see cref="ObserveAsync"/> records a fetched one. Only listfile
    /// parts and deltas are accepted; anything failing <see cref="ObservationRequest.RecordProblem"/> is Refused with
    /// <c>OBSERVATION_REQUEST_INVALID</c>. A store failure is StoreFailed. A source without a history log answers as its
    /// <see cref="ObserveAsync"/> does when it can't record.
    /// </summary>
    Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken);

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
    /// <summary>
    /// A range read of a loose encoded object at least this long that isn't stored fetches only the BLTE header and the chunks
    /// under the range, each proven by its MD5, and stores nothing (D340). Smaller objects are fetched whole and stored first.
    /// </summary>
    public long RangeFetchMinBytes { get; init; } = 64L << 20;
    /// <summary>A host that failed is tried last for this long.</summary>
    public TimeSpan HostCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
