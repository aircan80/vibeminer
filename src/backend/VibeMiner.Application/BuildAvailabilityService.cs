using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// What an availability check tests: the build's exact configs, and the official CDN bases to ask, at most two hosts,
/// taken from a saved Blizzard cdns document. A community build is tested with its own configs from the list.
/// </summary>
public sealed record AvailabilityTarget(string ProductId, string BuildId, string BuildConfig, string CdnConfig, IReadOnlyList<Uri> Routes);

/// <summary>Counted progress of one stage: configs, archive indexes or archives.</summary>
public sealed record AvailabilityProgress(string Stage, int Completed, int Total);

public interface IBuildAvailabilityStore
{
    /// <summary>The saved build's configs and CDN bases. BUILD_NOT_FOUND for an unknown build; AVAILABILITY_NO_ROUTE without a cdns document.</summary>
    Task<AvailabilityTarget> ResolveAvailabilityTargetAsync(string productId, string buildId, CancellationToken token);
    /// <summary>Replaces the build's saved check with this one.</summary>
    Task RecordAvailabilityAsync(AvailabilityTarget target, BuildAvailability result, CancellationToken token);
}

public interface IBuildAvailabilityProbe
{
    /// <summary>Tests the target in stages and stops at the first stage that decides the outcome. Never downloads game content.</summary>
    Task<BuildAvailability> CheckAsync(AvailabilityTarget target, bool archives, Func<AvailabilityProgress, Task> progress, CancellationToken token);
}

/// <summary>
/// Check availability (#9, D106): one check at a time. A check that finishes is saved with the time it ran and replaces
/// the build's earlier one; a cancelled or failed check saves nothing, so the earlier one stays.
/// </summary>
public sealed class BuildAvailabilityService(IBuildAvailabilityStore store, IBuildAvailabilityProbe probe)
{
    private readonly SemaphoreSlim single = new(1, 1);

    public async Task<BuildAvailability> CheckAsync(string productId, string buildId, bool archives,
        Func<AvailabilityProgress, Task> progress, CancellationToken token)
    {
        ProductRegistry.Validate(productId);
        if (buildId is not { Length: 64 } || buildId.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DiscoveryException("INVALID_REQUEST", "Choose one exact saved build.");
        if (!await single.WaitAsync(0, token))
            throw new DiscoveryException("AVAILABILITY_BUSY", "Another availability check is running. Wait for it or stop it first.", true);
        try
        {
            var target = await store.ResolveAvailabilityTargetAsync(productId, buildId, token);
            var result = await probe.CheckAsync(target, archives, progress, token);
            // The check finished; a cancellation arriving now doesn't throw away what it found.
            await store.RecordAvailabilityAsync(target, result, CancellationToken.None);
            return result;
        }
        finally { single.Release(); }
    }
}
