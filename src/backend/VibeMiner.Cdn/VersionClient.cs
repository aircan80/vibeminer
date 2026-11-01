using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace VibeMiner.Cdn;

/// <summary>
/// The version-service documents, numbered like the history log's record kinds. <see cref="Summary"/> lists every product the
/// service knows with its documents' sequence numbers; it belongs to no product, so its product is "".
/// </summary>
public enum VersionDocumentKind { Versions = 1, Cdns = 2, Bgdl = 3, Summary = 4 }

/// <summary>One raw version-service reply, exactly as served, with the URL and time it was observed.</summary>
public sealed record VersionReply(string Product, string Region, VersionDocumentKind Kind, Uri Url, DateTimeOffset ObservedAt, byte[] Bytes, TimeSpan Elapsed);

/// <summary>Layer 1 version-service client: fetches versions, cdns, bgdl and summary replies. It doesn't interpret them.</summary>
public interface IVersionClient
{
    /// <summary>One reply; <paramref name="product"/> is "" for <see cref="VersionDocumentKind.Summary"/>. Throws <see cref="CdnException"/> (Offline, NotFound, HttpStatus, Timeout, Protocol, TooLarge ...).</summary>
    Task<VersionReply> FetchAsync(string product, string region, VersionDocumentKind kind, CancellationToken cancellationToken);
}

public sealed class VersionClient(HttpClient client, CdnClientOptions? options = null) : IVersionClient
{
    /// <summary>The version service's document limit.</summary>
    public const int MaxBytes = 1 << 20;
    private static readonly Regex ProductPattern = new("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant);
    /// <summary>Regions with their own version-service host.</summary>
    public static readonly IReadOnlyList<string> Regions = ["us", "eu", "kr", "tw"];
    private readonly CdnClientOptions options = options ?? new();

    /// <summary>
    /// <c>https://{region}.version.battle.net/v2/products/{product}/{versions|cdns|bgdl}</c>, or <c>/v2/summary</c> on the same host
    /// for the summary, whose product must be "".
    /// </summary>
    public static Uri UrlFor(string product, string region, VersionDocumentKind kind)
    {
        if (kind == VersionDocumentKind.Summary ? product != "" : product is null || !ProductPattern.IsMatch(product))
            throw new ArgumentException(kind == VersionDocumentKind.Summary ? "The summary has no product." : "A product is 1-64 of [a-z0-9_-].", nameof(product));
        if (!Regions.Contains(region)) throw new ArgumentException("Unknown version-service region.", nameof(region));
        if (kind == VersionDocumentKind.Summary) return new Uri($"https://{region}.version.battle.net/v2/summary", UriKind.Absolute);
        var name = kind switch
        {
            VersionDocumentKind.Versions => "versions", VersionDocumentKind.Cdns => "cdns", VersionDocumentKind.Bgdl => "bgdl",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new Uri($"https://{region}.version.battle.net/v2/products/{product}/{name}", UriKind.Absolute);
    }

    public async Task<VersionReply> FetchAsync(string product, string region, VersionDocumentKind kind, CancellationToken cancellationToken)
    {
        var url = UrlFor(product, region, kind);
        var clock = Stopwatch.StartNew();
        CdnAttempt Failed(CdnFailure failure, string message, HttpStatusCode? status = null) =>
            new(url.Host, url, failure, status, options.Time.GetUtcNow(), message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HeaderTimeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            message.Headers.UserAgent.ParseAdd(options.UserAgent);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = response.StatusCode;
            if ((int)status is >= 300 and < 400 || response.RequestMessage?.RequestUri != url)
                throw new CdnException([Failed(CdnFailure.Protocol, "Version-service redirects are not followed.", status)]);
            if (status == HttpStatusCode.NotFound) throw new CdnException([Failed(CdnFailure.NotFound, "HTTP 404 from the version service.", status)]);
            if (status != HttpStatusCode.OK) throw new CdnException([Failed(CdnFailure.HttpStatus, $"HTTP {(int)status} from the version service.", status)]);
            var content = response.Content.Headers;
            if (content.ContentEncoding.Count != 0 || content.ContentRange is not null)
                throw new CdnException([Failed(CdnFailure.Protocol, "Encoded or ranged version-service replies are refused.", status)]);
            var media = content.ContentType?.MediaType?.ToLowerInvariant();
            if (media is not null and not ("text/plain" or "application/octet-stream"))
                throw new CdnException([Failed(CdnFailure.Protocol, $"Unexpected media type {media}.", status)]);
            if (content.ContentLength > MaxBytes) throw new CdnException([Failed(CdnFailure.TooLarge, "The reply is over 1 MiB.", status)]);
            var observed = options.Time.GetUtcNow();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var buffer = new byte[16_384];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, timeout.Token);
                if (count == 0) break;
                if (body.Length + count > MaxBytes) throw new CdnException([Failed(CdnFailure.TooLarge, "The reply is over 1 MiB.", status)]);
                body.Write(buffer, 0, count);
            }
            if (content.ContentLength is { } declared && declared != body.Length)
                throw new CdnException([Failed(CdnFailure.SizeConflict, "The reply ended before its Content-Length.", status)]);
            if (body.Length == 0) throw new CdnException([Failed(CdnFailure.SizeConflict, "The reply is empty.", status)]);
            return new(product, region, kind, url, observed, body.ToArray(), clock.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CdnException([Failed(CdnFailure.Timeout, "The version service didn't answer in time.")]); }
        catch (HttpRequestException ex)
        {
            var offline = ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError;
            throw new CdnException([Failed(offline ? CdnFailure.Offline : CdnFailure.Network, offline ? "The version service could not be reached." : "The version-service connection failed.")]);
        }
        catch (IOException) { throw new CdnException([Failed(CdnFailure.Network, "The version-service reply could not be read.")]); }
    }
}
