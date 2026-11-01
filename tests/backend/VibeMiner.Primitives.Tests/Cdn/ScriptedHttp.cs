using VibeMiner.Cdn.Testing;
using System.Net;
using System.Net.Http.Headers;

namespace VibeMiner.Cdn.Tests;

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a script and logs every request. No network.</summary>
internal sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Ranges { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
            Ranges.Add(request.Headers.Range?.ToString() ?? "");
        }
        return answer(request, cancellationToken);
    }
}

/// <summary>A body whose reads are counted, so a test can prove the client never read it, and whose disposal is seen.</summary>
internal sealed class WatchedContent(byte[] bytes, int failAfter = -1) : HttpContent
{
    public int BytesRead;
    public bool Disposed;
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Watched(this, bytes, failAfter));
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }

    private sealed class Watched(WatchedContent owner, byte[] bytes, int failAfter) : MemoryStream(bytes, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failAfter >= 0 && Position >= failAfter) throw new IOException("Connection reset by peer (scripted).");
            var limit = failAfter >= 0 ? (int)Math.Min(buffer.Length, failAfter - Position) : buffer.Length;
            var n = await base.ReadAsync(buffer[..limit], cancellationToken);
            Interlocked.Add(ref owner.BytesRead, n);
            return n;
        }
    }
}

/// <summary>Reply builders, including header sets recorded from the official CDN on 2026-11-01 (bodies are synthetic).</summary>
internal static class Replies
{
    public static HttpResponseMessage Make(HttpRequestMessage request, HttpStatusCode status, byte[] body, string? contentType = "binary/octet-stream",
        Version? version = null, ContentRangeHeaderValue? range = null, long? contentLength = null, HttpContent? content = null)
    {
        var c = content ?? new WatchedContent(body);
        if (contentType is not null) c.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (range is not null) c.Headers.ContentRange = range;
        if (contentLength is not null) c.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(status) { Content = c, RequestMessage = request, Version = version ?? HttpVersion.Version11 };
    }

    /// <summary>us.cdn.blizzard.com, HTTP/1.1, for <c>Range: bytes=0-15</c> of a 268,435,419-byte archive.</summary>
    public static HttpResponseMessage UsCdnRange206(HttpRequestMessage request, byte[] body16) =>
        Make(request, HttpStatusCode.PartialContent, body16, "binary/octet-stream", HttpVersion.Version11, new ContentRangeHeaderValue(0, 15, 268435419), 16);

    /// <summary>level3.blizzard.com, HTTP/2, the same range.</summary>
    public static HttpResponseMessage Level3Range206(HttpRequestMessage request, byte[] body16) =>
        Make(request, HttpStatusCode.PartialContent, body16, "binary/octet-stream", HttpVersion.Version20, new ContentRangeHeaderValue(0, 15, 268435419), 16);

    /// <summary>us.cdn.blizzard.com for a key it doesn't have: 404, text/html, 3 bytes.</summary>
    public static HttpResponseMessage UsCdn404(HttpRequestMessage request) =>
        Make(request, HttpStatusCode.NotFound, "404"u8.ToArray(), "text/html");

    /// <summary>level3.blizzard.com for a key it doesn't have: 403 from S3, application/xml.</summary>
    public static HttpResponseMessage Level3Missing403(HttpRequestMessage request) =>
        Make(request, HttpStatusCode.Forbidden, "<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code></Error>"u8.ToArray(), "application/xml", HttpVersion.Version20);

    /// <summary>A whole object, as both hosts serve an index: 200, binary/octet-stream, Content-Length.</summary>
    public static HttpResponseMessage Whole200(HttpRequestMessage request, byte[] body, Version? version = null) =>
        Make(request, HttpStatusCode.OK, body, "binary/octet-stream", version);
}
