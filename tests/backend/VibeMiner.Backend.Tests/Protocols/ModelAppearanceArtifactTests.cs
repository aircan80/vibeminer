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

    private static async Task<(M2GeometrySkin Skin, AppearanceArtifact Artifact, byte[] Bytes)> BuildAsync(AppearanceBinding? binding = null, byte[][]? texels = null)
    {
        var (_, skin, _) = await M2AppearanceTests.PlanAsync(Simple(), [new()]);
        binding ??= Binding(Texture()); texels ??= [Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()];
        using var output = new MemoryStream();
        var artifact = await ModelAppearanceArtifacts.BuildAsync(skin, [new(0, 0, 0, 0, true, false, true, false)], binding, texels, output, Budget, default);
        return (skin, artifact, output.ToArray());
    }

    [Fact]
    public async Task Build_writes_exact_regions_and_verify_accepts_only_the_identical_bytes()
    {
        var (_, artifact, bytes) = await BuildAsync(); var m = artifact.Manifest;
        Assert.Equal("VMA1"u8.ToArray(), bytes[..4]); Assert.Equal(160 + 3 * 20 + 3 * 4 + 32 + 32 + 16, bytes.Length);
        Assert.Equal((3, 3, 1, 1), (m.VertexCount, m.IndexCount, m.DrawCount, m.TextureCount)); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), artifact.Sha256);
        Assert.Equal(new uint[] { 0, 1, 2 }, Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Indices.Offset + i * 4))).ToArray());
        Assert.Equal(1u | 8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(m.Draws.Offset + 28)));
        Assert.Equal(1, bytes[m.Textures.Offset + 26]); Assert.Equal(2, bytes[m.Textures.Offset + 25]);
        Assert.Equal([0f, 1f], [BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(m.Uvs.Offset)), BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(m.Uvs.Offset + 4))]);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), bytes[m.Texels.Offset..]);
        Assert.Equal([0f, 0f, 0f], m.Bounds.Min); Assert.Equal([1f, 0f, 1f], m.Bounds.Max);
        await ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(bytes), artifact, Budget, default);
        foreach (var at in new[] { 20, m.Positions.Offset, m.Uvs.Offset + 1, m.Indices.Offset, m.Draws.Offset + 4, m.Textures.Offset + 8, bytes.Length - 1 })
        {
            var copy = bytes.ToArray(); copy[at] ^= 0x01;
            Assert.Equal("GEOMETRY_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(copy), artifact, Budget, default))).Code);
        }
        await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream([.. bytes, 0]), artifact, Budget, default));
        var other = artifact with { Manifest = m with { DisplayId = 124 } };
        await Assert.ThrowsAsync<DiscoveryException>(() => ModelAppearanceArtifacts.VerifyAsync(new MemoryStream(bytes), other, Budget, default));
    }

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
