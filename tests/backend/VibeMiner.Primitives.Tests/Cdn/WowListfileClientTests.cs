using VibeMiner.Cdn;
using System.Net;
using System.Net.Http.Headers;
using VibeMiner.Cdn.Testing;
using Xunit;

namespace VibeMiner.Primitives.Tests.Cdn;

/// <summary>D105: the layer 1 wow-listfile client against a local fake GitHub with authored bytes. No network.</summary>
public sealed class WowListfileClientTests
{
    private const string Tag = "202610281921";
    private const string Signed = "https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sp=r&sig=secret";
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Release = "{\"tag_name\":\"202610281921\",\"assets\":[]}"u8.ToArray();

    /// <summary>A local fake of GitHub's hosts. Unrouted addresses answer 404.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        internal readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.Ordinal);
        internal readonly List<HttpRequestMessage> Requests = [];
        internal bool Hang;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            lock (Requests) Requests.Add(request);
            if (Hang) await Task.Delay(Timeout.Infinite, token);
            return Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        internal static HttpResponseMessage Redirect(string to, HttpStatusCode status = HttpStatusCode.Found)
        { var response = new HttpResponseMessage(status); response.Headers.Location = new Uri(to); return response; }

        // Serves a file of these bytes the way the asset host does: a 206 for the asked single range, 416 past the end.
        internal static Func<HttpRequestMessage, HttpResponseMessage> Ranged(byte[] file) => request =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            var from = range.From!.Value;
            if (from >= file.Length) return new(HttpStatusCode.RequestedRangeNotSatisfiable);
            var to = Math.Min(range.To!.Value, file.Length - 1);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file[(int)from..(int)(to + 1)]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
            return response;
        };

        internal static HttpResponseMessage Partial(byte[] body, long from, long to, long total, bool declared = true)
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = declared ? new ByteArrayContent(body) : new StreamContent(new Unseekable(body)) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
            return response;
        }
    }

    /// <summary>A body whose length the content can't compute, so no Content-Length is sent.</summary>
    private sealed class Unseekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static byte[] File(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();

    private static GitHub Serving(byte[] file)
    {
        var github = new GitHub();
        github.Routes[WowListfileClient.AssetUrl(Tag).AbsoluteUri] = _ => GitHub.Redirect(Signed);
        github.Routes[Signed] = GitHub.Ranged(file);
        return github;
    }

    private static async Task<CdnException> Fails(Func<Task> fetch, CdnFailure failure)
    {
        var error = await Assert.ThrowsAsync<CdnException>(fetch);
        Assert.Equal(failure, error.Failure);
        return error;
    }

    [Fact]
    public async Task The_release_is_the_api_answer_exactly_as_served()
    {
        var github = new GitHub();
        github.Routes[WowListfileClient.LatestReleaseUrl.AbsoluteUri] = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Release) };
        var reply = await new WowListfileClient(new HttpClient(github), new ManualTime(T0)).FetchReleaseAsync(default);
        Assert.Equal(Release, reply.Bytes);
        Assert.Equal((WowListfileClient.LatestReleaseUrl, T0), (reply.Url, reply.ObservedAt));
        Assert.Equal("https://api.github.com/repos/wowdev/wow-listfile/releases/latest", reply.Url.AbsoluteUri);
        var request = github.Requests.Single();
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Null(request.Headers.Range);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_release_answer_over_one_MiB_is_refused_and_exactly_one_MiB_is_kept(bool declared)
    {
        var github = new GitHub();
        var body = new byte[WowListfileClient.MaxReleaseResponseBytes + 1];
        github.Routes[WowListfileClient.LatestReleaseUrl.AbsoluteUri] = _ => new(HttpStatusCode.OK)
            { Content = declared ? new ByteArrayContent(body) : new StreamContent(new Unseekable(body)) };
        var client = new WowListfileClient(new HttpClient(github));
        var error = await Fails(() => client.FetchReleaseAsync(default), CdnFailure.Protocol);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub sent a larger answer than VibeMiner allows.", error.Message);
        var limit = new byte[WowListfileClient.MaxReleaseResponseBytes];
        github.Routes[WowListfileClient.LatestReleaseUrl.AbsoluteUri] = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(limit)) };
        Assert.Equal(limit.Length, (await client.FetchReleaseAsync(default)).Bytes.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, CdnFailure.Protocol)]
    [InlineData(HttpStatusCode.TemporaryRedirect, CdnFailure.Protocol)]
    [InlineData(HttpStatusCode.NotFound, CdnFailure.NotFound)]
    [InlineData(HttpStatusCode.Forbidden, CdnFailure.HttpStatus)]
    [InlineData(HttpStatusCode.InternalServerError, CdnFailure.HttpStatus)]
    public async Task The_release_follows_no_redirect_and_maps_statuses(HttpStatusCode status, CdnFailure failure)
    {
        var github = new GitHub();
        github.Routes[WowListfileClient.LatestReleaseUrl.AbsoluteUri] = _ => GitHub.Redirect("https://api.github.com/elsewhere", status);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchReleaseAsync(default), failure);
        Assert.Equal((HttpStatusCode?)status, error.Attempts.Single().Status);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_part_follows_one_redirect_to_the_asset_host_and_asks_for_one_range()
    {
        var file = File(WowListfileClient.PartBytes + 1000);
        var github = Serving(file);
        var part = await new WowListfileClient(new HttpClient(github), new ManualTime(T0)).FetchPartAsync(Tag, 0, default);
        Assert.Equal(file[..WowListfileClient.PartBytes], part.Bytes);
        Assert.Equal((0L, (long)file.Length, T0), (part.Offset, part.TotalLength, part.ObservedAt));
        Assert.Equal($"https://github.com/wowdev/wow-listfile/releases/download/{Tag}/community-listfile-withcapitals.csv", part.Url.AbsoluteUri);
        Assert.Equal(new[] { WowListfileClient.AssetUrl(Tag).AbsoluteUri, Signed }, github.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        foreach (var request in github.Requests)
        {
            Assert.Equal($"bytes=0-{WowListfileClient.PartBytes - 1}", request.Headers.Range!.ToString());
            Assert.Equal("application/octet-stream", request.Headers.Accept.ToString());
        }
    }

    [Fact]
    public async Task A_redirect_to_the_older_asset_host_is_followed_too()
    {
        const string objects = "https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/abc?X-Amz-Signature=secret";
        var file = File(100);
        var github = new GitHub();
        github.Routes[WowListfileClient.AssetUrl(Tag).AbsoluteUri] = _ => GitHub.Redirect(objects, HttpStatusCode.SeeOther);
        github.Routes[objects] = GitHub.Ranged(file);
        var part = await new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default);
        Assert.Equal(file, part.Bytes);
        Assert.Equal(100L, part.TotalLength);
        Assert.Equal(WowListfileClient.AssetUrl(Tag), part.Url);
    }

    [Fact]
    public async Task The_last_part_is_short_and_a_part_past_the_end_is_an_http_status()
    {
        var file = File(WowListfileClient.PartBytes + 1000);
        var client = new WowListfileClient(new HttpClient(Serving(file)));
        var last = await client.FetchPartAsync(Tag, 1, default);
        Assert.Equal(file[WowListfileClient.PartBytes..], last.Bytes);
        Assert.Equal(((long)WowListfileClient.PartBytes, (long)file.Length), (last.Offset, last.TotalLength));
        // The caller knows the size from the release and never asks past the end, so a 416 is just a status.
        var past = await Fails(() => client.FetchPartAsync(Tag, 2, default), CdnFailure.HttpStatus);
        Assert.Equal((HttpStatusCode?)HttpStatusCode.RequestedRangeNotSatisfiable, past.Attempts.Single().Status);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub answered HTTP 416.", past.Message);
    }

    [Fact]
    public async Task A_failure_at_the_asset_host_never_shows_the_signed_query()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => new(HttpStatusCode.InternalServerError);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.HttpStatus);
        var attempt = error.Attempts.Single();
        Assert.Equal("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc", attempt.Url.AbsoluteUri);
        Assert.Equal("release-assets.githubusercontent.com", attempt.Host);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task A_second_redirect_is_refused()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => GitHub.Redirect("https://objects.githubusercontent.com/again");
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.Protocol);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub redirected the download more than once.", error.Message);
        Assert.Equal(2, github.Requests.Count);
    }

    [Theory]
    [InlineData("https://example.invalid/file.csv")]
    [InlineData("http://release-assets.githubusercontent.com/file.csv")]
    [InlineData("https://release-assets.githubusercontent.com:8443/file.csv")]
    [InlineData("https://user:pass@release-assets.githubusercontent.com/file.csv")]
    [InlineData("https://api.github.com/file.csv")]
    public async Task A_redirect_off_the_asset_hosts_is_refused_and_not_followed(string location)
    {
        var github = new GitHub();
        github.Routes[WowListfileClient.AssetUrl(Tag).AbsoluteUri] = _ => GitHub.Redirect(location, HttpStatusCode.MovedPermanently);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.Protocol);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub redirected the download to an address VibeMiner doesn’t allow.", error.Message);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_whole_file_answer_instead_of_a_part_is_refused()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(File(100)) };
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.RangeIgnored);
        Assert.Equal((HttpStatusCode?)HttpStatusCode.OK, error.Attempts.Single().Status);
    }

    [Theory]
    [InlineData(1, 99, 100)]                                  // starts elsewhere
    [InlineData(0, 98, 100)]                                  // ends before the end
    [InlineData(0, WowListfileClient.PartBytes, 20 << 20)]    // one byte more than a part
    public async Task A_part_whose_range_is_not_exactly_the_asked_one_is_refused(long from, long to, long total)
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => GitHub.Partial(new byte[to - from + 1], from, to, total);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.RangeMismatch);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub sent a part VibeMiner didn’t ask for.", error.Message);
    }

    [Fact]
    public async Task A_multi_range_or_unknown_total_answer_is_refused()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(File(100)) };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/byteranges; boundary=x");
            return response;
        };
        var client = new WowListfileClient(new HttpClient(github));
        await Fails(() => client.FetchPartAsync(Tag, 0, default), CdnFailure.RangeMismatch);
        github.Routes[Signed] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(File(100)) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99);
            return response;
        };
        await Fails(() => client.FetchPartAsync(Tag, 0, default), CdnFailure.RangeMismatch);
    }

    [Theory]
    [InlineData(true, 99, CdnFailure.SizeConflict)]
    [InlineData(true, 101, CdnFailure.SizeConflict)]
    [InlineData(false, 99, CdnFailure.Network)]
    [InlineData(false, 101, CdnFailure.SizeConflict)]
    public async Task A_body_of_another_length_than_its_range_is_refused(bool declared, int length, CdnFailure failure)
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => GitHub.Partial(File(length), 0, 99, 100, declared);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), failure);
        Assert.Equal(failure == CdnFailure.Network
            ? "Getting the wow-listfile community file names from GitHub failed. The download ended early."
            : "Getting the wow-listfile community file names from GitHub failed. GitHub sent a part of another length than its range.", error.Message);
    }

    [Fact]
    public async Task A_part_that_ends_early_with_its_length_declared_is_a_network_failure()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ =>
        {
            var response = GitHub.Partial(File(60), 0, 99, 100, declared: false);
            response.Content.Headers.ContentLength = 100;
            return response;
        };
        await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.Network);
    }

    [Theory]
    [InlineData(60, CdnFailure.Network)]
    [InlineData(140, CdnFailure.SizeConflict)]
    public async Task A_release_of_another_length_than_declared_is_refused(int length, CdnFailure failure)
    {
        var github = new GitHub();
        github.Routes[WowListfileClient.LatestReleaseUrl.AbsoluteUri] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[length])) };
            response.Content.Headers.ContentLength = 100;
            return response;
        };
        await Fails(() => new WowListfileClient(new HttpClient(github)).FetchReleaseAsync(default), failure);
    }

    [Fact]
    public async Task An_encoded_part_is_refused()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ =>
        {
            var response = GitHub.Partial(File(100), 0, 99, 100);
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        };
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.Protocol);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub sent an encoded part.", error.Message);
    }

    [Fact]
    public async Task A_file_over_512_MiB_is_too_large()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ => GitHub.Partial(new byte[16], 0, WowListfileClient.PartBytes - 1, WowListfileClient.MaxAssetBytes + 1);
        var error = await Fails(() => new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, 0, default), CdnFailure.TooLarge);
        Assert.False(error.Retryable);
        Assert.Equal("The newest wow-listfile file is larger than VibeMiner allows (512 MiB), so it wasn’t downloaded.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Release1")]
    [InlineData("1.2")]
    [InlineData("../x")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task A_tag_outside_lowercase_letters_digits_underscore_and_dash_is_refused(string tag)
    {
        var github = new GitHub();
        // The returned task fails; the call itself doesn't throw.
        var fetch = new WowListfileClient(new HttpClient(github)).FetchPartAsync(tag, 0, default);
        await Assert.ThrowsAsync<ArgumentException>(() => fetch);
        Assert.Empty(github.Requests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(WowListfileClient.MaxParts)]
    public async Task An_index_outside_the_parts_is_refused(int index)
    {
        var github = new GitHub();
        var fetch = new WowListfileClient(new HttpClient(github)).FetchPartAsync(Tag, index, default);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fetch);
        Assert.Empty(github.Requests);
        Assert.Equal(64, WowListfileClient.MaxParts);
    }

    [Fact]
    public async Task Headers_that_never_come_hit_the_header_budget_and_a_cancellation_stops_it_at_once()
    {
        var github = new GitHub { Hang = true };
        var slow = new WowListfileClient(new HttpClient(github), headerBudget: TimeSpan.FromMilliseconds(100));
        var timedOut = await Fails(() => slow.FetchPartAsync(Tag, 0, default), CdnFailure.Timeout);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub didn’t answer within 0.1 seconds.", timedOut.Message);
        Assert.True(timedOut.Retryable);
        await Fails(() => slow.FetchReleaseAsync(default), CdnFailure.Timeout);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WowListfileClient(new HttpClient(github)).FetchReleaseAsync(cancel.Token));
    }

    [Fact]
    public async Task A_body_that_stalls_hits_the_idle_budget()
    {
        var github = Serving(File(100));
        github.Routes[Signed] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new Trickle(10, TimeSpan.Zero, stallAfter: 1)) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99, 100);
            return response;
        };
        var client = new WowListfileClient(new HttpClient(github), idleBudget: TimeSpan.FromMilliseconds(100));
        var error = await Fails(() => client.FetchPartAsync(Tag, 0, default), CdnFailure.Timeout);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. GitHub stopped sending for 0.1 seconds.", error.Message);
    }

    [Fact]
    public async Task A_body_that_keeps_trickling_is_stopped_by_the_total_cap()
    {
        // Each read is well inside the idle budget, so only the fetch's cap stops it.
        var github = Serving(File(100));
        github.Routes[Signed] = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(new Trickle(1, TimeSpan.FromMilliseconds(30))) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 99, 100);
            return response;
        };
        var client = new WowListfileClient(new HttpClient(github), fetchBudget: TimeSpan.FromMilliseconds(500), idleBudget: TimeSpan.FromSeconds(5));
        var error = await Fails(() => client.FetchPartAsync(Tag, 0, default), CdnFailure.Timeout);
        Assert.Equal("Getting the wow-listfile community file names from GitHub failed. The download from GitHub didn’t finish within 0.5 seconds.", error.Message);
    }

    /// <summary>Returns <c>chunk</c> bytes per read after <c>delay</c>, and after <c>stallAfter</c> reads never again until cancelled.</summary>
    private sealed class Trickle(int chunk, TimeSpan delay, int stallAfter = int.MaxValue) : Stream
    {
        private int reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (reads++ >= stallAfter) await Task.Delay(Timeout.Infinite, token);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            var count = Math.Min(chunk, buffer.Length);
            buffer.Span[..count].Fill(1);
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Offline_is_its_own_state()
    {
        var client = new WowListfileClient(new HttpClient(new Failing(new HttpRequestException(HttpRequestError.ConnectionError, "offline"))));
        var error = await Fails(() => client.FetchReleaseAsync(default), CdnFailure.Offline);
        Assert.Equal("VibeMiner couldn’t reach GitHub to get the wow-listfile community file names.", error.Message);
        await Fails(() => new WowListfileClient(new HttpClient(new Failing(new HttpRequestException(HttpRequestError.InvalidResponse, "bad"))))
            .FetchPartAsync(Tag, 0, default), CdnFailure.Network);
    }

    [Fact]
    public async Task One_fetch_runs_at_a_time()
    {
        var github = new GitHub { Hang = true };
        var client = new WowListfileClient(new HttpClient(github));
        using var cancel = new CancellationTokenSource();
        var running = new[] { client.FetchReleaseAsync(cancel.Token), client.FetchReleaseAsync(cancel.Token) };
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (github.Requests.Count < 1) await Task.Delay(10, bound.Token);
        await Task.Delay(100);
        Assert.Single(github.Requests);
        cancel.Cancel();
        foreach (var fetch in running) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public void The_connection_budget_allows_its_count_per_fetch_and_resets()
    {
        var budget = new WowListfileConnections();
        for (var i = 0; i < WowListfileConnections.PerFetch; i++) Assert.True(budget.TryTake());
        Assert.False(budget.TryTake());
        budget.Reset();
        Assert.True(budget.TryTake());
        using var handler = WowListfileClient.CreateHandler(budget);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    /// <summary>A proxy that accepts each connection and closes it before answering, as in the WoWDBDefs #124 tests.</summary>
    private sealed class DroppingProxy : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private int accepted;
        private readonly Task loop;
        internal DroppingProxy()
        {
            listener.Start();
            loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        Interlocked.Increment(ref accepted);
                        client.Client.Close();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        internal int Accepted => Volatile.Read(ref accepted);
        internal SocketsHttpHandler Handler(WowListfileConnections connections)
        {
            var handler = WowListfileClient.CreateHandler(connections);
            handler.Proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"); handler.UseProxy = true;
            return handler;
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
    }

    [Fact]
    public async Task A_fetch_through_a_proxy_that_drops_connections_opens_at_most_its_budget()
    {
        await using var proxy = new DroppingProxy();
        var connections = new WowListfileConnections();
        using var handler = proxy.Handler(connections);
        var client = new WowListfileClient(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, connections: connections);
        await Fails(() => client.FetchReleaseAsync(default), CdnFailure.Offline);
        var first = proxy.Accepted;
        Assert.InRange(first, 1, WowListfileConnections.PerFetch);
        // The next fetch gets a fresh budget, never more.
        await Fails(() => client.FetchPartAsync(Tag, 0, default), CdnFailure.Offline);
        Assert.InRange(proxy.Accepted - first, 1, WowListfileConnections.PerFetch);
    }

    [Fact]
    public async Task The_handler_opens_no_connection_once_its_budget_is_spent()
    {
        await using var proxy = new DroppingProxy();
        using var handler = proxy.Handler(new WowListfileConnections(perFetch: 0));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(WowListfileClient.LatestReleaseUrl));
        Assert.Equal(HttpRequestError.ConnectionError, error.HttpRequestError);
        Assert.Equal(0, proxy.Accepted);
    }

    private sealed class Failing(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw failure;
    }
}
