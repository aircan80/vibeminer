using static VibeMiner.Formats.Wow.GeometryArtifactEncoding;

namespace VibeMiner.Formats.Wow;

public static partial class ModelGeometryArtifacts
{
    /// <summary>Computes the existing normalized data binding before a build. Grants no source authority.</summary>
    public static string ComputeBindingSha256(GeometrySourceBinding binding, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var digest = Convert.ToHexStringLower(BindingHash(binding));
        token.ThrowIfCancellationRequested(); return digest;
    }
}
