using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Whether the community listfile may be used and downloaded (D105).</summary>
public interface ICommunityListfilePolicy
{
    // Never throws for an unreadable settings file: an unreadable value or a newer file reads as off.
    Task<bool> CommunityListfileEnabledAsync(CancellationToken token);
}

/// <summary>Community names for ROOT FileDataIDs. Names are labels only: they never select or verify any data.</summary>
public interface IFileNameSource
{
    /// <summary>At most <see cref="InventoryService.MaxFilesPage"/> FileDataIDs; the names found and where they came
    /// from. Off or not ready returns no names. Local only: never contacts the network.</summary>
    Task<FileNameLookup> LookupAsync(IReadOnlyList<long> fileDataIds, CancellationToken token);
}

/// <summary>The listfile download as Settings shows it, and a deliberate check for a newer release.</summary>
public interface ICommunityListfileControl
{
    Task<CommunityListfileStatus> StatusAsync(CancellationToken token);
    // LISTFILE_OFF while the switch reads as off. Returns at once; the work shows in the status.
    Task<CommunityListfileStatus> CheckAsync(CancellationToken token);
}

public sealed record FileNameLookup(CommunityNamesSource Source, IReadOnlyDictionary<long, string> Names)
{
    public static FileNameLookup Off { get; } = new(CommunityNamesSource.Off, new Dictionary<long, string>());
}
