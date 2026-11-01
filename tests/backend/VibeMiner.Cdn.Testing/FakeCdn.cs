using System.Net;
using VibeMiner.Cdn;

namespace VibeMiner.Cdn.Testing;

/// <summary>What a fake host does with a request, set per host.</summary>
public enum FakeHostMode
{
    /// <summary>Serves what it has: 200 whole, 206 for a range, 404 for a key it lacks.</summary>
    Normal,
    /// <summary>Connection refused for every request.</summary>
    Offline,
    /// <summary>Answers a range request with 200 and the whole object (as some hosts answer multi-range requests).</summary>
    IgnoresRanges,
    /// <summary>HTTP 503 for every request.</summary>
    Unavailable,
}

/// <summary>One request the fake CDN saw.</summary>
public sealed record FakeCdnRequest(string Host, CdnObjectKind Kind, CdnKey Key, long? Offset, long? Length, FetchLabel Label);

/// <summary>Free-form label a test can attach to requests (for example, which priority sent them).</summary>
public readonly record struct FetchLabel(string? Value);

/// <summary>
/// A synthetic CDN: objects by kind and key, served by every host unless a host is told otherwise. The real
/// <see cref="CdnClient"/> reaches it through <see cref="Handler"/>; <see cref="ScriptedCdnClient"/> reads it directly.
/// Both front ends must give the same answers; the shared contract tests check that.
/// </summary>
public sealed class FakeCdn
{
    private readonly Lock gate = new();
    private readonly Dictionary<(CdnObjectKind, CdnKey), byte[]> objects = [];
    private readonly Dictionary<string, FakeHostMode> modes = new(StringComparer.Ordinal);
    private readonly HashSet<(string, CdnObjectKind, CdnKey)> missing = [];
    private readonly HashSet<(string, CdnObjectKind, CdnKey)> corrupt = [];
    private readonly HashSet<string> http2 = new(StringComparer.Ordinal);
    private readonly Dictionary<(CdnObjectKind, CdnKey), int> breaks = [];
    private readonly List<FakeCdnRequest> requests = [];

    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>When set, every request waits for this before answering (cancellation still ends the wait).</summary>
    public Func<FakeCdnRequest, CancellationToken, Task>? BeforeAnswer { get; set; }

    public FakeCdn Add(CdnObjectKind kind, CdnKey key, byte[] bytes) { lock (gate) objects[(kind, key)] = bytes; return this; }
    public FakeCdn Remove(CdnObjectKind kind, CdnKey key) { lock (gate) objects.Remove((kind, key)); return this; }
    public FakeCdn SetMode(string host, FakeHostMode mode) { lock (gate) modes[host] = mode; return this; }
    /// <summary>This host answers 404 for the key even though others have it.</summary>
    public FakeCdn MissingOn(string host, CdnObjectKind kind, CdnKey key) { lock (gate) missing.Add((host, kind, key)); return this; }
    /// <summary>This host serves the key with one byte flipped.</summary>
    public FakeCdn CorruptOn(string host, CdnObjectKind kind, CdnKey key) { lock (gate) corrupt.Add((host, kind, key)); return this; }
    public FakeCdn Http2(string host) { lock (gate) http2.Add(host); return this; }
    /// <summary>The next whole-object reply for the key breaks off after this many body bytes (once).</summary>
    public FakeCdn BreakOnceAfter(CdnObjectKind kind, CdnKey key, int bytes) { lock (gate) breaks[(kind, key)] = bytes; return this; }

    public IReadOnlyList<FakeCdnRequest> Requests { get { lock (gate) return requests.ToArray(); } }
    public void ClearRequests() { lock (gate) requests.Clear(); }

    internal sealed record Answer(HttpStatusCode Status, byte[] Body, long? RangeFrom, long? Total, Version Version, int? BreakAfter = null);

    /// <summary>The answer one host gives; throws <see cref="HttpRequestException"/> for an offline host.</summary>
    internal async Task<Answer> AnswerAsync(string host, CdnObjectKind kind, CdnKey key, long? offset, long? length, FetchLabel label, CancellationToken token)
    {
        var request = new FakeCdnRequest(host, kind, key, offset, length, label);
        lock (gate) requests.Add(request);
        if (BeforeAnswer is { } wait) await wait(request, token);
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            var mode = modes.GetValueOrDefault(host);
            var version = http2.Contains(host) ? HttpVersion.Version20 : HttpVersion.Version11;
            if (mode == FakeHostMode.Offline) throw new HttpRequestException(HttpRequestError.ConnectionError, $"{host}: connection refused (fake).");
            if (mode == FakeHostMode.Unavailable) return new(HttpStatusCode.ServiceUnavailable, "busy"u8.ToArray(), null, null, version);
            if (missing.Contains((host, kind, key)) || !objects.TryGetValue((kind, key), out var bytes))
                return new(HttpStatusCode.NotFound, "404"u8.ToArray(), null, null, version);
            if (corrupt.Contains((host, kind, key))) { bytes = (byte[])bytes.Clone(); bytes[bytes.Length / 2] ^= 0x40; }
            int? cut = null;
            if (offset is null && breaks.Remove((kind, key), out var after)) cut = after;
            if (offset is null || mode == FakeHostMode.IgnoresRanges) return new(HttpStatusCode.OK, bytes, null, null, version, cut);
            if (offset.Value + length!.Value > bytes.Length) return new(HttpStatusCode.RequestedRangeNotSatisfiable, [], null, bytes.Length, version);
            return new(HttpStatusCode.PartialContent, bytes.AsSpan((int)offset.Value, (int)length.Value).ToArray(), offset, bytes.Length, version);
        }
    }

    /// <summary>An HTTP front end for the real <see cref="CdnClient"/>: parses the official URL shape back into kind and key.</summary>
    public HttpMessageHandler Handler(FetchLabel label = default) => new FakeCdnHandler(this, label);
}
