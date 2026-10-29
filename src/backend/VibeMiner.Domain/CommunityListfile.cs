namespace VibeMiner.Domain;

/// <summary>
/// One verified wow-listfile release kept in the data folder (D105). Tag and PublishedAt are GitHub's release facts;
/// FetchedAt is when VibeMiner finished downloading and verifying it. Sha256 and ByteLength are the downloaded CSV's,
/// as GitHub published them; NameCount is the number of names read from it.
/// </summary>
public sealed record CommunityListfileRelease(string Tag, DateTimeOffset PublishedAt, DateTimeOffset FetchedAt,
    string Sha256, long ByteLength, long NameCount, string SourceUrl);

/// <summary>What the listfile download is doing now.</summary>
public enum CommunityListfileActivity { Idle, Verifying, Checking, Downloading, Importing }

/// <summary>
/// The community listfile as Settings and Files see it. Release is the copy names come from (null when there is none);
/// it is kept, but not used, while the switch is off. Completed/Total count bytes while downloading or importing.
/// Problem is the last attempt's failure; a usable release stays in use after a failed refresh.
/// </summary>
public sealed record CommunityListfileStatus(bool Enabled, CommunityListfileActivity Activity, CommunityListfileRelease? Release,
    string? PendingTag, long? Completed, long? Total, DateTimeOffset? CheckedAt, SettingsProblem? Problem)
{
    /// <summary>False until something asked for names in this engine run and nothing was kept from earlier: then
    /// nothing has been fetched yet.</summary>
    public bool Started { get; init; }
}

/// <summary>The source of the community names on one Files page or detail: "off", "notReady" (on, no usable copy
/// yet, a download on its way), "failed" (on, no usable copy, the last attempt failed and nothing runs now) or
/// "unreadable" (on, a copy is kept but couldn't be read for this page) or "ready", with the release only when ready.</summary>
public sealed record CommunityNamesSource(string State, string? Tag, DateTimeOffset? PublishedAt, DateTimeOffset? FetchedAt)
{
    public static CommunityNamesSource Off { get; } = new("off", null, null, null);
    public static CommunityNamesSource NotReady { get; } = new("notReady", null, null, null);
    public static CommunityNamesSource Failed { get; } = new("failed", null, null, null);
    public static CommunityNamesSource Unreadable { get; } = new("unreadable", null, null, null);
    public static CommunityNamesSource From(CommunityListfileRelease release) => new("ready", release.Tag, release.PublishedAt, release.FetchedAt);
}
