namespace VibeMiner.Domain;

/// <summary>One listed build, named by its exact product and identity. Region and ledger are resolved by the engine.</summary>
public sealed record BuildKey(string ProductId, string BuildIdentity);

public enum BuildDownloadState
{
    /// <summary>No Open was ever admitted for this build.</summary>
    NotOpened,
    /// <summary>Opened, but no download manifest size was admitted (absent or unsupported DL, or a legacy profile).</summary>
    NotMeasured,
    /// <summary>A download manifest ledger is admitted and has a running stored figure.</summary>
    Measured
}

public enum StoredCountState
{
    /// <summary>The first pass over content already on disk has not finished; the figure is incomplete.</summary>
    Counting,
    /// <summary>Every verified commit up to the last processed change is counted.</summary>
    Current,
    /// <summary>The ledger could not be read, so the figure is not maintained until the build is opened again.</summary>
    Unavailable
}

/// <summary>The latest dated presence check over this ledger's Download all jobs, or notAssessed.</summary>
public sealed record BuildPresenceCheck(string State, string? AssessedAt, long ObservedObjects,
    long MissingObjects, long CorruptObjects);

/// <summary>
/// Running stored figure for one admitted DL ledger. Stored means a verified receipt for the exact encoded identity and
/// length, with no known corruption and not found missing by a presence check. The same ledger counts once however many
/// source regions opened it.
/// </summary>
public sealed record BuildStoredCoverage(string LedgerId, string Scope, long ObjectCount, long TotalEncodedBytes,
    long StoredObjects, long StoredBytes, StoredCountState CountState, RetentionProblem? CountProblem, string UpdatedAt,
    BuildPresenceCheck Presence, bool DownloadAll, bool Running, IReadOnlyList<string> SourceRegions,
    long UnresolvableRootContent = 0, string ScopeState = "manifestOnly", RetentionProblem? ScopeProblem = null,
    bool ChangesPending = false);

public sealed record BuildDownloadSummary(string ProductId, string BuildIdentity, BuildDownloadState State,
    BuildStoredCoverage? Download);
