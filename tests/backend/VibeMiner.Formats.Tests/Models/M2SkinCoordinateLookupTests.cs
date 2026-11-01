using System.Buffers.Binary;
using VibeMiner.Formats.Models;
using Xunit;
using static VibeMiner.Formats.Tests.Models.ModelFixtures;

namespace VibeMiner.Formats.Tests.Models;

/// <summary>D401: modern chunked models ship an empty texture-coordinate lookup (MD20 0x88), and the shader picks the
/// coordinates. The strict SKIN reader skips only that range check, only for chunked 272/274 with an empty lookup.</summary>
public sealed class M2SkinCoordinateLookupTests
{
    private const string ComboRange="A SKIN batch combo range is outside its model lookup.";

    [Theory]
    [InlineData(272u,0)][InlineData(274u,0)][InlineData(272u,5)][InlineData(274u,5)]
    public async Task Empty_lookup_on_chunked_272_and_274_is_admitted_with_any_coordinate_index(uint version,ushort coordinateIndex)
    {
        var model=await ReadModel(Modern(EmptyCoordinates(Body(version))));
        var skin=await ReadSkin(Batch(Skin(version),18,coordinateIndex),model);
        Assert.Empty(model.TextureCoordinates);var batch=Assert.Single(skin.Batches);
        Assert.Equal(coordinateIndex,batch.TextureCoordinateComboIndex);Assert.Equal((ushort)1,batch.TextureCount);
        Assert.Equal(new uint[]{2,0,1},M2StaticGeometry.Create(model,skin).Indices);
    }

    [Theory]
    [InlineData(272u)][InlineData(274u)]
    public async Task Empty_lookup_keeps_every_other_combo_range_check(uint version)
    {
        var model=await ReadModel(Modern(EmptyCoordinates(Body(version))));
        foreach(var at in new[]{16,20,22})
        {
            var error=await Assert.ThrowsAsync<ModelFormatException>(()=>ReadSkin(Batch(Skin(version),at,2),model));
            Assert.Equal("MODEL_INVALID",error.Code);Assert.Equal(ComboRange,error.Message);
        }
    }

    [Fact]
    public async Task Empty_lookup_on_unchunked_264_still_refuses()
    {
        var model=await ReadModel(EmptyCoordinates(Body(264)));Assert.Empty(model.TextureCoordinates);
        var error=await Assert.ThrowsAsync<ModelFormatException>(()=>ReadSkin(Skin(264),model));
        Assert.Equal("MODEL_INVALID",error.Code);Assert.Equal(ComboRange,error.Message);
    }

    [Theory]
    [InlineData(264u)][InlineData(272u)][InlineData(274u)]
    public async Task A_non_empty_lookup_that_is_too_short_still_refuses(uint version)
    {
        var model=await ReadModel(Model(version));Assert.Single(model.TextureCoordinates);
        await ReadSkin(Skin(version),model);
        var error=await Assert.ThrowsAsync<ModelFormatException>(()=>ReadSkin(Batch(Skin(version),18,1),model));
        Assert.Equal("MODEL_INVALID",error.Code);Assert.Equal(ComboRange,error.Message);
    }

    [Theory]
    [InlineData(272u)][InlineData(274u)]
    public async Task Opaque_materials_refuse_an_empty_lookup_instead_of_reading_it_as_UV0(uint version)
    {
        var model=await ReadModel(Modern(EmptyCoordinates(Body(version))));var skin=await ReadSkin(Skin(version),model);
        var error=Assert.Throws<ModelFormatException>(()=>M2StaticMaterials.AdmitOpaque(model,skin));
        Assert.Equal("MODEL_MATERIAL_UNSUPPORTED",error.Code);
        Assert.Equal("Batch 0 has no texture-coordinate lookup entry; this profile needs one that says UV0.",error.Message);
        // The same pair with its one-entry UV0 lookup is admitted, so the refusal is the lookup alone.
        var withLookup=await ReadModel(Model(version));Assert.Single(M2StaticMaterials.AdmitOpaque(withLookup,await ReadSkin(Skin(version),withLookup)).Draws);
    }

    private static byte[] EmptyCoordinates(byte[] body){BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(136),0);return body;}
    // Writes one u16 field of the first batch (16 texture combo, 18 coordinate, 20 weight, 22 transform lookup index).
    private static byte[] Batch(byte[] skin,int field,ushort value)
    {var at=(int)BinaryPrimitives.ReadUInt32LittleEndian(skin.AsSpan(40));BinaryPrimitives.WriteUInt16LittleEndian(skin.AsSpan(at+field),value);return skin;}
    private static async Task<M2Model> ReadModel(byte[] bytes){using var input=new MemoryStream(bytes);return await M2ModelReader.ReadAsync(input);}
    private static async Task<M2Skin> ReadSkin(byte[] bytes,M2Model model){using var input=new MemoryStream(bytes);return await M2SkinReader.ReadAsync(input,model);}
}
