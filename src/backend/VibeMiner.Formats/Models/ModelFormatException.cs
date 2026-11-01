namespace VibeMiner.Formats.Models;

/// <summary>An M2, SKIN or geometry artifact the readers refuse. <see cref="Code"/> is the stable error code callers branch on
/// (MODEL_* and GEOMETRY_ARTIFACT_*), never the message. Replaces the legacy reader's DiscoveryException for layer 5.</summary>
public sealed class ModelFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
