namespace VibeMiner.Domain;

public enum AcquisitionPriority { Background, Interactive }

// Physical byte identity and assurance only; FileDataID/CKey/locale interpretation is separate.
public sealed record EncodedRequirement(string EncodingKey, long EncodedBytes,
    string VerifierRevision = "blte-encoded-v1");
public sealed record EncodedSourceContext(string ProductId, string BuildIdentity, string SourceRegion,
    string CdnConfigurationSha256, OpeningSourceReference? SourceReference = null);
/// <summary>
/// CdnConfig is the raw CDN configuration key whose archive list authorized an archive Location: the source
/// plan's own one, or an earlier CDN configuration of the same build when only that one lists the archive.
/// It is null for a loose object and for receipts recorded before it existed.
/// </summary>
public sealed record EncodedReceipt(string EncodingKey, string Sha256, long EncodedBytes,
    string SourceUrl, string RetrievedAt, string VerifiedAt, string VerifierRevision, ContentLocation Location,
    string? CdnConfig = null);
public sealed record AcquisitionProgress(string Stage, string Unit, long? Completed = null, long? Total = null);
