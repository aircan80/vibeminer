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
