using System.Net;
using VibeMiner.Cdn;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// Orders a route's hosts per request. Background work tries hosts that have answered over HTTP/2 first (one connection
/// carries many streams); interactive work keeps the advertised order. Either way a host that just failed (offline,
/// timeout, 5xx, broken connection, a 200 to a range) goes last until its cooldown passes. 404s don't count: a host
/// can lack one key and have the rest.
/// </summary>
public sealed class HostRanking(TimeSpan cooldown, TimeProvider time)
{
    private readonly Lock gate = new();
    private readonly HashSet<string> http2 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> failedAt = new(StringComparer.Ordinal);

    public CdnRoute Order(CdnRoute route, FetchPriority priority)
    {
        string[] ordered;
        lock (gate)
        {
            var now = time.GetUtcNow();
            ordered = route.Hosts
                .Select((host, index) => (host, index,
                    cooling: failedAt.TryGetValue(host, out var at) && now - at < cooldown,
                    h2: priority == FetchPriority.Background && http2.Contains(host)))
                .OrderBy(h => h.cooling).ThenByDescending(h => h.h2).ThenBy(h => h.index)
                .Select(h => h.host).ToArray();
        }
        return ordered.SequenceEqual(route.Hosts) ? route : route.WithHosts(ordered);
    }

    public void Observe(CdnReply reply)
    {
        lock (gate)
        {
            foreach (var attempt in reply.Failed) Failed(attempt);
            if (reply.HttpVersion >= HttpVersion.Version20) http2.Add(reply.Host);
            failedAt.Remove(reply.Host);
        }
    }

    public void Observe(CdnException failure) { lock (gate) foreach (var attempt in failure.Attempts) Failed(attempt); }

    private void Failed(CdnAttempt attempt)
    {
        if (attempt.Failure is CdnFailure.Offline or CdnFailure.Timeout or CdnFailure.Network or CdnFailure.HttpStatus or CdnFailure.RangeIgnored)
            failedAt[attempt.Host] = time.GetUtcNow();
    }
}
