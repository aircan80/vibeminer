using System.Numerics;

namespace VibeMiner.Formats.Wow;

public sealed record M2Limits(long MaxSourceBytes = 64L << 20, long MaxOutputBytes = 128L << 20,
    int MaxVertices = 1_000_000, int MaxVertexLookup = 65_536, int MaxTriangleIndices = 3_000_000,
    int MaxSections = 256, int MaxMaterials = 256, int MaxBatches = 512, int MaxTextures = 32,
    int MaxSkinProfiles = 32, int MaxChunks = 128, int MaxNameBytes = 4096, int MaxMetadataEntries = 65_536);
public readonly record struct M2Vertex(Vector3 Position, uint BoneWeights, uint BoneIndices, Vector3 Normal, Vector2 Uv0, Vector2 Uv1);
public readonly record struct M2Bounds(Vector3 Minimum, Vector3 Maximum, float Radius);
public sealed record M2ArrayDeclaration(string Name, uint Count, uint Offset, int Stride);
public sealed record M2Chunk(string Id, long Offset, long ByteLength);
public sealed record M2Texture(int Ordinal, uint Type, uint Flags, string Filename, uint? FileDataId);
public readonly record struct M2Material(ushort Flags, ushort BlendMode);
public readonly record struct M2Bone(int KeyBoneId, uint Flags, short Parent, ushort SubmeshId, Vector3 Pivot);
public readonly record struct M2AnimationFile(ushort AnimationId, ushort Variation, uint FileDataId);

/// <summary>Validated native static structures, not an acquired appearance or animated scene.</summary>
public sealed class M2Model
{
    internal M2Model(uint version, bool chunked, uint flags, string name, long sourceBytes, long baseOffset, long baseBytes,
        uint viewCount, M2Vertex[] vertices, M2Texture[] textures, M2Material[] materials, M2Bone[] bones,
        ushort[] textureCombos, short[] textureCoordinates, ushort[] weights, ushort[] transforms, ushort[] boneLookup,
        uint[] skins, uint[] lodSkins, uint skeleton, uint[] boneFiles, M2AnimationFile[] animationFiles,
        M2ArrayDeclaration[] declarations, M2Chunk[] chunks, M2Bounds declaredBounds, M2Bounds? computedBounds,
        long retainedBytes, M2Limits limits)
    {
        Version = version; IsChunked = chunked; Flags = flags; Name = name; SourceBytes = sourceBytes;
        BaseOffset = baseOffset; BaseBytes = baseBytes; ViewCount = viewCount; Vertices = Array.AsReadOnly(vertices);
        Textures = Array.AsReadOnly(textures); Materials = Array.AsReadOnly(materials); Bones = Array.AsReadOnly(bones);
        TextureCombos = Array.AsReadOnly(textureCombos); TextureCoordinates = Array.AsReadOnly(textureCoordinates);
        TextureWeights = Array.AsReadOnly(weights); TextureTransforms = Array.AsReadOnly(transforms); BoneLookup = Array.AsReadOnly(boneLookup);
        SkinFileDataIds = Array.AsReadOnly(skins); LodSkinFileDataIds = Array.AsReadOnly(lodSkins); SkeletonFileDataId = skeleton;
        BoneFileDataIds = Array.AsReadOnly(boneFiles); AnimationFiles = Array.AsReadOnly(animationFiles);
        Declarations = Array.AsReadOnly(declarations); Chunks = Array.AsReadOnly(chunks);
        DeclaredBounds = declaredBounds; ComputedBounds = computedBounds; RetainedBytes = retainedBytes; Limits = limits;
    }
    public uint Version { get; }
    public bool IsChunked { get; }
    public uint Flags { get; }
    public string Name { get; }
    public long SourceBytes { get; }
    public long BaseOffset { get; }
    public long BaseBytes { get; }
    public uint ViewCount { get; }
    public IReadOnlyList<M2Vertex> Vertices { get; }
    public IReadOnlyList<M2Texture> Textures { get; }
    public IReadOnlyList<M2Material> Materials { get; }
    public IReadOnlyList<M2Bone> Bones { get; }
    public IReadOnlyList<ushort> TextureCombos { get; }
    public IReadOnlyList<short> TextureCoordinates { get; }
    public IReadOnlyList<ushort> TextureWeights { get; }
    public IReadOnlyList<ushort> TextureTransforms { get; }
    public IReadOnlyList<ushort> BoneLookup { get; }
    public IReadOnlyList<uint> SkinFileDataIds { get; }
    public IReadOnlyList<uint> LodSkinFileDataIds { get; }
    public uint SkeletonFileDataId { get; }
    public IReadOnlyList<uint> BoneFileDataIds { get; }
    public IReadOnlyList<M2AnimationFile> AnimationFiles { get; }
    public IReadOnlyList<M2ArrayDeclaration> Declarations { get; }
    public IReadOnlyList<M2Chunk> Chunks { get; }
    public M2Bounds DeclaredBounds { get; }
    public M2Bounds? ComputedBounds { get; }
    internal long RetainedBytes { get; }
    internal M2Limits Limits { get; }
}

public readonly record struct M2Section(ushort Id, ushort Level, ushort VertexStart, ushort VertexCount,
    uint TriangleStart, ushort TriangleCount, ushort BoneCount, ushort BoneStart, ushort BoneInfluences,
    ushort CenterBoneIndex, Vector3 Center, Vector3 SortCenter, float SortRadius);
public readonly record struct M2Batch(byte Flags, sbyte Priority, ushort ShaderId, ushort SectionIndex,
    ushort Flags2, short ColorIndex, ushort MaterialIndex, ushort MaterialLayer, ushort TextureCount,
    ushort TextureComboIndex, ushort TextureCoordinateComboIndex, ushort TextureWeightComboIndex, ushort TextureTransformComboIndex);
public readonly record struct M2ShadowBatch(byte Flags, byte Flags2, ushort Unknown, ushort SectionIndex,
    ushort TextureIndex, ushort ColorIndex, ushort WeightIndex);
public sealed class M2Skin
{
    internal M2Skin(M2Model model, ushort[] lookup, ushort[] triangles, uint[] properties,
        M2Section[] sections, M2Batch[] batches, M2ShadowBatch[] shadows, uint maxBones, long sourceBytes, long retainedBytes, M2Limits limits)
    {
        Limits = limits; Model = model; VertexLookup = Array.AsReadOnly(lookup); TriangleLookup = Array.AsReadOnly(triangles);
        BoneProperties = Array.AsReadOnly(properties); Sections = Array.AsReadOnly(sections); Batches = Array.AsReadOnly(batches);
        ShadowBatches = Array.AsReadOnly(shadows); MaximumBones = maxBones; SourceBytes = sourceBytes; RetainedBytes = retainedBytes;
    }
    internal M2Model Model { get; }
    internal M2Limits Limits { get; }
    internal long RetainedBytes { get; }
    public long SourceBytes { get; }
    public IReadOnlyList<ushort> VertexLookup { get; }
    public IReadOnlyList<ushort> TriangleLookup { get; }
    public IReadOnlyList<uint> BoneProperties { get; }
    public IReadOnlyList<M2Section> Sections { get; }
    public IReadOnlyList<M2Batch> Batches { get; }
    public IReadOnlyList<M2ShadowBatch> ShadowBatches { get; }
    /// <summary>Raw uint32 SKIN header field. Modern274 zero is preserved without an inferred effective maximum.</summary>
    public uint MaximumBones { get; }
}

public enum M2GeometryScope { UnfilteredDeclaredSections }
public enum M2GeometryEvaluation { NotEvaluated }

/// <summary>Checked source geometry only. Material, pose, visibility and appearance interpretation are not admitted.
/// Caller independently proves the source's exact model/SFID association.</summary>
public sealed class M2GeometrySkin
{
    internal M2GeometrySkin(M2Model model, M2SkinGeometryData core)
    {
        Model = model; Limits = core.Limits; RetainedBytes = core.RetainedBytes;
        VertexLookup = Array.AsReadOnly(core.Lookup); TriangleLookup = Array.AsReadOnly(core.Triangles);
        BoneProperties = Array.AsReadOnly(core.Properties); Sections = Array.AsReadOnly(core.Sections);
        MaximumBones = core.MaximumBones; SourceBytes = core.Input.Length; HeaderBytes = core.HeaderBytes;
        BatchDeclaration = new("uninterpreted SKIN batches", (uint)core.BatchSpan.Count, (uint)core.BatchSpan.Offset, core.BatchSpan.Stride);
        ShadowBatchDeclaration = new("uninterpreted SKIN shadow batches", (uint)core.ShadowSpan.Count, (uint)core.ShadowSpan.Offset, core.ShadowSpan.Stride);
    }
    public M2Model Model { get; }
    internal M2Limits Limits { get; }
    internal long RetainedBytes { get; }
    public IReadOnlyList<ushort> VertexLookup { get; }
    public IReadOnlyList<ushort> TriangleLookup { get; }
    public IReadOnlyList<uint> BoneProperties { get; }
    public IReadOnlyList<M2Section> Sections { get; }
    public uint MaximumBones { get; }
    public long SourceBytes { get; }
    public int HeaderBytes { get; }
    public M2ArrayDeclaration BatchDeclaration { get; }
    public M2ArrayDeclaration ShadowBatchDeclaration { get; }
    public const string InterpretationPolicy = "skin-unfiltered-source-geometry-v1";
    public M2GeometryScope Scope => M2GeometryScope.UnfilteredDeclaredSections;
    public M2GeometryEvaluation MaterialEvaluation => M2GeometryEvaluation.NotEvaluated;
    public M2GeometryEvaluation PoseEvaluation => M2GeometryEvaluation.NotEvaluated;
    public M2GeometryEvaluation VisibilityEvaluation => M2GeometryEvaluation.NotEvaluated;
    public M2GeometryEvaluation AppearanceEvaluation => M2GeometryEvaluation.NotEvaluated;
}
