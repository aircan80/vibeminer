using System.Globalization;
using System.Net;
using System.Text.Json;

namespace VibeMiner.Cdn;

/// <summary>One table's .dbd file exactly as WoWDBDefs served it, with the commit it came from, its raw URL and when it was observed.</summary>
public sealed record WoWDBDefsReply(string TableName, string Revision, Uri Url, DateTimeOffset ObservedAt, byte[] Bytes);

/// <summary>Layer 1 WoWDBDefs client (D081): fetches a table's newest .dbd bytes. It doesn't cache or parse them.</summary>
public interface IWoWDBDefsClient
{
    /// <summary>
    /// The file from the last commit on master that changed <c>definitions/{tableName}.dbd</c>. Throws <see cref="CdnException"/>:
    /// Offline (unreachable, connect timeout), NotFound (404 or no commit), HttpStatus (rate limit, other statuses), Protocol
    /// (redirect, disallowed address, unreadable or oversized commits reply), TooLarge (definition over 1 MiB), Timeout (the
    /// fetch's budget), Network (interrupted, ended early). The caller's cancellation throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<WoWDBDefsReply> FetchAsync(string tableName, CancellationToken cancellationToken);
}

/// <summary>
/// The real <see cref="IWoWDBDefsClient"/> over the official WoWDBDefs repository on GitHub. The bytes are untrusted: HTTPS
/// only, two fixed hosts, no redirects, a 256 KiB commits reply, a 1 MiB definition, one time budget per fetch (both requests),
/// at most two fetches at once, cancellable. Definitions describe column layouts only; all game data comes from Blizzard's CDN.
/// Pass <paramref name="connections"/> when <paramref name="client"/> runs on <see cref="CreateHandler"/>.
/// </summary>
public sealed class WoWDBDefsClient(HttpClient client, TimeProvider? time = null, TimeSpan? fetchBudget = null, WoWDBDefsConnections? connections = null)
    : IWoWDBDefsClient
{
    public const string ApiHost = "api.github.com";
    public const string RawHost = "raw.githubusercontent.com";
    public const string Repository = "wowdev/WoWDBDefs";
    public const string Branch = "master";
    public const int MaxDefinitionBytes = 1 << 20;
    public const int MaxCommitResponseBytes = 256 * 1024;
    public const int MaxConcurrentFetches = 2;
    public static readonly TimeSpan DefaultFetchBudget = TimeSpan.FromSeconds(30);
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly TimeSpan budget = fetchBudget ?? DefaultFetchBudget;
    private readonly SemaphoreSlim fetches = new(MaxConcurrentFetches, MaxConcurrentFetches);

    public static Uri CommitsUrl(string tableName) =>
        new($"https://{ApiHost}/repos/{Repository}/commits?path=definitions/{tableName}.dbd&sha={Branch}&per_page=1");
    public static string DefinitionUrl(string revision, string tableName) =>
        $"https://{RawHost}/{Repository}/{revision}/definitions/{tableName}.dbd";

    /// <summary>
    /// The definitions' own HTTP handler: no redirects or decompression, two connections per host, a 10 s connect timeout,
    /// and every new connection, also to a proxy, taken from <paramref name="connections"/>. .NET's pool opens a new
    /// connection again when one closes before any response, so one check through a proxy that drops connections opened a
    /// dozen or more within milliseconds (#124).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(WoWDBDefsConnections connections) => new()
    {
        AllowAutoRedirect = false, MaxConnectionsPerServer = MaxConcurrentFetches, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, token) =>
        {
            await connections.TakeAsync(token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public async Task<WoWDBDefsReply> FetchAsync(string tableName, CancellationToken cancellationToken)
    {
        if (tableName is not { Length: > 0 and <= 128 } || tableName.Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("A table name is 1-128 of [A-Za-z0-9_-].", nameof(tableName));
        await fetches.WaitAsync(cancellationToken);
        try
        {
            using var limit = new CancellationTokenSource(budget, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
            var at = CommitsUrl(tableName);
            try
            {
                var revision = await LatestRevisionAsync(tableName, linked.Token);
                at = new Uri(DefinitionUrl(revision, tableName));
                var (bytes, observed) = await DownloadAsync(at, MaxDefinitionBytes, tableName, linked.Token);
                return new(tableName, revision, at, observed, bytes);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw Fail(at, CdnFailure.Timeout, Failed(tableName,
                    $"WoWDBDefs didn’t answer within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."));
            }
        }
        finally { fetches.Release(); }
    }

    // The last commit that changed this table's file on the default branch.
    private async Task<string> LatestRevisionAsync(string tableName, CancellationToken token)
    {
        var url = CommitsUrl(tableName);
        var (bytes, _) = await DownloadAsync(url, MaxCommitResponseBytes, tableName, token, api: true);
        CdnException Unreadable() => Fail(url, CdnFailure.Protocol, Failed(tableName, "GitHub sent an answer VibeMiner doesn’t understand."));
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) throw Unreadable();
            if (root.GetArrayLength() == 0) throw Fail(url, CdnFailure.NotFound, NotFound(tableName));
            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String ||
                sha.GetString() is not { Length: 40 } revision || !revision.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw Unreadable();
            return revision;
        }
        catch (JsonException) { throw Unreadable(); }
    }

    private async Task<(byte[] Bytes, DateTimeOffset ObservedAt)> DownloadAsync(Uri uri, int maximum, string tableName, CancellationToken token, bool api = false)
    {
        // Only these two fixed HTTPS hosts on the default port. Redirects are never followed.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.Host is not (ApiHost or RawHost) || !string.IsNullOrEmpty(uri.UserInfo))
            throw Fail(uri, CdnFailure.Protocol, Failed(tableName, "The definition address isn’t one VibeMiner allows."));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)");
        request.Headers.Accept.ParseAdd(api ? "application/vnd.github+json" : "text/plain");
        if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        // The request may open connections until its answer's headers arrive (#124).
        using (connections?.Grant())
        {
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException error) when (Unreachable(error)) { throw Fail(uri, CdnFailure.Offline, Offline(tableName)); }
            catch (HttpRequestException) { throw Fail(uri, CdnFailure.Network, Failed(tableName, "The connection to GitHub failed.")); }
            // A connect timeout surfaces as a cancellation wrapping a TimeoutException; the caller didn't cancel.
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && error.InnerException is TimeoutException)
            { throw Fail(uri, CdnFailure.Offline, Offline(tableName)); }
        }
        using (response)
        {
            var status = response.StatusCode;
            var observed = time.GetUtcNow();
            if (status == HttpStatusCode.NotFound) throw Fail(uri, CdnFailure.NotFound, NotFound(tableName), status);
            if ((int)status is >= 300 and < 400)
                throw Fail(uri, CdnFailure.Protocol, Failed(tableName, "GitHub redirected the request, and VibeMiner doesn’t follow redirects."), status);
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw Fail(uri, CdnFailure.HttpStatus, Failed(tableName, "GitHub is limiting requests right now."), status);
            if (!response.IsSuccessStatusCode)
                throw Fail(uri, CdnFailure.HttpStatus, Failed(tableName, $"GitHub answered HTTP {((int)status).ToString(CultureInfo.InvariantCulture)}."), status);
            CdnException Large() => api
                ? Fail(uri, CdnFailure.Protocol, Failed(tableName, "GitHub sent a larger answer than VibeMiner allows."), status)
                : Fail(uri, CdnFailure.TooLarge, TooLarge(tableName), status);
            if (response.Content.Headers.ContentLength > maximum) throw Large();
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
                    var read = await stream.ReadAsync(buffer.AsMemory(length), token);
                    if (read == 0) break;
                    length += read;
                }
                if (length > maximum) throw Large();
                if (response.Content.Headers.ContentLength is { } declared && declared != length)
                    throw Fail(uri, CdnFailure.Network, Failed(tableName, "The download ended early."), status);
                return (buffer[..length], observed);
            }
            catch (HttpRequestException) { throw Fail(uri, CdnFailure.Network, Failed(tableName, "The download from GitHub was interrupted."), status); }
            catch (IOException) { throw Fail(uri, CdnFailure.Network, Failed(tableName, "The download from GitHub was interrupted."), status); }
        }
    }

    /// <summary>
    /// No connection: name resolution or connect failed, the connection was refused, or it was closed or reset before any
    /// response (#125, #127). The pool reports a reset as an unknown error over a socket error. A certificate failure or an
    /// answer VibeMiner can't read is a failure, not offline.
    /// </summary>
    internal static bool Unreachable(HttpRequestException error)
    {
        if (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded) return true;
        var depth = 0;
        for (var inner = error.InnerException; inner is not null && depth < 8; inner = inner.InnerException, depth++)
            if (inner is System.Net.Sockets.SocketException) return true;
        return false;
    }

    private CdnException Fail(Uri url, CdnFailure failure, string message, HttpStatusCode? status = null) =>
        new([new CdnAttempt(url.Host, url, failure, status, time.GetUtcNow(), message)]);

    internal static string Offline(string table) => $"VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer {table} definition.";
    internal static string Failed(string table, string detail) => $"Fetching a newer {table} definition from WoWDBDefs failed. {detail}";
    internal static string TooLarge(string table) => $"The newest WoWDBDefs {table} definition is larger than VibeMiner allows (1 MiB), so it wasn’t used.";
    internal static string NotFound(string table) =>
        $"WoWDBDefs on GitHub has no {table}.dbd file to fetch, so only the bundled {table} definition can be used.";
}

/// <summary>
/// New connections the WoWDBDefs client may open (#124). Each request grants <see cref="PerRequest"/> while it waits for
/// its answer, and the grant ends with the last request in flight, so the client never opens more than that many per
/// request and none for a request it didn't make. One connection per request opens at once; any other starts at least
/// <see cref="Spacing"/> after the connection before it, so a proxy that drops connections sees two a second apart
/// instead of a burst. Waiting is cancellable.
/// </summary>
public sealed class WoWDBDefsConnections(TimeProvider? clock = null)
{
    /// <summary>The first connection and one reconnect.</summary>
    public const int PerRequest = 2;
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private int requests, left, firsts;
    private long last;

    internal IDisposable Grant()
    {
        lock (gate) { requests++; left += PerRequest; firsts++; }
        return new Granted(this);
    }

    private void End() { lock (gate) if (--requests == 0) left = firsts = 0; }

    /// <summary>Waits for this connection's turn, then takes it, or refuses as a connection error when none is left.</summary>
    internal async ValueTask TakeAsync(CancellationToken token)
    {
        TimeSpan wait;
        lock (gate)
        {
            if (left == 0) throw Refused();
            var now = time.GetTimestamp();
            if (firsts > 0) { firsts--; left--; last = Math.Max(last, now); return; }
            // A reconnect: its turn comes a spacing after the connection before it.
            var start = Math.Max(now, last + (long)(Spacing.TotalSeconds * time.TimestampFrequency));
            last = start;
            wait = TimeSpan.FromSeconds((start - now) / (double)time.TimestampFrequency);
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, time, token);
        // A request that ended or was cancelled while this waited took its grant with it.
        lock (gate) { if (left == 0) throw Refused(); left--; }
    }

    private static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "The WoWDBDefs requests used the connections they may open.");

    private sealed class Granted(WoWDBDefsConnections owner) : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) owner.End(); }
    }
}
