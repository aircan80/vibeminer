using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

public sealed record RootTargetMatch(long BlockOrdinal, long EntryOrdinal, RootEntry Entry);

/// <summary>
/// Scans complete decoded ROOT grammar/EOF while retaining only requested IDs and all their
/// published variants. Source verification and exact build binding remain the caller's job.
/// </summary>
public static class RootTargetLookup
{
    public const int MaxTargets = 1024;
    public const int MaxMatches = 4096;

    public static Task<IReadOnlyList<RootTargetMatch>> ReadAsync(Stream decoded,
        IReadOnlyCollection<uint> requested, RootLimits limits, CancellationToken cancellationToken = default) =>
        ReadAsync(decoded, requested, limits, null, cancellationToken);

    public static async Task<IReadOnlyList<RootTargetMatch>> ReadAsync(Stream decoded,
        IReadOnlyCollection<uint> requested, RootLimits limits,
        Func<long, long, CancellationToken, ValueTask>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (requested.Count > MaxTargets)
            throw new DiscoveryException("ROOT_LIMIT", "Too many requested FileDataIDs for one target scan.");
        var consumer = new TargetConsumer(requested.ToHashSet(), progress);
        await RootInventoryReader.ReadAsync(decoded, limits, consumer, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await consumer.CompletedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return consumer.Matches.AsReadOnly();
    }

    private sealed class TargetConsumer(HashSet<uint> requested,
        Func<long, long, CancellationToken, ValueTask>? progress) : IRootConsumer
    {
        public readonly List<RootTargetMatch> Matches = [];
        private long total, completed;

        public async ValueTask HeaderAsync(RootHeader header, CancellationToken token)
        {
            total = header.MembershipCount;
            if (progress is not null) await progress(0, total, token);
        }

        public async ValueTask EntryAsync(long block, long ordinal, RootEntry entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (requested.Contains(entry.FileDataId))
            {
                if (Matches.Count == MaxMatches)
                    throw new DiscoveryException("ROOT_LIMIT", "Requested FileDataIDs exceed the target variant limit.");
                Matches.Add(new(block, ordinal, entry));
            }
            completed++;
            if (progress is not null && completed % 4096 == 0 && completed < total)
                await progress(completed, total, token);
        }

        // Terminal progress is emitted only after the outer reader has validated EOF.
        public ValueTask CompletedAsync(CancellationToken token) =>
            progress is null || total == 0 ? ValueTask.CompletedTask : progress(total, total, token);
    }
}
