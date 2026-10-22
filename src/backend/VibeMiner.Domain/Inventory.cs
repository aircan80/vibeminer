namespace VibeMiner.Domain;

public sealed record InventorySummary(string Generation, string PreparedAt, string Scope,
    string MembershipCount, string RootMembershipCount, string UniqueContentCount,
    string DownloadObjectCount, string LogicalDecodedBytes, string TotalEncodedBytes,
    string VerifiedEncodedBytes, string RemainingEncodedBytes, string SharedVerifiedEncodedBytes,
    string MetadataEncodedBytes);
public sealed record MetadataReceipt(string Role, string ContentKey, string EncodingKey,
    string Sha256, string EncodedBytes, string DecodedBytes, string SourceUrl,
    string RetrievedAt, string VerifiedAt, string ParserRevision, ContentLocation? Location = null);
public sealed record InventoryEntry(string Id, string Family, string? Name, string? FileDataId,
    string? ContentKey, string EncodingKey, string? DecodedBytes, string EncodedBytes,
    bool Retained, string? LocaleFlags, string? ContentFlags, string? NameHash);
public sealed record InventoryPage(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, IReadOnlyList<InventoryEntry> Entries, string TotalCount, string? NextCursor);

/// <summary>One Files row: every ROOT membership of one FileDataID, or every install membership with one exact name.</summary>
public sealed record InventoryFile(string Key, string Family, string? FileDataId, string? Name,
    int MembershipCount, int MatchingCount, int ContentKeyCount, string? EncodedBytesMin, string? EncodedBytesMax,
    int UnresolvedCount, string? LocaleFlags, int StoredCount);
public sealed record InventoryFilesPage(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, IReadOnlyList<InventoryFile> Entries, string? TotalCount, string? NextCursor, bool ScanLimited);
public sealed record InventoryRawContentFlags(string First, string Second, int Third);
public sealed record InventoryFileMembership(string Id, string Family, int? BlockOrdinal, int? EntryOrdinal,
    int? InstallOrdinal, string? LocaleFlags, string? ContentFlags, InventoryRawContentFlags? RawContentFlags,
    string? NameHash, string ContentKey, string? EncodingKey, string? DecodedBytes, string? EncodedBytes,
    bool Stored, bool Matches);
public sealed record InventoryFileDetail(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, string File, string Family, string? FileDataId, string? Name, int MembershipCount,
    int MatchingCount, IReadOnlyList<InventoryFileMembership> Memberships, string? NextCursor);

/// <summary>The sealed ROOT/install file catalog of one build (D071): file and membership counts, all exact.</summary>
public sealed record FileCatalogSummary(string Generation, string PreparedAt, string RootFileCount, string InstallFileCount,
    string RootMembershipCount, string InstallMembershipCount, string UniqueContentCount, string UnresolvedMembershipCount,
    string StoredMembershipCount);
/// <summary>A problem with its typed code and message, and the underlying storage or I/O cause when there is one.</summary>
public sealed record FileCatalogProblem(string Code, string Message, bool Retryable, string? Detail = null);
/// <summary>A build named by its exact identity and visible version.</summary>
public sealed record FileCatalogBuild(string ProductId, string BuildIdentity, string BuildVersion);
/// <summary>
/// Where one opened build's file catalog stands (D071). State is notOpened, pending, stopped, ready, failed or unsupported.
/// Stage (pending only) is queued, paused (with the build's Download all), acquiring, waiting, reading, writing or sealing;
/// Completed and Total count written memberships while writing. WaitingFor names the build a queued preparation waits for,
/// or the build whose ROOT scope measurement a waiting one waits for. Attempts counts failed attempts; a pending one with a
/// problem is waiting to retry.
/// </summary>
public sealed record FileCatalogStatus(string ProductId, string BuildIdentity, string SourceRegion, string? BuildVersion, string State, string? Stage,
    string? Completed, string? Total, int Attempts, FileCatalogSummary? Catalog, FileCatalogProblem? Problem, FileCatalogBuild? WaitingFor);
/// <summary>The preparations Downloads & activity shows, at most 32.</summary>
public sealed record FileCatalogList(IReadOnlyList<FileCatalogStatus> Catalogs);
