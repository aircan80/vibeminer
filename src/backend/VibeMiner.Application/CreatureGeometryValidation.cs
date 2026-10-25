using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public static partial class CreatureGeometryValidation
{
    public const string Policy = "creature-source-geometry-v1";
    public const int MaximumResponseBytes = 65536;
    internal const long SafeInteger = 9_007_199_254_740_991;
    private static readonly string[] Stages = ["metadata", "sourceContext", "modelResolution", "modelAcquisition",
        "modelVerification", "modelParsing", "skinResolution", "skinAcquisition", "skinVerification", "skinParsing",
        "artifactBuild", "artifactVerification", "publication", "textureResolution", "textureAcquisition", "textureVerification",
        "textureDecoding", "appearanceBuild", "appearanceVerification", "appearancePublication"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };

    public static void Validate(CreatureGeometryRequest request)
    { if (request is null) throw Invalid(); CreatureDisplayMetadataService.Validate(request.Selection); }
    public static void Validate(CreatureGeometryReadRequest request)
    { if (request is null || request.ExpectedGeometryArtifactId is not null && !Hash(request.ExpectedGeometryArtifactId)) throw Invalid(); CreatureDisplayMetadataService.Validate(request.Selection); }
    public static void Validate(CreatureGeometryOpenRequest request)
    { if (request is null || !Hash(request.GeometryArtifactId)) throw Invalid(); CreatureDisplayMetadataService.Validate(request.Selection); }
    internal static CreatureDisplayMetadataRequest Own(CreatureDisplayMetadataRequest selection) =>
        selection with { Context = selection.Context with { } };

    public static void ValidateProgress(CreatureGeometryProgress value)
    {
        if (value is null || !Stages.Contains(value.Stage, StringComparer.Ordinal) || !Text(value.Label, 128) ||
            value.Unit is not ("stage" or "bytes" or "entries") || value.Completed is < 0 or > SafeInteger ||
            value.Total is < 0 or > SafeInteger || value.Completed is { } done && value.Total is { } total && done > total ||
            value.Dependency is { } dependency && !Enum.IsDefined(dependency))
            throw new DiscoveryException("CREATURE_GEOMETRY_PROGRESS_INVALID", "Geometry progress requires a known stage and honest bounded counters.");
    }

    public static CreatureGeometryManifest Snapshot(CreatureGeometryManifest value)
    {
        Require(value is not null && Hash(value.ArtifactId) && Hash(value.BindingSha256) && Hash(value.Sha256));
        Require(value.FormatVersion == 1 && value.HeaderBytes == 128 && value.ByteLength is >= 1 and <= 2097152 &&
            value.Scope == "unfiltered-declared-sections" && value.CoordinatePolicy == "native-m2-position-normal-uv-winding-v1" &&
            value.Evaluations is { Material: "notEvaluated", Pose: "notEvaluated", Visibility: "notEvaluated", Appearance: "notEvaluated" });
        Require(value.VertexCount is >= 1 and <= 65536 && value.VertexLookupCount is >= 1 and <= 65536 &&
            value.SourceTriangleIndexCount is >= 3 and <= 131070 && value.SourceTriangleIndexCount % 3 == 0 &&
            value.SectionIndexCount is >= 3 and <= 131070 && value.SectionIndexCount % 3 == 0 &&
            value.LineIndexCount == value.SectionIndexCount * 2 && value.SectionCount is >= 1 and <= 256);
        var positions = new CreatureGeometryBufferRegion(128, value.VertexCount * 12);
        var indices = new CreatureGeometryBufferRegion(128 + positions.ByteLength, value.LineIndexCount * 4);
        var sections = new CreatureGeometryBufferRegion(indices.Offset + indices.ByteLength, value.SectionCount * 32);
        Require(value.Positions == positions && value.LineIndices == indices && value.Sections == sections && value.ByteLength == sections.Offset + sections.ByteLength);
        Require(value.Bounds is not null);
        var min = Entries(value.Bounds.Min, 3); var max = Entries(value.Bounds.Max, 3);
        Require(min.Length == 3 && max.Length == 3);
        for (var i = 0; i < 3; i++) Require(float.IsFinite(min[i]) && float.IsFinite(max[i]) && min[i] <= max[i] &&
            (min[i] != 0 || BitConverter.SingleToInt32Bits(min[i]) == 0) && (max[i] != 0 || BitConverter.SingleToInt32Bits(max[i]) == 0));
        Require(value.ModelFileDataId > 0 && value.SkinFileDataId > 0 && value.MainSkinOrdinal == 0 &&
            value.ModelReaderRevision == "m2-static-native-v1" && value.SkinReaderRevision == "m2-skin-geometry-v1" &&
            value.InterpretationPolicy == "skin-unfiltered-source-geometry-v1" && value.ArtifactRevision == "model-geometry-vmg1-v1" &&
            value.EdgePolicy == "all-sections-triangle-edges-v1");
        var owned = value with
        {
            Positions = positions,
            LineIndices = indices,
            Sections = sections,
            Evaluations = value.Evaluations with { },
            Bounds = new(Array.AsReadOnly(min), Array.AsReadOnly(max))
        };
        Bound(owned); return owned;
    }

    internal static T[] Entries<T>(IReadOnlyList<T>? values, int maximum)
    {
        Require(values is not null);
        try
        {
            var count = values.Count; Require(count >= 0 && count <= maximum);
            var copy = new T[count]; for (var i = 0; i < count; i++) copy[i] = values[i];
            Require(values.Count == count); return copy;
        }
        catch (Exception error) when (error is not DiscoveryException and not OutOfMemoryException) { throw BadResult(); }
    }
    private static void Bound<T>(T value)
    {
        using var sink = new BudgetSink(); JsonSerializer.Serialize(sink, value, Json);
    }
    private sealed class BudgetSink : Stream
    {
        private int count;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => count; public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Write(ReadOnlySpan<byte> bytes)
        { if (bytes.Length > MaximumResponseBytes - count) throw new DiscoveryException("CREATURE_GEOMETRY_RESOURCE_LIMIT", "Geometry control evidence exceeds its bounded response budget."); count += bytes.Length; }
        public override void Write(byte[] bytes, int offset, int length) => Write(bytes.AsSpan(offset, length));
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
    }
    private static bool Hash(string? value, int length = 64) => value?.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Unsigned(string? value, ulong maximum = uint.MaxValue) => value is { Length: >= 1 and <= 20 } &&
        (value.Length == 1 || value[0] != '0') && value.All(c => c is >= '0' and <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum;
    private static ulong U(string value) => ulong.Parse(value, CultureInfo.InvariantCulture);
    private static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw BadResult(); }
    private static DiscoveryException BadResult() => new("CREATURE_GEOMETRY_RESULT_INVALID", "Geometry evidence does not match its bounded exact-source contract.");
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact Creature artifact, declared slot and approved geometry artifact identity.");
}
