using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Builds;

/// <summary>
/// <see cref="MaxOpenBuilds"/>: opened builds kept for reuse. <see cref="IndexParallelism"/>: archive indexes read and
/// parsed at once. <see cref="MaxEncodedBytes"/> and <see cref="MaxDecodedBytes"/> bound one metadata object (ENCODING,
/// ROOT, an index) before and after BLTE; past them an open is Unsupported <c>BUILD_METADATA_TOO_LARGE</c>.
/// <see cref="ReadChunkBytes"/>: range size for an object too large for one in-memory read.
/// <see cref="MaxConcurrentOpens"/>: opens running at once.
/// </summary>
public sealed record BuildOpenerOptions
{
    public int MaxOpenBuilds { get; init; } = 4;
    public int IndexParallelism { get; init; } = 4;
    public long MaxEncodedBytes { get; init; } = 512L << 20;
    public long MaxDecodedBytes { get; init; } = 1L << 30;
    public int ReadChunkBytes { get; init; } = 8 << 20;
    /// <summary>Opens of different builds that read and parse at once; the rest wait their turn (each can hold a large ENCODING and ROOT).</summary>
    public int MaxConcurrentOpens { get; init; } = 2;

    internal void Validate()
    {
        if (MaxOpenBuilds < 1 || MaxConcurrentOpens < 1 || IndexParallelism < 1 || ReadChunkBytes < 1 || MaxEncodedBytes < 1 || MaxEncodedBytes > Array.MaxLength ||
            MaxDecodedBytes < 1 || MaxDecodedBytes > Blte.MaxDecodedBytes)
            throw new ArgumentException("Build options need positive limits within the BLTE limits.");
    }
}

/// <summary>
/// Opens builds through layer 2 at interactive priority: the newest <c>cdns</c> route, the build and CDN configs, ENCODING,
/// ROOT, then every archive index and the file-index. Stops at the first failure and never retries; what isn't stored and
/// can't be fetched comes back as NeedsNetwork. Opened builds are reused (see <see cref="IBuilds"/>).
/// </summary>
public sealed class BuildOpener : IBuilds
{
    private const string TooLarge = "BUILD_METADATA_TOO_LARGE";
    private const int HistoryPage = 1000;
    /// <summary>Distinct <c>cdns</c> replies read per open, the most recently observed ones.</summary>
    internal const int MaxCdnsReplies = 32;
    private readonly IObjectSource source;
    private readonly BuildOpenerOptions options;
    private readonly OpenCache cache;

    public BuildOpener(IObjectSource source, BuildOpenerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new();
        options.Validate();
        this.source = source; this.options = options;
        cache = new OpenCache(options.MaxOpenBuilds, options.MaxConcurrentOpens);
    }

    public Task<BuildOpenResult> OpenAsync(BuildRef build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        return cache.OpenAsync(build, token => OpenOnceAsync(build, token), cancellationToken);
    }

    private sealed class OpenFailed(BuildOpenState state, BuildPart part, string code, string message, DateTimeOffset? observedAt = null)
        : Exception(message)
    {
        public BuildOpenState State { get; } = state;
        public BuildPart Part { get; } = part;
        public string Code { get; } = code;
        public DateTimeOffset? ObservedAt { get; } = observedAt;
    }

    private async Task<BuildOpenResult> OpenOnceAsync(BuildRef build, CancellationToken ct)
    {
        try { return new(build, BuildOpenState.Open, await ReadBuildAsync(build, ct).ConfigureAwait(false), null, null, null, null); }
        catch (OpenFailed f) { return new(build, f.State, null, f.Part, f.Code, f.Message, f.ObservedAt); }
    }

    private async Task<IBuild> ReadBuildAsync(BuildRef build, CancellationToken ct)
    {
        var route = await RouteAsync(build, ct).ConfigureAwait(false);
        var configCdn = route is null ? null : new CdnContext(route);

        var buildKey = ObjectKey.Parse(ObjectKeyType.Config, build.BuildConfig);
        var cdnKey = ObjectKey.Parse(ObjectKeyType.Config, build.CdnConfig);
        var configs = await source.GetKeysAsync([new(buildKey), new(cdnKey)], configCdn, FetchPriority.Interactive, null, ct).ConfigureAwait(false);
        Check(configs[0], BuildPart.BuildConfig);
        Check(configs[1], BuildPart.CdnConfig);
        var buildBytes = await ReadAsync(new(buildKey), BuildPart.BuildConfig, configCdn, ct).ConfigureAwait(false);
        var buildConfig = await ParseAsync(BuildPart.BuildConfig, () => BuildConfigFile.Parse(buildBytes, ct), ct).ConfigureAwait(false);
        var cdnBytes = await ReadAsync(new(cdnKey), BuildPart.CdnConfig, configCdn, ct).ConfigureAwait(false);
        var cdnConfig = await ParseAsync(BuildPart.CdnConfig, () => CdnConfigFile.Parse(cdnBytes, ct), ct).ConfigureAwait(false);
        var cdn = route is null ? null : new CdnContext(route, cdnConfig.Archives);

        var encodingEKey = buildConfig.Encoding.EKey;
        var encodingBytes = await ReadDecodedAsync(BuildPart.Encoding, encodingEKey, buildConfig.EncodingSize.Encoded,
            buildConfig.Encoding.CKey, buildConfig.EncodingSize.Decoded, cdn, ct).ConfigureAwait(false);
        var encodingLimits = new EncodingLimits(MaxDecodedBytes: options.MaxDecodedBytes);
        var encoding = await ParseAsync(BuildPart.Encoding, () => EncodingTable.Open(encodingBytes, encodingLimits, ct), ct).ConfigureAwait(false);

        if (!encoding.TryFindContent(buildConfig.Root, out var rootContent) || rootContent.EKeys.Count == 0)
            throw new OpenFailed(BuildOpenState.Corrupt, BuildPart.Root, "ROOT_NOT_IN_ENCODING", "ENCODING doesn't list the build's ROOT.");
        var rootEKey = rootContent.EKeys[0];
        long? rootEncoded = encoding.TryFindEncoded(rootEKey, out var rootRow) ? rootRow.EncodedSize : null;
        var rootLimits = new RootLimits();
        if (rootContent.DecodedSize > rootLimits.MaxDecodedBytes)
            throw new OpenFailed(BuildOpenState.Unsupported, BuildPart.Root, TooLarge, "ROOT is over the decoded byte limit.");
        var rootBytes = await ReadDecodedAsync(BuildPart.Root, rootEKey, rootEncoded, buildConfig.Root, rootContent.DecodedSize, cdn, ct).ConfigureAwait(false);
        var root = await ParseAsync(BuildPart.Root, () => RootTable.Parse(rootBytes, rootLimits, ct), ct).ConfigureAwait(false);

        var locations = await ReadIndexesAsync(cdnConfig, cdn, ct).ConfigureAwait(false);
        var metadata = new BuildMetadata(buildConfig.BuildName, buildConfig.Root, buildConfig.Encoding.CKey, encodingEKey,
            cdnConfig.Archives.Count, root.FileCount, BuildLocales.Of(root.LocaleUnion));
        return new OpenedBuild(build, metadata, cdn, encoding, root, locations);
    }

    /// <summary>
    /// The route from the newest readable <c>cdns</c> reply with a row for the region (null: <c>us</c>, then any), newest by
    /// D139: highest numeric <c>## seqn</c>, then latest observed; a reply without a numeric seqn comes after every one with.
    /// Only the <see cref="MaxCdnsReplies"/> most recently observed distinct replies are read. Null when none can be used, so
    /// stored metadata still opens.
    /// </summary>
    private async Task<CdnRoute?> RouteAsync(BuildRef build, CancellationToken ct)
    {
        var latest = new Dictionary<ObjectKey, HistoryEntry>();
        try
        {
            HistoryEntry? after = null;
            while (true)
            {
                var page = await source.ReadHistoryAsync(new(build.Product, null, HistoryKind.Cdns, after, HistoryPage), ct).ConfigureAwait(false);
                foreach (var e in page)
                    if (!latest.TryGetValue(e.Document, out var seen) || seen.ObservedAt < e.ObservedAt) latest[e.Document] = e;
                if (latest.Count > 2 * MaxCdnsReplies) Trim(latest);
                if (page.Count < HistoryPage) break;
                after = page[^1];
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
        Trim(latest);

        var replies = new List<(long? Seqn, DateTimeOffset ObservedAt, VersionsTable Table)>();
        foreach (var entry in latest.Values)
        {
            try
            {
                var reply = await source.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, ct).ConfigureAwait(false);
                if (reply is not { IsPresent: true, Bytes: { } bytes }) continue;
                var table = VersionsTable.Parse(bytes);
                replies.Add((long.TryParse(table.Sequence, out var seqn) ? seqn : null, entry.ObservedAt, table));
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        var region = build.Region ?? "us";
        foreach (var (_, _, table) in replies.OrderBy(r => r.Seqn is null).ThenByDescending(r => r.Seqn).ThenByDescending(r => r.ObservedAt))
        {
            var row = table.Rows.FirstOrDefault(r => r.Fields.TryGetValue("Name", out var name) && name == region)
                ?? (build.Region is null ? table.Rows.FirstOrDefault() : null);
            if (row is null || !row.Fields.TryGetValue("Path", out var path) || !row.Fields.TryGetValue("Hosts", out var hosts)) continue;
            try { return new CdnRoute(hosts.Split(' ', StringSplitOptions.RemoveEmptyEntries), path); }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static void Trim(Dictionary<ObjectKey, HistoryEntry> latest)
    {
        foreach (var old in latest.Values.OrderByDescending(e => e.ObservedAt).Skip(MaxCdnsReplies).Select(e => e.Document).ToArray())
            latest.Remove(old);
    }

    private async Task<ArchiveLocations> ReadIndexesAsync(CdnConfigFile config, CdnContext? cdn, CancellationToken ct)
    {
        var requests = new List<(ObjectRequest Request, BuildPart Part, CdnKey Key)>(config.Archives.Count + 1);
        foreach (var archive in config.Archives)
            requests.Add((new(new ObjectKey(ObjectKeyType.ArchiveIndex, archive.ToBytes())), BuildPart.ArchiveIndex, archive));
        if (config.FileIndex is { } fileIndex)
            requests.Add((new(new ObjectKey(ObjectKeyType.FileIndex, fileIndex.ToBytes())), BuildPart.FileIndex, fileIndex));
        if (requests.Count > 0)
        {
            var present = await source.GetKeysAsync(requests.Select(r => r.Request).ToArray(), cdn, FetchPriority.Interactive, null, ct).ConfigureAwait(false);
            for (var i = 0; i < requests.Count; i++) Check(present[i], requests[i].Part);
        }

        var entries = new IReadOnlyList<ArchiveIndexEntry>[requests.Count];
        var limits = new ArchiveIndexLimits();
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count),
            new ParallelOptions { MaxDegreeOfParallelism = options.IndexParallelism, CancellationToken = ct },
            async (i, token) =>
            {
                var (request, part, key) = requests[i];
                var bytes = await ReadAsync(request, part, cdn, token).ConfigureAwait(false);
                var kind = part == BuildPart.ArchiveIndex ? ArchiveIndexKind.Archive : ArchiveIndexKind.FileIndex;
                entries[i] = Parse(part, () => ArchiveIndex.Read(bytes, key, kind, limits, token));
            }).ConfigureAwait(false);

        var archives = new IndexedArchive[config.Archives.Count];
        for (var i = 0; i < archives.Length; i++) archives[i] = new(config.Archives[i], entries[i]);
        var loose = config.FileIndex is null ? null : entries[^1];
        return await ParseAsync(BuildPart.ArchiveIndex, () => ArchiveLocations.Build(archives, loose, new ArchiveLocationLimits(), ct), ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadDecodedAsync(BuildPart part, CdnKey eKey, long? encodedSize, CdnKey cKey, long decodedSize, CdnContext? cdn,
        CancellationToken ct)
    {
        if (encodedSize > options.MaxEncodedBytes || decodedSize > options.MaxDecodedBytes)
            throw new OpenFailed(BuildOpenState.Unsupported, part, TooLarge, $"The {part} is over the metadata byte limit.");
        var encoded = await ReadAsync(new(new ObjectKey(ObjectKeyType.Encoded, eKey.ToBytes()), encodedSize), part, cdn, ct).ConfigureAwait(false);
        return await DecodeAsync(part, () => Blte.DecodeAsync(encoded, eKey, cKey, decodedSize, options.MaxDecodedBytes, ct), ct).ConfigureAwait(false);
    }

    /// <summary>The object's bytes: whole when layer 2 hands them over, else read by range into one bounded buffer.</summary>
    private async Task<byte[]> ReadAsync(ObjectRequest request, BuildPart part, CdnContext? cdn, CancellationToken ct)
    {
        var got = await source.GetKeyAsync(request, cdn, FetchPriority.Interactive, ct).ConfigureAwait(false);
        Check(got, part);
        if (got.Bytes is { } bytes)
            return bytes.Length > options.MaxEncodedBytes
                ? throw new OpenFailed(BuildOpenState.Unsupported, part, TooLarge, $"The {part} is over the metadata byte limit.")
                : bytes;
        var length = got.Length ?? request.DeclaredLength;
        if (length is not { } known || known > options.MaxEncodedBytes)
            throw new OpenFailed(BuildOpenState.Unsupported, part, TooLarge, $"The {part} is over the metadata byte limit, or its length is unknown.");
        var buffer = new byte[known];
        for (long at = 0; at < known; at += options.ReadChunkBytes)
        {
            var take = (int)Math.Min(options.ReadChunkBytes, known - at);
            Check(await source.GetRangeAsync(request, at, buffer.AsMemory((int)at, take), cdn, FetchPriority.Interactive, ct).ConfigureAwait(false), part);
        }
        return buffer;
    }

    private static void Check(ObjectResult result, BuildPart part)
    {
        if (result.IsPresent) return;
        var state = result.State switch
        {
            ObjectState.NeedsNetwork => BuildOpenState.NeedsNetwork,
            ObjectState.Unavailable when result.Code is "CDN_OFFLINE" or "OBJECT_ACCESS_NO_CDN_CONTEXT" => BuildOpenState.NeedsNetwork,
            ObjectState.Unavailable => BuildOpenState.Unavailable,
            ObjectState.NotFound => BuildOpenState.Missing,
            ObjectState.Refused => BuildOpenState.Corrupt,
            _ => BuildOpenState.StoreFailed,
        };
        var code = result.Code ?? "OBJECT_ACCESS_" + result.State.ToString().ToUpperInvariant();
        throw new OpenFailed(state, part, code, result.Message ?? $"The {part} is {result.State} ({code}).", result.ObservedAt);
    }

    private static Task<T> ParseAsync<T>(BuildPart part, Func<T> parse, CancellationToken ct) => Task.Run(() => Parse(part, parse), ct);

    private static async Task<T> DecodeAsync<T>(BuildPart part, Func<Task<T>> parse, CancellationToken ct)
    {
        try { return await Task.Run(parse, ct).ConfigureAwait(false); }
        catch (BuildFormatException e) { throw Failed(part, e); }
    }

    private static T Parse<T>(BuildPart part, Func<T> parse)
    {
        try { return parse(); }
        catch (BuildFormatException e) { throw Failed(part, e); }
    }

    private static OpenFailed Failed(BuildPart part, BuildFormatException e) => new(e.Failure switch
    {
        BuildFailure.Encrypted => BuildOpenState.Encrypted,
        BuildFailure.Unsupported => BuildOpenState.Unsupported,
        _ => BuildOpenState.Corrupt,
    }, part, e.Code, e.Message);
}

/// <summary>An opened build over its parsed ENCODING, ROOT and archive locations. Read only after construction.</summary>
internal sealed class OpenedBuild(BuildRef build, BuildMetadata metadata, CdnContext? cdn, EncodingTable encoding, RootTable root,
    ArchiveLocations locations) : IBuild, ILedgerSource
{
    private readonly Lazy<ObjectLedger> ledger = new(() => ObjectLedger.From(encoding), LazyThreadSafetyMode.ExecutionAndPublication);

    public BuildRef Build { get; } = build;
    public BuildMetadata Metadata { get; } = metadata;
    public CdnContext? Cdn { get; } = cdn;
    /// <summary>The build's object ledger, built from ENCODING on first use.</summary>
    public ObjectLedger Ledger => ledger.Value;

    public FileResolution Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return root.TryFindByNameHash(RootNameHash.Of(path), out var id) ? Resolve(id) : new(FileResolutionState.UnknownFile, null, []);
    }

    public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => root.FileDataIds(BuildLocales.Bit(locale), fromId);

    public FileResolution Resolve(uint fileDataId)
    {
        var entries = root.Find(fileDataId);
        if (entries.Count == 0) return new(FileResolutionState.UnknownFile, null, []);
        var variants = new FileVariant[entries.Count];
        var any = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!encoding.TryFindContent(e.CKey, out var content) || content.EKeys.Count == 0)
            {
                variants[i] = new(e.LocaleFlags, e.ContentFlags, e.CKey, null, null, null, null);
                continue;
            }
            any = true;
            var eKey = content.EKeys[0];
            ArchiveLocation? location = locations.TryFind(eKey, out var at) ? at : null;
            long? size = encoding.TryFindEncoded(eKey, out var row) ? row.EncodedSize
                : location?.Size ?? (locations.TryFindLoose(eKey, out var loose) ? loose : null);
            variants[i] = new(e.LocaleFlags, e.ContentFlags, e.CKey, content.DecodedSize, eKey, size, location);
        }
        return new(any ? FileResolutionState.Found : FileResolutionState.NotInEncoding, fileDataId, Array.AsReadOnly(variants));
    }
}
