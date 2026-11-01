namespace VibeMiner.Formats.Images;

/// <summary>A BLP file the decoder refuses. <see cref="Code"/> is the stable error code callers branch on (BLP_INVALID,
/// BLP_UNSUPPORTED, BLP_LIMIT, BLP_MIP_UNAVAILABLE), never the message. Replaces the legacy decoder's DiscoveryException for layer 5.</summary>
public sealed class BlpFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
