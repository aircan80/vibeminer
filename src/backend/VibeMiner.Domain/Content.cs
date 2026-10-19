namespace VibeMiner.Domain;

// One verified encoded representation. Build/locale/FileDataID membership is a separate association.
public sealed record ContentLocation(string Kind, string Offset, string SourceBytes, string? ArchiveKey);
public sealed record ContentReceipt(string ContentKey, string EncodingKey, string Sha256,
    string EncodedBytes, string DecodedBytes, string SourceUrl, string RetrievedAt, string VerifiedAt,
    string VerifierRevision, ContentLocation Location, ContentProjection? Projection = null);
public sealed record SelectedRootMembership(string BlockOrdinal, string EntryOrdinal, string ContentKey,
    string LocaleFlags, string ContentFlags, string RawFirst, string RawSecond, string RawThird, string? NameHash);
public sealed record SelectedFileAssociation(string FileDataId, string ResearchLocale, string VariantPolicy,
    MetadataReceipt EncodingSource, MetadataReceipt RootSource, IReadOnlyList<SelectedRootMembership> Memberships);

// Readable chunks have actual decoded hashes; unavailable intervals have only authenticated declared geometry.
public sealed record ContentReadableRange(string Offset, string ByteLength, string Sha256);
public sealed record ContentUnavailableRange(string Offset, string ByteLength, int ChunkOrdinal, string KeyName);
public sealed record ContentProjection(string Policy, string IdentitySha256,
    IReadOnlyList<ContentReadableRange> ReadableRanges, IReadOnlyList<ContentUnavailableRange> UnavailableRanges);
