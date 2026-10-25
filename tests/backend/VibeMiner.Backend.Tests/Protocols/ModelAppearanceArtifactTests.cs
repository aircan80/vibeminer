using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;
using static VibeMiner.Backend.Tests.AppearanceModels;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ModelAppearanceArtifactTests
{
    private static readonly IGeometryArtifactWorkBudget Budget = new Free();
    private static AppearanceTextureInput Texture(uint id = 779, int width = 2, int height = 2, bool wrapU = true) =>
        new(id, new(new('a', 32), new('b', 32), new('c', 64), 100, 200, "content-blte-v1"), width, height, 0, width, height, Blp2Encoding.Bc1, wrapU, false);
    private static AppearanceBinding Binding(params AppearanceTextureInput[] textures) => new(new('d', 64), 123, [0, 779, 0, 0, 0], textures);

    private static readonly AppearanceDrawInput Keyed = new(0, 0, 0, 0, true, false, true, false, M2Combiner.Mod);
    private static readonly AppearanceDrawInput Env = new(0, 0, 0, 0, false, false, true, true, M2Combiner.OpaqueAddAlphaAlpha, M2StageCoordinate.Uv0, 1, 1, M2StageCoordinate.Env);

    private static async Task<(M2GeometrySkin Skin, AppearanceArtifact Artifact, byte[] Bytes)> BuildAsync(AppearanceBinding? binding = null, byte[][]? texels = null,
        AppearanceDrawInput? draw = null)
    {
        var (_, skin, _) = await M2AppearanceTests.PlanAsync(Simple(), [new()]);
        binding ??= Binding(Texture()); texels ??= [Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()];
        using var output = new MemoryStream();
        var artifact = await ModelAppearanceArtifacts.BuildAsync(skin, [draw ?? Keyed], binding, texels, output, Budget, default);
        return (skin, artifact, output.ToArray());
    }

    [Fact]
    public async Task Build_writes_exact_regions_and_verify_accepts_only_the_identical_bytes()
    {
        var (_, artifact, bytes) = await BuildAsync(); var m = artifact.Manifest;
        Assert.Equal("VMA1"u8.ToArray(), bytes[..4]); Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(160 + 3 * 20 + 3 * 4 + 48 + 32 + 16, bytes.Length); Assert.Equal(new GeometryBufferRegion(196, 0), m.Normals);
        Assert.Equal((3, 3, 1, 1), (m.VertexCount, m.IndexCount, m.DrawCount, m.TextureCount)); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256);
        Assert.Equal(new uint[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Indices.Offset + i * 4))).ToArray());
        Assert.Equal(1u | 8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Draws.Offset + 44)));
        Assert.Equal((uint)M2Combiner.Mod, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Draws.Offset + 20)));
        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Draws.Offset + 32))); Assert.Equal(0xFF, bytes[m.Draws.Offset + 41]);
        Assert.Equal(1, bytes[m.Textures.Offset + 26]); Assert.Equal(2, bytes[m.Textures.Offset + 25]);
        Assert.Equal([0f, 1f], [BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(m.Uvs.Offset)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(m.Uvs.Offset + 4))]);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), bytes[m.Texels.Offset..]);
        Assert.Equal([0f, 0f, 0f], m.Bounds.Min); Assert.Equal([1f, 0f, 1f], m.Bounds.Max);
        await ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(bytes), artifact, Budget, default);
        foreach (var at in new[] { 20, m.Positions.Offset, m.Uvs.Offset + 1, m.Indices.Offset, m.Draws.Offset + 4, m.Draws.Offset + 20, m.Draws.Offset + 40, m.Textures.Offset + 8, bytes.Length - 1 })
        {
            var copy = bytes.ToArray(); copy[at] ^= 0x01;
            Assert.Equal("GEOMETRY_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(copy), artifact, Budget, default))).Code);
        }
        await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream([.. bytes, 0]), artifact, Budget, default));
        var other = artifact with { Manifest = m with { DisplayId = 124 } };
        await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(bytes), other, Budget, default));
    }

    [Fact]
    public async Task An_env_stage_writes_normals_and_both_stage_slots()
    {
        var binding = Binding(Texture(), Texture(id: 900));
        var (_, artifact, bytes) = await BuildAsync(binding, [new byte[16], new byte[16]], Env); var m = artifact.Manifest;
        Assert.Equal(new GeometryBufferRegion(196, 36), m.Normals); Assert.Equal(232, m.Uvs.Offset);
        Assert.Equal([0f, 0f, 1f], Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(m.Normals.Offset + i * 4))));
        var r = m.Draws.Offset;
        Assert.Equal(((uint)M2Combiner.OpaqueAddAlphaAlpha, 0u, 0u, 1u, 1u), (U(bytes, r + 20), U(bytes, r + 24), U(bytes, r + 28), U(bytes, r + 32), U(bytes, r + 36)));
        Assert.Equal((0, 2), (bytes[r + 40], bytes[r + 41]));
        await ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(bytes), artifact, Budget, default);
        foreach (var (at, value) in new[] { (m.Normals.Offset + 3, (byte)0xFF), (r + 41, (byte)0), (r + 41, (byte)0xFF), (r + 20, (byte)1) })
        {
            var copy = bytes.ToArray(); copy[at] = value;
            await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(copy), artifact, Budget, default));
        }
    }

    public static TheoryData<string> InconsistentDraws => new() { "alphaOnOpaque", "missingSecond", "wrongCoordinate", "secondOnOne", "unknownCombiner" };
    [Theory, MemberData(nameof(InconsistentDraws))]
    public async Task Draws_must_match_their_combiner(string kind)
    {
        var draw = kind switch
        {
            "alphaOnOpaque" => Keyed with { Combiner = M2Combiner.Opaque },
            "missingSecond" => Env with { SecondSlot = null },
            "wrongCoordinate" => Env with { SecondCoordinate = M2StageCoordinate.Uv0 },
            "secondOnOne" => Keyed with { SecondTextureOrdinal = 1, SecondSlot = 0, SecondCoordinate = M2StageCoordinate.Uv0 },
            _ => Keyed with { Combiner = (M2Combiner)9 },
        };
        Assert.Equal("GEOMETRY_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => BuildAsync(Binding(Texture(), Texture(id: 900)), [new byte[16], new byte[16]], draw))).Code);
    }
    private static uint U(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));

    [Fact]
    public async Task The_binding_is_keyed_by_display_and_texture_sources()
    {
        var (_, a, _) = await BuildAsync(); var (_, b, _) = await BuildAsync(Binding(Texture()) with { DisplayId = 124 });
        var (_, c, _) = await BuildAsync(Binding(Texture(wrapU: false)));
        Assert.NotEqual(a.BindingSha256, b.BindingSha256); Assert.NotEqual(a.BindingSha256, c.BindingSha256); Assert.NotEqual(a.ArtifactId, b.ArtifactId);
    }

    private static IReadOnlyList<Blp2Mip> Mips(int side, int levels) =>
        Enumerable.Range(0, levels).Select(l => new Blp2Mip(l, Math.Max(1, side >> l), Math.Max(1, side >> l), 148, 1)).ToArray();

    [Fact]
    public void Mips_step_down_largest_first_until_the_slots_fit_and_refuse_only_when_the_smallest_do_not()
    {
        // 2,048 starts at its 1,024 level. Over 8 MiB, the largest current image steps down first (lowest index on ties).
        Assert.Equal([2, 0, 0], ModelAppearanceArtifacts.ChooseMips([Mips(2048, 3), Mips(1024, 2), Mips(512, 1)], [1, 1, 1], out var none)!);
        Assert.Equal(-1, none);
        Assert.Equal([1, 1, 0], ModelAppearanceArtifacts.ChooseMips([Mips(1024, 2), Mips(1024, 2), Mips(1024, 2)], [1, 1, 1], out _)!);
        Assert.Equal([1, 1, 1], ModelAppearanceArtifacts.ChooseMips([Mips(1024, 2), Mips(1024, 2), Mips(1024, 2)], [1, 1, 1], out _, budget: 5L << 20)!);
        // Slot counts weigh an image shared by several wrap combinations.
        Assert.Equal([1], ModelAppearanceArtifacts.ChooseMips([Mips(1024, 2)], [3], out _)!);
        Assert.Null(ModelAppearanceArtifacts.ChooseMips([Mips(1024, 1), Mips(1024, 1), Mips(1024, 1)], [1, 1, 1], out none)); Assert.Equal(-1, none);
        Assert.Null(ModelAppearanceArtifacts.ChooseMips([Mips(512, 1), Mips(4096, 2)], [1, 1], out none)); Assert.Equal(1, none);
    }

    public static TheoryData<string> Invalid => new() { "texelLength", "duplicateSlot", "extended", "side", "missingSlot", "revision" };
    [Theory, MemberData(nameof(Invalid))]
    public async Task Invalid_inputs_are_refused_before_any_output(string kind)
    {
        var (binding, texels) = kind switch
        {
            "texelLength" => (Binding(Texture()), new[] { new byte[15] }),
            "duplicateSlot" => (Binding(Texture(), Texture()), new[] { new byte[16], new byte[16] }),
            "extended" => (Binding(Texture()) with { DisplayFields = [1, 779, 0, 0, 0] }, new[] { new byte[16] }),
            "side" => (Binding(Texture(width: 2048, height: 1)), new[] { new byte[8192] }),
            "missingSlot" => (Binding(Texture()), Array.Empty<byte[]>()),
            _ => (Binding(Texture() with { Source = Texture().Source with { VerifierRevision = "other" } }), new[] { new byte[16] }),
        };
        Assert.StartsWith("GEOMETRY_ARTIFACT_", (await Assert.ThrowsAsync<DiscoveryException>(() => BuildAsync(binding, texels))).Code);
    }

    private sealed class Free : IGeometryArtifactWorkBudget, IDisposable
    {
        public ValueTask<IDisposable> EnterAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<IDisposable>(this); }
        public void Dispose() { }
    }
}
