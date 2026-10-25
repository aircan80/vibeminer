using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

/// <summary>Original bounded source-geometry capability. No material records, pose, visibility or appearance admission.</summary>
public static class M2GeometrySkinReader
{
    public const string Revision = "m2-skin-geometry-v1";
    public static async Task<M2GeometrySkin> ReadAsync(Stream source, M2Model model, M2Limits? limits = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(model); token.ThrowIfCancellationRequested();
        if (ProfileRefusal(model) is { } refusal) throw Error("UNSUPPORTED", refusal);
        limits ??= model.Limits;
        var core = await M2SkinGeometryCore.ReadAsync(source, model, limits, includeMaterialBuffers: false, token);
        await core.Input.FinishAsync();
        return new(model, core);
    }

    /// <summary>Null when the parsed model fits the geometry-only profile (chunked 272 or 274, no external skeleton
    /// or bone files); otherwise a message naming the first feature outside it.</summary>
    public static string? ProfileRefusal(M2Model model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!model.IsChunked || model.Version is not (272 or 274))
            return $"Geometry needs a chunked MD21 model of version 272 or 274; this one is {(model.IsChunked ? "chunked" : "unchunked")} version {model.Version}.";
        if (model.SkeletonFileDataId != 0)
            return $"The model keeps its bones in an external skeleton file (SKID {model.SkeletonFileDataId}), which geometry does not read.";
        if (model.BoneFileDataIds.Any(id => id != 0))
            return "The model declares external bone files (BFID), which geometry does not read.";
        return null;
    }
}
