namespace VibeMiner.Domain;

public sealed record OpeningSourceReference(string Generation, string Region, string VersionsSha256,
    int VersionsRowOrdinal, string CdnsSha256, int CdnsRowOrdinal);
public sealed record ConfigurationReceipt(string Role, string Key, string Sha256, int ByteLength,
    string SourceUrl, string RetrievedAt, string VerifiedAt, int ParsedFieldCount);
public sealed record WorkspaceSnapshot(Build Build, string OpenedAt, string PreparationProfile,
    string Readiness, string MetadataState, string InventoryState, string RetentionState,
    string DownloadPolicy, string? ResearchLocale, string? PlatformScope,
    OpeningSourceReference SourceReference, IReadOnlyList<ConfigurationReceipt> Configurations,
    InventorySummary? Inventory = null, IReadOnlyList<MetadataReceipt>? Metadata = null,
    DownloadSummary? Download = null, DownloadProblem? DownloadProblem = null);
