using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public static partial class CreatureGeometryValidation
{
    public const string AppearancePolicy = "creature-textured-appearance-v1";
    private static readonly string[] Encodings = ["palette", "bc1", "bc2", "bc3", "bgra8"];

    /// <summary>Owned bounded copy of an appearance outcome. Ready needs its manifest and decoded chain; every other
    /// state has no manifest, and texture-source states name the texture ordinal that stopped the chain.</summary>
    public static CreatureGeometryAppearance Snapshot(CreatureGeometryAppearance value)
    {
        Require(value is not null && value.Policy == AppearancePolicy && Enum.IsDefined(value.State));
        CreatureGeometryAppearanceDisplay? display = null;
        if (value.Display is { } d)
        {
            var variations = Entries(d.TextureVariations, 4);
            Require(variations.Length == 4 && Unsigned(d.DisplayId) && U(d.DisplayId) is > 0 and <= int.MaxValue && Signed(d.ExtendedDisplayInfoId) && variations.All(Signed));
            display = new(d.DisplayId, d.ExtendedDisplayInfoId, Array.AsReadOnly(variations));
        }
        var textures = Entries(value.Textures, 32); var ordinals = new HashSet<int>();
        for (var i = 0; i < textures.Length; i++)
        {
            var t = textures[i];
            Require(t is not null && t.Ordinal is >= 0 and < 32 && ordinals.Add(t.Ordinal) && (i == 0 || t.Ordinal > textures[i - 1].Ordinal) &&
                Enum.IsDefined(t.Binding) && Enum.IsDefined(t.State) &&
                (t.Binding == CreatureGeometryTextureBinding.Txid ? t.Type == 0 && t.Variation is null : t.Type is >= 11 and <= 13 && t.Variation == t.Type - 11) &&
                (t.FileDataId is null ? t.State == CreatureGeometryTextureState.Unresolved : Unsigned(t.FileDataId) && U(t.FileDataId) > 0 && t.State != CreatureGeometryTextureState.Unresolved) &&
                (t.State == CreatureGeometryTextureState.Decoded) == (t.Image is not null) && (t.State == CreatureGeometryTextureState.Decoded) == (t.Source is not null));
            if (t.Image is { } image)
                Require(image.Width is >= 1 and <= 1024 && image.Height is >= 1 and <= 1024 && image.MipLevel is >= 0 and <= 15 &&
                    image.BaseWidth >= image.Width && image.BaseHeight >= image.Height && image.BaseWidth <= 65535 && image.BaseHeight <= 65535 && Encodings.Contains(image.Encoding));
            if (t.Source is { } source)
                Require(Hash(source.ContentKey, 32) && Hash(source.EncodingKey, 32) && Hash(source.EncodedSha256) && source.EncodedBytes is >= 9 and <= 16 << 20 &&
                    source.DecodedBytes is >= 1 and <= 16 << 20 && source.FullVerifierRevision == "content-blte-v1");
            textures[i] = t with { Image = t.Image is null ? null : t.Image with { }, Source = t.Source is null ? null : t.Source with { } };
        }
        if (value.Effects is { } e) Require(new[] { e.Particles, e.Ribbons, e.Lights, e.ShadowBatches, e.UnbatchedSections, e.BlendedBatches, e.EnvTransforms }.All(n => n is >= 0 and <= 65536));
        if (value.Problem is { } p)
            Require(Text(p.Code, 64) && p.Code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') && Text(p.Message, 512) &&
                p.BatchOrdinal is null or (>= 0 and < 512) && p.TextureOrdinal is null or (>= 0 and < 32));
        CreatureGeometryAppearanceManifest? manifest = null;
        switch (value.State)
        {
            case CreatureGeometryAppearanceState.NotPrepared:
                Require(value.Problem is null && value.Manifest is null && textures.Length == 0); break;
            case CreatureGeometryAppearanceState.Ready:
                Require(value.Problem is null && value.Manifest is not null && display is not null && display.ExtendedDisplayInfoId == "0" && value.Effects is not null &&
                    textures.Length >= 1 && textures.All(t => t.State == CreatureGeometryTextureState.Decoded));
                manifest = Snapshot(value.Manifest);
                Require(manifest.DisplayId.ToString(CultureInfo.InvariantCulture) == display.DisplayId && manifest.TextureCount <= textures.Length); break;
            default:
                Require(value.Problem is not null && value.Manifest is null);
                if (value.State == CreatureGeometryAppearanceState.CharacterModel) Require(display is not null && display.ExtendedDisplayInfoId != "0");
                if (value.State is CreatureGeometryAppearanceState.TextureMissing or CreatureGeometryAppearanceState.TextureAmbiguous or CreatureGeometryAppearanceState.TextureProtected or
                    CreatureGeometryAppearanceState.TextureUnsupported or CreatureGeometryAppearanceState.TextureCorrupt or CreatureGeometryAppearanceState.TextureUnavailable)
                    Require(value.Problem!.TextureOrdinal is { } ordinal && textures.Any(t => t.Ordinal == ordinal));
                break;
        }
        var owned = value with
        {
            Display = display, Textures = Array.AsReadOnly(textures), Effects = value.Effects is null ? null : value.Effects with { },
            Manifest = manifest, Problem = value.Problem is null ? null : value.Problem with { }
        };
        Bound(owned); return owned;
    }

    public static CreatureGeometryAppearanceManifest Snapshot(CreatureGeometryAppearanceManifest value)
    {
        Require(value is not null && Hash(value.ArtifactId) && Hash(value.BindingSha256) && Hash(value.Sha256) && Hash(value.GeometryBindingSha256));
        Require(value.FormatVersion == 2 && value.HeaderBytes == 160 && value.CoordinatePolicy == "native-m2-position-normal-uv-winding-v1" &&
            value.VertexCount is >= 1 and <= 65536 && value.IndexCount is >= 3 and <= 131070 && value.IndexCount % 3 == 0 &&
            value.DrawCount is >= 1 and <= 512 && value.TextureCount is >= 1 and <= 32 && value.AlphaReference == 128 && value.DisplayId is > 0 and <= int.MaxValue &&
            value.MaterialPolicy == "m2-one-or-two-stage-combiners-v1" && value.TexturePolicy == "blp2-largest-mip-within-1024-rgba8-v1" &&
            value.ReaderRevision == "m2-appearance-batches-v1" && value.DecoderRevision == "blp2-rgba8-v2" && value.ArtifactRevision == "model-appearance-vma1-v2");
        Require(value.Texels is not null && value.Texels.ByteLength is >= 4 and <= 8 << 20 && value.Texels.ByteLength % 4 == 0);
        // Normals are present (one per vertex) only when a drawn stage samples an env map; otherwise the region is empty.
        Require(value.Normals is not null && (value.Normals.ByteLength == 0 || value.Normals.ByteLength == value.VertexCount * 12));
        var positions = new CreatureGeometryBufferRegion(160, value.VertexCount * 12);
        var normals = new CreatureGeometryBufferRegion(positions.Offset + positions.ByteLength, value.Normals!.ByteLength);
        var uvs = new CreatureGeometryBufferRegion(normals.Offset + normals.ByteLength, value.VertexCount * 8);
        var indices = new CreatureGeometryBufferRegion(uvs.Offset + uvs.ByteLength, value.IndexCount * 4);
        var draws = new CreatureGeometryBufferRegion(indices.Offset + indices.ByteLength, value.DrawCount * 48);
        var textures = new CreatureGeometryBufferRegion(draws.Offset + draws.ByteLength, value.TextureCount * 32);
        var texels = new CreatureGeometryBufferRegion(textures.Offset + textures.ByteLength, value.Texels.ByteLength);
        Require(value.Positions == positions && value.Normals == normals && value.Uvs == uvs && value.Indices == indices && value.Draws == draws && value.Textures == textures &&
            value.Texels == texels && value.ByteLength == (long)texels.Offset + texels.ByteLength && value.ByteLength <= 10 << 20);
        Require(value.Bounds is not null);
        var min = Entries(value.Bounds.Min, 3); var max = Entries(value.Bounds.Max, 3);
        Require(min.Length == 3 && max.Length == 3);
        for (var i = 0; i < 3; i++) Require(float.IsFinite(min[i]) && float.IsFinite(max[i]) && min[i] <= max[i] &&
            (min[i] != 0 || BitConverter.SingleToInt32Bits(min[i]) == 0) && (max[i] != 0 || BitConverter.SingleToInt32Bits(max[i]) == 0));
        return value with { Positions = positions, Normals = normals, Uvs = uvs, Indices = indices, Draws = draws, Textures = textures, Texels = texels,
            Bounds = new(Array.AsReadOnly(min), Array.AsReadOnly(max)) };
    }
    private static bool Signed(string? value) => value is { Length: >= 1 and <= 11 } && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) &&
        n.ToString(CultureInfo.InvariantCulture) == value;
}
