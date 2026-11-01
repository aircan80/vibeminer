namespace VibeMiner.Files.Names;

/// <summary>Why a listfile delta (#186) couldn't be made or replayed.</summary>
internal enum ListfileDeltaProblem
{
    /// <summary>The delta's bytes break its format, or its ops don't fit the base it was made against.</summary>
    Malformed,
    /// <summary>The base fed to the delta isn't the release the delta was made against (its SHA-256 differs).</summary>
    BrokenChain,
    /// <summary>The replayed bytes don't have the delta's target size or SHA-256.</summary>
    WrongTarget,
    /// <summary>Encode was given a base or target whose bytes don't match the SHA-256 or size it was told.</summary>
    InputMismatch,
}

/// <summary>A listfile delta that can't be made or replayed. Nothing written before it was thrown is the target.</summary>
internal sealed class ListfileDeltaException(ListfileDeltaProblem problem, string detail) : Exception(detail)
{
    public ListfileDeltaProblem Problem { get; } = problem;
}
