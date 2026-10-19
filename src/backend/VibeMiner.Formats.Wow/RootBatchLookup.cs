using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

public sealed record RootBatchTarget(uint FileDataId, IReadOnlyList<RootTargetMatch> Memberships);

/// <summary>Complete grammar/EOF facts and all observed memberships for the requested IDs.
/// Completion does not authenticate source bytes or bind a build, locale or selection policy.</summary>
public sealed record RootBatchLookupResult(RootCompletion Completion, IReadOnlyList<RootBatchTarget> Targets);

/// <summary>One complete native ROOT pass for a bounded batch. Preserves every requested variant;
/// no locale/platform selection, fetching, persistence or source discovery occurs. A target with
/// no memberships means observed absence only after this method successfully completes over a
/// separately authenticated complete source. Input remains caller-owned and need not seek.</summary>
public static class RootBatchLookup
{
    public const int MaxTargets = 128;
    public const int MaxMatchesPerTarget = 4096;
    public const int MaxMatches = 32_768;

    public static async Task<RootBatchLookupResult> ReadAsync(Stream decoded,
        IReadOnlyCollection<uint> requested, RootLimits limits,
        Func<long, long, CancellationToken, ValueTask>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(limits);
        token.ThrowIfCancellationRequested();
        if (requested.Count > MaxTargets)
            throw Error("ROOT_LIMIT", "Too many requested FileDataIDs for one bounded batch.");
        var targets = new Dictionary<uint, List<RootTargetMatch>>(requested.Count);
        var supplied = 0;
        foreach (var id in requested)
        {
            token.ThrowIfCancellationRequested();
            if (++supplied > MaxTargets)
                throw Error("ROOT_LIMIT", "Too many requested FileDataIDs for one bounded batch.");
            if (id == 0)
                throw Error("ROOT_INVALID", "A batch target FileDataID must be nonzero.");
            targets.TryAdd(id, []);
        }
        var orderedIds = targets.Keys.Order().ToArray();
        var consumer = new BatchConsumer(targets, progress);
        var completion = await RootInventoryReader.ReadAsync(decoded, limits, consumer, token);
        token.ThrowIfCancellationRequested();
        await consumer.CompletedAsync(token);
        token.ThrowIfCancellationRequested();
        var result = new RootBatchTarget[targets.Count];
        var offset = 0;
        foreach (var id in orderedIds)
        {
            token.ThrowIfCancellationRequested();
            result[offset++] = new(id, targets[id].AsReadOnly());
        }
        token.ThrowIfCancellationRequested();
        return new(completion, Array.AsReadOnly(result));
    }

    private sealed class BatchConsumer(Dictionary<uint, List<RootTargetMatch>> targets,
        Func<long, long, CancellationToken, ValueTask>? progress) : IRootConsumer
    {
        private long total, completed;
        private int matches;

        public async ValueTask HeaderAsync(RootHeader header, CancellationToken token)
        {
            total = header.MembershipCount;
            if (progress is not null) await progress(0, total, token);
        }

        public async ValueTask EntryAsync(long block, long ordinal, RootEntry entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (targets.TryGetValue(entry.FileDataId, out var selected))
            {
                if (selected.Count == MaxMatchesPerTarget || matches == MaxMatches)
                    throw Error("ROOT_LIMIT", "Requested FileDataIDs exceed the batch membership bound.");
                selected.Add(new(block, ordinal, entry));
                matches++;
            }
            completed++;
            if (progress is not null && completed % 4096 == 0 && completed < total)
                await progress(completed, total, token);
        }

        public ValueTask CompletedAsync(CancellationToken token) =>
            progress is null || total == 0 ? ValueTask.CompletedTask : progress(total, total, token);
    }

    private static DiscoveryException Error(string code, string message) => new(code, message);
}
