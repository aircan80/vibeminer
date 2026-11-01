using System.Net;
using VibeMiner.Cdn.Testing;
using Xunit;

namespace VibeMiner.Cdn.Tests;

/// <summary>D081 / D113 / #124: the layer 1 WoWDBDefs client against a local fake GitHub. No network.</summary>
public sealed class WoWDBDefsClientTests
{
    internal const string Revision = "1111111111111111111111111111111111111111";
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Dbd = "COLUMNS\nint ID\n\nLAYOUT 1234ABCD\nBUILD 1.2.3.4\nID<32>\n"u8.ToArray();

    /// <summary>A local fake of the two GitHub hosts. Unrouted addresses answer 404.</summary>
    internal sealed class GitHub : HttpMessageHandler
    {
        internal readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new(StringComparer.Ordinal);
        internal readonly List<HttpRequestMessage> Requests = [];
        internal Exception? Failure;
        internal bool Hang;
        internal void Serve(string table, string revision, byte[] bytes)
        {
            Routes[WoWDBDefsClient.CommitsUrl(table).AbsoluteUri] = () => Text($"[{{\"sha\":\"{revision}\",\"commit\":{{\"message\":\"authored\"}}}}]");
            Routes[WoWDBDefsClient.DefinitionUrl(revision, table)] = () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        internal static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme);
            Assert.Contains(uri.Host, new[] { WoWDBDefsClient.ApiHost, WoWDBDefsClient.RawHost });
            lock (Requests) Requests.Add(request);
            if (Hang) await Task.Delay(Timeout.Infinite, token);
            if (Failure is not null) throw Failure;
            return Routes.TryGetValue(uri.AbsoluteUri, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static async Task<CdnException> Fails(IWoWDBDefsClient client, CdnFailure failure, string table = "Item")
    {
        var error = await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync(table, default));
        Assert.Equal(failure, error.Failure);
        return error;
    }

    [Fact]
    public async Task A_fetch_returns_the_bytes_of_the_last_commit_with_its_raw_url_and_time()
    {
        var github = new GitHub(); github.Serve("Item", Revision, Dbd);
        var reply = await new WoWDBDefsClient(new HttpClient(github), new ManualTime(T0)).FetchAsync("Item", default);
        Assert.Equal(("Item", Revision, T0), (reply.TableName, reply.Revision, reply.ObservedAt));
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{Revision}/definitions/Item.dbd", reply.Url.AbsoluteUri);
        Assert.Equal(Dbd, reply.Bytes);
        Assert.Equal(new[] { WoWDBDefsClient.CommitsUrl("Item"), reply.Url }, github.Requests.Select(r => r.RequestUri));
        Assert.Equal("https://api.github.com/repos/wowdev/WoWDBDefs/commits?path=definitions/Item.dbd&sha=master&per_page=1",
            github.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", github.Requests[0].Headers.Accept.ToString());
        Assert.Equal("2022-11-28", github.Requests[0].Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Equal("text/plain", github.Requests[1].Headers.Accept.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => new WoWDBDefsClient(new HttpClient(github)).FetchAsync("../Item", default));
        await Assert.ThrowsAsync<ArgumentException>(() => new WoWDBDefsClient(new HttpClient(github)).FetchAsync("", default));
    }

    [Fact]
    public async Task Offline_is_its_own_state_and_a_cached_definition_keeps_working()
    {
        // The client keeps no cache: the layer above keeps its definition. Here: offline is its own state and nothing sticks.
        var github = new GitHub { Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline") };
        var client = new WoWDBDefsClient(new HttpClient(github), new ManualTime(T0));
        var offline = await Fails(client, CdnFailure.Offline);
        Assert.True(offline.Retryable); Assert.Equal(CdnFailure.Offline, offline.Overall);
        Assert.Equal("VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer Item definition.", offline.Message);
        Assert.Equal((WoWDBDefsClient.ApiHost, T0), (offline.Attempts.Single().Host, offline.ObservedAt));
        github.Failure = null; github.Serve("Item", Revision, Dbd);
        Assert.Equal(Dbd, (await client.FetchAsync("Item", default)).Bytes);
        github.Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "offline");
        await Fails(client, CdnFailure.Offline);
    }

    // The shapes .NET 10 gives a connection, direct or to a proxy, that ends before any answer, and two that aren't that.
    [Theory]
    [InlineData("reset", CdnFailure.Offline)]
    [InlineData("ended", CdnFailure.Offline)]
    [InlineData("tls-reset", CdnFailure.Offline)]
    [InlineData("certificate", CdnFailure.Network)]
    [InlineData("garbled", CdnFailure.Network)]
    public async Task A_connection_closed_before_any_answer_is_offline_every_time(string shape, CdnFailure expected)
    {
        // #125: a proxy's reset surfaced as an unknown error over a socket error and read as a failed fetch.
        var reset = new IOException("Unable to read data from the transport connection: Connection reset by peer.",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));
        Exception failure = shape switch
        {
            "reset" => new HttpRequestException(HttpRequestError.Unknown, "An error occurred while sending the request.", reset),
            "ended" => new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.",
                new HttpIOException(HttpRequestError.ResponseEnded)),
            "tls-reset" => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", reset),
            "certificate" => new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.",
                new System.Security.Authentication.AuthenticationException("The remote certificate is invalid.")),
            _ => new HttpRequestException(HttpRequestError.InvalidResponse, "The server returned an invalid or unrecognized response.",
                new HttpIOException(HttpRequestError.InvalidResponse)),
        };
        var error = await Fails(new WoWDBDefsClient(new HttpClient(new GitHub { Failure = failure })), expected);
        Assert.True(error.Retryable);
        if (expected == CdnFailure.Network) Assert.Equal("Fetching a newer Item definition from WoWDBDefs failed. The connection to GitHub failed.", error.Message);
    }

    [Fact]
    public async Task A_connect_timeout_is_offline_and_a_cached_definition_keeps_working()
    {
        var github = new GitHub { Failure = new TaskCanceledException("connect", new TimeoutException("connect timeout")) };
        var client = new WoWDBDefsClient(new HttpClient(github));
        await Fails(client, CdnFailure.Offline);
        github.Failure = null; github.Serve("Item", Revision, Dbd);
        Assert.Equal(Dbd, (await client.FetchAsync("Item", default)).Bytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_definition_over_one_MiB_is_too_large_and_never_saved(bool declared)
    {
        var github = new GitHub();
        github.Serve("Item", Revision, []);
        var huge = new byte[WoWDBDefsClient.MaxDefinitionBytes + 1];
        github.Routes[WoWDBDefsClient.DefinitionUrl(Revision, "Item")] = () => new(HttpStatusCode.OK)
            { Content = declared ? new ByteArrayContent(huge) : new StreamContent(new MemoryStream(huge)) };
        var error = await Fails(new WoWDBDefsClient(new HttpClient(github)), CdnFailure.TooLarge);
        Assert.False(error.Retryable);
        Assert.Equal("The newest WoWDBDefs Item definition is larger than VibeMiner allows (1 MiB), so it wasn’t used.", error.Message);
        // Exactly 1 MiB is allowed.
        var limit = new byte[WoWDBDefsClient.MaxDefinitionBytes];
        github.Routes[WoWDBDefsClient.DefinitionUrl(Revision, "Item")] = () => new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(limit)) };
        Assert.Equal(limit.Length, (await new WoWDBDefsClient(new HttpClient(github)).FetchAsync("Item", default)).Bytes.Length);
    }

    [Theory]
    [InlineData("big", "GitHub sent a larger answer than VibeMiner allows.")]
    [InlineData("{}", "GitHub sent an answer VibeMiner doesn’t understand.")]
    [InlineData("[{\"sha\":\"ABC\"}]", "GitHub sent an answer VibeMiner doesn’t understand.")]
    [InlineData("[{\"sha\":\"1111111111111111111111111111111111111111\"", "GitHub sent an answer VibeMiner doesn’t understand.")]
    public async Task A_commits_answer_that_is_too_big_or_unreadable_is_a_protocol_failure_and_no_file_is_asked_for(string body, string detail)
    {
        var github = new GitHub();
        github.Routes[WoWDBDefsClient.CommitsUrl("Item").AbsoluteUri] = () => body == "big"
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[WoWDBDefsClient.MaxCommitResponseBytes + 1])) }
            : GitHub.Text(body);
        var error = await Fails(new WoWDBDefsClient(new HttpClient(github)), CdnFailure.Protocol);
        Assert.Equal("Fetching a newer Item definition from WoWDBDefs failed. " + detail, error.Message);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_table_WoWDBDefs_does_not_have_is_not_found()
    {
        var github = new GitHub();
        github.Routes[WoWDBDefsClient.CommitsUrl("Item").AbsoluteUri] = () => GitHub.Text("[]");
        var client = new WoWDBDefsClient(new HttpClient(github));
        var none = await Fails(client, CdnFailure.NotFound);
        Assert.Equal("WoWDBDefs on GitHub has no Item.dbd file to fetch, so only the bundled Item definition can be used.", none.Message);
        // The commit exists but the file doesn't.
        github.Routes[WoWDBDefsClient.CommitsUrl("Item").AbsoluteUri] = () => GitHub.Text($"[{{\"sha\":\"{Revision}\"}}]");
        var missing = await Fails(client, CdnFailure.NotFound);
        Assert.Equal((HttpStatusCode?)HttpStatusCode.NotFound, missing.Attempts.Single().Status);
        Assert.Equal(WoWDBDefsClient.DefinitionUrl(Revision, "Item"), missing.Attempts.Single().Url.AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, CdnFailure.Protocol, "GitHub redirected the request, and VibeMiner doesn’t follow redirects.")]
    [InlineData(HttpStatusCode.Forbidden, CdnFailure.HttpStatus, "GitHub is limiting requests right now.")]
    [InlineData(HttpStatusCode.TooManyRequests, CdnFailure.HttpStatus, "GitHub is limiting requests right now.")]
    [InlineData(HttpStatusCode.InternalServerError, CdnFailure.HttpStatus, "GitHub answered HTTP 500.")]
    public async Task Redirects_rate_limits_and_server_errors_fail_without_following_anything(HttpStatusCode status, CdnFailure failure, string detail)
    {
        var github = new GitHub();
        github.Serve("Item", Revision, Dbd);
        github.Routes[WoWDBDefsClient.DefinitionUrl(Revision, "Item")] = () =>
        { var response = new HttpResponseMessage(status); response.Headers.Location = new Uri("https://example.invalid/Item.dbd"); return response; };
        var error = await Fails(new WoWDBDefsClient(new HttpClient(github)), failure);
        Assert.Equal("Fetching a newer Item definition from WoWDBDefs failed. " + detail, error.Message);
        Assert.Equal((HttpStatusCode?)status, error.Attempts.Single().Status);
        // A redirect is a protocol refusal (not retryable at layer 1); the rest can be tried again.
        Assert.Equal(failure != CdnFailure.Protocol, error.Retryable);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task A_fetch_stops_at_its_time_budget_and_a_cancellation_stops_it_at_once()
    {
        var github = new GitHub { Hang = true };
        var slow = new WoWDBDefsClient(new HttpClient(github), fetchBudget: TimeSpan.FromMilliseconds(100));
        var timedOut = await Fails(slow, CdnFailure.Timeout);
        Assert.Equal("Fetching a newer Item definition from WoWDBDefs failed. WoWDBDefs didn’t answer within 0.1 seconds.", timedOut.Message);
        Assert.True(timedOut.Retryable);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WoWDBDefsClient(new HttpClient(github)).FetchAsync("Item", cancel.Token));
    }

    [Fact]
    public async Task At_most_two_fetches_run_at_once()
    {
        var github = new GitHub { Hang = true };
        var client = new WoWDBDefsClient(new HttpClient(github));
        using var cancel = new CancellationTokenSource();
        var running = Enumerable.Range(0, 3).Select(_ => client.FetchAsync("Item", cancel.Token)).ToArray();
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (github.Requests.Count < 2) await Task.Delay(10, bound.Token);
        await Task.Delay(100);
        Assert.Equal(2, github.Requests.Count);
        cancel.Cancel();
        foreach (var fetch in running) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    /// <summary>A proxy that accepts each connection and closes it before answering, as QA's refusing proxy did (#124).</summary>
    private sealed class DroppingProxy : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<TimeSpan> accepted = [];
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
                        lock (accepted) accepted.Add(clock.Elapsed);
                        client.Client.Close();
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
        internal TimeSpan[] Accepted { get { lock (accepted) return [.. accepted]; } }
        internal readonly WoWDBDefsConnections Connections = new();
        internal SocketsHttpHandler Handler()
        {
            var handler = WoWDBDefsClient.CreateHandler(Connections);
            handler.Proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"); handler.UseProxy = true;
            return handler;
        }
        internal WoWDBDefsClient Client(SocketsHttpHandler handler) =>
            new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, connections: Connections);
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await loop; stop.Dispose(); }
    }

    [Fact]
    public async Task A_failed_check_through_a_proxy_that_drops_connections_opens_at_most_two_a_second_apart()
    {
        // Before #124 one check opened 12 to 16 connections to the proxy within about 35 ms.
        await using var proxy = new DroppingProxy();
        using var handler = proxy.Handler();
        var client = proxy.Client(handler);
        await Fails(client, CdnFailure.Offline);
        var seen = proxy.Accepted;
        // The pool usually reconnects at once after a drop; it may also give up after the first.
        Assert.InRange(seen.Length, 1, WoWDBDefsConnections.PerRequest);
        if (seen.Length == 2) Assert.True(seen[1] - seen[0] >= WoWDBDefsConnections.Spacing - TimeSpan.FromMilliseconds(50), $"{seen[1] - seen[0]}");
        // A Retry is a new request with its own two connections, never a burst.
        await Fails(client, CdnFailure.Offline);
        Assert.InRange(proxy.Accepted.Length - seen.Length, 1, WoWDBDefsConnections.PerRequest);
    }

    [Fact]
    public async Task Cancelling_a_check_while_it_waits_to_reconnect_stops_it_at_once()
    {
        await using var proxy = new DroppingProxy();
        using var handler = proxy.Handler();
        var client = proxy.Client(handler);
        using var cancel = new CancellationTokenSource();
        var check = client.FetchAsync("Item", cancel.Token);
        using (var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (proxy.Accepted.Length == 0) await Task.Delay(10, bound.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cancel.Cancel();
        // The pool may already have given up on the dropped connection; either way nothing waits out the reconnect.
        try { await check; Assert.Fail("A dropped connection never answers."); }
        catch (CdnException error) { Assert.Equal(CdnFailure.Offline, error.Failure); }
        catch (OperationCanceledException) { }
        Assert.True(watch.Elapsed < WoWDBDefsConnections.Spacing / 2, $"{watch.Elapsed}");
        // Whatever the pool still does after the cancel stays inside the request's allowance.
        await Task.Delay(WoWDBDefsConnections.Spacing + TimeSpan.FromMilliseconds(300));
        Assert.InRange(proxy.Accepted.Length, 1, WoWDBDefsConnections.PerRequest);
    }

    [Fact]
    public async Task A_reconnect_waits_its_spacing_cancellably_and_is_refused_once_its_request_ended()
    {
        var connections = new WoWDBDefsConnections();
        using (connections.Grant())
        {
            await connections.TakeAsync(default);
            using var cancel = new CancellationTokenSource();
            var reconnect = connections.TakeAsync(cancel.Token).AsTask();
            await Task.Delay(50);
            Assert.False(reconnect.IsCompleted);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect);
            Assert.True(watch.Elapsed < WoWDBDefsConnections.Spacing / 2, $"{watch.Elapsed}");
        }
        var late = new WoWDBDefsConnections();
        Task waiting;
        using (late.Grant()) { await late.TakeAsync(default); waiting = late.TakeAsync(default).AsTask(); }
        // The request ended while its reconnect waited: the reconnect takes nothing.
        await Assert.ThrowsAsync<HttpRequestException>(() => waiting);
    }

    [Fact]
    public async Task Each_request_opens_its_first_connection_at_once_so_a_working_fetch_never_waits()
    {
        // The commit answer then the file: two requests one after the other, each needing one new connection.
        var connections = new WoWDBDefsConnections();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var request = 0; request < 3; request++)
            using (connections.Grant()) await connections.TakeAsync(default);
        Assert.True(watch.Elapsed < WoWDBDefsConnections.Spacing / 2, $"{watch.Elapsed}");
        // Once no request is in flight nothing is left to take.
        await Assert.ThrowsAsync<HttpRequestException>(() => connections.TakeAsync(default).AsTask());
    }

    [Fact]
    public async Task The_definitions_handler_opens_no_connection_for_a_request_it_did_not_make()
    {
        await using var proxy = new DroppingProxy();
        using var handler = proxy.Handler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(WoWDBDefsClient.CommitsUrl("Item")));
        Assert.Equal(HttpRequestError.ConnectionError, error.HttpRequestError);
        Assert.Empty(proxy.Accepted);
    }
}
