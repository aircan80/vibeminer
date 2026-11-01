using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>Routes an observation request to the one source that serves its kind.</summary>
internal sealed class Observations
{
    private readonly Dictionary<ObservationKind, IObservationSource> byKind = [];

    public Observations(IReadOnlyList<IObservationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
            foreach (var kind in source.Kinds)
                if (!byKind.TryAdd(kind, source)) throw new ArgumentException($"Two observation sources serve {kind}.", nameof(sources));
    }

    public async Task<(Observation? Observation, ObservationResult? Failed)> FetchAsync(ObservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!byKind.TryGetValue(request.Kind, out var source))
            return (null, new(ObjectState.Refused, Code: "OBSERVATION_SOURCE_MISSING", Message: $"No source serves {request.Kind}."));
        try { return (await source.FetchAsync(request, cancellationToken).ConfigureAwait(false), null); }
        catch (CdnException failure)
        {
            var r = CdnFetcher.FromCdnFailure(default, failure);
            return (null, new(r.State, Code: r.Code, Message: r.Message, ObservedAt: r.ObservedAt));
        }
    }
}

/// <summary>Counts present keys with a few index lookups at once (the store has a pool of read connections).</summary>
internal static class Presence
{
    private const int Lanes = 8;

    public static async Task<long> CountAsync(IObjectReader store, IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return 0;
        var lanes = Math.Min(Lanes, keys.Count);
        var counts = await Task.WhenAll(Enumerable.Range(0, lanes).Select(lane => Task.Run(async () =>
        {
            long present = 0;
            for (var i = lane; i < keys.Count; i += lanes)
                if (await store.HasAsync(keys[i], cancellationToken).ConfigureAwait(false)) present++;
            return present;
        }, cancellationToken))).ConfigureAwait(false);
        return counts.Sum();
    }
}
