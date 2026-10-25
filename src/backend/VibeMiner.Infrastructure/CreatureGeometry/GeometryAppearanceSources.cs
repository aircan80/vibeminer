using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Texture chain outcome for one display. Ready carries owned decoded slots until the VMA1 build retires them.
/// Cacheable outcomes depend only on the verified model, SKIN and CreatureDisplayInfo row, so they are retained.</summary>
internal sealed class GeometryAppearancePreparation : IDisposable
{
    private IDisposable? reservation;
    private IReadOnlyList<byte[]>? texels;
    internal GeometryAppearancePreparation(CreatureGeometryAppearance evidence, bool cacheable, IReadOnlyList<AppearanceDrawInput>? draws = null,
        AppearanceBinding? binding = null, IReadOnlyList<byte[]>? images = null, IDisposable? payload = null)
    { Evidence = evidence; Cacheable = cacheable; Draws = draws; Binding = binding; texels = images; reservation = payload; }
    internal CreatureGeometryAppearance Evidence { get; }
    internal bool Cacheable { get; }
    internal IReadOnlyList<AppearanceDrawInput>? Draws { get; }
    internal AppearanceBinding? Binding { get; }
    internal IReadOnlyList<byte[]> Texels => texels ?? throw new ObjectDisposedException(nameof(GeometryAppearancePreparation));
    internal bool HasImages => texels is not null;
    public void Dispose() { texels = null; Interlocked.Exchange(ref reservation, null)?.Dispose(); }
}

internal static class GeometryAppearanceSources
{
    internal const string Policy = "creature-textured-appearance-v1";
    internal const long PayloadBytes = 16L << 20;
    private static readonly Blp2Limits Limits = new(MaxSourceBytes: 16L << 20, MaxDimension: 16_384, MaxPixels: 1 << 20,
        MaxMipBytes: 4L << 20, MaxRgbaBytes: 4L << 20);

    internal static CreatureGeometryAppearance Outcome(CreatureGeometryAppearanceState state, CreatureGeometryAppearanceDisplay? display,
        IReadOnlyList<CreatureGeometryTexture> textures, CreatureGeometryEffects? effects, CreatureGeometryAppearanceProblem? problem) =>
        new(Policy, state, display, Array.AsReadOnly(textures.ToArray()), effects, null, problem);
    internal static CreatureGeometryAppearance NotPrepared(GeometryDisplayAuthorization selection) =>
        Outcome(CreatureGeometryAppearanceState.NotPrepared, Display(selection), [], null, null);
    internal static CreatureGeometryAppearanceDisplay? Display(GeometryDisplayAuthorization selection) => selection.Display.Textures is { } t
        ? new(selection.Selected.DisplayId, t.ExtendedDisplayInfoId, Array.AsReadOnly(t.TextureVariations.ToArray())) : null;

    /// <summary>Plan failures, display refusals and unresolved FileDataIDs stop before any texture demand. Each texture is
    /// resolved through the open geometry handle, acquired at interactive priority and fully BLTE-verified before decoding.</summary>
    internal static async Task<GeometryAppearancePreparation> PrepareAsync(GeometrySourceServices services, AssetResolutionSession handle,
        GeometrySourceContextIdentity context, IGeometrySelectionFence selection, M2AppearancePlan? plan, DiscoveryException? planFailure,
        string geometryBindingSha256, IGeometryPayloadBudget payload, Func<CreatureGeometryProgress, Task> progress, CancellationToken token)
    {
        var original = selection.Original; var display = Display(original); var effects = plan is null ? null : Effects(plan.Effects);
        CreatureGeometryAppearanceProblem Problem(string code, string message, bool retryable = false, int? batch = null, int? texture = null) => new(code, message, retryable, batch, texture);
        GeometryAppearancePreparation Stop(CreatureGeometryAppearanceState state, CreatureGeometryAppearanceProblem problem, IReadOnlyList<CreatureGeometryTexture>? chain = null, bool cacheable = true) =>
            new(Outcome(state, display, chain ?? [], effects, problem), cacheable);
        if (display is null)
            return Stop(CreatureGeometryAppearanceState.TextureUnresolved, Problem("CREATURE_DISPLAY_TEXTURES_UNREADABLE", "The display row has no readable ExtendedDisplayInfoID and four texture variation fields."));
        if (display.ExtendedDisplayInfoId != "0")
            return Stop(CreatureGeometryAppearanceState.CharacterModel, Problem("CREATURE_APPEARANCE_CHARACTER_MODEL", "This display is a character model (ExtendedDisplayInfoID " + display.ExtendedDisplayInfoId + "); its baked textures are not resolved."));
        if (planFailure is not null)
            return Stop(planFailure.Code.Contains("LIMIT", StringComparison.Ordinal) ? CreatureGeometryAppearanceState.ResourceLimit : CreatureGeometryAppearanceState.MaterialUnsupported,
                Problem(planFailure.Code, "The model's batches or texture weights could not be read for texturing."));
        if (plan is null) throw GeometrySourceCapsuleCodec.Invalid();
        if (plan.Decline is { } decline)
            return Stop(decline.Reason switch
            {
                M2AppearanceRefusal.MultiTexture => CreatureGeometryAppearanceState.MultiTexture,
                M2AppearanceRefusal.BlendUnsupported => CreatureGeometryAppearanceState.BlendUnsupported,
                M2AppearanceRefusal.TextureWeight => CreatureGeometryAppearanceState.TextureWeight,
                M2AppearanceRefusal.TextureTransform => CreatureGeometryAppearanceState.TextureTransform,
                _ => CreatureGeometryAppearanceState.MaterialUnsupported
            }, Problem("CREATURE_APPEARANCE_" + decline.Reason.ToString().ToUpperInvariant() switch
            {
                "MULTITEXTURE" => "MULTI_TEXTURE", "BLENDUNSUPPORTED" => "BLEND_UNSUPPORTED", "TEXTUREWEIGHT" => "TEXTURE_WEIGHT",
                "TEXTURETRANSFORM" => "TEXTURE_TRANSFORM", _ => "MATERIAL_UNSUPPORTED"
            }, decline.Message, batch: decline.BatchOrdinal, texture: decline.TextureOrdinal));
        // Chain: type 0 uses TXID[ordinal]; 11/12/13 use TextureVariationFileDataID[0/1/2]. Zero is unresolved, never substituted.
        var chain = new CreatureGeometryTexture[plan.Textures.Count]; var ids = new uint[plan.Textures.Count]; int? unresolved = null;
        for (var i = 0; i < chain.Length; i++)
        {
            var t = plan.Textures[i]; uint id = 0;
            if (t.Binding == M2TextureBinding.FileDataId) id = t.FileDataId;
            else if (int.TryParse(display.TextureVariations[t.Variation!.Value], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value > 0) id = (uint)value;
            ids[i] = id; unresolved ??= id == 0 ? t.Ordinal : null;
            chain[i] = new(t.Ordinal, (int)t.Type, t.Binding == M2TextureBinding.FileDataId ? CreatureGeometryTextureBinding.Txid : CreatureGeometryTextureBinding.Variation,
                t.Variation, id == 0 ? null : N(id), id == 0 ? CreatureGeometryTextureState.Unresolved : CreatureGeometryTextureState.NotReached);
        }
        if (unresolved is { } missing)
            return Stop(CreatureGeometryAppearanceState.TextureUnresolved, Problem("CREATURE_APPEARANCE_TEXTURE_UNRESOLVED",
                "Texture ordinal " + missing + " has a zero FileDataID in its TXID entry or display variation; nothing is substituted.", texture: missing), chain);
        var distinct = ids.Distinct().ToArray();
        await progress(new("textureResolution", "Resolving exact texture memberships", "stage", Dependency: CreatureGeometryDependency.Texture));
        await selection.RevalidateAsync(token);
        var batch = await handle.ResolveAsync(distinct, _ => progress(new("textureResolution", "Resolving exact texture memberships", "stage", Dependency: CreatureGeometryDependency.Texture)), token);
        await selection.RevalidateAsync(token);
        if (batch.Results.Count != distinct.Length || batch.Results.Select(r => r.FileDataId).Except(distinct).Any()) throw GeometrySourceCapsuleCodec.Invalid();
        var resolved = new Dictionary<uint, AssetResolutionResult>(); var budget = new GeometryWorkBudget(services.Resources);
        IReadOnlyList<CreatureGeometryTexture> Mark(int at, CreatureGeometryTextureState state)
        { var copy = chain.ToArray(); copy[at] = copy[at] with { State = state }; return copy; }
        for (var i = 0; i < chain.Length; i++)
        {
            if (resolved.ContainsKey(ids[i])) continue;
            var result = batch.Results.Single(r => r.FileDataId == ids[i]);
            if (result.Status != AssetResolutionStatus.Ready)
            {
                var ambiguous = result.Status == AssetResolutionStatus.AmbiguousVariants;
                return Stop(ambiguous ? CreatureGeometryAppearanceState.TextureAmbiguous : CreatureGeometryAppearanceState.TextureMissing,
                    Problem(ambiguous ? "CREATURE_APPEARANCE_TEXTURE_AMBIGUOUS" : result.Status == AssetResolutionStatus.MissingMembership ? "CREATURE_APPEARANCE_TEXTURE_MISSING" : "CREATURE_APPEARANCE_TEXTURE_ENCODING_UNRESOLVED",
                        "Texture ordinal " + chain[i].Ordinal + " (FileDataID " + ids[i] + ") has no single verified source in this build.", texture: chain[i].Ordinal),
                    Mark(i, ambiguous ? CreatureGeometryTextureState.Ambiguous : CreatureGeometryTextureState.Missing), cacheable: false);
            }
            var expected = result.Expectation;
            if (expected is null || expected.Role != "asset" || expected.EncodedBytes is < 9 or > ModelAppearanceArtifacts.MaximumTextureSourceBytes ||
                expected.DecodedBytes is < 1 or > ModelAppearanceArtifacts.MaximumTextureSourceBytes)
                return Stop(CreatureGeometryAppearanceState.ResourceLimit, Problem("CREATURE_APPEARANCE_TEXTURE_LIMIT",
                    "Texture ordinal " + chain[i].Ordinal + " exceeds the 16 MiB texture source bound.", texture: chain[i].Ordinal), Mark(i, CreatureGeometryTextureState.ResourceLimit), cacheable: false);
            var association = await GeometrySourceCapsuleCodec.SnapshotAssociationAsync(result.Association, budget, token);
            var core = original.Core.Artifact.Binding.Source.Association;
            if (association.Request != context.Request || association.SourceReference != context.Opening || association.ResearchLocale != context.ResearchLocale ||
                association.Memberships.Any(m => m.Entry.FileDataId != ids[i] || m.Entry.ContentKey != expected.ContentKey) ||
                !MetadataReceiptIdentity.Same(core.EncodingSource, association.EncodingSource) || !MetadataReceiptIdentity.Same(core.RootSource, association.RootSource))
                throw GeometrySourceCapsuleCodec.Invalid();
            resolved[ids[i]] = result with { Association = association, Expectation = expected with { } };
        }
        var reservation = payload.Reserve(PayloadBytes); var transferred = false;
        try
        {
            var images = new Dictionary<uint, (Blp2Image Image, ContentReceipt Receipt)>(); long held = 0;
            var slots = new List<(uint Id, bool WrapU, bool WrapV)>(); var draws = new List<AppearanceDrawInput>();
            // One slot per distinct (FileDataID, wrap U, wrap V), in texture-ordinal order; plan.Textures and chain share indices.
            for (var i = 0; i < chain.Length; i++) { var key = (ids[i], plan.Textures[i].WrapU, plan.Textures[i].WrapV); if (!slots.Contains(key)) slots.Add(key); }
            GeometryAppearancePreparation Failed(int at, DiscoveryException error)
            {
                var (state, textureState) = TextureFailure(error.Code);
                return Stop(state, Problem(error.Code.Length <= 64 ? error.Code : "CREATURE_APPEARANCE_TEXTURE_FAILED",
                    "Texture ordinal " + chain[at].Ordinal + " (FileDataID " + ids[at] + ") could not be drawn (" + Words(textureState) + ").", error.Retryable, texture: chain[at].Ordinal),
                    Mark(at, textureState), cacheable: false);
            }
            GeometryAppearancePreparation Storage(int at) => Stop(CreatureGeometryAppearanceState.TextureUnavailable, Problem("CREATURE_APPEARANCE_TEXTURE_UNAVAILABLE",
                "Texture ordinal " + chain[at].Ordinal + " source storage is unavailable.", true, texture: chain[at].Ordinal), Mark(at, CreatureGeometryTextureState.Unavailable), cacheable: false);
            // Pass 1: verify each source and read only its declared mips, so the plan is fixed before any pixel is held (D091).
            var declared = new List<IReadOnlyList<Blp2Mip>>(distinct.Length);
            foreach (var id in distinct)
            {
                var at = Array.IndexOf(ids, id);
                try
                {
                    await using var node = await GeometrySourceVerification.VerifyAsync(services, resolved[id], handle.SourceContext, selection, progress, CreatureGeometryDependency.Texture, token);
                    using (await services.Resources.EnterCpuAsync(null, token)) declared.Add(await Blp2Decoder.ReadMipsAsync(node.Decoded, Limits, token));
                }
                catch (DiscoveryException error) when (!Request(error.Code)) { return Failed(at, error); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Storage(at); }
            }
            var levels = ModelAppearanceArtifacts.ChooseMips(declared, distinct.Select(id => slots.Count(slot => slot.Id == id)).ToArray(), out var noFit);
            if (noFit >= 0) return Failed(Array.IndexOf(ids, distinct[noFit]), new("BLP_MIP_UNAVAILABLE", "No declared mip fits 1,024 px each side; no level was resized or substituted."));
            if (levels is null)
                return Stop(CreatureGeometryAppearanceState.ResourceLimit, Problem("CREATURE_APPEARANCE_TEXEL_LIMIT", "Even the smallest declared mips exceed the 8 MiB decoded texture bound."), chain, cacheable: false);
            // Pass 2: decode exactly the planned level. Held images stay within the planned 8 MiB, checked after every decode.
            for (var d = 0; d < distinct.Length; d++)
            {
                var id = distinct[d]; var at = Array.IndexOf(ids, id);
                try
                {
                    await using var node = await GeometrySourceVerification.VerifyAsync(services, resolved[id], handle.SourceContext, selection, progress, CreatureGeometryDependency.Texture, token);
                    await progress(new("textureDecoding", "Decoding texture " + chain[at].Ordinal, "stage", Dependency: CreatureGeometryDependency.Texture));
                    Blp2Image image;
                    using (await services.Resources.EnterCpuAsync(null, token)) image = await Blp2Decoder.DecodeAsync(node.Decoded, levels[d], Limits, token);
                    held += image.Rgba.LongLength;
                    if (held > ModelAppearanceArtifacts.MaximumTexelBytes) throw new DiscoveryException("CREATURE_APPEARANCE_TEXEL_LIMIT", "Decoded textures exceeded their planned bound.");
                    var planned = declared[d][levels[d]];
                    if (image.MipLevel != levels[d] || image.Width != planned.Width || image.Height != planned.Height)
                        throw new DiscoveryException("BLP_INVALID", "The decoded texture differs from its planned mip.");
                    images[id] = (image, node.Canonical.Receipt);
                }
                catch (DiscoveryException error) when (!Request(error.Code)) { return Failed(at, error); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Storage(at); }
            }
            for (var i = 0; i < chain.Length; i++)
            {
                var (decoded, receipt) = images[ids[i]];
                chain[i] = chain[i] with
                {
                    State = CreatureGeometryTextureState.Decoded,
                    Image = new(decoded.Width, decoded.Height, decoded.MipLevel, decoded.BaseWidth, decoded.BaseHeight, Name(decoded.Encoding)),
                    Source = new(receipt.ContentKey, receipt.EncodingKey, receipt.Sha256, long.Parse(receipt.EncodedBytes, CultureInfo.InvariantCulture),
                        long.Parse(receipt.DecodedBytes, CultureInfo.InvariantCulture), receipt.VerifierRevision)
                };
            }
            var inputs = slots.Select(s =>
            {
                var (image, receipt) = images[s.Id];
                return new AppearanceTextureInput(s.Id, new(receipt.ContentKey, receipt.EncodingKey, receipt.Sha256, long.Parse(receipt.EncodedBytes, CultureInfo.InvariantCulture),
                    long.Parse(receipt.DecodedBytes, CultureInfo.InvariantCulture), receipt.VerifierRevision), image.Width, image.Height, image.MipLevel,
                    image.BaseWidth, image.BaseHeight, image.Encoding, s.WrapU, s.WrapV);
            }).ToArray();
            foreach (var draw in plan.Draws)
            {
                var t = plan.Textures.Single(x => x.Ordinal == draw.TextureOrdinal); var at = Array.FindIndex(chain, c => c.Ordinal == t.Ordinal);
                draws.Add(new(draw.BatchOrdinal, draw.SectionIndex, draw.TextureOrdinal, slots.IndexOf((ids[at], t.WrapU, t.WrapV)),
                    draw.AlphaKey, draw.TwoSided, draw.DepthTest, draw.DepthWrite));
            }
            var fields = new[] { 0 }.Concat(display.TextureVariations.Select(v => int.Parse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))).ToArray();
            var binding = new AppearanceBinding(geometryBindingSha256, uint.Parse(display.DisplayId, CultureInfo.InvariantCulture), Array.AsReadOnly(fields), Array.AsReadOnly(inputs));
            var ready = new GeometryAppearancePreparation(Outcome(CreatureGeometryAppearanceState.Ready, display, chain, effects, null), false,
                Array.AsReadOnly(draws.ToArray()), binding, Array.AsReadOnly(slots.Select(s => images[s.Id].Image.Rgba).ToArray()), reservation);
            transferred = true; return ready;
        }
        finally { if (!transferred) reservation.Dispose(); }
    }
    internal static CreatureGeometryEffects Effects(M2AppearanceEffects e) => new(e.Particles, e.Ribbons, e.Lights, e.ShadowBatches, e.UnbatchedSections);
    private static string Words(CreatureGeometryTextureState state) => state switch
    {
        CreatureGeometryTextureState.Protected => "encrypted", CreatureGeometryTextureState.ResourceLimit => "over a size limit",
        CreatureGeometryTextureState.Unavailable => "unavailable", CreatureGeometryTextureState.Unsupported => "unsupported encoding", _ => "failed integrity checks"
    };
    internal static string Name(Blp2Encoding encoding) => encoding switch
    { Blp2Encoding.Paletted => "palette", Blp2Encoding.Bc1 => "bc1", Blp2Encoding.Bc2 => "bc2", Blp2Encoding.Bc3 => "bc3", _ => "bgra8" };
    private static (CreatureGeometryAppearanceState, CreatureGeometryTextureState) TextureFailure(string code) => code switch
    {
        "BLTE_ENCRYPTED" or "BLTE_KEY_UNAVAILABLE" => (CreatureGeometryAppearanceState.TextureProtected, CreatureGeometryTextureState.Protected),
        "BLP_UNSUPPORTED" => (CreatureGeometryAppearanceState.TextureUnsupported, CreatureGeometryTextureState.Unsupported),
        "BLP_MIP_UNAVAILABLE" => (CreatureGeometryAppearanceState.ResourceLimit, CreatureGeometryTextureState.ResourceLimit),
        _ when code.Contains("LIMIT", StringComparison.Ordinal) || code.Contains("CAPACITY", StringComparison.Ordinal) => (CreatureGeometryAppearanceState.ResourceLimit, CreatureGeometryTextureState.ResourceLimit),
        _ when code.Contains("UNAVAILABLE", StringComparison.Ordinal) || code.Contains("HTTP", StringComparison.Ordinal) ||
            code.Contains("NETWORK", StringComparison.Ordinal) || code == "LOCAL_OBJECT_MISSING" => (CreatureGeometryAppearanceState.TextureUnavailable, CreatureGeometryTextureState.Unavailable),
        _ when code.Contains("UNSUPPORTED", StringComparison.Ordinal) => (CreatureGeometryAppearanceState.TextureUnsupported, CreatureGeometryTextureState.Unsupported),
        _ => (CreatureGeometryAppearanceState.TextureCorrupt, CreatureGeometryTextureState.Corrupt)
    };
    private static bool Request(string code) => code.Contains("STALE", StringComparison.Ordinal) || code.Contains("SOURCE_CHANGED", StringComparison.Ordinal) ||
        code.Contains("BUSY", StringComparison.Ordinal) || code.EndsWith("CLEANUP_FAILED", StringComparison.Ordinal) ||
        code is "ENGINE_STOPPING" or "INVALID_REQUEST" or "ASSET_SOURCE_CLEANUP" or "ASSET_CLEANUP_FAILED";
    private static string N(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
