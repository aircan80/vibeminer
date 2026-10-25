using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

public enum M2AppearanceRefusal { MultiTexture, BlendUnsupported, TextureWeight, TextureTransform, MaterialUnsupported, CombinerUnsupported }
/// <summary>The first batch, in SKIN order, that the appearance profile refuses. A null batch means the model as a whole.</summary>
public sealed record M2AppearanceDecline(M2AppearanceRefusal Reason, int? BatchOrdinal, int? TextureOrdinal, string Message);
public enum M2TextureBinding { FileDataId, CreatureVariation }
/// <summary>A texture ordinal some admitted batch draws. FileDataId is the exact TXID entry for type 0 (zero when
/// the TXID entry is zero); Variation is the CreatureDisplayInfo position for types 11-13. Nothing is resolved here.</summary>
public sealed record M2AppearanceTexture(int Ordinal, uint Type, M2TextureBinding Binding, int? Variation, uint FileDataId, bool WrapU, bool WrapV);
/// <summary>One drawn batch. Stage 0 is always present; SecondTextureOrdinal and SecondCoordinate are set exactly for
/// two-stage combiners.</summary>
public sealed record M2AppearanceDraw(int BatchOrdinal, int SectionIndex, int TextureOrdinal, bool AlphaKey, bool TwoSided, bool DepthTest, bool DepthWrite,
    M2Combiner Combiner = M2Combiner.Opaque, M2StageCoordinate Coordinate = M2StageCoordinate.Uv0, int? SecondTextureOrdinal = null,
    M2StageCoordinate? SecondCoordinate = null);
/// <summary>Declared parts the textured draw leaves out. They are counted, never drawn or evaluated. BlendedBatches are
/// batches with blend mode 2-7; EnvTransforms are env-map stages drawn without their UV animation (D094).</summary>
public sealed record M2AppearanceEffects(int Particles, int Ribbons, int Lights, int ShadowBatches, int UnbatchedSections,
    int BlendedBatches = 0, int EnvTransforms = 0);
/// <summary>Model-phase facts the SKIN phase needs; holds no source bytes.</summary>
public sealed class M2AppearanceModelFacts
{
    internal M2AppearanceModelFacts(M2Model model, bool[] weights) { Model = model; StaticWeights = Array.AsReadOnly(weights); }
    internal M2Model Model { get; }
    /// <summary>Per texture-weight track: true only for a proven constant full opacity.</summary>
    public IReadOnlyList<bool> StaticWeights { get; }
}
public sealed class M2AppearancePlan
{
    internal M2AppearancePlan(M2AppearanceDraw[] draws, M2AppearanceTexture[] textures, M2AppearanceDecline? decline, M2AppearanceEffects effects)
    { Draws = Array.AsReadOnly(draws); Textures = Array.AsReadOnly(textures); Decline = decline; Effects = effects; }
    /// <summary>Empty when declined.</summary>
    public IReadOnlyList<M2AppearanceDraw> Draws { get; }
    /// <summary>Distinct drawn texture ordinals in ascending order; empty when declined.</summary>
    public IReadOnlyList<M2AppearanceTexture> Textures { get; }
    public M2AppearanceDecline? Decline { get; }
    public M2AppearanceEffects Effects { get; }
}

/// <summary>Original reader and admission for the static textured appearance profile (one or two texture stages). It reads SKIN batch
/// records and texture-weight keys from the same verified sources the geometry readers consumed. It never
/// fetches textures, picks a display or evaluates animation. Layout facts: pywowlib 55276dc, wow.export 1b625d0.</summary>
public static class M2Appearance
{
    public const string ReaderRevision = "m2-appearance-batches-v1";
    public const string MaterialPolicy = "m2-one-or-two-stage-combiners-v1";
    /// <summary>Alpha-key reference out of 255 for blend mode 1, as the pinned wow.export renderer uses.</summary>
    public const int AlphaReference = 128;
    private const int MaxWeightKeys = 65_536, MaxWeightSlices = 65_536;
    private const ushort FullOpacity = 32767, NoChannel = ushort.MaxValue;
    private const uint KnownRenderFlags = 0x01 | 0x04 | 0x08 | 0x10;

    /// <summary>Both phases in one call, for callers that hold both verified sources at once.</summary>
    public static async Task<M2AppearancePlan> ReadAsync(Stream model, M2Model parsedModel, Stream skin, M2GeometrySkin parsedSkin,
        CancellationToken token = default) => await AdmitAsync(skin, parsedSkin, await ReadModelAsync(model, parsedModel, token), token);

    /// <summary>Model phase: sequence residency flags and one constant-full-opacity proof per texture-weight track.
    /// A track is proven only when every key it stores in this model equals 32767; keys a sequence keeps in an .anim
    /// file, an unknown global sequence or the read bound leave it unproven. A track with no keys at all has the default
    /// full opacity, as the pinned wow.export renderer evaluates it.</summary>
    public static async Task<M2AppearanceModelFacts> ReadModelAsync(Stream model, M2Model parsedModel, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parsedModel); token.ThrowIfCancellationRequested();
        var modelInput = new M2Input(model, parsedModel.Limits, token);
        if (modelInput.Length != parsedModel.SourceBytes) throw Error("INVALID", "The appearance model differs from the parsed geometry model.");
        var limits = parsedModel.Limits; var header = (parsedModel.Flags & 8) != 0 ? 312 : 304;
        var sequences = Declaration(parsedModel, "sequences"); var flags = new uint[(int)sequences.Count];
        await modelInput.RecordsAsync(modelInput.Span("sequences", sequences.Count, sequences.Offset, 64, limits.MaxMetadataEntries,
            parsedModel.BaseOffset, parsedModel.BaseBytes, header), (row, i) => flags[i] = U32(row, 12));
        var weightTracks = Declaration(parsedModel, "weights"); var globals = Declaration(parsedModel, "globalSequences").Count;
        // One shared budget of slice entries and keys across all tracks. Each distinct slice array and key array is read
        // once, in bulk; tracks that share arrays share the result. A track the budget cannot cover stays unproven.
        var tracks = new (short Global, uint Count, uint Offset)[(int)weightTracks.Count];
        await modelInput.RecordsAsync(modelInput.Span("weights", weightTracks.Count, weightTracks.Offset, 20, limits.MaxMetadataEntries,
            parsedModel.BaseOffset, parsedModel.BaseBytes, header), (row, i) => tracks[i] = (I16(row, 2), U32(row, 12), U32(row, 16)));
        var proofs = new bool[tracks.Length]; int slicesLeft = MaxWeightSlices, keysLeft = MaxWeightKeys;
        var sliceArrays = new Dictionary<(uint Count, uint Offset, bool Global), bool>(); var keyArrays = new Dictionary<(uint Count, uint Offset), bool>();
        for (var track = 0; track < proofs.Length; track++)
        {
            token.ThrowIfCancellationRequested(); var (global, count, offset) = tracks[track];
            if (global >= globals) continue;
            var key = (count, count == 0 ? 0u : offset, global >= 0);
            if (sliceArrays.TryGetValue(key, out var known)) { proofs[track] = known; continue; }
            var result = false;
            if (count <= slicesLeft)
            {
                slicesLeft -= (int)count; result = true;
                var slices = new (uint Count, uint Offset)[(int)count];
                await modelInput.RecordsAsync(modelInput.Span("weight values", count, offset, 8, limits.MaxMetadataEntries, parsedModel.BaseOffset, parsedModel.BaseBytes, header),
                    (row, i) => slices[i] = (U32(row), U32(row, 4)));
                for (var i = 0; result && i < slices.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var (keyCount, keyOffset) = slices[i]; if (keyCount == 0) continue;
                    // Keys for a sequence without 0x20 live in an .anim file; their values are not provable from this model.
                    if (global < 0 && (i >= flags.Length || (flags[i] & 0x20) == 0)) { result = false; break; }
                    if (keyArrays.TryGetValue((keyCount, keyOffset), out var keysKnown)) { result = keysKnown; continue; }
                    if (keyCount > keysLeft) { result = false; break; }
                    keysLeft -= (int)keyCount; var full = true;
                    await modelInput.RecordsAsync(modelInput.Span("weight keys", keyCount, keyOffset, 2, MaxWeightKeys, parsedModel.BaseOffset, parsedModel.BaseBytes, header),
                        (value, _) => { if (U16(value) != FullOpacity) full = false; });
                    keyArrays[(keyCount, keyOffset)] = full; result = full;
                }
            }
            sliceArrays[key] = result; proofs[track] = result;
        }
        await modelInput.FinishAsync();
        return new(parsedModel, proofs);
    }

    /// <summary>SKIN phase: reads the batch records and admits every batch or names the first refused one. Batches with
    /// blend mode 2-7 are counted as not drawn (D094); the rest must fit a supported combiner (m2-skin.md).</summary>
    public static async Task<M2AppearancePlan> AdmitAsync(Stream skin, M2GeometrySkin parsedSkin, M2AppearanceModelFacts facts, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parsedSkin); ArgumentNullException.ThrowIfNull(facts); token.ThrowIfCancellationRequested();
        var parsedModel = facts.Model;
        if (!ReferenceEquals(parsedSkin.Model, parsedModel)) throw Error("INVALID", "The SKIN belongs to another parsed model.");
        var skinInput = new M2Input(skin, parsedSkin.Limits, token);
        if (skinInput.Length != parsedSkin.SourceBytes) throw Error("INVALID", "The appearance SKIN differs from the parsed geometry SKIN.");
        var batchDeclaration = parsedSkin.BatchDeclaration;
        if (batchDeclaration.Stride != 24 || batchDeclaration.Count > parsedModel.Limits.MaxBatches) throw Error("LIMIT", "The SKIN batch count exceeds its bound.");
        var batches = new M2Batch[(int)batchDeclaration.Count];
        await skinInput.RecordsAsync(new("batches", batchDeclaration.Offset, batches.Length, 24), (row, i) => batches[i] =
            new(row[0], unchecked((sbyte)row[1]), U16(row, 2), U16(row, 4), U16(row, 6), I16(row, 8), U16(row, 10), U16(row, 12),
                U16(row, 14), U16(row, 16), U16(row, 18), U16(row, 20), U16(row, 22)));
        await skinInput.FinishAsync();
        var weightTracks = Declaration(parsedModel, "weights"); var transformTracks = Declaration(parsedModel, "transforms").Count;
        var sections = parsedSkin.Sections; var batched = new bool[sections.Count]; var drawn = new bool[sections.Count];
        var draws = new List<M2AppearanceDraw>(batches.Length); var textures = new SortedDictionary<int, M2AppearanceTexture>();
        var colors = Declaration(parsedModel, "colors").Count; int blended = 0, envTransforms = 0; int? firstBlended = null;
        M2AppearanceDecline? decline = null;
        M2AppearanceDecline Refuse(M2AppearanceRefusal reason, int batch, int? texture, string message) => new(reason, batch, texture, message);
        for (var i = 0; i < batches.Length && decline is null; i++)
        {
            token.ThrowIfCancellationRequested(); var b = batches[i];
            if (b.SectionIndex >= sections.Count || b.MaterialIndex >= parsedModel.Materials.Count || b.ColorIndex < -1 || b.ColorIndex >= colors)
            { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, null, "The batch names a section, material or color the model does not declare."); break; }
            var material = parsedModel.Materials[b.MaterialIndex];
            if (material.BlendMode > 7) { decline = Refuse(M2AppearanceRefusal.BlendUnsupported, i, null, "The batch blends with mode " + material.BlendMode + ", which has no M2 meaning."); break; }
            batched[b.SectionIndex] = true;
            // D094: blended layers (2 alpha, 3 add without alpha, 4 add, 5 mod, 6 mod2x, 7 blend add) are counted, not drawn.
            if (material.BlendMode >= 2) { blended++; if (sections[b.SectionIndex].TriangleCount != 0) firstBlended ??= i; continue; }
            if (b.TextureCount is 0 or > 2) { decline = Refuse(M2AppearanceRefusal.MultiTexture, i, null, "The batch combines " + b.TextureCount + " texture stages; one or two are drawn."); break; }
            var stages = b.TextureCount;
            if ((material.Flags & ~KnownRenderFlags) != 0 || b.Flags is not (0 or 0x10) && !(stages == 2 && b.Flags is 0x80 or 0x90) || b.Flags2 != 0 || b.MaterialLayer != 0)
            { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, null, "The batch needs a render flag, batch flag or layer outside the appearance profile."); break; }
            if (M2Combiners.Admit(stages, b.ShaderId, out var combinerName) is not { } profile)
            { decline = Refuse(M2AppearanceRefusal.CombinerUnsupported, i, null, "The batch combines its textures with " + combinerName + ", which is not drawn."); break; }
            if (b.TextureComboIndex + stages > parsedModel.TextureCombos.Count || Enumerable.Range(b.TextureComboIndex, stages).Any(c => parsedModel.TextureCombos[c] >= parsedModel.Textures.Count))
            { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, null, "The batch texture lookup names no declared texture."); break; }
            var ordinals = Enumerable.Range(b.TextureComboIndex, stages).Select(c => (int)parsedModel.TextureCombos[c]).ToArray();
            // A present coordinate lookup must agree with the combiner's stages: 0 for UV0, 1 for UV1, -1 for an env map.
            // Retail models usually ship an empty lookup, which defers to the shader.
            var coordinates = parsedModel.TextureCoordinates; int? wrongCoordinate = null;
            if (coordinates.Count != 0)
                for (var s = 0; s < stages && wrongCoordinate is null; s++)
                    if (b.TextureCoordinateComboIndex + s >= coordinates.Count || coordinates[b.TextureCoordinateComboIndex + s] != profile.Stages[s] switch
                        { M2StageCoordinate.Uv0 => 0, M2StageCoordinate.Uv1 => 1, _ => -1 }) wrongCoordinate = ordinals[s];
            if (wrongCoordinate is { } badStage) { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, badStage, "The batch coordinate lookup disagrees with its combiner's texture coordinates."); break; }
            if (b.TextureWeightComboIndex >= parsedModel.TextureWeights.Count || b.TextureTransformComboIndex + stages > parsedModel.TextureTransforms.Count)
            { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, ordinals[0], "The batch opacity or UV-transform lookup lies outside the model."); break; }
            // Stage s reads weight lookup entry index + s; the client may scale a stage (such as an added env term) by it. Each entry
            // present must be 0xFFFF or a proven constant full opacity. An entry past the lookup's end names no weight channel
            // (live retail two-stage bodies ship a one-entry lookup), as an entry past the declared transform tracks names no transform.
            int? animatedWeight = null;
            for (var s = 0; s < stages && animatedWeight is null; s++)
            {
                if (b.TextureWeightComboIndex + s >= parsedModel.TextureWeights.Count) break;
                var weight = parsedModel.TextureWeights[b.TextureWeightComboIndex + s];
                if (weight != NoChannel && (weight >= weightTracks.Count || !facts.StaticWeights[weight])) animatedWeight = ordinals[s];
            }
            if (animatedWeight is { } weighted) { decline = Refuse(M2AppearanceRefusal.TextureWeight, i, weighted, "The batch texture weight is not a proven constant full opacity."); break; }
            // Stage s reads transform lookup entry index + s. 0xFFFF, or an entry past the declared tracks, is no transform.
            var animated = 0; int? uvTransform = null;
            for (var s = 0; s < stages; s++)
            {
                var transform = parsedModel.TextureTransforms[b.TextureTransformComboIndex + s];
                if (transform == NoChannel || transform >= transformTracks) continue;
                if (profile.Stages[s] == M2StageCoordinate.Env) animated++; else { uvTransform = ordinals[s]; break; }
            }
            if (uvTransform is { } scrolled) { decline = Refuse(M2AppearanceRefusal.TextureTransform, i, scrolled, "The batch animates its texture coordinates."); break; }
            var references = new M2AppearanceTexture[stages]; int? badTexture = null; string? badMessage = null;
            for (var s = 0; s < stages && badTexture is null; s++)
            {
                var texture = parsedModel.Textures[ordinals[s]];
                if ((texture.Flags & ~3u) != 0) { badTexture = ordinals[s]; badMessage = "The texture has flags outside the U/V wrap bits."; }
                else if (texture.Type == 0) references[s] = new(ordinals[s], 0, M2TextureBinding.FileDataId, null, texture.FileDataId ?? 0, (texture.Flags & 1) != 0, (texture.Flags & 2) != 0);
                else if (texture.Type is >= 11 and <= 13) references[s] = new(ordinals[s], texture.Type, M2TextureBinding.CreatureVariation, (int)texture.Type - 11, 0, (texture.Flags & 1) != 0, (texture.Flags & 2) != 0);
                else { badTexture = ordinals[s]; badMessage = "Texture type " + texture.Type + " has no creature binding in this profile."; }
            }
            if (badTexture is { } bad) { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, bad, badMessage!); break; }
            if (drawn[b.SectionIndex]) { decline = Refuse(M2AppearanceRefusal.MaterialUnsupported, i, ordinals[0], "Several opaque or alpha-key batches layer the same section."); break; }
            drawn[b.SectionIndex] = true; envTransforms += animated;
            foreach (var reference in references) textures[reference.Ordinal] = reference;
            if (sections[b.SectionIndex].TriangleCount != 0)
                draws.Add(new(i, b.SectionIndex, ordinals[0], material.BlendMode == 1 && profile.TextureAlpha, (material.Flags & 4) != 0, (material.Flags & 8) == 0,
                    (material.Flags & 0x10) == 0, profile.Combiner, profile.Stages[0], stages == 2 ? ordinals[1] : null, stages == 2 ? profile.Stages[1] : null));
        }
        if (decline is null && draws.Count == 0)
            decline = firstBlended is { } layer
                ? new(M2AppearanceRefusal.BlendUnsupported, layer, null, "Every batch with surface triangles is a blended layer; no opaque or alpha-key surface is left to draw.")
                : new(M2AppearanceRefusal.MaterialUnsupported, null, null, "No batch draws any declared surface triangle.");
        var unbatched = 0; for (var i = 0; i < sections.Count; i++) if (!batched[i] && sections[i].TriangleCount != 0) unbatched++;
        var effects = new M2AppearanceEffects((int)Declaration(parsedModel, "particles").Count, (int)Declaration(parsedModel, "ribbons").Count,
            (int)Declaration(parsedModel, "lights").Count, (int)parsedSkin.ShadowBatchDeclaration.Count, decline is null ? unbatched : 0,
            decline is null ? blended : 0, decline is null ? envTransforms : 0);
        return decline is null ? new(draws.ToArray(), textures.Values.ToArray(), null, effects) : new([], [], decline, effects);
    }
    private static M2ArrayDeclaration Declaration(M2Model model, string name) =>
        model.Declarations.SingleOrDefault(d => d.Name == name) ?? throw Error("INVALID", "The parsed model lacks its " + name + " declaration.");
}
