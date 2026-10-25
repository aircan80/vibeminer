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
        Assert.Equal([new(0, 0, 0, true, true, true, true), new M2AppearanceDraw(1, 1, 1, false, false, true, true)], plan.Draws);
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
        { "transform", M2AppearanceRefusal.TextureTransform, 1 }, { "combiner", M2AppearanceRefusal.MaterialUnsupported, 1 },
        { "layered", M2AppearanceRefusal.MaterialUnsupported, 1 }, { "type14", M2AppearanceRefusal.MaterialUnsupported, 1 },
        { "renderFlag", M2AppearanceRefusal.MaterialUnsupported, 1 }, { "uv1", M2AppearanceRefusal.MaterialUnsupported, 1 },
    };

    [Theory, MemberData(nameof(Refusals))]
    public async Task The_first_refused_batch_in_skin_order_names_the_refusal(string kind, M2AppearanceRefusal reason, int batch)
    {
        var ok = new Batch(); var spec = Simple(); Batch second = new(Section: 1);
        switch (kind)
        {
            case "multi": second = second with { TextureCount = 2 }; spec = spec with { Combos = [0, 0], TransformLookup = [ushort.MaxValue, ushort.MaxValue] }; break;
            case "blend": spec = spec with { Materials = [(0, 0), (0, 2)] }; second = second with { Material = 1 }; break;
            case "animatedWeight": spec = spec with { WeightLookup = [0, 1], WeightKeys = [[[32767]], [[32767, 0]]] }; second = second with { Weight = 1 }; break;
            case "animWeight": spec = spec with { WeightLookup = [0, 1], WeightKeys = [[[32767]], [[], [32767]]], SequenceFlags = [0x20, 0] }; second = second with { Weight = 1 }; break;
            case "transform": spec = spec with { TransformLookup = [ushort.MaxValue, 0] }; second = second with { Transform = 1 }; break;
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
