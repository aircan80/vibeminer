namespace VibeMiner.Domain;

public sealed record DownloadSummary(string LedgerId, string Scope, string ObjectCount,
    string TotalEncodedBytes, string VerifiedEncodedBytes, string RemainingEncodedBytes,
    string AssessedAt, string MetadataEncodedBytes);
public sealed record DownloadProblem(string Code, string Message);
/// <summary>
/// One ledger member. <c>CheckedAtOpen</c> is false for a file ROOT lists outside the admitted manifest (D066, D093):
/// Open's assessment never covered it, so it is never verified at assessment. <c>Check</c> is the latest completed
/// Check of those files (<c>verified</c>, <c>notStored</c>, <c>missing</c> or <c>corrupt</c>), or null before one.
/// </summary>
public sealed record DownloadObjectEntry(string Id, string EncodingKey, string EncodedBytes, bool VerifiedAtAssessment,
    bool CheckedAtOpen = true, string? Check = null);
public sealed record DownloadPage(string ProductId, string BuildIdentity, string SourceRegion,
    string LedgerId, string AssessedAt, IReadOnlyList<DownloadObjectEntry> Entries, string TotalCount, string? NextCursor,
    string ManifestLedgerId, DownloadCheckSummary? LastCheck);
/// <summary>The counts of the latest completed Check, kept with it so a page can say what it found (D093).</summary>
public sealed record DownloadCheckSummary(string CheckedAt, string CheckedObjects, string VerifiedObjects, string NotStoredObjects,
    string MissingObjects, string CorruptObjects);
/// <summary>One completed Check of the files a measured ledger adds to its admitted manifest (D093).</summary>
public sealed record DownloadCheckResult(string ProductId, string BuildIdentity, string SourceRegion, string LedgerId,
    string ManifestLedgerId, string CheckedAt, string CheckedObjects, string VerifiedObjects, string NotStoredObjects,
    string MissingObjects, string CorruptObjects);
public sealed record DownloadCheckProgress(long Completed, long Total);
