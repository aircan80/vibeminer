using System.Collections.ObjectModel;
using System.Numerics;

namespace VibeMiner.Formats.Wow;

// Immutable derivative inputs only. Original receipt verification and build/slot authority belong to the adapter.
public sealed record GeometryOriginalSource(string ContentKey, string EncodingKey, string EncodedSha256,
    long EncodedBytes, long DecodedBytes, string VerifierRevision);
public sealed record GeometrySourceBinding(GeometryOriginalSource Model, GeometryOriginalSource Skin,
    uint ModelFileDataId, uint SkinFileDataId, int MainSkinOrdinal);
public sealed record GeometryBufferRegion(int Offset, int ByteLength);
public sealed record GeometryEvaluations(string Material, string Pose, string Visibility, string Appearance);
public sealed class GeometryBounds
{
    public GeometryBounds(Vector3 min, Vector3 max)
    { Min = Array.AsReadOnly(new[] { min.X, min.Y, min.Z }); Max = Array.AsReadOnly(new[] { max.X, max.Y, max.Z }); }
    public ReadOnlyCollection<float> Min { get; }
    public ReadOnlyCollection<float> Max { get; }
}
public sealed record GeometryManifest(string ArtifactId, string BindingSha256, string Sha256, long ByteLength,
    int FormatVersion, int HeaderBytes, string Scope, string CoordinatePolicy, GeometryEvaluations Evaluations,
    int VertexCount, int VertexLookupCount, int SourceTriangleIndexCount, int SectionIndexCount, int LineIndexCount,
    int SectionCount, GeometryBufferRegion Positions, GeometryBufferRegion LineIndices, GeometryBufferRegion Sections,
    GeometryBounds Bounds, uint ModelFileDataId, uint SkinFileDataId, int MainSkinOrdinal, string ModelReaderRevision,
    string SkinReaderRevision, string InterpretationPolicy, string ArtifactRevision, string EdgePolicy);
public sealed record GeometryArtifact(GeometrySourceBinding Binding, string BindingSha256, string ArtifactId,
    string Sha256, long ByteLength, GeometryManifest Manifest);
