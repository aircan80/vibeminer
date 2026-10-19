using System.Net;
using System.Net.Sockets;
using System.Text;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class HttpStreamingTests
{
    [Fact]
    public async Task RealHttpStreamsAuthoredMetadataAndPreservesOfficialReceiptUrls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var served = ServePair(listener);
        using var handler = new LoopbackHandler(address);
        using var client = new HttpClient(handler);
        using var source = new OfficialDiscoverySource(client);
        var result = await source.FetchAsync("wow", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await served.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(result.Observations); Assert.Empty(result.Errors);
        Assert.Equal("online", result.Connection);
        Assert.All(result.Documents, document => Assert.StartsWith("https://us.version.battle.net/v2/products/wow/", document.Url, StringComparison.Ordinal));
    }
    private static async Task ServePair(TcpListener listener)
    {
        var requests = new List<Task>();
        for (var i = 0; i < 2; i++)
        {
            var socket = await listener.AcceptTcpClientAsync();
            requests.Add(Serve(socket));
        }
        await Task.WhenAll(requests);
        static async Task Serve(TcpClient socket)
        {
            using (socket)
            {
                var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync();
                while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                var body = Fixtures.Utf8(first!.Contains("/versions", StringComparison.Ordinal) ? Fixtures.Versions(Fixtures.Row()) : Fixtures.Cdns("us"));
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                for (var offset = 0; offset < body.Length; offset += 7)
                    await stream.WriteAsync(body.AsMemory(offset, Math.Min(7, body.Length - offset)));
            }
        }
    }
    private sealed class LoopbackHandler(Uri endpoint) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var local = new HttpRequestMessage(request.Method, new Uri(endpoint, request.RequestUri!.PathAndQuery));
            var response = await inner.SendAsync(local, cancellationToken);
            response.RequestMessage = request;
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    [Fact]
    public async Task CancelDuringBodyReadWaitsForCleanupAndDisposesBody()
    {
        var body = new GatedBody();
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal) ? new StreamContent(body) : new StringContent(Fixtures.Cdns("us")) }));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        using var cancel = new CancellationTokenSource();
        var operation = source.FetchAsync("wow", cancel.Token);
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await body.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(operation.IsCompleted);
        body.Cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(body.Disposed);
    }
    private sealed class GatedBody : Stream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Stopping.TrySetResult(); await Cleanup.Task; }
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
