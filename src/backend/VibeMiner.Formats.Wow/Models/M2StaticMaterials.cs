using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

public enum M2TextureReferenceKind { FileDataId, CreatureReplacement }
public sealed record M2StaticDraw(int BatchOrdinal, int SectionIndex, int MaterialIndex, int TextureOrdinal,
    M2TextureReferenceKind ReferenceKind, uint? FileDataId, uint ReplacementType, bool WrapU, bool WrapV, M2Batch Batch, M2Material Material);
/// <summary>Material admission only. The application still supplies source authority, texture bindings and display/geoset eligibility.</summary>
public sealed class M2StaticDrawSet
{
    internal M2StaticDrawSet(M2StaticDraw[] draws) => Draws = Array.AsReadOnly(draws);
    public IReadOnlyList<M2StaticDraw> Draws { get; }
    public const string MaterialPolicy = "m2-opaque-shader0-flags0-uv0-absent-channels-v1";
}
public static class M2StaticMaterials
{
    /// <summary>All-or-fail conservative opaque profile. Does not fetch textures or select appearance/geosets.</summary>
    public static M2StaticDrawSet AdmitOpaque(M2Model model, M2Skin skin, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(skin); token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(model, skin.Model)) throw Error("INVALID", "The SKIN belongs to another parsed model.");
        void Unsupported(string message) => throw Error("MATERIAL_UNSUPPORTED", message);
        if (model.Flags != 0) Unsupported("The model has global flags outside the initial reviewed material profile.");
        if (model.SkeletonFileDataId != 0 || model.BoneFileDataIds.Any(id => id != 0)) Unsupported("External skeleton/bone semantics are not admitted for this appearance profile.");
        if (model.Bones.Any(bone => (bone.Flags & 0x78) != 0)) Unsupported("Billboard bones require a supported pose/view policy.");
        if (model.Declarations.Any(d => d.Count != 0 && d.Name is "lights" or "ribbons" or "particles")) Unsupported("Required model light/emitter effects are outside the static profile.");
        foreach (var chunk in model.Chunks)
        {
            token.ThrowIfCancellationRequested();
            if (chunk.Id is not ("MD21" or "SFID" or "TXID" or "SKID" or "AFID" or "BFID")) Unsupported($"Chunk {chunk.Id} has no reviewed static-appearance interpretation.");
        }
        if (skin.ShadowBatches.Count != 0) Unsupported("Shadow batches have not been admitted by the first preview material policy.");
        if (skin.Batches.Count == 0) Unsupported("The selected SKIN has no explicit draw batches.");
        var budget = new M2Budget(skin.Limits.MaxOutputBytes, skin.RetainedBytes);
        budget.Add(skin.Batches.Count, 128); budget.Add(skin.Sections.Count, 1);
        var draws = new M2StaticDraw[skin.Batches.Count]; var seen = new bool[skin.Sections.Count];
        for (var i = 0; i < draws.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var batch = skin.Batches[i]; var material = model.Materials[batch.MaterialIndex];
            if (batch.Flags != 0 || batch.Priority != 0 || batch.ShaderId != 0 || batch.Flags2 != 0 || batch.ColorIndex != -1
                || batch.MaterialLayer != 0 || batch.TextureCount != 1 || material.Flags != 0 || material.BlendMode != 0)
                Unsupported($"Batch {i} requires a shader/flag/color/layer/blend outside the reviewed opaque profile.");
            if (seen[batch.SectionIndex]) Unsupported("Multiple batches on a section require a reviewed layering policy.");
            seen[batch.SectionIndex] = true;
            if (model.TextureCoordinates[batch.TextureCoordinateComboIndex] != 0
                || model.TextureWeights[batch.TextureWeightComboIndex] != ushort.MaxValue
                || model.TextureTransforms[batch.TextureTransformComboIndex] != ushort.MaxValue)
                Unsupported($"Batch {i} requires an environment/active opacity/UV-transform channel.");
            var textureOrdinal = model.TextureCombos[batch.TextureComboIndex]; var texture = model.Textures[textureOrdinal];
            if ((texture.Flags & ~3u) != 0) Unsupported("A texture has flags outside the reviewed U/V wrap bits.");
            M2TextureReferenceKind kind;
            if (texture.Type == 0)
            {
                if (texture.FileDataId is null or 0) Unsupported("A type-0 texture needs its exact positive TXID; filename inference is not supported.");
                kind = M2TextureReferenceKind.FileDataId;
            }
            else if (texture.Type is >= 11 and <= 13) kind = M2TextureReferenceKind.CreatureReplacement;
            else throw Error("MATERIAL_UNSUPPORTED", "The required replacement texture type has no admitted creature binding; no slot or fallback is guessed.");
            var section = skin.Sections[batch.SectionIndex];
            if (section.TriangleCount == 0) Unsupported("An explicit batch has no static surface triangles.");
            for (var n = section.TriangleStart; n < section.TriangleStart + section.TriangleCount; n++)
            {
                if ((n & 4095) == 0) token.ThrowIfCancellationRequested(); var normal = model.Vertices[skin.VertexLookup[skin.TriangleLookup[(int)n]]].Normal;
                var lengthSquared = normal.LengthSquared();
                if (lengthSquared <= 0 || !float.IsFinite(lengthSquared)) Unsupported("Lit preview requires usable finite nonzero source normals.");
            }
            draws[i] = new(i, batch.SectionIndex, batch.MaterialIndex, textureOrdinal, kind,
                kind == M2TextureReferenceKind.FileDataId ? texture.FileDataId : null,
                kind == M2TextureReferenceKind.CreatureReplacement ? texture.Type : 0, (texture.Flags & 1) != 0, (texture.Flags & 2) != 0, batch, material);
        }
        for (var i = 0; i < seen.Length; i++)
        { token.ThrowIfCancellationRequested(); if (!seen[i] && skin.Sections[i].TriangleCount != 0) Unsupported("A surface section lacks an explicit supported material batch."); }
        return new(draws);
    }
}
