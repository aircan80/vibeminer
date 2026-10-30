using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

/// <summary>Original SKIN reader for the explicitly paired static profiles: unchunked 264 and chunked 272/274.</summary>
public static class M2SkinReader
{
    public const string Revision = "m2-skin-native-v2";
    public static async Task<M2Skin> ReadAsync(Stream source, M2Model model, M2Limits? limits = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); limits ??= model.Limits;
        var core = await M2SkinGeometryCore.ReadAsync(source, model, limits, includeMaterialBuffers: true, token);
        var input = core.Input; var lookup = core.Lookup; var triangles = core.Triangles; var properties = core.Properties;
        var sections = core.Sections; var batchSpan = core.BatchSpan; var shadowSpan = core.ShadowSpan;
        var batches = new M2Batch[batchSpan.Count]; var shadows = new M2ShadowBatch[shadowSpan.Count];
        var colorCount = model.Declarations.Single(d => d.Name == "colors").Count;
        var weightCount = model.Declarations.Single(d => d.Name == "weights").Count;
        var transformCount = model.Declarations.Single(d => d.Name == "transforms").Count;
        await input.RecordsAsync(batchSpan, (row, i) =>
        {
            var batch = new M2Batch(row[0], unchecked((sbyte)row[1]), U16(row, 2), U16(row, 4), U16(row, 6), I16(row, 8),
                U16(row, 10), U16(row, 12), U16(row, 14), U16(row, 16), U16(row, 18), U16(row, 20), U16(row, 22));
            if (batch.SectionIndex >= sections.Length || batch.MaterialIndex >= model.Materials.Count || batch.ColorIndex < -1 || batch.ColorIndex >= colorCount && batch.ColorIndex != -1)
                throw Error("INVALID", "A SKIN batch has an invalid section/material/color reference.");
            if (batch.TextureCount > limits.MaxTextures) throw CountLimit($"A SKIN batch uses {N(batch.TextureCount)} textures", limits.MaxTextures);
            if (batch.TextureCount > 0)
            {
                if (batch.TextureComboIndex + (long)batch.TextureCount > model.TextureCombos.Count
                    || batch.TextureCoordinateComboIndex + (long)batch.TextureCount > model.TextureCoordinates.Count
                    || batch.TextureTransformComboIndex + (long)batch.TextureCount > model.TextureTransforms.Count
                    || batch.TextureWeightComboIndex >= model.TextureWeights.Count)
                    throw Error("INVALID", "A SKIN batch combo range is outside its model lookup.");
                for (var n = 0; n < batch.TextureCount; n++)
                {
                    if (model.TextureCombos[batch.TextureComboIndex + n] >= model.Textures.Count)
                        throw Error("INVALID", "A texture combo identifies no declared model texture.");
                    var transform = model.TextureTransforms[batch.TextureTransformComboIndex + n];
                    if (transform != ushort.MaxValue && transform >= transformCount) throw Error("INVALID", "A UV-transform combo identifies no declared channel.");
                }
                var weight = model.TextureWeights[batch.TextureWeightComboIndex];
                if (weight != ushort.MaxValue && weight >= weightCount) throw Error("INVALID", "An opacity combo identifies no declared channel.");
            }
            batches[i] = batch;
        });
        await input.RecordsAsync(shadowSpan, (row, i) => shadows[i] = new(row[0], row[1], U16(row, 2), U16(row, 4), U16(row, 6), U16(row, 8), U16(row, 10)));
        await input.FinishAsync();
        return new(model, lookup, triangles, properties, sections, batches, shadows, core.MaximumBones, input.Length, core.RetainedBytes, limits);
    }
}
