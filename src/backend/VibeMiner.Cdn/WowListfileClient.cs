using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace VibeMiner.Cdn;

/// <summary>The newest wow-listfile release as GitHub's API served it, unparsed, with its URL and when it was observed.</summary>
public sealed record WowListfileReply(Uri Url, DateTimeOffset ObservedAt, byte[] Bytes);

/// <summary>
/// One part of a release's listfile asset: <see cref="Bytes"/> start at <see cref="Offset"/> of a file of
/// <see cref="TotalLength"/> bytes. <see cref="Url"/> is the github.com asset address, never the signed address it redirected to.
/// </summary>
public sealed record WowListfilePart(Uri Url, DateTimeOffset ObservedAt, byte[] Bytes, long Offset, long TotalLength);

/// <summary>Layer 1 wow-listfile client (D105): fetches the release description and the asset in parts. It doesn't cache, parse or verify them.</summary>
public interface IWowListfileClient
{
    /// <summary>
    /// GET <see cref="WowListfileClient.LatestReleaseUrl"/>: the JSON bytes exactly as served, at most 1 MiB. Throws
    /// <see cref="CdnException"/>: Offline (unreachable, connect timeout), NotFound (404), HttpStatus (rate limit, other
    /// statuses), Protocol (redirect, answer over 1 MiB), SizeConflict (more bytes than declared), Timeout (no headers in
    /// time, a stalled read, or the fetch's total cap), Network (interrupted, ended early). The caller's cancellation
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<WowListfileReply> FetchReleaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Bytes [index*PartBytes, min((index+1)*PartBytes, total)) of <see cref="WowListfileClient.AssetUrl"/>. Throws
    /// <see cref="CdnException"/> as <see cref="FetchReleaseAsync"/>, plus HttpStatus for a 416 (the caller asked past the
    /// end it knows from the release), Protocol for a second redirect, a redirect off GitHub's asset hosts or an encoded part,
    /// RangeIgnored for a 200, RangeMismatch for a 206 that isn't exactly this single range, SizeConflict for a body longer
    /// than its range or a Content-Length that differs from it, Network for a body that ends early, and TooLarge for a file
    /// over 512 MiB. The returned task throws <see cref="ArgumentException"/> unless <paramref name="tag"/> is 1-64 of
    /// [a-z0-9_-], and <see cref="ArgumentOutOfRangeException"/> unless <paramref name="index"/> is 0 to
    /// <see cref="WowListfileClient.MaxParts"/> - 1.
    /// </summary>
    Task<WowListfilePart> FetchPartAsync(string tag, int index, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IWowListfileClient"/> over wowdev/wow-listfile's GitHub releases (D105). The bytes are untrusted:
/// HTTPS only, fixed hosts on the default port, no redirect for the release, exactly one redirect to GitHub's asset hosts
/// for a part, single byte ranges checked exactly, a 1 MiB release answer and 8 MiB parts of at most a 512 MiB file, a time
/// budget for each response's headers, an idle budget for each read and a total cap per fetch, one fetch at a time, cancellable. The listfile names files only; all game data comes from
/// Blizzard's CDN. Pass <paramref name="connections"/> when <paramref name="client"/> runs on <see cref="CreateHandler"/>.
/// </summary>
public sealed class WowListfileClient(HttpClient client, TimeProvider? time = null, TimeSpan? fetchBudget = null, WowListfileConnections? connections = null,
    TimeSpan? headerBudget = null, TimeSpan? idleBudget = null)
    : IWowListfileClient
{
    public const string ApiHost = "api.github.com";
    public const string DownloadHost = "github.com";
    public static readonly IReadOnlyList<string> AssetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    public const string Repository = "wowdev/wow-listfile";
    public const string AssetName = "community-listfile-withcapitals.csv";
    public const int PartBytes = 8 << 20;
    public const long MaxAssetBytes = 512L << 20;
    public const int MaxParts = (int)(MaxAssetBytes / PartBytes);
    public const int MaxReleaseResponseBytes = 1 << 20;
    /// <summary>The cap on one whole fetch, every hop and the body.</summary>
    public static readonly TimeSpan DefaultFetchBudget = TimeSpan.FromMinutes(5);
    /// <summary>How long each request may wait for its response headers.</summary>
    public static readonly TimeSpan DefaultHeaderBudget = TimeSpan.FromSeconds(30);
    /// <summary>How long each body read may wait for bytes; every read starts it again.</summary>
    public static readonly TimeSpan DefaultIdleBudget = TimeSpan.FromSeconds(30);
    public static readonly Uri LatestReleaseUrl = new($"https://{ApiHost}/repos/{Repository}/releases/latest");
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly TimeSpan budget = fetchBudget ?? DefaultFetchBudget;
    private readonly TimeSpan headers = headerBudget ?? DefaultHeaderBudget;
    private readonly TimeSpan idle = idleBudget ?? DefaultIdleBudget;
    private readonly SemaphoreSlim fetches = new(1, 1);

    public static Uri AssetUrl(string tag) => new($"https://{DownloadHost}/{Repository}/releases/download/{ValidTag(tag)}/{AssetName}");

    /// <summary>
    /// The listfile's own HTTP handler: no automatic redirects or decompression, one connection per host, a 10 s connect
    /// timeout, and every new connection taken from <paramref name="connections"/>, which each fetch resets. .NET's pool
    /// opens a new connection again when one closes before any response, so a proxy that drops connections made one
    /// request open 16 of them; with the budget a fetch opens at most <see cref="WowListfileConnections.PerFetch"/>.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(WowListfileConnections connections) => new()
    {
        AllowAutoRedirect = false, MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, token) =>
        {
            if (!connections.TryTake())
                throw new HttpRequestException(HttpRequestError.ConnectionError, "The listfile fetch used its connection budget.");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public Task<WowListfileReply> FetchReleaseAsync(CancellationToken cancellationToken) =>
        FetchAsync(LatestReleaseUrl, async token =>
        {
            using var request = Request(LatestReleaseUrl, "application/vnd.github+json", null);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await SendAsync(request, token);
            var observed = time.GetUtcNow();
            Check(response, LatestReleaseUrl, "GitHub has no latest wow-listfile release.");
            if (response.Content.Headers.ContentLength > MaxReleaseResponseBytes) throw ReleaseTooLarge(response.StatusCode);
            var bytes = await ReadAsync(response, LatestReleaseUrl, MaxReleaseResponseBytes, token);
            if (bytes.Length > MaxReleaseResponseBytes) throw ReleaseTooLarge(response.StatusCode);
            if (response.Content.Headers.ContentLength is { } declared && declared != bytes.Length)
                throw bytes.Length < declared
                    ? Fail(LatestReleaseUrl, CdnFailure.Network, Failed("The download ended early."), response.StatusCode)
                    : Fail(LatestReleaseUrl, CdnFailure.SizeConflict, Failed("GitHub sent more bytes than it declared."), response.StatusCode);
            return new WowListfileReply(LatestReleaseUrl, observed, bytes);
        }, cancellationToken);

    public async Task<WowListfilePart> FetchPartAsync(string tag, int index, CancellationToken cancellationToken)
    {
        var asset = AssetUrl(tag);
        if (index is < 0 or >= MaxParts) throw new ArgumentOutOfRangeException(nameof(index), index, $"A part index is 0 to {MaxParts - 1}.");
        long start = (long)index * PartBytes;
        return await FetchAsync(asset, token => PartAsync(asset, tag, start, token), cancellationToken);
    }

    private async Task<T> FetchAsync<T>(Uri url, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        await fetches.WaitAsync(cancellationToken);
        try
        {
            // Each fetch gets a fresh, small connection budget (see CreateHandler).
            connections?.Reset();
            using var limit = new CancellationTokenSource(budget, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
            try { return await fetch(linked.Token); }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Fail(url, CdnFailure.Timeout, Failed($"The download from GitHub didn’t finish within {Seconds(budget)} seconds."));
            }
        }
        finally { fetches.Release(); }
    }

    // github.com answers with one redirect to its release asset host; nothing else is followed.
    private async Task<WowListfilePart> PartAsync(Uri asset, string tag, long start, CancellationToken token)
    {
        var uri = asset;
        for (var hop = 0; ; hop++)
        {
            using var request = Request(uri, "application/octet-stream", start);
            using var response = await SendAsync(request, token);
            var observed = time.GetUtcNow();
            var status = response.StatusCode;
            if ((int)status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop > 0) throw Fail(uri, CdnFailure.Protocol, Failed("GitHub redirected the download more than once."), status);
                var location = response.Headers.Location;
                if (location is null || !location.IsAbsoluteUri || !Official(location) || !AssetHosts.Contains(location.Host))
                    throw Fail(uri, CdnFailure.Protocol, Failed("GitHub redirected the download to an address VibeMiner doesn’t allow."), status);
                uri = location;
                continue;
            }
            Check(response, uri, $"The wow-listfile release {tag} has no {AssetName} file.");
            // A 200 is the whole file: the range was ignored, and the body is never read.
            if (status == HttpStatusCode.OK)
                throw Fail(uri, CdnFailure.RangeIgnored, Failed("GitHub sent the whole file instead of the asked part."), status);
            if (status != HttpStatusCode.PartialContent)
                throw Fail(uri, CdnFailure.HttpStatus, Failed($"GitHub answered HTTP {((int)status).ToString(CultureInfo.InvariantCulture)}."), status);
            var headers = response.Content.Headers;
            if (headers.ContentType?.MediaType is { } media && media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase)
                || headers.ContentRange is not { Unit: "bytes", HasRange: true, HasLength: true, From: { } from, To: { } to, Length: { } total })
                throw Fail(uri, CdnFailure.RangeMismatch, Failed("GitHub sent a part VibeMiner didn’t ask for."), status);
            if (total > MaxAssetBytes) throw Fail(uri, CdnFailure.TooLarge, TooLarge(), status);
            var end = Math.Min(start + PartBytes, total) - 1;
            if (from != start || to != end || start >= total)
                throw Fail(uri, CdnFailure.RangeMismatch, Failed("GitHub sent a part VibeMiner didn’t ask for."), status);
            // Encoded bytes aren't the file's bytes at these offsets.
            if (headers.ContentEncoding.Count > 0)
                throw Fail(uri, CdnFailure.Protocol, Failed("GitHub sent an encoded part."), status);
            var expected = (int)(end - start + 1);
            if (headers.ContentLength is { } declared && declared != expected)
                throw Fail(uri, CdnFailure.SizeConflict, Failed("GitHub sent a part of another length than its range."), status);
            var bytes = await ReadAsync(response, uri, expected, token);
            if (bytes.Length > expected)
                throw Fail(uri, CdnFailure.SizeConflict, Failed("GitHub sent a part of another length than its range."), status);
            if (bytes.Length < expected) throw Fail(uri, CdnFailure.Network, Failed("The download ended early."), status);
            return new WowListfilePart(asset, observed, bytes, start, total);
        }
    }

    // Only fixed HTTPS hosts on the default port, without user info.
    private static bool Official(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host is ApiHost or DownloadHost || AssetHosts.Contains(uri.Host));

    private HttpRequestMessage Request(Uri uri, string accept, long? start)
    {
        if (!Official(uri)) throw Fail(uri, CdnFailure.Protocol, Failed("The listfile address isn’t one VibeMiner allows."));
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)");
        request.Headers.Accept.ParseAdd(accept);
        if (start is { } from) request.Headers.Range = new RangeHeaderValue(from, from + PartBytes - 1);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        using var wait = new CancellationTokenSource(headers, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, wait.Token);
        try
        {
            try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token); }
            catch (HttpRequestException error) when (WoWDBDefsClient.Unreachable(error)) { throw Fail(uri, CdnFailure.Offline, Offline()); }
            catch (HttpRequestException) { throw Fail(uri, CdnFailure.Network, Failed("The connection to GitHub failed.")); }
            // A connect timeout surfaces as a cancellation wrapping a TimeoutException; nobody cancelled.
            catch (OperationCanceledException error) when (!linked.IsCancellationRequested && error.InnerException is TimeoutException)
            { throw Fail(uri, CdnFailure.Offline, Offline()); }
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested && !token.IsCancellationRequested)
        { throw Fail(uri, CdnFailure.Timeout, Failed($"GitHub didn’t answer within {Seconds(headers)} seconds.")); }
    }

    private void Check(HttpResponseMessage response, Uri uri, string notFound)
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.NotFound) throw Fail(uri, CdnFailure.NotFound, notFound, status);
        if ((int)status is >= 300 and < 400)
            throw Fail(uri, CdnFailure.Protocol, Failed("GitHub redirected the request, and VibeMiner doesn’t follow that redirect."), status);
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw Fail(uri, CdnFailure.HttpStatus, Failed("GitHub is limiting requests right now."), status);
        if (!response.IsSuccessStatusCode)
            throw Fail(uri, CdnFailure.HttpStatus, Failed($"GitHub answered HTTP {((int)status).ToString(CultureInfo.InvariantCulture)}."), status);
    }

    // Reads at most maximum + 1 bytes, so a caller sees an answer that runs over.
    private async Task<byte[]> ReadAsync(HttpResponseMessage response, Uri uri, int maximum, CancellationToken token)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[Math.Min(maximum + 1, (int?)response.Content.Headers.ContentLength + 1 ?? 64 * 1024)];
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length > maximum) break;
                    Array.Resize(ref buffer, Math.Min(maximum + 1, buffer.Length * 2));
                }
                // Each read gets the whole idle budget again.
                int read;
                using (var stall = new CancellationTokenSource(idle, time))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stall.Token))
                {
                    try { read = await stream.ReadAsync(buffer.AsMemory(length), linked.Token); }
                    catch (OperationCanceledException) when (stall.IsCancellationRequested && !token.IsCancellationRequested)
                    { throw Fail(uri, CdnFailure.Timeout, Failed($"GitHub stopped sending for {Seconds(idle)} seconds."), response.StatusCode); }
                }
                if (read == 0) break;
                length += read;
            }
            return buffer[..length];
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { throw Fail(uri, CdnFailure.Network, Failed("The download from GitHub was interrupted."), response.StatusCode); }
    }

    private static string ValidTag(string tag) =>
        tag is { Length: >= 1 and <= 64 } && tag.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? tag : throw new ArgumentException("A release tag is 1-64 of [a-z0-9_-].", nameof(tag));

    // A redirected asset address carries a signed query; failures keep only its scheme, host and path.
    private CdnException Fail(Uri url, CdnFailure failure, string message, HttpStatusCode? status = null)
    {
        var shown = new Uri(url.GetLeftPart(UriPartial.Path));
        return new([new CdnAttempt(shown.Host, shown, failure, status, time.GetUtcNow(), message)]);
    }

    private CdnException ReleaseTooLarge(HttpStatusCode status) =>
        Fail(LatestReleaseUrl, CdnFailure.Protocol, Failed("GitHub sent a larger answer than VibeMiner allows."), status);

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString(CultureInfo.InvariantCulture);

    internal static string Offline() => "VibeMiner couldn’t reach GitHub to get the wow-listfile community file names.";
    internal static string Failed(string detail) => $"Getting the wow-listfile community file names from GitHub failed. {detail}";
    internal static string TooLarge() =>
        $"The newest wow-listfile file is larger than VibeMiner allows ({(MaxAssetBytes >> 20).ToString(CultureInfo.InvariantCulture)} MiB), so it wasn’t downloaded.";
}

/// <summary>How many new connections one listfile fetch may open (D105). The client resets it at the start of each fetch.</summary>
public sealed class WowListfileConnections(int perFetch = WowListfileConnections.PerFetch)
{
    /// <summary>A part needs two (github.com, the asset host); twice that leaves room for one reconnect each.</summary>
    public const int PerFetch = 4;
    private readonly int limit = perFetch;
    private int left = perFetch;
    public bool TryTake() => Interlocked.Decrement(ref left) >= 0;
    public void Reset() => Volatile.Write(ref left, limit);
}
