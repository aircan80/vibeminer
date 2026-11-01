using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace VibeMiner.Cdn;

/// <summary>Checks a whole reply against its key; returns the refusal code, or null when the bytes are right.</summary>
public delegate string? CdnCheck(ReadOnlySpan<byte> bytes);

/// <summary>
/// One request: a whole key, or exactly one byte range of it. Never more than one range: multi-range requests are
/// never sent, because some CDN hosts answer them with the whole archive.
/// </summary>
public sealed record CdnRequest
{
    private CdnRequest(CdnObjectKind kind, CdnKey key) { Kind = kind; Key = key; }
    public CdnObjectKind Kind { get; }
    public CdnKey Key { get; }
    /// <summary>Whole: the expected length when the source declared one. Range: the range length.</summary>
    public long? Length { get; private init; }
    /// <summary>Set for a range request.</summary>
    public long? Offset { get; private init; }
    /// <summary>Range only: the whole object's length when known; the Content-Range total must equal it.</summary>
    public long? ExpectedTotal { get; private init; }
    /// <summary>In-memory fetches only (whole or range): run on the received bytes; a refusal fails that host's attempt.</summary>
    public CdnCheck? Check { get; private init; }
    /// <summary>Whole only: the largest reply accepted, below the client's own limits; a longer one is <see cref="CdnFailure.TooLarge"/>.</summary>
    public long? MaxLength { get; private init; }

    public bool IsRange => Offset is not null;

    public static CdnRequest Whole(CdnObjectKind kind, CdnKey key, long? expectedLength = null, CdnCheck? check = null, long? maxLength = null)
    {
        if (expectedLength is < 1) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (maxLength is < 1 || expectedLength > maxLength) throw new ArgumentOutOfRangeException(nameof(maxLength));
        return new(kind, key) { Length = expectedLength, Check = check, MaxLength = maxLength };
    }

    public static CdnRequest Range(CdnObjectKind kind, CdnKey key, long offset, long length, long? expectedTotal = null, CdnCheck? check = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        if (expectedTotal is { } total && (total < 1 || offset > total - length)) throw new ArgumentOutOfRangeException(nameof(expectedTotal));
        return new(kind, key) { Offset = offset, Length = length, ExpectedTotal = expectedTotal, Check = check };
    }
}

/// <summary>A successful reply. <see cref="Bytes"/> is null for a streamed fetch. <see cref="Failed"/> lists hosts tried before this one.</summary>
public sealed record CdnReply(string Host, Uri Url, Version HttpVersion, long Length, long? Total, byte[]? Bytes,
    DateTimeOffset ObservedAt, TimeSpan HeadersAfter, TimeSpan Elapsed, IReadOnlyList<CdnAttempt> Failed);

/// <summary>Layer 1 CDN client: runs the requests it is given, checks each reply exactly and knows nothing about stores or builds.</summary>
public interface ICdnClient
{
    /// <summary>
    /// Fetches into memory (at most <see cref="CdnClientOptions.MaxInMemoryBytes"/>), trying the route's hosts in order.
    /// Throws <see cref="CdnException"/> when every host failed; cancellation stays <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<CdnReply> FetchAsync(CdnRoute route, CdnRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Streams the body to <paramref name="sink"/> in order. Hosts are tried in order only until the first byte reaches
    /// the sink; a failure after that throws at once, so the sink never sees bytes from two hosts. No <see cref="CdnRequest.Check"/> runs.
    /// </summary>
    Task<CdnReply> FetchToAsync(CdnRoute route, CdnRequest request, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken);
}

public sealed record CdnClientOptions
{
    /// <summary>The largest reply <see cref="ICdnClient.FetchAsync"/> holds in memory (the store's whole-Put limit).</summary>
    public long MaxInMemoryBytes { get; init; } = 64L << 20;
    /// <summary>The largest object or range either call accepts (the store's object limit).</summary>
    public long MaxBytes { get; init; } = 512L << 20;
    /// <summary>From sending the request to the response headers.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>The longest wait for the next body bytes.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public string UserAgent { get; init; } = "VibeMiner/0.1";
}

/// <summary>The real <see cref="ICdnClient"/> over an <see cref="HttpClient"/> (use <see cref="CreateHandler"/>: no redirects, no decompression).</summary>
public sealed class CdnClient(HttpClient client, CdnClientOptions? options = null) : ICdnClient
{
    private const int Slice = 65_536;
    private readonly CdnClientOptions options = options ?? new();

    /// <summary>A handler fit for the CDN: no redirects or decompression, HTTP/2 multiplexing, bounded connections per host.</summary>
    public static SocketsHttpHandler CreateHandler(int maxConnectionsPerServer = 32) => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = maxConnectionsPerServer,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public async Task<CdnReply> FetchAsync(CdnRoute route, CdnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route); ArgumentNullException.ThrowIfNull(request);
        if (request.Length > options.MaxInMemoryBytes) throw new ArgumentOutOfRangeException(nameof(request), "The request is larger than the in-memory limit.");
        var limit = Math.Min(options.MaxInMemoryBytes, request.MaxLength ?? long.MaxValue);
        var failed = new List<CdnAttempt>();
        foreach (var host in route.Hosts)
        {
            var url = route.UrlFor(host, request.Kind, request.Key);
            byte[]? buffer = null; long filled = 0; long? declared = null;
            var outcome = await SendAsync(host, url, request, failed, limit, (length) =>
            {
                declared = length;
                buffer = new byte[length ?? Math.Min(limit, 1 << 20)];
            }, (data, _) =>
            {
                if (buffer!.Length - filled < data.Length)
                {
                    if (declared is not null) throw new InvalidOperationException("SendAsync bounds the body by its declared length.");
                    var grown = new byte[Math.Min(limit, Math.Max(buffer.Length * 2L, filled + data.Length))];
                    buffer.AsSpan(0, (int)filled).CopyTo(grown); buffer = grown;
                }
                data.Span.CopyTo(buffer.AsSpan((int)filled)); filled += data.Length;
                return ValueTask.CompletedTask;
            }, cancellationToken);
            if (outcome is null) continue;
            var bytes = buffer!.Length == filled ? buffer : buffer.AsSpan(0, (int)filled).ToArray();
            if (request.Check is { } check && check(bytes) is { } code)
            {
                failed.Add(new(host, url, CdnFailure.VerificationFailed, HttpStatusCode.OK, outcome.ObservedAt,
                    "The bytes don't pass their key's check.", code));
                continue;
            }
            return outcome with { Bytes = bytes, Failed = failed.ToArray() };
        }
        throw new CdnException(failed);
    }

    public async Task<CdnReply> FetchToAsync(CdnRoute route, CdnRequest request, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(sink);
        var failed = new List<CdnAttempt>();
        foreach (var host in route.Hosts)
        {
            var url = route.UrlFor(host, request.Kind, request.Key);
            var started = false;
            var outcome = await SendAsync(host, url, request, failed, Math.Min(options.MaxBytes, request.MaxLength ?? long.MaxValue), _ => { }, (data, token) =>
            { started = true; return sink(data, token); }, cancellationToken);
            if (outcome is not null) return outcome with { Failed = failed.ToArray() };
            if (started) break;
        }
        throw new CdnException(failed);
    }

    private sealed class Abort(CdnFailure failure, string message, HttpStatusCode? status = null) : Exception(message)
    {
        public CdnFailure Failure { get; } = failure;
        public HttpStatusCode? Status { get; } = status;
    }

    /// <summary>The caller's sink threw: its exception is the caller's, never a CDN failure.</summary>
    private sealed class SinkFailed(Exception inner) : Exception(inner.Message, inner);

    /// <summary>One host. Returns the reply (without bytes) or null after adding the failed attempt.</summary>
    private async Task<CdnReply?> SendAsync(string host, Uri url, CdnRequest request, List<CdnAttempt> failed, long limit,
        Action<long?> begin, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> body, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url)
            { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            message.Headers.UserAgent.ParseAdd(options.UserAgent);
            if (request.Offset is { } offset) message.Headers.Range = new RangeHeaderValue(offset, checked(offset + request.Length!.Value - 1));
            HttpResponseMessage response;
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headers.CancelAfter(options.HeaderTimeout);
                try { response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                { throw new Abort(CdnFailure.Timeout, "No response headers within the header timeout."); }
            }
            // Disposing an unread response closes the HTTP/1.1 connection or resets the HTTP/2 stream: that is the abort.
            using (response)
            {
                var headersAfter = clock.Elapsed;
                var observed = options.Time.GetUtcNow();
                var length = CheckHeaders(response, url, request, limit);
                begin(length);
                // The headers declared the length (Content-Length, or the exact Content-Range): a body that stops short of it,
                // even at zero bytes, is a broken transfer, not another length. HTTP/2 in .NET ends such a body cleanly where
                // HTTP/1.1 throws (#197).
                var declared = request.IsRange || response.Content.Headers.ContentLength is not null ? length : null;
                var received = await ReadBodyAsync(response, length, limit, body, declared is not null, cancellationToken);
                if (received < declared)
                    throw new Abort(CdnFailure.Network, $"The reply ended after {received} of its {declared} declared bytes.");
                if (request.IsRange && received != request.Length || !request.IsRange && request.Length is { } expected && received != expected)
                    throw new Abort(CdnFailure.SizeConflict, $"Received {received} bytes, expected {request.Length}.");
                return new(host, url, response.Version, received, response.Content.Headers.ContentRange?.Length, null,
                    observed, headersAfter, clock.Elapsed, []);
            }
        }
        catch (SinkFailed sink)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sink.InnerException!).Throw();
            throw;
        }
        catch (Abort abort)
        {
            failed.Add(new(host, url, abort.Failure, abort.Status, options.Time.GetUtcNow(), abort.Message));
            return null;
        }
        catch (HttpRequestException ex)
        {
            var offline = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;
            failed.Add(new(host, url, offline ? CdnFailure.Offline : CdnFailure.Network, null, options.Time.GetUtcNow(),
                offline ? "The CDN host could not be reached." : "The CDN connection failed."));
            return null;
        }
        catch (IOException)
        {
            failed.Add(new(host, url, CdnFailure.Network, null, options.Time.GetUtcNow(), "The CDN response could not be read."));
            return null;
        }
    }

    /// <summary>Checks status and headers before any body byte is read. Returns the exact body length when known.</summary>
    private static long? CheckHeaders(HttpResponseMessage response, Uri url, CdnRequest request, long limit)
    {
        var status = response.StatusCode;
        if ((int)status is >= 300 and < 400 || response.RequestMessage?.RequestUri != url)
            throw new Abort(CdnFailure.Protocol, "CDN redirects are not followed.", status);
        if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Forbidden && url.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com")
            throw new Abort(CdnFailure.NotFound, $"HTTP {(int)status}: this host doesn't have the key now.", status);
        if (request.IsRange && status == HttpStatusCode.OK)
            throw new Abort(CdnFailure.RangeIgnored, "The host answered a range request with the whole object; aborted at the headers.", status);
        if (!request.IsRange && status == HttpStatusCode.PartialContent)
            throw new Abort(CdnFailure.RangeMismatch, "The host answered a whole request with a range.", status);
        if (status != HttpStatusCode.OK && status != HttpStatusCode.PartialContent)
            throw new Abort(CdnFailure.HttpStatus, $"HTTP {(int)status}.", status);
        var content = response.Content.Headers;
        if (content.ContentEncoding.Count != 0) throw new Abort(CdnFailure.Protocol, "Encoded (compressed) CDN replies are refused.", status);
        var media = content.ContentType?.MediaType?.ToLowerInvariant();
        if (media is not null and not ("application/octet-stream" or "binary/octet-stream" or "application/binary" or "text/plain"))
            throw new Abort(CdnFailure.Protocol, $"Unexpected media type {media}.", status);
        var range = content.ContentRange;
        if (request.IsRange)
        {
            var from = request.Offset!.Value; var to = from + request.Length!.Value - 1;
            if (range is null || range.Unit != "bytes" || range.From != from || range.To != to || range.Length is not { } total || total <= to)
                throw new Abort(CdnFailure.RangeMismatch, $"Content-Range {range} is not exactly bytes {from}-{to}.", status);
            if (request.ExpectedTotal is { } expectedTotal && total != expectedTotal)
                throw new Abort(CdnFailure.SizeConflict, $"Content-Range total {total} differs from the expected {expectedTotal}.", status);
            if (content.ContentLength is { } declared && declared != request.Length)
                throw new Abort(CdnFailure.RangeMismatch, "Content-Length differs from the range length.", status);
            return request.Length;
        }
        if (range is not null) throw new Abort(CdnFailure.RangeMismatch, "A whole reply carries a Content-Range.", status);
        if (content.ContentLength is { } length)
        {
            if (request.Length is { } expected && length != expected)
                throw new Abort(CdnFailure.SizeConflict, $"Content-Length {length} differs from the expected {expected}.", status);
            if (length > limit) throw new Abort(CdnFailure.TooLarge, $"Content-Length {length} is over the {limit} byte limit.", status);
            if (length == 0) throw new Abort(CdnFailure.SizeConflict, "The reply is empty.", status);
            return length;
        }
        return request.Length;
    }

    private async Task<long> ReadBodyAsync(HttpResponseMessage response, long? length, long limit,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> body, bool headersDeclared, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[Slice];
        long received = 0;
        var bound = length ?? limit;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            idle.CancelAfter(options.IdleTimeout);
            int count;
            // Ask for one byte past the bound so a longer body is caught without reading it.
            try { count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bound - received + 1)), idle.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new Abort(CdnFailure.Timeout, "The CDN stopped sending bytes."); }
            if (count == 0) break;
            if (received + count > bound)
                throw length is null ? new Abort(CdnFailure.TooLarge, $"The reply is over the {limit} byte limit.")
                    : new Abort(CdnFailure.SizeConflict, "The reply is longer than its declared length.");
            try { await body(buffer.AsMemory(0, count), cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new SinkFailed(ex); }
            received += count;
        }
        if (received == 0 && !headersDeclared) throw new Abort(CdnFailure.SizeConflict, "The reply is empty.");
        return received;
    }
}
