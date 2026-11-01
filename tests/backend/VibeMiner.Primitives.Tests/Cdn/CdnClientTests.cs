using VibeMiner.Cdn.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Xunit;

namespace VibeMiner.Cdn.Tests;

public sealed class CdnClientTests
{
    private static readonly CdnRoute Route = new(["level3.blizzard.com", "us.cdn.blizzard.com"], "tpr/wow");
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);
    private static byte[] Bytes(int n, int seed = 1) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static CdnKey Md5Key(byte[] b) => new(MD5.HashData(b));
    private static string? Md5Check(CdnKey key, ReadOnlySpan<byte> b) => new CdnKey(MD5.HashData(b)) == key ? null : "OBJECT_KEY_MISMATCH";

    private static (CdnClient Client, ScriptedHandler Handler) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
        CdnClientOptions? options = null)
    {
        var handler = new ScriptedHandler(answer);
        return (new CdnClient(new HttpClient(handler), options ?? new CdnClientOptions { Time = new ManualTime(T0) }), handler);
    }

    [Fact]
    public async Task AWholeKeyIsFetchedCheckedAndAddressedByKind()
    {
        var body = Bytes(5000); var key = Md5Key(body);
        var (client, handler) = Make((r, _) => Task.FromResult(Replies.Whole200(r, body, HttpVersion.Version20)));
        var reply = await client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Config, key, 5000, b => Md5Check(key, b)), CancellationToken.None);
        Assert.Equal(body, reply.Bytes);
        Assert.Equal("level3.blizzard.com", reply.Host);
        Assert.Equal(HttpVersion.Version20, reply.HttpVersion);
        Assert.Equal(T0, reply.ObservedAt);
        Assert.Empty(reply.Failed);
        var hex = key.Hex;
        Assert.Equal($"https://level3.blizzard.com/tpr/wow/config/{hex[..2]}/{hex[2..4]}/{hex}", handler.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.Equal("", handler.Ranges.Single());
        Assert.EndsWith($"/tpr/wow/data/{hex[..2]}/{hex[2..4]}/{hex}.index", Route.UrlFor("us.cdn.blizzard.com", CdnObjectKind.Index, key).AbsoluteUri);
    }

    [Fact]
    public async Task ARangeSendsExactlyOneRangeAndAcceptsTheRecordedReplies()
    {
        var body = Bytes(16); var key = Md5Key(Bytes(1, 9));
        foreach (var recorded in new Func<HttpRequestMessage, byte[], HttpResponseMessage>[] { Replies.Level3Range206, Replies.UsCdnRange206 })
        {
            var (client, handler) = Make((r, _) => Task.FromResult(recorded(r, body)));
            var reply = await client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, key, 0, 16, 268435419), CancellationToken.None);
            Assert.Equal(body, reply.Bytes);
            Assert.Equal(268435419, reply.Total);
            var range = handler.Requests.Single().Headers.Range!;
            Assert.Equal("bytes", range.Unit);
            var only = Assert.Single(range.Ranges);
            Assert.Equal(0, only.From); Assert.Equal(15, only.To);
        }
    }

    [Fact]
    public async Task A200ToARangeIsAbortedAtTheHeadersAndTheNextHostServesIt()
    {
        var whole = new WatchedContent(Bytes(1 << 20));
        var body = Bytes(16);
        var (client, handler) = Make((r, _) => Task.FromResult(r.RequestUri!.Host == "level3.blizzard.com"
            ? Replies.Make(r, HttpStatusCode.OK, [], content: whole)
            : Replies.UsCdnRange206(r, body)));
        var reply = await client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, Md5Key(body), 0, 16), CancellationToken.None);
        Assert.Equal(body, reply.Bytes);
        Assert.Equal(0, whole.BytesRead);
        Assert.True(whole.Disposed);
        var failed = Assert.Single(reply.Failed);
        Assert.Equal(CdnFailure.RangeIgnored, failed.Failure);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(1, 16, 100)]   // starts one byte late
    [InlineData(0, 14, 100)]   // ends one byte early
    [InlineData(0, 15, 15)]    // total smaller than the span
    [InlineData(-1, -1, -1)]   // no Content-Range at all
    public async Task AContentRangeThatIsNotExactlyTheRequestIsRejected(long from, long to, long total)
    {
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.PartialContent, Bytes(16),
            range: from < 0 ? null : new ContentRangeHeaderValue(from, to, total))));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, Md5Key([1]), 0, 16), CancellationToken.None));
        Assert.All(ex.Attempts, a => Assert.Equal(CdnFailure.RangeMismatch, a.Failure));
        Assert.Equal(2, ex.Attempts.Count);
    }

    [Fact]
    public async Task ARangeTotalThatDiffersFromTheExpectedTotalIsASizeConflict()
    {
        var (client, _) = Make((r, _) => Task.FromResult(Replies.UsCdnRange206(r, Bytes(16))));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, Md5Key([1]), 0, 16, 1000), CancellationToken.None));
        Assert.Equal(CdnFailure.SizeConflict, ex.Overall);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task A404OnEveryHostIsADatedRetryableObservationNotAbsence()
    {
        var (client, _) = Make((r, _) => Task.FromResult(r.RequestUri!.Host == "level3.blizzard.com" ? Replies.Level3Missing403(r) : Replies.UsCdn404(r)));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([2])), CancellationToken.None));
        Assert.Equal(CdnFailure.NotFound, ex.Overall);
        Assert.True(ex.Retryable);
        Assert.Equal(T0, ex.ObservedAt);
        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.NotFound], ex.Attempts.Select(a => a.Status!.Value));
    }

    [Fact]
    public async Task A403FromAnotherHostIsAnHttpStatusNotMissing()
    {
        var route = new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow");
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.Forbidden, [1])));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([2])), CancellationToken.None));
        Assert.Equal(CdnFailure.HttpStatus, ex.Overall);
    }

    [Fact]
    public async Task NoConnectionOnEveryHostIsOffline()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([2])), CancellationToken.None));
        Assert.Equal(CdnFailure.Offline, ex.Overall);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task A404OnOneHostAndNoConnectionOnTheOtherIsStillNotFound()
    {
        var (client, _) = Make((r, _) => r.RequestUri!.Host == "level3.blizzard.com"
            ? Task.FromResult(Replies.UsCdn404(r)) : throw new HttpRequestException(HttpRequestError.NameResolutionError, "dns"));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([2])), CancellationToken.None));
        Assert.Equal(CdnFailure.NotFound, ex.Overall);
    }

    [Fact]
    public async Task AContentLengthThatDiffersFromTheDeclaredSizeIsASizeConflictAndTheBodyIsNotRead()
    {
        var content = new WatchedContent(Bytes(100));
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.OK, [], content: content)));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([3]), 99), CancellationToken.None));
        Assert.Equal(CdnFailure.SizeConflict, ex.Overall);
        Assert.Equal(0, content.BytesRead);
    }

    [Fact]
    public async Task ABodyLongerThanTheExpectedLengthWithoutContentLengthIsASizeConflict()
    {
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.OK, [], content: new UnsizedContent(Bytes(200)))));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([3]), 100), CancellationToken.None));
        Assert.Equal(CdnFailure.SizeConflict, ex.Overall);
    }

    [Fact]
    public async Task AReplyOverTheInMemoryLimitIsRefusedWithoutReadingIt()
    {
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.OK, [], content: new UnsizedContent(Bytes(5000)))),
            new CdnClientOptions { MaxInMemoryBytes = 4096 });
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([3])), CancellationToken.None));
        Assert.Equal(CdnFailure.TooLarge, ex.Overall);
    }

    [Fact]
    public async Task BytesThatFailTheirCheckFailThatHostAndTheNextHostIsTried()
    {
        var good = Bytes(300); var key = Md5Key(good); var bad = (byte[])good.Clone(); bad[7] ^= 1;
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Whole200(r, r.RequestUri!.Host == "level3.blizzard.com" ? bad : good)));
        var reply = await client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, 300, b => Md5Check(key, b)), CancellationToken.None);
        Assert.Equal(good, reply.Bytes);
        var failed = Assert.Single(reply.Failed);
        Assert.Equal(CdnFailure.VerificationFailed, failed.Failure);
        Assert.Equal("OBJECT_KEY_MISMATCH", failed.Code);
    }

    [Fact]
    public async Task BytesThatFailOnEveryHostAreAVerificationFailure()
    {
        var bad = Bytes(300);
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Whole200(r, bad)));
        var key = Md5Key([9]);
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, 300, b => Md5Check(key, b)), CancellationToken.None));
        Assert.Equal(CdnFailure.VerificationFailed, ex.Overall);
        Assert.Equal("OBJECT_KEY_MISMATCH", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task BadBytesOnOneHostAndATimeoutOnTheOtherStayRetryable()
    {
        var bad = Bytes(300); var key = Md5Key([9]);
        var (client, _) = Make((r, token) => r.RequestUri!.Host == "level3.blizzard.com" ? Task.FromResult(Replies.Whole200(r, bad))
            : Task.FromResult(Replies.Make(r, HttpStatusCode.GatewayTimeout, [1])));
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, 300, b => Md5Check(key, b)), CancellationToken.None));
        Assert.Equal(CdnFailure.HttpStatus, ex.Overall);
        Assert.True(ex.Retryable);
        Assert.Equal(CdnFailure.VerificationFailed, ex.Attempts[0].Failure);
    }

    [Theory]
    [InlineData("encoding")]
    [InlineData("media")]
    [InlineData("redirect")]
    [InlineData("206-to-whole")]
    public async Task ProtocolBreaksAreRefused(string kind)
    {
        var (client, _) = Make((r, _) =>
        {
            var reply = Replies.Whole200(r, Bytes(10));
            switch (kind)
            {
                case "encoding": reply.Content.Headers.ContentEncoding.Add("gzip"); break;
                case "media": reply.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html"); break;
                case "redirect": reply.StatusCode = HttpStatusCode.Found; reply.Headers.Location = new Uri("https://example.com/"); break;
                case "206-to-whole": reply.StatusCode = HttpStatusCode.PartialContent; reply.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 9, 10); break;
            }
            return Task.FromResult(reply);
        });
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([4])), CancellationToken.None));
        Assert.Equal(kind == "206-to-whole" ? CdnFailure.RangeMismatch : CdnFailure.Protocol, ex.Overall);
    }

    [Fact]
    public async Task NoHeadersInTimeIsATimeoutAndCallerCancellationStaysCancellation()
    {
        var (client, _) = Make(async (r, token) => { await Task.Delay(Timeout.Infinite, token); return Replies.Whole200(r, [1]); },
            new CdnClientOptions { HeaderTimeout = TimeSpan.FromMilliseconds(50) });
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([4])), CancellationToken.None));
        Assert.All(ex.Attempts, a => Assert.Equal(CdnFailure.Timeout, a.Failure));

        var (slow, _) = Make(async (r, token) => { await Task.Delay(Timeout.Infinite, token); return Replies.Whole200(r, [1]); });
        using var cancel = new CancellationTokenSource();
        var pending = slow.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([4])), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ABodyThatStallsIsATimeout()
    {
        var (client, _) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.OK, [], content: new StallingContent())),
            new CdnClientOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) });
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([4])), CancellationToken.None));
        Assert.All(ex.Attempts, a => Assert.Equal(CdnFailure.Timeout, a.Failure));
    }

    [Fact]
    public async Task AStreamedFetchNeverMixesHostsOnceBytesReachedTheSink()
    {
        var (client, handler) = Make((r, _) => Task.FromResult(Replies.Make(r, HttpStatusCode.OK, [], content: new WatchedContent(Bytes(200_000), failAfter: 70_000))));
        long got = 0;
        var ex = await Assert.ThrowsAsync<CdnException>(() => client.FetchToAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([5]), 200_000),
            (b, _) => { got += b.Length; return ValueTask.CompletedTask; }, CancellationToken.None));
        Assert.Equal(CdnFailure.Network, ex.Overall);
        Assert.Single(handler.Requests);
        Assert.True(got is > 0 and < 200_000);
    }

    [Fact]
    public async Task AStreamedFetchMovesToTheNextHostBeforeAnyByteAndDeliversInOrder()
    {
        var body = Bytes(300_000);
        var (client, _) = Make((r, _) => Task.FromResult(r.RequestUri!.Host == "level3.blizzard.com" ? Replies.UsCdn404(r) : Replies.Whole200(r, body)));
        using var sink = new MemoryStream();
        var reply = await client.FetchToAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([5]), 300_000),
            (b, t) => sink.WriteAsync(b, t), CancellationToken.None);
        Assert.Equal(body, sink.ToArray());
        Assert.Null(reply.Bytes);
        Assert.Equal(300_000, reply.Length);
        Assert.Equal(CdnFailure.NotFound, Assert.Single(reply.Failed).Failure);
    }

    [Fact]
    public async Task ASinkFailureIsTheCallersExceptionNotACdnFailure()
    {
        var (client, handler) = Make((r, _) => Task.FromResult(Replies.Whole200(r, Bytes(1000))));
        var ex = await Assert.ThrowsAsync<IOException>(() => client.FetchToAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, Md5Key([5])),
            (_, _) => throw new IOException("disk full"), CancellationToken.None));
        Assert.Equal("disk full", ex.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void RoutesAdmitOnlyOfficialHostsAndValidPaths()
    {
        var route = new CdnRoute(["blzddist1-a.akamaihd.net", "level3.blizzard.com", "us.cdn.blizzard.com", "evil.example", "level3.blizzard.com"], "tpr/wow");
        Assert.Equal(["level3.blizzard.com", "us.cdn.blizzard.com"], route.Hosts);
        Assert.Throws<ArgumentException>(() => new CdnRoute(["cdn.example.com"], "tpr/wow"));
        Assert.Throws<ArgumentException>(() => new CdnRoute(["us.cdn.blizzard.com"], "../etc"));
        Assert.Throws<ArgumentException>(() => new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow/"));
        Assert.Throws<ArgumentException>(() => route.UrlFor("eu.cdn.blizzard.com", CdnObjectKind.Data, Md5Key([1])));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdnRequest.Range(CdnObjectKind.Data, Md5Key([1]), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CdnRequest.Range(CdnObjectKind.Data, Md5Key([1]), 90, 20, 100));
    }

    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Stall());
        private sealed class Stall : MemoryStream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        }
    }
}
