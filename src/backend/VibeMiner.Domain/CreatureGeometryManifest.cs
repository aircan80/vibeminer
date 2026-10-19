namespace VibeMiner.Domain;

public sealed record CreatureGeometryBufferRegion(int Offset, int ByteLength);
public sealed record CreatureGeometryEvaluations(string Material, string Pose, string Visibility, string Appearance);
public sealed record CreatureGeometryBounds(IReadOnlyList<float> Min, IReadOnlyList<float> Max);
public sealed record CreatureGeometryManifest(string ArtifactId, string BindingSha256, string Sha256, long ByteLength,
    int FormatVersion, int HeaderBytes, string Scope, string CoordinatePolicy, CreatureGeometryEvaluations Evaluations,
    int VertexCount, int VertexLookupCount, int SourceTriangleIndexCount, int SectionIndexCount, int LineIndexCount,
    int SectionCount, CreatureGeometryBufferRegion Positions, CreatureGeometryBufferRegion LineIndices,
    CreatureGeometryBufferRegion Sections, CreatureGeometryBounds Bounds, uint ModelFileDataId, uint SkinFileDataId,
    int MainSkinOrdinal, string ModelReaderRevision, string SkinReaderRevision, string InterpretationPolicy,
    string ArtifactRevision, string EdgePolicy);
