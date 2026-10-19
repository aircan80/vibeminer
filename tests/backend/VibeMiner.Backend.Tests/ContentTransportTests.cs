using System.Net;
using System.Net.Http.Headers;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ContentTransportTests
{
    private static readonly Uri Url = new("https://us.cdn.blizzard.com/tpr/wow/data/11/11/11111111111111111111111111111111");

    [Fact]
    public async Task ExactRangesRequire206AndReturnObservedWholeSourceLength()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("bytes=17-20", request.Headers.Range!.ToString());
            return Response(request, [1, 2, 3, 4], 17, 20, 100);
        });
        using var client = new HttpClient(handler);
        var result = await new OfficialSourceTransport(client).ReadRangeAsync(Url, 100, 17, 4, default);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.Bytes);
        Assert.Equal(100, result.Total);
    }

    [Fact]
    public async Task FooterSuffixGeometryIsObservedRatherThanGuessedFromConfigurationHints()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=-4", request.Headers.Range!.ToString());
            return Response(request, [1, 2, 3, 4], 96, 99, 100);
        }));
        var result = await new OfficialSourceTransport(client).ReadRangeAsync(new(Url.AbsoluteUri + ".index"), null, null, 4, default);
        Assert.Equal(100, result.Total);
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("wrong-start")]
    [InlineData("wrong-end")]
    [InlineData("wrong-total")]
    [InlineData("no-range")]
    [InlineData("wrapped")]
    [InlineData("declared-length")]
    public async Task ChangedOrUnprovenRangeGeometryNeverYieldsBytes(string kind)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            var response = Response(request, [1, 2, 3, 4], 17, 20, 100);
            switch (kind)
            {
                case "ignored": response.StatusCode = HttpStatusCode.OK; break;
                case "wrong-start": response.Content.Headers.ContentRange = new(16, 19, 100); break;
                case "wrong-end": response.Content.Headers.ContentRange = new(17, 21, 100); break;
                case "wrong-total": response.Content.Headers.ContentRange = new(17, 20, 101); break;
                case "no-range": response.Content.Headers.ContentRange = null; break;
                case "wrapped": response.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "declared-length": response.Content.Headers.ContentLength = 3; break;
            }
            return response;
        }));
        Assert.Equal("CONTENT_RANGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new OfficialSourceTransport(client).ReadRangeAsync(Url, 100, 17, 4, default))).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RangeBodiesMustEndAtTheExactExpectedByteCount(bool extra)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            var response = Response(request, extra ? [1, 2, 3, 4, 5] : [1, 2, 3], 17, 20, 100);
            response.Content.Headers.ContentLength = 4;
            return response;
        }));
        Assert.Equal("CONTENT_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new OfficialSourceTransport(client).ReadRangeAsync(Url, 100, 17, 4, default))).Code);
    }

    [Fact]
    public async Task OnlyExactLoose404AuthorizesArchiveFallback()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.PartialContent })
        {
            using var client = new HttpClient(new Handler(request => new(status)
                { RequestMessage = request, Content = new ByteArrayContent([]) }));
            using var output = new MemoryStream();
            Assert.Equal(status == HttpStatusCode.NotFound ? "CONTENT_LOOSE_MISSING" : "CONTENT_UNAVAILABLE",
                (await Assert.ThrowsAsync<DiscoveryException>(() => new OfficialSourceTransport(client)
                    .DownloadLooseAsync(Url, output, 4, "selected", _ => Task.CompletedTask, default))).Code);
            Assert.Empty(output.ToArray());
        }
    }

    [Fact]
    public async Task RedirectChangedSourceAndNonofficialUrisAreRejected()
    {
        using var redirected = new HttpClient(new Handler(request => Response(
            new(HttpMethod.Get, "https://evil.invalid/body"), [1, 2, 3, 4], 17, 20, 100)));
        Assert.Equal("SOURCE_REDIRECT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new OfficialSourceTransport(redirected).ReadRangeAsync(Url, 100, 17, 4, default))).Code);
        var handler = new Handler(_ => throw new InvalidOperationException("No request permitted."));
        using var client = new HttpClient(handler);
        foreach (var uri in new[] { "http://us.cdn.blizzard.com/tpr/wow/data/11/11/11111111111111111111111111111111",
            "https://evil.invalid/tpr/wow/data/11/11/11111111111111111111111111111111", Url.AbsoluteUri + "?x=1" })
            Assert.Equal("SOURCE_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                new OfficialSourceTransport(client).ReadRangeAsync(new(uri), 100, 17, 4, default))).Code);
        Assert.Equal(0, handler.Requests);
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes, long start, long end, long total)
    {
        var result = new HttpResponseMessage(HttpStatusCode.PartialContent)
            { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, total);
        return result;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(respond(request)); }
    }
}
