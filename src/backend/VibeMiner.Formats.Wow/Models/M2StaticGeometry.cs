using System.Numerics;
using static VibeMiner.Formats.Wow.M2Input;

namespace VibeMiner.Formats.Wow;

/// <summary>Native bind/rest triangles only; no pose, visibility choice, material or acquired appearance claim.</summary>
public sealed class M2StaticGeometry
{
    private M2StaticGeometry(M2Model model, IReadOnlyList<M2Section> sections, uint[] indices, M2Bounds bounds)
    { Vertices = model.Vertices; Indices = Array.AsReadOnly(indices); Sections = sections; Bounds = bounds; }
    public IReadOnlyList<M2Vertex> Vertices { get; }
    public IReadOnlyList<uint> Indices { get; }
    public IReadOnlyList<M2Section> Sections { get; }
    /// <summary>Axis-aligned bound and enclosing box-sphere radius over section-referenced positions.</summary>
    public M2Bounds Bounds { get; }
    public const string CoordinatePolicy = "native-m2-position-normal-uv-winding-v1";
    public static M2StaticGeometry Create(M2Model model, M2Skin skin, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(skin); token.ThrowIfCancellationRequested();
        return CreateCore(model, skin.Model, skin.VertexLookup, skin.TriangleLookup, skin.Sections, token);
    }
    /// <summary>Unfiltered checked source geometry. Does not promote material or appearance authority.</summary>
    public static M2StaticGeometry Create(M2Model model, M2GeometrySkin skin, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(model); ArgumentNullException.ThrowIfNull(skin); token.ThrowIfCancellationRequested();
        return CreateCore(model, skin.Model, skin.VertexLookup, skin.TriangleLookup, skin.Sections, token);
    }
    private static M2StaticGeometry CreateCore(M2Model model, M2Model boundModel, IReadOnlyList<ushort> lookup,
        IReadOnlyList<ushort> triangles, IReadOnlyList<M2Section> sections, CancellationToken token)
    {
        if (!ReferenceEquals(model, boundModel)) throw Error("INVALID", "The SKIN was validated against another model.");
        if (triangles.Count == 0 || !sections.Any(s => s.TriangleCount != 0)) throw Error("UNSUPPORTED", "The selected profile has no static surface triangles.");
        var indices = new uint[triangles.Count];
        for (var i = 0; i < indices.Length; i++)
        { if ((i & 4095) == 0) token.ThrowIfCancellationRequested(); indices[i] = lookup[triangles[i]]; }
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        foreach (var section in sections)
        {
            for (var n = section.TriangleStart; n < section.TriangleStart + section.TriangleCount; n++)
            {
                if ((n & 4095) == 0) token.ThrowIfCancellationRequested(); var p = model.Vertices[(int)indices[n]].Position;
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
        }
        var dx = (double)max.X - min.X; var dy = (double)max.Y - min.Y; var dz = (double)max.Z - min.Z;
        var radius = Math.Sqrt(dx * dx + dy * dy + dz * dz) / 2;
        if (radius > float.MaxValue) throw Error("LIMIT", "The section bounds exceed the finite geometry representation.");
        return new(model, sections, indices, new(min, max, (float)radius));
    }
}
