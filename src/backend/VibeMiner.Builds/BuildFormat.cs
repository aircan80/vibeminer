namespace VibeMiner.Builds;

/// <summary>Why a build's metadata can't be read. Each stays its own state: never folded into another.</summary>
public enum BuildFailure
{
    /// <summary>The bytes are wrong: bad magic, a failed hash, truncation, trailing bytes, broken ordering or limits.</summary>
    Corrupt,
    /// <summary>The bytes are encrypted (a BLTE <c>E</c> chunk) and can't be read without a key layer 3 doesn't hold.</summary>
    Encrypted,
    /// <summary>Well formed, but a version, mode or layout this engine doesn't read (for example a newer ROOT version).</summary>
    Unsupported,
}

/// <summary>
/// A layer 3 format parser refused its input. <see cref="Code"/> is stable (for example <c>BLTE_ENCRYPTED</c>,
/// <c>ENCODING_INVALID</c>, <c>ROOT_UNSUPPORTED</c>); the message says where, never what game content was in the bytes.
/// </summary>
public sealed class BuildFormatException(BuildFailure failure, string code, string message) : Exception(message)
{
    public BuildFailure Failure { get; } = failure;
    public string Code { get; } = code;
}
