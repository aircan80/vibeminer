namespace VibeMiner.Formats;

/// <summary>A DBD definition or DB2 file the readers refuse. <see cref="Code"/> is the stable error code callers
/// branch on (DBD_* and DB2_*), never the message. Replaces the legacy reader's DiscoveryException for layer 5.</summary>
public sealed class Db2FormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
