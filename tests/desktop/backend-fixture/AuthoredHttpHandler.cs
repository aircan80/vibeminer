using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VibeMiner.Desktop.BackendFixture;

/// <summary>No inner handler exists: every response is authored locally, including failures.</summary>
internal sealed class AuthoredHttpHandler : HttpMessageHandler
{
    private readonly AuthoredScenario scenario;
    private readonly FixtureJournal journal;
    private readonly ReleaseGate release;

    internal AuthoredHttpHandler(AuthoredScenario scenario, string controls)
    {
        this.scenario = scenario;
        journal = new(controls);
        release = new(Path.Combine(controls, "release"));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var url = request.RequestUri?.AbsoluteUri ?? "";
        await journal.RecordHttpAsync(url, token);
        if (request.Method != HttpMethod.Get || !scenario.Responses.TryGetValue(url, out var bytes))
            return new(HttpStatusCode.NotFound) { RequestMessage = request, Content = new ByteArrayContent([]) };
        long from = 0, to = bytes.LongLength - 1;
        var partial = request.Headers.Range is not null;
        if (request.Headers.Range is { } range)
        {
            if (range.Ranges.Count != 1)
                return new(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request, Content = new ByteArrayContent([]) };
            var span = range.Ranges.Single();
            from = span.From ?? Math.Max(0, bytes.LongLength - (span.To ?? 0));
            to = span.From is null ? bytes.LongLength - 1 : Math.Min(span.To ?? bytes.LongLength - 1, bytes.LongLength - 1);
            if (from < 0 || from > to || from >= bytes.LongLength)
                return new(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request, Content = new ByteArrayContent([]) };
        }
        HttpContent content;
        var body = bytes.AsMemory((int)from, (int)(to - from + 1));
        if (url == VibeMiner.Backend.Tests.InventoryFixture.Url("data", scenario.GatedEncodingKey))
        {
            var attempt = await journal.BeginGatedAttemptAsync(token);
            content = new StreamContent(new GatedStream(body, scenario.GatedEncodingKey, attempt, release, journal));
        }
        else content = new ByteArrayContent(body.ToArray());
        content.Headers.ContentLength = body.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue(url.EndsWith("/versions", StringComparison.Ordinal) ||
            url.EndsWith("/cdns", StringComparison.Ordinal) ? "text/plain" : "application/octet-stream");
        if (partial) content.Headers.ContentRange = new(from, to, bytes.LongLength);
        return new(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { RequestMessage = request, Content = content };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) release.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ReleaseGate : IDisposable
{
    private readonly string path;
    private readonly FileSystemWatcher watcher;
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ReleaseGate(string path)
    {
        this.path = path;
        watcher = new(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        watcher.Created += (_, _) => Check();
        watcher.Changed += (_, _) => Check();
        watcher.Renamed += (_, _) => Check();
        watcher.Error += (_, args) => released.TrySetException(new IOException("Authored release watcher failed.", args.GetException()));
        watcher.EnableRaisingEvents = true;
        Check();
    }
    private void Check() { if (File.Exists(path)) released.TrySetResult(); }
    internal Task WaitAsync(CancellationToken token) { Check(); return released.Task.WaitAsync(token); }
    public void Dispose() { watcher.Dispose(); released.TrySetCanceled(); }
}

internal sealed class GatedStream(ReadOnlyMemory<byte> bytes, string key, int attempt,
    ReleaseGate release, FixtureJournal journal) : Stream
{
    private readonly int prefix = Math.Min(12, Math.Max(1, bytes.Length - 1));
    private int position;
    private bool announced;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (buffer.Length == 0 || position == bytes.Length) return 0;
        if (position >= prefix)
        {
            if (!announced)
            {
                announced = true;
                await journal.GateStateAsync("waiting", key, attempt, prefix, bytes.Length, CancellationToken.None);
            }
            try { await release.WaitAsync(token); }
            catch (OperationCanceledException)
            {
                await journal.GateStateAsync("canceled", key, attempt, position, bytes.Length, CancellationToken.None);
                throw;
            }
            await journal.GateStateAsync("released", key, attempt, position, bytes.Length, CancellationToken.None);
        }
        var boundary = position < prefix ? prefix : bytes.Length;
        var count = Math.Min(buffer.Length, boundary - position);
        bytes.Slice(position, count).CopyTo(buffer); position += count;
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => bytes.Length;
    public override long Position { get => position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The fixture requires async reads.");
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class FixtureJournal(string controls)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal async Task RecordHttpAsync(string url, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var countsPath = Path.Combine(controls, "http-counts.json");
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (File.Exists(countsPath))
            {
                if (new FileInfo(countsPath).Length > 32768) throw new IOException("Authored HTTP counter file exceeds its bound.");
                counts = JsonSerializer.Deserialize<Dictionary<string, long>>(await File.ReadAllTextAsync(countsPath, token), Json)
                    ?? throw new IOException("Authored HTTP counts are empty.");
            }
            if (!counts.ContainsKey(url) && counts.Count >= 64) throw new IOException("Authored HTTP identities exceed the fixture bound.");
            counts[url] = checked(counts.GetValueOrDefault(url) + 1);
            await WriteAsync(countsPath, counts, token);
            var log = Path.Combine(controls, "http.ndjson");
            if (File.Exists(log) && new FileInfo(log).Length > 1 << 20) throw new IOException("Authored HTTP event log exceeds its bound.");
            await File.AppendAllTextAsync(log, JsonSerializer.Serialize(new { url, processId = Environment.ProcessId,
                at = DateTimeOffset.UtcNow.ToString("O") }, Json) + "\n", token);
        }
        finally { gate.Release(); }
    }
    internal async Task<int> BeginGatedAttemptAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var path = Path.Combine(controls, "gated-attempts.txt"); var attempt = 0;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 16 || !int.TryParse(await File.ReadAllTextAsync(path, token), out attempt) || attempt < 0)
                    throw new IOException("Authored attempt checkpoint is invalid.");
            }
            if (attempt >= 10000) throw new IOException("Authored attempt bound exceeded.");
            await File.WriteAllTextAsync(path, (++attempt).ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            return attempt;
        }
        finally { gate.Release(); }
    }
    internal async Task GateStateAsync(string state, string key, int attempt, int transferred, int total, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { await WriteAsync(Path.Combine(controls, "gated-state.json"), new { state, encodingKey = key, attempt,
            transferred, total, processId = Environment.ProcessId, at = DateTimeOffset.UtcNow.ToString("O") }, token); }
        finally { gate.Release(); }
    }
    private static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json), token);
        File.Move(temporary, path, overwrite: true);
    }
}
