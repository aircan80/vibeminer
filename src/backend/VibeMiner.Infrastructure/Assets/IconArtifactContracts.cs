namespace VibeMiner.Infrastructure;

// Build/locale/FileDataID associations and original source receipts stay with their catalogue owner.
public sealed record IconSourceBinding(string ContentKey, string EncodingKey, string EncodedSha256,
    long EncodedBytes, long DecodedBytes, string VerifierRevision);
public sealed record IconPngProof(string RequestKey, IconSourceBinding Source, string DecoderRevision,
    string EncoderRevision, string PixelPolicy, int MipLevel, int Width, int Height, int BaseWidth,
    int BaseHeight, string SourceEncoding, int AlphaDepth, int AlphaEncoding, int MipFlags, string RgbaSha256);
public sealed record IconPngArtifact(string RequestKey, string ArtifactId, string Sha256, long ByteLength, IconPngProof Proof);
