using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;

namespace VibeMiner.Builds.Tests;

/// <summary>
/// Wraps the in-memory source for build tests. <see cref="Offline"/> turns anything not stored into NeedsNetwork, like
/// <c>LocalObjectSource</c>. Counts calls, can answer a key with a scripted result, and can withhold whole-object bytes so
/// the reader has to use ranges.
/// </summary>
internal sealed class TestObjectSource(InMemoryObjectSource inner) : IObjectSource
{
    private readonly Lock gate = new();
    private readonly List<string> calls = [];
    private readonly List<CdnContext?> contexts = [];

    public InMemoryObjectSource Inner { get; } = inner;
    public bool Offline { get; set; }
    public Func<ObjectKey, ObjectResult?>? Override { get; set; }
    public HashSet<ObjectKey> WithoutBytes { get; } = [];
    /// <summary>Makes the history log, and GetKeyAsync for these keys, throw.</summary>
    public bool HistoryThrows { get; set; }
    public HashSet<ObjectKey> Throwing { get; } = [];

    /// <summary>Every call, as "Method" or "Method:keyType".</summary>
    public IReadOnlyList<string> Calls { get { lock (gate) return calls.ToArray(); } }
    public int ObjectCalls => Calls.Count(c => !c.StartsWith(nameof(ReadHistoryAsync), StringComparison.Ordinal));
    public IReadOnlyList<CdnContext?> Contexts { get { lock (gate) return contexts.ToArray(); } }
    public int Count(string prefix) => Calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    public void Reset() { lock (gate) { calls.Clear(); contexts.Clear(); } }

    private void Log(string call, CdnContext? cdn = null) { lock (gate) { calls.Add(call); contexts.Add(cdn); } }

    private async Task<ObjectResult?> LocalAsync(ObjectKey key, CancellationToken ct)
    {
        if (Override?.Invoke(key) is { } scripted) return scripted;
        if (Offline && await Inner.CountPresentAsync([key], ct) == 0)
            return new(key, ObjectState.NeedsNetwork, Code: "OBJECT_ACCESS_NEEDS_NETWORK", Message: "Not stored, and this source is local only.");
        return null;
    }

    public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken)
    {
        Log($"{nameof(GetKeyAsync)}:{request.Key.Type}", cdn);
        Assert(priority);
        if (Throwing.Contains(request.Key)) throw new InvalidOperationException("scripted failure");
        if (await LocalAsync(request.Key, cancellationToken) is { } local) return local;
        var got = await Inner.GetKeyAsync(request, cdn, priority, cancellationToken);
        return WithoutBytes.Contains(request.Key) ? got with { Bytes = null } : got;
    }

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
        IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
    {
        Log($"{nameof(GetKeysAsync)}:{requests[0].Key.Type}", cdn);
        Assert(priority);
        var results = new ObjectResult[requests.Count];
        for (var i = 0; i < requests.Count; i++)
            results[i] = await LocalAsync(requests[i].Key, cancellationToken) ?? (await Inner.GetKeysAsync([requests[i]], cdn, priority, progress, cancellationToken))[0];
        return results;
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
        CancellationToken cancellationToken)
    {
        Log($"{nameof(GetRangeAsync)}:{request.Key.Type}", cdn);
        Assert(priority);
        return await LocalAsync(request.Key, cancellationToken) ?? await Inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);
    }

    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
    { Log(nameof(ObserveAsync)); return Inner.ObserveAsync(request, cancellationToken); }

    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    { Log(nameof(CountPresentAsync)); return Inner.CountPresentAsync(keys, cancellationToken); }

    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    { Log(nameof(HasKeysAsync)); return Inner.HasKeysAsync(keys, cancellationToken); }

    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        Log(nameof(ReadHistoryAsync));
        return HistoryThrows ? throw new InvalidOperationException("scripted failure") : Inner.ReadHistoryAsync(query, cancellationToken);
    }

    private static void Assert(FetchPriority priority)
    {
        if (priority != FetchPriority.Interactive) throw new InvalidOperationException("Opening a build asks at interactive priority.");
    }
}
