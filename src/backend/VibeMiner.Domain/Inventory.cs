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
