using System.Globalization;

namespace VibeMiner.Formats.Wow;

/// <summary>How a drawn batch combines its texture stages. The numeric values are the VMA1 v2 combiner codes.</summary>
public enum M2Combiner
{
    /// <summary>Combiners_Opaque: colour = T0.</summary>
    Opaque = 1,
    /// <summary>Combiners_Mod: colour = T0, alpha = T0.a.</summary>
    Mod = 2,
    /// <summary>Combiners_Opaque_Mod2xNA_Alpha: colour = mix(T0 × T1 × 2, T0, T0.a).</summary>
    OpaqueMod2xNAAlpha = 3,
    /// <summary>Combiners_Opaque_AddAlpha_Alpha: colour = T0 + T1 × T1.a × (1 − T0.a).</summary>
    OpaqueAddAlphaAlpha = 4,
    /// <summary>Combiners_Opaque_Alpha: colour = mix(T0, T1, T1.a).</summary>
    OpaqueAlpha = 5,
}
/// <summary>Where a stage takes its texture coordinates. The numeric values are the VMA1 v2 coordinate codes.</summary>
public enum M2StageCoordinate { Uv0 = 0, Uv1 = 1, Env = 2 }
/// <summary>A supported combiner: the colour formula and each stage's coordinate source.</summary>
public sealed record M2CombinerProfile(M2Combiner Combiner, string PixelShader, string VertexShader, IReadOnlyList<M2StageCoordinate> Stages, bool TextureAlpha);

/// <summary>Names the pixel and vertex shader a batch's (texture count, shader ID) selects and admits the supported
/// combiners. Shader names and the selection rules are layout facts from the pinned wow.export ShaderMapper.js
/// (1b625d0); nothing was copied. Only the names are used here, never the remaining table entries' behaviour.</summary>
public static class M2Combiners
{
    // Table selected by shader IDs with bit 0x8000: index = shader & 0x7FFF. (pixel shader, vertex shader).
    private static readonly (string Ps, string Vs)[] Effects =
    [
        ("Combiners_Opaque_Mod2xNA_Alpha", "Diffuse_T1_Env"), ("Combiners_Opaque_AddAlpha", "Diffuse_T1_Env"),
        ("Combiners_Opaque_AddAlpha_Alpha", "Diffuse_T1_Env"), ("Combiners_Opaque_Mod2xNA_Alpha_Add", "Diffuse_T1_Env_T1"),
        ("Combiners_Mod_AddAlpha", "Diffuse_T1_Env"), ("Combiners_Opaque_AddAlpha", "Diffuse_T1_T1"),
        ("Combiners_Mod_AddAlpha", "Diffuse_T1_T1"), ("Combiners_Mod_AddAlpha_Alpha", "Diffuse_T1_Env"),
        ("Combiners_Opaque_Alpha_Alpha", "Diffuse_T1_Env"), ("Combiners_Opaque_Mod2xNA_Alpha_3s", "Diffuse_T1_Env_T1"),
        ("Combiners_Opaque_AddAlpha_Wgt", "Diffuse_T1_T1"), ("Combiners_Mod_Add_Alpha", "Diffuse_T1_Env"),
        ("Combiners_Opaque_ModNA_Alpha", "Diffuse_T1_Env"), ("Combiners_Mod_AddAlpha_Wgt", "Diffuse_T1_Env"),
        ("Combiners_Mod_AddAlpha_Wgt", "Diffuse_T1_T1"), ("Combiners_Opaque_AddAlpha_Wgt", "Diffuse_T1_T2"),
        ("Combiners_Opaque_Mod_Add_Wgt", "Diffuse_T1_Env"), ("Combiners_Opaque_Mod2xNA_Alpha_UnshAlpha", "Diffuse_T1_Env_T1"),
        ("Combiners_Mod_Dual_Crossfade", "Diffuse_T1"), ("Combiners_Mod_Depth", "Diffuse_EdgeFade_T1"),
        ("Combiners_Opaque_Mod2xNA_Alpha_Alpha", "Diffuse_T1_Env_T2"), ("Combiners_Mod_Mod", "Diffuse_EdgeFade_T1_T2"),
        ("Combiners_Mod_Masked_Dual_Crossfade", "Diffuse_T1_T2"), ("Combiners_Opaque_Alpha", "Diffuse_T1_T1"),
        ("Combiners_Opaque_Mod2xNA_Alpha_UnshAlpha", "Diffuse_T1_Env_T2"), ("Combiners_Mod_Depth", "Diffuse_EdgeFade_Env"),
        ("Guild", "Diffuse_T1_T2_T1"), ("Guild_NoBorder", "Diffuse_T1_T2"), ("Guild_Opaque", "Diffuse_T1_T2_T1"),
        ("Illum", "Diffuse_T1_T1"), ("Combiners_Mod_Mod_Mod_Const", "Diffuse_T1_T2_T3"), ("Combiners_Mod_Mod_Mod_Const", "Color_T1_T2_T3"),
        ("Combiners_Opaque", "Diffuse_T1"), ("Combiners_Mod_Mod2x", "Diffuse_EdgeFade_T1_T2"), ("Combiners_Mod", "Diffuse_EdgeFade_T1"),
        ("Combiners_Mod_Mod_Depth", "Diffuse_EdgeFade_T1_T2"),
    ];
    private static readonly M2StageCoordinate[] T1 = [M2StageCoordinate.Uv0], T1Env = [M2StageCoordinate.Uv0, M2StageCoordinate.Env],
        T1T1 = [M2StageCoordinate.Uv0, M2StageCoordinate.Uv0];
    private static readonly Dictionary<(int Count, string Ps, string Vs), M2CombinerProfile> Supported = new()
    {
        [(1, "Combiners_Opaque", "Diffuse_T1")] = new(M2Combiner.Opaque, "Combiners_Opaque", "Diffuse_T1", T1, false),
        [(1, "Combiners_Mod", "Diffuse_T1")] = new(M2Combiner.Mod, "Combiners_Mod", "Diffuse_T1", T1, true),
        [(2, "Combiners_Opaque_Mod2xNA_Alpha", "Diffuse_T1_Env")] = new(M2Combiner.OpaqueMod2xNAAlpha, "Combiners_Opaque_Mod2xNA_Alpha", "Diffuse_T1_Env", T1Env, false),
        [(2, "Combiners_Opaque_AddAlpha_Alpha", "Diffuse_T1_Env")] = new(M2Combiner.OpaqueAddAlphaAlpha, "Combiners_Opaque_AddAlpha_Alpha", "Diffuse_T1_Env", T1Env, false),
        [(2, "Combiners_Opaque_Alpha", "Diffuse_T1_T1")] = new(M2Combiner.OpaqueAlpha, "Combiners_Opaque_Alpha", "Diffuse_T1_T1", T1T1, false),
    };

    /// <summary>The supported profile for a VMA1 combiner code, or null.</summary>
    public static M2CombinerProfile? Profile(M2Combiner combiner) => Supported.Values.FirstOrDefault(p => p.Combiner == combiner);

    /// <summary>The (pixel, vertex) shader names the client selects for this stage count and shader ID, or null for an ID
    /// past the 0x8000 table.</summary>
    public static (string PixelShader, string VertexShader)? Name(int textureCount, ushort shaderId)
    {
        if ((shaderId & 0x8000) != 0) { var index = shaderId & 0x7FFF; return index < Effects.Length ? Effects[index] : null; }
        if (textureCount == 1)
            return ((shaderId & 0x70) != 0 ? "Combiners_Mod" : "Combiners_Opaque",
                (shaderId & 0x80) != 0 ? "Diffuse_Env" : (shaderId & 0x4000) != 0 ? "Diffuse_T2" : "Diffuse_T1");
        var low = shaderId & 7;
        var ps = (shaderId & 0x70) != 0
            ? low switch { 3 => "Combiners_Mod_Add", 4 => "Combiners_Mod_Mod2x", 6 => "Combiners_Mod_Mod2xNA", 7 => "Combiners_Mod_AddNA", _ => "Combiners_Mod_Mod" }
            : low switch { 0 => "Combiners_Opaque_Opaque", 3 or 7 => "Combiners_Opaque_AddAlpha", 4 => "Combiners_Opaque_Mod2x", 6 => "Combiners_Opaque_Mod2xNA", _ => "Combiners_Opaque_Mod" };
        var vs = (shaderId & 0x80) != 0 ? (shaderId & 8) != 0 ? "Diffuse_Env_Env" : "Diffuse_Env_T1"
            : (shaderId & 8) != 0 ? "Diffuse_T1_Env" : (shaderId & 0x4000) != 0 ? "Diffuse_T1_T2" : "Diffuse_T1_T1";
        return (ps, vs);
    }

    /// <summary>The supported profile, or null with <paramref name="label"/> naming what the batch needs.</summary>
    public static M2CombinerProfile? Admit(int textureCount, ushort shaderId, out string label)
    {
        var name = Name(textureCount, shaderId);
        var id = "0x" + shaderId.ToString("X4", CultureInfo.InvariantCulture);
        if (name is not { } n) { label = "shader " + id + " (no named combiner)"; return null; }
        // Bits the selection above never reads have no assigned meaning here, so they are not drawn.
        var known = (shaderId & 0x8000) != 0 ? 0xFFFF : textureCount == 1 ? 0x40F0 : 0x40FF;
        if ((shaderId & ~known) != 0) { label = "shader " + id + " (bits outside the named selection)"; return null; }
        label = n.PixelShader + " with " + n.VertexShader + " (shader " + id + ", " + textureCount + (textureCount == 1 ? " texture)" : " textures)");
        return Supported.GetValueOrDefault((textureCount, n.PixelShader, n.VertexShader));
    }
}
