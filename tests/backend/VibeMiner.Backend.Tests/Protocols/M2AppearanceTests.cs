using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;
using static VibeMiner.Backend.Tests.AppearanceModels;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class M2AppearanceTests
{
    private static readonly M2Limits Limits = new(MaxSourceBytes: 8 << 20, MaxOutputBytes: 8 << 20, MaxVertices: 65536, MaxTriangleIndices: 131070, MaxMetadataEntries: 8192);

    internal static async Task<(M2Model Model, M2GeometrySkin Skin, M2AppearancePlan Plan)> PlanAsync(Model spec, Batch[] batches, int sections = 1)
    {
        var modelBytes = ModelBytes(spec); var skinBytes = SkinBytes(batches, sections);
        using var model = new MemoryStream(modelBytes); using var skin = new MemoryStream(skinBytes);
        var parsed = await M2ModelReader.ReadAsync(model, Limits); var geometry = await M2GeometrySkinReader.ReadAsync(skin, parsed, Limits);
        model.Position = 0; skin.Position = 0;
        return (parsed, geometry, await M2Appearance.ReadAsync(model, parsed, skin, geometry));
    }

    [Fact]
    public async Task Single_texture_batches_bind_txid_and_display_variations_and_count_undrawn_effects()
    {
        var spec = new Model([new(0, 1, 700), new(12, 2)], [(4, 1), (0, 0)], [0, 1], [ushort.MaxValue], [ushort.MaxValue], [], [0x20], Particles: 4, Ribbons: 2);
        var (_, _, plan) = await PlanAsync(spec, [new(Section: 0, Shader: 0x10, Combo: 0), new(Section: 1, Shader: 0, Material: 1, Combo: 1)], sections: 2);
        Assert.Null(plan.Decline);
        Assert.Equal([new(0, 0, 0, true, true, true, true, M2Combiner.Mod), new M2AppearanceDraw(1, 1, 1, false, false, true, true)], plan.Draws);
        Assert.Equal([new(0, 0, M2TextureBinding.FileDataId, null, 700, true, false), new M2AppearanceTexture(1, 12, M2TextureBinding.CreatureVariation, 1, 0, false, true)], plan.Textures);
        Assert.Equal(new M2AppearanceEffects(4, 2, 0, 0, 0), plan.Effects);
    }

    [Fact]
    public async Task Alpha_key_needs_blend_one_and_texture_alpha_shader()
    {
        var (_, _, opaqueShader) = await PlanAsync(Simple(blend: 1), [new(Shader: 0)]);
        Assert.False(Assert.Single(opaqueShader.Draws).AlphaKey);
        var (_, _, mod) = await PlanAsync(Simple(blend: 1), [new(Shader: 0x10)]);
        Assert.True(Assert.Single(mod.Draws).AlphaKey);
        var (_, _, opaque) = await PlanAsync(Simple(blend: 0), [new(Shader: 0x10)]);
        Assert.False(Assert.Single(opaque.Draws).AlphaKey);
    }

    public static TheoryData<string, M2AppearanceRefusal, int> Refusals => new()
    {
        { "multi", M2AppearanceRefusal.MultiTexture, 1 }, { "blend", M2AppearanceRefusal.BlendUnsupported, 1 },
        { "animatedWeight", M2AppearanceRefusal.TextureWeight, 1 }, { "animWeight", M2AppearanceRefusal.TextureWeight, 1 },
        { "transform", M2AppearanceRefusal.TextureTransform, 1 }, { "combiner", M2AppearanceRefusal.CombinerUnsupported, 1 },
        { "layered", M2AppearanceRefusal.MaterialUnsupported, 1 }, { "type14", M2AppearanceRefusal.MaterialUnsupported, 1 },
        { "renderFlag", M2AppearanceRefusal.MaterialUnsupported, 1 }, { "uv1", M2AppearanceRefusal.MaterialUnsupported, 1 },
        { "twoStageFlagOnOne", M2AppearanceRefusal.MaterialUnsupported, 1 }, { "unknownShaderBit", M2AppearanceRefusal.CombinerUnsupported, 1 },
        { "envTransformOnUv", M2AppearanceRefusal.TextureTransform, 1 }, { "envCoordinate", M2AppearanceRefusal.MaterialUnsupported, 1 },
    };

    [Theory, MemberData(nameof(Refusals))]
    public async Task The_first_refused_batch_in_skin_order_names_the_refusal(string kind, M2AppearanceRefusal reason, int batch)
    {
        var ok = new Batch(); var spec = Simple(); Batch second = new(Section: 1);
        switch (kind)
        {
            case "multi": second = second with { TextureCount = 3 }; spec = spec with { Combos = [0, 0, 0], TransformLookup = [ushort.MaxValue, ushort.MaxValue, ushort.MaxValue] }; break;
            case "blend": spec = spec with { Materials = [(0, 0), (0, 8)] }; second = second with { Material = 1 }; break;
            case "animatedWeight": spec = spec with { WeightLookup = [0, 1], WeightKeys = [[[32767]], [[32767, 0]]] }; second = second with { Weight = 1 }; break;
            case "animWeight": spec = spec with { WeightLookup = [0, 1], WeightKeys = [[[32767]], [[], [32767]]], SequenceFlags = [0x20, 0] }; second = second with { Weight = 1 }; break;
            case "transform": spec = spec with { TransformLookup = [ushort.MaxValue, 0], TransformTracks = 1 }; second = second with { Transform = 1 }; break;
            case "twoStageFlagOnOne": second = second with { Flags = 0x80 }; break;
            case "unknownShaderBit": second = second with { Shader = 0x0100 }; break;
            case "envTransformOnUv":
                // Stage 0 samples UV0 and names a real transform track: that animates the surface, so it still refuses.
                spec = TwoStage() with { TransformLookup = [0, ushort.MaxValue, ushort.MaxValue], TransformTracks = 1 }; ok = new(Shader: 0x8000, Flags: 0x90, TextureCount: 2, Transform: 1);
                second = new(Section: 1, Shader: 0x8000, Flags: 0x80, TextureCount: 2, Transform: 0); break;
            case "envCoordinate":
                spec = TwoStage() with { Coordinates = [0, ushort.MaxValue, 0, 0] }; ok = new(Shader: 0x8002, Flags: 0x80, TextureCount: 2);
                second = new(Section: 1, Shader: 0x8002, Flags: 0x80, TextureCount: 2, Coordinate: 2); break;
            case "combiner": second = second with { Shader = 0x8000 }; break;
            case "layered": second = second with { Section = 0 }; break;
            case "type14": spec = spec with { Textures = [new(11, 3), new(14)], Combos = [0, 1] }; second = second with { Combo = 1 }; break;
            case "renderFlag": spec = spec with { Materials = [(0, 0), (0x40, 0)] }; second = second with { Material = 1 }; break;
            case "uv1": spec = spec with { Coordinates = [0, 1] }; second = second with { Coordinate = 1 }; break;
        }
        var (_, _, plan) = await PlanAsync(spec, [ok, second], sections: 2);
        Assert.Equal(reason, plan.Decline!.Reason); Assert.Equal(batch, plan.Decline.BatchOrdinal);
        Assert.Empty(plan.Draws); Assert.Empty(plan.Textures);
    }

    /// <summary>A type 11 body and a TXID env map: combos [0, 1], one static weight, no transforms.</summary>
    private static Model TwoStage() => new([new(11, 0), new(0, 0, 900)], [(0, 0), (0x4, 1)], [0, 1], [0], [ushort.MaxValue, ushort.MaxValue], [[[32767]]], [0x20]);

    public static TheoryData<ushort, byte, M2Combiner, M2StageCoordinate> TwoStageCombiners => new()
    {
        { 0x8000, 0x90, M2Combiner.OpaqueMod2xNAAlpha, M2StageCoordinate.Env }, { 0x8002, 0x80, M2Combiner.OpaqueAddAlphaAlpha, M2StageCoordinate.Env },
        { 0x8017, 0x80, M2Combiner.OpaqueAlpha, M2StageCoordinate.Uv0 },
    };

    [Theory, MemberData(nameof(TwoStageCombiners))]
    public async Task Two_stage_combiners_bind_both_textures_with_their_coordinates(ushort shader, byte flags, M2Combiner combiner, M2StageCoordinate second)
    {
        var (_, _, plan) = await PlanAsync(TwoStage(), [new(Shader: shader, Flags: flags, TextureCount: 2)]);
        Assert.Null(plan.Decline);
        Assert.Equal(new M2AppearanceDraw(0, 0, 0, false, false, true, true, combiner, M2StageCoordinate.Uv0, 1, second), Assert.Single(plan.Draws));
        Assert.Equal([new(0, 11, M2TextureBinding.CreatureVariation, 0, 0, false, false), new M2AppearanceTexture(1, 0, M2TextureBinding.FileDataId, null, 900, false, false)], plan.Textures);
        // These combiners have no texture alpha, so blend mode 1 does not alpha key them.
        var (_, _, keyed) = await PlanAsync(TwoStage(), [new(Shader: shader, Flags: flags, TextureCount: 2, Material: 1)]);
        Assert.False(Assert.Single(keyed.Draws).AlphaKey); Assert.True(keyed.Draws[0].TwoSided);
    }

    [Fact]
    public async Task An_unsupported_combiner_is_refused_by_name()
    {
        var (_, _, plan) = await PlanAsync(TwoStage(), [new(Shader: 0x4011, Flags: 0x80, TextureCount: 2)]);
        Assert.Equal(M2AppearanceRefusal.CombinerUnsupported, plan.Decline!.Reason); Assert.Equal(0, plan.Decline.BatchOrdinal);
        Assert.Contains("Combiners_Mod_Mod with Diffuse_T1_T2 (shader 0x4011, 2 textures)", plan.Decline.Message);
        var (_, _, table) = await PlanAsync(TwoStage(), [new(Shader: 0x8001, Flags: 0x80, TextureCount: 2)]);
        Assert.Contains("Combiners_Opaque_AddAlpha with Diffuse_T1_Env (shader 0x8001", table.Decline!.Message);
        var (_, _, past) = await PlanAsync(TwoStage(), [new(Shader: 0x8100, Flags: 0x80, TextureCount: 2)]);
        Assert.Contains("shader 0x8100 (no named combiner)", past.Decline!.Message);
    }

    [Fact]
    public async Task Material_refusals_name_the_exact_value_outside_the_profile()
    {
        // Live Dark Phoenix Hatchling (#75): one alpha-key batch whose material carries render flags 0x81.
        var (_, _, flag) = await PlanAsync(Simple(blend: 1) with { Materials = [(0x81, 1)] }, [new(Shader: 0x10)]);
        Assert.Equal(M2AppearanceRefusal.MaterialUnsupported, flag.Decline!.Reason); Assert.Contains("render flags 0x80", flag.Decline.Message);
        var (_, _, batch) = await PlanAsync(Simple(), [new(Flags: 0x20)]);
        Assert.Contains("batch flags 0x20", batch.Decline!.Message);
        var (_, _, layer) = await PlanAsync(Simple(), [new(Layer: 2)]);
        Assert.Contains("material layer 2", layer.Decline!.Message);
        var (_, _, blended) = await PlanAsync(Simple(blend: 4), [new()]);
        Assert.Equal(M2AppearanceRefusal.BlendUnsupported, blended.Decline!.Reason); Assert.Contains("blends with mode 4", blended.Decline.Message);
    }

    [Fact]
    public async Task Blended_layers_are_counted_not_drawn_and_never_layer_conflict()
    {
        // Batch 1 (alpha blend) and batch 2 (add) lie over sections 0 and 1; batch 0 alone is drawn.
        var spec = Simple() with { Materials = [(0, 0), (0x15, 2), (0x10, 4)] };
        var (_, _, plan) = await PlanAsync(spec, [new(), new(Section: 0, Material: 1, Shader: 0x4014, Flags: 0x80, TextureCount: 2), new(Section: 1, Material: 2)], sections: 2);
        Assert.Null(plan.Decline); Assert.Equal(0, Assert.Single(plan.Draws).BatchOrdinal);
        Assert.Equal(new M2AppearanceEffects(0, 0, 0, 0, 0, 2, 0), plan.Effects);
        var (_, _, all) = await PlanAsync(spec, [new(Material: 1), new(Section: 1, Material: 2)], sections: 2);
        Assert.Equal(M2AppearanceRefusal.BlendUnsupported, all.Decline!.Reason); Assert.Equal(0, all.Decline.BatchOrdinal);
    }

    [Fact]
    public async Task Env_stage_transforms_are_not_evaluated_and_undeclared_tracks_are_no_transform()
    {
        // Stage 1 (env) names transform track 0: drawn without its UV animation and counted.
        var animated = TwoStage() with { TransformLookup = [ushort.MaxValue, 0], TransformTracks = 1 };
        var (_, _, plan) = await PlanAsync(animated, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
        Assert.Null(plan.Decline); Assert.Equal(1, plan.Effects.EnvTransforms);
        // A lookup entry past the declared tracks (live Felhunter and Felguard) is no transform at all.
        var (_, _, undeclared) = await PlanAsync(TwoStage() with { TransformLookup = [ushort.MaxValue, 0] }, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
        Assert.Null(undeclared.Decline); Assert.Equal(0, undeclared.Effects.EnvTransforms);
        var (_, _, single) = await PlanAsync(Simple() with { TransformLookup = [3] }, [new()]);
        Assert.Null(single.Decline);
        // The second stage's lookup entry must exist.
        var (_, _, outside) = await PlanAsync(TwoStage() with { TransformLookup = [ushort.MaxValue] }, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
        Assert.Equal(M2AppearanceRefusal.MaterialUnsupported, outside.Decline!.Reason);
    }

    [Fact]
    public async Task Every_present_stage_weight_must_be_a_constant_full_opacity()
    {
        // Stage 1's entry names an animated track: the client may scale the added env term by it, so it refuses and names the env texture.
        var animated = TwoStage() with { WeightLookup = [0, 1], WeightKeys = [[[32767]], [[32767, 0]]] };
        var (_, _, plan) = await PlanAsync(animated, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
        Assert.Equal(M2AppearanceRefusal.TextureWeight, plan.Decline!.Reason); Assert.Equal(1, plan.Decline.TextureOrdinal);
        // A missing track is not a proof either.
        var (_, _, missing) = await PlanAsync(TwoStage() with { WeightLookup = [0, 5] }, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
        Assert.Equal(M2AppearanceRefusal.TextureWeight, missing.Decline!.Reason);
        // Proven, no channel, or no entry at all (the live one-entry lookup) draw.
        foreach (ushort[] lookup in new ushort[][] { [0, 0], [0, ushort.MaxValue], [0] })
        {
            var (_, _, ok) = await PlanAsync(TwoStage() with { WeightLookup = lookup }, [new(Shader: 0x8002, Flags: 0x80, TextureCount: 2)]);
            Assert.Null(ok.Decline);
        }
    }

    [Fact]
    public async Task A_present_coordinate_lookup_must_agree_with_every_stage()
    {
        var (_, _, env) = await PlanAsync(TwoStage() with { Coordinates = [0, ushort.MaxValue] }, [new(Shader: 0x8000, Flags: 0x90, TextureCount: 2)]);
        Assert.Null(env.Decline);
        var (_, _, uv) = await PlanAsync(TwoStage() with { Coordinates = [0, 0] }, [new(Shader: 0x8000, Flags: 0x90, TextureCount: 2)]);
        Assert.Equal(M2AppearanceRefusal.MaterialUnsupported, uv.Decline!.Reason); Assert.Equal(1, uv.Decline.TextureOrdinal);
        var (_, _, t1t1) = await PlanAsync(TwoStage() with { Coordinates = [0, 0] }, [new(Shader: 0x8017, Flags: 0x80, TextureCount: 2)]);
        Assert.Null(t1t1.Decline);
    }

    [Fact]
    public async Task Weight_tracks_without_keys_have_the_default_full_opacity()
    {
        var (_, _, plan) = await PlanAsync(Simple() with { WeightKeys = [[[], []]], SequenceFlags = [0x20, 0] }, [new()]);
        Assert.Null(plan.Decline);
        var (_, _, missing) = await PlanAsync(Simple() with { WeightLookup = [3] }, [new()]);
        Assert.Equal(M2AppearanceRefusal.TextureWeight, missing.Decline!.Reason);
    }

    [Fact]
    public async Task Weight_tracks_share_one_slice_budget_and_each_slice_array_is_read_once()
    {
        // 8,192 tracks sharing one 8,192-slice array: read once, in bulk, not 67M small reads.
        var shared = Simple() with { WeightKeys = [], WeightStress = (8192, 8192, true) };
        var bytes = ModelBytes(shared); using var parse = new MemoryStream(bytes);
        var model = await M2ModelReader.ReadAsync(parse, Limits);
        await using var counted = new CountingStream(bytes);
        var facts = await M2Appearance.ReadModelAsync(counted, model);
        Assert.True(facts.StaticWeights.All(proven => proven)); Assert.InRange(counted.Reads, 1, 200);
        // Nine tracks with their own 8,192-slice arrays exceed the 65,536 shared slice budget: the ninth stays unproven.
        var distinct = Simple() with { WeightKeys = [], WeightStress = (9, 8192, false) };
        bytes = ModelBytes(distinct); using var second = new MemoryStream(bytes); model = await M2ModelReader.ReadAsync(second, Limits);
        second.Position = 0; facts = await M2Appearance.ReadModelAsync(second, model);
        Assert.True(facts.StaticWeights.Take(8).All(proven => proven)); Assert.False(facts.StaticWeights[8]);
        var (_, _, plan) = await PlanAsync(distinct with { WeightLookup = [8] }, [new()]);
        Assert.Equal(M2AppearanceRefusal.TextureWeight, plan.Decline!.Reason);
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal int Reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { Reads++; return base.ReadAsync(buffer, token); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { Reads++; return base.ReadAsync(buffer, offset, count, token); }
    }

    [Fact]
    public async Task An_empty_coordinate_lookup_defers_to_the_UV0_shader()
    {
        var (_, _, plan) = await PlanAsync(Simple(), [new(Coordinate: ushort.MaxValue)]);
        Assert.Null(plan.Decline);
    }

    [Fact]
    public async Task No_drawn_triangle_and_foreign_skins_are_refused()
    {
        var (_, _, empty) = await PlanAsync(Simple(), []);
        Assert.Equal(M2AppearanceRefusal.MaterialUnsupported, empty.Decline!.Reason); Assert.Null(empty.Decline.BatchOrdinal);
        var (model, _, _) = await PlanAsync(Simple(), [new()]); var (_, other, _) = await PlanAsync(Simple(), [new()]);
        using var a = new MemoryStream(ModelBytes(Simple())); using var b = new MemoryStream(SkinBytes([new()]));
        var facts = await M2Appearance.ReadModelAsync(a, model);
        Assert.Equal("MODEL_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => M2Appearance.AdmitAsync(b, other, facts))).Code);
    }

    [Fact]
    public async Task Cancellation_stops_both_phases()
    {
        using var source = new CancellationTokenSource(); await source.CancelAsync();
        var (model, skin, _) = await PlanAsync(Simple(), [new()]);
        using var a = new MemoryStream(ModelBytes(Simple()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => M2Appearance.ReadModelAsync(a, model, source.Token));
    }
}
