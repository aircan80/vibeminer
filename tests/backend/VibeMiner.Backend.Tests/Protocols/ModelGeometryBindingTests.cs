using System.Buffers.Binary;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ModelGeometryBindingTests
{
    // Same authored original receipt shapes as the independently frozen VMG1 byte golden.
    private static GeometrySourceBinding Binding => new(
        new(new string('1', 32), new string('2', 32), new string('3', 64), 100, 516, "content-blte-v1"),
        new(new string('4', 32), new string('5', 32), new string('6', 64), 80, 230, "content-blte-v1"), 800, 900, 0);
    [Fact]
    public void Prebuild_binding_matches_frozen_artifact_binding_and_each_source_identity_matters()
    {
        var expected = "26d091dddb2aae81502176c74454a201d3c55e29150aa0f3d3311d19a08cc956";
        Assert.Equal(expected, ModelGeometryArtifacts.ComputeBindingSha256(Binding));
        Assert.Equal(expected, ModelGeometryArtifacts.ComputeBindingSha256(Binding with { }, default));
        Assert.NotEqual(expected, ModelGeometryArtifacts.ComputeBindingSha256(Binding with { ModelFileDataId = 801 }));
        Assert.NotEqual(expected, ModelGeometryArtifacts.ComputeBindingSha256(Binding with { Skin = Binding.Skin with { EncodedSha256 = new('7', 64) } }));
    }
    [Theory]
    [InlineData("id")]
    [InlineData("ordinal")]
    [InlineData("key")]
    [InlineData("projection")]
    [InlineData("size")]
    public void Malformed_or_partial_bindings_cannot_reserve_an_identity(string kind)
    {
        var b = Binding; b = kind switch
        {
            "id" => b with { ModelFileDataId = 0 },
            "ordinal" => b with { MainSkinOrdinal = 1 },
            "key" => b with { Model = b.Model with { ContentKey = "bad" } },
            "projection" => b with { Skin = b.Skin with { VerifierRevision = "content-blte-projection-v1" } },
            _ => b with { Skin = b.Skin with { DecodedBytes = 8388609 } }
        };
        Assert.Equal("GEOMETRY_ARTIFACT_" + (kind == "size" ? "LIMIT" : "INVALID"),
            Assert.Throws<DiscoveryException>(() => ModelGeometryArtifacts.ComputeBindingSha256(b)).Code);
    }
    [Fact]
    public void Canceled_binding_computation_is_not_a_reservation()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelGeometryArtifacts.ComputeBindingSha256(Binding, stop.Token));
    }
    [Fact]
    public async Task Prebuild_digest_equals_the_binding_in_a_complete_authored_artifact()
    {
        var body = new byte[496]; "MD20"u8.CopyTo(body); Put(body, 4, 274); Put(body, 60, 4); Put(body, 64, 304); Put(body, 68, 1);
        var modelBytes = new byte[516]; "MD21"u8.CopyTo(modelBytes); Put(modelBytes, 4, 496); body.CopyTo(modelBytes, 8);
        "SFID"u8.CopyTo(modelBytes.AsSpan(504)); Put(modelBytes, 508, 4); Put(modelBytes, 512, 900);
        using var modelSource = new MemoryStream(modelBytes); var model = await M2ModelReader.ReadAsync(modelSource);
        var skinBytes = new byte[230]; "SKIN"u8.CopyTo(skinBytes); Put(skinBytes, 4, 3); Put(skinBytes, 8, 56);
        Put(skinBytes, 12, 6); Put(skinBytes, 16, 62); Put(skinBytes, 20, 3); Put(skinBytes, 24, 74); Put(skinBytes, 28, 3); Put(skinBytes, 32, 86);
        for (var i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(56 + i * 2), (ushort)i);
        for (var i = 0; i < 6; i++) BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(62 + i * 2), (ushort)(i % 3));
        for (var i = 0; i < 3; i++)
        { BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(86 + i * 48 + 6), 3); BinaryPrimitives.WriteUInt16LittleEndian(skinBytes.AsSpan(86 + i * 48 + 10), (ushort)(i == 2 ? 0 : 3)); }
        using var skinSource = new MemoryStream(skinBytes); var skin = await M2GeometrySkinReader.ReadAsync(skinSource, model);
        using var output = new MemoryStream(); var artifact = await ModelGeometryArtifacts.BuildAsync(skin, Binding, output);
        Assert.Equal(ModelGeometryArtifacts.ComputeBindingSha256(Binding), artifact.BindingSha256);
        output.Position = 0; await ModelGeometryArtifacts.VerifyAsync(output, artifact);
    }
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
