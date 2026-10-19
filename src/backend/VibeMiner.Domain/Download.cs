namespace VibeMiner.Domain;

public sealed record DownloadSummary(string LedgerId, string Scope, string ObjectCount,
    string TotalEncodedBytes, string VerifiedEncodedBytes, string RemainingEncodedBytes,
    string AssessedAt, string MetadataEncodedBytes);
public sealed record DownloadProblem(string Code, string Message);
public sealed record DownloadObjectEntry(string Id, string EncodingKey, string EncodedBytes, bool VerifiedAtAssessment);
public sealed record DownloadPage(string ProductId, string BuildIdentity, string SourceRegion,
    string LedgerId, string AssessedAt, IReadOnlyList<DownloadObjectEntry> Entries, string TotalCount, string? NextCursor);
