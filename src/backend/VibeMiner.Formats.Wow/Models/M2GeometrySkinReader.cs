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
        if (!model.IsChunked || model.Version != 274 || model.SkeletonFileDataId != 0 || model.BoneFileDataIds.Any(id => id != 0))
            throw Error("UNSUPPORTED", "Geometry-only SKIN admission requires a chunked274 model without external skeleton/bone dependencies.");
        limits ??= model.Limits;
        var core = await M2SkinGeometryCore.ReadAsync(source, model, limits, includeMaterialBuffers: false, token);
        await core.Input.FinishAsync();
        return new(model, core);
    }
}
