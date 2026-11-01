namespace VibeMiner.Files.Names;

/// <summary>The community file names switch (D105, <c>communityListfile</c> in settings-v1). It fails closed: a null task,
/// false or a throw reads as off.</summary>
public interface IFileNameSwitch
{
    Task<bool> NamesEnabledAsync(CancellationToken cancellationToken);
}

public enum FileNamesState
{
    /// <summary>The switch is off: ids only.</summary>
    Off,
    /// <summary>On, but no usable copy is stored and no attempt failed while the engine runs.</summary>
    NotReady,
    /// <summary>A usable copy is loaded. A failed newer attempt is in Code and Message; the copy keeps serving.</summary>
    Ready,
    /// <summary>No usable copy, and the last attempt failed (Code says why).</summary>
    Failed,
}

/// <summary>
/// Where the community names stand. Tag, PublishedAt, FetchedAt (the newest part's observation) and Count describe the
/// loaded copy; CheckedAt is the newest release observation. Code, Message and ObservedAt are the last failed attempt,
/// kept in memory only (D113).
/// </summary>
public sealed record FileNamesStatus(FileNamesState State, string? Tag = null, DateTimeOffset? PublishedAt = null,
    DateTimeOffset? FetchedAt = null, DateTimeOffset? CheckedAt = null, int Count = 0, string? Code = null, string? Message = null,
    DateTimeOffset? ObservedAt = null);

/// <summary>The wow-listfile names (D105) over layer 2's history log.</summary>
public interface IFileNames
{
    /// <summary>Where the names stand. Reads stored history only; never fetches.</summary>
    Task<FileNamesStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Asks GitHub for the newest release through layer 2 when a check is due (or <paramref name="force"/>), fetches
    /// the parts it doesn't hold yet, verifies them and swaps the new copy in.</summary>
    Task<FileNamesStatus> UpdateAsync(bool force, CancellationToken cancellationToken);
}
