using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Whether the community build list may be used (#9, D106). Fails closed like D081: unreadable reads as off.</summary>
public interface ICommunityBuildPolicy
{
    Task<bool> CommunityBuildHistoryEnabledAsync(CancellationToken token);
}

/// <summary>One fetch of the community build list: the usable rows, plus what the bytes were.</summary>
public sealed record CommunityBuildFetch(IReadOnlyList<CommunityBuildRow> Rows, string Url, string Sha256, long ByteLength, int Skipped);

/// <summary>Fetches and parses the community build list. Failures are typed COMMUNITY_BUILDS_* DiscoveryExceptions.</summary>
public interface ICommunityBuildFetcher
{
    Task<CommunityBuildFetch> FetchAsync(CancellationToken token);
}

/// <summary>The saved state of the community build list: latest attempt, latest import and its problem, if any.</summary>
public sealed record CommunityBuildsState(string? LastAttemptAt, string? FetchedAt, int BuildCount, SettingsProblem? Problem);

public interface ICommunityBuildStore
{
    Task<CommunityBuildsState> ReadCommunityBuildsStateAsync(CancellationToken token);
    /// <summary>Saved before any request goes out, so a crash or a cancellation still counts against the daily limit.</summary>
    Task RecordCommunityAttemptAsync(string attemptedAt, CancellationToken token);
    Task RecordCommunityFailureAsync(SettingsProblem problem, CancellationToken token);
    /// <summary>Adds new rows and marks listed ones as seen again; rows no longer listed stay. Returns the saved count.</summary>
    Task<int> ImportCommunityBuildsAsync(CommunityBuildFetch fetch, string fetchedAt, CancellationToken token);
}

/// <summary>
/// The community build list (#9, D106): at most one request a day, only while the switch reads as on, and only from a
/// Refresh builds. Attempts are single-flight; a second caller waits for the running one.
/// </summary>
public sealed class CommunityBuildsService(ICommunityBuildFetcher fetcher, ICommunityBuildStore store, ICommunityBuildPolicy policy,
    TimeProvider? clock = null, TimeSpan? interval = null)
{
    /// <summary>Owner decision on #9: wago.tools at most once a day.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromDays(1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan every = interval ?? DefaultInterval;
    private readonly SemaphoreSlim single = new(1, 1);

    public Task<bool> EnabledAsync(CancellationToken token) => policy.CommunityBuildHistoryEnabledAsync(token);

    /// <summary>
    /// Asks the community source when the switch is on and the last attempt is at least a day old. A failure is saved as
    /// the list's problem and never thrown: product checks don't depend on it. Cancellation stops the request; the
    /// attempt still counts.
    /// </summary>
    public async Task RefreshIfDueAsync(CancellationToken token)
    {
        if (!await policy.CommunityBuildHistoryEnabledAsync(token)) return;
        await single.WaitAsync(token);
        try
        {
            var state = await store.ReadCommunityBuildsStateAsync(token);
            var now = time.GetUtcNow();
            if (Instant(state.LastAttemptAt) is { } last && now - last < every && now >= last) return;
            await store.RecordCommunityAttemptAsync(now.ToString("O", CultureInfo.InvariantCulture), token);
            try
            {
                var fetch = await fetcher.FetchAsync(token);
                await store.ImportCommunityBuildsAsync(fetch, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), CancellationToken.None);
            }
            catch (DiscoveryException error) when (error.Code.StartsWith("COMMUNITY_BUILDS_", StringComparison.Ordinal))
            {
                await store.RecordCommunityFailureAsync(new(error.Code, error.Message, error.Retryable), CancellationToken.None);
            }
            // Never let an unexpected fault from the list end Refresh builds: it is the list's problem, saved as such.
            catch (Exception error) when (error is not (DiscoveryException or OperationCanceledException or OutOfMemoryException))
            {
                await store.RecordCommunityFailureAsync(new("COMMUNITY_BUILDS_INVALID",
                    "The wago.tools build list couldn’t be read safely, so it wasn’t used.", false), CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await store.RecordCommunityFailureAsync(new("COMMUNITY_BUILDS_STOPPED",
                    "Refresh builds was stopped before the wago.tools build list arrived.", true), CancellationToken.None);
                throw;
            }
        }
        finally { single.Release(); }
    }

    /// <summary>The status a page carries, from the saved state and the switch as read for that page.</summary>
    public static CommunityBuildsStatus Status(bool enabled, CommunityBuildsState state, TimeSpan? interval = null)
    {
        if (!enabled) return CommunityBuildsStatus.Off with { LastAttemptAt = state.LastAttemptAt, FetchedAt = state.FetchedAt, BuildCount = state.BuildCount };
        var next = Instant(state.LastAttemptAt) is { } last ? (last + (interval ?? DefaultInterval)).ToString("O", CultureInfo.InvariantCulture) : null;
        var kind = state.Problem is not null ? "failed" : state.FetchedAt is null ? "notFetched" : "ready";
        return new(true, kind, CommunityBuildsStatus.SourceName, state.LastAttemptAt, state.FetchedAt, next, state.BuildCount, state.Problem);
    }

    private static DateTimeOffset? Instant(string? text) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
}
